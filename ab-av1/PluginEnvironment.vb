Imports System.Diagnostics
Imports System.IO
Imports System.Linq

Public NotInheritable Class PluginEnvironment

    Private Sub New()
    End Sub

    Public Shared ReadOnly Property PluginDirectory As String = ResolvePluginDirectory()

    Public Shared ReadOnly Property AbAv1Path As String
        Get
            Return Path.Combine(PluginDirectory, "ab-av1.exe")
        End Get
    End Property

    ''' <summary>让 ab-av1 优先找到插件目录或 FFmpegFreeUI 根目录中的 ffmpeg。</summary>
    Public Shared Sub ConfigureChildProcessEnvironment(startInfo As ProcessStartInfo)
        If startInfo Is Nothing Then Throw New ArgumentNullException(NameOf(startInfo))

        Dim existingPath As String = Nothing
        startInfo.Environment.TryGetValue("PATH", existingPath)
        Dim entries = New String() {
            PluginDirectory,
            Path.GetFullPath(AppContext.BaseDirectory),
            If(existingPath, String.Empty)
        }
        startInfo.Environment("PATH") = String.Join(Path.PathSeparator, entries.Where(
            Function(value) Not String.IsNullOrWhiteSpace(value)))
    End Sub

    Public Shared Function ResolveExecutablePath(fileName As String) As String
        If String.IsNullOrWhiteSpace(fileName) Then Return String.Empty

        Dim directories As New List(Of String) From {
            PluginDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        }
        Dim pathValue = Environment.GetEnvironmentVariable("PATH")
        If Not String.IsNullOrWhiteSpace(pathValue) Then
            directories.AddRange(pathValue.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries Or StringSplitOptions.TrimEntries))
        End If

        For Each directory In directories.Distinct(StringComparer.OrdinalIgnoreCase)
            Try
                If String.IsNullOrWhiteSpace(directory) Then Continue For
                Dim candidate = Path.GetFullPath(Path.Combine(directory.Trim().Trim(""""c), fileName))
                If File.Exists(candidate) Then Return candidate
            Catch
            End Try
        Next
        Return String.Empty
    End Function

    Private Shared Function ResolvePluginDirectory() As String
        Dim location = GetType(PluginEnvironment).Assembly.Location
        If Not String.IsNullOrWhiteSpace(location) Then
            Dim directory = Path.GetDirectoryName(location)
            If Not String.IsNullOrWhiteSpace(directory) Then Return Path.GetFullPath(directory)
        End If

        Return Path.GetFullPath(AppContext.BaseDirectory)
    End Function

End Class
