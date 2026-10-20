Imports System
Imports System.Globalization
Imports System.IO
Imports System.Linq

Namespace videoenhancer
    Friend Partial Class PreviewEngine
        Private NotInheritable Class PreviewSource
            Public Output As String = ""
            Public Context As String = ""
            Public Waiting As Boolean
            Public FrameRate As Double
            Public UseTelemetry As Boolean
        End Class

        ' 只读取当前 Ext 步骤；自有步骤使用计划中明确的视频路径，不猜测 Python 参数的末尾文件。
        Private Shared Function ResolvePreviewSource(task As HostTask) As PreviewSource
            Dim result As New PreviewSource()
            Dim stepItem = task.当前步骤
            Dim command = task.命令行
            Dim stage = If(task.进度 Is Nothing, "", task.进度.当前阶段)
            Dim process As String = ""
            Dim kind As String = ""
            If stepItem IsNot Nothing Then
                command = HostRuntime.ReadValue(Of String)(stepItem, "实际执行参数")
                If String.IsNullOrWhiteSpace(command) Then command = HostRuntime.ReadValue(Of String)(stepItem, "命令行")
                process = HostRuntime.ReadValue(Of String)(stepItem, "实际执行文件名")
                If String.IsNullOrWhiteSpace(process) Then process = HostRuntime.ReadValue(Of String)(stepItem, "进程文件名")
                stage = HostRuntime.ReadValue(Of String)(stepItem, "显示名称")
                kind = Convert.ToString(HostRuntime.ReadMember(stepItem, "阶段"), CultureInfo.InvariantCulture)
            End If
            result.Context = task.当前进程ID.ToString(CultureInfo.InvariantCulture) & "|" & kind & "|" & stage & "|" & process & "|" & command
            Dim tokens = VideoEnhancer.WindowsArguments.Parse(If(command, ""))
            result.Waiting = tokens.Contains("-vn") OrElse kind.Contains("音频", StringComparison.Ordinal) OrElse
                kind.Contains("FFprobe", StringComparison.OrdinalIgnoreCase) OrElse
                stage.Contains("ffprobe", StringComparison.OrdinalIgnoreCase) OrElse
                (stage.Contains("音频", StringComparison.Ordinal) AndAlso Not stage.Contains("视频", StringComparison.Ordinal))
            If result.Waiting Then Return result

            If stepItem IsNot Nothing AndAlso HostRuntime.ReadValue(Of Boolean)(stepItem, "是插件步骤") AndAlso
                HostRuntime.ReadValue(Of String)(stepItem, "插件ID") = VideoEnhancer.ExtVideoPipeline.PluginId Then
                Dim stepId = HostRuntime.ReadValue(Of String)(stepItem, "插件步骤ID")
                Dim source = VideoEnhancer.EnhancementTaskRegistry.GetPreviewSource(task.ID, stepId)
                result.Waiting = source Is Nothing
                If source IsNot Nothing Then
                    result.Output = source.OutputPath
                    result.FrameRate = source.FrameRate
                    result.UseTelemetry = source.UseTelemetry
                End If
                Return result
            End If

            ' 只从确认的 FFmpeg 步骤提取输出，其他插件的工具或模型路径不具有视频输出语义。
            If Not String.IsNullOrWhiteSpace(process) AndAlso
                Not String.Equals(Path.GetFileName(process), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase) AndAlso
                Not String.Equals(process, FfmpegToolResolver.Resolve("ffmpeg.exe"), StringComparison.OrdinalIgnoreCase) Then
                result.Waiting = True
                Return result
            End If
            result.Output = task.输出文件
            If stepItem IsNot Nothing AndAlso tokens.Count > 0 Then
                Dim index = tokens.Count - 1
                While index >= 0 AndAlso (tokens(index) = "-y" OrElse tokens(index) = "-n")
                    index -= 1
                End While
                If index >= 0 Then
                    Dim output = tokens(index)
                    If output = "-" OrElse output.Equals("NUL", StringComparison.OrdinalIgnoreCase) OrElse output.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase) Then
                        result.Waiting = True
                        result.Output = ""
                    ElseIf Not output.StartsWith("-", StringComparison.Ordinal) Then
                        Dim directory = HostRuntime.ReadValue(Of String)(stepItem, "工作目录")
                        If Path.IsPathRooted(output) Then
                            result.Output = output
                        ElseIf Not String.IsNullOrWhiteSpace(directory) Then
                            result.Output = Path.GetFullPath(output, directory)
                        End If
                    End If
                End If
            End If
            Return result
        End Function
    End Class
End Namespace

