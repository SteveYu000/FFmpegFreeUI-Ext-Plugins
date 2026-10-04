using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers.SevenZip;

namespace VideoEnhancer;

/// <summary>使用托管库解压受支持格式，并在写盘前统一阻止目录穿越和链接项。</summary>
internal static class ManagedArchiveExtractor
{
    private static readonly ExtractionOptions ExtractionOptions = new()
    {
        Overwrite = true,
        ExtractFullPath = true,
        PreserveFileTime = true,
        PreserveAttributes = false,
        CheckCrc = true
    };

    internal static void Extract(string archivePath, string outputDirectory, Action<int>? progress = null)
    {
        var archive = Path.GetFullPath(archivePath);
        var output = Path.GetFullPath(outputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!File.Exists(archive)) throw new FileNotFoundException("压缩文件不存在", archive);
        EnsureSafeDirectory(output);

        // 7z 没有流式 Reader API；其余当前支持格式走顺序 Reader，避免一次加载全部内容。
        if (Path.GetExtension(archive).Equals(".7z", StringComparison.OrdinalIgnoreCase))
            ExtractSevenZip(archive, output, progress);
        else
            ExtractSequential(archive, output);
    }

    /// <summary>把目录内容写成 7z；供发布脚本复用，避免要求构建机安装外部 7-Zip。</summary>
    internal static void CreateSevenZip(string sourceDirectory, string archivePath)
    {
        var sourceRoot = Path.GetFullPath(sourceDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var output = Path.GetFullPath(archivePath);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException("归档源目录不存在：" + sourceRoot);
        if (!Path.GetExtension(output).Equals(".7z", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("托管归档输出必须使用 .7z 扩展名", nameof(archivePath));
        if (output.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("归档输出不能位于归档源目录内部");

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        var files = Directory.EnumerateFiles(sourceRoot, "*", enumeration)
            .Select(path => new
            {
                Source = path,
                Entry = NormalizeArchiveEntryPath(Path.GetRelativePath(sourceRoot, path))
            })
            .OrderBy(item => item.Entry, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) throw new InvalidDataException("归档源目录不包含文件");

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new SevenZipWriter(destination, new SevenZipWriterOptions()))
            {
                foreach (var file in files)
                {
                    using var input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    writer.Write(file.Entry, input, File.GetLastWriteTimeUtc(file.Source));
                }
            }
            File.Move(temporary, output, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void ExtractSevenZip(string archivePath, string outputRoot, Action<int>? progress)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries.ToArray();
        // 写盘前预检全部归档项；相同父目录只检查一次，避免数万文件重复查询磁盘。
        var checkedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { outputRoot };
        for (var index = 0; index < entries.Length; index++)
        {
            DownloadCancellation.Check();
            var destination = ResolveEntry(outputRoot, entries[index], archivePath, index);
            var parent = Path.GetDirectoryName(destination)!;
            var uncheckedParents = new Stack<string>();
            while (!checkedDirectories.Contains(parent))
            {
                uncheckedParents.Push(parent);
                parent = Path.GetDirectoryName(parent)!;
            }
            while (uncheckedParents.TryPop(out var directory))
            {
                EnsureSafeDirectory(directory);
                checkedDirectories.Add(directory);
            }
            RejectExistingReparsePoint(destination);
        }

        if (NativeSevenZipExtractor.EnsureAvailable())
        {
            NativeSevenZipExtractor.Extract(archivePath, outputRoot, progress);
            return;
        }

        using var reader = archive.ExtractAllEntries();
        var completed = 0;
        var lastPercent = -1;
        while (reader.MoveToNextEntry())
        {
            DownloadCancellation.Check();
            var entry = reader.Entry;
            var destination = ResolveEntry(outputRoot, entry, archivePath, completed);
            if (entry.IsDirectory)
                EnsureSafeDirectory(destination);
            else
            {
                EnsureSafeParent(destination, outputRoot);
                RejectExistingReparsePoint(destination);
                reader.WriteEntryToFile(destination, ExtractionOptions);
            }
            completed++;
            if (progress is null || entries.Length == 0) continue;
            var percent = (int)Math.Min(100L, completed * 100L / entries.Length);
            if (percent == lastPercent) continue;
            lastPercent = percent;
            progress(percent);
        }
    }

    private static void ExtractSequential(string archivePath, string outputRoot)
    {
        using var reader = ReaderFactory.OpenReader(archivePath);
        var index = 0;
        while (reader.MoveToNextEntry())
        {
            DownloadCancellation.Check();
            var entry = reader.Entry;
            var destination = ResolveEntry(outputRoot, entry, archivePath, index++);
            if (entry.IsDirectory)
            {
                EnsureSafeDirectory(destination);
                continue;
            }
            EnsureSafeParent(destination, outputRoot);
            RejectExistingReparsePoint(destination);
            reader.WriteEntryToFile(destination, ExtractionOptions);
        }
    }

    private static string ResolveEntry(string outputRoot, IEntry entry, string archivePath, int index)
    {
        // 7z 中零字节标记文件没有加密数据流；SharpCompress 可能将其误报为加密项。
        if (entry.IsEncrypted && entry.Size > 0) throw new InvalidDataException("不支持加密压缩项：" + entry.Key);
        if (!string.IsNullOrEmpty(entry.LinkTarget))
            throw new InvalidDataException("出于安全原因不解压符号链接或硬链接：" + entry.Key);

        var key = entry.Key;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (entry.IsDirectory || index != 0)
                throw new InvalidDataException("压缩包包含无文件名条目");
            key = Path.GetFileNameWithoutExtension(archivePath);
        }

        var normalized = key.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new InvalidDataException("压缩项不是安全的相对路径：" + key);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(IsUnsafeSegment))
            throw new InvalidDataException("压缩项路径不安全：" + key);

        var destination = Path.GetFullPath(Path.Combine(outputRoot,
            Path.Combine(parts)));
        var prefix = outputRoot + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("压缩项越出目标目录：" + key);
        return destination;
    }

    private static bool IsUnsafeSegment(string segment)
    {
        if (segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')) return true;
        return segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;
    }

    private static string NormalizeArchiveEntryPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new InvalidDataException("归档项不是安全的相对路径：" + relativePath);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(IsUnsafeSegment))
            throw new InvalidDataException("归档项路径不安全：" + relativePath);
        return string.Join('/', parts);
    }

    private static void EnsureSafeParent(string path, string root)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("无法确定解压目标目录：" + path);
        EnsureSafeDirectory(root);
        if (parent.Equals(root, StringComparison.OrdinalIgnoreCase)) return;

        var relative = Path.GetRelativePath(root, parent);
        var current = root;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureSafeDirectory(current);
        }
    }

    private static void EnsureSafeDirectory(string path)
    {
        if (File.Exists(path)) throw new IOException("解压目录被同名文件占用：" + path);
        if (Directory.Exists(path))
        {
            RejectExistingReparsePoint(path);
            return;
        }
        Directory.CreateDirectory(path);
        RejectExistingReparsePoint(path);
    }

    private static void RejectExistingReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("解压目标包含重解析点，已拒绝写入：" + path);
    }
}
