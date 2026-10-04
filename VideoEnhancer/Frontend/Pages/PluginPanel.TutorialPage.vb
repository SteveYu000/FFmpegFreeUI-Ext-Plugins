Imports System
Imports System.Diagnostics
Imports Process = VideoEnhancer.BackendOperation
Imports System.Drawing
Imports System.IO
Imports System.Reflection
Imports System.Windows.Forms
Imports LakeUI

Namespace videoenhancer
    Public Partial Class PluginPanel
        Private Shared Function BeginnerTutorialMarkdown() As String
            Using resource = GetType(PluginPanel).Assembly.GetManifestResourceStream("VideoEnhancer.ExtGuide")
                If resource Is Nothing Then Return "# VideoEnhancer Ext" & Environment.NewLine & "请阅读安装包内的 README.md。"
                Using reader As New StreamReader(resource, System.Text.Encoding.UTF8)
                    Return reader.ReadToEnd()
                End Using
            End Using
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
            page.Controls.Add(CreateMarkdownViewer(markdown))
            _markdownReady.Add(page)
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
