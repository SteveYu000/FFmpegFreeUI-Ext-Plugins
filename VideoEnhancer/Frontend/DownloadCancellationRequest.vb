Imports System
Imports System.IO
Imports System.Text

Namespace videoenhancer
    ' 通过文件请求子进程自行取消，避免强杀正在替换后端的安装事务。
    Friend NotInheritable Class DownloadCancellationRequest
        Friend ReadOnly Marker As String = Path.Combine(PortableRuntime.WorkRoot, "download-cancel-" & Guid.NewGuid().ToString("N"))
        Friend Property Requested As Boolean
        Friend Property Installing As Boolean

        Friend Sub Cancel()
            If Requested OrElse Installing Then Return
            Directory.CreateDirectory(Path.GetDirectoryName(Marker))
            File.WriteAllText(Marker, "cancel", New UTF8Encoding(False))
            Requested = True
        End Sub

        Friend Sub Clean()
            If File.Exists(Marker) Then File.Delete(Marker)
        End Sub
    End Class
End Namespace
