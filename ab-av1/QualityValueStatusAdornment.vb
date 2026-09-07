Imports System.Drawing
Imports System.Windows.Forms
Imports LakeUI

''' <summary>
''' 将 ab-av1 环境状态放在原生“质量值”输入框右侧，不参与宿主 Dock 布局。
''' </summary>
Friend NotInheritable Class QualityValueStatusAdornment
    Implements IDisposable

    Friend Const StatusControlName As String = "AbAv1QualityValueStatus"
    Private Const LogicalGap As Integer = 10

    Private ReadOnly _qualityValue As Control
    Private ReadOnly _hostRow As Control
    Private ReadOnly _status As HtmlColorLabel
    Private _active As Boolean
    Private _disposed As Boolean

    Public Sub New(qualityValue As Control, status As HtmlColorLabel)
        If status Is Nothing Then Throw New ArgumentNullException(NameOf(status))
        _qualityValue = qualityValue
        _hostRow = qualityValue?.Parent
        _status = status
        _status.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        _status.BackColor1 = Color.Transparent
        _status.Dock = DockStyle.None
        _status.TabStop = False
        _status.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleLeft
        _status.Visible = False

        If _qualityValue Is Nothing OrElse _hostRow Is Nothing Then Return

        _hostRow.Controls.Add(_status)
        _status.BringToFront()
        AddHandler _hostRow.Layout, AddressOf HostLayoutChanged
        AddHandler _qualityValue.LocationChanged, AddressOf QualityValueBoundsChanged
        AddHandler _qualityValue.SizeChanged, AddressOf QualityValueBoundsChanged
        UpdateBounds()
    End Sub

    Friend ReadOnly Property IsAttached As Boolean
        Get
            Return _status.Parent Is _hostRow AndAlso _hostRow IsNot Nothing
        End Get
    End Property

    Public Sub SetActive(active As Boolean)
        If _disposed Then Return
        _active = active
        UpdateBounds()
        _status.Visible = active AndAlso IsAttached
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True

        If _hostRow IsNot Nothing AndAlso Not _hostRow.IsDisposed Then
            RemoveHandler _hostRow.Layout, AddressOf HostLayoutChanged
        End If
        If _qualityValue IsNot Nothing AndAlso Not _qualityValue.IsDisposed Then
            RemoveHandler _qualityValue.LocationChanged, AddressOf QualityValueBoundsChanged
            RemoveHandler _qualityValue.SizeChanged, AddressOf QualityValueBoundsChanged
        End If
        If _status.Parent IsNot Nothing AndAlso Not _status.Parent.IsDisposed Then
            _status.Parent.Controls.Remove(_status)
        End If
        If Not _status.IsDisposed Then _status.Dispose()
    End Sub

    Private Sub HostLayoutChanged(sender As Object, e As LayoutEventArgs)
        UpdateBounds()
    End Sub

    Private Sub QualityValueBoundsChanged(sender As Object, e As EventArgs)
        UpdateBounds()
    End Sub

    Private Sub UpdateBounds()
        If _disposed OrElse
           _qualityValue Is Nothing OrElse
           _qualityValue.IsDisposed OrElse
           _hostRow Is Nothing OrElse
           _hostRow.IsDisposed Then Return

        Dim gap = Math.Max(1, CInt(Math.Round(LogicalGap * _qualityValue.DeviceDpi / 96.0R)))
        Dim left = _qualityValue.Right + gap
        Dim right = _hostRow.ClientSize.Width - _hostRow.Padding.Right
        _status.SetBounds(left, _qualityValue.Top, Math.Max(0, right - left), _qualityValue.Height)
        If _active Then _status.BringToFront()
    End Sub

End Class
