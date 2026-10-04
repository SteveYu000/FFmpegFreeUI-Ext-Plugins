Imports System
Imports System.Collections
Imports System.Collections.Generic
Imports System.Linq

Namespace videoenhancer
    ' 现有 SDK 没有队列快照接口；这里只读取 Ext 宿主公开的队列快照。
    Friend NotInheritable Class HostQueueAccess
        Friend Shared Function GetQueueSnapshot() As List(Of HostTask)
            Dim result As New List(Of HostTask)
            If HostRuntime.ResolveType("编码队列_v6", False) Is Nothing Then Return result
            Dim items = TryCast(HostRuntime.InvokeShared("编码队列_v6", "获取队列快照"), IEnumerable)
            If items IsNot Nothing Then
                For Each item In items
                    Dim task = HostTask.Wrap(item)
                    If task IsNot Nothing Then result.Add(task)
                Next
            End If
            Return result
        End Function

        Friend Shared Function FindTask(id As String) As HostTask
            Return GetQueueSnapshot().FirstOrDefault(Function(task) String.Equals(task.ID, id, StringComparison.Ordinal))
        End Function
    End Class
End Namespace
