Imports VideoEnhancer
Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Reflection
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports LakeUI

Namespace videoenhancer

    ''' <summary>"视频超分"插件页面：独立 AI 参数页与视频超分管理页面。</summary>
    Public Partial Class PluginPanel
        Inherits UserControl

        ' 关闭状态的选项框不应因鼠标滚轮经过显示区域而悄悄改变配置。
        ' LakeUI 的下拉列表使用独立窗口，拦截这里的消息不会影响打开列表后的滚动。
        Private NotInheritable Class WheelLockedComboBox
            Inherits LakeComboBox

            Private Const WmMouseWheel As Integer = &H20A
            Private Const WmMouseHWheel As Integer = &H20E

            Protected Overrides Sub WndProc(ByRef m As Message)
                If m.Msg = WmMouseWheel OrElse m.Msg = WmMouseHWheel Then
                    If m.Msg = WmMouseWheel Then
                        SmoothScrollPanel.ForwardWheelToScrollHost(Me, SmoothScrollPanel.WheelDelta(m.WParam))
                    End If
                    Return
                End If
                MyBase.WndProc(m)
            End Sub
        End Class

        ' RTX HDR 参数只允许直接输入整数；关闭 LakeUI 数字框默认的滚轮、方向键和隐藏按钮步进。
        Private NotInheritable Class RtxHdrNumericUpDown
            Inherits ModernNumericUpDown

            Protected Overrides Sub OnMouseWheel(e As MouseEventArgs)
                ' 不修改 HDR 参数；将滚轮交回工作台视口，避免经过数字框时滚动中断。
                SmoothScrollPanel.ForwardWheelToScrollHost(Me, e.Delta)
            End Sub

            Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
                Select Case e.KeyCode
                    Case Keys.Up, Keys.Down, Keys.PageUp, Keys.PageDown
                        e.Handled = True
                        Return
                End Select
                MyBase.OnKeyDown(e)
            End Sub

            Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
                ' ButtonAreaWidth 最小为 1；再拦截末端少量像素，确保不可见区域也不会触发步进。
                If e.Button = MouseButtons.Left AndAlso
                   e.X >= Math.Max(0, ClientSize.Width - 6) Then
                    Focus()
                    Return
                End If
                MyBase.OnMouseDown(e)
            End Sub

            ' LakeUI 左对齐文本在控件由窄变宽时不会主动清除旧的横向滚动偏移。
            ' 临时使用居中对齐让基类在尺寸/字体变化时执行“文本未溢出则归零”，
            ' 再恢复左对齐，避免四位数的第一位在启动布局期间被裁掉。
            Protected Overrides Sub OnSizeChanged(e As EventArgs)
                ResetViewportDuringLayout(Sub() MyBase.OnSizeChanged(e))
            End Sub

            Protected Overrides Sub OnFontChanged(e As EventArgs)
                ResetViewportDuringLayout(Sub() MyBase.OnFontChanged(e))
            End Sub

            Private Sub ResetViewportDuringLayout(action As Action)
                Dim originalAlign = TextAlign
                If originalAlign = ModernNumericUpDown.TextAlignMode.Left Then
                    TextAlign = ModernNumericUpDown.TextAlignMode.Center
                End If
                Try
                    action.Invoke()
                Finally
                    If originalAlign = ModernNumericUpDown.TextAlignMode.Left Then
                        TextAlign = originalAlign
                    End If
                End Try
            End Sub
        End Class

        ' 与官方 API 示例插件保持一致：#181818 背景、半透明灰控件、低饱和文字和单一蓝色强调。
        Private Shared ReadOnly UiCanvas As Color = Color.FromArgb(24, 24, 24)
        Private Shared ReadOnly UiSurface As Color = Color.FromArgb(40, 220, 220, 220)
        Private Shared ReadOnly UiSurfaceRaised As Color = Color.FromArgb(40, 220, 220, 220)
        Private Shared ReadOnly UiSurfaceHover As Color = Color.FromArgb(60, 220, 220, 220)
        Private Shared ReadOnly UiSurfaceDark As Color = Color.FromArgb(40, 0, 0, 0)
        Private Shared ReadOnly UiSeparator As Color = Color.FromArgb(80, 220, 220, 220)
        Private Shared ReadOnly UiScrollThumb As Color = Color.FromArgb(80, 220, 220, 220)
        Private Shared ReadOnly UiScrollThumbHover As Color = Color.FromArgb(120, 220, 220, 220)
        Private Shared ReadOnly UiStrokeSoft As Color = Color.Transparent
        Private Shared ReadOnly UiAccent As Color = Color.FromArgb(71, 156, 255)
        Private Shared ReadOnly UiAccentHover As Color = Color.FromArgb(110, 71, 156, 255)
        Private Shared ReadOnly UiAccentPressed As Color = Color.FromArgb(140, 71, 156, 255)
        Private Shared ReadOnly UiSuccess As Color = Color.FromArgb(63, 205, 135)
        Private Shared ReadOnly UiDanger As Color = Color.FromArgb(235, 93, 93)
        Private Shared ReadOnly UiText As Color = Color.FromArgb(220, 220, 220)
        Private Shared ReadOnly UiTextSecondary As Color = Color.FromArgb(176, 220, 220, 220)
        Private Shared ReadOnly UiTextMuted As Color = Color.FromArgb(120, 255, 255, 255)

        ' 统一的 96 DPI 紧凑尺寸；字体保持不变，由布局容器负责换算实际 DPI。
        Private Const UiControlHeight As Integer = 28
        Private Const UiFieldHeight As Integer = 56
        Private Const UiFieldCaptionHeight As Integer = 20
        Private Const UiFieldEditorTop As Integer = 23
        Private Const UiRowHeight As Integer = 36
        Private Const UiColumnGap As Integer = 8
        Private Const UiCornerRadius As Integer = 6

        Private _config As PluginConfig
        Private _uiReady As Boolean = False
        ' ── 选项卡分栏：超分主界面 / 实时预览 / 高级功能 / 模型转换器 ──
        Private ReadOnly _tabs As New ModernTabListControl()
        ' 3FUI 通过字段名和控件名 ModernPanel1 绑定 LakeUI 背景穿透缓存。
        Private ReadOnly ModernPanel1 As New ModernPanel()
        Private ReadOnly _pageUpscale As New SmoothScrollPanel()
        Private ReadOnly _pagePreview As New ModernPanel()
        Private ReadOnly _pageDownloader As New ModernPanel()
        Private ReadOnly _pageConverter As New ModernPanel()
        Private ReadOnly _pageImporter As New ModernPanel()
        Private ReadOnly _pageTutorial As New ModernPanel()
        Private ReadOnly _markdownSources As New Dictionary(Of ModernPanel, String)()
        Private ReadOnly _markdownReady As New HashSet(Of ModernPanel)()
        ' 页签懒加载钩子索引：构建页签时按实际顺序捕获，插入新页后不再依赖固定数字。
        Private _tabIndexDownloader As Integer = -1
        Private _tabIndexImporter As Integer = -1
        Private _tabIndexTutorial As Integer = -1
        ' ModernContextMenu 的菜单项不是 WinForms 控件，使用 LakeUI 的浮动提示窗显示当前悬停模型说明。
        Private NotInheritable Class ModelMenuToolTipController
            Private ReadOnly _menus As New HashSet(Of ModernContextMenu)()
            Private ReadOnly _tooltips As Dictionary(Of ModernContextMenu.ModernMenuItem, String)
            Private ReadOnly _timer As New Timer() With {.Interval = 100}
            Private _tipForm As FloatingToolTipForm
            Private _tipOwner As Form
            Private ReadOnly _tipStyle As FloatingToolTipStyle
            Private _hoveredItem As ModernContextMenu.ModernMenuItem
            Private _shownItem As ModernContextMenu.ModernMenuItem
            Private _hoverSinceUtc As DateTime
            Private _closed As Boolean

            Public Sub New(rootMenu As ModernContextMenu,
                           tooltips As Dictionary(Of ModernContextMenu.ModernMenuItem, String))
                _tooltips = If(tooltips,
                    New Dictionary(Of ModernContextMenu.ModernMenuItem, String)())
                RegisterMenu(rootMenu)
                _tipStyle = New FloatingToolTipStyle() With {
                    .Font = New Font("Microsoft YaHei UI", 9.0F, FontStyle.Regular),
                    .BackColor = Color.FromArgb(245, 42, 42, 42),
                    .ForeColor = UiText,
                    .BorderColor = Color.FromArgb(96, 96, 96),
                    .BorderSize = 1,
                    .BorderRadius = 8,
                    .Padding = New Padding(10, 8, 10, 8),
                    .MaxWidth = 360
                }
                AddHandler _timer.Tick, AddressOf OnTimerTick
            End Sub

            Public Sub Start()
                If _closed Then Return
                _timer.Start()
            End Sub

            Public Sub Close()
                If _closed Then Return
                _closed = True
                Try
                    _timer.Stop()
                    RemoveHandler _timer.Tick, AddressOf OnTimerTick
                    _timer.Dispose()
                Catch
                End Try
                HideTip()
                Try
                    If _tipForm IsNot Nothing Then _tipForm.Dispose()
                Catch
                End Try
                Try
                    If _tipStyle.Font IsNot Nothing Then _tipStyle.Font.Dispose()
                Catch
                End Try
            End Sub

            Private Sub RegisterMenu(menu As ModernContextMenu)
                If menu Is Nothing OrElse Not _menus.Add(menu) Then Return
                For Each item As ModernContextMenu.ModernMenuItem In menu.Items
                    If item IsNot Nothing AndAlso item.SubMenu IsNot Nothing Then
                        RegisterMenu(item.SubMenu)
                    End If
                Next
            End Sub

            Private Sub OnTimerTick(sender As Object, e As EventArgs)
                If _closed Then Return
                Try
                    Dim popup As Form = Nothing
                    Dim item As ModernContextMenu.ModernMenuItem = Nothing
                    Dim itemBounds As Rectangle
                    If Not TryGetHoveredItem(popup, item, itemBounds) Then
                        ResetHover()
                        Return
                    End If

                    Dim tooltipText As String = Nothing
                    If Not _tooltips.TryGetValue(item, tooltipText) OrElse
                       String.IsNullOrWhiteSpace(tooltipText) Then
                        ResetHover()
                        Return
                    End If

                    If Not Object.ReferenceEquals(_hoveredItem, item) Then
                        _hoveredItem = item
                        _shownItem = Nothing
                        _hoverSinceUtc = DateTime.UtcNow
                        HideTip()
                        Return
                    End If
                    If Object.ReferenceEquals(_shownItem, item) AndAlso
                       _tipForm IsNot Nothing AndAlso Not _tipForm.IsDisposed AndAlso _tipForm.Visible Then Return
                    If (DateTime.UtcNow - _hoverSinceUtc).TotalMilliseconds < 350 Then Return

                    ShowTip(popup, itemBounds, item, tooltipText)
                Catch
                    ResetHover()
                End Try
            End Sub

            Private Function TryGetHoveredItem(ByRef popup As Form,
                                               ByRef item As ModernContextMenu.ModernMenuItem,
                                               ByRef itemBounds As Rectangle) As Boolean
                Dim cursorPoint As Point = Cursor.Position
                Try
                    For index = Application.OpenForms.Count - 1 To 0 Step -1
                        Dim candidate = Application.OpenForms(index)
                        If candidate Is Nothing OrElse candidate.IsDisposed OrElse Not candidate.Visible OrElse
                           Not candidate.Bounds.Contains(cursorPoint) OrElse
                           Not String.Equals(candidate.GetType().FullName,
                               "LakeUI.ModernContextMenu+MenuPopupForm", StringComparison.Ordinal) Then
                            Continue For
                        End If

                        Dim menu = GetPopupMenu(candidate)
                        If menu Is Nothing OrElse Not _menus.Contains(menu) Then Continue For
                        Dim location = candidate.PointToClient(cursorPoint)
                        Dim itemIndex = GetPopupItemIndex(candidate, location)
                        If itemIndex < 0 OrElse itemIndex >= menu.Items.Count Then Continue For
                        Dim candidateItem = menu.Items(itemIndex)
                        If candidateItem Is Nothing OrElse candidateItem.IsSeparator OrElse candidateItem.IsDescription Then
                            Continue For
                        End If

                        popup = candidate
                        item = candidateItem
                        itemBounds = GetPopupItemBounds(candidate, itemIndex, location)
                        Return True
                    Next
                Catch
                End Try
                Return False
            End Function

            Private Shared Function GetPopupMenu(popup As Form) As ModernContextMenu
                Try
                    Dim field = popup.GetType().GetField("菜单",
                        BindingFlags.Instance Or BindingFlags.NonPublic)
                    If field Is Nothing Then
                        field = popup.GetType().GetFields(
                            BindingFlags.Instance Or BindingFlags.NonPublic).
                            FirstOrDefault(Function(candidate) GetType(ModernContextMenu).IsAssignableFrom(candidate.FieldType))
                    End If
                    If field Is Nothing Then Return Nothing
                    Return TryCast(field.GetValue(popup), ModernContextMenu)
                Catch
                    Return Nothing
                End Try
            End Function

            Private Shared Function GetPopupItemIndex(popup As Form, location As Point) As Integer
                Try
                    Dim method = popup.GetType().GetMethod("获取项目索引",
                        BindingFlags.Instance Or BindingFlags.NonPublic)
                    If method Is Nothing Then Return -1
                    Dim result = method.Invoke(popup, New Object() {location, True})
                    Return If(result Is Nothing, -1, CInt(result))
                Catch
                    Return -1
                End Try
            End Function

            Private Shared Function GetPopupItemBounds(popup As Form,
                                                       itemIndex As Integer,
                                                       location As Point) As Rectangle
                Try
                    Dim field = popup.GetType().GetField("项目区域列表",
                        BindingFlags.Instance Or BindingFlags.NonPublic)
                    Dim areas = If(field Is Nothing, Nothing,
                        TryCast(field.GetValue(popup), System.Collections.IList))
                    If areas IsNot Nothing AndAlso itemIndex >= 0 AndAlso itemIndex < areas.Count AndAlso
                       TypeOf areas(itemIndex) Is Rectangle Then
                        Return DirectCast(areas(itemIndex), Rectangle)
                    End If
                Catch
                End Try
                Return New Rectangle(location, New Size(1, 1))
            End Function

            Private Sub ShowTip(popup As Form,
                                itemBounds As Rectangle,
                                item As ModernContextMenu.ModernMenuItem,
                                text As String)
                Dim screenBounds = popup.RectangleToScreen(itemBounds)
                Dim workingArea = Screen.FromRectangle(screenBounds).WorkingArea
                Dim side As FloatingToolTipSide
                Dim anchor As Point
                If workingArea.Right - screenBounds.Right >= 380 Then
                    side = FloatingToolTipSide.Right
                    anchor = New Point(screenBounds.Right,
                                       screenBounds.Top + Math.Max(1, screenBounds.Height \ 2))
                Else
                    side = FloatingToolTipSide.Left
                    anchor = New Point(screenBounds.Left,
                                       screenBounds.Top + Math.Max(1, screenBounds.Height \ 2))
                End If
                ' 提示窗随当前菜单弹窗创建，避免宿主失活或子菜单关闭后复用已释放的窗体。
                If _tipForm Is Nothing OrElse _tipForm.IsDisposed OrElse Not Object.ReferenceEquals(_tipOwner, popup) Then
                    If _tipForm IsNot Nothing Then _tipForm.Dispose()
                    _tipOwner = popup
                    _tipForm = New FloatingToolTipForm(popup)
                End If
                _tipForm.ShowTip(text, anchor, _tipStyle, 8, side)
                _shownItem = If(_tipForm.Visible, item, Nothing)
            End Sub

            Private Sub ResetHover()
                _hoveredItem = Nothing
                _shownItem = Nothing
                _hoverSinceUtc = DateTime.MinValue
                HideTip()
            End Sub

            Private Sub HideTip()
                Try
                    If _tipForm IsNot Nothing AndAlso Not _tipForm.IsDisposed Then _tipForm.Hide()
                Catch
                End Try
            End Sub
        End Class

        Private NotInheritable Class ModelImportResponse
            Public Property Success As Boolean
            Public Property Source As String = ""
            Public Property Id As String = ""
            Public Property InstalledPath As String = ""
            Public Property Task As String = ""
            Public Property Architecture As String = ""
            Public Property Purpose As String = ""
            Public Property Scale As Integer
            Public Property Backends As String() = Array.Empty(Of String)()
            Public Property [Error] As String = ""
        End Class
        Private ReadOnly _statusClearTimer As New Timer() With {.Interval = 5000}
        ''' <summary>插件面板实例（编码队列右键「预览输出」等外部入口使用）。</summary>
        Friend Shared Current As PluginPanel

        Public Sub New(config As PluginConfig, Optional previewOnly As Boolean = False, Optional parameterMode As Boolean = False)
            _config = If(config, New PluginConfig())
            _parameterMode = parameterMode
            Current = Me
            SuspendLayout()
            AutoScaleMode = AutoScaleMode.None
            Try
                If Not LakeUiRequiredVersionAvailable() Then
                    InitializeDependencyErrorUi()
                    Return
                End If
                InitializeUi()
                AddHandler _config.Saved, AddressOf OnConfigurationSaved
                If previewOnly Then
                    _uiReady = True
                    RefreshUi()
                Else
                    AddHandler Load, AddressOf OnPanelLoad
                End If
            Finally
                ' 页面和全部子控件完成 96 DPI 布局后再统一缩放，避免构建途中发生字体缩放。
                AutoScaleMode = AutoScaleMode.Dpi
                AutoScaleDimensions = New SizeF(96.0F, 96.0F)
                ResumeLayout(True)
            End Try
        End Sub

        Protected Overrides Sub ScaleControl(factor As SizeF, specified As BoundsSpecified)
            MyBase.ScaleControl(factor, specified)
            ' LakeUI 只把已访问的页加入控件树，未访问页也必须参与本次缩放。
            If _tabs Is Nothing Then Return
            Dim pageFactor = New SizeF(
                If((specified And BoundsSpecified.Width) <> 0, factor.Width, 1.0F),
                If((specified And BoundsSpecified.Height) <> 0, factor.Height, 1.0F))
            If pageFactor = New SizeF(1.0F, 1.0F) Then Return
            For Each page In PluginPages()
                If page IsNot Nothing AndAlso Not page.IsDisposed AndAlso Not Contains(page) Then
                    page.Scale(pageFactor)
                End If
            Next
        End Sub

        Private Function PluginPages() As ModernPanel()
            Return New ModernPanel() {_pageUpscale, _pageHome, _pagePreview, _pageModelManagement, _pageDownloader, _pageConverter, _pageImporter, _pageSegmented, _pageTutorial}
        End Function

        Public ReadOnly Property IsEnabled As Boolean
            Get
                Return _config.Enabled
            End Get
        End Property

        Private Sub OnPanelLoad(sender As Object, e As EventArgs)
            _uiReady = True
            RefreshUi()
            If Not _parameterMode AndAlso _config.AutoCheckUpdates Then OnCheckUpdates(Me, EventArgs.Empty)
            AddHandler _tabs.SelectedIndexChanged, AddressOf OnTabChanged
        End Sub

        Private Async Sub StartAutomaticUpdateCheck()
            Await Task.Delay(1500)
            If IsDisposed Then Return
            Await CheckForUpdatesAsync(True)
        End Sub

        Private Async Sub OnCheckUpdates(sender As Object, e As EventArgs)
            Await CheckForUpdatesAsync(False)
        End Sub

        Private Async Function CheckForUpdatesAsync(silent As Boolean) As Task
            If _updateCheckBusy Then Return
            _updateCheckBusy = True
            _btnCheckUpdates.Enabled = False
            Try
                _availableUpdate = Await PluginUpdater.FetchLatestManifestAsync()
                _btnZipUpdate.Enabled = PluginUpdater.HasUpdate(_availableUpdate)
                If _availableUpdate Is Nothing Then
                    ShowStatus("尚未发布 Ext 版本的 ZIP 安装包。当前版本 v" & PluginUpdater.CurrentVersion, False)
                ElseIf PluginUpdater.HasUpdate(_availableUpdate) Then
                    ShowStatus("可更新至 v" & _availableUpdate.Version & "；ZIP 大小 " & FormatDownloadSize(_availableUpdate.Package.Size), False)
                ElseIf Not silent Then
                    ShowStatus("当前已是最新 Ext 版本 v" & PluginUpdater.CurrentVersion, False)
                End If
            Catch ex As Exception
                If Not silent Then ShowStatus("检查更新失败：" & ex.Message, True)
            Finally
                _updateCheckBusy = False
                If Not IsDisposed Then _btnCheckUpdates.Enabled = True
            End Try
        End Function

        Private Async Sub OnDownloadPluginUpdate(sender As Object, e As EventArgs)
            If _availableUpdate Is Nothing Then Await CheckForUpdatesAsync(False)
            If _availableUpdate Is Nothing Then Return
            _btnZipUpdate.Enabled = False
            Try
                Dim package = Await PluginUpdater.DownloadPackageAsync(_availableUpdate, Sub(percent) ShowStatus("正在下载 ZIP：" & percent & "%", False))
                ShowStatus("已校验 ZIP：" & package & "。关闭宿主后解压到 Plugin 目录即可更新。", False)
                System.Diagnostics.Process.Start(New ProcessStartInfo("explorer.exe", "/select," & Microsoft.VisualBasic.ChrW(34) & package & Microsoft.VisualBasic.ChrW(34)) With {.UseShellExecute = True})
            Catch ex As Exception
                ShowStatus("下载更新失败：" & ex.Message, True)
            Finally
                If Not IsDisposed Then _btnZipUpdate.Enabled = True
            End Try
        End Sub

        Private Sub InitializeUi()
            BackColor = UiCanvas
            Dock = DockStyle.Fill
            MinimumSize = New Size(640, 420)
            Font = New Font("Microsoft YaHei UI", 10.0F)
            ModernPanel1.Name = "ModernPanel1"
            ModernPanel1.Dock = DockStyle.Fill
            ModernPanel1.BackColor = Color.Transparent
            ModernPanel1.BackColor1 = Color.Transparent
            ModernPanel1.BorderSize = 0
            ModernPanel1.Padding = New Padding(16, 12, 16, 12)
            If _parameterMode Then
                BuildOfficialUpscalePage()
                BuildOfficialSegmentedPage()
                ModernPanel1.Controls.Add(_pageUpscale)
            Else
                BuildTabs()
                ModernPanel1.Controls.Add(_tabs)
            End If
            Controls.Add(ModernPanel1)
            BindScrollableGpuBackgroundSources(ModernPanel1, ModernPanel1)
            AddHandler ClientSizeChanged, Sub() SyncUpscaleRootBounds()
            AddHandler Layout, Sub() SyncUpscaleRootBounds()
        End Sub

        ' ────────────────────────── 选项卡分栏 ──────────────────────────

        Private Sub BuildTabs()
            _tabs.Dock = DockStyle.Fill
            _tabs.BackColor = Color.Transparent
            _tabs.TabStripBackColor = Color.Transparent
            _tabs.ContentBackColor = Color.Transparent
            _tabs.BackgroundSource = ModernPanel1
            _tabs.TabStripWidth = 156
            _tabs.TabItemHeight = 34
            _tabs.TabItemSpacing = 0
            _tabs.TabItemSelectedBackColor = UiSurface
            _tabs.TabItemHoverBackColor = UiSurfaceHover
            _tabs.AnimationDuration = 0
            BuildHomePage()
            BuildOfficialPreviewPage()
            BuildModelManagementPage()
            BuildOfficialModelDownloadPage()
            BuildOfficialConverterPage()
            BuildOfficialImporterPage()
            BuildMarkdownPage(_pageTutorial, BeginnerTutorialMarkdown())
            For Each page As ModernPanel In PluginPages()
                page.BackColor = Color.Transparent
                page.BackColor1 = Color.Transparent
                page.BorderSize = 0
                page.BackgroundSource = ModernPanel1
            Next
            _tabs.Items.Add(New ModernTabListControl.ModernTabPage() With {.Text = "首页", .BoundControl = _pageHome})
            _tabs.Items.Add(New ModernTabListControl.ModernTabPage() With {.Text = "实时预览", .BoundControl = _pagePreview})
            _tabs.Items.Add(New ModernTabListControl.ModernTabPage() With {.Text = "模型管理", .BoundControl = _pageModelManagement})
            _tabs.Items.Add(New ModernTabListControl.ModernTabPage() With {.Text = "模型下载", .BoundControl = _pageDownloader})
            _tabIndexDownloader = _tabs.Items.Count - 1
            _tabs.Items.Add(New ModernTabListControl.ModernTabPage() With {.Text = "模型转换", .BoundControl = _pageConverter})
            _tabs.Items.Add(New ModernTabListControl.ModernTabPage() With {.Text = "模型导入", .BoundControl = _pageImporter})
            _tabIndexImporter = _tabs.Items.Count - 1
            _tabs.Items.Add(New ModernTabListControl.ModernTabPage() With {.Text = "使用教程", .BoundControl = _pageTutorial})
            _tabIndexTutorial = _tabs.Items.Count - 1
            _tabs.SelectedIndex = 0
        End Sub

        ' ────────────────────────── 超分主界面页 ──────────────────────────

        Private Shared Function CreateOfficialValueBox(valueControl As Control) As ModernPanel
            Dim box As New ModernPanel With {
                .Dock = DockStyle.Fill,
                .Margin = New Padding(0, 4, 0, 4),
                .Padding = New Padding(8, 0, 8, 0),
                .BackColor = Color.Transparent,
                .BackColor1 = UiSurface,
                .BorderColor = Color.Transparent,
                .BorderSize = 0,
                .BorderRadius = UiCornerRadius
            }
            valueControl.Dock = DockStyle.Fill
            valueControl.Margin = Padding.Empty
            box.Controls.Add(valueControl)
            ' 文字应采样文件框的半透明底色，不能直接采样宿主背景而挖空框内区域。
            Dim label = TryCast(valueControl, HtmlColorLabel)
            If label IsNot Nothing Then
                label.BackgroundSource = box
            End If
            Return box
        End Function

        Private Shared Sub ConfigureOfficialTextBox(textBox As ModernTextBox, waterText As String)
            textBox.Dock = DockStyle.Fill
            textBox.Margin = New Padding(0, 4, 0, 4)
            textBox.Padding = New Padding(8, 0, 8, 0)
            textBox.Font = New Font("Microsoft YaHei UI", 10.0F)
            textBox.BackColor1 = UiSurfaceRaised
            textBox.ForeColor = UiText
            textBox.WaterText = waterText
            textBox.WaterTextForeColor = UiTextMuted
            textBox.CaretColor = UiText
            textBox.SelectionColor = UiSurfaceHover
            textBox.BorderColor = Color.Transparent
            textBox.BorderColorFocus = Color.FromArgb(80, 220, 220, 220)
            textBox.BorderSize = 0
            textBox.BorderRadius = UiCornerRadius
            textBox.MultiLine = False
        End Sub

        Private Shared Function CreateOfficialSeparator() As Control
            Dim host As New ModernPanel With {
                .Margin = Padding.Empty,
                .Padding = Padding.Empty,
                .BackColor = Color.Transparent,
                .BackColor1 = Color.Transparent,
                .BorderSize = 0
            }
            Dim line As New ModernPanel With {
                .BackColor = Color.Transparent,
                .BackColor1 = Color.FromArgb(58, 220, 220, 220),
                .BorderSize = 0
            }
            line.Anchor = AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Top
            host.Controls.Add(line)
            AddHandler host.Layout,
                Sub(sender, e)
                    line.SetBounds(0, Math.Max(0, (host.ClientSize.Height - 1) \ 2),
                        host.ClientSize.Width, 1)
                End Sub
            Return host
        End Function

        Private Shared Function BuildOfficialModeHeader(title As String, description As String,
                                                        switchControl As LakeUI.BooleanSwitch,
                                                        stateControl As Control,
                                                        Optional halfSwitch As LakeUI.BooleanSwitch = Nothing,
                                                        Optional stateWidth As Single = 112.0F) As Control
            Dim titleLabel = CreateTextLabel(title, 12.0F, FontStyle.Regular, UiText)
            titleLabel.Margin = Padding.Empty
            titleLabel.TextAlign = ContentAlignment.MiddleLeft
            Dim titleWidth = Math.Max(84, MeasureTextWidth96(title, titleLabel.Font) + 4)
            Dim row As ModernHorizontalPanel
            Dim halfLabel As LakeTextLabel = Nothing
            If halfSwitch Is Nothing Then
                row = New ModernHorizontalPanel(
                    CSng(titleWidth), CSng(UiColumnGap), 40.0F, -1.0F, CSng(stateWidth))
            Else
                halfLabel = CreateTextLabel("半精度推理", 11.0F, FontStyle.Regular, UiTextSecondary)
                halfLabel.AutoSize = False
                halfLabel.Dock = DockStyle.Fill
                halfLabel.TextAlign = ContentAlignment.MiddleCenter
                halfLabel.Margin = Padding.Empty
                Dim halfLabelWidth = Math.Max(96,
                    MeasureTextWidth96(halfLabel.Text, halfLabel.Font) + 10)
                row = New ModernHorizontalPanel(
                    CSng(titleWidth), CSng(UiColumnGap), 40.0F, 12.0F, CSng(halfLabelWidth), 6.0F, 40.0F, -1.0F,
                    CSng(stateWidth))
            End If
            switchControl.Anchor = AnchorStyles.None
            switchControl.Margin = Padding.Empty
            Dim descriptionLabel = CreateOfficialCaption(description)
            descriptionLabel.TextAlign = ContentAlignment.MiddleLeft
            descriptionLabel.Margin = New Padding(14, 0, 0, 0)
            Dim stateLabel = TryCast(stateControl, HtmlColorLabel)
            If stateLabel IsNot Nothing Then
                stateLabel.Dock = DockStyle.Fill
                stateLabel.Margin = Padding.Empty
                stateLabel.Padding = Padding.Empty
                stateLabel.AutoSize = False
                stateLabel.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleRight
            Else
                Dim stateButton = TryCast(stateControl, ModernButton)
                If stateButton IsNot Nothing Then
                    ' ModernButton 的单行绘制路径默认不启用 wordWrap；透明化后只作为右侧状态文本使用。
                    stateButton.Dock = DockStyle.Fill
                    stateButton.Margin = Padding.Empty
                    stateButton.Padding = Padding.Empty
                    stateButton.AutoSize = False
                    stateButton.Font = New Font("Microsoft YaHei UI", 10.0F, FontStyle.Bold)
                    stateButton.TextAlign = ModernButton.TextAlignEnum.Right
                    stateButton.BackColor = Color.Transparent
                    stateButton.BackColor1 = Color.Transparent
                    stateButton.BackColor2 = Color.Transparent
                    stateButton.HoverBackColor1 = Color.Transparent
                    stateButton.HoverBackColor2 = Color.Transparent
                    stateButton.PressedBackColor1 = Color.Transparent
                    stateButton.PressedBackColor2 = Color.Transparent
                    stateButton.BorderColor = Color.Transparent
                    stateButton.HoverBorderColor = Color.Transparent
                    stateButton.PressedBorderColor = Color.Transparent
                    stateButton.BorderSize = 0
                    stateButton.BorderRadius = 0
                    stateButton.RippleEnabled = False
                    stateButton.HoldClickEnabled = False
                    stateButton.TabStop = False
                End If
            End If
            row.AddColumn(titleLabel, 0)
            row.AddColumn(switchControl, 2)
            If halfSwitch Is Nothing Then
                row.AddColumn(descriptionLabel, 3)
                row.AddColumn(stateControl, 4)
            Else
                halfSwitch.Anchor = AnchorStyles.None
                halfSwitch.Margin = Padding.Empty
                row.AddColumn(halfLabel, 4)
                row.AddColumn(halfSwitch, 6)
                row.AddColumn(descriptionLabel, 7)
                row.AddColumn(stateControl, 8)
            End If
            Return row
        End Function

        Private Shared Sub AddWorkbenchControl(root As DpiLayoutPanel, control As Control,
                                               top As Integer, height As Integer,
                                               leftRatio As Single, rightRatio As Single,
                                               Optional leftOffset As Integer = 0,
                                               Optional rightOffset As Integer = 0)
            control.Dock = DockStyle.None
            control.Anchor = AnchorStyles.Top Or AnchorStyles.Left
            Dim arrange =
                Sub()
                    Dim left = CInt(Math.Round(root.ClientSize.Width * leftRatio)) + root.ScaleX(leftOffset)
                    Dim right = CInt(Math.Round(root.ClientSize.Width * rightRatio)) + root.ScaleX(rightOffset)
                    control.SetBounds(left, root.ScaleY(top), Math.Max(0, right - left), root.ScaleY(height))
                End Sub
            root.Controls.Add(control)
            AddHandler root.Layout, Sub(sender, e) arrange()
            arrange()
        End Sub

        Private Shared Sub AddWorkbenchRow(root As DpiLayoutPanel, control As Control,
                                           top As Integer, height As Integer)
            AddWorkbenchControl(root, control, top, height, 0.0F, 1.0F)
        End Sub

        ''' <summary>参照 3FUI 编码队列：固定列按 DPI 换算，首列使用视口的剩余宽度。</summary>
        Private Shared Sub ConfigureDpiListColumns(list As UltraDetailListView, minimumFirstWidth As Integer)
            Dim lastScale As Double = 1.0R
            Dim arrange As Action =
                Sub()
                    If list.IsDisposed OrElse list.Columns.Count = 0 Then Return
                    Dim scale = If(list.IsHandleCreated, list.DeviceDpi / 96.0R, 1.0R)
                    list.BeginUpdate()
                    Try
                        Dim fixedWidth = 0
                        For index = 1 To list.Columns.Count - 1
                            ' 保留用户拖动后的宽度，只在 DPI 改变时按比例换算。
                            If Math.Abs(scale - lastScale) > 0.001R Then
                                list.Columns(index).Width = CInt(Math.Round(list.Columns(index).Width * scale / lastScale))
                            End If
                            fixedWidth += list.Columns(index).Width
                        Next
                        lastScale = scale
                        list.Columns(0).Width = Math.Max(CInt(Math.Round(minimumFirstWidth * scale)),
                            list.ClientSize.Width - list.Padding.Horizontal - CInt(Math.Round(18 * scale)) - fixedWidth)
                    Finally
                        list.EndUpdate()
                    End Try
                End Sub
            AddHandler list.HandleCreated, Sub(sender, e) arrange()
            AddHandler list.ClientSizeChanged, Sub(sender, e) arrange()
            AddHandler list.DpiChangedAfterParent, Sub(sender, e) arrange()
            arrange()
        End Sub

        ''' <summary>对齐 3FUI 准备文件页的半透明列表；保留各页面的列、行高和交互配置。</summary>
        Private Sub ConfigureTransparentListAppearance(list As UltraDetailListView)
            list.BackColor = Color.Transparent
            list.BackgroundColor = UiSurface
            list.BackgroundSource = ModernPanel1
            list.BorderColor = Color.Transparent
            list.BorderSize = 0
            list.BorderRadius = 10
            list.HeaderBackColor = Color.Transparent
            list.HeaderForeColor = UiTextSecondary
            list.HeaderBorderColor = UiSurface
            list.HeaderBorderWidth = 2
            list.ItemForeColor = UiTextSecondary
            list.ItemHoverBackColor = UiSurfaceHover
            list.ItemSelectedBackColor = UiSurface
            list.ItemCornerRadius = 10
            ' 准备文件页不使用分组；下载页的分组也不能沿用 LakeUI 的不透明默认值。
            list.GroupBackColor = UiSurfaceDark
            list.GroupForeColor = UiText
            list.GroupBorderColor = UiSurface
            list.ScrollBarWidth = 10
            list.ScrollBarTrackColor = UiSurface
            list.ScrollBarThumbColor = UiScrollThumb
            list.ScrollBarThumbHoverColor = UiScrollThumbHover
        End Sub

        ''' <summary>
        ''' 按 LakeUI V5 的显式 BackgroundSource 语义，为滚动页内的每个 GPU 控件
        ''' 注册同一个稳定背景源，减少嵌套层级之间的取景差异。滚动时仍需通过
        ''' 渲染事务同步位置变化和子表面的背景提交。
        ''' </summary>
        Private Shared Sub BindScrollableGpuBackgroundSources(root As Control, source As Control, Optional replaceExisting As Boolean = False)
            If root Is Nothing OrElse source Is Nothing Then Return

            Dim provider = TryCast(root, D3D_IBackgroundSourceProvider)
            If provider IsNot Nothing Then
                Dim currentSource As Control = Nothing
                If replaceExisting OrElse Not provider.TryGetBackgroundSource(currentSource) OrElse currentSource Is Nothing Then
                    Dim sourceProperty = root.GetType().GetProperty(
                        "BackgroundSource", BindingFlags.Instance Or BindingFlags.Public)
                    If sourceProperty IsNot Nothing AndAlso sourceProperty.CanWrite AndAlso
                       sourceProperty.PropertyType.IsAssignableFrom(source.GetType()) Then
                        sourceProperty.SetValue(root, source)
                    End If
                End If
            End If

            For Each child As Control In root.Controls
                BindScrollableGpuBackgroundSources(child, source, replaceExisting)
            Next
        End Sub

        ''' <summary>BooleanSwitch 按宿主窗口的实际 DPI 重新计算尺寸（96 DPI 基准为 38×20）。</summary>
        Private Shared Sub ConfigureDpiSwitch(switchControl As LakeUI.BooleanSwitch)
            switchControl.TrackColorOn = UiAccent
            switchControl.HoverTrackColorOn = UiAccentHover
            switchControl.PressedTrackColorOn = UiAccentPressed
            switchControl.TrackColorOff = Color.FromArgb(63, 73, 86)
            switchControl.HoverTrackColorOff = Color.FromArgb(76, 88, 103)
            switchControl.PressedTrackColorOff = Color.FromArgb(52, 62, 74)
            switchControl.KnobColor = Color.FromArgb(245, 248, 251)
            switchControl.HoverKnobColor = Color.White
            switchControl.PressedKnobColor = Color.FromArgb(225, 232, 240)
            switchControl.BorderColor = Color.Transparent
            switchControl.BorderSize = 0
            Dim applySize As Action =
                Sub()
                    Dim ancestor = switchControl.Parent
                    While ancestor IsNot Nothing AndAlso Not TypeOf ancestor Is DpiLayoutPanel
                        ancestor = ancestor.Parent
                    End While
                    Dim layout = TryCast(ancestor, DpiLayoutPanel)
                    If layout IsNot Nothing Then
                        switchControl.Size = New Size(layout.ScaleX(38), layout.ScaleY(20))
                    Else
                        switchControl.Size = New Size(38, 20)
                    End If
                End Sub
            AddHandler switchControl.HandleCreated, Sub(sender, e) applySize()
            AddHandler switchControl.DpiChangedAfterParent, Sub(sender, e) applySize()
            AddHandler switchControl.ParentChanged, Sub(sender, e) applySize()
            applySize()
        End Sub

        Private Shared Function EscapeHtml(text As String) As String
            If String.IsNullOrEmpty(text) Then
                Return text
            End If
            Return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        End Function

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then
                RemoveHandler _config.Saved, AddressOf OnConfigurationSaved
                RemoveHandler EnhancementTaskRegistry.Changed, AddressOf OnManagedTaskChanged
                If _parameterContext IsNot Nothing Then RemoveHandler _parameterContext.StateRestored, AddressOf OnPresetStateRestored
                CloseModelMenuToolTip()
                CloseUserModelContextMenu()
                CloseDownloadModelContextMenu()
                For Each cancellation In _downloadCancellations.Values.ToArray()
                    cancellation.Cancel()
                Next
                ' LakeUI 5.x 在 TabControl 隐藏时会重新显示当前绑定页。
                ' 先解除绑定，避免父窗体销毁期间访问已经 Dispose 的 ModernPanel。
                Try
                    For Each tab In _tabs.Items
                        tab.BoundControl = Nothing
                    Next
                Catch
                End Try
                If Current Is Me Then
                    Current = Nothing
                End If
                Try
                    _statusClearTimer.Stop()
                    _statusClearTimer.Dispose()
                Catch
                End Try
                If _engine IsNot Nothing Then
                    Try
                        _engine.Dispose()
                    Catch
                    End Try
                    _engine = Nothing
                End If
                If _lastPreviewImage IsNot Nothing Then
                    Try
                        _picPreview.Source = Nothing
                        PreviewPictureSource.Release(_lastPreviewImage)
                    Catch
                    End Try
                    _lastPreviewImage = Nothing
                End If
            End If
            MyBase.Dispose(disposing)
        End Sub

        Private Sub ShowStatus(text As String, error_ As Boolean)
            If error_ Then System.Diagnostics.Trace.WriteLine("[VideoEnhancer] " & text)
            If Not _uiReady Then
                Return
            End If
            Try
                If IsHandleCreated Then
                    BeginInvoke(New Action(Sub() SetStatus(text, error_)))
                Else
                    SetStatus(text, error_)
                End If
            Catch
            End Try
        End Sub

        Private Sub SetStatus(text As String, error_ As Boolean)
            If _parameterMode Then
                If error_ Then MessageBox.Show(Me, text, "AI 参数", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            _homeMessage.Text = text
            If error_ Then
                _lblStatus.Text = "<font color=#E07878>" & EscapeHtml(text) & "</font>"
            Else
                _lblStatus.Text = "<font color=#96D2A0>" & EscapeHtml(text) & "</font>"
            End If
            ' 错误提示（如"超分和补帧不能同时开启"）5 秒后自动消失
            If error_ Then
                Try
                    _statusClearTimer.Stop()
                    _statusClearTimer.Start()
                Catch
                End Try
            End If
        End Sub

    End Class

End Namespace
