Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks

Partial Public NotInheritable Class AbAv1Runner

    Private Shared ReadOnly HumanSampleResultRegex As New Regex(
        "\b(?<metric>VMAF|XPSNR)\s+(?<score>[+-]?(?:\d+(?:\.\d+)?|inf(?:inity)?))\s+predicted\s+(?:video\s+stream|image)\s+size\s+(?<size>\d+(?:\.\d+)?)\s+(?<unit>[KMGTPE]?i?B)\b",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant Or RegexOptions.IgnoreCase)

    Public Shared Async Function BuildSampleCommandLineAsync(profile As PresetProfile,
                                                              inputPath As String,
                                                              settings As SampleEncodeSettings,
                                                              cancellationToken As CancellationToken) As Task(Of String)
        Dim executable = GetExecutablePath()
        Dim useJson = Await SupportsJsonOutputAsync(executable, cancellationToken).ConfigureAwait(False)
        Return FormatCommandLine(executable, profile.BuildSampleEncodeArguments(inputPath, settings, useJson))
    End Function

    Public Shared Async Function BuildSampleCommandLineTemplateAsync(profile As PresetProfile,
                                                                      settings As SampleEncodeSettings,
                                                                      cancellationToken As CancellationToken) As Task(Of String)
        Dim executable = GetExecutablePath()
        Dim useJson = Await SupportsJsonOutputAsync(executable, cancellationToken).ConfigureAwait(False)
        Return FormatCommandLine(executable, profile.BuildSampleEncodeArgumentTemplate(settings, useJson))
    End Function

    Public Async Function SampleEncodeAsync(profile As PresetProfile,
                                             inputPath As String,
                                             settings As SampleEncodeSettings,
                                             progress As IProgress(Of SearchProgress),
                                             cancellationToken As CancellationToken) As Task(Of SampleEncodeResult)
        Dim executable = GetExecutablePath()
        Dim useJson = Await SupportsJsonOutputAsync(executable, cancellationToken).ConfigureAwait(False)
        Dim arguments = profile.BuildSampleEncodeArguments(inputPath, settings, useJson)
        SetCurrentCommandLine(FormatCommandLine(executable, arguments))
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
        For Each argument In arguments
            startInfo.ArgumentList.Add(argument)
        Next

        cancellationToken.ThrowIfCancellationRequested()
        Using process As New Process With {.StartInfo = startInfo}
            If Not process.Start() Then Throw New InvalidOperationException("无法启动 ab-av1.exe。")
            RegisterProcess(process)
            Dim stderr As New ConcurrentQueue(Of String)()
            Dim stdoutTask = ReadSampleStdoutAsync(process.StandardOutput, settings, useJson, progress, cancellationToken)
            Dim stderrTask = ReadStderrAsync(process.StandardError, stderr, progress, cancellationToken)
            Try
                Await process.WaitForExitAsync(cancellationToken).ConfigureAwait(False)
                Dim result = Await stdoutTask.ConfigureAwait(False)
                Await stderrTask.ConfigureAwait(False)
                If process.ExitCode <> 0 Then Throw New InvalidOperationException(BuildFailureMessage(process.ExitCode, stderr))
                If result Is Nothing Then Throw New InvalidDataException("ab-av1 已结束，但没有输出可识别的样本编码结果。")
                Return result
            Finally
                ' 停止、取消及解析失败都会终止整个进程树，暂停中的子进程也不能遗留。
                TerminateProcess(process)
                UnregisterProcess(process)
            End Try
        End Using
    End Function

    Private Shared Async Function ReadSampleStdoutAsync(reader As StreamReader,
                                                         settings As SampleEncodeSettings,
                                                         jsonOutput As Boolean,
                                                         progress As IProgress(Of SearchProgress),
                                                         cancellationToken As CancellationToken) As Task(Of SampleEncodeResult)
        Dim result As SampleEncodeResult = Nothing
        While True
            Dim line = Await reader.ReadLineAsync(cancellationToken).ConfigureAwait(False)
            If line Is Nothing Then Exit While
            line = StripTerminalFormatting(line).Trim()
            If line = String.Empty Then Continue While
            Dim parsed As SampleEncodeResult = Nothing
            If jsonOutput Then
                parsed = ParseSampleJson(line, settings)
            Else
                Dim match = HumanSampleResultRegex.Match(line)
                Dim metric As QualityScoreMetric
                Dim score, size As Double
                If match.Success AndAlso
                   SearchSettings.TryParseMetric(match.Groups("metric").Value, metric) AndAlso metric = settings.Metric AndAlso
                   TryParseInvariant(match.Groups("score").Value, score) AndAlso
                   TryParseInvariant(match.Groups("size").Value, size) Then
                    parsed = New SampleEncodeResult With {
                        .Crf = settings.Crf, .Metric = metric, .Score = score,
                        .PredictedEncodeSize = ConvertHumanBytes(size, match.Groups("unit").Value)
                    }
                End If
            End If
            If parsed IsNot Nothing Then
                result = parsed
                progress?.Report(New SearchProgress(
                    $"CRF {FormatNumber(parsed.Crf)} 样本完成 · {SearchSettings.GetMetricDisplayName(parsed.Metric)} {FormatScore(parsed.Score)}",
                    parsed.Crf, parsed.Score, parsed.Metric))
            ElseIf Not jsonOutput Then
                progress?.Report(New SearchProgress(line))
            End If
        End While
        Return result
    End Function

    Friend Shared Function ParseSampleJson(line As String, settings As SampleEncodeSettings) As SampleEncodeResult
        Try
            Using document = JsonDocument.Parse(line)
                Dim root = document.RootElement
                Dim eventType = GetString(root, "type")
                If eventType.EndsWith("-error", StringComparison.Ordinal) Then
                    Throw New InvalidOperationException(If(GetString(root, "message"), "样本编码失败。"))
                End If
                ' 兼容旧版直接输出结果对象、不含 type 和 crf 的 JSON 协议。
                If eventType <> "sample-encode-done" AndAlso
                   Not (eventType = String.Empty AndAlso GetOptionalInt64(root, "predicted_encode_size").HasValue) Then Return Nothing
                Dim score = GetOptionalDouble(root, SearchSettings.GetMetricId(settings.Metric))
                If Not score.HasValue OrElse Double.IsNaN(score.Value) Then
                    Throw New InvalidDataException($"ab-av1 JSON 结果缺少有效的 {SearchSettings.GetMetricDisplayName(settings.Metric)} 分数。")
                End If
                Dim crf = GetOptionalDouble(root, "crf").GetValueOrDefault(settings.Crf)
                If Not Double.IsFinite(crf) OrElse crf < 0 Then Throw New InvalidDataException("ab-av1 返回了无效的 CRF。")
                Return New SampleEncodeResult With {
                    .Crf = crf,
                    .Metric = settings.Metric,
                    .Score = score.Value,
                    .PredictedEncodeSize = GetOptionalInt64(root, "predicted_encode_size").GetValueOrDefault(),
                    .PredictedEncodeSeconds = GetOptionalDouble(root, "predicted_encode_seconds").GetValueOrDefault(),
                    .PredictedEncodePercent = GetOptionalDouble(root, "predicted_encode_percent").GetValueOrDefault()
                }
            End Using
        Catch ex As JsonException
            Throw New InvalidDataException($"无法解析 ab-av1 JSON 输出：{line}", ex)
        End Try
    End Function

End Class
