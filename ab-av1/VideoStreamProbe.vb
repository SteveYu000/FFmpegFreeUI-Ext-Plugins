Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>仅在需要放行显式 0:v:0 时读取实际输入，不缓存不同任务或文件的探测结果。</summary>
Friend NotInheritable Class VideoStreamProbe
    Private Sub New()
    End Sub

    Friend Shared Async Function CountAsync(inputPath As String,
                                            cancellationToken As CancellationToken) As Task(Of Integer)
        cancellationToken.ThrowIfCancellationRequested()
        If Not File.Exists(inputPath) Then Throw New FileNotFoundException("找不到输入文件。", inputPath)
        Dim executable = PluginEnvironment.ResolveExecutablePath("ffprobe.exe")
        If executable = String.Empty Then executable = PluginEnvironment.ResolveExecutablePath("ffprobe")
        If executable = String.Empty Then
            Throw New FileNotFoundException("确认 0:v:0 对应单视频流输入需要 ffprobe，请将 ffprobe.exe 放入插件目录、3FUI 目录或 PATH。", "ffprobe.exe")
        End If

        Dim startInfo As New ProcessStartInfo With {
            .FileName = executable,
            .WorkingDirectory = PluginEnvironment.PluginDirectory,
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8
        }
        PluginEnvironment.ConfigureChildProcessEnvironment(startInfo)
        For Each argument In {"-v", "error", "-select_streams", "v", "-show_entries", "stream=index",
                              "-of", "json", "-i", Path.GetFullPath(inputPath)}
            startInfo.ArgumentList.Add(argument)
        Next

        Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(30)),
              linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token),
              process As New Process With {.StartInfo = startInfo}
            linked.Token.ThrowIfCancellationRequested()
            If Not process.Start() Then Throw New InvalidOperationException("无法启动 ffprobe。")
            Dim stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token)
            Dim stderrTask = process.StandardError.ReadToEndAsync(linked.Token)
            Try
                Await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(linked.Token)).ConfigureAwait(False)
                linked.Token.ThrowIfCancellationRequested()
                Dim stdout = Await stdoutTask.ConfigureAwait(False)
                Dim stderr = Await stderrTask.ConfigureAwait(False)
                If process.ExitCode <> 0 Then
                    Dim detail = stderr.Trim()
                    If detail.Length > 2000 Then detail = detail.Substring(0, 2000)
                    Throw New InvalidDataException($"ffprobe 无法确认输入文件的视频流数量（退出码 {process.ExitCode}）：{detail}")
                End If
                Return ParseCount(stdout)
            Catch ex As OperationCanceledException When timeout.IsCancellationRequested AndAlso Not cancellationToken.IsCancellationRequested
                Throw New TimeoutException("探测输入文件的视频流数量超时，无法放行 0:v:0。", ex)
            Finally
                Try
                    If Not process.HasExited Then process.Kill(entireProcessTree:=True)
                Catch ex As InvalidOperationException
                Catch ex As System.ComponentModel.Win32Exception
                End Try
            End Try
        End Using
    End Function

    Friend Shared Function ParseCount(output As String) As Integer
        Try
            Using document = JsonDocument.Parse(output)
                Dim streams As JsonElement
                If document.RootElement.ValueKind <> JsonValueKind.Object OrElse
                   Not document.RootElement.TryGetProperty("streams", streams) OrElse
                   streams.ValueKind <> JsonValueKind.Array Then
                    Throw New InvalidDataException("ffprobe 未返回有效的视频流列表，无法放行 0:v:0。")
                End If
                For Each stream In streams.EnumerateArray()
                    Dim index As JsonElement
                    Dim streamIndex As Integer
                    If stream.ValueKind <> JsonValueKind.Object OrElse
                       Not stream.TryGetProperty("index", index) OrElse index.ValueKind <> JsonValueKind.Number OrElse
                       Not index.TryGetInt32(streamIndex) OrElse streamIndex < 0 Then
                        Throw New InvalidDataException("ffprobe 返回的视频流信息不完整，无法放行 0:v:0。")
                    End If
                Next
                Return streams.GetArrayLength()
            End Using
        Catch ex As JsonException
            Throw New InvalidDataException("ffprobe 未返回有效 JSON，无法确认输入文件的视频流数量。", ex)
        End Try
    End Function
End Class
