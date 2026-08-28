Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.Json.Serialization

Friend NotInheritable Class AbAv1PluginState

    Public Property Version As Integer = 2

    Public Property Enabled As Boolean

    Public Property TargetVmaf As Double = 95

    Public Property TargetXpsnr As Double = 40

    Public Property ScoreMetric As String = "vmaf"

    Public Property MinCrf As Double = 5

    Public Property MaxCrf As Double = 55

    Public Property Samples As Integer?

    Public Property SampleDuration As String = "20s"

    Public Property Thorough As Boolean

    Public Property VmafModel As String = String.Empty

    <JsonIgnore>
    Public Property Metric As QualityScoreMetric
        Get
            Return SearchSettings.ParseMetric(ScoreMetric)
        End Get
        Set(value As QualityScoreMetric)
            ScoreMetric = SearchSettings.GetMetricId(value)
        End Set
    End Property

    <JsonIgnore>
    Public Property TargetScore As Double
        Get
            Return If(Metric = QualityScoreMetric.Xpsnr, TargetXpsnr, TargetVmaf)
        End Get
        Set(value As Double)
            If Metric = QualityScoreMetric.Xpsnr Then
                TargetXpsnr = value
            Else
                TargetVmaf = value
            End If
        End Set
    End Property

    Public Function ToSearchSettings() As SearchSettings
        Dim result As New SearchSettings With {
            .Metric = Metric,
            .TargetScore = TargetScore,
            .MinCrf = MinCrf,
            .MaxCrf = MaxCrf,
            .Samples = Samples,
            .SampleDuration = If(SampleDuration, String.Empty).Trim(),
            .Thorough = Thorough,
            .VmafModel = If(VmafModel, String.Empty).Trim()
        }
        result.Validate()
        Return result
    End Function

    Public Shared Function Deserialize(json As String) As AbAv1PluginState
        If String.IsNullOrWhiteSpace(json) Then Return New AbAv1PluginState()

        Try
            Dim state = JsonSerializer.Deserialize(Of AbAv1PluginState)(
                json,
                New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True})
            If state Is Nothing Then Return New AbAv1PluginState()
            state.Normalize()
            Return state
        Catch ex As JsonException
            Return New AbAv1PluginState()
        End Try
    End Function

    Public Function Serialize() As String
        Normalize()
        Return JsonSerializer.Serialize(Me)
    End Function

    Public Function ToOverviewText() As String
        Dim samplesText = If(
            Samples.HasValue,
            Samples.Value.ToString(Globalization.CultureInfo.InvariantCulture),
            "自动")
        Dim parts As New List(Of String) From {
            $"目标 {SearchSettings.GetMetricDisplayName(Metric)} {SearchSettings.FormatNumber(TargetScore)}",
            $"CRF {SearchSettings.FormatNumber(MinCrf)}–{SearchSettings.FormatNumber(MaxCrf)}",
            $"采样 {samplesText}",
            $"单段 {If(SampleDuration, String.Empty).Trim()}",
            $"彻底搜索 {If(Thorough, "是", "否")}"
        }
        If Metric = QualityScoreMetric.Vmaf Then
            Dim modelText = If(String.IsNullOrWhiteSpace(VmafModel), "自动", VmafModel.Trim())
            parts.Add($"模型 {modelText}")
        End If
        Return String.Join("  ·  ", parts)
    End Function

    Public Function ToOverviewLines() As IReadOnlyList(Of String)
        Dim samplesText = If(
            Samples.HasValue,
            Samples.Value.ToString(Globalization.CultureInfo.InvariantCulture),
            "自动")
        Dim lines As New List(Of String) From {
            $"AB-AV1 目标 {SearchSettings.GetMetricDisplayName(Metric)}：{SearchSettings.FormatNumber(TargetScore)}",
            $"AB-AV1 最小 CRF：{SearchSettings.FormatNumber(MinCrf)}",
            $"AB-AV1 最大 CRF：{SearchSettings.FormatNumber(MaxCrf)}",
            $"AB-AV1 采样数量：{samplesText}",
            $"AB-AV1 单段时长：{If(SampleDuration, String.Empty).Trim()}",
            $"AB-AV1 彻底搜索：{If(Thorough, "是", "否")}"
        }
        If Metric = QualityScoreMetric.Vmaf Then
            Dim modelText = If(String.IsNullOrWhiteSpace(VmafModel), "自动", VmafModel.Trim())
            lines.Add($"AB-AV1 VMAF 模型：{modelText}")
        End If
        Return lines
    End Function

    Public Shared Function HasStoredState(json As String) As Boolean
        If String.IsNullOrWhiteSpace(json) Then Return False
        Try
            Dim value = TryCast(JsonNode.Parse(json), JsonObject)
            Return value IsNot Nothing AndAlso value.Count > 0
        Catch ex As JsonException
            Return False
        End Try
    End Function

    Public Shared Function ReadFromPreset(presetJson As String, pluginId As String) As AbAv1PluginState
        If String.IsNullOrWhiteSpace(presetJson) Then Return New AbAv1PluginState()

        Try
            Dim root = TryCast(JsonNode.Parse(presetJson), JsonObject)
            Dim extensionData = TryCast(root?("插件扩展数据"), JsonObject)
            If extensionData Is Nothing Then Return New AbAv1PluginState()

            Dim stateNode As JsonNode = Nothing
            For Each pair In extensionData
                If String.Equals(pair.Key, pluginId, StringComparison.OrdinalIgnoreCase) Then
                    stateNode = pair.Value
                    Exit For
                End If
            Next
            If stateNode Is Nothing Then Return New AbAv1PluginState()

            Dim stateValue = TryCast(stateNode, JsonValue)
            If stateValue IsNot Nothing Then
                Dim stateJson As String = Nothing
                If stateValue.TryGetValue(stateJson) Then Return Deserialize(stateJson)
            End If
            Return Deserialize(stateNode.ToJsonString())
        Catch ex As JsonException
            Return New AbAv1PluginState()
        End Try
    End Function

    Private Sub Normalize()
        Version = 2
        ScoreMetric = SearchSettings.GetMetricId(SearchSettings.ParseMetric(ScoreMetric))
        SampleDuration = If(SampleDuration, String.Empty)
        VmafModel = If(VmafModel, String.Empty)
    End Sub

End Class
