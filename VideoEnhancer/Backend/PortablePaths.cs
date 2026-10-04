using System.Diagnostics;

namespace VideoEnhancer;

/// <summary>
/// VideoEnhancer 的便携目录约定。除用户明确选择的输入、输出外，运行期文件写入插件的数据目录。
/// </summary>
internal static class PortablePaths
{
    internal static string CoreRoot { get; private set; } = Path.Combine(Path.GetDirectoryName(typeof(BackendServices).Assembly.Location)!, "videoenhancer");
    public static void Configure(string root) => CoreRoot = Path.GetFullPath(root);

    internal static string CacheRoot => Path.Combine(CoreRoot, "cache");
    internal static string WorkRoot => Path.Combine(CoreRoot, ".work");
    internal static string UpdateRoot => Path.Combine(CoreRoot, ".update");

    internal static string EmbeddedToolsRoot(string version) =>
        Path.Combine(CoreRoot, "bin", "embedded-tools", version);

    internal static string CreateWorkDirectory(string purpose)
    {
        var safePurpose = string.Concat((purpose ?? "task").Where(
            character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        if (safePurpose.Length == 0) safePurpose = "task";
        var path = Path.Combine(WorkRoot, safePurpose + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string CreateWorkFilePath(string purpose, string extension)
    {
        Directory.CreateDirectory(WorkRoot);
        var safeExtension = extension.StartsWith('.') ? extension : "." + extension;
        return Path.Combine(WorkRoot, purpose + "-" + Guid.NewGuid().ToString("N") + safeExtension);
    }

    /// <summary>把子进程可能产生的临时文件和常见计算缓存重定向到便携目录。</summary>
    internal static void ConfigureChildProcess(ProcessStartInfo startInfo)
    {
        if (startInfo.UseShellExecute) return;

        var temporaryRoot = Path.Combine(WorkRoot, "tmp");
        Directory.CreateDirectory(temporaryRoot);
        Directory.CreateDirectory(CacheRoot);

        startInfo.Environment["TEMP"] = temporaryRoot;
        startInfo.Environment["TMP"] = temporaryRoot;
        startInfo.Environment["VIDEOENHANCER_WORK_DIR"] = WorkRoot;
        startInfo.Environment["XDG_CACHE_HOME"] = CacheRoot;
        startInfo.Environment["HF_HOME"] = Path.Combine(CacheRoot, "huggingface");
        startInfo.Environment["TORCH_HOME"] = Path.Combine(CacheRoot, "torch");
        startInfo.Environment["PIP_CACHE_DIR"] = Path.Combine(CacheRoot, "pip");
        startInfo.Environment["UV_CACHE_DIR"] = Path.Combine(CacheRoot, "uv");
        startInfo.Environment["CUDA_CACHE_PATH"] = Path.Combine(CacheRoot, "cuda");
        startInfo.Environment["NUMBA_CACHE_DIR"] = Path.Combine(CacheRoot, "numba");
        startInfo.Environment["MPLCONFIGDIR"] = Path.Combine(CacheRoot, "matplotlib");
        startInfo.Environment["PYTHONPYCACHEPREFIX"] = Path.Combine(CacheRoot, "pycache");
    }
}
