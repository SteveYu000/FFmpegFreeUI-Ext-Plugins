Imports System.Globalization
Imports System.IO

Public Enum QualityScoreMetric
    Vmaf
    Xpsnr
End Enum

Public NotInheritable Class SearchSettings

    Public Property Metric As QualityScoreMetric = QualityScoreMetric.Vmaf

    Public Property TargetScore As Double = 95

    Public Property MinCrf As Double = 5

    Public Property MaxCrf As Double = 55

    Public Property Samples As Integer?

    Public Property SampleDuration As String = "20s"

    Public Property Thorough As Boolean

    ''' <summary>
    ''' VMAF 内置模型名称或本地 JSON 路径。留空时完全采用 ab-av1 的自动模型逻辑。
    ''' </summary>
    Public Property VmafModel As String = String.Empty

    Public ReadOnly Property MetricDisplayName As String
        Get
            Return GetMetricDisplayName(Metric)
        End Get
    End Property

    Public Sub Validate()
        If Double.IsNaN(TargetScore) OrElse Double.IsInfinity(TargetScore) Then
            Throw New ArgumentOutOfRangeException(NameOf(TargetScore), "目标分数必须是有限数字。")
        End If
        If Metric = QualityScoreMetric.Vmaf AndAlso (TargetScore <= 0 OrElse TargetScore > 100) Then
            Throw New ArgumentOutOfRangeException(NameOf(TargetScore), "目标 VMAF 必须大于 0 且不超过 100。")
        End If

        If Double.IsNaN(MinCrf) OrElse Double.IsNaN(MaxCrf) OrElse MinCrf < 0 OrElse MinCrf >= MaxCrf Then
            Throw New ArgumentException("CRF 范围无效：最小值必须大于等于 0，并且小于最大值。")
        End If

        If Samples.HasValue AndAlso Samples.Value <= 0 Then
            Throw New ArgumentOutOfRangeException(NameOf(Samples), "采样数量必须是正整数。")
        End If

        If String.IsNullOrWhiteSpace(SampleDuration) Then
            Throw New ArgumentException("采样时长不能为空。", NameOf(SampleDuration))
        End If

        If Metric = QualityScoreMetric.Vmaf Then
            Dim model = If(VmafModel, String.Empty).Trim()
            Dim modelPath = model
            If model.StartsWith("path=", StringComparison.OrdinalIgnoreCase) Then
                modelPath = model.Substring("path=".Length).Trim()
            End If
            If modelPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) AndAlso Not File.Exists(modelPath) Then
                Throw New FileNotFoundException("找不到手动指定的 VMAF 模型文件。", modelPath)
            End If
        End If
    End Sub

    Public Shared Function TryParseMetric(value As String,
                                          ByRef metric As QualityScoreMetric) As Boolean
        Select Case If(value, String.Empty).Trim().ToLowerInvariant()
            Case "vmaf"
                metric = QualityScoreMetric.Vmaf
                Return True
            Case "xpsnr"
                metric = QualityScoreMetric.Xpsnr
                Return True
            Case Else
                metric = QualityScoreMetric.Vmaf
                Return False
        End Select
    End Function

    Public Shared Function ParseMetric(value As String) As QualityScoreMetric
        Dim metric As QualityScoreMetric
        If TryParseMetric(value, metric) Then Return metric
        Return QualityScoreMetric.Vmaf
    End Function

    Public Shared Function GetMetricId(metric As QualityScoreMetric) As String
        Return If(metric = QualityScoreMetric.Xpsnr, "xpsnr", "vmaf")
    End Function

    Public Shared Function GetMetricDisplayName(metric As QualityScoreMetric) As String
        Return If(metric = QualityScoreMetric.Xpsnr, "XPSNR", "VMAF")
    End Function

    Public Shared Function FormatNumber(value As Double) As String
        Return value.ToString("0.###", CultureInfo.InvariantCulture)
    End Function

End Class

Public NotInheritable Class SearchResult

    Public Property Crf As Double

    Public Property Metric As QualityScoreMetric

    Public Property Score As Double

    Public Property PredictedEncodeSize As Long

    Public Property PredictedEncodeSeconds As Double

End Class

Public NotInheritable Class SearchProgress

    Public Sub New(message As String,
                   Optional testedCrf As Double? = Nothing,
                   Optional testedScore As Double? = Nothing,
                   Optional metric As QualityScoreMetric = QualityScoreMetric.Vmaf)
        Me.Message = message
        Me.TestedCrf = testedCrf
        Me.TestedScore = testedScore
        Me.Metric = metric
    End Sub

    Public ReadOnly Property Message As String

    Public ReadOnly Property TestedCrf As Double?

    Public ReadOnly Property TestedScore As Double?

    Public ReadOnly Property Metric As QualityScoreMetric

End Class

Public NotInheritable Class PresetCompatibilityException
    Inherits InvalidOperationException

    Public Sub New(message As String)
        MyBase.New(message)
    End Sub
End Class
