Imports System
Imports System.IO
Imports System.Linq
Imports System.Net.Http
Imports System.Reflection
Imports System.Security.Cryptography
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading.Tasks

Namespace videoenhancer
    Public NotInheritable Class UpdatePackage
        Public Property Path As String = ""
        Public Property Url As String = ""
        Public Property Size As Long
        Public Property Sha256 As String = ""
    End Class

    Public NotInheritable Class UpdateManifest
        Public Property Version As String = ""
        Public Property Package As New UpdatePackage
        Public Property ReleaseUrl As String = ""
    End Class

    ' Ext 版本只下载 ZIP；安装由关闭宿主后的文件覆盖或源码安装脚本完成。
    Public NotInheritable Class PluginUpdater
        Public Shared ReadOnly Property CurrentVersion As String
            Get
                Return GetType(PluginUpdater).Assembly.GetName().Version.ToString(3)
            End Get
        End Property

        Private Shared Function CreateClient() As HttpClient
            Dim client As New HttpClient With {.Timeout = TimeSpan.FromMinutes(5)}
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VideoEnhancer-Ext/" & CurrentVersion)
            Return client
        End Function

        Public Shared Async Function FetchLatestManifestAsync() As Task(Of UpdateManifest)
            Using client = CreateClient()
                Dim json = Await client.GetStringAsync("https://api.github.com/repos/SteveYu000/FFmpegFreeUI-Ext-Plugins/releases?per_page=50")
                Using doc = JsonDocument.Parse(json)
                    For Each release In doc.RootElement.EnumerateArray()
                        If release.GetProperty("draft").GetBoolean() OrElse release.GetProperty("prerelease").GetBoolean() Then Continue For
                        For Each asset In release.GetProperty("assets").EnumerateArray()
                            Dim name = asset.GetProperty("name").GetString()
                            Dim match = Regex.Match(name, "^VideoEnhancer-(\d+\.\d+\.\d+)-win-x64\.zip$", RegexOptions.IgnoreCase)
                            If Not match.Success Then Continue For
                            Dim digest As JsonElement
                            Dim sha = If(asset.TryGetProperty("digest", digest) AndAlso digest.ValueKind = JsonValueKind.String, digest.GetString(), "")
                            If sha.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) Then sha = sha.Substring(7)
                            If Not Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$") Then
                                Dim checksumUrl = release.GetProperty("assets").EnumerateArray().Where(Function(a) a.GetProperty("name").GetString() = name & ".sha256").
                                    Select(Function(a) a.GetProperty("browser_download_url").GetString()).FirstOrDefault()
                                If String.IsNullOrEmpty(checksumUrl) Then Throw New InvalidDataException("ZIP 发布包缺少 SHA-256 校验信息")
                                sha = (Await client.GetStringAsync(checksumUrl)).Split(New Char() {" "c, Microsoft.VisualBasic.ChrW(9), Microsoft.VisualBasic.ChrW(13), Microsoft.VisualBasic.ChrW(10)}, StringSplitOptions.RemoveEmptyEntries)(0)
                            End If
                            If Not Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$") Then Throw New InvalidDataException("ZIP 校验信息无效")
                            Return New UpdateManifest With {.Version = match.Groups(1).Value, .ReleaseUrl = release.GetProperty("html_url").GetString(),
                                .Package = New UpdatePackage With {.Path = name, .Url = asset.GetProperty("browser_download_url").GetString(), .Size = asset.GetProperty("size").GetInt64(), .Sha256 = sha}}
                        Next
                    Next
                End Using
            End Using
            Return Nothing
        End Function

        Public Shared Function HasUpdate(manifest As UpdateManifest) As Boolean
            Return manifest IsNot Nothing AndAlso New Version(manifest.Version) > New Version(CurrentVersion)
        End Function

        Public Shared Async Function DownloadPackageAsync(manifest As UpdateManifest, progress As Action(Of Integer)) As Task(Of String)
            Dim directory = IO.Path.Combine(PortableRuntime.UpdateRoot, "packages", manifest.Version)
            IO.Directory.CreateDirectory(directory)
            Dim destination = IO.Path.Combine(directory, IO.Path.GetFileName(manifest.Package.Path))
            Dim temporary = destination & ".download"
            Try
                Using client = CreateClient(), response = Await client.GetAsync(manifest.Package.Url, HttpCompletionOption.ResponseHeadersRead)
                    response.EnsureSuccessStatusCode()
                    Using source = Await response.Content.ReadAsStreamAsync(), output As New FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, True)
                        Dim buffer(65535) As Byte
                        Dim count As Integer
                        Dim total As Long
                        Do
                            count = Await source.ReadAsync(buffer, 0, buffer.Length)
                            If count = 0 Then Exit Do
                            Await output.WriteAsync(buffer, 0, count)
                            total += count
                            If manifest.Package.Size > 0 Then progress?.Invoke(CInt(Math.Min(100, total * 100 / manifest.Package.Size)))
                        Loop
                        If manifest.Package.Size > 0 AndAlso total <> manifest.Package.Size Then Throw New InvalidDataException("ZIP 大小不匹配")
                    End Using
                End Using
                Using stream = File.OpenRead(temporary)
                    If Not Convert.ToHexString(SHA256.HashData(stream)).Equals(manifest.Package.Sha256, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("ZIP 的 SHA-256 校验失败")
                End Using
                File.Move(temporary, destination, True)
                Return destination
            Finally
                If File.Exists(temporary) Then File.Delete(temporary)
            End Try
        End Function
    End Class
End Namespace
