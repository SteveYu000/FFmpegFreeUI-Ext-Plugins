Imports System
Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports System.Windows.Forms
Imports FFmpegFreeUI.Ext.PluginSdk
Imports LakeUI
Imports VideoEnhancer

Namespace videoenhancer
    Public Partial Class PluginPanel
        Private ReadOnly _parameterMode As Boolean
        Private _parameterContext As IExtPluginPageContext
        Private ReadOnly _pageHome As New SmoothScrollPanel()
        Private ReadOnly _homeTasks As New UltraDetailListView()
        Private ReadOnly _homeModels As New UltraDetailListView()
        Private ReadOnly _homeSettings As New LakeTextLabel()
        Private ReadOnly _homeMessage As New LakeTextLabel()
        Private ReadOnly _btnZipUpdate As New ModernButton()
        Private _availableUpdate As UpdateManifest
        Private ReadOnly _switchSegmentedMode As New BooleanSwitch()

        Friend Sub BindPresetContext(context As IExtPluginPageContext)
            _parameterContext = context
            AddHandler context.StateRestored, AddressOf OnPresetStateRestored
            EnhancementTaskRegistry.CurrentSettingsJson = context.StateJson
        End Sub

        Private Sub OnPresetStateRestored(sender As Object, e As EventArgs)
            RemoveHandler _config.Saved, AddressOf OnConfigurationSaved
            _config = PluginConfig.FromStateJson(_parameterContext.StateJson)
            AddHandler _config.Saved, AddressOf OnConfigurationSaved
            EnhancementTaskRegistry.CurrentSettingsJson = _parameterContext.StateJson
            _modelsLoaded = False
            _interpModelsLoaded = False
            RefreshUi()
            RefreshModels()
        End Sub

        Private Sub SavePresetState()
            If _parameterContext Is Nothing Then Return
            _parameterContext.StateJson = EnhancementSettings.NormalizeJson(JsonSerializer.Serialize(_config))
            EnhancementTaskRegistry.CurrentSettingsJson = _parameterContext.StateJson
            _parameterContext.RequestParameterRefresh()
        End Sub

        Private Sub BuildHomePage()
            _pageHome.Dock = DockStyle.Fill
            _pageHome.LayoutMode = ModernPanel.LayoutModeEnum.Absolute
            _pageHome.AutoScroll = False
            _pageHome.ScrollBarMode = ModernPanel.ScrollMode.Vertical
            _pageHome.ScrollBarTrackColor = UiSurface
            _pageHome.ScrollBarThumbColor = UiScrollThumb
            _pageHome.ScrollBarThumbHoverColor = UiScrollThumbHover
            Dim root As New ModernGridPanel With {.ColumnCount = 1, .RowCount = 7, .Padding = New Padding(12)}
            root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            For Each rowHeight As Integer In New Integer() {36, 64, 28}
                root.RowStyles.Add(New RowStyle(SizeType.Absolute, rowHeight))
            Next
            root.RowStyles.Add(New RowStyle(SizeType.Percent, 50))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 76))
            root.RowStyles.Add(New RowStyle(SizeType.Percent, 50))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 56))
            root.AddAt(CreateOfficialCaption("视频超分 · v" & PluginUpdater.CurrentVersion, UiText), 0, 0)
            _homeSettings.Dock = DockStyle.Fill
            _homeSettings.Margin = Padding.Empty
            _homeSettings.ForeColor = UiTextSecondary
            root.AddAt(_homeSettings, 0, 1)
            root.AddAt(CreateOfficialCaption("本插件任务队列（双击查看参数和后端命令）"), 0, 2)
            ConfigureHomeList(_homeTasks)
            _homeTasks.Columns.AddRange(New UltraDetailListView.ListColumn() {
                New UltraDetailListView.ListColumn("输入文件", 220), New UltraDetailListView.ListColumn("状态", 90),
                New UltraDetailListView.ListColumn("处理阶段", 160), New UltraDetailListView.ListColumn("输出文件", 220)})
            ConfigureDpiListColumns(_homeTasks, 180)
            AddHandler _homeTasks.ItemDoubleClick, AddressOf ShowHomeTaskDetails
            root.AddAt(_homeTasks, 0, 3)
            Dim actions As New ModernGridPanel With {.ColumnCount = 4, .RowCount = 2, .Dock = DockStyle.Fill, .Margin = New Padding(0, 8, 0, 8)}
            For index = 0 To 3
                actions.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 25))
            Next
            actions.RowStyles.Add(New RowStyle(SizeType.Absolute, 24))
            actions.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            actions.AddAt(CreateOfficialCaption("已安装模型"), 0, 0)
            actions.SetColumnSpan(actions.Controls(0), 4)
            Dim refresh As New ModernButton With {.Text = "刷新模型"}
            Dim remove As New ModernButton With {.Text = "移除所选模型"}
            _btnCheckUpdates.Text = "检查插件更新"
            _btnZipUpdate.Text = "下载 ZIP 更新"
            Dim buttons = New ModernButton() {refresh, remove, _btnCheckUpdates, _btnZipUpdate}
            For index = 0 To buttons.Length - 1
                ConfigureSecondaryButton(buttons(index))
                buttons(index).Dock = DockStyle.Fill
                buttons(index).Margin = New Padding(0, 0, If(index = 3, 0, 8), 0)
                actions.AddAt(buttons(index), index, 1)
            Next
            AddHandler refresh.Click, Sub() RefreshHomeModels()
            AddHandler remove.Click, AddressOf RemoveHomeModel
            AddHandler _btnCheckUpdates.Click, AddressOf OnCheckUpdates
            AddHandler _btnZipUpdate.Click, AddressOf OnDownloadPluginUpdate
            root.AddAt(actions, 0, 4)
            ConfigureHomeList(_homeModels)
            _homeModels.Columns.AddRange(New UltraDetailListView.ListColumn() {
                New UltraDetailListView.ListColumn("模型", 300), New UltraDetailListView.ListColumn("位置", 400)})
            ConfigureDpiListColumns(_homeModels, 180)
            root.AddAt(_homeModels, 0, 5)
            _homeMessage.Dock = DockStyle.Fill
            _homeMessage.Margin = Padding.Empty
            _homeMessage.ForeColor = UiTextSecondary
            root.AddAt(_homeMessage, 0, 6)
            _pageHome.Controls.Add(root)
            Dim arrange As Action = Sub()
                Dim height = Math.Max(root.ScaleY(600), _pageHome.ClientSize.Height)
                root.SetBounds(0, If(_pageHome.VerticalScrollOffset > 0, root.Top, 0),
                    Math.Max(0, _pageHome.ClientSize.Width - root.ScaleX(12)), height)
            End Sub
            AddHandler _pageHome.ClientSizeChanged, Sub() arrange()
            AddHandler _pageHome.Layout, Sub() arrange()
            arrange()
            AddHandler EnhancementTaskRegistry.Changed, AddressOf OnManagedTaskChanged
            AddHandler _pageHome.VisibleChanged, Sub()
                If _pageHome.Visible Then
                    RefreshHomeInformation()
                    RefreshHomeModels()
                End If
            End Sub
            RefreshHomeInformation()
        End Sub

        Private Sub ConfigureHomeList(list As UltraDetailListView)
            list.Dock = DockStyle.Fill
            list.Margin = Padding.Empty
            list.MultiSelect = False
            list.HeaderHeight = 30
            ConfigureTransparentListAppearance(list)
        End Sub

        Private Sub OnManagedTaskChanged(sender As Object, e As EventArgs)
            If IsDisposed OrElse Not IsHandleCreated Then Return
            Try
                BeginInvoke(New Action(AddressOf RefreshHomeInformation))
            Catch ex As InvalidOperationException
            End Try
        End Sub

        Private Sub RefreshHomeInformation()
            If _parameterMode Then Return
            Try
                _homeSettings.Text = "当前参数：" & EnhancementSettings.FromJson(EnhancementTaskRegistry.CurrentSettingsJson).Summary()
            Catch ex As Exception
                _homeSettings.Text = "当前参数无法读取：" & ex.Message
            End Try
            If Not String.IsNullOrWhiteSpace(EnhancementTaskRegistry.CurrentParameterError) Then
                _homeSettings.Text &= Environment.NewLine & "参数检查：" & EnhancementTaskRegistry.CurrentParameterError
            End If
            _homeTasks.BeginUpdate()
            Try
                _homeTasks.Items.Clear()
                For Each task In EnhancementTaskRegistry.Snapshot()
                    Dim row As New UltraDetailListView.ListItem(New UltraDetailListView.ListSubItem() {
                        New UltraDetailListView.ListSubItem(Path.GetFileName(task.InputPath)),
                        New UltraDetailListView.ListSubItem(task.Status), New UltraDetailListView.ListSubItem(task.Phase),
                        New UltraDetailListView.ListSubItem(task.OutputPath)}) With {.Tag = task}
                    _homeTasks.Items.Add(row)
                Next
            Finally
                _homeTasks.EndUpdate()
            End Try
        End Sub

        Private Sub ShowHomeTaskDetails(sender As Object, e As UltraDetailListView.ListItemEventArgs)
            Dim task = TryCast(e.Item?.Tag, EnhancementTaskView)
            If task Is Nothing Then Return
            Using dialog As New Form With {.Text = "AI 任务参数 · " & Path.GetFileName(task.InputPath), .Size = New Size(900, 680), .StartPosition = FormStartPosition.CenterParent}
                Dim text As New TextBox With {.Multiline = True, .ReadOnly = True, .ScrollBars = ScrollBars.Both, .Dock = DockStyle.Fill, .Font = New Font("Consolas", 10), .BackColor = UiCanvas, .ForeColor = UiText, .Text = task.Settings & Environment.NewLine & Environment.NewLine & task.BackendRequest}
                dialog.Controls.Add(text)
                dialog.ShowDialog(Me)
            End Using
        End Sub

        Private Sub RefreshHomeModels()
            Dim root = Path.Combine(PluginConfig.ApplicationRoot, "models")
            _homeModels.Items.Clear()
            If Not Directory.Exists(root) Then Return
            For Each modelPath In Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).
                Where(Function(p) New String() {".param", ".pth", ".pkl", ".ckpt", ".pt", ".onnx", ".engine", ".safetensors"}.Contains(Path.GetExtension(p).ToLowerInvariant()))
                Dim row As New UltraDetailListView.ListItem(New UltraDetailListView.ListSubItem() {
                    New UltraDetailListView.ListSubItem(Path.GetFileNameWithoutExtension(modelPath)),
                    New UltraDetailListView.ListSubItem(Path.GetRelativePath(root, modelPath))}) With {.Tag = modelPath}
                _homeModels.Items.Add(row)
            Next
        End Sub

        Private Sub RemoveHomeModel(sender As Object, e As EventArgs)
            If _homeModels.SelectedItem Is Nothing Then Return
            If EnhancementTaskRegistry.HasActiveTasks Then
                ShowStatus("增强任务运行时不能移除模型。", True)
                Return
            End If
            Dim modelPath = CStr(_homeModels.SelectedItem.Tag)
            Dim modelRoot = Path.GetFullPath(Path.Combine(PluginConfig.ApplicationRoot, "models")) & Path.DirectorySeparatorChar
            If Not Path.GetFullPath(modelPath).StartsWith(modelRoot, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidOperationException("模型路径超出插件目录")
            If MessageBox.Show(Me, "移除模型：" & Path.GetFileName(modelPath) & "？", "模型管理", MessageBoxButtons.YesNo, MessageBoxIcon.Question) <> DialogResult.Yes Then Return
            Try
                BackendServices.RemoveInstalledModel(modelPath)
                _modelsLoaded = False
                _interpModelsLoaded = False
                RefreshHomeModels()
                ShowStatus("已移除模型：" & Path.GetFileName(modelPath), False)
            Catch ex As Exception
                ShowStatus("移除模型失败：" & ex.Message, True)
            End Try
        End Sub

        Private Sub OpenSegmentedEditor()
            Using editor As New Form With {.Text = "分段超分", .AutoScaleMode = AutoScaleMode.None,
                .StartPosition = FormStartPosition.CenterParent, .BackColor = UiCanvas, .Font = Font}
                Dim scale = DeviceDpi / 96.0F
                editor.ClientSize = New Size(CInt(1000 * scale), CInt(760 * scale))
                editor.MinimumSize = New Size(CInt(760 * scale), CInt(520 * scale))
                Dim surface As New ModernPanel With {.Dock = DockStyle.Fill, .BackColor1 = UiCanvas,
                    .LayoutMode = ModernPanel.LayoutModeEnum.Absolute, .BorderSize = 0,
                    .Padding = New Padding(CInt(16 * scale))}
                editor.Controls.Add(surface)
                _pageSegmented.Dock = DockStyle.Fill
                surface.Controls.Add(_pageSegmented)
                ' 页面已经随参数面板缩放，独立窗口只补上 DPI 差值，不再次缩放整棵控件树。
                Dim applyDpi As Action(Of Integer) = Sub(dpi)
                    Dim target = dpi / 96.0F
                    _pageSegmented.Scale(New SizeF(target / _segmentRoot.LayoutScale.Width, target / _segmentRoot.LayoutScale.Height))
                    SyncSegmentedRootBounds()
                End Sub
                AddHandler editor.Load, Sub() applyDpi(editor.DeviceDpi)
                AddHandler editor.DpiChanged, Sub(sender, e) applyDpi(e.DeviceDpiNew)
                BindScrollableGpuBackgroundSources(_pageSegmented, surface, True)
                ActivateSegmentedPage()
                Try
                    editor.ShowDialog(Me)
                Finally
                    surface.Controls.Remove(_pageSegmented)
                    BindScrollableGpuBackgroundSources(_pageSegmented, ModernPanel1, True)
                End Try
            End Using
        End Sub

        Private Sub AddSegmentedEntry(root As DpiLayoutPanel)
            Dim row As New ModernHorizontalPanel(108.0F, 56.0F, -1.0F)
            ConfigureDpiSwitch(_switchSegmentedMode)
            _switchSegmentedMode.Checked = _config.SegmentedEnabled
            AddHandler _switchSegmentedMode.CheckedChanged, Sub()
                                                           If _config.SegmentedEnabled = _switchSegmentedMode.Checked Then Return
                                                           _config.SegmentedEnabled = _switchSegmentedMode.Checked
                                                           _config.Save()
                                                           UpdateAdvancedControlState()
                                                       End Sub
            Dim open As New ModernButton With {.Text = "编辑分段方案"}
            ConfigureSecondaryButton(open)
            AddHandler open.Click, Sub() OpenSegmentedEditor()
            row.AddColumn(CreateOfficialCaption("分段超分"), 0)
            row.AddColumn(_switchSegmentedMode, 1)
            row.AddColumn(open, 2)
            AddWorkbenchRow(root, row, 204, UiRowHeight)
        End Sub
    End Class
End Namespace
