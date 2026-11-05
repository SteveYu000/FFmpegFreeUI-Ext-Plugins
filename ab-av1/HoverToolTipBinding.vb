Imports System.Drawing
Imports System.Windows.Forms
Imports LakeUI

''' <summary>将整块按钮的悬停事件绑定到 LakeUI 原生浮动提示窗。</summary>
Friend NotInheritable Class HoverToolTipBinding
    Implements IDisposable

    Private ReadOnly _owner As Control
    Friend ReadOnly Property Text As String
    Private _popup As FloatingToolTipForm
    Private _disposed As Boolean

    Public Sub New(owner As Control, text As String)
        _owner = owner
        Me.Text = text
        AddHandler owner.MouseHover, AddressOf ShowTip
        AddHandler owner.MouseLeave, AddressOf HideTip
        AddHandler owner.Click, AddressOf HideTip
        AddHandler owner.VisibleChanged, AddressOf HideTip
        AddHandler owner.EnabledChanged, AddressOf HideTip
        AddHandler owner.HandleDestroyed, AddressOf HideTip
    End Sub

    Private Sub ShowTip(sender As Object, e As EventArgs)
        If _disposed OrElse _owner.IsDisposed OrElse Not _owner.Visible OrElse Not _owner.Enabled Then Return
        If _popup Is Nothing OrElse _popup.IsDisposed Then _popup = New FloatingToolTipForm(_owner)
        Dim gap = CInt(Math.Round(6 * _owner.DeviceDpi / 96.0R))
        _popup.ShowTip(Text, _owner.PointToScreen(New Point(_owner.Width + gap, 0)),
                      New FloatingToolTipStyle With {.Font = _owner.Font, .Padding = New Padding(15), .MaxWidth = 420},
                      _owner.Width + gap * 2)
    End Sub

    Private Sub HideTip(sender As Object, e As EventArgs)
        If _popup IsNot Nothing Then
            If Not _popup.IsDisposed Then _popup.Dispose()
            _popup = Nothing
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        RemoveHandler _owner.MouseHover, AddressOf ShowTip
        RemoveHandler _owner.MouseLeave, AddressOf HideTip
        RemoveHandler _owner.Click, AddressOf HideTip
        RemoveHandler _owner.VisibleChanged, AddressOf HideTip
        RemoveHandler _owner.EnabledChanged, AddressOf HideTip
        RemoveHandler _owner.HandleDestroyed, AddressOf HideTip
        HideTip(Me, EventArgs.Empty)
    End Sub
End Class
