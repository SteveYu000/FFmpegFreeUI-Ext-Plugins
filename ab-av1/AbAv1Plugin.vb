Imports System.Collections.Concurrent
Imports System.Linq
Imports System.Windows.Forms
Imports FFmpegFreeUI.Ext.PluginSdk

''' <summary>
''' 在 FFmpegFreeUI 原生质量页中提供“使用目标分数”模式，并在原生任务准备阶段运行 ab-av1。
''' </summary>
Public NotInheritable Class AbAv1Plugin
    Implements IExtFFmpegFreeUIPlugin

    Friend Const PluginId As String = "ffmpegfreeui.ext.ab-av1"
    Friend Const QualityChoiceId As String = PluginId & ".target-vmaf"
    Private Shared ReadOnly RequiredApiVersion As New Version(2, 5, 0)

    Private ReadOnly _registrations As New List(Of IDisposable)()
    Private ReadOnly _settingsPanels As New ConcurrentDictionary(Of String, QualitySettingsPanel)(
        StringComparer.OrdinalIgnoreCase)
    Private _host As IExtFFmpegFreeUIHost

    Public ReadOnly Property Id As String Implements IExtFFmpegFreeUIPlugin.Id
        Get
            Return PluginId
        End Get
    End Property

    Public ReadOnly Property DisplayName As String Implements IExtFFmpegFreeUIPlugin.DisplayName
        Get
            Return "AB-AV1"
        End Get
    End Property

    Public Sub Initialize(host As IExtFFmpegFreeUIHost) Implements IExtFFmpegFreeUIPlugin.Initialize
        If host Is Nothing Then Throw New ArgumentNullException(NameOf(host))
        If host.ApiVersion < RequiredApiVersion Then
            Throw New NotSupportedException(
                $"AB-AV1 插件需要 Ext Plugin API {RequiredApiVersion} 或更高版本，当前为 {host.ApiVersion}。")
        End If

        EnsureCapability(
            host.Ui.AvailableChoiceAnchors,
            ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityMode,
            "原生质量控制方式下拉框扩展")
        EnsureCapability(
            host.Ui.AvailableAnchors,
            ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityParameterName,
            "原生质量指标下拉框")
        EnsureCapability(
            host.Ui.AvailableAnchors,
            ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityAfterGlobal,
            "原生质量页参数插槽")
        EnsureCapability(
            host.Ui.AvailableAnchors,
            ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityValue,
            "原生质量值状态位置")
        If host.Commands Is Nothing Then
            Throw New NotSupportedException("当前 FFmpegFreeUI 未提供命令模板步骤接口。")
        End If
        If host.PresetOverview Is Nothing Then
            Throw New NotSupportedException("当前 FFmpegFreeUI 未提供预设总览行接口。")
        End If
        EnsureCapability(
            host.Pipeline.AvailableStages,
            ExtFFmpegFreeUIPipelineStages.PresetAfterCapture,
            "预设捕获后清理阶段")
        EnsureCapability(
            host.Pipeline.AvailableStages,
            ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,
            "任务准备前处理阶段")
        _host = host
        RegisterQualityChoice(host)
        RegisterSettingsPanel(host)
        RegisterPresetOverview(host)
        RegisterSearchCommandPreview(host)
        RegisterSearchPipeline(host)

        host.Log(
            ExtPluginLogLevel.Information,
            $"{DisplayName} 已加载；API={host.ApiVersion}，FFmpegFreeUI={host.HostVersion}。")
    End Sub

    Private Sub RegisterQualityChoice(host As IExtFFmpegFreeUIHost)
        Dim choice As New ExtPluginUiChoiceExtension(
            "target-vmaf-choice",
            ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityMode,
            QualityChoiceId,
            "使用目标分数（ab-av1）",
            ExtFFmpegFreeUIUiChoices.VideoQualityCrf) With {
            .Order = 100,
            .RestoreSelection =
                Function(context) AbAv1PluginState.Deserialize(context.StateJson).Enabled,
            .SelectionChanged = AddressOf QualityChoiceSelectionChanged
        }

        _registrations.Add(host.Ui.RegisterChoice(choice))
    End Sub

    Private Sub RegisterSettingsPanel(host As IExtFFmpegFreeUIHost)
        Dim extension As New ExtPluginUiExtension(
            "target-vmaf-settings",
            ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityAfterGlobal,
            AddressOf CreateSettingsPanel) With {
            .Order = 100,
            .Cleanup = AddressOf CleanupSettingsPanel,
            .ResourceId = ExtFFmpegFreeUIPluginResources.ParametersVideoQualityFields,
            .ResourceAccess = ExtPluginResourceAccess.OrderedTransform
        }
        _registrations.Add(host.Ui.Register(extension))
    End Sub

    Private Sub RegisterPresetOverview(host As IExtFFmpegFreeUIHost)
        Dim provider As New ExtPluginPresetOverviewRowProvider(
            "target-vmaf-overview",
            AddressOf BuildPresetOverviewRows) With {
            .Order = 100
        }
        _registrations.Add(host.PresetOverview.RegisterRowProvider(provider))
    End Sub

    Private Sub RegisterSearchCommandPreview(host As IExtFFmpegFreeUIHost)
        Dim provider As New ExtPluginCommandStepProvider(
            "target-vmaf-command-preview",
            AddressOf BuildSearchCommandPreview) With {
            .Order = 100
        }
        _registrations.Add(host.Commands.RegisterStepProvider(provider))
    End Sub

    Private Sub RegisterSearchPipeline(host As IExtFFmpegFreeUIHost)
        Dim captureHandler As New ExtPluginPipelineHandler(
            "sanitize-target-score-fields",
            ExtFFmpegFreeUIPipelineStages.PresetAfterCapture,
            AddressOf SanitizeCapturedScoreFieldsAsync) With {
            .Order = 100,
            .ResourceId = ExtFFmpegFreeUIPluginResources.PresetDocument,
            .ResourceAccess = ExtPluginResourceAccess.OrderedTransform
        }
        _registrations.Add(host.Pipeline.Register(captureHandler))

        Dim handler As New ExtPluginPipelineHandler(
            "search-target-vmaf-crf",
            ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,
            AddressOf SearchBeforePrepareAsync) With {
            .Order = 100
        }
        _registrations.Add(host.Pipeline.Register(handler))
    End Sub

    Private Function CreateSettingsPanel(context As IExtPluginUiContext) As Control
        Dim previous As QualitySettingsPanel = Nothing
        If _settingsPanels.TryRemove(context.SurfaceId, previous) AndAlso
           previous IsNot Nothing AndAlso
           Not previous.IsDisposed Then
            previous.Dispose()
        End If

        Dim panel As New QualitySettingsPanel(context)
        _settingsPanels(context.SurfaceId) = panel
        Return panel
    End Function

    Private Sub CleanupSettingsPanel(context As IExtPluginUiContext)
        Dim panel As QualitySettingsPanel = Nothing
        If _settingsPanels.TryRemove(context.SurfaceId, panel) AndAlso
           panel IsNot Nothing AndAlso
           Not panel.IsDisposed Then
            panel.Dispose()
        End If
    End Sub

    Private Sub QualityChoiceSelectionChanged(context As IExtPluginUiChoiceContext, selected As Boolean)
        Dim state = AbAv1PluginState.Deserialize(context.StateJson)
        state.Enabled = selected
        context.StateJson = state.Serialize()

        Dim panel As QualitySettingsPanel = Nothing
        If _settingsPanels.TryGetValue(context.SurfaceId, panel) Then panel.SetActive(selected)
        ' RegisterChoice 宿主会在回调后统一刷新参数，这里不重复触发整页重算。
    End Sub

    Private Shared Sub BuildPresetOverviewRows(context As ExtPluginPresetOverviewContext)
        If context Is Nothing OrElse
           Not AbAv1PluginState.HasStoredState(context.PluginStateJson) Then Return

        Dim state = AbAv1PluginState.Deserialize(context.PluginStateJson)
        Dim order = 0
        For Each line In state.ToOverviewLines()
            context.Rows.Add(New ExtPluginPresetOverviewRow(line) With {
                .Order = order,
                .Level = ExtPluginPresetOverviewRowLevel.Normal
            })
            order += 1
        Next
    End Sub

    Private Sub BuildSearchCommandPreview(context As ExtPluginCommandContext)
        If context Is Nothing OrElse Not context.IsPreview Then Return

        Dim state = AbAv1PluginState.Deserialize(context.PluginStateJson)
        If Not state.Enabled Then Return

        Try
            Dim settings = state.ToSearchSettings()
            Dim profile = PresetProfile.LoadJsonForPreview(context.PresetJson)
            Dim arguments = profile.BuildSearchArgumentTemplate(settings, jsonOutput:=False)
            context.Steps.Add(New ExtPluginCommandStep(
                "ab-av1-crf-search-preview",
                $"AB-AV1 {settings.MetricDisplayName} CRF 搜索",
                PluginEnvironment.AbAv1Path,
                AbAv1Runner.FormatArgumentList(arguments)) With {
                .Placement = ExtPluginCommandStepPlacement.BeforeNative,
                .Order = 100,
                .IncludeInPreview = True,
                .WorkingDirectory = PluginEnvironment.PluginDirectory
            })
        Catch ex As Exception
            Log(ExtPluginLogLevel.Warning, "无法生成 AB-AV1 命令行模板：" & ex.Message, ex)
        End Try
    End Sub

    Private Shared Sub EnsureCapability(
        available As IEnumerable(Of String),
        required As String,
        description As String)

        If available.Contains(required, StringComparer.OrdinalIgnoreCase) Then Return
        Throw New NotSupportedException($"当前 FFmpegFreeUI 未提供{description}：{required}。")
    End Sub

    Friend Sub Log(level As ExtPluginLogLevel, message As String, Optional exception As Exception = Nothing)
        _host?.Log(level, message, exception)
    End Sub

End Class
