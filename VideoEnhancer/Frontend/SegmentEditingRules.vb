Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq

Namespace videoenhancer
    Friend NotInheritable Class SegmentEditingRules
        Private Shared Function IsSegmentModelBackend(backend As String) As Boolean
            Dim value = If(backend, "").Trim().ToLowerInvariant()
            Return value = "ncnn" OrElse value = "cuda" OrElse value = "tensorrt" OrElse value = "onnx"
        End Function

        Private Shared Function IsSegmentCustomBackend(backend As String) As Boolean
            Dim value = If(backend, "").Trim().ToLowerInvariant()
            Return value = "ffmpeg" OrElse value = "anime4k"
        End Function
        Friend Shared Sub ConvertSegmentSecondsToFrames(config As SegmentedVideoConfig)
            If config.FrameCount <= 0 OrElse config.DurationSeconds <= 0 Then Return
            Dim expected As Long = 1
            For index = 0 To config.Segments.Count - 1
                Dim segment = config.Segments(index)
                segment.Start = expected
                If index = config.Segments.Count - 1 Then
                    segment.[End] = config.FrameCount
                Else
                    segment.[End] = Math.Max(expected,
                        Math.Min(config.FrameCount - 1,
                            CLng(Math.Round(segment.EndSeconds / config.DurationSeconds * config.FrameCount))))
                End If
                expected = segment.[End] + 1
            Next
        End Sub

        Friend Shared Sub ConvertSegmentFramesToSeconds(config As SegmentedVideoConfig, probe As SegmentVideoProbe)
            If config.FrameCount <= 0 OrElse probe.DurationSeconds <= 0 Then Return
            Dim expected As Double = 0
            For index = 0 To config.Segments.Count - 1
                Dim segment = config.Segments(index)
                segment.StartSeconds = expected
                If index = config.Segments.Count - 1 Then
                    segment.EndSeconds = probe.DurationSeconds
                Else
                    Dim desired = CDbl(segment.[End]) / config.FrameCount * probe.DurationSeconds
                    Dim snapped = SnapSegmentBoundary(probe, desired, expected, probe.DurationSeconds)
                    If snapped < 0 Then snapped = desired
                    segment.EndSeconds = snapped
                End If
                expected = segment.EndSeconds
            Next
            config.DurationSeconds = probe.DurationSeconds
            config.SourceWidth = probe.Width
            config.SourceHeight = probe.Height
            SnapAllSegmentBoundaries(config, probe)
            ApplySegmentResolutionRule(config)
        End Sub

        Friend Shared Function SnapSegmentBoundary(
            probe As SegmentVideoProbe, desired As Double, minimum As Double, maximum As Double) As Double
            If probe Is Nothing OrElse probe.Keyframes Is Nothing Then Return -1
            Dim candidates = probe.Keyframes.Where(
                Function(value) value > minimum + 0.001 AndAlso value < maximum - 0.001).ToList()
            If candidates.Count = 0 Then Return -1
            Return candidates.OrderBy(Function(value) Math.Abs(value - desired)).First()
        End Function

        Friend Shared Sub SnapAllSegmentBoundaries(config As SegmentedVideoConfig, probe As SegmentVideoProbe)
            If config Is Nothing OrElse probe Is Nothing OrElse config.Segments Is Nothing OrElse config.Segments.Count < 2 Then Return
            config.Segments(0).StartSeconds = 0
            For index = 0 To config.Segments.Count - 2
                Dim current = config.Segments(index)
                Dim following = config.Segments(index + 1)
                Dim minimum = current.StartSeconds
                Dim maximum = If(index + 1 = config.Segments.Count - 1, config.DurationSeconds, following.EndSeconds)
                Dim desired = If(current.EndSeconds > minimum AndAlso current.EndSeconds < maximum,
                    current.EndSeconds, minimum + (maximum - minimum) / 2)
                Dim snapped = SnapSegmentBoundary(probe, desired, minimum, maximum)
                If snapped >= 0 Then
                    current.EndSeconds = snapped
                    following.StartSeconds = snapped
                End If
            Next
            config.Segments(config.Segments.Count - 1).EndSeconds = config.DurationSeconds
        End Sub

        Friend Shared Function SegmentFixedScale(config As SegmentedVideoConfig) As Integer
            If config Is Nothing OrElse config.Segments Is Nothing Then Return 0
            Return config.Segments.Where(
                Function(segment) IsSegmentModelBackend(segment.Backend) AndAlso segment.Scale > 0).
                Select(Function(segment) segment.Scale).FirstOrDefault()
        End Function

        Friend Shared Sub ApplySegmentResolutionRule(config As SegmentedVideoConfig)
            If config Is Nothing OrElse config.Segments Is Nothing OrElse config.Segments.Count = 0 Then Return
            Dim fixedScale = SegmentFixedScale(config)
            Dim width As Integer
            Dim height As Integer
            If fixedScale > 0 AndAlso config.SourceWidth > 0 AndAlso config.SourceHeight > 0 Then
                width = config.SourceWidth * fixedScale
                height = config.SourceHeight * fixedScale
            Else
                Dim existing = config.Segments.FirstOrDefault(
                    Function(segment) segment.TargetWidth > 0 AndAlso segment.TargetHeight > 0)
                If existing IsNot Nothing Then
                    width = existing.TargetWidth
                    height = existing.TargetHeight
                Else
                    width = Math.Max(1, config.SourceWidth * 2)
                    height = Math.Max(1, config.SourceHeight * 2)
                End If
            End If
            For Each segment In config.Segments
                segment.TargetWidth = width
                segment.TargetHeight = height
            Next
        End Sub

        Friend Shared Function ValidateSegmentRanges(config As SegmentedVideoConfig) As String
            If config.Segments Is Nothing OrElse config.Segments.Count = 0 Then Return "至少添加一个分段"
            Dim secondsMode = String.Equals(config.BoundaryMode, "seconds", StringComparison.OrdinalIgnoreCase)
            If secondsMode AndAlso config.DurationSeconds <= 0 Then Return "视频时长无效"
            If Not secondsMode AndAlso config.FrameCount <= 0 Then Return "视频帧数无效"
            Dim expectedFrame As Long = 1
            Dim expectedSeconds As Double = 0
            Dim fixedScale As Integer = 0
            Dim firstModelBackend As String = ""
            Dim targetWidth As Integer = 0
            Dim targetHeight As Integer = 0
            For index = 0 To config.Segments.Count - 1
                Dim segment = config.Segments(index)
                If secondsMode Then
                    If Math.Abs(segment.StartSeconds - expectedSeconds) > 0.002 OrElse segment.EndSeconds <= segment.StartSeconds Then
                        Return $"第 {index + 1} 段秒级边界不连续"
                    End If
                    expectedSeconds = segment.EndSeconds
                Else
                    If segment.Start <> expectedFrame OrElse segment.[End] < segment.Start Then Return $"第 {index + 1} 段应从第 {expectedFrame} 帧开始"
                    expectedFrame = segment.[End] + 1
                End If
                If String.IsNullOrWhiteSpace(segment.Model) Then Return $"第 {index + 1} 段尚未选择处理方式"
                If IsSegmentModelBackend(segment.Backend) Then
                    If segment.Scale <= 0 Then Return $"第 {index + 1} 段模型倍率无效"
                    Dim currentBackend = segment.Backend.Trim().ToLowerInvariant()
                    If firstModelBackend.Length = 0 Then
                        firstModelBackend = currentBackend
                    ElseIf Not config.AllowMixedModelBackends AndAlso
                           Not String.Equals(currentBackend, firstModelBackend, StringComparison.OrdinalIgnoreCase) Then
                        Return "跨 NCNN / CUDA / TensorRT / ONNX 混用是测试功能，请先手动开启跨模型后端混用开关"
                    End If
                    If fixedScale = 0 Then
                        fixedScale = segment.Scale
                    ElseIf segment.Scale <> fixedScale Then
                        Return "固定倍率模型必须使用相同倍率"
                    End If
                ElseIf Not IsSegmentCustomBackend(segment.Backend) Then
                    Return $"第 {index + 1} 段处理方式不受支持"
                End If
                If targetWidth = 0 Then
                    targetWidth = segment.TargetWidth
                    targetHeight = segment.TargetHeight
                ElseIf segment.TargetWidth <> targetWidth OrElse segment.TargetHeight <> targetHeight Then
                    Return "整片视频必须使用统一输出分辨率"
                End If
            Next
            If secondsMode Then
                If Math.Abs(config.Segments(0).StartSeconds) > 0.002 OrElse
                   Math.Abs(config.Segments(config.Segments.Count - 1).EndSeconds - config.DurationSeconds) > 0.002 Then Return "必须自动覆盖完整视频时长"
            ElseIf config.Segments(0).Start <> 1 OrElse config.Segments(config.Segments.Count - 1).[End] <> config.FrameCount Then
                Return "必须覆盖全部帧"
            End If
            If targetWidth <= 0 OrElse targetHeight <= 0 Then Return "目标分辨率无效"
            If fixedScale > 0 AndAlso config.SourceWidth > 0 AndAlso config.SourceHeight > 0 AndAlso
               (targetWidth <> config.SourceWidth * fixedScale OrElse targetHeight <> config.SourceHeight * fixedScale) Then
                Return "固定倍率模型存在时，输出分辨率必须服从模型倍率"
            End If
            Return ""
        End Function

        Friend Shared Function ValidateSegmentedConfig(config As SegmentedVideoConfig) As String
            If config.Segments Is Nothing OrElse config.Segments.Count = 0 Then Return "至少需要一个分段"
            Dim secondsMode = String.Equals(config.BoundaryMode, "seconds", StringComparison.OrdinalIgnoreCase)
            If secondsMode AndAlso config.DurationSeconds <= 0 Then Return "尚未取得有效视频时长"
            If Not secondsMode AndAlso config.FrameCount <= 0 Then Return "尚未取得有效帧数"
            Dim expectedFrame As Long = 1
            Dim expectedSeconds As Double = 0
            Dim fixedScale As Integer = 0
            Dim firstModelBackend As String = ""
            Dim customWidth As Integer = 0
            Dim customHeight As Integer = 0
            For index = 0 To config.Segments.Count - 1
                Dim segment = config.Segments(index)
                If secondsMode Then
                    If Math.Abs(segment.StartSeconds - expectedSeconds) > 0.002 OrElse
                       segment.EndSeconds <= segment.StartSeconds Then
                        Return $"第 {index + 1} 段秒级边界不连续"
                    End If
                    expectedSeconds = segment.EndSeconds
                Else
                    If segment.Start <> expectedFrame OrElse segment.[End] < segment.Start Then
                        Return $"第 {index + 1} 段必须从第 {expectedFrame} 帧开始"
                    End If
                    expectedFrame = segment.[End] + 1
                End If
                If String.IsNullOrWhiteSpace(segment.Model) Then Return $"第 {index + 1} 段尚未选择处理方式"
                Dim currentBackend = If(segment.Backend, "").Trim().ToLowerInvariant()
                Dim modelBackend = currentBackend = "ncnn" OrElse currentBackend = "cuda" OrElse
                    currentBackend = "tensorrt" OrElse currentBackend = "onnx"
                If Not modelBackend AndAlso currentBackend <> "ffmpeg" AndAlso currentBackend <> "anime4k" Then
                    Return $"第 {index + 1} 段处理后端不受支持"
                End If
                If modelBackend Then
                    If segment.Scale <= 0 Then Return $"第 {index + 1} 段模型倍率无效"
                    If firstModelBackend.Length = 0 Then
                        firstModelBackend = currentBackend
                    ElseIf Not config.AllowMixedModelBackends AndAlso
                           Not String.Equals(currentBackend, firstModelBackend, StringComparison.OrdinalIgnoreCase) Then
                        Return "跨 NCNN / CUDA / TensorRT / ONNX 混用是测试功能，请先手动开启跨模型后端混用开关"
                    End If
                    If fixedScale = 0 Then
                        fixedScale = segment.Scale
                    ElseIf segment.Scale <> fixedScale Then
                        Return "所有固定倍率模型必须使用相同放大倍率"
                    End If
                ElseIf fixedScale = 0 AndAlso segment.TargetWidth > 0 AndAlso segment.TargetHeight > 0 Then
                    If customWidth = 0 Then
                        customWidth = segment.TargetWidth
                        customHeight = segment.TargetHeight
                    ElseIf segment.TargetWidth <> customWidth OrElse segment.TargetHeight <> customHeight Then
                        Return "仅使用 FFmpeg / Anime4K 时所有分段必须使用相同目标分辨率"
                    End If
                End If
            Next
            If secondsMode Then
                If Math.Abs(config.Segments(0).StartSeconds) > 0.002 OrElse
                   Math.Abs(config.Segments(config.Segments.Count - 1).EndSeconds - config.DurationSeconds) > 0.002 Then
                    Return $"必须完整覆盖 0 到 {config.DurationSeconds:0.###} 秒"
                End If
            ElseIf config.Segments(0).Start <> 1 OrElse
                   config.Segments(config.Segments.Count - 1).[End] <> config.FrameCount Then
                Return $"必须完整覆盖第 1 到第 {config.FrameCount} 帧"
            End If
            If fixedScale = 0 Then
                If customWidth <= 0 OrElse customHeight <= 0 Then
                    Return "仅使用 FFmpeg / Anime4K 时必须设置统一目标分辨率"
                End If
                For Each segment In config.Segments
                    If segment.TargetWidth <> customWidth OrElse segment.TargetHeight <> customHeight Then
                        Return "仅使用 FFmpeg / Anime4K 时所有分段必须使用相同目标分辨率"
                    End If
                Next
            End If
            Return ""
        End Function

    End Class
End Namespace
