Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.Json
Imports VideoEnhancer

Namespace videoenhancer
    ' 模型目录通过类型化 DLL 服务读取，不再查找 EXE 或解析命令行。
    Friend NotInheritable Class ModelCatalogClient
        Private Sub New()
        End Sub

        Private Shared Function ReadResult(request As BackendRequest, timeout As Integer) As String
            Using job = BackendServiceJob.Start(request)
                Dim output = job.Output.ReadToEndAsync()
                Dim errors = job.Error.ReadToEndAsync()
                If Not job.Wait(timeout) Then
                    job.Cancel()
                    Throw New TimeoutException("读取模型目录超时")
                End If
                Dim diagnostic = errors.GetAwaiter().GetResult()
                If job.ResultCode <> 0 Then Throw New InvalidOperationException(PluginPanel.LastNonEmptyLine(diagnostic))
                Return output.GetAwaiter().GetResult().Replace(Convert.ToChar(13).ToString(), "").Split(Convert.ToChar(10)).
                    LastOrDefault(Function(line) line.Trim().StartsWith("["c))
            End Using
        End Function

        Friend Shared Function RunListModels(backend As String, Optional interpolation As Boolean = False) As List(Of String)
            Dim json = ReadResult(New BackendRequest(If(interpolation, BackendAction.ListInterpolationModels, BackendAction.ListModels)) With {.Backend = backend}, 180000)
            If String.IsNullOrWhiteSpace(json) Then Return New List(Of String)()
            Return JsonSerializer.Deserialize(Of List(Of String))(json).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        Friend Shared Function RunModelCatalog(backend As String, Optional interpolation As Boolean = False) As List(Of ModelCatalogItem)
            Dim json = ReadResult(New BackendRequest(If(interpolation, BackendAction.ListInterpolationCatalog, BackendAction.ListModelCatalog)) With {.Backend = backend}, 180000)
            If String.IsNullOrWhiteSpace(json) Then Return New List(Of ModelCatalogItem)()
            Return JsonSerializer.Deserialize(Of List(Of ModelCatalogItem))(json, New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True}).
                Where(Function(item) item IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(item.Id)).GroupBy(Function(item) item.Id, StringComparer.OrdinalIgnoreCase).
                Select(Function(group) group.First()).ToList()
        End Function

        Friend Shared Function RunUserModelList() As List(Of UserModelItem)
            Dim json = ReadResult(New BackendRequest(BackendAction.ListUserModels), 30000)
            If String.IsNullOrWhiteSpace(json) Then Return New List(Of UserModelItem)()
            Return JsonSerializer.Deserialize(Of List(Of UserModelItem))(json, New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True})
        End Function
    End Class
End Namespace
