Imports System
Imports System.Diagnostics
Imports System.IO

Namespace videoenhancer

    ''' <summary>插件的固定便携目录；根目录由插件 DLL 的实际位置唯一确定。</summary>
    Friend NotInheritable Class PortableRuntime

        Private Sub New()
        End Sub

        Friend Shared ReadOnly Property PluginRoot As String = ResolvePluginRoot()
        ' 随 ZIP 提供的预览原生库固定从安装目录加载，数据目录可以单独重定向。
        Friend Shared ReadOnly Property PreviewNativeRoot As String =
            Path.Combine(PluginRoot, "videoenhancer", "bin", "fff-native-11")
        Friend Shared ReadOnly Property ApplicationRoot As String =
            ResolveApplicationRoot()
        Friend Shared ReadOnly Property CacheRoot As String =
            Path.Combine(ApplicationRoot, "cache")
        Friend Shared ReadOnly Property WorkRoot As String =
            Path.Combine(ApplicationRoot, ".work")
        Friend Shared ReadOnly Property UpdateRoot As String =
            Path.Combine(ApplicationRoot, ".update")

        Friend Shared Function CreateWorkFilePath(purpose As String, extension As String) As String
            Directory.CreateDirectory(WorkRoot)
            Dim safePurpose = If(String.IsNullOrWhiteSpace(purpose), "task", Path.GetFileName(purpose))
            Dim safeExtension = If(extension.StartsWith(".", StringComparison.Ordinal), extension, "." & extension)
            Return Path.Combine(WorkRoot, safePurpose & "-" & Guid.NewGuid().ToString("N") & safeExtension)
        End Function

        ''' <summary>把子进程的临时文件和常见计算缓存限制在插件便携目录内。</summary>
        Friend Shared Sub ConfigureProcess(startInfo As ProcessStartInfo)
            If startInfo Is Nothing OrElse startInfo.UseShellExecute Then Return

            Dim temporaryRoot = Path.Combine(WorkRoot, "tmp")
            Directory.CreateDirectory(temporaryRoot)
            Directory.CreateDirectory(CacheRoot)

            startInfo.Environment("TEMP") = temporaryRoot
            startInfo.Environment("TMP") = temporaryRoot
            startInfo.Environment("VIDEOENHANCER_WORK_DIR") = WorkRoot
            startInfo.Environment("XDG_CACHE_HOME") = CacheRoot
            startInfo.Environment("HF_HOME") = Path.Combine(CacheRoot, "huggingface")
            startInfo.Environment("TORCH_HOME") = Path.Combine(CacheRoot, "torch")
            startInfo.Environment("PIP_CACHE_DIR") = Path.Combine(CacheRoot, "pip")
            startInfo.Environment("UV_CACHE_DIR") = Path.Combine(CacheRoot, "uv")
            startInfo.Environment("CUDA_CACHE_PATH") = Path.Combine(CacheRoot, "cuda")
            startInfo.Environment("NUMBA_CACHE_DIR") = Path.Combine(CacheRoot, "numba")
            startInfo.Environment("MPLCONFIGDIR") = Path.Combine(CacheRoot, "matplotlib")
            startInfo.Environment("PYTHONPYCACHEPREFIX") = Path.Combine(CacheRoot, "pycache")
        End Sub

        Private Shared Function ResolveApplicationRoot() As String
            Dim configured = Environment.GetEnvironmentVariable("VIDEOENHANCER_ROOT")
            If String.IsNullOrWhiteSpace(configured) Then Return Path.Combine(PluginRoot, "videoenhancer")
            Return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured), PluginRoot)
        End Function

        Private Shared Function ResolvePluginRoot() As String
            Dim location = GetType(PortableRuntime).Assembly.Location
            If String.IsNullOrWhiteSpace(location) Then
                Throw New InvalidOperationException("无法确定 videoenhancer.3fui.dll 所在目录")
            End If
            Dim directory = Path.GetDirectoryName(Path.GetFullPath(location))
            If String.IsNullOrWhiteSpace(directory) Then
                Throw New InvalidOperationException("无法确定 videoenhancer.3fui.dll 所在目录")
            End If
            Return directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        End Function

    End Class

End Namespace
