Imports System.Globalization
Imports System.Text.Json.Nodes
Imports System.Threading
Imports System.Threading.Tasks
Imports FFmpegFreeUI.Ext.PluginSdk

Partial Public NotInheritable Class AbAv1Plugin

    Private Function SanitizeCapturedScoreFieldsAsync(
        context As ExtPluginPipelineContext,
        cancellationToken As CancellationToken) As ValueTask

        cancellationToken.ThrowIfCancellationRequested()
        Dim state = AbAv1PluginState.ReadFromPreset(context.PresetJson, PluginId)
        If Not state.Enabled Then Return ValueTask.CompletedTask

        Dim root = TryCast(JsonNode.Parse(context.PresetJson), JsonObject)
        If root Is Nothing Then
            Throw New InvalidOperationException("3FUI 捕获的预设不是有效 JSON 对象。")
        End If

        ' 原生第二、第三栏在插件模式下只承担指标与目标分数的 UI；真正数据保存在插件状态中。
        ' 捕获后清空原生 FFmpeg 字段，防止预览或保存时把 VMAF/XPSNR 当成编码器参数。
        root("视频参数_质量控制_参数名") = String.Empty
        root("视频参数_质量控制_值") = String.Empty
        context.PresetJson = root.ToJsonString()
        Return ValueTask.CompletedTask
    End Function

    Private Function SearchBeforePrepareAsync(
        context As ExtPluginPipelineContext,
        cancellationToken As CancellationToken) As ValueTask

        Return New ValueTask(SearchBeforePrepareCoreAsync(context, cancellationToken))
    End Function

    Private Async Function SearchBeforePrepareCoreAsync(
        context As ExtPluginPipelineContext,
        cancellationToken As CancellationToken) As Task

        Dim state = AbAv1PluginState.ReadFromPreset(context.PresetJson, PluginId)
        If Not state.Enabled Then Return

        cancellationToken.ThrowIfCancellationRequested()
        Dim settings = state.ToSearchSettings()
        Dim profile = PresetProfile.LoadJson(context.PresetJson)
        Dim inputPath = If(context.InputPath, String.Empty).Trim()
        If inputPath = String.Empty Then
            Throw New InvalidOperationException("AB-AV1 搜索任务没有输入文件。")
        End If

        context.ReportProgress(
            $"AB-AV1：搜索目标 {settings.MetricDisplayName} {SearchSettings.FormatNumber(settings.TargetScore)} 对应的 CRF……",
            0.01)

        Dim reportLock As New Object()
        Dim progress As New InlineProgress(Of SearchProgress)(
            Sub(update)
                If update Is Nothing OrElse String.IsNullOrWhiteSpace(update.Message) Then Return
                SyncLock reportLock
                    context.ReportProgress("AB-AV1：" & update.Message)
                End SyncLock
            End Sub)

        Try
            Dim runner As New AbAv1Runner()
            Dim result = Await runner.SearchAsync(
                profile,
                inputPath,
                settings,
                progress,
                cancellationToken).ConfigureAwait(False)

            cancellationToken.ThrowIfCancellationRequested()
            context.PresetJson = profile.ApplyCrf(result.Crf)
            context.ReportResult(
                "search.crf",
                SearchSettings.FormatNumber(result.Crf),
                "AB-AV1 CRF")
            context.ReportResult(
                "search." & SearchSettings.GetMetricId(result.Metric),
                result.Score.ToString("0.###", CultureInfo.InvariantCulture),
                "AB-AV1 " & SearchSettings.GetMetricDisplayName(result.Metric))

            If result.PredictedEncodeSize > 0 Then
                context.ReportResult(
                    "search.predicted-video-bytes",
                    result.PredictedEncodeSize.ToString(CultureInfo.InvariantCulture),
                    "预测视频流大小",
                    "bytes")
            End If
            If result.PredictedEncodeSeconds > 0 Then
                context.ReportResult(
                    "search.predicted-encode-seconds",
                    result.PredictedEncodeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    "预测编码时长",
                    "s")
            End If

            context.ReportProgress(
                $"AB-AV1：已找到 CRF {SearchSettings.FormatNumber(result.Crf)}（{SearchSettings.GetMetricDisplayName(result.Metric)} {result.Score:0.###}），开始原生编码。",
                1)
            Log(
                ExtPluginLogLevel.Information,
                $"任务 {context.TaskId} 的 AB-AV1 搜索完成：CRF={SearchSettings.FormatNumber(result.Crf)}，{SearchSettings.GetMetricDisplayName(result.Metric)}={result.Score:0.###}。")
        Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
            Log(ExtPluginLogLevel.Information, $"任务 {context.TaskId} 的 AB-AV1 搜索已取消。")
            Throw
        Catch ex As Exception
            Log(ExtPluginLogLevel.Error, $"任务 {context.TaskId} 的 AB-AV1 搜索失败。", ex)
            Throw New InvalidOperationException("AB-AV1 CRF 搜索失败：" & ex.Message, ex)
        End Try
    End Function

End Class
