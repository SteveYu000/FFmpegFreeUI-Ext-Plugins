using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoEnhancer;

/// <summary>所有压缩包均由随插件分发的 7za 解压；写盘前检查目录、加密项和链接。</summary>
internal static class NativeSevenZipExtractor
{
    private sealed record ArchiveListing(string Type, List<Dictionary<string, string>> Entries);
    private static readonly HashSet<string> StreamFormats = new(StringComparer.OrdinalIgnoreCase)
        { "gzip", "xz", "zstd", "bzip2", "lzma", "lzma86" };
    private static readonly Regex PercentPattern = new(@"(?:^|\s)(\d{1,3})%", RegexOptions.Compiled);
    internal static string Executable => Path.Combine(PortablePaths.CoreRoot, "bin", "7zip", "7za.exe");

    internal static bool EnsureAvailable()
    {
        // 单 DLL 携带固定版工具及许可声明；对应源码由同版本独立源码包提供。
        var assembly = typeof(NativeSevenZipExtractor).Assembly;
        foreach (var (name, relative) in new[]
        {
            ("7za.exe", "bin/7zip/7za.exe"), ("License.txt", "licenses/7zip/License.txt"),
            ("SOURCE.txt", "licenses/7zip/SOURCE.txt")
        })
        {
            DownloadCancellation.Check();
            var target = Path.Combine(PortablePaths.CoreRoot, relative);
            if (File.Exists(target)) continue;
            using var input = assembly.GetManifestResourceStream("VideoEnhancer.Embedded.SevenZip." + name);
            if (input is null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".new-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = File.Create(temporary)) input.CopyTo(output);
                try { File.Move(temporary, target); }
                catch (IOException) when (File.Exists(target)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return File.Exists(Executable);
    }

    internal static void Extract(string archivePath, string outputDirectory, Action<int>? progress = null)
    {
        DownloadCancellation.Check();
        var archive = Path.GetFullPath(archivePath);
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (!File.Exists(archive)) throw new FileNotFoundException("压缩文件不存在", archive);
        if (Path.GetExtension(archive).Equals(".rar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("7za 不支持 RAR，请先转换为 ZIP 或 7z");
        if (!EnsureAvailable()) throw new FileNotFoundException("缺少随插件分发的 7za 解压器，请重新安装完整 ZIP", Executable);
        var listing = ListArchive(archive)!;
        ValidateEntries(listing, archive, output);
        EnsureSafeDirectoryTree(output);
        progress?.Invoke(0);
        if (StreamFormats.Contains(listing.Type))
            ExtractStream(archive, output, listing, progress);
        else
            ExtractFiles(archive, output, listing, percent => progress?.Invoke(Math.Min(99, percent)));
        DownloadCancellation.Check();
        progress?.Invoke(100);
    }

    private static void ExtractStream(string archive, string output, ArchiveListing listing, Action<int>? progress)
    {
        // 单流格式先解码到独立临时文件；若内容为 TAR，再用同一 7za 解包。
        // 不能直接把 .tar.gz/.tar.xz 的第一层文件作为安装结果。
        if (listing.Entries.Count != 1) throw new InvalidDataException("压缩流没有唯一的输出文件");
        var work = PortablePaths.CreateWorkDirectory("archive-stream");
        var payload = Path.Combine(work, "payload");
        try
        {
            EnsureSafeDirectoryTree(work);
            using (var destination = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                string errors = "";
                int exit = Run(["x", "-so", "-bso0", "-bsp2", "-bb0", "--", archive], process =>
                {
                    var reading = Task.Run(() =>
                    {
                        try { return ReadProgress(process.StandardError, percent => progress?.Invoke(Math.Min(49, percent / 2))); }
                        catch { Kill(process); throw; }
                    });
                    process.StandardOutput.BaseStream.CopyTo(destination);
                    errors = reading.GetAwaiter().GetResult();
                }, out _);
                if (exit != 0) throw ToolError("解压压缩流", exit, errors);
            }
            var inner = ListArchive(payload, tarOnly: true, allowNonArchive: true);
            if (inner is not null)
            {
                ValidateEntries(inner, payload, output);
                ExtractFiles(payload, output, inner,
                    percent => progress?.Invoke(Math.Min(99, 50 + percent / 2)), tarOnly: true);
            }
            else
            {
                var name = EntryName(listing.Entries[0], archive);
                if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(archive).Equals(".tgz", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(archive).Equals(".txz", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("压缩流声明为 TAR，但内容不是可读取的 TAR 文件");
                var target = ResolveEntry(output, name, isDirectory: false)!;
                EnsureSafeDirectoryTree(Path.GetDirectoryName(target)!);
                RejectExistingReparsePoint(target);
                if (Directory.Exists(target)) throw new IOException("解压文件被同名目录占用：" + target);
                DownloadCancellation.Check();
                File.Move(payload, target, overwrite: true);
            }
        }
        finally
        {
            // 此目录只容纳由 -so 创建的单个普通文件，不递归删除归档提供的路径。
            if (File.Exists(payload)) File.Delete(payload);
            if (Directory.Exists(work)) Directory.Delete(work);
        }
    }

    private static void ExtractFiles(string archive, string output, ArchiveListing listing, Action<int>? progress, bool tarOnly = false)
    {
        var checkedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in listing.Entries)
        {
            DownloadCancellation.Check();
            var directory = IsDirectory(entry);
            var target = ResolveEntry(output, EntryName(entry, archive), directory);
            if (target is null) continue;
            var parent = directory ? target : Path.GetDirectoryName(target)!;
            EnsureSafeDirectoryTree(parent, checkedDirectories);
            RejectExistingReparsePoint(target);
            if (!directory && Directory.Exists(target)) throw new IOException("解压文件被同名目录占用：" + target);
        }
        var arguments = new List<string> { "x", "-o" + output, "-y", "-aoa", "-mmt=on", "-bsp1", "-bso1", "-bse2", "-bb0", "-sns-" };
        if (tarOnly) arguments.Add("-ttar");
        arguments.AddRange(["--", archive]);
        string diagnostics = "";
        int exit = Run(arguments, process => diagnostics = ReadProgress(process.StandardOutput, progress), out var errors);
        if (exit != 0) throw ToolError("解压", exit, errors + "\n" + diagnostics);
    }

    private static ArchiveListing? ListArchive(string archive, bool tarOnly = false, bool allowNonArchive = false)
    {
        var arguments = new List<string> { "l", "-slt", "-bd" };
        if (tarOnly) arguments.Add("-ttar");
        arguments.AddRange(["--", archive]);
        string text = "";
        int exit = Run(arguments, process => text = process.StandardOutput.ReadToEnd(), out var errors);
        if (exit != 0)
        {
            // 仅压缩流的 TAR 探测允许“不是此格式”；损坏、权限或其他错误仍须报告。
            if (allowNonArchive && exit == 2 && errors.Contains("Cannot open the file as [tar] archive", StringComparison.Ordinal)) return null;
            throw ToolError("读取目录", exit, errors + "\n" + text);
        }
        using var reader = new StringReader(text);
        string? type = null, line;
        bool entriesStarted = false;
        var entries = new List<Dictionary<string, string>>();
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        while ((line = reader.ReadLine()) is not null)
        {
            if (!entriesStarted)
            {
                if (line == "----------") entriesStarted = true;
                else if (line.StartsWith("Type = ", StringComparison.Ordinal))
                {
                    if (type is not null) throw new InvalidDataException("不支持多层目录格式");
                    type = line[7..];
                }
                continue;
            }
            if (line.Length == 0)
            {
                if (properties.Count > 0) { entries.Add(properties); properties = new(StringComparer.Ordinal); }
                continue;
            }
            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator <= 0 || !properties.TryAdd(line[..separator], line[(separator + 3)..]))
                throw new InvalidDataException("7za 返回了无法校验的目录条目");
        }
        if (properties.Count > 0) entries.Add(properties);
        if (!entriesStarted || string.IsNullOrWhiteSpace(type)) throw new InvalidDataException("7za 没有返回可校验的压缩目录");
        return new(type, entries);
    }

    private static void ValidateEntries(ArchiveListing listing, string archive, string output)
    {
        bool stream = StreamFormats.Contains(listing.Type);
        var destinations = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!stream && listing.Type is not ("7z" or "zip" or "tar" or "Cab" or "cab"))
            throw new InvalidDataException("7za 不支持此安装压缩格式：" + listing.Type);
        foreach (var entry in listing.Entries)
        {
            DownloadCancellation.Check();
            if (!stream && (!entry.TryGetValue("Path", out var path) || string.IsNullOrWhiteSpace(path)))
                throw new InvalidDataException("压缩项没有有效的文件名");
            var name = EntryName(entry, archive);
            if (entry.GetValueOrDefault("Encrypted") == "+") throw new InvalidDataException("不支持加密压缩项：" + name);
            if (entry.GetValueOrDefault("Anti") == "+" || !string.IsNullOrEmpty(entry.GetValueOrDefault("Symbolic Link")) ||
                !string.IsNullOrEmpty(entry.GetValueOrDefault("Hard Link")) || HasLinkOrSpecialAttributes(entry))
                throw new InvalidDataException("出于安全原因不解压链接或特殊文件：" + name);
            if (!stream && (!entry.TryGetValue("Size", out var size) ||
                !long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                throw new InvalidDataException("压缩项没有有效的大小：" + name);
            var target = ResolveEntry(output, name, IsDirectory(entry));
            if (target is not null && !destinations.TryAdd(target, IsDirectory(entry)))
                throw new InvalidDataException("压缩包包含重复或大小写冲突路径：" + name);
        }
        foreach (var target in destinations.Keys)
        {
            var parent = Path.GetDirectoryName(target);
            while (parent is not null && !parent.Equals(output, StringComparison.OrdinalIgnoreCase))
            {
                if (destinations.TryGetValue(parent, out var directory) && !directory)
                    throw new InvalidDataException("压缩包包含文件与目录路径冲突：" + parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
    }

    private static bool HasLinkOrSpecialAttributes(Dictionary<string, string> entry)
    {
        var mode = entry.GetValueOrDefault("Mode", "");
        if (mode.Length > 0 && mode[0] is not ('-' or 'd')) return true;
        var attributes = entry.GetValueOrDefault("Attributes", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // 7z 的 L 表示重解析点；ZIP 的 Unix 权限字符串会直接标出链接和设备类型。
        return attributes.Any(value => value.Contains('L') ||
            (value.Length == 10 && value[0] is 'l' or 'b' or 'c' or 'p' or 's'));
    }

    private static bool IsDirectory(Dictionary<string, string> entry) => entry.GetValueOrDefault("Folder") == "+" ||
        entry.GetValueOrDefault("Attributes", "").StartsWith('D');

    private static string EntryName(Dictionary<string, string> entry, string archive) =>
        entry.TryGetValue("Path", out var name) && name.Length > 0 ? name : Path.GetFileNameWithoutExtension(archive);

    private static string? ResolveEntry(string output, string name, bool isDirectory)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new InvalidDataException("压缩项不是安全的相对路径：" + name);
        // TAR 的 ./ 前缀与根目录标记不越界，保留对标准打包工具的支持。
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        if (isDirectory && (normalized is "" or ".")) return null;
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(IsUnsafeSegment)) throw new InvalidDataException("压缩项路径不安全：" + name);
        var target = Path.GetFullPath(Path.Combine(output, Path.Combine(parts)));
        var prefix = Path.EndsInDirectorySeparator(output) ? output : output + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("压缩项越出目标目录：" + name);
        return target;
    }

    private static bool IsUnsafeSegment(string segment)
    {
        if (segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return true;
        var device = segment.Split('.')[0].ToUpperInvariant();
        return device is "CON" or "PRN" or "AUX" or "NUL" ||
            (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(device[3]));
    }

    private static void EnsureSafeDirectoryTree(string path, HashSet<string>? checkedDirectories = null)
    {
        var uncheckedParents = new Stack<string>();
        string? parent = path;
        while (parent is not null && checkedDirectories?.Contains(parent) != true)
        {
            uncheckedParents.Push(parent);
            parent = Path.GetDirectoryName(parent);
        }
        while (uncheckedParents.TryPop(out var directory))
        {
            if (File.Exists(directory)) throw new IOException("解压目录被同名文件占用：" + directory);
            RejectExistingReparsePoint(directory);
            Directory.CreateDirectory(directory);
            RejectExistingReparsePoint(directory);
            checkedDirectories?.Add(directory);
        }
    }

    private static void RejectExistingReparsePoint(string path)
    {
        // File.GetAttributes 也能识别目标已经失效的目录链接。
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("解压目标包含重解析点，已拒绝写入：" + path);
    }

    private static int Run(IEnumerable<string> arguments, Action<System.Diagnostics.Process> readOutput, out string errors)
    {
        DownloadCancellation.Check();
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        PortablePaths.ConfigureChildProcess(start);
        // 目录校验会拒绝加密项；固定占位密码和关闭 stdin 避免加密头在后台等待交互。
        start.ArgumentList.Add("-sccUTF-8");
        start.ArgumentList.Add("-pVideoEnhancer-no-encrypted-archives");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("无法启动 7za 解压器");
        process.StandardInput.Close();
        using var cancellation = DownloadCancellation.Token.Register(() => Kill(process));
        // -so 的进度写到 stderr；由调用方同时消费，避免把进度混入二进制输出。
        var stderr = start.ArgumentList.Contains("-so") ? null : process.StandardError.ReadToEndAsync();
        try
        {
            readOutput(process);
            process.WaitForExit();
            errors = stderr?.GetAwaiter().GetResult() ?? "";
            DownloadCancellation.Check();
            return process.ExitCode;
        }
        catch
        {
            Kill(process);
            process.WaitForExit();
            throw;
        }
    }

    private static void Kill(System.Diagnostics.Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string ReadProgress(StreamReader reader, Action<int>? progress)
    {
        var line = new StringBuilder();
        var diagnostics = new Queue<string>();
        var lastPercent = -1;
        void Flush()
        {
            var value = line.ToString().Trim();
            line.Clear();
            if (value.Length == 0) return;
            var match = PercentPattern.Match(value);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var percent) && percent is >= 0 and <= 100)
            {
                if (percent != lastPercent) { lastPercent = percent; progress?.Invoke(percent); }
            }
            else { diagnostics.Enqueue(value); if (diagnostics.Count > 20) diagnostics.Dequeue(); }
        }
        // 按字符消费已缓冲的文本，不能等满 4096 字符才交付短进度行。
        int character;
        while ((character = reader.Read()) >= 0)
        {
            if (character == '\b') continue;
            if (character is '\r' or '\n') Flush();
            else if (line.Length < 8192) line.Append((char)character);
        }
        Flush();
        return string.Join('\n', diagnostics);
    }

    private static IOException ToolError(string operation, int exit, string errors) =>
        new($"7za {operation}失败（退出码 {exit}）：{errors.Trim()}");
}
