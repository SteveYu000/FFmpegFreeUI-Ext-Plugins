namespace VideoEnhancer;

/// <summary>由插件在便携工作目录写入取消标记；安装事务开始后不再中断。</summary>
internal static class DownloadCancellation
{
    internal static CancellationToken Token => BackendInvocation.Current?.Cancellation.Token ?? CancellationToken.None;
    internal static void Check() => Token.ThrowIfCancellationRequested();

    internal static void Log(string operation, Exception error)
    {
        try
        {
            var folder = Path.Combine(PortablePaths.CoreRoot, "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "downloads.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {operation}{Environment.NewLine}{error}{Environment.NewLine}",
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception logError) { System.Diagnostics.Trace.WriteLine(logError); }
    }
}
