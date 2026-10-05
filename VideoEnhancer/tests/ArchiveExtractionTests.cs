using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

internal static partial class Program
{
    private const BindingFlags ArchiveFlags = BindingFlags.NonPublic | BindingFlags.Static;

    private static void TestMergedArchives(Assembly assembly)
    {
        System.Console.WriteLine("运行：合并 DLL 的原生 7za 解压、目录检查与取消");
        string runtime = Path.Combine(_root, "archive-runtime");
        assembly.GetType("VideoEnhancer.BackendServices", true)!.GetMethod("ConfigureRuntime")!.Invoke(null, [runtime]);
        var extractor = assembly.GetType("VideoEnhancer.NativeSevenZipExtractor", true)!;
        Check((bool)extractor.GetMethod("EnsureAvailable", ArchiveFlags)!.Invoke(null, null)!, "单 DLL 能离线释放 7za");
        var executable = (string)extractor.GetProperty("Executable", ArchiveFlags)!.GetValue(null)!;
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))) == "EDBEE35370E14030E4C785CF88200F42DC651C1EB4217C1E3963C38A12F099B0",
            "释放的解压器与锁定的官方 7za 字节一致");
        Check(File.Exists(Path.Combine(runtime, "licenses", "7zip", "License.txt")) &&
            File.Exists(Path.Combine(runtime, "licenses", "7zip", "7z2603-src.tar.xz")), "7za 许可与对应源码一起释放");
        Check(assembly.GetType("VideoEnhancer.ManagedArchiveExtractor") is null, "合并 DLL 没有托管解压和托管打包辅助类");

        string source = Path.Combine(_root, "archive-source");
        Directory.CreateDirectory(Path.Combine(source, "模型 子目录"));
        Directory.CreateDirectory(Path.Combine(source, "空目录"));
        byte[] content = Encoding.UTF8.GetBytes("原生解压的内容\n模型配置");
        File.WriteAllBytes(Path.Combine(source, "模型 子目录", "配置.txt"), content);
        File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
        string seven = Path.Combine(_root, "中文 包.7z"), zip = Path.Combine(_root, "archive.zip"), tar = Path.Combine(_root, "archive.tar");
        PackNative(executable, source, ["a", "-t7z", seven, ".", "-mx=1"]);
        ZipFile.CreateFromDirectory(source, zip);
        using (var writer = new TarWriter(File.Create(tar), TarEntryFormat.Pax, leaveOpen: false))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./空目录/"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./模型 子目录/配置.txt") { DataStream = new MemoryStream(content) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "empty.bin") { DataStream = new MemoryStream() });
        }
        string gzip = tar + ".gz", xz = tar + ".xz", zstd = tar + ".zst";
        PackNative(executable, _root, ["a", "-tgzip", gzip, tar]);
        PackNative(executable, _root, ["a", "-txz", xz, tar]);
        WriteZstdFrame(tar, zstd);
        foreach (var archive in new[] { seven, zip, tar, gzip, xz, zstd })
        {
            string output = Path.Combine(_root, "extracted-" + Path.GetFileName(archive));
            var progress = new List<int>();
            ExtractNative(extractor, archive, output, progress.Add);
            Check(File.ReadAllBytes(Path.Combine(output, "模型 子目录", "配置.txt")).SequenceEqual(content), "中文路径与内容完整：" + Path.GetFileName(archive));
            Check(new FileInfo(Path.Combine(output, "empty.bin")).Length == 0 && Directory.Exists(Path.Combine(output, "空目录")), "保留空文件和空目录：" + Path.GetFileName(archive));
            Check(progress[0] == 0 && progress[^1] == 100 && progress.Zip(progress.Skip(1)).All(pair => pair.First <= pair.Second), "进度保持有序并仅在成功后完成：" + Path.GetFileName(archive));
            Check(!Directory.EnumerateFiles(output, "*.tar", SearchOption.AllDirectories).Any(), "压缩 TAR 没有遗留第一层文件：" + Path.GetFileName(archive));
        }
        string overwrite = Path.Combine(_root, "extracted-archive.zip", "模型 子目录", "配置.txt");
        File.WriteAllText(overwrite, "之前的文件");
        ExtractNative(extractor, zip, Path.GetDirectoryName(Path.GetDirectoryName(overwrite)!)!);
        Check(File.ReadAllBytes(overwrite).SequenceEqual(content), "普通已有文件可被安装覆盖");

        string raw = Path.Combine(_root, "原始模型.onnx");
        File.WriteAllBytes(raw, content);
        foreach (var (suffix, format) in new[] { ("gz", "gzip"), ("xz", "xz"), ("zst", "zstd") })
        {
            var archive = raw + "." + suffix;
            if (format == "zstd") WriteZstdFrame(raw, archive);
            else PackNative(executable, _root, ["a", "-t" + format, archive, raw]);
            string output = Path.Combine(_root, "raw-extracted-" + suffix);
            ExtractNative(extractor, archive, output);
            Check(File.ReadAllBytes(Path.Combine(output, Path.GetFileName(raw))).SequenceEqual(content), "单独压缩的模型按原文件名输出：" + suffix);
        }
        Check(!Directory.EnumerateDirectories(Path.Combine(runtime, ".work"), "archive-stream-*").Any(), "压缩流临时文件在成功后清理");
        TestArchiveRejections(extractor, executable, source, zip, runtime);
        TestArchiveCancellation(assembly, extractor, executable, runtime);
    }

    private static void ExtractNative(Type extractor, string archive, string output, Action<int>? progress = null)
    {
        try { extractor.GetMethod("Extract", ArchiveFlags)!.Invoke(null, [archive, output, progress]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }

    private static void PackNative(string executable, string workingDirectory, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments.Concat(["-y", "-bd", "-sccUTF-8"])) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("7za 测试夹具创建超时"); }
        if (process.ExitCode != 0) throw new IOException("7za 测试夹具创建失败：" + errors.Result + output.Result);
    }

    private static void WriteZstdFrame(string input, string output)
    {
        // 测试数据采用标准 Zstandard 的未压缩块，不依赖额外的压缩库。
        byte[] data = File.ReadAllBytes(input);
        using var writer = new BinaryWriter(File.Create(output));
        writer.Write(0xFD2FB528u);
        writer.Write((byte)0xA0); // 单段，四字节内容长度，无校验和。
        writer.Write((uint)data.Length);
        uint block = ((uint)data.Length << 3) | 1u;
        writer.Write((byte)block); writer.Write((byte)(block >> 8)); writer.Write((byte)(block >> 16));
        writer.Write(data);
    }

    private static void TestArchiveRejections(Type extractor, string executable, string source, string validZip, string runtime)
    {
        int index = 0;
        foreach (var entry in new[] { "../escape.txt", "/absolute.txt", "C:/absolute.txt", "safe/../escape.txt", "safe/file.", "safe/file:stream", "NUL.txt" })
        {
            string archive = Path.Combine(_root, "unsafe-" + ++index + ".zip"), output = Path.Combine(_root, "unsafe-output-" + index);
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write("拒绝写入");
            ExpectArchiveFailure(extractor, archive, output, "拒绝不安全路径：" + entry, typeof(InvalidDataException));
            Check(!Directory.Exists(output), "目录预检失败之前没有创建目标目录：" + entry);
        }
        foreach (var type in new[] { TarEntryType.SymbolicLink, TarEntryType.HardLink, TarEntryType.Fifo })
        {
            string archive = Path.Combine(_root, type + ".tar"), output = Path.Combine(_root, type + "-output");
            using (var writer = new TarWriter(File.Create(archive), TarEntryFormat.Pax, leaveOpen: false))
            {
                var entry = new PaxTarEntry(type, "unsafe-link");
                if (type != TarEntryType.Fifo) entry.LinkName = "../escape.txt";
                writer.WriteEntry(entry);
            }
            ExpectArchiveFailure(extractor, archive, output, "拒绝 TAR 链接或特殊文件：" + type, typeof(InvalidDataException));
        }
        // ZIP 的 Unix 符号链接通过中央目录权限标志表达。
        string linkZip = Path.Combine(_root, "symbolic-link.zip");
        using (var archive = ZipFile.Open(linkZip, ZipArchiveMode.Create))
        {
            var link = archive.CreateEntry("link");
            link.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(link.Open()); writer.Write("../escape.txt");
        }
        byte[] linkBytes = File.ReadAllBytes(linkZip);
        for (int offset = 0; offset <= linkBytes.Length - 6; offset++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(linkBytes.AsSpan(offset, 4)) == 0x02014B50)
            { linkBytes[offset + 5] = 3; break; }
        File.WriteAllBytes(linkZip, linkBytes);
        ExpectArchiveFailure(extractor, linkZip, Path.Combine(_root, "zip-link-output"), "拒绝 ZIP 的 Unix 符号链接", typeof(InvalidDataException));

        string external = Path.Combine(_root, "outside-destination"), destination = Path.Combine(_root, "linked-destination");
        Directory.CreateDirectory(external); Directory.CreateDirectory(destination);
        string junction = Path.Combine(destination, "模型 子目录");
        CreateArchiveJunction(junction, external);
        try
        {
            ExpectArchiveFailure(extractor, validZip, destination, "拒绝已有目标目录中的重解析点", typeof(InvalidDataException));
            ExpectArchiveFailure(extractor, validZip, Path.Combine(junction, "child"), "拒绝输出根目录祖先的重解析点", typeof(InvalidDataException));
            Check(!Directory.EnumerateFileSystemEntries(external).Any(), "重解析点外部目标没有被写入");
        }
        finally { Directory.Delete(junction); }

        string wrappedLink = Path.Combine(_root, "SymbolicLink.tar.gz");
        PackNative(executable, _root, ["a", "-tgzip", wrappedLink, Path.Combine(_root, "SymbolicLink.tar")]);
        ExpectArchiveFailure(extractor, wrappedLink, Path.Combine(_root, "wrapped-link-output"), "压缩 TAR 的第二层也拒绝链接", typeof(InvalidDataException));
        string falseTar = Path.Combine(_root, "invalid-content.tar"), falseGzip = falseTar + ".gz";
        File.WriteAllText(falseTar, "不是 TAR 的模型数据");
        PackNative(executable, _root, ["a", "-tgzip", falseGzip, falseTar]);
        ExpectArchiveFailure(extractor, falseGzip, Path.Combine(_root, "invalid-tar-output"), "损坏的压缩 TAR 不作为普通文件安装", typeof(InvalidDataException));

        foreach (bool header in new[] { false, true })
        {
            string archive = Path.Combine(_root, "encrypted-" + header + ".7z");
            PackNative(executable, source, ["a", "-t7z", archive, ".", "-pfixture-secret", "-mhe=" + (header ? "on" : "off")]);
            var watch = Stopwatch.StartNew();
            ExpectArchiveFailure(extractor, archive, Path.Combine(_root, "encrypted-output-" + header), "拒绝加密数据或加密目录且不等待交互", typeof(IOException), typeof(InvalidDataException));
            Check(watch.Elapsed < TimeSpan.FromSeconds(5), "加密包没有后台密码交互等待");
        }
        string rar = Path.Combine(_root, "renamed.rar");
        File.Copy(validZip, rar);
        ExpectArchiveFailure(extractor, rar, Path.Combine(_root, "rar-output"), "取消 RAR 格式入口", typeof(InvalidDataException));
        string corrupted = Path.Combine(_root, "corrupt.zip");
        File.WriteAllBytes(corrupted, [0x50, 0x4B, 3, 4, 0]);
        ExpectArchiveFailure(extractor, corrupted, Path.Combine(_root, "corrupt-output"), "损坏包返回可处理的解压错误", typeof(IOException));

        string invalidCrc = Path.Combine(_root, "bad-crc.zip");
        using (var archive = ZipFile.Open(invalidCrc, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("bad.txt", CompressionLevel.NoCompression).Open())) writer.Write("CRC fixture");
        byte[] crcBytes = File.ReadAllBytes(invalidCrc);
        int start = 30 + BinaryPrimitives.ReadUInt16LittleEndian(crcBytes.AsSpan(26, 2)) + BinaryPrimitives.ReadUInt16LittleEndian(crcBytes.AsSpan(28, 2));
        crcBytes[start] ^= 1;
        File.WriteAllBytes(invalidCrc, crcBytes);
        var percentages = new List<int>();
        try { ExtractNative(extractor, invalidCrc, Path.Combine(_root, "bad-crc-output"), percentages.Add); throw new Exception("损坏 CRC 被误判为成功"); }
        catch (IOException error) { Check(error.Message.Contains("CRC", StringComparison.OrdinalIgnoreCase), "7za 校验 CRC 并报告失败"); }
        Check(!percentages.Contains(100), "CRC 失败没有报告解压完成");
        Check(!Directory.EnumerateDirectories(Path.Combine(runtime, ".work"), "archive-stream-*").Any(), "失败路径未遗留压缩流工作目录");
    }

    private static void ExpectArchiveFailure(Type extractor, string archive, string output, string message, params Type[] expected)
    {
        try { ExtractNative(extractor, archive, output); throw new Exception("未拒绝：" + message); }
        catch (Exception error) when (expected.Any(type => type.IsInstanceOfType(error))) { Check(true, message); }
    }

    private static void CreateArchiveJunction(string link, string target)
    {
        // 目录联接不要求开发者模式，夹具仅指向本测试的临时目录。
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Value '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd(); process.StandardOutput.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("无法创建目录联接夹具：" + error);
    }

    private static void TestArchiveCancellation(Assembly assembly, Type extractor, string executable, string runtime)
    {
        string large = Path.Combine(_root, "cancel-payload.bin"), zipPath = Path.Combine(_root, "cancel.zip"), gzipPath = large + ".gz";
        using (var file = File.Create(large))
        {
            byte[] block = new byte[1024 * 1024];
            for (int index = 0; index < 64; index++) file.Write(block);
        }
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var output = zip.CreateEntry("large.bin", CompressionLevel.Optimal).Open())
        using (var input = File.OpenRead(large)) input.CopyTo(output);
        using (var output = new GZipStream(File.Create(gzipPath), CompressionLevel.Optimal))
        using (var input = File.OpenRead(large)) input.CopyTo(output);
        foreach (var archive in new[] { zipPath, gzipPath })
        {
            // 在合并 DLL 的调用作用域注入令牌，并在实际解压子进程出现后取消。
            var invocationType = assembly.GetType("VideoEnhancer.BackendInvocation", true)!;
            var invocation = Activator.CreateInstance(invocationType, nonPublic: true)!;
            var token = (CancellationTokenSource)invocationType.GetField("Cancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(invocation)!;
            var slot = invocationType.GetField("Slot", ArchiveFlags)!.GetValue(null)!;
            var value = slot.GetType().GetProperty("Value")!;
            value.SetValue(slot, invocation);
            using var ready = new ManualResetEventSlim();
            var percentages = new System.Collections.Concurrent.ConcurrentQueue<int>();
            var pids = new HashSet<int>();
            Task? task = null;
            try
            {
                task = Task.Run(() => ExtractNative(extractor, archive, Path.Combine(_root, "cancel-output-" + Path.GetFileName(archive)), percent =>
                {
                    percentages.Enqueue(percent);
                    if (percent == 0) ready.Set();
                }));
                if (!ready.Wait(10000)) throw new TimeoutException("解压没有进入执行阶段");
                var deadline = Stopwatch.StartNew();
                while (pids.Count == 0 && !task.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5))
                {
                    foreach (var process in Process.GetProcessesByName("7za"))
                        using (process)
                            try
                            {
                                if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase) && !process.HasExited)
                                    pids.Add(process.Id);
                            }
                            catch (InvalidOperationException) { }
                    if (pids.Count == 0) Thread.Sleep(1);
                }
                token.Cancel();
                try { task.GetAwaiter().GetResult(); throw new Exception("取消操作未停止解压"); }
                catch (OperationCanceledException) { Check(true, "实际解压返回取消异常：" + Path.GetExtension(archive)); }
                Check(pids.Count > 0 && pids.All(pid =>
                {
                    try { using var process = Process.GetProcessById(pid); return process.HasExited; }
                    catch (ArgumentException) { return true; }
                }), "取消关闭本次真实 7za 解压子进程：" + Path.GetExtension(archive));
                Check(!percentages.Contains(100), "取消的解压没有报告完成：" + Path.GetExtension(archive));
                ExpectArchiveFailure(extractor, archive, Path.Combine(_root, "pre-cancel-output"), "已取消调用不再启动解压器", typeof(OperationCanceledException));
                Check(!Directory.Exists(Path.Combine(_root, "pre-cancel-output")), "预先取消不会创建解压目标");
                Check(!Directory.EnumerateDirectories(Path.Combine(runtime, ".work"), "archive-stream-*").Any(), "取消后没有压缩流工作目录");
            }
            finally
            {
                token.Cancel();
                if (task is not null) try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                value.SetValue(slot, null); token.Dispose();
            }
        }
    }
}
