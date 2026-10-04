Imports System
Imports System.Collections
Imports System.Collections.Generic
Imports System.Linq
Imports System.Reflection

Namespace videoenhancer
    Friend NotInheritable Class HostInputFiles
        Friend Shared Function GetCurrentPrepareFilePaths() As IEnumerable(Of String)
            Dim form = HostAccess.GetDefaultInstance("Form_v6_准备文件")
            If form Is Nothing Then Return Array.Empty(Of String)()
            Dim list = HostAccess.GetFileListView(form)
            If list Is Nothing Then Return Array.Empty(Of String)()
            Dim items = TryCast(HostAccess.GetProperty(list, "Items"), IEnumerable)
            Dim method = form.GetType().GetMethod("获取项路径", BindingFlags.Instance Or BindingFlags.Public Or BindingFlags.NonPublic)
            Dim paths As New List(Of String)
            If items Is Nothing OrElse method Is Nothing Then Return paths
            For Each item In items
                Dim value = TryCast(method.Invoke(form, {item}), String)
                If Not String.IsNullOrWhiteSpace(value) Then paths.Add(value)
            Next
            Return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        End Function
    End Class
End Namespace
