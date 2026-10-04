Imports System.Threading

Namespace videoenhancer
    ' 下载操作直接使用 DLL 内的取消令牌；安装事务开始后等待它安全完成。
    Friend NotInheritable Class DownloadCancellationRequest
        Private ReadOnly _source As New CancellationTokenSource()
        Friend ReadOnly Property Token As CancellationToken
            Get
                Return _source.Token
            End Get
        End Property
        Friend Property Requested As Boolean
        Friend Property Installing As Boolean

        Friend Sub Cancel()
            If Requested OrElse Installing Then Return
            Requested = True
            _source.Cancel()
        End Sub

        Friend Sub Clean()
            _source.Dispose()
        End Sub
    End Class
End Namespace
