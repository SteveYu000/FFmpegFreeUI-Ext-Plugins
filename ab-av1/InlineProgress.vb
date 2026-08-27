Friend NotInheritable Class InlineProgress(Of T)
    Implements IProgress(Of T)

    Private ReadOnly _callback As Action(Of T)

    Public Sub New(callback As Action(Of T))
        If callback Is Nothing Then Throw New ArgumentNullException(NameOf(callback))
        _callback = callback
    End Sub

    Public Sub Report(value As T) Implements IProgress(Of T).Report
        _callback.Invoke(value)
    End Sub
End Class
