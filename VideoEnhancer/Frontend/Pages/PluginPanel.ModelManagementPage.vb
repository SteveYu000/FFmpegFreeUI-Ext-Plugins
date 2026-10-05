Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports LakeUI
Imports VideoEnhancer

Namespace videoenhancer
    Public Partial Class PluginPanel
        Private ReadOnly _pageModelManagement As New SmoothScrollPanel()
        Private ReadOnly _installedModels As New UltraDetailListView()
        Private ReadOnly _installedModelMessage As New LakeTextLabel()
        Private ReadOnly _btnRefreshInstalledModels As New ModernButton With {.Text = "刷新模型"}
        Private ReadOnly _btnRemoveInstalledModel As New ModernButton With {.Text = "移除所选模型"}
        Private _loadingInstalledModels As Boolean

        Private Sub BuildModelManagementPage()
            _pageModelManagement.Dock = DockStyle.Fill
            _pageModelManagement.LayoutMode = ModernPanel.LayoutModeEnum.Absolute
            _pageModelManagement.AutoScroll = False
            _pageModelManagement.ScrollBarMode = ModernPanel.ScrollMode.Vertical
            _pageModelManagement.ScrollBarTrackColor = UiSurface
            _pageModelManagement.ScrollBarThumbColor = UiScrollThumb
            _pageModelManagement.ScrollBarThumbHoverColor = UiScrollThumbHover
            Dim root As New ModernGridPanel With {.ColumnCount = 1, .RowCount = 4, .Padding = New Padding(12)}
            root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 56))
            root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 48))
            root.AddAt(CreateOfficialCaption("已安装模型", UiText), 0, 0)
            Dim actions As New ModernGridPanel With {.ColumnCount = 2, .RowCount = 1, .Dock = DockStyle.Fill, .Margin = New Padding(0, 0, 0, 8)}
            actions.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
            actions.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
            actions.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            Dim buttons = New ModernButton() {_btnRefreshInstalledModels, _btnRemoveInstalledModel}
            For index = 0 To buttons.Length - 1
                ConfigureSecondaryButton(buttons(index))
                buttons(index).Dock = DockStyle.Fill
                buttons(index).Margin = New Padding(0, 0, If(index = 1, 0, 8), 0)
                actions.AddAt(buttons(index), index, 0)
            Next
            AddHandler _btnRefreshInstalledModels.Click, Sub() RefreshInstalledModels()
            AddHandler _btnRemoveInstalledModel.Click, AddressOf RemoveInstalledModel
            root.AddAt(actions, 0, 1)
            ConfigureHomeList(_installedModels)
            _installedModels.GroupHeight = 38
            _installedModels.ItemPadding = New Padding(10, 5, 8, 5)
            _installedModels.Columns.AddRange(New UltraDetailListView.ListColumn() {
                New UltraDetailListView.ListColumn("模型", 320),
                New UltraDetailListView.ListColumn("支持后端", 180),
                New UltraDetailListView.ListColumn("位置", 400)})
            ConfigureDpiListColumns(_installedModels, 180)
            root.AddAt(_installedModels, 0, 2)
            _installedModelMessage.Dock = DockStyle.Fill
            _installedModelMessage.Margin = Padding.Empty
            _installedModelMessage.ForeColor = UiTextSecondary
            root.AddAt(_installedModelMessage, 0, 3)
            _pageModelManagement.Controls.Add(root)
            Dim arrange As Action = Sub()
                root.SetBounds(0, If(_pageModelManagement.VerticalScrollOffset > 0, root.Top, 0),
                    Math.Max(0, _pageModelManagement.ClientSize.Width - root.ScaleX(12)),
                    Math.Max(root.ScaleY(340), _pageModelManagement.ClientSize.Height))
            End Sub
            AddHandler _pageModelManagement.ClientSizeChanged, Sub() arrange()
            AddHandler _pageModelManagement.Layout, Sub() arrange()
            AddHandler _pageModelManagement.ParentChanged, Sub()
                If _pageModelManagement.Visible AndAlso _pageModelManagement.Parent IsNot Nothing Then RefreshInstalledModels()
            End Sub
            AddHandler _pageModelManagement.VisibleChanged, Sub()
                If _pageModelManagement.Visible AndAlso _pageModelManagement.Parent IsNot Nothing Then RefreshInstalledModels()
            End Sub
            arrange()
        End Sub

        Private Async Sub RefreshInstalledModels()
            If _loadingInstalledModels OrElse IsDisposed OrElse Disposing Then Return
            _loadingInstalledModels = True
            _btnRefreshInstalledModels.Enabled = False
            _btnRemoveInstalledModel.Enabled = False
            SetInstalledModelMessage("正在读取已安装模型…", False)
            Try
                Dim catalog = Await Task.Run(Function() ModelCatalogClient.RunInstalledModelCatalog())
                If IsDisposed OrElse Disposing Then Return
                ApplyInstalledModelCatalog(catalog)
            Catch ex As Exception
                If Not IsDisposed AndAlso Not Disposing Then SetInstalledModelMessage("读取模型失败：" & ex.Message, True)
            Finally
                _loadingInstalledModels = False
                If Not IsDisposed AndAlso Not Disposing Then
                    _btnRefreshInstalledModels.Enabled = True
                    _btnRemoveInstalledModel.Enabled = True
                End If
            End Try
        End Sub

        Private Shared Function InstalledModelGroup(entry As ModelCatalogItem) As String
            Dim taskName = If(String.Equals(entry.Task, "interpolation", StringComparison.OrdinalIgnoreCase), "视频补帧", "视频超分")
            Return taskName & " · " & ModelDescriptionProvider.ModelArchitectureGroup(entry)
        End Function

        Private Sub ApplyInstalledModelCatalog(catalog As List(Of ModelCatalogItem))
            Dim selectedPath = TryCast(_installedModels.SelectedItem?.Tag, ModelCatalogItem)?.RelativePath
            Dim collapsedGroups = _installedModels.Groups.ToDictionary(Function(group) group.Name, Function(group) group.IsCollapsed, StringComparer.OrdinalIgnoreCase)
            Dim selectedIndex = -1
            Dim displayIndex = 0
            _installedModels.ClearSelection()
            _installedModels.BeginUpdate()
            Try
                _installedModels.Items.Clear()
                _installedModels.Groups.Clear()
                For Each group In catalog.GroupBy(Function(entry) InstalledModelGroup(entry), StringComparer.OrdinalIgnoreCase).
                    OrderBy(Function(entries) entries.Key, StringComparer.CurrentCultureIgnoreCase)
                    Dim collapsed = collapsedGroups.ContainsKey(group.Key) AndAlso collapsedGroups(group.Key)
                    _installedModels.Groups.Add(New UltraDetailListView.ListGroup(group.Key, group.Key & "  ·  " & group.Count() & " 个模型") With {.ForeColor = UiText, .IsCollapsed = collapsed})
                    ' LakeUI 的 SelectedIndex 计入分组标题，仅对展开组累计可见模型行。
                    displayIndex += 1
                    For Each entry In group.OrderBy(Function(item) item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                        Dim interpolation = String.Equals(entry.Task, "interpolation", StringComparison.OrdinalIgnoreCase)
                        Dim row As New UltraDetailListView.ListItem(New UltraDetailListView.ListSubItem() {
                            New UltraDetailListView.ListSubItem(ModelDescriptionProvider.ModelDisplayText(entry, interpolation)),
                            New UltraDetailListView.ListSubItem(String.Join(" / ", entry.Backends.Select(Function(value) ModelDescriptionProvider.BackendDisplayName(value)))),
                            New UltraDetailListView.ListSubItem(entry.RelativePath)}) With {.GroupName = group.Key, .Tag = entry}
                        _installedModels.Items.Add(row)
                        If Not collapsed Then
                            If String.Equals(entry.RelativePath, selectedPath, StringComparison.OrdinalIgnoreCase) Then selectedIndex = displayIndex
                            displayIndex += 1
                        End If
                    Next
                Next
            Finally
                _installedModels.EndUpdate()
            End Try
            If selectedIndex >= 0 Then _installedModels.SelectedIndex = selectedIndex
            SetInstalledModelMessage(If(catalog.Count = 0, "尚未安装模型，可前往模型下载或模型导入。", "已安装 " & catalog.Count & " 个模型"), False)
        End Sub

        Private Sub SetInstalledModelMessage(message As String, isError As Boolean)
            _installedModelMessage.Text = message
            _installedModelMessage.ForeColor = If(isError, UiDanger, UiTextSecondary)
        End Sub

        Private Sub RemoveInstalledModel(sender As Object, e As EventArgs)
            Dim entry = TryCast(_installedModels.SelectedItem?.Tag, ModelCatalogItem)
            If entry Is Nothing Then Return
            If EnhancementTaskRegistry.HasActiveTasks Then
                SetInstalledModelMessage("增强任务运行时不能移除模型。", True)
                Return
            End If
            Try
                Dim root = Path.GetFullPath(Path.Combine(PluginConfig.ApplicationRoot, "models"))
                Dim modelPath = Path.GetFullPath(Path.Combine(root, entry.RelativePath))
                If Not modelPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then
                    Throw New InvalidOperationException("模型路径超出插件目录")
                End If
                If MessageBox.Show(Me, "移除模型：" & entry.DisplayName & "？" & Environment.NewLine & entry.RelativePath,
                    "模型管理", MessageBoxButtons.YesNo, MessageBoxIcon.Question) <> DialogResult.Yes Then Return
                BackendServices.RemoveInstalledModel(modelPath)
                _modelsLoaded = False
                _interpModelsLoaded = False
                RefreshInstalledModels()
            Catch ex As Exception
                SetInstalledModelMessage("移除模型失败：" & ex.Message, True)
            End Try
        End Sub
    End Class
End Namespace
