using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoEnhancer;

internal sealed class ModelDownloadManager
{
    private readonly string _coreRoot;
    private readonly string _frameInterpolationDir;
    private readonly string? _token;
    private readonly string _toolVersion;
    private readonly ModelRepositoryClient _repository;
    private readonly Func<string,string,bool,int> _downloadWithAria;
    private readonly Func<string,string,bool,int> _extractArchive;
    private readonly Func<string,int,int> _fail;

    internal ModelDownloadManager(string coreRoot, string? token, string toolVersion, ModelRepositoryClient repository,
        Func<string,string,bool,int> downloadWithAria, Func<string,string,bool,int> extractArchive,
        Func<string,int,int> fail)
    {
        _coreRoot=coreRoot;
        _frameInterpolationDir=Path.Combine(coreRoot,"models","Frame-Interpolation");
        _token=token;
        _toolVersion=toolVersion;
        _repository=repository;
        _downloadWithAria=downloadWithAria;
        _extractArchive=extractArchive;
        _fail=fail;
    }

    internal int ListRemoteModels(bool json)
    {
        try
        {
            var models = _repository.FetchRemoteModels();
            if (json)
            {
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartArray();
                    foreach (var model in models)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", model.Name);
                        writer.WriteString("path", model.Path);
                        writer.WriteNumber("size", model.Size);
                        writer.WriteString("sha256", model.Sha256);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                Console.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
            }
            else
            {
                foreach (var model in models)
                    Console.WriteLine($"{model.Path}\t{model.Size}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            ModelRepositoryClient.WriteRemoteFailure("无法读取模型列表", ex);
            return 3;
        }
    }

    internal int DownloadRepositoryModel(string requestedPath)
    {
        List<ModelRepositoryClient.RemoteModel> models;
        try
        {
            models = _repository.FetchRemoteModels();
        }
        catch (Exception ex)
        {
            ModelRepositoryClient.WriteRemoteFailure("无法连接模型镜像", ex);
            return 3;
        }

        var normalized = requestedPath.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("Backend/", StringComparison.OrdinalIgnoreCase))
            return _fail("后端应在模型下载页通过后端更新服务进行安装或修复", 1);
        var model = models.FirstOrDefault(m => m.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (model is null) return _fail("镜像中不存在该文件：" + normalized, 1);

        if (Path.GetExtension(model.Path).Equals(".rar", StringComparison.OrdinalIgnoreCase))
            return _fail("7za 不支持 RAR，请选择 ZIP 或 7z 压缩包", 1);
        var slash = model.Path.IndexOf('/');
        if (slash <= 0) return _fail("模型镜像路径无效：" + model.Path, 1);
        var category = model.Path[..slash];
        var suffix = model.Path[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar);
        var destinationRoot = category.Equals("Plugin", StringComparison.OrdinalIgnoreCase)
            ? _coreRoot
            : category.Equals("Backend", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(_coreRoot, "python")
                : category.Equals("Bin", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(_coreRoot, "bin")
                    : Path.Combine(_coreRoot, "models", category);
        var destination = SafeCombine(destinationRoot, suffix);
        var url = _repository.ResolveRoot + string.Join("/", model.Path.Split('/').Select(Uri.EscapeDataString));
        // 完成下载及解压后才移除标记，刷新列表不会把取消后的半成品认作已安装。
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var pending = destination + ".pending";
        var pendingIdentity = category.Equals("Bin", StringComparison.OrdinalIgnoreCase)
            ? model.Path + "\n" + model.Sha256 : model.Path;
        if (category.Equals("Bin", StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
        {
            // 同路径组件换包后，不能续传旧内容或复用旧的完整归档。
            var partial = File.Exists(destination + ".aria2");
            var stale = partial
                ? !File.Exists(pending) || File.ReadAllText(pending, Encoding.UTF8) != pendingIdentity
                : !string.IsNullOrWhiteSpace(model.Sha256) && !ArchiveHashMatches(destination, model.Sha256);
            if (stale)
            {
                File.Delete(destination);
                File.Delete(destination + ".aria2");
            }
        }
        File.WriteAllText(pending, pendingIdentity, new UTF8Encoding(false));
        Console.WriteLine("DOWNLOAD_START|" + model.Path);
        var code = _token is null
            ? _downloadWithAria(url, destination, false)
            : DownloadModelScopeFile(url, destination);
        if (code != 0) return code;

        if (model.Size > 0 && new FileInfo(destination).Length != model.Size)
            return _fail("下载文件大小校验失败：" + destination, 1);
        if (!string.IsNullOrWhiteSpace(model.Sha256))
        {
            using var stream = File.OpenRead(destination);
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                DownloadCancellation.Check();
                hash.AppendData(buffer, 0, count);
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
                return _fail("下载文件 SHA256 校验失败：" + destination, 1);
        }

        if (IsArchiveFile(destination))
        {
            // 旧镜像压缩包包含一级分类目录；新版补帧包只包含架构目录，需直接解到 Frame-Interpolation。
            var extractionRoot = category.Equals("Backend", StringComparison.OrdinalIgnoreCase)
                ? _coreRoot
                : category.Equals("Bin", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(_coreRoot, "bin")
                    : category.Equals("Frame-Interpolation", StringComparison.OrdinalIgnoreCase)
                        ? _frameInterpolationDir
                        : Path.Combine(_coreRoot, "models");
            code = _extractArchive(destination, extractionRoot, true);
            if (code != 0) return code;
            if (category.Equals("Frame-Interpolation", StringComparison.OrdinalIgnoreCase))
            {
                var marker = FrameInterpolationArchiveMarkerPath(model.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, model.Path, Encoding.UTF8);
            }
            if (category.Equals("Bin", StringComparison.OrdinalIgnoreCase))
            {
                // 组件同一路径也可能更新，完成校验和解压后保存远端内容哈希。
                DownloadCancellation.Check();
                var normalizedPath = model.Path.Replace('\\', '/').ToUpperInvariant();
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
                var marker = Path.Combine(_coreRoot, "bin", ".downloads", key + ".installed");
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, model.Sha256, new UTF8Encoding(false));
            }
            if (IsRtxVideoRuntimeArchivePath(model.Path))
            {
                // runtime 已完整解压并通过下载哈希校验，日期归档不再参与运行；
                // 清掉所有历史归档，避免每次更新都在本地累积一份约 22MB 的包。
                DeleteRtxVideoRuntimeArchives();
            }
        }
        DownloadCancellation.Check();
        File.Delete(pending);
        Console.WriteLine("DOWNLOAD_COMPLETE|" + destination);
        return 0;
    }

    internal int DeleteDownloadedModel(string requestedPath)
    {
        var normalized = requestedPath.Replace('\\', '/').TrimStart('/');
        if (IsRtxVideoRuntimeArchivePath(normalized))
            return DeleteRtxVideoRuntime();

        var slash = normalized.IndexOf('/');
        if (slash <= 0) return _fail("模型路径无效：" + normalized, 1);
        var category = normalized[..slash];
        var allowedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "BasicVSR++", "FlashVSR", "Frame-Interpolation", "ONNX", "Param-Bin", "RIFE", "PTH" };
        if (!allowedCategories.Contains(category))
            return _fail("只允许删除 models 目录中的模型文件", 1);

        var suffix = normalized[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar);
        var destination = SafeCombine(Path.Combine(_coreRoot, "models", category), suffix);
        if (IsArchiveFile(destination))
            return _fail("压缩模型包可能包含共享目录，不能按单文件方式删除；请使用清理归档功能", 1);
        if (!File.Exists(destination))
            return _fail("本地模型文件不存在：" + normalized, 1);

        try
        {
            var attributes = File.GetAttributes(destination);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return _fail("本地模型文件是符号链接或联接点，已拒绝删除", 1);
            File.Delete(destination);
            Console.WriteLine("MODEL_DELETE_COMPLETE|" + normalized);
            return 0;
        }
        catch (Exception ex)
        {
            return _fail("删除本地模型失败：" + ex.Message, 1);
        }
    }

    private int DeleteRtxVideoRuntime()
    {
        var root = Path.GetFullPath(Path.Combine(_coreRoot, "bin", "rtx-video"));
        if (!Directory.Exists(root))
            return _fail("本地 RTX 运行组件不存在", 1);

        var sidecars = Process.GetProcessesByName("vsr_backend");
        try
        {
            if (sidecars.Length > 0)
                return _fail("RTX 运行组件正在被视频任务使用，请先停止任务再卸载", 1);
        }
        finally
        {
            foreach (var process in sidecars) process.Dispose();
        }

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(
                    root,
                    "*",
                    SearchOption.AllDirectories)
                .Prepend(root);
            foreach (var entry in entries)
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    return _fail("RTX 运行组件目录包含符号链接或联接点，已拒绝卸载", 1);
            }

            Directory.Delete(root, recursive: true);
            Console.WriteLine("RTX_RUNTIME_DELETE_COMPLETE|Bin/rtx-video");
            return 0;
        }
        catch (Exception ex)
        {
            return _fail("卸载 RTX 运行组件失败：" + ex.Message, 1);
        }
    }

    private static bool ArchiveHashMatches(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private string FrameInterpolationArchiveMarkerPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_frameInterpolationDir, ".downloads", hash + ".installed");
    }

    internal int DownloadModelScopeFile(string url, string destination)
    {
        var partial = destination + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _repository.ApplyModelScopeAuthentication(client);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VideoEnhancer/" + _toolVersion);
            using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, DownloadCancellation.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"ModelScope 下载返回 HTTP {(int)response.StatusCode} ({response.ReasonPhrase})",
                    null,
                    response.StatusCode);

            var total = response.Content.Headers.ContentLength ?? 0;
            using var input = response.Content.ReadAsStreamAsync(DownloadCancellation.Token).GetAwaiter().GetResult();
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1024 * 1024];
                long completed = 0;
                var lastPercent = -1;
                int read;
                while ((read = input.ReadAsync(buffer.AsMemory(), DownloadCancellation.Token).AsTask().GetAwaiter().GetResult()) > 0)
                {
                    output.Write(buffer, 0, read);
                    completed += read;
                    if (total <= 0) continue;
                    var percent = (int)Math.Clamp(completed * 100L / total, 0, 100);
                    if (percent == lastPercent) continue;
                    lastPercent = percent;
                    Console.WriteLine($"DOWNLOAD_PROGRESS|{percent}|{Path.GetFileName(destination)}");
                }
                output.Flush(true);
            }
            File.Move(partial, destination, true);
            return 0;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            DownloadCancellation.Log("下载 " + destination, ex);
            if (ex is OperationCanceledException && DownloadCancellation.Token.IsCancellationRequested)
                Console.Error.WriteLine("DOWNLOAD_CANCELLED|已取消");
            else if (ModelRepositoryClient.IsAuthenticationFailure(ex))
                Console.Error.WriteLine("AUTH_REQUIRED|ModelScope 私有文件需要有效令牌；请检查 VIDEOENHANCER_MODELSCOPE_TOKEN 或 MODELSCOPE_API_TOKEN");
            else
                Console.Error.WriteLine("[错误] ModelScope 下载失败：" + ex.Message);
            return 1;
        }
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("路径越出了目标目录：" + relative);
        return full;
    }

    private static bool IsArchiveFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tar", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bz2", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tgz", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txz", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tbz2", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tzst", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gz", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xz", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zst", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRtxVideoRuntimeArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return Regex.IsMatch(
            normalized,
            @"^Bin/rtx-video/RTXVideoRuntime_\d{8}\.7z$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private void DeleteRtxVideoRuntimeArchives()
    {
        var root = Path.Combine(_coreRoot, "bin", "rtx-video");
        if (!Directory.Exists(root)) return;
        foreach (var archive in Directory.EnumerateFiles(
                     root,
                     "RTXVideoRuntime_*.7z",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(archive);
                Console.WriteLine("CLEAN_DELETED|" + archive);
            }
            catch (Exception ex)
            {
                // 运行组件已经安装成功，杀毒软件短暂占用归档不应把整个下载任务判为失败；
                // 用户仍可稍后使用“清理归档”重试。
                Console.Error.WriteLine("[警告] RTX runtime 归档暂时无法清理：" + ex.Message);
            }
        }
    }

    internal int CleanDownloadArchives()
    {
        var deleted = 0;
        long reclaimedBytes = 0;
        var failures = new List<string>();

        var candidates = new List<string>();
        var modelsRoot = Path.Combine(_coreRoot, "models");
        if (Directory.Exists(modelsRoot))
            candidates.AddRange(Directory.EnumerateFiles(modelsRoot, "*", SearchOption.AllDirectories));
        // Backend 下载包落在核心 python 目录的顶层；其子目录是运行时与后端源码，
        // 其中也包含 base_library.zip、测试数据 .gz 等不可删除的正常文件。
        var pythonRoot = Path.Combine(_coreRoot, "python");
        if (Directory.Exists(pythonRoot))
            candidates.AddRange(Directory.EnumerateFiles(pythonRoot, "*", SearchOption.TopDirectoryOnly));
        // RTX runtime 使用日期版本归档，解压后不再需要；只扫描专用目录顶层，
        // 不触碰 bin 中 FFmpeg、mkvtoolnix 等其他运行组件。
        var rtxVideoRoot = Path.Combine(_coreRoot, "bin", "rtx-video");
        if (Directory.Exists(rtxVideoRoot))
            candidates.AddRange(Directory.EnumerateFiles(
                rtxVideoRoot,
                "RTXVideoRuntime_*.7z",
                SearchOption.TopDirectoryOnly));

        foreach (var file in candidates.Where(IsArchiveFile).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var full = Path.GetFullPath(file);
                var length = new FileInfo(full).Length;
                File.Delete(full);
                reclaimedBytes += length;
                deleted++;
                Console.WriteLine("CLEAN_DELETED|" + full);
            }
            catch (Exception ex)
            {
                failures.Add(file + "：" + ex.Message);
            }
        }

        Console.WriteLine($"CLEAN_COMPLETE|{deleted}|{reclaimedBytes}");
        if (failures.Count == 0) return 0;
        foreach (var failure in failures) Console.Error.WriteLine("[清理失败] " + failure);
        return 2;
    }

}
