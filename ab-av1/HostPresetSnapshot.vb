Imports System.Reflection
Imports System.Runtime.ExceptionServices
Imports System.Text.Json
Imports System.Windows.Forms
Imports FFmpegFreeUI.Ext.PluginSdk

''' <summary>
''' Ext API 2.5 没有主动捕获预设接口，通过宿主公开的捕获方法读取当前面板。
''' 只在同一控件父链匹配方法参数类型，不访问私有字段，也不读取上一次预览缓存。
''' </summary>
Friend NotInheritable Class HostPresetSnapshot
    Private Sub New()
    End Sub

    Friend Shared Function Capture(context As IExtPluginUiContext) As String
        Dim anchor = context.AnchorControl
        If anchor.IsDisposed Then Throw New ObjectDisposedException("参数面板")
        If anchor.InvokeRequired Then
            Return CStr(anchor.Invoke(New Func(Of String)(Function() Capture(context))))
        End If
        Dim operation = FindCaptureOperation(context)
        Dim arguments = If(operation.ByReference,
                           New Object() {Nothing, operation.Surface},
                           New Object() {operation.Surface})
        Return InvokeCapture(operation.Method, arguments, operation.ByReference)
    End Function

    Friend Shared Function GetParameterSurface(context As IExtPluginUiContext) As Control
        Return FindCaptureOperation(context).Surface
    End Function

    Private Shared Function FindCaptureOperation(context As IExtPluginUiContext) As CaptureOperation
        Dim anchor = context.AnchorControl
        Dim current As Control = anchor
        While current IsNot Nothing
            Dim manager = current.GetType().Assembly.GetType("FFmpegFreeUI.预设管理_v6", throwOnError:=False)
            If manager IsNot Nothing Then
                Dim factory = manager.GetMethods(BindingFlags.Public Or BindingFlags.Static).FirstOrDefault(
                    Function(method) method.Name = "从面板创建预设" AndAlso
                                     method.GetParameters().Length = 1 AndAlso
                                     method.GetParameters()(0).ParameterType.IsInstanceOfType(current))
                If factory IsNot Nothing Then Return New CaptureOperation(current, factory, False)
                ' 兼容较早 v6 宿主的公开 ByRef 捕获方法。
                Dim storage = manager.GetMethods(BindingFlags.Public Or BindingFlags.Static).FirstOrDefault(
                    Function(method) method.Name = "储存预设" AndAlso
                                     method.GetParameters().Length = 2 AndAlso
                                     method.GetParameters()(0).ParameterType.IsByRef AndAlso
                                     method.GetParameters()(1).ParameterType.IsInstanceOfType(current))
                If storage IsNot Nothing Then Return New CaptureOperation(current, storage, True)
            End If
            current = current.Parent
        End While
        Throw New NotSupportedException("当前 3FUI 未提供可用的公开参数快照方法，请升级 FFmpegFreeUI API Extended Edition。")
    End Function

    Private NotInheritable Class CaptureOperation
        Public Sub New(surface As Control, method As MethodInfo, byReference As Boolean)
            Me.Surface = surface
            Me.Method = method
            Me.ByReference = byReference
        End Sub

        Public ReadOnly Property Surface As Control
        Public ReadOnly Property Method As MethodInfo
        Public ReadOnly Property ByReference As Boolean
    End Class

    Private Shared Function InvokeCapture(method As MethodInfo, arguments As Object(), byReference As Boolean) As String
        Try
            Dim preset = method.Invoke(Nothing, arguments)
            If byReference Then preset = arguments(0)
            If preset Is Nothing Then Throw New InvalidOperationException("3FUI 返回了空参数快照。")
            Return JsonSerializer.Serialize(preset, preset.GetType())
        Catch ex As TargetInvocationException When ex.InnerException IsNot Nothing
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw()
            Throw
        End Try
    End Function
End Class
