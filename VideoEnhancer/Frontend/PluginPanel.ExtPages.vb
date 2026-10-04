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
        Private ReadOnly _pageHome As New ModernPanel()
        Private ReadOnly _homeTasks As New ListView()
        Private ReadOnly _homeModels As New ListView()
        Private ReadOnly _homeSettings As New LakeTextLabel()
        Private ReadOnly _homeMessage As New LakeTextLabel()
        Private ReadOnly _btnZipUpdate As New ModernButton()
        Private _availableUpdate As UpdateManifest
        Private ReadOnly _switchSegmentedMode As New BooleanSwitch()

        Friend Sub BindPresetContext(context As IExtPluginPageContext)
            _parameterContext = context
            AddHandler context.StateRestored, AddressOf OnPresetStateRestored
            EnhancementTaskRegistry.CurrentSettingsJson = EnhancementSettings.NormalizeJson(context.StateJson)
        End Sub

        Private Sub OnPresetStateRestored(sender As Object, e As EventArgs)
            RemoveHandler _config.Saved, AddressOf OnConfigurationSaved
            _config = PluginConfig.FromStateJson(_parameterContext.StateJson)
            AddHandler _config.Saved, AddressOf OnConfigurationSaved
            EnhancementTaskRegistry.CurrentSettingsJson = EnhancementSettings.NormalizeJson(_parameterContext.StateJson)
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
            Dim root As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 7, .BackColor = Color.Transparent, .Padding = New Padding(12)}
            root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 70))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 34))
            root.RowStyles.Add(New RowStyle(SizeType.Percent, 50))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 40))
            root.RowStyles.Add(New RowStyle(SizeType.Percent, 50))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            root.Controls.Add(New LakeTextLabel With {.Text = "视频超分 · v" & PluginUpdater.CurrentVersion, .Dock = DockStyle.Fill, .ForeColor = UiText}, 0, 0)
            _homeSettings.Dock = DockStyle.Fill
            _homeSettings.ForeColor = UiTextSecondary
            root.Controls.Add(_homeSettings, 0, 1)
            root.Controls.Add(New LakeTextLabel With {.Text = "本插件任务队列", .Dock = DockStyle.Fill, .ForeColor = UiText}, 0, 2)
            ConfigureHomeList(_homeTasks)
            _homeTasks.Columns.Add("输入文件", 220)
            _homeTasks.Columns.Add("状态", 90)
            _homeTasks.Columns.Add("处理阶段", 180)
            _homeTasks.Columns.Add("输出文件", 220)
            AddHandler _homeTasks.DoubleClick, AddressOf ShowHomeTaskDetails
            root.Controls.Add(_homeTasks, 0, 3)
            Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .BackColor = Color.Transparent}
            Dim refresh As New ModernButton With {.Text = "刷新模型", .Width = 100, .Height = 28}
            Dim remove As New ModernButton With {.Text = "移除所选模型", .Width = 140, .Height = 28}
            ConfigureSecondaryButton(refresh)
            ConfigureSecondaryButton(remove)
            AddHandler refresh.Click, Sub() RefreshHomeModels()
            AddHandler remove.Click, AddressOf RemoveHomeModel
            _btnCheckUpdates.Text = "检查插件更新"
            _btnCheckUpdates.Size = New Size(130, 28)
            ConfigureSecondaryButton(_btnCheckUpdates)
            AddHandler _btnCheckUpdates.Click, AddressOf OnCheckUpdates
            _btnZipUpdate.Text = "下载 ZIP 更新"
            _btnZipUpdate.Size = New Size(140, 28)
            ConfigureSecondaryButton(_btnZipUpdate)
            AddHandler _btnZipUpdate.Click, AddressOf OnDownloadPluginUpdate
            actions.Controls.AddRange(New Control() {New LakeTextLabel With {.Text = "已安装模型", .Width = 130, .Height = 32, .ForeColor = UiText}, refresh, remove, _btnCheckUpdates, _btnZipUpdate})
            root.Controls.Add(actions, 0, 4)
            ConfigureHomeList(_homeModels)
            _homeModels.Columns.Add("模型", 300)
            _homeModels.Columns.Add("位置", 480)
            root.Controls.Add(_homeModels, 0, 5)
            _homeMessage.Dock = DockStyle.Fill
            _homeMessage.ForeColor = UiTextSecondary
            root.Controls.Add(_homeMessage, 0, 6)
            _pageHome.Controls.Add(root)
            AddHandler EnhancementTaskRegistry.Changed, AddressOf OnManagedTaskChanged
            AddHandler _pageHome.VisibleChanged, Sub()
                                                         If _pageHome.Visible Then
                                                             RefreshHomeInformation()
                                                             RefreshHomeModels()
                                                         End If
                                                     End Sub
            RefreshHomeInformation()
        End Sub

        Private Shared Sub ConfigureHomeList(list As ListView)
            list.Dock = DockStyle.Fill
            list.View = View.Details
            list.FullRowSelect = True
            list.HideSelection = False
            list.BackColor = UiCanvas
            list.ForeColor = UiText
            list.BorderStyle = BorderStyle.None
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
            _homeSettings.Text = "当前参数：" & EnhancementSettings.FromJson(EnhancementTaskRegistry.CurrentSettingsJson).Summary()
            _homeTasks.BeginUpdate()
            Try
                _homeTasks.Items.Clear()
                For Each task In EnhancementTaskRegistry.Snapshot()
                    Dim row As New ListViewItem(Path.GetFileName(task.InputPath)) With {.Tag = task}
                    row.SubItems.Add(task.Status)
                    row.SubItems.Add(task.Phase)
                    row.SubItems.Add(task.OutputPath)
                    _homeTasks.Items.Add(row)
                Next
            Finally
                _homeTasks.EndUpdate()
            End Try
        End Sub

        Private Sub ShowHomeTaskDetails(sender As Object, e As EventArgs)
            If _homeTasks.SelectedItems.Count = 0 Then Return
            Dim task = DirectCast(_homeTasks.SelectedItems(0).Tag, EnhancementTaskView)
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
                Dim row As New ListViewItem(Path.GetFileNameWithoutExtension(modelPath)) With {.Tag = modelPath}
                row.SubItems.Add(Path.GetRelativePath(root, modelPath))
                _homeModels.Items.Add(row)
            Next
        End Sub

        Private Sub RemoveHomeModel(sender As Object, e As EventArgs)
            If _homeModels.SelectedItems.Count = 0 Then Return
            If EnhancementTaskRegistry.HasActiveTasks Then
                ShowStatus("增强任务运行时不能移除模型。", True)
                Return
            End If
            Dim modelPath = CStr(_homeModels.SelectedItems(0).Tag)
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
            ActivateSegmentedPage()
            Using editor As New Form With {.Text = "分段超分", .Size = New Size(1000, 760), .MinimumSize = New Size(760, 520), .StartPosition = FormStartPosition.CenterParent, .BackColor = UiCanvas}
                _pageSegmented.Dock = DockStyle.Fill
                editor.Controls.Add(_pageSegmented)
                Try
                    editor.ShowDialog(Me)
                Finally
                    editor.Controls.Remove(_pageSegmented)
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
