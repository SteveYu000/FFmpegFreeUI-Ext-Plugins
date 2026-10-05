Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text.Json
Imports FFmpegFreeUI.Ext.PluginSdk
Imports VideoEnhancer

Namespace videoenhancer
    ' 官方队列事件和 Ext 扩展共用一个入口，不再安装旧队列钩子或替代宿主进程。
    Public NotInheritable Class Entry
        Implements IExtFFmpegFreeUIPlugin

        Private Shared _subscribe As Action(Of String, Object)
        Private Shared _pipeline As ExtVideoPipeline
        Private ReadOnly _pages As New List(Of IDisposable)

        Public ReadOnly Property Id As String Implements IExtFFmpegFreeUIPlugin.Id
            Get
                Return ExtVideoPipeline.PluginId
            End Get
        End Property

        Public ReadOnly Property DisplayName As String Implements IExtFFmpegFreeUIPlugin.DisplayName
            Get
                Return "视频超分"
            End Get
        End Property

        Public Shared Sub SetHost_SubscribeQueueEvents(callback As Action(Of String, Object))
            _subscribe = callback
        End Sub

        Public Shared Sub Entry()
            ' 页面和处理器全部由 Ext Initialize 注册，官方 Entry 仅满足事件注入约定。
        End Sub

        Public Sub Initialize(host As IExtFFmpegFreeUIHost) Implements IExtFFmpegFreeUIPlugin.Initialize
            If host.ApiVersion < New Version(2, 5) Then Throw New InvalidOperationException("VideoEnhancer 需要 Ext API 2.5 或更新版本")
            BackendServices.ConfigureRuntime(PortableRuntime.ApplicationRoot)
            _pipeline = New ExtVideoPipeline(host, Function() FfmpegToolResolver.Resolve("ffmpeg.exe"), Function() FfmpegToolResolver.Resolve("ffprobe.exe"))
            _pages.Add(host.PageEntries.RegisterPage(New ExtPluginPageExtension(
                "ai-video-parameters", ExtFFmpegFreeUIPageTargets.ParametersVideoFrame, ExtPluginRelativePosition.After,
                "视频参数 | AI增强", Function(context)
                                       Dim page As New PluginPanel(PluginConfig.FromStateJson(context.StateJson), parameterMode:=True)
                                       page.BindPresetContext(context)
                                       Return page
                                   End Function)))
            _pages.Add(host.PageEntries.RegisterPage(New ExtPluginPageExtension(
                "video-enhancement-tools", ExtFFmpegFreeUIPageTargets.MainParameters, ExtPluginRelativePosition.After,
                "视频超分", Function(context) New PluginPanel(PluginConfig.Load()))))
            If _subscribe IsNot Nothing Then _subscribe("*", New Action(Of String, String)(AddressOf OnQueueEvent))
            For Each task In HostQueueAccess.GetQueueSnapshot()
                TrackTask(task)
            Next
            host.Log(ExtPluginLogLevel.Information, "VideoEnhancer Ext 已加载：AI 视频前置步骤 → 宿主 FFmpeg → 后处理")
        End Sub

        Private Shared Sub OnQueueEvent(name As String, json As String)
            Try
                OnQueueEventCore(name, json)
            Catch ex As Exception
                EnhancementTaskRegistry.CurrentParameterError = "队列参数无法读取：" & ex.Message
                System.Diagnostics.Trace.WriteLine(ex.ToString())
            End Try
        End Sub

        Private Shared Sub OnQueueEventCore(name As String, json As String)
            If _pipeline Is Nothing Then Return
            If name = "task.added" OrElse name = "task.reset" Then
                Using doc = JsonDocument.Parse(json)
                    Dim item As JsonElement
                    If doc.RootElement.TryGetProperty("task", item) Then
                        TrackTask(HostQueueAccess.FindTask(item.GetProperty("id").GetString()))
                    End If
                End Using
            End If
            _pipeline.OnQueueEvent(name, json)
        End Sub

        Private Shared Sub TrackTask(task As HostTask)
            If task Is Nothing Then Return
            Dim preset = HostRuntime.ReadMember(task.Instance, "预设数据")
            If preset Is Nothing Then Return
            _pipeline.TrackQueuedTask(task.ID, task.输入文件, task.输出文件, JsonSerializer.Serialize(preset, preset.GetType()))
        End Sub
    End Class
End Namespace
