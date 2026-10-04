using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VideoEnhancer;

/// <summary>
/// videoenhancer.3fui.dll 的后端服务，由 DLL 内部调用模型管理、运行环境及预览操作。
/// 宿主视频任务使用 ExtVideoPipeline 处理链。
/// </summary>
public static partial class BackendServices
{
    // 运行时从合并后的程序集元数据读取发布版本，避免服务内多处字面量漂移。
    private static string ToolVersion { get; } = ResolveToolVersion();

    private static string ResolveToolVersion()
    {
        var assembly = typeof(BackendServices).Assembly;
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // InformationalVersion 可能带 "+提交哈希" 元数据后缀，缓存目录等用途需要纯版本号。
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
        return assembly?.GetName().Version?.ToString(3) ?? "0.0.0";
    }
    private const string EmbeddedOrderedBackendResource = "VideoEnhancer.Embedded.rve-ordered-backend.py";
    private const string EmbeddedInterpolationInspectorResource = "VideoEnhancer.Embedded.inspect_interpolation_models.py";
    private const string EmbeddedUpscaleInspectorResource = "VideoEnhancer.Embedded.inspect_upscale_models.py";
    private const string EmbeddedRifeTensorRTPrepareResource = "VideoEnhancer.Embedded.prepare_rife_tensorrt.py";
    private const string EmbeddedFrameBackendResource = "VideoEnhancer.Embedded.rve-frame-backend.py";
    private const string EmbeddedSegmentedBackendResource = "VideoEnhancer.Embedded.rve-segmented-backend.py";
    private const int InterpolationCapabilityCacheVersion = 1;
    private const string DefaultModelScopeDataset = "AerithDream/VideoEnhancer-Models";
    private static string? ModelScopeToken =>
        Environment.GetEnvironmentVariable("VIDEOENHANCER_MODELSCOPE_TOKEN")?.Trim() is { Length: > 0 } localToken
            ? localToken
            : Environment.GetEnvironmentVariable("MODELSCOPE_API_TOKEN")?.Trim() is { Length: > 0 } apiToken
                ? apiToken
                : null;
    private static string ModelScopeDataset =>
        Environment.GetEnvironmentVariable("VIDEOENHANCER_MODELSCOPE_DATASET")?.Trim() is { Length: > 0 } value
            ? value.Trim('/').Replace('\\', '/')
            : DefaultModelScopeDataset;
    private const int ModelScopeTreePageSize = 500;
    private static string ModelScopeResolveRoot =>
        "https://www.modelscope.cn/datasets/" + ModelScopeDataset + "/resolve/master/";

    // CoreRoot 为配置的数据根目录，默认使用 DLL 同级的 videoenhancer 目录。
    private static string CoreRoot => PortablePaths.CoreRoot;

    private static string PythonExe => Path.Combine(CoreRoot, "python", "python", "python.exe");
    private static string BackendScript => Path.Combine(CoreRoot, "python", "backend", "rve-backend.py");
    private static string FrameBackendScript => Path.Combine(CoreRoot, "python", "backend", "rve-frame-backend.py");
    private static string SegmentedBackendScript => Path.Combine(CoreRoot, "python", "backend", "rve-segmented-backend.py");
    private static string TensorRTValidatorScript => Path.Combine(CoreRoot, "python", "backend", "validate_tensorrt_engines.py");
    private static string TensorRTConverterScript => Path.Combine(CoreRoot, "python", "backend", "convert_tensorrt.py");
    private static string InterpolationInspectorScript => Path.Combine(CoreRoot, "python", "backend", "inspect_interpolation_models.py");
    private static string UpscaleInspectorScript => Path.Combine(CoreRoot, "python", "backend", "inspect_upscale_models.py");
    private static string RifeTensorRTPrepareScript => Path.Combine(CoreRoot, "python", "backend", "prepare_rife_tensorrt.py");
    private static string Aria2NextExe => Path.Combine(CoreRoot, "bin", "aria2-next", "aria2-next.exe");
    // 最终编码复用 3FUI 的 FFmpeg：显式覆盖除外，先查 3FUI 工作目录，再查其他路径。
    private static string FfmpegExe => Resolve3FuiFfmpegTool("ffmpeg.exe");
    private static string FfmpegPathOverride { get => BackendInvocation.Current?.FfmpegPath ?? ""; set { if(BackendInvocation.Current is {} c)c.FfmpegPath=value; } }
    private static string FfprobePathOverride { get => BackendInvocation.Current?.FfprobePath ?? ""; set { if(BackendInvocation.Current is {} c)c.FfprobePath=value; } }
    private static string RtxVideoBackendExe => RtxVideoBackendClient.FindBackend(CoreRoot);
    private static string ModelsDir => Path.Combine(CoreRoot, "models");
    private static string FrameInterpolationDir => Path.Combine(ModelsDir, "Frame-Interpolation");
    private static string UserInterpolationDir => Path.Combine(ModelsDir, "User", "Interpolation");
    private static string TensorRTCacheDir => Path.Combine(ModelsDir, "TensorRT-Cache");
    private static string SceneDetectModel => FindNcnnModelFolder("EfficientNet-SceneDetect")
        ?? Path.Combine(ModelsDir, "EfficientNet-SceneDetect");
    private static string PythonSitePackages => Path.Combine(CoreRoot, "python", "python", "Lib", "site-packages");

    private static string Resolve3FuiFfmpegTool(string fileName)
    {
        var candidates = new List<string>();
        var suppliedPath = fileName.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
            ? FfmpegPathOverride : FfprobePathOverride;
        if (!string.IsNullOrWhiteSpace(suppliedPath)) candidates.Add(suppliedPath);
        var explicitFfmpeg = Environment.GetEnvironmentVariable("VIDEOENHANCER_FFMPEG")?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(explicitFfmpeg))
        {
            candidates.Add(fileName.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                ? explicitFfmpeg
                : Path.Combine(Path.GetDirectoryName(explicitFfmpeg) ?? "", fileName));
        }
        try
        {
            // 标准安装：<3FUI>\Plugin\videoenhancer，因此向上两级就是宿主 EXE 目录。
            var hostRoot = Path.GetFullPath(Path.Combine(CoreRoot, "..", ".."));
            var settingsPath = Path.Combine(hostRoot, "Settings.json");
            if (File.Exists(settingsPath))
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (settings.RootElement.TryGetProperty("工作目录", out var configured) &&
                    configured.ValueKind == JsonValueKind.String)
                {
                    var workingDirectory = configured.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                        candidates.Add(Path.Combine(workingDirectory, fileName));
                }
            }
            candidates.Add(Path.Combine(hostRoot, fileName));
        }
        catch
        {
            // 设置文件无效或目录异常时继续按宿主目录、PATH 与插件工具目录解析。
            try { candidates.Add(Path.Combine(Path.GetFullPath(Path.Combine(CoreRoot, "..", "..")), fileName)); }
            catch { }
        }
        candidates.Add(Path.Combine(Environment.CurrentDirectory, fileName));
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var rawDirectory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length > 0) candidates.Add(Path.Combine(directory, fileName));
        }
        var pluginFallback = Path.Combine(CoreRoot, "bin", "ffmpeg", fileName);
        candidates.Add(pluginFallback);
        foreach (var candidate in candidates)
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath)) return fullPath;
            }
            catch
            {
                // PATH 可能包含无效目录；跳过该项并继续查找其余 3FUI 环境。
            }
        }
        return pluginFallback;
    }
    private static string InterpolationCapabilityCachePath =>
        Path.Combine(PortablePaths.CacheRoot,
            $"interpolation-capabilities-v{InterpolationCapabilityCacheVersion}.json");

    // ── 模型转换子进程随当前 DLL 服务调用取消 ──

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    /// <summary>创建"最后一个句柄关闭即终止作业内进程"的作业对象；失败返回 IntPtr.Zero。</summary>
    private static IntPtr CreateKillOnCloseJob()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return job;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>参与 TensorRT Engine 缓存隔离的本机运行时信息。</summary>
    private sealed record TensorRtRuntime(string GpuName, string TensorRtVersion, string TorchTensorRtVersion);

    public static string Version => ToolVersion;

    internal static int Execute(BackendRequest request)
    {
        try
        {
            FfmpegPathOverride=request.FfmpegPath;
            FfprobePathOverride=request.FfprobePath;
            DownloadCancellation.Check();
            if(request.Action==BackendAction.ListDownloadModels)return CreateModelDownloadManager().ListRemoteModels(true);
            BackendUpdateManager.RecoverPending(CoreRoot);
            if(request.Action==BackendAction.BackendStatus)return RunBackendStatus(request.ChannelSource,true);
            if(request.Action==BackendAction.UpdateBackend)return RunBackendUpdate(request.ChannelSource,request.ForceFullPackage);
            EnsureInterpolationSupportScripts();
            return request.Action switch
            {
                BackendAction.ListModels => ListModels(true,request.Backend),
                BackendAction.ListModelCatalog => ListModelCatalog(true,request.Backend,false),
                BackendAction.ListInterpolationModels => ListInterpModels(true,request.Backend),
                BackendAction.ListInterpolationCatalog => ListModelCatalog(true,request.Backend,true),
                BackendAction.ListUserModels => ListUserModels(true),
                BackendAction.InspectUpscaleModel => InspectUpscaleModel(request.Path),
                BackendAction.InspectInterpolationModel => InspectInterpolationModel(request.Path),
                BackendAction.ImportModels => ImportModels(request.Path,true),
                BackendAction.UpdateUserModel => UpdateUserModel(request),
                BackendAction.DeleteUserModel => DeleteUserModel(request.ModelId),
                BackendAction.PrepareInterpolationEngine => request.Width>0&&request.Height>0
                    ? PrepareRifeTensorRTEngine(request.Path,request.Width,request.Height,request.StaticShape)
                    : Fail("Engine 输入宽度和高度必须大于 0"),
                BackendAction.CheckEnvironment => RunCheck(true,request.Backend)?0:1,
                BackendAction.ListBackends => ListBackendsWithEngineValidation(),
                BackendAction.ValidateEngines => ValidateAllTensorRTEngines(),
                BackendAction.DownloadModel => CreateModelDownloadManager().DownloadRepositoryModel(request.Path),
                BackendAction.DeleteDownloadModel => CreateModelDownloadManager().DeleteDownloadedModel(request.Path),
                BackendAction.CleanDownloadArchives => CreateModelDownloadManager().CleanDownloadArchives(),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Action))
            };
        }
        catch(Exception ex)
        {
            if(request.Action is BackendAction.DownloadModel or BackendAction.UpdateBackend)
                DownloadCancellation.Log("下载管理",ex);
            Console.Error.WriteLine(ex is OperationCanceledException&&DownloadCancellation.Token.IsCancellationRequested
                ? "DOWNLOAD_CANCELLED|已取消" : "[错误] "+ex.Message);
            return 1;
        }
    }

    private static string DefaultBackendChannel => ModelScopeResolveRoot + "Backend/channel.json";
    private static ModelRepositoryClient CreateModelRepository() => new(ModelScopeDataset, ModelScopeToken, ToolVersion);

    private static BackendUpdateChannel LoadBackendChannel(string configuredSource, out string source)
    {
        source = string.IsNullOrWhiteSpace(configuredSource)
            ? Environment.GetEnvironmentVariable("VIDEOENHANCER_BACKEND_CHANNEL")?.Trim() ?? DefaultBackendChannel
            : configuredSource.Trim();
        string json;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            CreateModelRepository().ApplyModelScopeAuthentication(client);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VideoEnhancer/" + ToolVersion);
            json = client.GetStringAsync(uri, DownloadCancellation.Token).GetAwaiter().GetResult();
        }
        else
        {
            source = Path.GetFullPath(source);
            json = File.ReadAllText(source, Encoding.UTF8);
        }
        return BackendUpdateManager.ReadChannel(json);
    }

    private static int RunBackendStatus(string configuredSource, bool json)
    {
        try
        {
            var channel = LoadBackendChannel(configuredSource, out _);
            var status = BackendUpdateManager.GetStatus(CoreRoot, channel);
            if (json)
            {
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("state", status.State);
                    writer.WriteString("installedVersion", status.InstalledVersion);
                    writer.WriteString("latestVersion", status.LatestVersion);
                    writer.WriteString("mode", status.Mode);
                    writer.WriteNumber("downloadSize", status.DownloadSize);
                    writer.WriteNumber("fullSize", status.Full.Size);
                    writer.WriteNumber("patchCount", status.PatchRoute.Count);
                    writer.WriteEndObject();
                }
                Console.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
            }
            else
            {
                Console.WriteLine($"BACKEND_STATUS|{status.State}|{status.InstalledVersion}|{status.LatestVersion}|{status.Mode}|{status.DownloadSize}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            ModelRepositoryClient.WriteRemoteFailure("无法读取后端更新状态", ex);
            return 3;
        }
    }

    private static int RunBackendUpdate(string configuredSource, bool forceFull)
    {
        try
        {
            Console.WriteLine("DOWNLOAD_STAGE|连接更新服务");
            DownloadCancellation.Check();
            var channel = LoadBackendChannel(configuredSource, out var source);
            var status = BackendUpdateManager.GetStatus(CoreRoot, channel);
            if (status.State == "current" && !forceFull)
            {
                Console.WriteLine("BACKEND_CURRENT|" + status.LatestVersion);
                return 0;
            }
            if (forceFull || status.Mode == "full")
            {
                Console.WriteLine($"BACKEND_FULL_START|{status.LatestVersion}|{status.Full.Size}");
                var archive = AcquireBackendArtifact(source, status.Full.Path, status.Full.Size, status.Full.Sha256);
                return ApplyBackendFullArchive(archive, status.LatestVersion);
            }

            foreach (var patch in status.PatchRoute)
            {
                Console.WriteLine($"BACKEND_PATCH_START|{patch.BaseVersion}|{patch.TargetVersion}|{patch.Size}");
                var archive = AcquireBackendArtifact(source, patch.Path, patch.Size, patch.Sha256);
                var code = ApplyBackendPatchArchive(archive);
                if (code != 0) return code;
            }
            Console.WriteLine("BACKEND_UPDATE_COMPLETE|" + status.LatestVersion);
            return 0;
        }
        catch (Exception ex)
        {
            DownloadCancellation.Log("后端更新", ex);
            if (ex is OperationCanceledException && DownloadCancellation.Token.IsCancellationRequested)
                Console.Error.WriteLine("DOWNLOAD_CANCELLED|已取消");
            else if (ModelRepositoryClient.IsNetworkFailure(ex) || ModelRepositoryClient.IsAuthenticationFailure(ex))
                ModelRepositoryClient.WriteRemoteFailure("后端更新失败", ex);
            else
                Console.Error.WriteLine("[错误] 后端更新失败：" + ex.Message);
            return 1;
        }
    }

    private static string AcquireBackendArtifact(
        string channelSource,
        string artifactPath,
        long expectedSize,
        string expectedSha256)
    {
        var downloads = Path.Combine(BackendUpdateManager.StateRoot(CoreRoot), "downloads");
        Directory.CreateDirectory(downloads);
        var safeName = Path.GetFileName(artifactPath.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(safeName))
            throw new InvalidOperationException("后端更新包路径无效：" + artifactPath);
        var destination = Path.Combine(downloads, safeName);
        if (File.Exists(destination) && !File.Exists(destination + ".aria2"))
        {
            try
            {
                Console.WriteLine("DOWNLOAD_STAGE|校验下载包");
                DownloadCancellation.Check();
                BackendUpdateManager.VerifyArtifact(destination, expectedSize, expectedSha256);
                return destination;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                File.Delete(destination);
            }
        }

        if (Uri.TryCreate(channelSource, UriKind.Absolute, out var channelUri)
            && (channelUri.Scheme == Uri.UriSchemeHttp || channelUri.Scheme == Uri.UriSchemeHttps))
        {
            string url;
            if (Uri.TryCreate(artifactPath, UriKind.Absolute, out var artifactUri))
                url = artifactUri.ToString();
            else if (channelSource.Equals(DefaultBackendChannel, StringComparison.OrdinalIgnoreCase))
                url = ModelScopeResolveRoot + string.Join("/", artifactPath.Split('/').Select(Uri.EscapeDataString));
            else
                url = new Uri(channelUri, artifactPath).ToString();
            var code = ModelScopeToken is null
                ? DownloadWithAria(url, destination, printComplete: false)
                : CreateModelDownloadManager().DownloadModelScopeFile(url, destination);
            if (code != 0) throw new InvalidOperationException("无法下载后端更新包：" + artifactPath);
        }
        else
        {
            var channelFile = Path.GetFullPath(channelSource);
            var channelDirectory = Path.GetDirectoryName(channelFile)!;
            var source = Path.IsPathRooted(artifactPath)
                ? Path.GetFullPath(artifactPath)
                : Path.GetFullPath(Path.Combine(channelDirectory, artifactPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(source)
                && Path.GetFileName(channelDirectory).Equals("Backend", StringComparison.OrdinalIgnoreCase)
                && artifactPath.Replace('\\', '/').StartsWith("Backend/", StringComparison.OrdinalIgnoreCase))
            {
                source = Path.GetFullPath(Path.Combine(
                    Directory.GetParent(channelDirectory)!.FullName,
                    artifactPath.Replace('/', Path.DirectorySeparatorChar)));
            }
            if (!File.Exists(source)) throw new FileNotFoundException("找不到本地后端更新包", source);
            File.Copy(source, destination, true);
        }
        Console.WriteLine("DOWNLOAD_STAGE|校验下载包");
        DownloadCancellation.Check();
        BackendUpdateManager.VerifyArtifact(destination, expectedSize, expectedSha256);
        return destination;
    }

    private static int ApplyBackendPatchArchive(string archive)
    {
        if (!File.Exists(archive)) return Fail("后端补丁不存在：" + archive, 1);
        var extractRoot = Path.Combine(
            BackendUpdateManager.StateRoot(CoreRoot),
            "extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            var code = ExtractArchive(archive, extractRoot);
            if (code != 0) return code;
            DownloadCancellation.Check();
            Console.WriteLine("BACKEND_INSTALL_START|应用补丁");
            var version = BackendUpdateManager.ApplyExtractedPatch(CoreRoot, extractRoot);
            Console.WriteLine("BACKEND_PATCH_COMPLETE|" + version);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("BACKEND_FULL_REQUIRED|增量补丁不适用于当前本地文件，可改用完整修复包");
            Console.Error.WriteLine("[错误] 应用后端补丁失败：" + ex.Message);
            return 1;
        }
        finally
        {
            if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true);
        }
    }

    private static int ApplyBackendFullArchive(string archive, string targetVersion)
    {
        var extractRoot = Path.Combine(
            BackendUpdateManager.StateRoot(CoreRoot),
            "full-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            var code = ExtractArchive(archive, extractRoot);
            if (code != 0) return code;
            var stagedPython = File.Exists(Path.Combine(extractRoot, "python", "python", "python.exe"))
                ? Path.Combine(extractRoot, "python")
                : extractRoot;
            DownloadCancellation.Check();
            Console.WriteLine("BACKEND_INSTALL_START|检查并安装后端");
            BackendUpdateManager.ApplyStagedFullBackend(CoreRoot, stagedPython, targetVersion);
            Console.WriteLine("BACKEND_UPDATE_COMPLETE|" + targetVersion);
            return 0;
        }
        catch (Exception ex)
        {
            DownloadCancellation.Log("完整后端安装 " + archive, ex);
            Console.Error.WriteLine(ex is OperationCanceledException
                ? "DOWNLOAD_CANCELLED|已取消"
                : "[错误] 完整后端安装失败：" + ex.Message);
            return 1;
        }
        finally
        {
            if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true);
        }
    }

    private static ModelDownloadManager CreateModelDownloadManager() => new(
        CoreRoot, ModelScopeToken, ToolVersion, CreateModelRepository(),
        DownloadWithAria, ExtractArchive, Fail);

    private static string EnsureEmbeddedFile(string resourceName, string fileName)
    {
        var directory = PortablePaths.EmbeddedToolsRoot(ToolVersion);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("内置工具资源不存在：" + fileName);
        var needsUpdate = !File.Exists(path);
        if (!needsUpdate)
        {
            using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resourceHash = SHA256.HashData(resource);
            var existingHash = SHA256.HashData(existing);
            needsUpdate = !resourceHash.AsSpan().SequenceEqual(existingHash);
            resource.Position = 0;
        }
        if (needsUpdate)
        {
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            resource.CopyTo(output);
        }
        return path;
    }

    /// <summary>把与插件版本配套的补帧检查/预构建脚本同步到当前 RVE 后端。</summary>
    private static void EnsureInterpolationSupportScripts()
    {
        var backendDirectory = Path.Combine(CoreRoot, "python", "backend");
        if (!Directory.Exists(backendDirectory)) return;
        try
        {
            InstallEmbeddedBackendScript(EmbeddedInterpolationInspectorResource, InterpolationInspectorScript);
            InstallEmbeddedBackendScript(EmbeddedUpscaleInspectorResource, UpscaleInspectorScript);
            InstallEmbeddedBackendScript(EmbeddedRifeTensorRTPrepareResource, RifeTensorRTPrepareScript);
            InstallEmbeddedBackendScript(EmbeddedFrameBackendResource, FrameBackendScript);
            InstallEmbeddedBackendScript(EmbeddedSegmentedBackendResource, SegmentedBackendScript);
            EnsureGmfssModelTypeCompatibility();
            EnsureGimmModelCompatibility();
            EnsurePytorchUpscaleCompatibility();
            EnsureOnnxModelCompatibility();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[警告] 无法同步补帧辅助脚本：" + ex.Message);
        }
    }

    private static void InstallEmbeddedBackendScript(string resourceName, string destinationPath)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("内置补帧脚本资源不存在：" + resourceName);
        var needsUpdate = !File.Exists(destinationPath);
        if (!needsUpdate)
        {
            using var existing = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resourceHash = SHA256.HashData(resource);
            var existingHash = SHA256.HashData(existing);
            needsUpdate = !resourceHash.AsSpan().SequenceEqual(existingHash);
            resource.Position = 0;
        }
        if (!needsUpdate) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        resource.CopyTo(output);
    }

    /// <summary>修补当前 RVE 2.4 GMFSS 加载器，使其按权重元数据区分 Base 与 Union。</summary>
    private static void EnsureGmfssModelTypeCompatibility()
    {
        var modelLoader = Path.Combine(CoreRoot, "python", "backend", "src", "pytorch",
            "InterpolateArchs", "GMFSS", "GMFSS.py");
        if (!File.Exists(modelLoader)) return;

        var bytes = File.ReadAllBytes(modelLoader);
        var hasUtf8Bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = Encoding.UTF8.GetString(bytes, hasUtf8Bom ? Encoding.UTF8.Preamble.Length : 0,
            bytes.Length - (hasUtf8Bom ? Encoding.UTF8.Preamble.Length : 0));
        if (text.Contains("detected_model_type", StringComparison.Ordinal)) return;

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = text.Replace("\r\n", "\n");
        const string oldDetect =
            "        combined_state_dict = torch.load(model_path, map_location=\"cpu\")\n\n" +
            "        archDetect = ArchDetect(combined_state_dict[\"rife\"])\n" +
            "        rife_version = archDetect.getArchName()\n" +
            "        # print(rife_version)\n" +
            "        if rife_version.lower() == \"rife46\":\n" +
            "            from .IFNet_HDv3 import IFNet\n" +
            "        else:\n" +
            "            # this is dumb, it detects rife4.7 with a stupid hack, so we need to just force load 422\n" +
            "            from .IFNet_HDv3_422 import IFNet";
        const string newDetect =
            "        combined_state_dict = torch.load(\n" +
            "            model_path, map_location=\"cpu\", weights_only=True, mmap=True\n" +
            "        )\n" +
            "        metadata = combined_state_dict.get(\"metadata\", {})\n" +
            "        detected_model_type = str(metadata.get(\"model_type\", \"\")).lower()\n" +
            "        if detected_model_type in {\"base\", \"union\"}:\n" +
            "            self.model_type = detected_model_type\n" +
            "        elif \"rife\" not in combined_state_dict:\n" +
            "            # 旧版 Base 权重没有 metadata，也没有 RIFE 分支。\n" +
            "            self.model_type = \"base\"\n\n" +
            "        IFNet = None\n" +
            "        if self.model_type != \"base\":\n" +
            "            if \"rife\" not in combined_state_dict:\n" +
            "                raise ValueError(\"GMFSS Union 权重缺少 rife 组件\")\n" +
            "            archDetect = ArchDetect(combined_state_dict[\"rife\"])\n" +
            "            rife_version = archDetect.getArchName()\n" +
            "            if rife_version.lower() == \"rife46\":\n" +
            "                from .IFNet_HDv3 import IFNet\n" +
            "            else:\n" +
            "                # RIFE 4.7 等分支继续使用现有 4.22 兼容实现。\n" +
            "                from .IFNet_HDv3_422 import IFNet";
        if (!normalized.Contains("from .FusionNet_u import GridNet", StringComparison.Ordinal)
            || !normalized.Contains(oldDetect, StringComparison.Ordinal)
            || !normalized.Contains("        self.ifnet = IFNet(ensemble=ensemble).to(dtype=dtype, device=device)", StringComparison.Ordinal)
            || !normalized.Contains("        self.fusionnet = GridNet().to(dtype=dtype, device=device)", StringComparison.Ordinal)
            || !normalized.Contains("        if model_type != \"base\":", StringComparison.Ordinal))
        {
            // 后端结构不是已知的 2.4 版本时不做文本改写，避免覆盖未来实现。
            return;
        }

        normalized = normalized
            .Replace("from .FusionNet_u import GridNet",
                "from .FusionNet_b import GridNet as BaseGridNet\nfrom .FusionNet_u import GridNet as UnionGridNet",
                StringComparison.Ordinal)
            .Replace(oldDetect, newDetect, StringComparison.Ordinal)
            .Replace("        self.ifnet = IFNet(ensemble=ensemble).to(dtype=dtype, device=device)",
                "        self.ifnet = (\n" +
                "            IFNet(ensemble=ensemble).to(dtype=dtype, device=device)\n" +
                "            if IFNet is not None\n" +
                "            else None\n" +
                "        )", StringComparison.Ordinal)
            .Replace("        self.fusionnet = GridNet().to(dtype=dtype, device=device)",
                "        fusionnet_class = BaseGridNet if self.model_type == \"base\" else UnionGridNet\n" +
                "        self.fusionnet = fusionnet_class().to(dtype=dtype, device=device)",
                StringComparison.Ordinal)
            .Replace("        if model_type != \"base\":",
                "        if self.model_type != \"base\":", StringComparison.Ordinal);

        File.WriteAllText(modelLoader, normalized.Replace("\n", newline), new UTF8Encoding(hasUtf8Bom));
    }

    /// <summary>修补当前 RVE 2.4 GIMM 加载器，使其兼容带元数据的新权重字段名。</summary>
    private static void EnsureGimmModelCompatibility()
    {
        var backendRoot = Path.Combine(CoreRoot, "python", "backend", "src", "pytorch");
        var files = new[]
        {
            Path.Combine(backendRoot, "InterpolateArchs", "DetectInterpolateArch.py"),
            Path.Combine(backendRoot, "InterpolateArchs", "GIMM", "gimmvfi_r.py"),
            Path.Combine(backendRoot, "InterpolateGIMM.py"),
        };
        if (files.Any(path => !File.Exists(path))) return;

        static void PatchUtf8File(string path, string oldValue, string newValue, string marker)
        {
            var bytes = File.ReadAllBytes(path);
            var hasUtf8Bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            var text = Encoding.UTF8.GetString(bytes, hasUtf8Bom ? Encoding.UTF8.Preamble.Length : 0,
                bytes.Length - (hasUtf8Bom ? Encoding.UTF8.Preamble.Length : 0));
            if (text.Contains(marker, StringComparison.Ordinal)) return;
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalized = text.Replace("\r\n", "\n");
            if (!normalized.Contains(oldValue, StringComparison.Ordinal)) return;
            normalized = normalized.Replace(oldValue, newValue, StringComparison.Ordinal);
            File.WriteAllText(path, normalized.Replace("\n", newline), new UTF8Encoding(hasUtf8Bom));
        }

        PatchUtf8File(
            files[0],
            "        if \"raft\" in self.state_dict:  # load in GIMM RAFT\n" +
            "            self.state_dict = self.state_dict[\"raft\"]",
            "        if \"raft\" in self.state_dict:  # load in legacy GIMM RAFT\n" +
            "            self.state_dict = self.state_dict[\"raft\"]\n" +
            "        elif \"flow_estimator\" in self.state_dict:  # 新模型包字段名\n" +
            "            self.state_dict = self.state_dict[\"flow_estimator\"]",
            "新模型包字段名");
        PatchUtf8File(
            files[1],
            "        ckpt = torch.load(model_path)\n" +
            "        model.load_state_dict(ckpt[\"raft\"], strict=True)",
            "        ckpt = torch.load(model_path, weights_only=True, map_location=\"cpu\")\n" +
            "        flow_state = ckpt.get(\"raft\", ckpt.get(\"flow_estimator\"))\n" +
            "        if flow_state is None:\n" +
            "            raise ValueError(\"GIMM 权重缺少 raft/flow_estimator 组件\")\n" +
            "        model.load_state_dict(flow_state, strict=True)",
            "GIMM 权重缺少 raft/flow_estimator 组件");
        PatchUtf8File(
            files[2],
            "            state_dict = torch.load(self.interpolateModel, map_location=self.device)[\n" +
            "                \"gimmvfi_r\"\n" +
            "            ]\n" +
            "            self.flownet.load_state_dict(state_dict)",
            "            combined_state_dict = torch.load(\n" +
            "                self.interpolateModel, map_location=self.device, weights_only=True\n" +
            "            )\n" +
            "            state_dict = combined_state_dict.get(\n" +
            "                \"gimmvfi_r\", combined_state_dict.get(\"gimmvfi\")\n" +
            "            )\n" +
            "            if state_dict is None:\n" +
            "                raise ValueError(\"GIMM 权重缺少 gimmvfi_r/gimmvfi 组件\")\n" +
            "            self.flownet.load_state_dict(state_dict)",
            "GIMM 权重缺少 gimmvfi_r/gimmvfi 组件");
        PatchUtf8File(
            files[2],
            "                    with torch.autocast(enabled=True, device_type=self.device.type):",
            "                    # FP32 兼容模式必须关闭 autocast，否则仍会回到半精度并产生 NaN。\n" +
            "                    with torch.autocast(\n" +
            "                        enabled=self.dtype != torch.float32, device_type=self.device.type\n" +
            "                    ):",
            "FP32 兼容模式必须关闭 autocast");
    }

    /// <summary>修补当前 RVE 2.4 ONNX 加载器的倍率解析与动态输入尺寸兼容性。</summary>
    private static void EnsureOnnxModelCompatibility()
    {
        var loader = Path.Combine(CoreRoot, "python", "backend", "src", "onnx", "UpscaleONNX.py");
        if (!File.Exists(loader)) return;
        var bytes = File.ReadAllBytes(loader);
        var hasUtf8Bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = Encoding.UTF8.GetString(bytes, hasUtf8Bom ? Encoding.UTF8.Preamble.Length : 0,
            bytes.Length - (hasUtf8Bom ? Encoding.UTF8.Preamble.Length : 0));
        const string oldValue = "    name = os.path.basename(modelPath).lower()";
        const string newValue = "    name = os.path.splitext(os.path.basename(modelPath))[0].lower()";
        if (!text.Contains(newValue, StringComparison.Ordinal)
            && text.Contains(oldValue, StringComparison.Ordinal))
        {
            text = text.Replace(oldValue, newValue, StringComparison.Ordinal);
        }

        const string marker = "VIDEOENHANCER_ONNX_INPUT_MULTIPLE";
        if (!text.Contains(marker, StringComparison.Ordinal))
        {
            const string oldInit =
                "        self.tilesize = max(0, int(tilesize or 0))\n" +
                "        self.gpu_id = gpu_id";
            const string newInit =
                "        self.tilesize = max(0, int(tilesize or 0))\n" +
                "        # 已知窗口注意力模型通过环境变量声明固有输入尺寸约束。\n" +
                "        self.input_multiple = max(1, int(os.environ.get(\"VIDEOENHANCER_ONNX_INPUT_MULTIPLE\", \"1\")))\n" +
                "        self.gpu_id = gpu_id";
            const string oldRun =
                "    def _run(self, image: np.ndarray) -> np.ndarray:\n" +
                "        output = self.inference_session.run(None, {self.input_name: self._prepare_input(image)})[0]\n" +
                "        return self._normalise_output(output)\n\n" +
                "    def _run_static_tiled";
            const string newRun =
                "    def _run(self, image: np.ndarray) -> np.ndarray:\n" +
                "        output = self.inference_session.run(None, {self.input_name: self._prepare_input(image)})[0]\n" +
                "        return self._normalise_output(output)\n\n" +
                "    def _run_padded(self, image: np.ndarray) -> np.ndarray:\n" +
                "        height, width = image.shape[:2]\n" +
                "        multiple = self.input_multiple\n" +
                "        pad_h = (-height) % multiple\n" +
                "        pad_w = (-width) % multiple\n" +
                "        if pad_h or pad_w:\n" +
                "            mode = \"reflect\" if height > 1 and width > 1 else \"edge\"\n" +
                "            image = np.pad(image, ((0, pad_h), (0, pad_w), (0, 0)), mode=mode)\n" +
                "        output = self._run(image)\n" +
                "        return output[: height * self.scale, : width * self.scale]\n\n" +
                "    def _run_dynamic_tiled(self, image: np.ndarray) -> np.ndarray:\n" +
                "        height, width = image.shape[:2]\n" +
                "        multiple = self.input_multiple\n" +
                "        core = max(multiple, self.tilesize - self.tilesize % multiple)\n" +
                "        overlap = max(self.tile_pad, multiple)\n" +
                "        result = np.empty((height * self.scale, width * self.scale, 3), dtype=np.uint8)\n" +
                "        for y in range(0, height, core):\n" +
                "            for x in range(0, width, core):\n" +
                "                actual_h = min(core, height - y)\n" +
                "                actual_w = min(core, width - x)\n" +
                "                y0, x0 = max(0, y - overlap), max(0, x - overlap)\n" +
                "                y1 = min(height, y + actual_h + overlap)\n" +
                "                x1 = min(width, x + actual_w + overlap)\n" +
                "                upscaled = self._run_padded(image[y0:y1, x0:x1])\n" +
                "                sy, sx = (y - y0) * self.scale, (x - x0) * self.scale\n" +
                "                cropped = upscaled[sy:sy + actual_h * self.scale, sx:sx + actual_w * self.scale]\n" +
                "                result[y * self.scale:(y + actual_h) * self.scale, x * self.scale:(x + actual_w) * self.scale] = cropped\n" +
                "        return result\n\n" +
                "    def _run_static_tiled";
            const string oldCall =
                "        if self.model_width is None or (\n" +
                "            image.shape[1] == self.model_width and image.shape[0] == self.model_height\n" +
                "        ):\n" +
                "            output = self._run(image)\n" +
                "        else:\n" +
                "            output = self._run_static_tiled(image)";
            const string newCall =
                "        if self.model_width is None:\n" +
                "            output = self._run_dynamic_tiled(image) if self.tilesize > 0 else self._run_padded(image)\n" +
                "        elif image.shape[1] == self.model_width and image.shape[0] == self.model_height:\n" +
                "            output = self._run(image)\n" +
                "        else:\n" +
                "            output = self._run_static_tiled(image)";

            var normalized = text.Replace("\r\n", "\n");
            if (normalized.Contains(oldInit, StringComparison.Ordinal)
                && normalized.Contains(oldRun, StringComparison.Ordinal)
                && normalized.Contains(oldCall, StringComparison.Ordinal))
            {
                normalized = normalized.Replace(oldInit, newInit, StringComparison.Ordinal)
                    .Replace(oldRun, newRun, StringComparison.Ordinal)
                    .Replace(oldCall, newCall, StringComparison.Ordinal);
                var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                text = normalized.Replace("\n", newline);
            }
        }

        var original = Encoding.UTF8.GetString(bytes, hasUtf8Bom ? Encoding.UTF8.Preamble.Length : 0,
            bytes.Length - (hasUtf8Bom ? Encoding.UTF8.Preamble.Length : 0));
        if (!text.Equals(original, StringComparison.Ordinal))
        {
            File.WriteAllText(loader, text, new UTF8Encoding(hasUtf8Bom));
        }
    }

    /// <summary>让 PyTorch/TensorRT 超分对模型声明的输入倍数统一补边并裁回原尺寸。</summary>
    private static void EnsurePytorchUpscaleCompatibility()
    {
        var loader = Path.Combine(CoreRoot, "python", "backend", "src", "pytorch", "UpscaleTorch.py");
        if (!File.Exists(loader)) return;
        var bytes = File.ReadAllBytes(loader);
        var hasUtf8Bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = Encoding.UTF8.GetString(bytes, hasUtf8Bom ? Encoding.UTF8.Preamble.Length : 0,
            bytes.Length - (hasUtf8Bom ? Encoding.UTF8.Preamble.Length : 0));
        if (text.Contains("VIDEOENHANCER_UPSCALE_INPUT_MULTIPLE", StringComparison.Ordinal)) return;

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = text.Replace("\r\n", "\n");
        const string oldInit =
            "        self.tilesize = tilesize\n" +
            "        self.tile = [self.tilesize, self.tilesize]";
        const string newInit =
            "        self.tilesize = tilesize\n" +
            "        # 插件的本地能力清单为已知模型声明真实的输入尺寸倍数。\n" +
            "        self.input_multiple = max(1, int(os.environ.get(\"VIDEOENHANCER_UPSCALE_INPUT_MULTIPLE\", \"1\")))\n" +
            "        self.tile = [self.tilesize, self.tilesize]";
        const string oldModulo =
            "            match self.scale:\n" +
            "                case 1:\n" +
            "                    modulo = 4\n" +
            "                case 2:\n" +
            "                    modulo = 2\n" +
            "                case _:\n" +
            "                    modulo = 1";
        const string newModulo =
            "            match self.scale:\n" +
            "                case 1:\n" +
            "                    modulo = 4\n" +
            "                case 2:\n" +
            "                    modulo = 2\n" +
            "                case _:\n" +
            "                    modulo = 1\n" +
            "            modulo = math.lcm(modulo, self.input_multiple)";
        const string oldInference =
            "            if not self.use_tiling:\n" +
            "                output = self.upscale_model_wrapper(image_tensor)\n" +
            "            else:\n" +
            "                output = self.renderTiledImage(image_tensor)";
        const string newInference =
            "            if not self.use_tiling:\n" +
            "                original_h, original_w = image_tensor.shape[-2:]\n" +
            "                pad_h = (-original_h) % self.input_multiple\n" +
            "                pad_w = (-original_w) % self.input_multiple\n" +
            "                if pad_h or pad_w:\n" +
            "                    image_tensor = F.pad(image_tensor, (0, pad_w, 0, pad_h), \"replicate\")\n" +
            "                output = self.upscale_model_wrapper(image_tensor)\n" +
            "                output = output[:, :, : original_h * self.scale, : original_w * self.scale]\n" +
            "            else:\n" +
            "                output = self.renderTiledImage(image_tensor)";

        if (!normalized.Contains(oldInit, StringComparison.Ordinal)
            || !normalized.Contains(oldModulo, StringComparison.Ordinal)
            || !normalized.Contains(oldInference, StringComparison.Ordinal))
        {
            return;
        }
        normalized = normalized.Replace(oldInit, newInit, StringComparison.Ordinal)
            .Replace(oldModulo, newModulo, StringComparison.Ordinal)
            .Replace(oldInference, newInference, StringComparison.Ordinal);
        File.WriteAllText(loader, normalized.Replace("\n", newline), new UTF8Encoding(hasUtf8Bom));
    }

    private static int DownloadWithAria(string url, string destination, bool printComplete = true)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var aria = Aria2NextExe;
            if (!File.Exists(aria))
                return Fail("缺少独立下载组件 aria2-next：" + aria + "。请重新安装完整发行包。", 1);
            var start = new ProcessStartInfo
            {
                FileName = aria,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            PortablePaths.ConfigureChildProcess(start);
            DownloadCancellation.Check();
            foreach (var argument in new[]
            {
                "--allow-overwrite=true", "--auto-file-renaming=false", "--continue=true",
                "--file-allocation=none", "--max-connection-per-server=8", "--split=8",
                "--min-split-size=1M", "--summary-interval=1", "--enable-color=false",
                "--dir=" + Path.GetDirectoryName(destination), "--out=" + Path.GetFileName(destination), url
            }) start.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = start };
            var percentRegex = new Regex(@"\((\d{1,3})%\)", RegexOptions.Compiled);
            var lastPercent = -1;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var match = percentRegex.Match(e.Data);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var percent) && percent != lastPercent)
                {
                    lastPercent = percent;
                    Console.WriteLine($"DOWNLOAD_PROGRESS|{percent}|{Path.GetFileName(destination)}");
                }
            };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
            if (!process.Start()) return Fail("无法启动 aria2-next", 1);
            using var cancellation = DownloadCancellation.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            DownloadCancellation.Check();
            if (process.ExitCode != 0) return Fail("aria2-next 下载失败，退出码：" + process.ExitCode, 1);
            if (!File.Exists(destination)) return Fail("下载结束但未找到输出文件：" + destination, 1);
            if (printComplete) Console.WriteLine("DOWNLOAD_COMPLETE|" + destination);
            return 0;
        }
        catch (Exception ex)
        {
            DownloadCancellation.Log("下载 " + destination, ex);
            Console.Error.WriteLine(ex is OperationCanceledException && DownloadCancellation.Token.IsCancellationRequested
                ? "DOWNLOAD_CANCELLED|已取消" : "[错误] 下载失败：" + ex.Message);
            return 1;
        }
    }

    private static int ExtractArchive(string archive, string outputDirectory, bool printComplete = true)
    {
        try
        {
            if (!File.Exists(archive)) return Fail("压缩文件不存在：" + archive, 1);
            if (printComplete) Console.WriteLine("EXTRACT_START|" + outputDirectory);
            ManagedArchiveExtractor.Extract(archive, outputDirectory,
                printComplete ? percent => Console.WriteLine("EXTRACT_PROGRESS|" + percent) : null);
            if (printComplete) Console.WriteLine("EXTRACT_COMPLETE|" + outputDirectory);
            return 0;
        }
        catch (Exception ex)
        {
            DownloadCancellation.Log("解压 " + archive, ex);
            Console.Error.WriteLine(ex is OperationCanceledException
                ? "DOWNLOAD_CANCELLED|已取消"
                : "[错误] 解压失败：" + ex.Message);
            return 1;
        }
    }

    /// <summary>是否为当前图像超分加载器可读取的权重文件。</summary>
    private static bool IsPthModelFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".pth", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".pt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".pkl", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ckpt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".safetensors", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前 RIFE/GMFSS/GIMM 检测器明确支持的补帧权重格式。</summary>
    private static bool IsInterpolationWeightFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".pth", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".pt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".pkl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTensorRTEngineFile(string path) =>
        Path.GetExtension(path).Equals(".engine", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] FlashVsrWeights =
    {
        "diffusion_pytorch_model_streaming_dmd.safetensors",
        "LQ_proj_in.ckpt",
        "TCDecoder.ckpt",
        "Wan2.1_VAE.pth",
    };

    private static bool IsFlashVsrModelDirectory(string path) =>
        Directory.Exists(path) && FlashVsrWeights.All(name => File.Exists(Path.Combine(path, name)));

    private static bool IsBasicVsrPlusPlusModelDirectory(string path) =>
        Directory.Exists(path)
        && File.Exists(Path.Combine(path, "config.py"))
        && File.Exists(Path.Combine(path, "chkpts.pth"));

    private static bool IsBasicVsrPlusPlusModel(string path) =>
        IsBasicVsrPlusPlusModelDirectory(path)
        || (File.Exists(path) && Path.GetExtension(path).Equals(".pth", StringComparison.OrdinalIgnoreCase)
            && IsInBasicVsrPlusPlusDirectory(path));

    private static string BasicVsrPlusPlusScale(string path) =>
        IsBasicVsrPlusPlusModelDirectory(path) ? "1" : "4";

    /// <summary>优先查询内置能力清单；未知模型才从文件名保守解析倍率。</summary>
    internal static ModelImportInspection? InspectModelCached(string path) =>
        ModelInspectionCache.Get(path, new ModelImportManager(ModelsDir, PythonExe, UpscaleInspectorScript, InterpolationInspectorScript));

    private static string? DetectScale(string modelFolder)
    {
        if (ModelCapabilityCatalog.TryGet(modelFolder, ModelsDir, out var capability))
        {
            return capability.Scale.ToString(CultureInfo.InvariantCulture);
        }
        if (IsBasicVsrPlusPlusModel(modelFolder))
        {
            return BasicVsrPlusPlusScale(modelFolder);
        }
        if (IsFlashVsrModelDirectory(modelFolder))
        {
            return "4";
        }
        var ncnnSignature = NcnnModelSignatures.Get(modelFolder);
        if (ncnnSignature is not null) return ncnnSignature.Value.Scale.ToString(CultureInfo.InvariantCulture);
        var inspection = InspectModelCached(modelFolder);
        if (inspection is not null && inspection.Scale > 0) return inspection.Scale.ToString(CultureInfo.InvariantCulture);
        var name = Path.GetFileName(modelFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var stem = Path.GetFileNameWithoutExtension(name);
        // RealESRGAN AnimeVideo v3 的官方文件名没有倍率后缀，但模型原生输出为 4 倍。
        if (stem.Equals("realesr-animevideov3", StringComparison.OrdinalIgnoreCase))
        {
            return "4";
        }
        // 优先取最后一个独立倍率标记。ONNX 文件名可能先包含 1x3xHxW 张量形状，
        // 直接取第一个 x 数字会把通道数误当成放大倍率。
        var matches = Regex.Matches(name, @"(?:^|[-_])(\d+)x(?=[-_.]|$)", RegexOptions.IgnoreCase);
        if (matches.Count > 0)
        {
            return matches[^1].Groups[1].Value;
        }
        // x4 形式也必须位于独立字段边界，避免把 fix1、fix2 等普通单词尾部误判为倍率。
        matches = Regex.Matches(name, @"(?:^|[-_])x(\d+)(?=[-_.v]|$)", RegexOptions.IgnoreCase);
        return matches.Count > 0 ? matches[^1].Groups[1].Value : null;
    }

    /// <summary>构建 rve-backend.py 的命令行参数，逻辑与 GUI 的 RvePaths.BuildBackendArgs 一致。</summary>
    private static List<string> BuildBackendArgs(
        string input, string outputFile, string modelFolder, string customEncoder, bool overwrite, string? scale, string pauseShm,
        string? interpModel, string? interpFactor, string backend, string? backendScript = null, bool hdrMode = false,
        bool dynamicOpticalFlow = false, double sceneThreshold = 4.0, int tileSize = 0, string precision = "auto")
    {
        var args = new List<string>
        {
            string.IsNullOrWhiteSpace(backendScript) ? BackendScript : backendScript,
            "-i", input,
            "-o", outputFile,
            "-b", backend is "cuda" or "tensorrt" ? (backend == "tensorrt" ? "tensorrt" : "pytorch") : backend,
            "--precision", precision,
            "--custom_encoder", " " + customEncoder + " ",
            "--tensorrt_opt_profile", "3",
            "--pytorch_gpu_id", "0",
            "--cwd", CoreRoot,
            "--ffmpeg_path", FfmpegExe,
        };
        if (backend is "cuda" or "tensorrt" or "onnx" or "flashvsr" or "basicvsrpp")
        {
            args.Add("--device");
            args.Add("cuda");
        }
        else
        {
            args.Add("--ncnn_gpu_id");
            args.Add("0");
        }

        if (hdrMode)
        {
            args.Add("--hdr_mode");
        }

        if (!string.IsNullOrEmpty(modelFolder))
        {
            args.Add("--upscale_model");
            // 导入目录名可能与 param/bin 文件名不同；直接传入真实模型文件，不移动用户文件。
            args.Add(backend == "ncnn" && Directory.Exists(modelFolder)
                ? Path.ChangeExtension(Directory.EnumerateFiles(modelFolder, "*.param").First(), ".bin")
                : modelFolder);
            if (tileSize > 0 && backend is ("ncnn" or "cuda" or "tensorrt" or "onnx"))
            {
                args.Add("--tilesize");
                args.Add(tileSize.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (interpModel is not null)
        {
            args.Add("--interpolate_model");
            args.Add(interpModel);
            args.Add("--interpolate_factor");
            args.Add(interpFactor ?? "2");
        }

        if (!string.IsNullOrEmpty(scale))
        {
            args.Add("--override_upscale_scale");
            args.Add(scale);
        }

        args.Add("--scene_detect_method");
        if (backend is "cuda" or "tensorrt")
        {
            // 当前模型包只提供 NCNN 格式的 EfficientNet 转场模型。CUDA/TensorRT
            // 主后端会把模型路径交给 torch.jit.load，因此改用 RVE 内置且无需模型的检测器。
            args.Add("pyscenedetect");
        }
        else
        {
            args.Add("sudo_scene_detect");
            args.Add("--scene_detect_model");
            args.Add(SceneDetectModel);
        }
        args.Add("--scene_detect_threshold");
        // 直接使用 RVE 官方外部阈值标尺；RVE 内部负责换算为模型阈值。
        args.Add(sceneThreshold.ToString("0.###", CultureInfo.InvariantCulture));
        if (dynamicOpticalFlow && backend == "cuda" && interpModel is not null)
        {
            args.Add("--dynamic_scaled_optical_flow");
        }

        if (overwrite)
        {
            args.Add("--overwrite");
        }

        if (!string.IsNullOrWhiteSpace(pauseShm))
        {
            args.Add("--pause_shared_memory_id");
            args.Add(pauseShm);
        }
        return args;
    }

    /// <summary>超分精度独立决策：用户强制 FP32 优先，已知 FP16 数值不稳定的模型保持 FP32。</summary>
    private static string ResolveUpscalePrecision(string modelPath, string backend, string requested)
    {
        if (requested == "float32") return "float32";
        // DAT2 与 AniToon-RPLKSRL 在当前 PyTorch/CUDA FP16 路径会直接输出 NaN；
        // 后续转字节会把 NaN 隐式变为黑值，因此不能依赖编码阶段发现问题。
        if (backend == "cuda" && Regex.IsMatch(
                ModelBaseName(modelPath), @"SwinIR|GRL|DAT2|AniToon-RPLKSRL", RegexOptions.IgnoreCase))
            return "float32";
        if (backend == "tensorrt" && Regex.IsMatch(ModelBaseName(modelPath), @"GRL", RegexOptions.IgnoreCase))
            return "float32";
        return requested;
    }

    /// <summary>补帧精度独立决策：GIMM 的 CUDA 路径必须使用 FP32，其他模型优先半精度。</summary>
    private static string ResolveInterpPrecision(string? modelPath, string backend, string requested)
    {
        if (requested == "float32") return "float32";
        if (backend == "cuda" && Regex.IsMatch(ModelBaseName(modelPath ?? ""), @"GIMM", RegexOptions.IgnoreCase))
            return "float32";
        return requested;
    }

    /// <summary>解析补帧模型路径：完整路径 / Frame-Interpolation 下相对路径 / 模型名；返回空串表示失败。</summary>
    /// <remarks>新目录按后端区分 NCNN 文件夹和 PyTorch 权重；TensorRT 由 RVE 从 RIFE 权重自动构建 Engine。</remarks>
    private static string ResolveInterpModel(string requested, string backend)
    {
        var raw = requested.Trim().Trim('"');
        var candidates = new List<string> { Path.GetFullPath(raw) };
        if (!Path.IsPathRooted(raw))
        {
            candidates.Add(Path.Combine(FrameInterpolationDir, raw));
            candidates.Add(Path.Combine(ModelsDir, raw));
            candidates.Add(Path.Combine(UserInterpolationDir, raw));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (backend == "cuda")
            {
                if (File.Exists(candidate) && IsInterpolationWeightFile(candidate)
                    && InspectInterpolationCapabilities(new[] { candidate })
                        .TryGetValue(Path.GetFullPath(candidate), out var capability)
                    && string.IsNullOrWhiteSpace(capability.Error) && capability.Cuda)
                {
                    return Path.GetFullPath(candidate);
                }
            }
            else if (backend == "tensorrt")
            {
                if (File.Exists(candidate) && IsRifeInterpolationSource(candidate)) return Path.GetFullPath(candidate);
            }
            else if (Directory.Exists(candidate) && IsNcnnModelFolder(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var normalizedRaw = raw.Replace('\\', '/').TrimEnd('/');
        var discovered = DiscoverInterpModels(backend);

        // UI 保存的是相对架构路径。先处理这个无歧义的精确值，避免把
        // rife4.26.heavy 中的 .heavy 误当作扩展名后又匹配到 rife4.26。
        var exactMatched = discovered
            .Where(path => InterpModelDisplayName(path).Equals(normalizedRaw, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exactMatched.Count == 1)
        {
            return exactMatched[0];
        }
        if (exactMatched.Count > 1)
        {
            Console.Error.WriteLine("[错误] 补帧模型相对路径不唯一：" + raw);
            return "";
        }

        var requestedName = InterpModelLookupName(normalizedRaw);
        var matched = discovered
            .Where(path => InterpModelLookupName(InterpModelDisplayName(path))
                .Equals(requestedName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matched.Count == 1)
        {
            return matched[0];
        }
        if (matched.Count > 1)
        {
            Console.Error.WriteLine("[错误] 补帧模型名不唯一，请使用包含架构目录的相对路径：" + raw);
            foreach (var path in matched)
            {
                Console.Error.WriteLine("       " + InterpModelDisplayName(path));
            }
            return "";
        }

        Console.Error.WriteLine("[错误] 未找到可用补帧模型：" + raw);
        Console.Error.WriteLine(backend is "cuda" or "tensorrt"
            ? "[提示] 可用补帧模型（" + (backend == "tensorrt" ? "RIFE TensorRT 自动构建" : "CUDA/PyTorch") + "）："
            : @"[提示] 可用补帧模型（models\Frame-Interpolation）：");
        foreach (var m in DiscoverInterpModels(backend))
        {
            Console.Error.WriteLine("       " + InterpModelDisplayName(m));
        }
        Console.Error.WriteLine(backend is "cuda" or "tensorrt"
            ? "[提示] 用法：-interp-backend " + backend + " -interp-model <模型名>，例如 -interp-model rife46"
            : "[提示] 用法：-interp-model <模型名或路径>，例如 -interp-model rife-v4.25");
        return "";
    }

    /// <summary>生成补帧模型的短名称，只剥离真实权重扩展名，保留 .heavy 等模型名后缀。</summary>
    private static string InterpModelLookupName(string value)
    {
        var fileName = Path.GetFileName(value.Replace('\\', '/').TrimEnd('/'));
        var extension = Path.GetExtension(fileName);
        return new[] { ".pth", ".pt", ".pkl", ".engine" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
    }

    /// <summary>发现 Frame-Interpolation 和用户导入的补帧模型。</summary>
    private static List<string> DiscoverInterpModels(string backend)
    {
        var roots = new[] { FrameInterpolationDir, UserInterpolationDir }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0)
        {
            return new List<string>();
        }
        if (backend is "cuda" or "tensorrt")
        {
            var weights = roots.SelectMany(root => new[] { "*.pth", "*.pt", "*.pkl" }
                    .SelectMany(pattern => Directory.GetFiles(root, pattern, SearchOption.AllDirectories)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var capabilities = InspectInterpolationCapabilities(weights);
            return weights.Where(path => capabilities.TryGetValue(Path.GetFullPath(path), out var capability)
                    && string.IsNullOrWhiteSpace(capability.Error)
                    && (backend == "cuda" ? capability.Cuda : capability.TensorRT))
                .ToList();
        }
        var ncnnFolders = roots.SelectMany(root => Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            .Where(IsNcnnModelFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ncnnFolders.OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>补帧模型显示为相对架构路径。</summary>
    private static string InterpModelDisplayName(string path)
    {
        string relative;
        if (IsPathUnder(path, FrameInterpolationDir))
        {
            relative = Path.GetRelativePath(FrameInterpolationDir, path);
        }
        else if (IsPathUnder(path, UserInterpolationDir))
        {
            relative = Path.GetRelativePath(ModelsDir, path);
        }
        else
        {
            relative = Path.GetFileName(path);
        }
        var extension = Path.GetExtension(path);
        if (new[] { ".pth", ".pt", ".pkl", ".engine" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            relative = Path.ChangeExtension(relative, null) ?? relative;
        }
        return relative.Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>只有 RIFE 权重具备当前 RVE 后端的 TensorRT 自动构建实现。</summary>
    private static bool IsRifeInterpolationSource(string path)
    {
        if (!File.Exists(path) || !IsInterpolationWeightFile(path)) return false;
        var capabilities = InspectInterpolationCapabilities(new[] { path });
        return capabilities.TryGetValue(Path.GetFullPath(path), out var capability)
            && string.IsNullOrWhiteSpace(capability.Error)
            && capability.TensorRT;
    }

    private sealed record InterpolationCapability(
        string Path, string Architecture, string BaseArchitecture, bool Cuda, bool TensorRT, string Error);

    private sealed record CachedInterpolationCapability(
        long Length, long LastWriteTimeUtcTicks, InterpolationCapability Capability);

    private static Dictionary<string, CachedInterpolationCapability> LoadInterpolationCapabilityCache()
    {
        var cache = new Dictionary<string, CachedInterpolationCapability>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(InterpolationCapabilityCachePath)) return cache;
            using var document = JsonDocument.Parse(File.ReadAllBytes(InterpolationCapabilityCachePath));
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != InterpolationCapabilityCacheVersion) return cache;
            foreach (var item in root.GetProperty("entries").EnumerateArray())
            {
                var path = Path.GetFullPath(item.GetProperty("path").GetString() ?? "");
                if (string.IsNullOrWhiteSpace(path)) continue;
                var capability = new InterpolationCapability(
                    path,
                    item.GetProperty("architecture").GetString() ?? "",
                    item.GetProperty("base_architecture").GetString() ?? "",
                    item.GetProperty("cuda").GetBoolean(),
                    item.GetProperty("tensorrt").GetBoolean(),
                    item.GetProperty("error").GetString() ?? "");
                cache[path] = new CachedInterpolationCapability(
                    item.GetProperty("length").GetInt64(),
                    item.GetProperty("last_write_utc_ticks").GetInt64(),
                    capability);
            }
        }
        catch
        {
            // 缓存损坏或被其他操作同时替换时直接重新检查，不影响模型列表。
        }
        return cache;
    }

    private static void SaveInterpolationCapabilityCache(
        Dictionary<string, CachedInterpolationCapability> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InterpolationCapabilityCachePath)!);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", InterpolationCapabilityCacheVersion);
                writer.WriteStartArray("entries");
                foreach (var entry in cache.Values
                             .Where(entry => File.Exists(entry.Capability.Path))
                             .Take(512))
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", entry.Capability.Path);
                    writer.WriteNumber("length", entry.Length);
                    writer.WriteNumber("last_write_utc_ticks", entry.LastWriteTimeUtcTicks);
                    writer.WriteString("architecture", entry.Capability.Architecture);
                    writer.WriteString("base_architecture", entry.Capability.BaseArchitecture);
                    writer.WriteBoolean("cuda", entry.Capability.Cuda);
                    writer.WriteBoolean("tensorrt", entry.Capability.TensorRT);
                    writer.WriteString("error", entry.Capability.Error);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            File.WriteAllBytes(InterpolationCapabilityCachePath, buffer.ToArray());
        }
        catch
        {
            // 缓存只是性能优化；只读目录或并发写入失败时仍使用本次检查结果。
        }
    }

    /// <summary>读取权重内部结构，不再用扩展名或目录名猜测补帧架构。</summary>
    private static Dictionary<string, InterpolationCapability> InspectInterpolationCapabilities(IEnumerable<string> modelPaths)
    {
        var paths = modelPaths.Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new Dictionary<string, InterpolationCapability>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
        {
            return result;
        }

        var cache = LoadInterpolationCapabilityCache();
        var pendingPaths = new List<string>();
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (cache.TryGetValue(path, out var cached)
                && cached.Length == info.Length
                && cached.LastWriteTimeUtcTicks == info.LastWriteTimeUtc.Ticks)
            {
                result[path] = cached.Capability;
            }
            else
            {
                pendingPaths.Add(path);
            }
        }
        if (pendingPaths.Count == 0
            || !File.Exists(PythonExe)
            || !File.Exists(InterpolationInspectorScript))
        {
            return result;
        }

        var args = new List<string> { InterpolationInspectorScript };
        args.AddRange(pendingPaths);
        var inspected = RunProcessCapture(PythonExe, args.ToArray(), 180);
        var jsonLine = inspected.Output.Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("[", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(jsonLine)) return result;

        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var fullPath = Path.GetFullPath(item.GetProperty("path").GetString() ?? "");
                result[fullPath] = new InterpolationCapability(
                    fullPath,
                    item.GetProperty("architecture").GetString() ?? "",
                    item.GetProperty("base_architecture").GetString() ?? "",
                    item.GetProperty("cuda").GetBoolean(),
                    item.GetProperty("tensorrt").GetBoolean(),
                    item.GetProperty("error").GetString() ?? "");
                var info = new FileInfo(fullPath);
                cache[fullPath] = new CachedInterpolationCapability(
                    info.Length, info.LastWriteTimeUtc.Ticks, result[fullPath]);
            }
            SaveInterpolationCapabilityCache(cache);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[警告] 补帧模型架构检查输出无效：" + ex.Message);
        }
        return result;
    }

    private static int InspectInterpolationModel(string requested)
    {
        var path = File.Exists(requested) ? Path.GetFullPath(requested) : ResolveInterpModel(requested, "cuda");
        if (string.IsNullOrWhiteSpace(path)) return 2;
        var capabilities = InspectInterpolationCapabilities(new[] { path });
        if (!capabilities.TryGetValue(path, out var capability))
            return Fail("补帧模型检查器不可用，请确认 inspect_interpolation_models.py 已安装");
        static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";
        Console.WriteLine("{" +
            "\"path\":" + JsonString(capability.Path) + "," +
            "\"architecture\":" + JsonString(capability.Architecture) + "," +
            "\"base_architecture\":" + JsonString(capability.BaseArchitecture) + "," +
            "\"cuda\":" + capability.Cuda.ToString().ToLowerInvariant() + "," +
            "\"tensorrt\":" + capability.TensorRT.ToString().ToLowerInvariant() + "," +
            "\"error\":" + JsonString(capability.Error) + "}");
        return string.IsNullOrWhiteSpace(capability.Error) ? 0 : 2;
    }

    private static int InspectUpscaleModel(string requested)
    {
        var path = Path.GetFullPath(requested.Trim().Trim('"'));
        var manager = new ModelImportManager(ModelsDir, PythonExe, UpscaleInspectorScript, InterpolationInspectorScript);
        var inspection = manager.Inspect(path);
        WriteInspectionJson(inspection);
        return string.IsNullOrWhiteSpace(inspection.Error) ? 0 : 2;
    }

    private static int ImportModels(string requested, bool json)
    {
        var source = Path.GetFullPath(requested.Trim().Trim('"'));
        string? extractionRoot = null;
        try
        {
            if (File.Exists(source) && IsModelArchive(source))
            {
                extractionRoot = PortablePaths.CreateWorkDirectory("model-import");
                if (ExtractArchive(source, extractionRoot, printComplete: false) != 0)
                    return 1;
                source = extractionRoot;
            }
            var manager = new ModelImportManager(ModelsDir, PythonExe, UpscaleInspectorScript, InterpolationInspectorScript);
            var results = manager.Import(source);
            if (json)
                WriteImportResultsJson(results);
            else
                foreach (var result in results)
                    if (result.Success)
                        Console.WriteLine("[已导入] " + result.Id + "  " + string.Join('/', result.Backends));
                    else
                        Console.Error.WriteLine("[失败] " + result.Source + "：" + result.Error);
            return results.Count > 0 && results.All(result => result.Success) ? 0 : 2;
        }
        finally
        {
            if (extractionRoot is not null && Directory.Exists(extractionRoot))
            {
                try { Directory.Delete(extractionRoot, recursive: true); }
                catch { }
            }
        }
    }

    private static bool IsModelArchive(string path) =>
        new[] { ".zip", ".7z", ".rar", ".tar", ".gz", ".xz", ".zst" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static void WriteInspectionJson(ModelImportInspection item)
    {
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput());
        writer.WriteStartObject();
        writer.WriteString("path", item.Path);
        writer.WriteString("format", item.Format);
        writer.WriteString("task", item.Task);
        writer.WriteString("architecture", item.Architecture);
        writer.WriteString("purpose", item.Purpose);
        writer.WriteNumber("scale", item.Scale);
        writer.WriteNumber("inputChannels", item.InputChannels);
        writer.WriteNumber("outputChannels", item.OutputChannels);
        writer.WriteNumber("inputMultiple", item.InputMultiple);
        writer.WriteNumber("minimumSize", item.MinimumSize);
        writer.WriteBoolean("square", item.Square);
        writer.WriteBoolean("supportsHalf", item.SupportsHalf);
        writer.WriteBoolean("supportsBfloat16", item.SupportsBFloat16);
        writer.WriteString("tiling", item.Tiling);
        writer.WriteStartArray("backends");
        foreach (var backend in item.Backends) writer.WriteStringValue(backend);
        writer.WriteEndArray();
        writer.WriteString("error", item.Error);
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
    }

    private static void WriteImportResultsJson(IReadOnlyList<ModelImportResult> results)
    {
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput());
        writer.WriteStartArray();
        foreach (var item in results)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", item.Success);
            writer.WriteString("architectureGroup", ModelArchitectureGroups.Get(item.Architecture));
            writer.WriteString("source", item.Source);
            writer.WriteString("id", item.Id);
            writer.WriteString("installedPath", item.InstalledPath);
            writer.WriteString("task", item.Task);
            writer.WriteString("architecture", item.Architecture);
            writer.WriteString("purpose", item.Purpose);
            writer.WriteNumber("scale", item.Scale);
            writer.WriteStartArray("backends");
            foreach (var backend in item.Backends) writer.WriteStringValue(backend);
            writer.WriteEndArray();
            writer.WriteString("error", item.Error);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.Flush();
        Console.WriteLine();
    }

    private static int PrepareRifeTensorRTEngine(string requested, int width, int height, bool staticShape)
    {
        if (!File.Exists(PythonExe)) return Fail("找不到便携 Python：" + PythonExe);
        if (!File.Exists(RifeTensorRTPrepareScript))
            return Fail("缺少 RIFE TensorRT 预构建脚本：" + RifeTensorRTPrepareScript);
        var model = File.Exists(requested) ? Path.GetFullPath(requested) : ResolveInterpModel(requested, "tensorrt");
        if (string.IsNullOrWhiteSpace(model)) return 2;

        var start = new ProcessStartInfo
        {
            FileName = PythonExe,
            WorkingDirectory = Path.GetDirectoryName(RifeTensorRTPrepareScript)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        PortablePaths.ConfigureChildProcess(start);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var arg in new[]
        {
            RifeTensorRTPrepareScript, model,
            "--width", width.ToString(CultureInfo.InvariantCulture),
            "--height", height.ToString(CultureInfo.InvariantCulture)
        }) start.ArgumentList.Add(arg);
        if (staticShape) start.ArgumentList.Add("--static-shape");

        using var process = Process.Start(start);
        if (process is null) return Fail("无法启动 RIFE TensorRT 预构建进程");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static int ListInterpModels(bool json, string backend)
    {
        var models = DiscoverInterpModels(backend);
        if (json)
        {
            var names = models.Select(InterpModelDisplayName).ToList();
            Console.WriteLine("[" + string.Join(",", names.Select(n => "\"" + n + "\"")) + "]");
            return 0;
        }
        Console.WriteLine(backend is "cuda" or "tensorrt"
            ? "可用补帧模型（" + (backend == "tensorrt" ? "RIFE TensorRT 自动构建" : "CUDA/PyTorch") + "，models\\Frame-Interpolation）："
            : @"可用补帧模型（models\Frame-Interpolation）：");
        if (models.Count == 0)
        {
            Console.WriteLine(backend is "cuda" or "tensorrt"
                ? "  (未找到兼容的补帧权重；请检查 models\\Frame-Interpolation 目录和所选后端)"
                : "  (未找到任何含 .param/.bin 的补帧模型文件夹)");
            return 0;
        }
        foreach (var m in models)
        {
            Console.WriteLine("  " + InterpModelDisplayName(m));
        }
        return 0;
    }

    private sealed class PreparedSegment
    {
        public long Start { get; set; }
        public long End { get; set; }
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public string Backend { get; set; } = "";
        public string Model { get; set; } = "";
        public int Scale { get; set; }
        public int InputMultiple { get; set; } = 1;
        public int OutputWidth { get; set; }
        public int OutputHeight { get; set; }
    }

    private static string EncodePreparedSegments(IEnumerable<PreparedSegment> segments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var segment in segments)
            {
                writer.WriteStartObject();
                writer.WriteNumber("start", segment.Start);
                writer.WriteNumber("end", segment.End);
                writer.WriteString("backend", segment.Backend);
                writer.WriteString("model", segment.Model);
                writer.WriteNumber("scale", segment.Scale);
                writer.WriteNumber("inputMultiple", segment.InputMultiple);
                writer.WriteNumber("outputWidth", segment.OutputWidth);
                writer.WriteNumber("outputHeight", segment.OutputHeight);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Convert.ToBase64String(stream.ToArray());
    }

    private static string EncodeStringList(IEnumerable<string> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var value in values) writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
        return Convert.ToBase64String(stream.ToArray());
    }

    private static bool IsSegmentModelBackend(string backend) =>
        backend is "ncnn" or "cuda" or "tensorrt" or "onnx";

    private static string ResolveAnime4kShaderForFfmpeg(string requested)
    {
        if (Path.IsPathRooted(requested) && File.Exists(requested))
            return Path.GetFullPath(requested);
        var name = Path.GetFileName(requested);
        var roots = new List<string>();
        var configured = Environment.GetEnvironmentVariable("VIDEOENHANCER_ANIME4K_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) roots.Add(configured);
        var ffmpegDirectory = Path.GetDirectoryName(FfmpegExe) ?? "";
        roots.Add(Path.Combine(ffmpegDirectory, "libplacebo"));
        var parent = Directory.GetParent(ffmpegDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent)) roots.Add(Path.Combine(parent, "libplacebo"));
        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return "";
    }

    private static string EscapeFfmpegFilterPath(string path) =>
        Path.GetFullPath(path).Replace("\\", "/", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    private static string BuildDirectCustomFilter(PreparedSegment segment)
    {
        if (segment.Backend == "ffmpeg")
            return $"scale={segment.OutputWidth}:{segment.OutputHeight}:flags={segment.Model}";
        var shader = ResolveAnime4kShaderForFfmpeg(segment.Model);
        if (shader.Length == 0)
            throw new FileNotFoundException("找不到 Anime4K 着色器：" + segment.Model);
        return $"libplacebo=w={segment.OutputWidth}:h={segment.OutputHeight}:"
               + $"custom_shader_path='{EscapeFfmpegFilterPath(shader)}'";
    }

    /// <summary>
    /// 把 PTH 源模型解析为当前 GPU、TensorRT 版本和输入尺寸对应的 Engine。
    /// 选择 Engine 时验证当前设备和输入尺寸；选择 PTH 时构建本机缓存。
    /// </summary>
    private static string EnsureTensorRtEngine(
        string modelPath, int inputWidth, int inputHeight, int tileSize = 0, int outputScale = 0,
        string requestedPrecision = "auto")
    {
        var sourcePath = modelPath;
        if (IsTensorRTEngineFile(modelPath))
        {
            DownloadCancellation.Check();
            return File.Exists(modelPath) && ValidateTensorRTEngine(modelPath, printSuccess: true,
                inputWidth: inputWidth, inputHeight: inputHeight, tileSize: tileSize)
                ? Path.GetFullPath(modelPath) : "";
        }

        if (!IsPthModelFile(sourcePath) || !File.Exists(sourcePath))
        {
            Console.Error.WriteLine("[错误] TensorRT 自动构建需要有效的 PTH 源模型：" + sourcePath);
            return "";
        }
        if (!File.Exists(TensorRTConverterScript))
        {
            Console.Error.WriteLine("[错误] 缺少 TensorRT 自动构建脚本：" + TensorRTConverterScript);
            return "";
        }
        if (!TryGetTensorRtRuntime(out var runtime, out var runtimeError))
        {
            Console.Error.WriteLine("[错误] 无法读取 TensorRT 运行环境：" + runtimeError);
            return "";
        }

        Directory.CreateDirectory(TensorRTCacheDir);
        var enginePrecision = TensorRtPrecisionForModel(sourcePath, requestedPrecision);
        var cachePath = BuildTensorRtCachePath(sourcePath, runtime!, inputWidth, inputHeight, tileSize, outputScale,
            enginePrecision);
        var mutexName = "Local\\VideoEnhancer_TRT_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(cachePath))).Substring(0, 24);
        using var buildMutex = new Mutex(false, mutexName);
        var hasMutex = false;
        try
        {
            Console.WriteLine("[TensorRT] 缓存键：" + Path.GetFileName(cachePath));
            while (!hasMutex)
            {
                DownloadCancellation.Check();
                try { hasMutex = buildMutex.WaitOne(250); }
                catch (AbandonedMutexException) { hasMutex = true; }
            }

            if (File.Exists(cachePath))
            {
                Console.WriteLine("[TensorRT] 命中本机尺寸缓存，正在验证…");
                if (ValidateTensorRTEngine(cachePath, printSuccess: true, inputWidth: inputWidth, inputHeight: inputHeight, tileSize: tileSize))
                {
                    EmitTensorRtProgress("超分 Engine", 100, "复用已验证缓存");
                    return cachePath;
                }
                Console.Error.WriteLine("[TensorRT] 缓存已失效，将自动重新构建。");
                try { File.Delete(cachePath); } catch { }
            }

            Console.WriteLine("[TensorRT] 未命中可用缓存，开始自动构建 Engine；首次使用可能需要数分钟。");
            Console.WriteLine("[TensorRT] GPU=" + runtime!.GpuName + "，TensorRT=" + runtime.TensorRtVersion +
                "，Torch-TensorRT=" + runtime.TorchTensorRtVersion +
                "，输入=" + inputWidth + "x" + inputHeight + "，输出倍率=" +
                (outputScale > 0 ? outputScale.ToString(CultureInfo.InvariantCulture) : "native") +
                "，分块=" + tileSize);
            EmitTensorRtProgress("超分 Engine", 0, "准备构建");
            var builtPath = RunTensorRtConverter(
                sourcePath, inputWidth, inputHeight, outputScale, tileSize, enginePrecision);
            if (builtPath.Length == 0) return "";

            var partialPath = Path.Combine(TensorRTCacheDir,
                Path.GetFileNameWithoutExtension(cachePath) + ".building-" + Guid.NewGuid().ToString("N") + ".engine");
            try
            {
                File.Copy(builtPath, partialPath, overwrite: true);
            if (!ValidateTensorRTEngine(partialPath, printSuccess: false, inputWidth: inputWidth, inputHeight: inputHeight, tileSize: tileSize))
                {
                    Console.Error.WriteLine("[错误] 自动构建完成，但 Engine 无法在当前 GPU 上反序列化，未写入缓存。");
                    return "";
                }
                File.Move(partialPath, cachePath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
                var buildDir = Path.GetDirectoryName(builtPath);
                if (buildDir is not null && IsPathUnder(buildDir, TensorRTCacheDir)
                    && Path.GetFileName(buildDir).StartsWith(".build-", StringComparison.Ordinal))
                {
                    try { Directory.Delete(buildDir, recursive: true); } catch { }
                }
            }
            Console.WriteLine("[TensorRT] Engine 已写入本机缓存：" + cachePath);
            EmitTensorRtProgress("超分 Engine", 100, "构建完成");
            return cachePath;
        }
        finally
        {
            if (hasMutex) buildMutex.ReleaseMutex();
        }
    }

    /// <summary>查询便携 Python 中的 GPU 名称和 NVIDIA TensorRT 版本。</summary>
    private static bool TryGetTensorRtRuntime(out TensorRtRuntime? runtime, out string error)
    {
        runtime = null;
        const string script = "import torch, tensorrt as trt; " +
            "assert torch.cuda.is_available(), 'CUDA is unavailable'; " +
            "import torch_tensorrt; " +
            "print('TRT_ENV|' + torch.cuda.get_device_name(0).replace('|','/') + '|' + str(trt.__version__) + '|' + str(torch_tensorrt.__version__))";
        var result = RunProcessCapture(PythonExe, new[] { "-c", script }, 60);
        var line = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(value => value.StartsWith("TRT_ENV|", StringComparison.Ordinal));
        if (!result.Ok || line is null)
        {
            error = string.IsNullOrWhiteSpace(result.Error) ? "便携 Python 未返回 GPU/TensorRT 信息" : result.Error.Trim();
            return false;
        }
        var parts = line.Split('|');
        if (parts.Length < 4 || string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2]) || string.IsNullOrWhiteSpace(parts[3]))
        {
            error = "GPU/TensorRT 信息格式无效：" + line;
            return false;
        }
        runtime = new TensorRtRuntime(parts[1].Trim(), parts[2].Trim(), parts[3].Trim());
        error = "";
        return true;
    }

    private const string TensorRtCacheSchema = "3";
    private const int TensorRtOptimizationLevel = 3;
    private const int TensorRtTilePad = 10;

    /// <summary>GRL 的相对位置编码在 FP16 转换时混用 Float/Half，必须构建 FP32 Engine。</summary>
    private static string TensorRtPrecisionForModel(string sourcePath, string requestedPrecision = "auto") =>
        requestedPrecision == "float32" || Regex.IsMatch(ModelBaseName(sourcePath), @"GRL", RegexOptions.IgnoreCase)
            ? "fp32"
            : "fp16";

    private static string BuildTensorRtCachePath(
        string sourcePath, TensorRtRuntime runtime, int width, int height, int tileSize, int outputScale,
        string precision)
    {
        using var stream = File.OpenRead(sourcePath);
        var sourceHash = Convert.ToHexString(SHA256.HashData(stream)).Substring(0, 12).ToLowerInvariant();
        var configuration = string.Join("|", new[]
        {
            runtime.GpuName, runtime.TensorRtVersion, runtime.TorchTensorRtVersion,
            precision, TensorRtOptimizationLevel.ToString(CultureInfo.InvariantCulture),
            TensorRtCacheSchema, width.ToString(CultureInfo.InvariantCulture),
            height.ToString(CultureInfo.InvariantCulture), tileSize.ToString(CultureInfo.InvariantCulture),
            TensorRtTilePad.ToString(CultureInfo.InvariantCulture), outputScale.ToString(CultureInfo.InvariantCulture),
            sourceHash,
        });
        var configurationHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(configuration))).Substring(0, 20).ToLowerInvariant();
        // 完整配置进入指纹，不再把 GPU/运行时版本全部展开到文件名，避免 Windows MAX_PATH。
        var fileName = SafeCacheComponent(ModelBaseName(sourcePath), 48) +
            "__input-" + width + "x" + height +
            "__scale-" + outputScale +
            "__tile-" + tileSize +
            "__cfg-" + configurationHash +
            "__src-" + sourceHash + ".engine";
        return Path.Combine(TensorRTCacheDir, fileName);
    }

    private static string SafeCacheComponent(string value, int maxLength)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsWhiteSpace(ch) || invalid.Contains(ch) ? '-' : ch);
        }
        var result = Regex.Replace(builder.ToString(), "-+", "-").Trim('-', '.');
        if (result.Length == 0) result = "unknown";
        return result.Length <= maxLength ? result : result[..maxLength];
    }

    /// <summary>输出给 3FUI 队列解析的 TensorRT 构建进度事件。</summary>
    private static void EmitTensorRtProgress(string phase, int percent, string detail)
    {
        var safeDetail = (detail ?? "").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
        Console.WriteLine("VIDEOENHANCER_TRT_PROGRESS|" + phase + "|" +
            Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture) + "|" + safeDetail);
    }

    /// <summary>调用开发包自带转换器，并实时转发构建日志与停止请求。</summary>
    private static string RunTensorRtConverter(
        string sourcePath, int inputWidth, int inputHeight, int outputScale, int tileSize,
        string precision)
    {
        var buildDir = Path.Combine(TensorRTCacheDir, ".build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(buildDir);
        var start = new ProcessStartInfo
        {
            FileName = PythonExe,
            WorkingDirectory = Path.GetDirectoryName(TensorRTConverterScript)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        PortablePaths.ConfigureChildProcess(start);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.ArgumentList.Add(TensorRTConverterScript);
        start.ArgumentList.Add(sourcePath);
        start.ArgumentList.Add("--output-dir");
        start.ArgumentList.Add(buildDir);
        start.ArgumentList.Add("--width");
        start.ArgumentList.Add(inputWidth.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--height");
        start.ArgumentList.Add(inputHeight.ToString(CultureInfo.InvariantCulture));
        if (outputScale > 0)
        {
            start.ArgumentList.Add("--output-scale");
            start.ArgumentList.Add(outputScale.ToString(CultureInfo.InvariantCulture));
        }
        start.ArgumentList.Add("--tile-size");
        start.ArgumentList.Add(tileSize.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--tile-pad");
        start.ArgumentList.Add(TensorRtTilePad.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--precision");
        start.ArgumentList.Add(precision);
        start.ArgumentList.Add("--optimization-level");
        start.ArgumentList.Add(TensorRtOptimizationLevel.ToString(CultureInfo.InvariantCulture));

        using var process = new Process { StartInfo = start };
        var job = CreateKillOnCloseJob();
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            // 保留机器可读进度协议的行首，3FUI 才能把转换阶段显示到任务进度。
            if (e.Data.StartsWith("VIDEOENHANCER_TRT_PROGRESS|", StringComparison.Ordinal))
                Console.WriteLine(e.Data);
            else
                Console.WriteLine("[TensorRT 构建] " + e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Console.Error.WriteLine("[TensorRT 构建] " + e.Data);
        };
        try
        {
            if (!process.Start())
            {
                Console.Error.WriteLine("[错误] 无法启动 TensorRT 自动构建进程。");
                EmitTensorRtProgress("超分 Engine", 0, "启动转换器失败");
                return "";
            }
            if (job != IntPtr.Zero) AssignProcessToJobObject(job, process.Handle);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            while (!process.WaitForExit(250))
            {
                if (!DownloadCancellation.Token.IsCancellationRequested) continue;
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit();
                Console.WriteLine("[TensorRT] 自动构建已取消。");
                EmitTensorRtProgress("超分 Engine", 0, "构建已取消");
                DownloadCancellation.Check();
            }
            process.WaitForExit();
            DownloadCancellation.Check();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine("[错误] TensorRT 自动构建失败，退出码：" + process.ExitCode);
                EmitTensorRtProgress("超分 Engine", 0, "构建失败");
                return "";
            }
            var engine = Directory.EnumerateFiles(buildDir, "*.engine", SearchOption.AllDirectories)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path)).FirstOrDefault();
            if (engine is null)
            {
                Console.Error.WriteLine("[错误] 转换器已退出，但没有生成 .engine 文件：" + buildDir);
                EmitTensorRtProgress("超分 Engine", 0, "未生成 Engine");
                return "";
            }
            return engine;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[错误] TensorRT 自动构建异常：" + ex.Message);
            EmitTensorRtProgress("超分 Engine", 0, "构建异常");
            return "";
        }
        finally
        {
            if (job != IntPtr.Zero) CloseHandle(job);
            if (!Directory.EnumerateFiles(buildDir, "*.engine", SearchOption.AllDirectories).Any())
            {
                try { Directory.Delete(buildDir, recursive: true); } catch { }
            }
        }
    }

    private static bool IsPathUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidateTensorRTEngine(string enginePath, bool printSuccess, int inputWidth = 0, int inputHeight = 0, int tileSize = 0)
    {
        if (!File.Exists(TensorRTValidatorScript))
        {
            Console.Error.WriteLine("[错误] 缺少 TensorRT Engine 验证脚本：" + TensorRTValidatorScript);
            return false;
        }
        var validatorArgs = new List<string> { TensorRTValidatorScript, "--engine", enginePath };
        if (inputWidth > 0 && inputHeight > 0)
        {
            validatorArgs.Add("--width");
            validatorArgs.Add((tileSize > 0 ? Math.Min(inputWidth, tileSize + TensorRtTilePad * 2) : inputWidth).ToString(CultureInfo.InvariantCulture));
            validatorArgs.Add("--height");
            validatorArgs.Add((tileSize > 0 ? Math.Min(inputHeight, tileSize + TensorRtTilePad * 2) : inputHeight).ToString(CultureInfo.InvariantCulture));
        }
        var result = RunProcessCapture(PythonExe, validatorArgs.ToArray(), 120);
        var lines = (result.Output + "\n" + result.Error)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("ENGINE_VALID|", StringComparison.Ordinal))
            {
                if (printSuccess) Console.WriteLine("[TensorRT] 当前 GPU 已成功反序列化：" + Path.GetFileName(enginePath));
            }
            else if (line.StartsWith("ENGINE_INVALID|", StringComparison.Ordinal))
            {
                var parts = line.Split('|');
                var detail = parts.Length > 2 ? parts[2] : line;
                Console.Error.WriteLine("[TensorRT 不兼容] " + enginePath);
                Console.Error.WriteLine("[错误] " + detail);
                Console.Error.WriteLine("[处理建议] 该 engine 需要在当前 GPU 上重新编译。");
            }
        }
        if (!result.Ok && !lines.Any(line => line.StartsWith("ENGINE_INVALID|", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("[TensorRT 不兼容] " + enginePath);
            Console.Error.WriteLine("[处理建议] 该 engine 需要在当前 GPU 上重新编译。" +
                (string.IsNullOrWhiteSpace(result.Error) ? "" : " 原因：" + result.Error.Trim()));
        }
        return result.Ok;
    }

    private static int ValidateAllTensorRTEngines()
    {
        var engines = DiscoverTensorRTEngineModels();
        if (engines.Count == 0)
        {
            Console.WriteLine("[TensorRT] models 中没有找到 .engine 文件。");
            return 0;
        }
        var failures = 0;
        foreach (var engine in engines)
        {
            if (!ValidateTensorRTEngine(engine, printSuccess: true)) failures++;
        }
        Console.WriteLine($"[TensorRT] 验证完成：{engines.Count - failures} 个可加载，{failures} 个需要重新编译。");
        return failures == 0 ? 0 : 3;
    }

    private static int ListBackendsWithEngineValidation()
    {
        var result = RunProcessCapture(PythonExe,
            new[] { BackendScript, "--list_backends", "--engine_dir", ModelsDir }, 600);
        if (!string.IsNullOrWhiteSpace(result.Output)) Console.Write(result.Output);
        if (!string.IsNullOrWhiteSpace(result.Error)) Console.Error.Write(result.Error);
        return result.Ok ? 0 : 3;
    }

    private static bool RunCheck(bool verbose, string? backend = null)
    {
        if (string.Equals(backend, "rtxvsr", StringComparison.OrdinalIgnoreCase))
            return RunRtxCheck(verbose, requireVsr: true, requireHdr: false);

        var ok = true;

        Console.WriteLine("[环境检查] videoenhancer v" + ToolVersion);
        Console.WriteLine("[环境检查] 根目录   : " + CoreRoot);

        var ffmpegOk = File.Exists(FfmpegExe);
        Report(ffmpegOk, "3FUI FFmpeg", FfmpegExe);
        ok &= ffmpegOk;

        var pythonOk = File.Exists(PythonExe);
        Report(pythonOk, "python", PythonExe);
        ok &= pythonOk;

        var backendOk = File.Exists(BackendScript);
        Report(backendOk, "后端脚本", BackendScript);
        ok &= backendOk;

        var sitePkgOk = Directory.Exists(PythonSitePackages);
        Report(sitePkgOk, "python 库", PythonSitePackages);
        ok &= sitePkgOk;

        var models = DiscoverModelsForBackend(backend);
        var modelDescription = string.IsNullOrWhiteSpace(backend)
            ? "未找到支持的模型（NCNN .param/.bin、CUDA/TensorRT .pth/.engine、ONNX 或 FlashVSR）"
            : "未找到 " + backend + " 后端可用模型";
        Report(models.Count > 0, "模型库", ModelsDir,
            models.Count > 0 ? models.Count + " 个可用模型（" + (backend ?? "自动") + "）" : modelDescription);
        ok &= models.Count > 0;

        var interpModels = DiscoverInterpModels("ncnn");
        Report(true, "补帧模型库", FrameInterpolationDir,
            interpModels.Count > 0 ? interpModels.Count + " 个可用补帧模型" : "未找到含 .param/.bin 的补帧模型（可忽略，仅超分可用）");

        if (verbose)
        {
            var osOk = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
                       && RuntimeInformation.OSArchitecture == Architecture.X64;
            Report(osOk, "Windows 运行环境",
                RuntimeInformation.OSDescription + " / " + RuntimeInformation.OSArchitecture,
                osOk ? "支持 Windows 10 1809 或更高版本（x64）" : "需要 Windows 10 1809 或更高版本（x64）");
            ok &= osOk;

            var vcRuntimeOk = NativeLibrary.TryLoad("msvcp140.dll", out var vcRuntimeHandle);
            if (vcRuntimeOk) NativeLibrary.Free(vcRuntimeHandle);
            Report(vcRuntimeOk, "VC++ 运行库", "MSVCP140.dll",
                vcRuntimeOk
                    ? "已可加载"
                    : "请安装 Microsoft Visual C++ 2015-2022 x64 运行库：https://aka.ms/vc14/vc_redist.x64.exe");
            ok &= vcRuntimeOk;

            var ffmpegVersion = RunProcessCapture(FfmpegExe, new[] { "-version" }, 30);
            var ffmpegFirst = ffmpegVersion.Output.Split('\n').FirstOrDefault(l => l.Contains("ffmpeg version"));
            Report(ffmpegVersion.Ok, "ffmpeg 可执行", FfmpegExe, ffmpegFirst?.Trim() ?? ffmpegVersion.Error.Trim());
            ok &= ffmpegVersion.Ok;

            var pyImport = RunProcessCapture(
                PythonExe, new[] { "-c", "import numpy, cv2; print('numpy', numpy.__version__); print('cv2', cv2.__version__)" }, 60);
            var pyDetail = pyImport.Ok ? pyImport.Output.Trim().Replace('\n', ' ') : pyImport.Error.Trim();
            Report(pyImport.Ok, "python 库导入", "numpy / opencv", pyDetail);
            ok &= pyImport.Ok;

            var backendVersion = RunProcessCapture(PythonExe, new[] { BackendScript, "--version" }, 60);
            Report(backendVersion.Ok, "后端脚本运行", BackendScript,
                backendVersion.Ok ? "rve-backend v" + backendVersion.Output.Trim() : backendVersion.Error.Trim());
            ok &= backendVersion.Ok;

            var backendProbe = RunBackendDependencyProbe(backend);
            if (backendProbe is not null)
            {
                Report(backendProbe.Value.Ok, "推理后端依赖", backend ?? "自动",
                    backendProbe.Value.Ok ? backendProbe.Value.Output.Trim().Replace('\n', ' ') : backendProbe.Value.Error.Trim());
                ok &= backendProbe.Value.Ok;
            }

            // 启动环境检查只验证基础组件，避免逐个加载大尺寸 TensorRT Engine。
            // Engine 适用性由类型化检查服务、后端列表和实际推理路径按需检查。
        }

        Console.WriteLine("[环境检查] " + (ok ? "全部通过。" : "存在缺失项，请检查上方 [缺失] 标记。"));
        return ok;
    }

    private static bool RunRtxCheck(bool verbose, bool requireVsr, bool requireHdr)
    {
        Console.WriteLine("[环境检查] videoenhancer v" + ToolVersion + " / RTX Video");
        var exists = File.Exists(RtxVideoBackendExe);
        Report(exists, "RTX Video sidecar", RtxVideoBackendExe,
            exists ? "已安装" : "缺少 vsr_backend.exe 及其运行库");
        if (!exists) return false;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = RtxVideoBackendClient.StartAsync(RtxVideoBackendExe, cancellation.Token).GetAwaiter().GetResult();
            var capabilities = client.GetCapabilitiesAsync(cancellation.Token).GetAwaiter().GetResult();
            var ok = capabilities.D3d11Available && capabilities.RtxSdkFound
                && (!requireVsr || capabilities.VsrAvailable)
                && (!requireHdr || capabilities.TruehdrAvailable);
            Report(capabilities.D3d11Available, "Direct3D 11", "Windows D3D11");
            Report(capabilities.RtxSdkFound, "NVIDIA RTX Video SDK", Path.GetDirectoryName(RtxVideoBackendExe)!);
            if (requireVsr || verbose) Report(capabilities.VsrAvailable, "RTX VSR", "NVIDIA RTX Video Super Resolution");
            if (requireHdr || verbose) Report(capabilities.TruehdrAvailable, "RTX Video HDR", "NVIDIA TrueHDR");
            if (verbose && capabilities.Messages.Length > 0)
                Console.WriteLine("[RTX Video] " + string.Join("；", capabilities.Messages));
            Console.WriteLine("[环境检查] " + (ok ? "全部通过。" : "RTX Video 能力不满足当前任务。"));
            return ok;
        }
        catch (Exception ex)
        {
            Report(false, "RTX Video sidecar 启动", RtxVideoBackendExe, ex.Message);
            return false;
        }
    }

    /// <summary>只导入所选后端的关键模块并检查设备，不加载模型或 TensorRT Engine。</summary>
    private static (bool Ok, string Output, string Error)? RunBackendDependencyProbe(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend)) return null;
        var script = backend.ToLowerInvariant() switch
        {
            "ncnn" =>
                "import ncnn, rife_ncnn_vulkan_python; print('ncnn', getattr(ncnn, '__version__', 'ok'), 'rife-ncnn ok')",
            "cuda" or "flashvsr" or "basicvsrpp" =>
                "import torch, torchvision; assert torch.cuda.is_available(), 'PyTorch 未检测到可用的 NVIDIA CUDA 设备，请更新显卡驱动'; print('torch', torch.__version__, 'cuda', torch.version.cuda, 'gpu', torch.cuda.get_device_name(0))",
            "tensorrt" =>
                "import torch, tensorrt, torch_tensorrt; assert torch.cuda.is_available(), 'TensorRT 未检测到可用的 NVIDIA CUDA 设备，请更新显卡驱动'; print('torch', torch.__version__, 'cuda', torch.version.cuda, 'tensorrt', tensorrt.__version__, 'gpu', torch.cuda.get_device_name(0))",
            "onnx" =>
                "import onnxruntime as ort; providers=ort.get_available_providers(); assert providers, 'ONNX Runtime 没有可用执行提供程序'; print('onnxruntime', ort.__version__, 'providers', ','.join(providers))",
            _ => null,
        };
        return script is null ? null : RunProcessCapture(PythonExe, new[] { "-c", script }, 90);
    }

    /// <summary>按实际推理后端检查模型，避免 TensorRT 机器被 NCNN 文件格式误判。</summary>
    private static List<string> DiscoverModelsForBackend(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return DiscoverModelFolders()
                .Concat(DiscoverUpscalePthModels())
                .Concat(DiscoverTensorRTEngineModels())
                .Concat(DiscoverOnnxModels())
                .Concat(DiscoverFlashVsrModels())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return backend.ToLowerInvariant() switch
        {
            "ncnn" => DiscoverModelFolders(),
            "cuda" => DiscoverUpscalePthModels(),
            "tensorrt" => DiscoverTensorRTSelectableModels(),
            "onnx" => DiscoverOnnxModels(),
            "flashvsr" => DiscoverFlashVsrModels(),
            "basicvsrpp" => DiscoverBasicVsrPlusPlusModels(),
            _ => new List<string>(),
        };
    }

    private static void Report(bool ok, string label, string detail, string? extra = null)
    {
        var mark = ok ? "[通过]" : "[缺失]";
        var line = "  " + mark + " " + label + " : " + detail;
        if (!string.IsNullOrWhiteSpace(extra))
        {
            line += "  (" + extra + ")";
        }
        (ok ? Console.Out : Console.Error).WriteLine(line);
    }

    private static (bool Ok, string Output, string Error) RunProcessCapture(string fileName, string[] args, int timeoutSeconds)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            PortablePaths.ConfigureChildProcess(psi);
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (false, "", "无法启动进程");
            }
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutSeconds * 1000))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 忽略
                }
                p.WaitForExit();
                return (false, stdoutTask.GetAwaiter().GetResult(), "超时（" + timeoutSeconds + " 秒）");
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            return (p.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private static int ListModels(bool json, string backend)
    {
        var isCuda = backend == "cuda";
        var isTensorRT = backend == "tensorrt";
        var isOnnx = backend == "onnx";
        var isFlashVsr = backend == "flashvsr";
        var isBasicVsrPlusPlus = backend == "basicvsrpp";
        var models = isBasicVsrPlusPlus ? DiscoverBasicVsrPlusPlusModels() : isFlashVsr ? DiscoverFlashVsrModels() : isCuda ? DiscoverUpscalePthModels() : isTensorRT ? DiscoverTensorRTSelectableModels() : isOnnx ? DiscoverOnnxModels() : DiscoverModelFolders();
        string DisplayName(string path) => UpscaleModelDisplayName(path, backend);
        if (json)
        {
            // 机器可读：一行 JSON 数组（插件下拉框等调用方直接解析）
            var names = models.Select(DisplayName).ToList();
            Console.WriteLine("[" + string.Join(",", names.Select(n => "\"" + n + "\"")) + "]");
            return 0;
        }
        Console.WriteLine(isBasicVsrPlusPlus ? "可用 BasicVSR++ 时序视频模型："
            : isFlashVsr ? "可用 FlashVSR 时序视频模型："
            : isTensorRT
            ? "可用放大模型（TensorRT，PTH 首次使用自动构建本机 Engine）："
            : isOnnx ? "可用放大模型（ONNX Runtime，递归扫描 models 的 .onnx 文件）："
            : isCuda ? "可用放大模型（CUDA，递归扫描 models 的 .pth/.pt/.pkl/.ckpt/.safetensors 文件，不含补帧目录）："
            : "可用放大模型（NCNN，递归扫描 models 中含 .param/.bin 的文件夹，不含补帧目录）：");
        if (models.Count == 0)
        {
            Console.WriteLine(isTensorRT
                ? "  (未找到任何 PTH 源模型或预制 .engine 文件)"
                : isOnnx ? "  (未找到任何 .onnx 模型文件)"
                : isCuda ? "  (未找到任何 PyTorch/safetensors 模型文件)"
                : "  (未找到任何含 .param/.bin 的模型文件夹)");
            return 0;
        }
        foreach (var m in models)
        {
            var scale = isBasicVsrPlusPlus ? BasicVsrPlusPlusScale(m) : DetectScale(m);
            Console.WriteLine("  " + DisplayName(m) + (scale is null ? "" : "  (" + scale + "x)"));
        }
        return 0;
    }

    private static int ListUserModels(bool json)
    {
        var models = UserModelCatalog.Load(ModelsDir)
            .OrderBy(item => item.Task, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Architecture, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!json)
        {
            foreach (var item in models)
                Console.WriteLine($"{item.Id}  {item.Architecture}  {item.Scale}x  [{string.Join(",", item.Backends)}]");
            return 0;
        }
        WriteUserModelsJson(models);
        return 0;
    }

    private static int UpdateUserModel(BackendRequest request)
    {
        var capability=request.Capabilities??throw new ArgumentException("缺少用户模型能力设置");
        var updated=UserModelCatalog.UpdateCapabilities(ModelsDir,request.ModelId,capability.Architecture,
            capability.Purpose,capability.Scale,capability.InputMultiple,capability.Backends);
        WriteUserModelsJson([updated]);
        return 0;
    }

    private static int DeleteUserModel(string id)
    {
        var deleted=UserModelCatalog.Delete(ModelsDir,id);
        using var writer=new Utf8JsonWriter(Console.OpenStandardOutput());
        writer.WriteStartObject();writer.WriteBoolean("deleted",true);writer.WriteString("id",deleted.Id);
        writer.WriteEndObject();writer.Flush();Console.WriteLine();
        return 0;
    }

    private static void WriteUserModelsJson(IEnumerable<UserModelRecord> models)
    {
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput());
        writer.WriteStartArray();
        foreach (var item in models)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.Id);
            writer.WriteString("displayName", item.DisplayName);
            writer.WriteString("relativePath", item.RelativePath);
            writer.WriteString("task", item.Task);
            writer.WriteString("architecture", item.Architecture);
            writer.WriteString("purpose", item.Purpose);
            writer.WriteString("format", item.Format);
            writer.WriteNumber("scale", item.Scale);
            writer.WriteNumber("inputMultiple", item.InputMultiple);
            writer.WriteNumber("minimumSize", item.MinimumSize);
            writer.WriteBoolean("square", item.Square);
            writer.WriteString("tiling", item.Tiling);
            writer.WriteString("sha256", item.Sha256);
            writer.WriteNumber("size", item.Size);
            writer.WriteString("importedAtUtc", item.ImportedAtUtc);
            writer.WriteStartArray("backends");
            foreach (var backend in item.Backends) writer.WriteStringValue(backend);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.Flush();
        Console.WriteLine();
    }

    private sealed class ModelListCatalogEntry
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Architecture { get; init; } = "";
        public string ArchitectureGroup { get; init; } = "";
        public int[] InferenceScales { get; init; } = [];
        public string Purpose { get; init; } = "";
        public int Scale { get; init; }
        public string Source { get; init; } = "";
        public string[] Backends { get; init; } = [];
    }

    private static int ListModelCatalog(bool json, string backend, bool interpolation)
    {
        var paths = interpolation
            ? DiscoverInterpModels(backend)
            : backend == "basicvsrpp" ? DiscoverBasicVsrPlusPlusModels()
            : backend == "flashvsr" ? DiscoverFlashVsrModels()
            : backend == "cuda" ? DiscoverUpscalePthModels()
            : backend == "tensorrt" ? DiscoverTensorRTSelectableModels()
            : backend == "onnx" ? DiscoverOnnxModels()
            : DiscoverModelFolders();
        var userModels = UserModelCatalog.Load(ModelsDir);
        var entries = new List<ModelListCatalogEntry>();
        foreach (var path in paths)
        {
            var id = interpolation ? InterpModelDisplayName(path) : UpscaleModelDisplayName(path, backend);
            var user = userModels.FirstOrDefault(item =>
                UserModelCatalog.NormalizeRelativePath(item.RelativePath, ModelsDir)
                    .Equals(UserModelCatalog.NormalizeRelativePath(path, ModelsDir), StringComparison.OrdinalIgnoreCase));
            var normalizedPath = UserModelCatalog.NormalizeRelativePath(path, ModelsDir);
            if (user is null && normalizedPath.StartsWith("User/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (user is not null && !string.IsNullOrWhiteSpace(backend)
                && !user.Backends.Contains(backend, StringComparer.OrdinalIgnoreCase))
                continue;
            ModelCapability? builtIn = null;
            if (user is null && ModelCapabilityCatalog.TryGet(path, ModelsDir, out var capability)) builtIn = capability;
            var inspected = !interpolation && builtIn is null ? InspectModelCached(path) : null;
            var ncnnSignature = !interpolation ? NcnnModelSignatures.Get(path) : null;
            var architecture = inspected?.Architecture ?? ncnnSignature?.Architecture ?? user?.Architecture ?? builtIn?.Architecture ?? InferArchitecture(id, interpolation);
            var purpose = user?.Purpose ?? (interpolation ? "Interpolation" : "SR");
            var scale = inspected?.Scale ?? user?.Scale ?? builtIn?.Scale ?? (int.TryParse(DetectScale(path), out var detected) ? detected : 0);
            var backends = user?.Backends ?? builtIn?.Backends ?? inspected?.Backends ?? [backend];
            if (!backends.Contains(backend, StringComparer.OrdinalIgnoreCase)) continue;
            entries.Add(new ModelListCatalogEntry
            {
                Id = id,
                DisplayName = ModelBaseName(path),
                Architecture = architecture,
                ArchitectureGroup = interpolation ? architecture : ModelArchitectureGroups.Get(architecture),
                InferenceScales = builtIn?.InferenceScales ?? (backends.Contains("flashvsr") ? [2, 4] : []),
                Purpose = purpose,
                Scale = scale,
                Source = user is not null ? "user" : builtIn is not null ? "builtin" : "discovered",
                Backends = backends,
            });
        }
        entries = entries.OrderBy(item => item.ArchitectureGroup, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!json)
        {
            foreach (var group in entries.GroupBy(item => item.ArchitectureGroup))
            {
                Console.WriteLine(group.Key + "：");
                foreach (var item in group) Console.WriteLine("  " + item.Id);
            }
            return 0;
        }
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput());
        writer.WriteStartArray();
        foreach (var item in entries)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.Id);
            writer.WriteString("displayName", item.DisplayName);
            writer.WriteString("architecture", item.Architecture);
            writer.WriteString("purpose", item.Purpose);
            writer.WriteNumber("scale", item.Scale);
            writer.WriteString("architectureGroup", item.ArchitectureGroup);
            writer.WriteStartArray("inferenceScales");
            foreach (var value in item.InferenceScales) writer.WriteNumberValue(value);
            writer.WriteEndArray();
            writer.WriteString("source", item.Source);
            writer.WriteStartArray("backends");
            foreach (var value in item.Backends) writer.WriteStringValue(value);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.Flush();
        Console.WriteLine();
        return 0;
    }

    private static string InferArchitecture(string id, bool interpolation)
    {
        var normalized = id.Replace('\\', '/');
        if (normalized.StartsWith("User/", StringComparison.OrdinalIgnoreCase))
        {
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3) return segments[2];
        }
        foreach (var architecture in new[]
        {
            "RealESRGAN", "RealHatGAN", "ESRGAN", "SPANPlus", "SPAN", "SwinIR", "RealCUGAN",
            "AnimeSR", "CRAFT", "DITN", "MoSR", "RIFE", "GMFSS", "GIMM"
        })
            if (normalized.Contains(architecture, StringComparison.OrdinalIgnoreCase)) return architecture;
        if (interpolation)
        {
            var first = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)) return first;
        }
        return "其他模型";
    }

    private static List<string> DiscoverModelFolders()
    {
        if (!Directory.Exists(ModelsDir))
        {
            return new List<string>();
        }
        return Directory.GetDirectories(ModelsDir, "*", SearchOption.AllDirectories)
            .Where(p => !IsInInterpolationDirectory(p))
            .Where(IsNcnnModelFolder)
            .Where(p => !ModelBaseName(p).Equals("EfficientNet-SceneDetect", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>发现 CUDA 放大模型：递归扫描 models，但排除独立的补帧目录。</summary>
    private static List<string> DiscoverUpscalePthModels()
    {
        if (!Directory.Exists(ModelsDir))
        {
            return new List<string>();
        }
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in new[] { "*.pth", "*.pt", "*.pkl", "*.ckpt", "*.safetensors" })
        {
            foreach (var f in Directory.GetFiles(ModelsDir, pattern, SearchOption.AllDirectories)
                         .Where(p => !IsInInterpolationDirectory(p)
                             && !IsInFlashVsrDirectory(p) && !IsInBasicVsrPlusPlusDirectory(p)))
            {
                set.Add(f);
            }
        }
        return set.OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static List<string> DiscoverTensorRTEngineModels()
    {
        if (!Directory.Exists(ModelsDir)) return new List<string>();
        return Directory.GetFiles(ModelsDir, "*.engine", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// TensorRT 任务只展示 PTH 源模型；Engine 始终按当前任务配置自动构建到本机缓存。
    /// </summary>
    private static List<string> DiscoverTensorRTSelectableModels()
    {
        return DiscoverUpscalePthModels()
            .Where(IsTensorRtConvertibleUpscaleSource)
            .OrderBy(path => UpscaleModelDisplayName(path, "tensorrt"), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>排除已实机确认不能进入当前单图直接 Engine 路径的架构。</summary>
    private static bool IsTensorRtConvertibleUpscaleSource(string path)
    {
        if (ModelCapabilityCatalog.TryGet(path, ModelsDir, out var capability))
            return capability.Backends.Contains("tensorrt", StringComparer.OrdinalIgnoreCase);
        var inspection = InspectModelCached(path);
        return inspection?.Backends.Contains("tensorrt", StringComparer.OrdinalIgnoreCase) == true;
    }

    private static List<string> DiscoverOnnxModels()
    {
        if (!Directory.Exists(ModelsDir)) return new List<string>();
        return Directory.GetFiles(ModelsDir, "*.onnx", SearchOption.AllDirectories)
            .Where(p => !IsInInterpolationDirectory(p))
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static List<string> DiscoverFlashVsrModels()
    {
        if (!Directory.Exists(ModelsDir)) return new List<string>();
        return Directory.GetDirectories(ModelsDir, "*", SearchOption.AllDirectories)
            .Prepend(ModelsDir)
            .Where(IsFlashVsrModelDirectory)
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<string> DiscoverBasicVsrPlusPlusModels()
    {
        if (!Directory.Exists(ModelsDir)) return new List<string>();
        var optimizedDirectories = Directory.GetDirectories(ModelsDir, "*", SearchOption.AllDirectories)
            .Where(IsBasicVsrPlusPlusModelDirectory)
            .ToList();
        var officialWeights = Directory.GetFiles(ModelsDir, "*.pth", SearchOption.AllDirectories)
            .Where(IsInBasicVsrPlusPlusDirectory)
            .Where(path => !optimizedDirectories.Any(directory => IsPathUnder(path, directory)));
        return optimizedDirectories.Concat(officialWeights)
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>超分模型显示为相对 models 的路径，避免分类目录中的同名模型冲突。</summary>
    private static string UpscaleModelDisplayName(string path, string backend)
    {
        var removeExtension = File.Exists(path) && backend is ("cuda" or "tensorrt" or "onnx" or "basicvsrpp");
        return RelativeModelDisplayName(path, removeExtension);
    }

    private static string RelativeModelDisplayName(string path, bool removeExtension)
    {
        var relative = Path.GetRelativePath(ModelsDir, path);
        if (removeExtension)
        {
            relative = Path.ChangeExtension(relative, null) ?? relative;
        }
        return relative.Replace('\\', '/');
    }

    private static string ModelBaseName(string path)
    {
        return File.Exists(path)
            ? Path.GetFileNameWithoutExtension(path)
            : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static bool IsInInterpolationDirectory(string path) =>
        IsPathUnder(path, FrameInterpolationDir) || IsPathUnder(path, UserInterpolationDir);

    private static bool IsInBasicVsrPlusPlusDirectory(string path)
    {
        var root = Path.GetFullPath(Path.Combine(ModelsDir, "BasicVSR++"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || HasModelPackageAncestor(path, IsBasicVsrPlusPlusModelDirectory);
    }

    private static bool IsInFlashVsrDirectory(string path)
    {
        var root = Path.GetFullPath(Path.Combine(ModelsDir, "FlashVSR"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || HasModelPackageAncestor(path, IsFlashVsrModelDirectory);
    }

    private static bool HasModelPackageAncestor(string path, Func<string, bool> predicate)
    {
        var directory = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : Path.GetFullPath(path);
        var modelsRoot = Path.GetFullPath(ModelsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(directory)
               && directory.StartsWith(modelsRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (predicate(directory)) return true;
            if (directory.Equals(modelsRoot, StringComparison.OrdinalIgnoreCase)) break;
            directory = Path.GetDirectoryName(directory);
        }
        return false;
    }

    private static string? FindNcnnModelFolder(string modelName)
    {
        if (!Directory.Exists(ModelsDir)) return null;
        // 场景检测模型不应出现在超分下拉框，但内部仍需要能够定位它。
        return Directory.GetDirectories(ModelsDir, "*", SearchOption.AllDirectories)
            .Where(IsNcnnModelFolder)
            .FirstOrDefault(p =>
            ModelBaseName(p).Equals(modelName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNcnnModelFolder(string dir)
    {
        return Directory.EnumerateFiles(dir, "*.param", SearchOption.TopDirectoryOnly).Any()
            && Directory.EnumerateFiles(dir, "*.bin", SearchOption.TopDirectoryOnly).Any();
    }

    private static int Fail(string message, int exitCode = 2)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("[错误] " + message);
        Console.Error.WriteLine("[提示] 使用 videoenhancer.3fui.dll -h 查看详细帮助。");
        return exitCode;
    }


}
