Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Text

Namespace videoenhancer
        Friend Enum GridKind
            SingleVideo
            Grid4
            TwoCol
            TwoRow
            TwoRight
            TwoLeft
            TwoTop
            TwoBottom
        End Enum

        ' ── 控件 ──
    Friend NotInheritable Class GridCompositionSpec
        Friend ReadOnly Inputs As List(Of String)
        Friend ReadOnly Kind As GridKind
        Friend ReadOnly Width As Integer
        Friend ReadOnly Height As Integer
        Friend ReadOnly ScaleAlgorithm As String
        Friend ReadOnly LineWidth As Integer
        Friend ReadOnly LineColor As Color
        Friend ReadOnly SubtitlePath As String
        Friend Sub New(inputs As List(Of String), kind As GridKind, width As Integer, height As Integer, algorithm As String, lineWidth As Integer, lineColor As Color, subtitlePath As String)
            Me.Inputs = inputs
            Me.Kind = kind
            Me.Width = width
            Me.Height = height
            Me.ScaleAlgorithm = algorithm
            Me.LineWidth = lineWidth
            Me.LineColor = lineColor
            Me.SubtitlePath = subtitlePath
        End Sub
    End Class

    Friend NotInheritable Class GridCompositionBuilder
        Friend Enum NameAnchor
            TopLeft
            TopRight
            BottomLeft
            BottomRight
        End Enum

        Friend Structure NamePlacement
            Public Anchor As NameAnchor
            Public StackIndex As Integer
        End Structure

        Friend Shared Function NamePlacements(count As Integer) As List(Of NamePlacement)
            Dim result As New List(Of NamePlacement)()
            Select Case count
                Case 1
                    result.Add(New NamePlacement With {.Anchor = NameAnchor.TopLeft})
                Case 2
                    result.Add(New NamePlacement With {.Anchor = NameAnchor.TopLeft})
                    result.Add(New NamePlacement With {.Anchor = NameAnchor.BottomRight})
                Case 3
                    ' 三路都使用“各自小块的左上角”，具体坐标由布局矩形决定。
                    For i As Integer = 0 To 2
                        result.Add(New NamePlacement With {.Anchor = NameAnchor.TopLeft})
                    Next
                Case Else
                    result.Add(New NamePlacement With {.Anchor = NameAnchor.TopLeft})
                    result.Add(New NamePlacement With {.Anchor = NameAnchor.TopRight})
                    result.Add(New NamePlacement With {.Anchor = NameAnchor.BottomLeft})
                    result.Add(New NamePlacement With {.Anchor = NameAnchor.BottomRight})
            End Select
            Return result
        End Function

        Friend Shared Function LayoutRects(kind As GridKind, w As Integer, h As Integer) As List(Of Rectangle)
            Dim hw = w \ 2
            Dim hh = h \ 2
            Dim result As New List(Of Rectangle)()
            Select Case kind
                Case GridKind.SingleVideo
                    result.Add(New Rectangle(0, 0, w, h))
                Case GridKind.TwoCol
                    result.Add(New Rectangle(0, 0, hw, h))
                    result.Add(New Rectangle(hw, 0, hw, h))
                Case GridKind.TwoRow
                    result.Add(New Rectangle(0, 0, w, hh))
                    result.Add(New Rectangle(0, hh, w, hh))
                Case GridKind.TwoRight
                    result.Add(New Rectangle(0, 0, hw, h))
                    result.Add(New Rectangle(hw, 0, hw, hh))
                    result.Add(New Rectangle(hw, hh, hw, hh))
                Case GridKind.TwoLeft
                    result.Add(New Rectangle(0, 0, hw, hh))
                    result.Add(New Rectangle(0, hh, hw, hh))
                    result.Add(New Rectangle(hw, 0, hw, h))
                Case GridKind.TwoTop
                    result.Add(New Rectangle(0, 0, hw, hh))
                    result.Add(New Rectangle(hw, 0, hw, hh))
                    result.Add(New Rectangle(0, hh, w, hh))
                Case GridKind.TwoBottom
                    result.Add(New Rectangle(0, 0, w, hh))
                    result.Add(New Rectangle(0, hh, hw, hh))
                    result.Add(New Rectangle(hw, hh, hw, hh))
                Case Else
                    result.Add(New Rectangle(0, 0, hw, hh))
                    result.Add(New Rectangle(hw, 0, hw, hh))
                    result.Add(New Rectangle(0, hh, hw, hh))
                    result.Add(New Rectangle(hw, hh, hw, hh))
            End Select
            Return result
        End Function

        Friend Shared Function XstackLayout(kind As GridKind) As String
            Select Case kind
                Case GridKind.SingleVideo : Return "0_0"
                Case GridKind.TwoCol : Return "0_0|w0_0"
                Case GridKind.TwoRow : Return "0_0|0_h0"
                Case GridKind.TwoRight : Return "0_0|w0_0|w0_h1"
                Case GridKind.TwoLeft : Return "0_0|0_h0|w0_0"
                Case GridKind.TwoTop : Return "0_0|w0_0|0_h0"
                Case GridKind.TwoBottom : Return "0_0|0_h0|w1_h0"
                Case Else : Return "0_0|w0_0|0_h0|w0_h0"
            End Select
        End Function

        Friend Shared Function LineRects(kind As GridKind, w As Integer, h As Integer, lw As Integer) As List(Of Rectangle)
            Dim hw = w \ 2
            Dim hh = h \ 2
            Dim half = lw \ 2
            Dim result As New List(Of Rectangle)()
            Select Case kind
                Case GridKind.SingleVideo
                    Return result
                Case GridKind.TwoCol
                    result.Add(New Rectangle(hw - half, 0, lw, h))
                Case GridKind.TwoRow
                    result.Add(New Rectangle(0, hh - half, w, lw))
                Case GridKind.TwoRight
                    result.Add(New Rectangle(hw - half, 0, lw, h))
                    result.Add(New Rectangle(hw, hh - half, hw, lw))
                Case GridKind.TwoLeft
                    result.Add(New Rectangle(hw - half, 0, lw, h))
                    result.Add(New Rectangle(0, hh - half, hw, lw))
                Case GridKind.TwoTop
                    result.Add(New Rectangle(0, hh - half, w, lw))
                    result.Add(New Rectangle(hw - half, 0, lw, hh))
                Case GridKind.TwoBottom
                    result.Add(New Rectangle(0, hh - half, w, lw))
                    result.Add(New Rectangle(hw - half, hh, lw, hh))
                Case Else
                    result.Add(New Rectangle(hw - half, 0, lw, h))
                    result.Add(New Rectangle(0, hh - half, w, lw))
            End Select
            Return result
        End Function
        ' ────────────────────────── 颜色 ──────────────────────────

        Friend Shared Function BuildFilter(spec As GridCompositionSpec) As String
            Dim inputs=spec.Inputs, kind=spec.Kind, w=spec.Width, h=spec.Height, algo=spec.ScaleAlgorithm, lw=spec.LineWidth, assPath=spec.SubtitlePath
            Dim sb As New StringBuilder()
            Dim rects = LayoutRects(kind, w, h)
            For i As Integer = 0 To rects.Count - 1
                Dim r = rects(i)
                If i > 0 Then
                    sb.Append(" ")
                End If
                sb.Append("[").Append(i.ToString()).Append(":v] ")
                ' 先按比例铺满完整画布，再从中心裁成画布大小，最后取该视频负责的格子。
                ' 这样四路分别取得左上/右上/左下/右下，不会把整帧挤压进小格。
                sb.Append("scale=").Append(w.ToString()).Append(":").Append(h.ToString())
                sb.Append(":force_original_aspect_ratio=increase:flags=").Append(algo)
                sb.Append(", crop=").Append(w.ToString()).Append(":").Append(h.ToString())
                sb.Append(":(iw-").Append(w.ToString()).Append(")/2:(ih-").Append(h.ToString()).Append(")/2")
                sb.Append(", setsar=1, crop=").Append(r.Width.ToString()).Append(":").Append(r.Height.ToString())
                sb.Append(":").Append(r.X.ToString()).Append(":").Append(r.Y.ToString())
                sb.Append(", setpts=PTS-STARTPTS [v").Append(i.ToString()).Append("]; ")
            Next
            If rects.Count = 1 Then
                sb.Append("[v0] null [out]; ")
            Else
                sb.Append("[v0]")
                For i As Integer = 1 To rects.Count - 1
                    sb.Append("[v").Append(i.ToString()).Append("]")
                Next
                sb.Append(" xstack=inputs=").Append(rects.Count.ToString()).Append(":layout=").Append(XstackLayout(kind)).Append(" [out]; ")
            End If

            Dim dividerRects = LineRects(kind, w, h, lw)
            Dim colorHex = "0x" & spec.LineColor.R.ToString("X2") & spec.LineColor.G.ToString("X2") & spec.LineColor.B.ToString("X2")
            Dim first As Boolean = True
            For Each r In dividerRects
                If Not first Then
                    sb.Append(", ")
                Else
                    sb.Append("[out] ")
                    first = False
                End If
                sb.Append("drawbox=x=").Append(r.X.ToString()).Append(":y=").Append(r.Y.ToString())
                sb.Append(":w=").Append(r.Width.ToString()).Append(":h=").Append(r.Height.ToString())
                sb.Append(":color=").Append(colorHex).Append(":t=fill")
            Next
            If first Then
                sb.Append("[out] null [lined]; ")
            Else
                sb.Append(" [lined]; ")
            End If

            If Not String.IsNullOrWhiteSpace(assPath) AndAlso File.Exists(assPath) Then
                sb.Append("[lined] subtitles=filename=").Append(EscapeFilterPath(assPath)).Append(" [final]")
            Else
                sb.Append("[lined] null [final]")
            End If
            Return sb.ToString()
        End Function

        Private Shared Function EscapeFilterPath(path As String) As String
            Dim sb As New StringBuilder()
            sb.Append("'")
            For Each c As Char In path
                Select Case c
                    Case "\"c
                        sb.Append("\\")
                    Case ":"c
                        sb.Append("\:")
                    Case "'"c
                        sb.Append("\'")
                    Case Else
                        sb.Append(c)
                End Select
            Next
            sb.Append("'")
            Return sb.ToString()
        End Function

        Friend Shared Function BuildAss(spec As GridCompositionSpec) As String
            Dim inputs=spec.Inputs, kind=spec.Kind, w=spec.Width, h=spec.Height
            Dim sb As New StringBuilder()
            sb.AppendLine("[Script Info]")
            sb.AppendLine("ScriptType: v4.00+")
            sb.AppendLine("PlayResX: " & w.ToString())
            sb.AppendLine("PlayResY: " & h.ToString())
            sb.AppendLine("ScaledBorderAndShadow: yes")
            sb.AppendLine()
            sb.AppendLine("[V4+ Styles]")
            sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding")
            Dim fontSize = Math.Max(22, w \ 60)
            sb.AppendLine("Style: Default,Microsoft YaHei," & fontSize.ToString() & ",&H00FFFFFF,&H000000FF,&H00101010,&H80000000,-1,0,0,0,100,100,0,0,1,2,1,5,20,20,20,1")
            sb.AppendLine()
            sb.AppendLine("[Events]")
            sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text")
            Dim placements = NamePlacements(inputs.Count)
            Dim cellRects = If(inputs.Count = 3, LayoutRects(kind, w, h), Nothing)
            Dim margin = Math.Max(20, fontSize)
            Dim lineStep = fontSize + Math.Max(8, fontSize \ 3)
            For i As Integer = 0 To inputs.Count - 1
                Dim placement = placements(i)
                Dim x = margin
                Dim y = margin + placement.StackIndex * lineStep
                Dim alignment = 7
                If inputs.Count = 3 AndAlso cellRects IsNot Nothing AndAlso i < cellRects.Count Then
                    x = cellRects(i).X + margin
                    y = cellRects(i).Y + margin
                End If
                Select Case placement.Anchor
                    Case NameAnchor.TopRight
                        x = w - margin
                        alignment = 9
                    Case NameAnchor.BottomLeft
                        y = h - margin
                        alignment = 1
                    Case NameAnchor.BottomRight
                        x = w - margin
                        y = h - margin
                        alignment = 3
                End Select
                Dim name = System.IO.Path.GetFileNameWithoutExtension(inputs(i))
                name = SanitizeAssText(name)
                sb.AppendLine("Dialogue: 0,0:00:00:00,99:00:00:00,Default,,0,0,0,,{\an" & alignment.ToString(CultureInfo.InvariantCulture) &
                              "\pos(" & x.ToString(CultureInfo.InvariantCulture) & "," & y.ToString(CultureInfo.InvariantCulture) & ")}" & name)
            Next
            Return sb.ToString()
        End Function

        Private Shared Function SanitizeAssText(text As String) As String
            If String.IsNullOrEmpty(text) Then
                Return text
            End If
            Dim sb As New StringBuilder()
            For Each c As Char In text
                Select Case c
                    Case "{"c, "}"c, "\"c
                        sb.Append(" ")
                    Case ","c
                        sb.Append("，")
                    Case Else
                        sb.Append(c)
                End Select
            Next
            Return sb.ToString()
        End Function

        ' ────────────────────────── ffmpeg 定位 / 关闭 ──────────────────────────

    End Class
End Namespace
