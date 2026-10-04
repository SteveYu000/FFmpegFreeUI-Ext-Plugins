Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.Json

Namespace videoenhancer
        Friend NotInheritable Class SegmentVideoProbe
            Public Property Path As String = ""
            Public Property FrameCount As Long
            Public Property DurationSeconds As Double
            Public Property Width As Integer
            Public Property Height As Integer
            Public Property FrameRate As Double
            Public Property Keyframes As New List(Of Double)()
        End Class

    Friend NotInheritable Class SegmentVideoProbeService
        Friend Shared Function ProbeSegmentVideo(ffprobe As String, source As String) As SegmentVideoProbe
            If String.IsNullOrWhiteSpace(ffprobe) OrElse Not File.Exists(ffprobe) Then Return Nothing
            Dim metadata = RunSegmentProbe(ffprobe, New String() {
                "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=width,height,nb_frames,avg_frame_rate,r_frame_rate,duration:format=duration",
                "-of", "json", source
            }, 120000)
            If String.IsNullOrWhiteSpace(metadata) Then Return Nothing
            Dim result As New SegmentVideoProbe With {.Path = Path.GetFullPath(source)}
            Using document = JsonDocument.Parse(metadata)
                Dim streams = document.RootElement.GetProperty("streams")
                If streams.GetArrayLength() = 0 Then Return Nothing
                Dim stream = streams(0)
                result.Width = stream.GetProperty("width").GetInt32()
                result.Height = stream.GetProperty("height").GetInt32()
                result.FrameRate = ParseSegmentRate(GetProbeString(stream, "avg_frame_rate"))
                If result.FrameRate <= 0 Then result.FrameRate = ParseSegmentRate(GetProbeString(stream, "r_frame_rate"))
                result.DurationSeconds = ParseProbeDouble(GetProbeString(stream, "duration"))
                If result.DurationSeconds <= 0 Then
                    Dim format As JsonElement
                    If document.RootElement.TryGetProperty("format", format) Then
                        result.DurationSeconds = ParseProbeDouble(GetProbeString(format, "duration"))
                    End If
                End If
                Dim frames As Long
                Dim frameText = GetProbeString(stream, "nb_frames")
                If Long.TryParse(frameText, NumberStyles.Integer, CultureInfo.InvariantCulture, frames) Then
                    result.FrameCount = frames
                End If
            End Using
            If result.Width <= 0 OrElse result.Height <= 0 OrElse result.DurationSeconds <= 0 OrElse result.FrameRate <= 0 Then Return Nothing
            If result.FrameCount <= 0 Then result.FrameCount = Math.Max(1, CLng(Math.Round(result.DurationSeconds * result.FrameRate)))

            Dim keyframeJson = RunSegmentProbe(ffprobe, New String() {
                "-v", "error", "-skip_frame", "nokey", "-select_streams", "v:0",
                "-show_frames", "-show_entries", "frame=best_effort_timestamp_time,pts_time,pkt_dts_time",
                "-of", "json", source
            }, 300000)
            result.Keyframes.Add(0)
            If Not String.IsNullOrWhiteSpace(keyframeJson) Then
                Using document = JsonDocument.Parse(keyframeJson)
                    Dim frames = document.RootElement.GetProperty("frames")
                    For Each frame In frames.EnumerateArray()
                        Dim timestamp As Double = -1
                        For Each propertyName In New String() {"best_effort_timestamp_time", "pts_time", "pkt_dts_time"}
                            timestamp = ParseProbeDouble(GetProbeString(frame, propertyName))
                            If timestamp >= 0 Then Exit For
                        Next
                        If timestamp >= 0 AndAlso timestamp < result.DurationSeconds Then result.Keyframes.Add(timestamp)
                    Next
                End Using
            End If
            result.Keyframes = result.Keyframes.Distinct().
                OrderBy(Function(value) value).ToList()
            Return result
        End Function

        Private Shared Function RunSegmentProbe(executable As String, arguments As IEnumerable(Of String), timeoutMs As Integer) As String
            Dim info As New ProcessStartInfo With {
                .FileName = executable,
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .StandardErrorEncoding = Encoding.UTF8
            }
            PortableRuntime.ConfigureProcess(info)
            For Each argument In arguments
                info.ArgumentList.Add(argument)
            Next
            Using child = Process.Start(info)
                If child Is Nothing Then Return ""
                ' 同时排空两个管道，让超时覆盖实际运行阶段，避免输出填满后互相等待。
                Dim outputTask = child.StandardOutput.ReadToEndAsync()
                Dim errorTask = child.StandardError.ReadToEndAsync()
                If Not child.WaitForExit(timeoutMs) Then
                    Try
                        child.Kill(True)
                    Catch
                    End Try
                    Return ""
                End If
                Dim output = outputTask.GetAwaiter().GetResult()
                Dim errorText = errorTask.GetAwaiter().GetResult()
                If child.ExitCode <> 0 Then
                    Trace.WriteLine("[VideoEnhancer][分段] ffprobe 失败：" & errorText)
                    Return ""
                End If
                Return output
            End Using
        End Function

        Private Shared Function GetProbeString(element As JsonElement, propertyName As String) As String
            Dim value As JsonElement
            If Not element.TryGetProperty(propertyName, value) Then Return ""
            If value.ValueKind = JsonValueKind.String Then Return If(value.GetString(), "")
            If value.ValueKind = JsonValueKind.Number Then Return value.GetRawText()
            Return ""
        End Function

        Private Shared Function ParseProbeDouble(value As String) As Double
            Dim result As Double
            Return If(Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, result), result, -1)
        End Function

        Private Shared Function ParseSegmentRate(value As String) As Double
            If String.IsNullOrWhiteSpace(value) Then Return 0
            Dim parts = value.Split("/"c)
            If parts.Length = 2 Then
                Dim numerator As Double
                Dim denominator As Double
                If Double.TryParse(parts(0), NumberStyles.Float, CultureInfo.InvariantCulture, numerator) AndAlso
                   Double.TryParse(parts(1), NumberStyles.Float, CultureInfo.InvariantCulture, denominator) AndAlso
                   denominator <> 0 Then Return numerator / denominator
            End If
            Return Math.Max(0, ParseProbeDouble(value))
        End Function

        Friend Shared Function ProbeSegmentFrameCount(ffprobe As String, source As String) As Long
            If String.IsNullOrWhiteSpace(ffprobe) OrElse Not File.Exists(ffprobe) Then Return 0
            Dim output = RunSegmentProbe(ffprobe, New String() {
                "-v", "error", "-select_streams", "v:0", "-count_frames",
                "-show_entries", "stream=nb_read_frames,nb_frames", "-of", "json", source
            }, 300000)
            If String.IsNullOrWhiteSpace(output) Then Return 0
            Using document = JsonDocument.Parse(output)
                Dim streams = document.RootElement.GetProperty("streams")
                If streams.GetArrayLength() = 0 Then Return 0
                Dim stream = streams(0)
                For Each propertyName In New String() {"nb_read_frames", "nb_frames"}
                    Dim frames As Long
                    If Long.TryParse(GetProbeString(stream, propertyName), NumberStyles.Integer, CultureInfo.InvariantCulture, frames) AndAlso frames > 0 Then
                        Return frames
                    End If
                Next
            End Using
            Return 0
        End Function

    End Class
End Namespace
