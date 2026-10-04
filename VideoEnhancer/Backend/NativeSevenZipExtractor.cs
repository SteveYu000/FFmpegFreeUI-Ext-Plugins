using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoEnhancer;

/// <summary>使用随插件分发的独立 7-Zip，按数据处理进度报告并支持取消。</summary>
internal static class NativeSevenZipExtractor
{
    internal static string Executable => Path.Combine(PortablePaths.CoreRoot, "bin", "7zip", "7za.exe");

    internal static bool EnsureAvailable()
    {
        // 单独更新运行 EXE 的用户也能取得解压组件和对应源码，无需额外联网。
        var assembly = typeof(NativeSevenZipExtractor).Assembly;
        foreach (var (name, relative) in new[]
        {
            ("7za.exe", "bin/7zip/7za.exe"), ("License.txt", "licenses/7zip/License.txt"),
            ("SOURCE.txt", "licenses/7zip/SOURCE.txt"), ("7z2603-src.tar.xz", "licenses/7zip/7z2603-src.tar.xz")
        })
        {
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

    internal static void Extract(string archive, string output, Action<int>? progress)
    {
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        PortablePaths.ConfigureChildProcess(start);
        foreach (var arg in new[] { "x", archive, "-o" + output, "-y", "-aoa", "-mmt=on", "-sccUTF-8", "-bsp1", "-bso1", "-bse2", "-bb0" })
            start.ArgumentList.Add(arg);
        DownloadCancellation.Check();
        using var process = Process.Start(start) ?? throw new IOException("无法启动 7-Zip 解压器");
        using var cancellation = DownloadCancellation.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var error = process.StandardError.ReadToEndAsync();
        var percentPattern = new Regex(@"(?:^|\s)(\d{1,3})%", RegexOptions.Compiled);
        var line = new StringBuilder();
        var lastPercent = -1;
        var diagnostics = new Queue<string>();
        var buffer = new char[4096];
        int count;
        while ((count = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var character = buffer[i];
                if (character == '\b') continue;
                if (character is not ('\r' or '\n')) { line.Append(character); continue; }
                var value = line.ToString().Trim();
                line.Clear();
                if (value.Length == 0) continue;
                var match = percentPattern.Match(value);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var percent) && percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Invoke(percent);
                }
                if (!match.Success)
                {
                    diagnostics.Enqueue(value);
                    if (diagnostics.Count > 20) diagnostics.Dequeue();
                }
            }
        }
        process.WaitForExit();
        var stderr = error.GetAwaiter().GetResult();
        DownloadCancellation.Check();
        if (process.ExitCode != 0)
            throw new IOException($"7-Zip 解压失败（退出码 {process.ExitCode}）：{stderr}\n{string.Join('\n', diagnostics)}");
        if (lastPercent != 100) progress?.Invoke(100);
    }
}
