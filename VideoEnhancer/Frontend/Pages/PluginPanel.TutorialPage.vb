Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Reflection
Imports System.Windows.Forms
Imports LakeUI
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading.Tasks

Namespace videoenhancer
    Public Partial Class PluginPanel
        Private Const TutorialUrl As String = "https://www1.arxchem.top/docs/6-videoenhancer"
        Private Const TutorialApiUrl As String = "https://www1.arxchem.top/api/articles/doc-6-videoenhancer"
        Private Const TutorialImageBaseUrl As String = "https://www1.arxchem.top"

        Private Shared Function ExtTutorialMarkdown() As String
            Using resource = GetType(PluginPanel).Assembly.GetManifestResourceStream("VideoEnhancer.ExtTutorial")
                If resource Is Nothing Then Return "## Ext 版补充" & Environment.NewLine & "请阅读安装包内的 README.md。"
                Using reader As New StreamReader(resource, Encoding.UTF8)
                    Return reader.ReadToEnd()
                End Using
            End Using
        End Function

        Private Shared Function ComposeTutorialMarkdown(original As String, notice As String) As String
            Return "# 使用教程" & Environment.NewLine & Environment.NewLine &
                "原教程作者：ARXChem。[在浏览器打开原教程](" & TutorialUrl & ")。" & Environment.NewLine &
                "原文中的入口和任务方式可能与 Ext 版不同；本页末尾列出对应修改。" & Environment.NewLine & Environment.NewLine &
                notice & Environment.NewLine & Environment.NewLine & original & Environment.NewLine & Environment.NewLine &
                "---" & Environment.NewLine & Environment.NewLine & ExtTutorialMarkdown()
        End Function

        Private Shared Function BeginnerTutorialMarkdown() As String
            Return ComposeTutorialMarkdown("", "正在加载原作者教程；Ext 版补充可先阅读。")
        End Function

        Private Async Sub LoadOnlineTutorialAsync(viewer As MarkDownViewer)
            Dim cachePath = Path.Combine(PortableRuntime.CacheRoot, "Tutorial", "original.md")
            Dim cached = ""
            Try
                If File.Exists(cachePath) Then cached = Await File.ReadAllTextAsync(cachePath, Encoding.UTF8)
                If IsDisposed OrElse viewer.IsDisposed Then Return
                If cached.Length > 0 Then viewer.SetMarkdownImmediate(ComposeTutorialMarkdown(cached, "已显示缓存的原教程，正在检查在线内容。"))
            Catch ex As IOException
                ' 缓存不可读时仍尝试在线读取。
            Catch ex As UnauthorizedAccessException
            End Try
            Try
                Using client As New HttpClient With {.Timeout = TimeSpan.FromSeconds(20)}
                    client.DefaultRequestVersion = Net.HttpVersion.Version11
                    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
                    Dim response = Await client.GetStringAsync(TutorialApiUrl)
                    Dim original As String
                    Using document = JsonDocument.Parse(response)
                        original = document.RootElement.GetProperty("article").GetProperty("content").GetString()
                    End Using
                    If String.IsNullOrWhiteSpace(original) Then Throw New InvalidDataException("原教程内容为空")
                    original = Await CacheTutorialImagesAsync(client, original)
                    If IsDisposed OrElse viewer.IsDisposed Then Return
                    viewer.SetMarkdownImmediate(ComposeTutorialMarkdown(original, "以下为原作者教程，随后是 Ext 版补充。"))
                    Try
                        Directory.CreateDirectory(Path.GetDirectoryName(cachePath))
                        Await File.WriteAllTextAsync(cachePath, original, New UTF8Encoding(False))
                    Catch ex As IOException
                    Catch ex As UnauthorizedAccessException
                    End Try
                End Using
            Catch ex As Exception
                If IsDisposed OrElse viewer.IsDisposed Then Return
                viewer.SetMarkdownImmediate(ComposeTutorialMarkdown(cached,
                    If(cached.Length > 0, "在线教程暂时不可用，以下为已缓存的原文。", "在线教程暂时无法加载，可通过上方链接阅读原文。")))
            End Try
        End Sub

        Private Shared Async Function CacheTutorialImagesAsync(client As HttpClient, markdown As String) As Task(Of String)
            Dim cacheDirectory = Path.Combine(PortableRuntime.CacheRoot, "TutorialImages")
            Dim imageMatches = Regex.Matches(markdown, "!\[[^\]]*\]\((?<url>[^)]+)\)")
            Dim downloads As New Dictionary(Of String, Task(Of String))(StringComparer.OrdinalIgnoreCase)
            Using limiter As New Threading.SemaphoreSlim(4)
                For Each imageMatch As Match In imageMatches
                    Dim imageUrl = imageMatch.Groups("url").Value
                    If imageUrl.StartsWith("/media/", StringComparison.OrdinalIgnoreCase) Then
                        imageUrl = TutorialImageBaseUrl & imageUrl
                    End If
                    If imageUrl.StartsWith(TutorialImageBaseUrl & "/media/", StringComparison.OrdinalIgnoreCase) AndAlso
                       Not downloads.ContainsKey(imageUrl) Then
                        downloads(imageUrl) = DownloadTutorialImageAsync(client, imageUrl, cacheDirectory, limiter)
                    End If
                Next
                If downloads.Count > 0 Then Await Task.WhenAll(downloads.Values)

                For Each imageMatch As Match In imageMatches
                    Dim imageUrl = imageMatch.Groups("url").Value
                    If Regex.IsMatch(imageUrl, "^[A-Za-z]:[\\/]") Then
                        ' 原文中有作者电脑上的 Typora 路径，无法从网站取得这张图片。
                        markdown = markdown.Replace(imageMatch.Value, "*原教程中的这张图片未上传，暂时无法显示。*")
                        Continue For
                    End If
                    If imageUrl.StartsWith("/media/", StringComparison.OrdinalIgnoreCase) Then
                        imageUrl = TutorialImageBaseUrl & imageUrl
                    ElseIf Not imageUrl.StartsWith(TutorialImageBaseUrl & "/media/", StringComparison.OrdinalIgnoreCase) Then
                        Continue For
                    End If
                    Dim imagePath = Await downloads(imageUrl)
                    If imagePath Is Nothing Then
                        ' 单张图片下载失败时保留可点击的原图地址，不影响其余教程内容。
                        markdown = markdown.Replace(imageMatch.Value, "[图片暂时无法加载，点击查看原图](" & imageUrl & ")")
                    Else
                        markdown = markdown.Replace(imageMatch.Groups("url").Value, imagePath.Replace("\", "/"))
                    End If
                Next
            End Using
            Return markdown
        End Function

        Private Shared Async Function DownloadTutorialImageAsync(client As HttpClient, imageUrl As String,
                                                                  cacheDirectory As String,
                                                                  limiter As Threading.SemaphoreSlim) As Task(Of String)
            Await limiter.WaitAsync().ConfigureAwait(False)
            Try
                Dim urlHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imageUrl)))
                Dim imagePath = Path.Combine(cacheDirectory, urlHash & "-fit960.png")
                If Not File.Exists(imagePath) Then
                    Using response = Await client.GetAsync(imageUrl).ConfigureAwait(False)
                        response.EnsureSuccessStatusCode()
                        If response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) <> True Then
                            Throw New InvalidDataException("教程图片响应不是图片")
                        End If
                        Dim imageBytes = Await response.Content.ReadAsByteArrayAsync().ConfigureAwait(False)
                        Directory.CreateDirectory(cacheDirectory)
                        Using imageStream As New MemoryStream(imageBytes)
                            Using sourceImage = Image.FromStream(imageStream)
                                ' 教程图片总解码量过大，滚动时会触发 LakeUI 共享缓存回收。
                                Dim targetWidth = Math.Min(sourceImage.Width, 960)
                                Dim targetHeight = Math.Max(1, CInt(Math.Round(sourceImage.Height * CDbl(targetWidth) / sourceImage.Width)))
                                Using opaqueImage As New Bitmap(targetWidth, targetHeight,
                                                                Imaging.PixelFormat.Format24bppRgb)
                                    Using canvasGraphics = Graphics.FromImage(opaqueImage)
                                        canvasGraphics.Clear(Color.FromArgb(32, 34, 38))
                                        canvasGraphics.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic
                                        canvasGraphics.DrawImage(sourceImage, 0, 0, targetWidth, targetHeight)
                                    End Using
                                    opaqueImage.Save(imagePath, Imaging.ImageFormat.Png)
                                End Using
                            End Using
                        End Using
                    End Using
                End If
                Return imagePath
            Catch
                Return Nothing
            Finally
                limiter.Release()
            End Try
        End Function

        Private Sub BuildMarkdownPage(page As ModernPanel, markdown As String)
            page.Dock = DockStyle.Fill
            page.BackColor = Color.Transparent
            page.BackColor1 = Color.Transparent
            page.BackgroundSource = ModernPanel1
            page.BorderSize = 0
            page.Padding = New Padding(0, 8, 0, 0)
            _markdownSources(page) = If(markdown, "")
        End Sub

        Private Sub EnsureMarkdownPage(page As ModernPanel)
            If page Is Nothing OrElse _markdownReady.Contains(page) Then Return
            Dim markdown As String = ""
            If Not _markdownSources.TryGetValue(page, markdown) Then Return
            Dim viewer = CreateMarkdownViewer(markdown)
            page.Controls.Add(viewer)
            _markdownReady.Add(page)
            If page Is _pageTutorial Then LoadOnlineTutorialAsync(viewer)
        End Sub

        Private Function CreateMarkdownViewer(markdown As String) As MarkDownViewer
            Dim viewer As New MarkDownViewer With {
                .Dock = DockStyle.Fill, .Margin = Padding.Empty, .Padding = New Padding(10, 8, 10, 12),
                .BackColor = Color.Transparent, .BackColor1 = UiSurfaceDark, .BackgroundSource = ModernPanel1,
                .BorderSize = 0, .BorderRadius = 10}
            viewer.ScrollBarWidth = 10
            viewer.ScrollBarTrackColor = UiSurface
            viewer.ScrollBarColor = UiScrollThumb
            viewer.ScrollBarHoverColor = UiScrollThumbHover
            viewer.ForeColor = Color.Silver
            viewer.HeadingColor = UiText
            viewer.HeadingSeparatorColor = UiSeparator
            viewer.BoldColor = UiText
            viewer.LinkColor = UiAccent
            viewer.SelectionColor = UiSurface
            viewer.CodeBackColor = MarkdownViewerCore.DefaultMarkdownCodeBackColor
            viewer.CodeBlockForeColor = Color.Silver
            viewer.TableHeaderBackColor = UiSurface
            viewer.TableBorderColor = UiSeparator
            viewer.HorizontalRuleColor = UiSeparator
            viewer.HorizontalRuleThickness = 2
            AddHandler viewer.LinkClicked,
                Sub(sender, args)
                    Try
                        If args Is Nothing OrElse String.IsNullOrWhiteSpace(args.LinkText) Then Return
                        Process.Start(New ProcessStartInfo With {.FileName = args.LinkText, .UseShellExecute = True})
                    Catch
                        ' 外部链接无法打开时不影响教程内容。
                    End Try
                End Sub
            viewer.SetMarkdownImmediate(markdown)
            Return viewer
        End Function
    End Class
End Namespace
