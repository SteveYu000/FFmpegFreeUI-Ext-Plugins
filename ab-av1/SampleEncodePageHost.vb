Imports System.Windows.Forms
Imports FFmpegFreeUI.Ext.PluginSdk
Imports LakeUI

''' <summary>覆盖当前参数面板（包括其二级导航），并在返回时恢复原页面和选择。</summary>
Friend NotInheritable Class SampleEncodePageHost
    Implements IDisposable

    Private ReadOnly _context As IExtPluginUiContext
    Private ReadOnly _background As Control
    Private ReadOnly _capture As Func(Of String)
    Private ReadOnly _previousVisibility As New Dictionary(Of Control, Boolean)()
    Private _root As Control
    Private _page As SampleEncodePanel
    Private _disposed As Boolean

    Public Sub New(context As IExtPluginUiContext, background As Control,
                   Optional capture As Func(Of String) = Nothing)
        _context = context
        _background = background
        _capture = If(capture, Function() HostPresetSnapshot.Capture(context))
    End Sub

    Friend ReadOnly Property Page As SampleEncodePanel
        Get
            Return _page
        End Get
    End Property

    Public Sub Open()
        If _disposed Then Return
        _root = HostPresetSnapshot.GetParameterSurface(_context)
        If _root Is Nothing OrElse _root.IsDisposed Then
            Throw New NotSupportedException("无法定位当前 3FUI 参数面板，无法打开样本编码子页面。")
        End If
        If _page Is Nothing OrElse _page.IsDisposed Then
            Dim state = AbAv1PluginState.Deserialize(_context.StateJson)
            Dim nativeCrf = If(state.Enabled, String.Empty,
                              _context.GetAnchorControl(ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityValue)?.Text)
            ' 子页现在跨越二级导航，需要使用覆盖整块参数面板的背景源。
            Dim nativeBackground = _root.Controls.OfType(Of ModernPanel)().FirstOrDefault(
                Function(panel) panel.Name = "ModernPanel1" AndAlso panel IsNot _page)
            Dim background = If(nativeBackground?.BackgroundSource, DirectCast(nativeBackground, Control))
            _page = New SampleEncodePanel(_capture, AddressOf Back, state, nativeCrf, If(background, _background))
        Else
            _page.RefreshFromHost()
        End If
        _root.SuspendLayout()
        Try
            If _previousVisibility.Count = 0 Then
                For Each child As Control In _root.Controls
                    If child Is _page Then Continue For
                    _previousVisibility(child) = child.Visible
                    child.Visible = False
                Next
            End If
            If _page.Parent IsNot _root Then _root.Controls.Add(_page)
            _page.Visible = True
            _page.BringToFront()
        Finally
            _root.ResumeLayout(True)
        End Try
    End Sub

    Public Sub Back()
        If _page IsNot Nothing AndAlso Not _page.IsDisposed Then _page.Visible = False
        For Each pair In _previousVisibility
            If Not pair.Key.IsDisposed Then pair.Key.Visible = pair.Value
        Next
        _previousVisibility.Clear()
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        Back()
        If _page IsNot Nothing Then
            _page.Parent?.Controls.Remove(_page)
            _page.Dispose()
            _page = Nothing
        End If
    End Sub
End Class
