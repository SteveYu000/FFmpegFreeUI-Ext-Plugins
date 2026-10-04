Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks

Namespace videoenhancer
    Friend NotInheritable Class ModelDownloadCoordinator
        Private Const MaxParallelDownloads As Integer = 3
        Private ReadOnly _queues As New Dictionary(Of String, QueueState)(StringComparer.OrdinalIgnoreCase)

        Private NotInheritable Class QueueState
            Friend ReadOnly Outcomes As New Dictionary(Of String, ItemOutcome)(StringComparer.OrdinalIgnoreCase)
            Friend Property Pending As Queue(Of String)
            Friend Property Result As BatchResult
            Friend Property StopRequested As Func(Of Boolean)
            Friend Property Progress As Action(Of BatchResult)
            Friend Property Changed As New TaskCompletionSource(Of Boolean)()
        End Class

        Friend Enum EnqueueResult
            NoQueue
            Enqueued
            AlreadyQueued
            Stopping
        End Enum

        Friend Function IsPathQueued(path As String) As Boolean
            Return _queues.Values.Any(Function(state) state.Pending.Contains(path, StringComparer.OrdinalIgnoreCase))
        End Function

        Friend Function Enqueue(category As String, path As String) As EnqueueResult
            Dim state As QueueState = Nothing
            If Not _queues.TryGetValue("全部资源", state) AndAlso Not _queues.TryGetValue(category, state) Then Return EnqueueResult.NoQueue
            If state.StopRequested.Invoke() OrElse state.Result.StopReason <> QueueStopReason.None Then Return EnqueueResult.Stopping
            If IsPathActive(path) OrElse state.Pending.Contains(path, StringComparer.OrdinalIgnoreCase) Then Return EnqueueResult.AlreadyQueued
            Dim previous As ItemOutcome
            If state.Outcomes.TryGetValue(path, previous) Then
                Select Case previous
                    Case ItemOutcome.Succeeded
                        state.Result.Succeeded -= 1
                    Case ItemOutcome.Cancelled
                        state.Result.Cancelled -= 1
                    Case Else
                        state.Result.Failed -= 1
                End Select
                state.Outcomes.Remove(path)
            End If
            state.Pending.Enqueue(path)
            state.Result.Pending = state.Pending.Count
            state.Progress.Invoke(state.Result)
            ' 唤醒等待中的调度，让空闲槽位立即开始重试。
            state.Changed.TrySetResult(True)
            Return EnqueueResult.Enqueued
        End Function
        Private ReadOnly _activePaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _activeGroups As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        Friend ReadOnly Property ActiveCount As Integer
            Get
                Return _activePaths.Count
            End Get
        End Property

        Friend ReadOnly Property HasCapacity As Boolean
            Get
                Return ActiveCount < MaxParallelDownloads
            End Get
        End Property

        Friend Function IsPathActive(path As String) As Boolean
            Return _activePaths.Contains(path)
        End Function

        Friend Function IsGroupActive(category As String) As Boolean
            Return _activeGroups.Contains(category)
        End Function

        Friend Function TryBeginGroup(category As String) As Boolean
            Return _activeGroups.Add(category)
        End Function

        Friend Sub EndGroup(category As String)
            _activeGroups.Remove(category)
        End Sub

        Friend Function TryBegin(path As String) As Boolean
            If Not HasCapacity Then Return False
            Return _activePaths.Add(path)
        End Function

        Friend Sub EndPath(path As String)
            _activePaths.Remove(path)
        End Sub

        Friend Enum ItemOutcome
            Succeeded
            Cancelled
            Failed
            Offline
            AuthenticationRequired
        End Enum

        Friend Enum QueueStopReason
            None
            UserStopped
            Offline
            AuthenticationRequired
        End Enum

        Friend NotInheritable Class BatchResult
            Friend Property Succeeded As Integer
            Friend Property Cancelled As Integer
            Friend Property Failed As Integer
            Friend Property Pending As Integer
            Friend Property Running As Integer
            Friend Property StopReason As QueueStopReason

            Friend ReadOnly Property Summary As String
                Get
                    Return $"成功 {Succeeded} · 取消 {Cancelled} · 失败 {Failed} · 待下载 {Pending} · 进行中 {Running}"
                End Get
            End Property
        End Class

        Friend Async Function RunGroupAsync(Of TResult)(
            category As String, paths As List(Of String),
            start As Func(Of String, Task(Of TResult)),
            classify As Func(Of TResult, ItemOutcome),
            finished As Action(Of String, TResult),
            progress As Action(Of BatchResult),
            stopRequested As Func(Of Boolean)) As Task(Of BatchResult)

            Dim batch As New BatchResult With {.Pending = paths.Count}
            If Not TryBeginGroup(category) Then Return batch
            Dim state As New QueueState With {
                .Pending = New Queue(Of String)(paths), .Result = batch,
                .StopRequested = stopRequested, .Progress = progress
            }
            _queues.Add(category, state)
            Dim running As New Dictionary(Of Task(Of TResult), String)()
            Try
                While state.Pending.Count > 0 OrElse running.Count > 0
                    If stopRequested() AndAlso batch.StopReason = QueueStopReason.None Then
                        batch.StopReason = QueueStopReason.UserStopped
                    End If
                    ' 先处理已经结束的任务，再补位，避免已返回的离线/认证错误之后又启动新任务。
                    While state.Pending.Count > 0 AndAlso HasCapacity AndAlso
                        batch.StopReason = QueueStopReason.None AndAlso Not running.Keys.Any(Function(task) task.IsCompleted)
                        Dim relativePath = state.Pending.Dequeue()
                        batch.Pending = state.Pending.Count
                        If Not TryBegin(relativePath) Then Continue While
                        running.Add(start(relativePath), relativePath)
                        batch.Pending = state.Pending.Count
                        batch.Running = running.Count
                        progress(batch)
                    End While
                    If running.Count = 0 Then Exit While
                    Dim ready = Await Task.WhenAny(running.Keys.Cast(Of Task)().Append(state.Changed.Task))
                    If ready Is state.Changed.Task Then
                        state.Changed = New TaskCompletionSource(Of Boolean)()
                        Continue While
                    End If
                    Dim completedTask = DirectCast(ready, Task(Of TResult))
                    Dim completedPath = running(completedTask)
                    running.Remove(completedTask)
                    Dim result = Await completedTask
                    EndPath(completedPath)
                    If stopRequested() AndAlso batch.StopReason = QueueStopReason.None Then
                        batch.StopReason = QueueStopReason.UserStopped
                    End If
                    Dim outcome = classify(result)
                    state.Outcomes(completedPath) = outcome
                    Select Case outcome
                        Case ItemOutcome.Succeeded
                            batch.Succeeded += 1
                        Case ItemOutcome.Cancelled
                            batch.Cancelled += 1
                        Case Else
                            batch.Failed += 1
                    End Select
                    If batch.StopReason = QueueStopReason.None Then
                        Select Case outcome
                            Case ItemOutcome.Offline
                                batch.StopReason = QueueStopReason.Offline
                            Case ItemOutcome.AuthenticationRequired
                                batch.StopReason = QueueStopReason.AuthenticationRequired
                        End Select
                    End If
                    batch.Running = running.Count
                    finished(completedPath, result)
                    progress(batch)
                End While
            Finally
                _queues.Remove(category)
                EndGroup(category)
            End Try
            Return batch
        End Function
    End Class
End Namespace
