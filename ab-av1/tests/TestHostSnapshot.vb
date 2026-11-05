Imports System.Text.Json.Nodes
Imports System.Windows.Forms
Imports System.ComponentModel

Namespace Global.FFmpegFreeUI
    ' 与真实宿主公开方法相同的契约，用于验证同一面板父链捕获与实例隔离。
    Public Class TestParameterSurface
        Inherits Panel
        <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
        Public Property CurrentPreset As String
        <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
        Public Property CaptureCount As Integer
    End Class

    Public Class 预设管理_v6
        Public Shared Function 从面板创建预设(surface As TestParameterSurface) As JsonObject
            surface.CaptureCount += 1
            Return JsonNode.Parse(surface.CurrentPreset).AsObject()
        End Function
    End Class
End Namespace
