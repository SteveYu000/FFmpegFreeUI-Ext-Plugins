Imports System.Linq
Imports System.Windows.Forms
Imports FFmpegFreeUI.Ext.PluginSdk
Imports LakeUI

''' <summary>
''' 通过 Ext API 的原生控件锚点，把 AB-AV1 预设参数作为普通行追加到 3FUI 的参数总览文本框。
''' 宿主每次重建总览文本后都会触发 TextChanged，因此无需额外面板或页面刷新钩子。
''' </summary>
Friend NotInheritable Class OverviewSummaryDecorator
    Implements IDisposable

    Private Shared ReadOnly OwnedLinePrefixes As String() = {
        "AB-AV1 目标 VMAF：",
        "AB-AV1 最小 CRF：",
        "AB-AV1 最大 CRF：",
        "AB-AV1 采样数量：",
        "AB-AV1 单段时长：",
        "AB-AV1 彻底搜索：",
        "AB-AV1 VMAF 模型："
    }

    Private ReadOnly _context As IExtPluginUiContext
    Private ReadOnly _summaryTextBox As ModernTextBox
    Private ReadOnly _stateRestoredHandler As EventHandler
    Private _stateJson As String
    Private _updatingText As Boolean
    Private _disposed As Boolean

    Public Sub New(context As IExtPluginUiContext)
        If context Is Nothing Then Throw New ArgumentNullException(NameOf(context))
        _context = context
        _summaryTextBox = TryCast(context.AnchorControl, ModernTextBox)
        If _summaryTextBox Is Nothing Then
            Throw New InvalidOperationException("AB-AV1 参数总览锚点不是 LakeUI ModernTextBox。")
        End If

        _stateJson = If(context.StateJson, "{}")
        _stateRestoredHandler = Sub(sender, args) UpdateState(_context.StateJson)
        AddHandler _summaryTextBox.TextChanged, AddressOf SummaryTextChanged
        AddHandler _context.StateRestored, _stateRestoredHandler
        ApplySummaryLines()
    End Sub

    Friend ReadOnly Property IsDisposed As Boolean
        Get
            Return _disposed
        End Get
    End Property

    Friend Sub UpdateState(stateJson As String)
        If _disposed OrElse _summaryTextBox.IsDisposed Then Return
        If _summaryTextBox.InvokeRequired Then
            _summaryTextBox.BeginInvoke(New Action(Of String)(AddressOf UpdateState), stateJson)
            Return
        End If

        _stateJson = If(stateJson, "{}")
        ApplySummaryLines()
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        RemoveHandler _summaryTextBox.TextChanged, AddressOf SummaryTextChanged
        RemoveHandler _context.StateRestored, _stateRestoredHandler
        If Not _summaryTextBox.IsDisposed Then
            SetSummaryText(TransformSummaryText(_summaryTextBox.Text, Array.Empty(Of String)()))
        End If
        _disposed = True
    End Sub

    Private Sub SummaryTextChanged(sender As Object, e As EventArgs)
        If _disposed OrElse _updatingText Then Return
        ApplySummaryLines()
    End Sub

    Private Sub ApplySummaryLines()
        If _disposed OrElse _summaryTextBox.IsDisposed Then Return

        Dim lines As IReadOnlyList(Of String) = Array.Empty(Of String)()
        If AbAv1PluginState.HasStoredState(_stateJson) Then
            lines = AbAv1PluginState.Deserialize(_stateJson).ToOverviewLines()
        End If
        If HasCurrentOwnedLines(_summaryTextBox.Text, lines) Then Return
        SetSummaryText(TransformSummaryText(_summaryTextBox.Text, lines))
    End Sub

    Private Sub SetSummaryText(value As String)
        If String.Equals(_summaryTextBox.Text, value, StringComparison.Ordinal) Then Return
        _updatingText = True
        Try
            _summaryTextBox.Text = value
        Finally
            _updatingText = False
        End Try
    End Sub

    Private Shared Function TransformSummaryText(source As String,
                                                 desiredLines As IReadOnlyList(Of String)) As String
        Dim normalized = If(source, String.Empty).
            Replace(vbCrLf, vbLf, StringComparison.Ordinal).
            Replace(vbCr, vbLf, StringComparison.Ordinal)
        Dim retained As New List(Of String)()
        For Each line In normalized.Split({vbLf}, StringSplitOptions.None)
            If Not IsOwnedLine(line) Then retained.Add(line)
        Next

        While retained.Count > 0 AndAlso retained(retained.Count - 1) = String.Empty
            retained.RemoveAt(retained.Count - 1)
        End While

        Dim result = String.Join(vbCrLf, retained)
        If desiredLines Is Nothing OrElse desiredLines.Count = 0 Then Return result

        Dim ownedBlock = String.Join(vbCrLf, desiredLines)
        If result = String.Empty Then Return ownedBlock
        Return result & vbCrLf & ownedBlock
    End Function

    Private Shared Function IsOwnedLine(line As String) As Boolean
        For Each prefix In OwnedLinePrefixes
            If line.StartsWith(prefix, StringComparison.Ordinal) Then Return True
        Next
        Return False
    End Function

    Private Shared Function HasCurrentOwnedLines(source As String,
                                                 desiredLines As IReadOnlyList(Of String)) As Boolean
        Dim normalized = If(source, String.Empty).
            Replace(vbCrLf, vbLf, StringComparison.Ordinal).
            Replace(vbCr, vbLf, StringComparison.Ordinal)
        Dim currentLines = normalized.Split({vbLf}, StringSplitOptions.None).
            Where(Function(line) IsOwnedLine(line)).ToArray()
        If desiredLines Is Nothing Then Return currentLines.Length = 0
        Return currentLines.SequenceEqual(desiredLines, StringComparer.Ordinal)
    End Function

End Class
