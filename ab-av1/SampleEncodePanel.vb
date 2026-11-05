Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports LakeUI

''' <summary>使用当前 3FUI 参数评估指定 CRF；只管理样本任务，不修改原生预设或正式编码队列。</summary>
Friend NotInheritable Class SampleEncodePanel
    Inherits ModernPanel

    Private Shared ReadOnly ColorControl As Color = Color.FromArgb(40, 220, 220, 220)
    Private Shared ReadOnly ColorControlHover As Color = Color.FromArgb(60, 220, 220, 220)
    Private Shared ReadOnly ColorControlPressed As Color = Color.FromArgb(80, 220, 220, 220)
    Private Shared ReadOnly ColorBackground As Color = Color.FromArgb(24, 24, 24)
    Private Shared ReadOnly ColorText As Color = Color.FromArgb(220, 220, 220)
    Private Shared ReadOnly ColorMuted As Color = Color.FromArgb(120, 255, 255, 255)
    Private Shared ReadOnly ColorAccent As Color = Color.FromArgb(71, 156, 255)
    Private Shared ReadOnly ColorSuccess As Color = Color.FromArgb(63, 205, 135)
    Private Shared ReadOnly ColorWarning As Color = Color.FromArgb(232, 177, 74)
    Private Shared ReadOnly ColorDanger As Color = Color.FromArgb(235, 93, 93)

    Private ReadOnly _presetProvider As Func(Of String)
    Private ReadOnly _backAction As Action
    Private ReadOnly _backButton As ModernButton
    Private ReadOnly _refreshParametersButton As ModernButton
    Private ReadOnly _heading As HtmlColorLabel
    Private ReadOnly _previewCaption As HtmlColorLabel
    Private ReadOnly _preview As ModernTextBox
    Private ReadOnly _fieldLabels As HtmlColorLabel()
    Private ReadOnly _modelCaption As HtmlColorLabel
    Private ReadOnly _scoreMetric As ModernComboBox
    Private ReadOnly _crf As ModernTextBox
    Private ReadOnly _samples As ModernTextBox
    Private ReadOnly _sampleDuration As ModernTextBox
    Private ReadOnly _vmafModel As ModernComboBox
    Private ReadOnly _fileList As UltraDetailListView
    Private ReadOnly _vmafModelStatus As HtmlColorLabel
    Private ReadOnly _status As HtmlColorLabel
    Private ReadOnly _progressRing As ProgressRing
    Private ReadOnly _startButton As ModernButton
    Private ReadOnly _addFilesButton As ModernButton
    Private ReadOnly _stopButton As ModernButton
    Private ReadOnly _pauseResumeButton As ModernButton
    Private ReadOnly _removeButton As ModernButton
    Private ReadOnly _resetButton As ModernButton
    Private ReadOnly _refreshModelsButton As ModernButton
    Private ReadOnly _browseModelButton As ModernButton
    Private ReadOnly _taskContextMenu As ModernContextMenu
    Private ReadOnly _progressRenderTimer As System.Windows.Forms.Timer
    Private ReadOnly _detailFont As Font
    Private ReadOnly _lifetimeCancellation As New CancellationTokenSource()
    Private ReadOnly _items As New List(Of SampleQueueFileItem)()

    Private _presetJson As String
    Private _currentProfile As PresetProfile
    Private _queueProfile As PresetProfile
    Private _queueSettings As SampleEncodeSettings
    Private _activeItem As SampleQueueFileItem
    Private _contextMenuTarget As SampleQueueFileItem
    Private _schedulerTask As Task
    Private _running As Boolean
    Private _scanningModels As Boolean
    Private _initialModelScanStarted As Boolean
    Private _resourcesDisposed As Boolean
    Private _loading As Boolean = True
    Private _layingOut As Boolean
    Private _pendingProgressItem As SampleQueueFileItem
    Private _pendingProgressStatus As String
    Private _pendingProgressScore As String

    Public Sub New(presetProvider As Func(Of String),
                   backAction As Action,
                   initialState As AbAv1PluginState,
                   initialCrf As String,
                   backgroundSource As Control)
        If presetProvider Is Nothing Then Throw New ArgumentNullException(NameOf(presetProvider))
        _presetProvider = presetProvider
        _backAction = backAction
        Name = "ModernPanel1"
        BackColor = Color.Transparent
        BackColor1 = Color.Transparent
        BorderSize = 0
        Me.BackgroundSource = backgroundSource
        If backgroundSource IsNot Nothing Then Font = backgroundSource.Font
        ForeColor = ColorText
        Dock = DockStyle.Fill
        AllowDrop = True
        SuspendLayout()

        _detailFont = New Font(Font.FontFamily, 8.5F)
        _heading = CreateLabel("样本编码", ColorText, 13.0F)
        _heading.Name = "AbAv1SampleHeading"
        _heading.TextAlign = HtmlColorLabel.TextAlignEnum.Center
        _backButton = CreateButton("← 返回", Sub(sender, e) _backAction?.Invoke())
        _refreshParametersButton = CreateButton("读取当前参数", Sub(sender, e) RefreshFromHost())
        _scoreMetric = CreateMetricComboBox()
        _crf = CreateTextBox("要评估的 CRF")
        _samples = CreateTextBox("留空自动采样")
        _sampleDuration = CreateTextBox("例如 20s")
        _vmafModel = CreateComboBox("Auto")
        _vmafModel.Items.Add("Auto")
        Dim state = If(initialState, New AbAv1PluginState())
        _scoreMetric.SelectedIndex = If(state.Metric = QualityScoreMetric.Xpsnr, 1, 0)
        Dim crfValue As Double
        _crf.Text = If(TryParseNumber(If(initialCrf, ""), crfValue) AndAlso Double.IsFinite(crfValue) AndAlso crfValue >= 0,
                       SearchSettings.FormatNumber(crfValue), "30")
        _samples.Text = If(state.Samples.HasValue, state.Samples.GetValueOrDefault().ToString(CultureInfo.InvariantCulture), "")
        _sampleDuration.Text = state.SampleDuration
        _vmafModel.Text = If(String.IsNullOrWhiteSpace(state.VmafModel), "Auto", state.VmafModel)

        _fieldLabels = {"评分指标", "CRF", "采样数量", "单段时长"}.Select(Function(text) CreateFieldLabel(text)).ToArray()
        For Each label In _fieldLabels
            label.TextAlign = HtmlColorLabel.TextAlignEnum.BottomLeft
        Next
        _modelCaption = CreateFieldLabel("VMAF 模型")
        _vmafModelStatus = CreateLabel("Auto = ab-av1 自动模型", ColorMuted, 8.5F)
        _vmafModelStatus.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleLeft
        _refreshModelsButton = CreateButton("扫描模型", AddressOf RefreshModels)
        _browseModelButton = CreateButton("本地 JSON", AddressOf BrowseVmafModel)
        _fileList = CreateFileList()
        _fileList.Name = "AbAv1SampleFiles"
        _fileList.AllowDrop = True
        _status = CreateLabel(String.Empty, ColorMuted, 9.0F)
        _status.Visible = False
        _status.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleLeft
        _progressRing = New ProgressRing With {
            .AutoScaleMode = AutoScaleMode.None, .RingColor = ColorAccent, .AutoStart = False, .Visible = False
        }
        _addFilesButton = CreateButton("添加媒体", AddressOf AddFiles)
        _stopButton = CreateButton("停止", AddressOf StopTasks, danger:=True)
        _pauseResumeButton = CreateButton("暂停 / 恢复", AddressOf PauseOrResumeTasks)
        _removeButton = CreateButton("移除", AddressOf RemoveTasks)
        _resetButton = CreateButton("重置状态", AddressOf ResetTasks)
        _startButton = CreateButton("开始", AddressOf StartQueue, accent:=True)
        _previewCaption = CreateFieldLabel("参数预览")
        _previewCaption.TextAlign = HtmlColorLabel.TextAlignEnum.BottomLeft
        _preview = CreatePreviewBox("AbAv1SampleParameterPreview")
        _taskContextMenu = CreateTaskContextMenu()
        _progressRenderTimer = New System.Windows.Forms.Timer With {.Interval = 80}
        AddHandler _progressRenderTimer.Tick, AddressOf ProgressRenderTimerTick

        Dim children As New List(Of Control) From {
            _backButton, _heading, _refreshParametersButton, _scoreMetric, _crf, _samples, _sampleDuration,
            _modelCaption, _vmafModel, _refreshModelsButton, _browseModelButton, _vmafModelStatus, _fileList,
            _status, _progressRing, _addFilesButton, _stopButton, _pauseResumeButton, _removeButton, _resetButton,
            _startButton, _previewCaption, _preview
        }
        children.AddRange(_fieldLabels)
        For Each child In children
            child.Dock = DockStyle.None
            If Not TypeOf child Is HtmlColorLabel Then child.Font = Font
            Controls.Add(child)
        Next
        AddHandler _scoreMetric.SelectedIndexChanged, AddressOf ScoreMetricChanged
        For Each editor As Control In New Control() {_crf, _samples, _sampleDuration, _vmafModel}
            AddHandler editor.TextChanged, AddressOf SampleSettingsChanged
        Next
        AddHandler _fileList.SelectedIndexChanged, AddressOf QueueSelectionChanged
        AddHandler _fileList.KeyDown, AddressOf QueueKeyDown
        AddHandler _fileList.MouseUp, AddressOf QueueMouseUp
        For Each target As Control In New Control() {Me, _fileList}
            AddHandler target.DragEnter, AddressOf FilesDragEnter
            AddHandler target.DragOver, AddressOf FilesDragOver
            AddHandler target.DragDrop, AddressOf FilesDragDrop
        Next
        _loading = False
        RefreshFromHost()
        UpdateScoreMetricUi()
        RefreshActionButtons()
        ResumeLayout(False)
    End Sub

    Friend ReadOnly Property PreviewText As String
        Get
            Return _preview.Text
        End Get
    End Property

    Friend ReadOnly Property IsRunning As Boolean
        Get
            Return _running
        End Get
    End Property

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing AndAlso Not _resourcesDisposed Then
            _resourcesDisposed = True
            _lifetimeCancellation.Cancel()
            _progressRenderTimer.Stop()
            _progressRenderTimer.Dispose()
            For Each item In _items.ToArray()
                item.Cancellation?.Cancel()
            Next
            _taskContextMenu.Dispose()
            _detailFont.Dispose()
            If _schedulerTask Is Nothing OrElse _schedulerTask.IsCompleted Then
                _lifetimeCancellation.Dispose()
            Else
                Dim cleanup = _schedulerTask.ContinueWith(Sub(completed) _lifetimeCancellation.Dispose(), TaskScheduler.Default)
            End If
        End If
        MyBase.Dispose(disposing)
    End Sub

    Protected Overrides Sub OnLayout(e As LayoutEventArgs)
        MyBase.OnLayout(e)
        If _layingOut OrElse _preview Is Nothing OrElse ClientSize.Width <= 0 Then Return
        _layingOut = True
        Try
            Dim pad = Scaled(20)
            Dim gap = Scaled(10)
            Dim width = Math.Max(0, ClientSize.Width - pad * 2)
            Dim row = Scaled(32)
            Dim headerHeight = Scaled(36)
            Dim titleGutter = Math.Min(Scaled(140), width \ 3)
            _backButton.SetBounds(pad, pad + Scaled(2), Math.Min(Scaled(84), titleGutter), row)
            _heading.SetBounds(pad + titleGutter, pad, Math.Max(0, width - titleGutter * 2), headerHeight)
            _refreshParametersButton.SetBounds(pad + Math.Max(0, width - Scaled(130)), pad + Scaled(2), Math.Min(Scaled(130), width), row)

            ' 顶部为左右两栏：预览占左侧，右侧参数按两行两列排列。
            Dim top = pad + headerHeight + Scaled(14)
            Dim columnGap = Scaled(20)
            Dim previewWidth = Math.Max(0, CInt((width - columnGap) * 0.46))
            Dim settingsLeft = pad + previewWidth + columnGap
            Dim settingsWidth = Math.Max(0, width - previewWidth - columnGap)
            _previewCaption.SetBounds(pad, top, previewWidth, Scaled(22))
            Dim previewTop = top + Scaled(30)
            Dim fieldWidth = Math.Max(0, (settingsWidth - gap) \ 2)
            Dim editors As Control() = {_scoreMetric, _crf, _samples, _sampleDuration}
            For index = 0 To editors.Length - 1
                Dim x = settingsLeft + (index Mod 2) * (fieldWidth + gap)
                Dim y = top + (index \ 2) * (Scaled(30) + row + Scaled(12))
                Dim editorWidth = If(index Mod 2 = 0, fieldWidth, Math.Max(0, settingsWidth - fieldWidth - gap))
                _fieldLabels(index).SetBounds(x, y, editorWidth, Scaled(22))
                editors(index).SetBounds(x, y + Scaled(30), editorWidth, row)
            Next
            top = _sampleDuration.Bottom + Scaled(14)

            If GetSelectedMetric() = QualityScoreMetric.Vmaf Then
                Dim captionWidth = Scaled(86)
                Dim scanWidth = Scaled(92)
                Dim browseWidth = Scaled(104)
                _modelCaption.SetBounds(settingsLeft, top, captionWidth, row)
                _vmafModel.SetBounds(settingsLeft + captionWidth, top, Math.Max(0, settingsWidth - captionWidth), row)
                top += row + gap
                _refreshModelsButton.SetBounds(settingsLeft, top, scanWidth, row)
                _browseModelButton.SetBounds(_refreshModelsButton.Right + gap, top, browseWidth, row)
                Dim inlineStatus = settingsWidth >= Scaled(440)
                If inlineStatus Then
                    _vmafModelStatus.SetBounds(_browseModelButton.Right + gap, top,
                                               Math.Max(0, settingsLeft + settingsWidth - _browseModelButton.Right - gap), row)
                    top += row
                Else
                    _vmafModelStatus.SetBounds(settingsLeft, top + row + Scaled(4), settingsWidth, Scaled(20))
                    top += row + Scaled(24)
                End If
            End If
            Dim sectionBottom = Math.Max(top, previewTop + Scaled(160))
            _preview.SetBounds(pad, previewTop, previewWidth, sectionBottom - previewTop)
            top = sectionBottom + Scaled(18)

            ' 文件操作相邻，执行操作相邻；较窄时将执行组换到下一行。
            _addFilesButton.SetBounds(pad, top, Scaled(112), row)
            _removeButton.SetBounds(_addFilesButton.Right + gap, top, Scaled(80), row)
            Dim executionButtons As ModernButton() = {_startButton, _pauseResumeButton, _stopButton, _resetButton}
            Dim buttonWidths = {90, 110, 80, 96}
            Dim executionLeft = _removeButton.Right + Scaled(20)
            Dim executionWidth = buttonWidths.Sum(Function(value) Scaled(value)) + gap * 3
            If executionLeft + executionWidth > pad + width Then
                top += row + gap
                executionLeft = pad
            End If
            Dim xButton = executionLeft
            For index = 0 To executionButtons.Length - 1
                Dim buttonWidth = Scaled(buttonWidths(index))
                executionButtons(index).SetBounds(xButton, top, buttonWidth, row)
                xButton += buttonWidth + gap
            Next
            top += row + Scaled(12)

            Dim showStatus = _running OrElse Not String.IsNullOrWhiteSpace(_status.Text)
            Dim footerTop = Math.Max(top, ClientSize.Height - pad - Scaled(26))
            Dim listBottom = If(showStatus, footerTop - gap, ClientSize.Height - pad)
            _fileList.SetBounds(pad, top, width, Math.Max(0, listBottom - top))
            _progressRing.SetBounds(pad, footerTop + Scaled(1), Scaled(24), Scaled(24))
            Dim statusInset = If(_running, Scaled(32), 0)
            _status.SetBounds(pad + statusInset, footerTop, Math.Max(0, width - statusInset), Scaled(26))
        Finally
            _layingOut = False
        End Try
    End Sub

    Protected Overrides Sub OnDpiChangedAfterParent(e As EventArgs)
        MyBase.OnDpiChangedAfterParent(e)
        PerformLayout()
    End Sub

    Protected Overrides Sub OnHandleCreated(e As EventArgs)
        MyBase.OnHandleCreated(e)
        If _vmafModel IsNot Nothing Then SampleEncodePanelLoad(Me, EventArgs.Empty)
    End Sub

    Private Function Scaled(value As Integer) As Integer
        Return Math.Max(0, CInt(Math.Round(value * DeviceDpi / 96.0R)))
    End Function

    Private Function CreatePreviewBox(name As String) As ModernTextBox
        Dim editor = CreateTextBox(String.Empty)
        editor.Name = name
        editor.MultiLine = True
        editor.ReadOnly = True
        Return editor
    End Function

    Friend Sub RefreshFromHost()
        If _running OrElse _resourcesDisposed Then Return
        Try
            _presetJson = _presetProvider()
            _currentProfile = PresetProfile.LoadJsonForPreview(_presetJson)
            RefreshPreview()
        Catch ex As Exception
            _currentProfile = Nothing
            _preview.Text = "无法读取当前参数：" & ex.Message
            _startButton.Enabled = False
        End Try
    End Sub

    Private Sub RefreshPreview()
        If _loading OrElse _resourcesDisposed OrElse _currentProfile Is Nothing Then Return
        Try
            Dim profile = If(_running, _queueProfile, _currentProfile)
            Dim settings = If(_running, _queueSettings, ReadSettings())
            _preview.Text = profile.BuildSampleParameterPreview(settings)
            _startButton.Enabled = Not _running
        Catch ex As Exception
            _preview.Text = ex.Message
            _startButton.Enabled = False
        End Try
    End Sub

    Private Function GetProfileForCommands() As PresetProfile
        If _running Then Return _queueProfile
        RefreshFromHost()
        If _currentProfile Is Nothing Then Throw New InvalidOperationException("无法读取当前 3FUI 参数。")
        Return _currentProfile
    End Function

    Private Sub SampleSettingsChanged(sender As Object, e As EventArgs)
        RefreshPreview()
    End Sub

    Private Async Sub SampleEncodePanelLoad(sender As Object, e As EventArgs)
        If Not _initialModelScanStarted AndAlso GetSelectedMetric() = QualityScoreMetric.Vmaf Then
            Await RefreshVmafModelsAsync()
        End If
    End Sub

    Private Async Sub ScoreMetricChanged(sender As Object, e As EventArgs)
        UpdateScoreMetricUi()
        RefreshPreview()
        If Not _initialModelScanStarted AndAlso IsHandleCreated AndAlso GetSelectedMetric() = QualityScoreMetric.Vmaf Then
            Await RefreshVmafModelsAsync()
        End If
    End Sub

    Private Function GetSelectedMetric() As QualityScoreMetric
        Return If(String.Equals(_scoreMetric.Text, "XPSNR", StringComparison.OrdinalIgnoreCase),
                  QualityScoreMetric.Xpsnr, QualityScoreMetric.Vmaf)
    End Function

    Private Sub UpdateScoreMetricUi()
        If _modelCaption Is Nothing Then Return
        Dim showModel = GetSelectedMetric() = QualityScoreMetric.Vmaf
        For Each control As Control In New Control() {_modelCaption, _vmafModel, _refreshModelsButton, _browseModelButton, _vmafModelStatus}
            control.Visible = showModel
        Next
        PerformLayout()
    End Sub

    Private Async Sub RefreshModels(sender As Object, e As EventArgs)
        If Not _running Then Await RefreshVmafModelsAsync()
    End Sub

    Private Async Function RefreshVmafModelsAsync() As Task
        If _scanningModels OrElse _resourcesDisposed OrElse GetSelectedMetric() <> QualityScoreMetric.Vmaf Then Return
        _scanningModels = True
        _initialModelScanStarted = True
        _refreshModelsButton.Enabled = False
        _vmafModelStatus.Text = "正在扫描当前 ffmpeg…"
        Try
            Dim result = Await VmafModelScanner.ScanAsync(_lifetimeCancellation.Token)
            If _resourcesDisposed Then Return
            Dim selected = _vmafModel.Text
            _loading = True
            Try
                _vmafModel.Items.Clear()
                _vmafModel.Items.Add("Auto")
                For Each model In result.Models
                    If Not _vmafModel.Items.Cast(Of String)().Contains(model, StringComparer.OrdinalIgnoreCase) Then
                        _vmafModel.Items.Add(model)
                    End If
                Next
                _vmafModel.Text = If(String.IsNullOrWhiteSpace(selected), "Auto", selected)
            Finally
                _loading = False
            End Try
            _vmafModelStatus.ForeColor = If(result.ErrorMessage = "", ColorSuccess, ColorWarning)
            _vmafModelStatus.Text = If(result.ErrorMessage = "", $"发现 {result.Models.Count} 个模型 · {Path.GetFileName(result.FfmpegPath)}", result.ErrorMessage)
            RefreshPreview()
        Catch ex As OperationCanceledException
        Catch ex As Exception
            If Not _resourcesDisposed Then _vmafModelStatus.Text = "扫描失败：" & ex.Message
        Finally
            _scanningModels = False
            If Not _resourcesDisposed Then _refreshModelsButton.Enabled = Not _running
        End Try
    End Function

    Private Sub BrowseVmafModel(sender As Object, e As EventArgs)
        Using dialog As New OpenFileDialog With {
            .Title = "选择本地 VMAF 模型", .Filter = "VMAF 模型 JSON (*.json)|*.json|所有文件 (*.*)|*.*", .CheckFileExists = True
        }
            If dialog.ShowDialog(FindForm()) = DialogResult.OK Then _vmafModel.Text = dialog.FileName
        End Using
    End Sub

    Private Sub AddFiles(sender As Object, e As EventArgs)
        Using dialog As New OpenFileDialog With {
            .Title = "选择要进行样本编码的媒体文件",
            .Filter = "媒体文件|*.mkv;*.mp4;*.mov;*.m4v;*.webm;*.avi;*.ts;*.m2ts;*.flv;*.wmv|所有文件 (*.*)|*.*",
            .CheckFileExists = True,
            .Multiselect = True
        }
            If dialog.ShowDialog(FindForm()) <> DialogResult.OK Then Return
            Dim added = AddFilePaths(dialog.FileNames)
            UpdateAddFilesStatus(added, dialog.FileNames.Length, dragged:=False)
        End Using
    End Sub

    Private Sub FilesDragEnter(sender As Object, e As DragEventArgs)
        SetFileDropEffect(e)
    End Sub

    Private Sub FilesDragOver(sender As Object, e As DragEventArgs)
        SetFileDropEffect(e)
    End Sub

    Private Sub FilesDragDrop(sender As Object, e As DragEventArgs)
        If e.Data Is Nothing OrElse Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        Dim dropped = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
        If dropped Is Nothing Then Return
        Dim added = AddFilePaths(dropped)
        UpdateAddFilesStatus(added, dropped.Length, dragged:=True)
    End Sub

    Private Shared Sub SetFileDropEffect(e As DragEventArgs)
        If e.Data IsNot Nothing AndAlso
           e.Data.GetDataPresent(DataFormats.FileDrop) AndAlso
           (e.AllowedEffect And DragDropEffects.Copy) = DragDropEffects.Copy Then
            e.Effect = DragDropEffects.Copy
        Else
            e.Effect = DragDropEffects.None
        End If
    End Sub

    Friend Function AddFilePaths(filePaths As IEnumerable(Of String)) As Integer
        Dim added = 0
        _fileList.BeginUpdate()
        Try
            For Each candidate In filePaths
                If String.IsNullOrWhiteSpace(candidate) OrElse Not File.Exists(candidate) Then Continue For
                Dim filePath = Path.GetFullPath(candidate)
                If _items.Any(Function(existingItem) String.Equals(existingItem.Path, filePath, StringComparison.OrdinalIgnoreCase)) Then Continue For
                Dim row As New UltraDetailListView.ListItem({
                    New UltraDetailListView.ListSubItem(Path.GetFileName(filePath), Font, ColorText),
                    New UltraDetailListView.ListSubItem("等待", Font, ColorMuted),
                    New UltraDetailListView.ListSubItem("—", Font, ColorMuted),
                    New UltraDetailListView.ListSubItem("—", Font, ColorMuted),
                    New UltraDetailListView.ListSubItem("—", Font, ColorMuted),
                    New UltraDetailListView.ListSubItem("—", Font, ColorMuted)
                })
                Dim item As New SampleQueueFileItem(filePath, row)
                row.Tag = item
                SetBottomLine(item, filePath, ColorMuted)
                _fileList.Items.Add(row)
                _items.Add(item)
                added += 1
            Next
        Finally
            _fileList.EndUpdate()
        End Try
        RefreshActionButtons()
        Return added
    End Function

    Private Sub UpdateAddFilesStatus(added As Integer, supplied As Integer, dragged As Boolean)
        Dim action = If(dragged, "拖入", "选择")
        If added > 0 Then
            Dim skipped = supplied - added
            Dim suffix = If(skipped > 0, $"，忽略 {skipped} 个重复项或非文件项", String.Empty)
            If _running Then
                UpdateStatus($"已{action} {added} 个文件并加入等待队列{suffix}")
            Else
                UpdateStatus($"已{action}并添加 {added} 个文件，列表共 {_items.Count} 个{suffix}")
            End If
        ElseIf supplied > 0 Then
            UpdateStatus($"未添加文件：{action}内容均为重复项或非文件项")
        End If
    End Sub

    Private Async Sub StartQueue(sender As Object, e As EventArgs)
        Try
            Await RunPendingAsync()
        Catch ex As OperationCanceledException
        Catch ex As Exception
            If Not _resourcesDisposed Then UpdateStatus("无法开始：" & ex.Message)
        End Try
    End Sub

    Friend Async Function RunPendingAsync() As Task
        If _running OrElse _resourcesDisposed Then Return
        If Not _items.Any(Function(item) item.State = SampleTaskState.Pending) Then
            Throw New InvalidOperationException("没有等待中的任务；请添加文件或重置任务状态。")
        End If
        ' 每批任务开始时捕获一次最新参数，批内所有文件和运行中的预览共用这一份不可变快照。
        _presetJson = _presetProvider()
        Dim profile = PresetProfile.LoadJson(_presetJson)
        Dim settings = ReadSettings()
        _currentProfile = profile
        _queueProfile = profile
        _queueSettings = settings
        SetRunning(True)
        RefreshPreview()
        Try
            _schedulerTask = RunQueueAsync(profile, settings)
            Await _schedulerTask
        Finally
            _schedulerTask = Nothing
            If Not _resourcesDisposed Then
                SetRunning(False)
                RefreshPreview()
            End If
        End Try
    End Function

    Private Async Function RunQueueAsync(profile As PresetProfile,
                                         settings As SampleEncodeSettings) As Task
        While Not _lifetimeCancellation.IsCancellationRequested
            Dim item = _items.FirstOrDefault(Function(value) value.State = SampleTaskState.Pending)
            If item Is Nothing Then
                Await Task.Yield()
                item = _items.FirstOrDefault(Function(value) value.State = SampleTaskState.Pending)
                If item Is Nothing Then Exit While
            End If
            Await RunQueueItemAsync(item, profile, settings)
        End While

        If Not IsDisposed Then
            Dim completed = _items.Where(Function(value) value.State = SampleTaskState.Completed).Count()
            Dim failed = _items.Where(Function(value) value.State = SampleTaskState.Failed).Count()
            Dim stopped = _items.Where(Function(value) value.State = SampleTaskState.Stopped).Count()
            UpdateStatus($"样本队列结束：{completed} 个完成，{failed} 个失败，{stopped} 个已停止")
        End If
    End Function

    Private Async Function RunQueueItemAsync(item As SampleQueueFileItem,
                                             profile As PresetProfile,
                                             settings As SampleEncodeSettings) As Task
        _activeItem = item
        item.Runner = New AbAv1Runner()
        item.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token)
        item.Row.SubItems(2).Text = SearchSettings.FormatNumber(settings.Crf)
        SetTaskState(item, SampleTaskState.Running)
        SetBottomLine(item, item.Path, ColorMuted)
        UpdateStatus($"{Path.GetFileName(item.Path)} · 正在启动 ab-av1 sample-encode")
        RefreshRows({item})
        RefreshActionButtons()

        Dim localItem = item
        Dim progress As New Progress(Of SearchProgress)(
            Sub(update)
                If IsDisposed Then Return
                QueueProgressRender(localItem, update)
            End Sub)
        Try
            Dim result = Await item.Runner.SampleEncodeAsync(
                profile,
                item.Path,
                settings,
                progress,
                item.Cancellation.Token)
            If _resourcesDisposed Then Return
            item.Result = result
            item.Row.SubItems(2).Text = SearchSettings.FormatNumber(result.Crf)
            item.Row.SubItems(3).Text = FormatMetricScore(result.Metric, result.Score)
            item.Row.SubItems(4).Text = FormatBytes(result.PredictedEncodeSize)
            item.Row.SubItems(5).Text = FormatDuration(result.PredictedEncodeSeconds)
            SetTaskState(item, SampleTaskState.Completed)

            Dim detail = $"预测完整编码：{FormatBytes(result.PredictedEncodeSize)}"
            If result.PredictedEncodeSeconds > 0 Then detail &= $" · {FormatDuration(result.PredictedEncodeSeconds)}"
            If result.PredictedEncodePercent > 0 Then detail &= $" · 输入大小的 {result.PredictedEncodePercent.ToString("0.##", CultureInfo.InvariantCulture)}%"
            SetBottomLine(item, detail, ColorMuted)
        Catch ex As OperationCanceledException
            SetTaskState(item, SampleTaskState.Stopped)
            SetBottomLine(item, "任务已停止；可重置后重新评估", ColorWarning)
        Catch ex As Exception
            If item.Cancellation IsNot Nothing AndAlso item.Cancellation.IsCancellationRequested Then
                SetTaskState(item, SampleTaskState.Stopped)
                SetBottomLine(item, "任务已停止；可重置后重新评估", ColorWarning)
            Else
                item.ErrorMessage = ex.Message
                SetTaskState(item, SampleTaskState.Failed)
                SetBottomLine(item, CompactMessage(ex.Message), ColorDanger)
            End If
        Finally
            ClearPendingProgress(item)
            If item.Runner IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(item.Runner.CurrentCommandLine) Then
                item.CommandLine = item.Runner.CurrentCommandLine
            End If
            item.Cancellation?.Dispose()
            item.Cancellation = Nothing
            item.Runner = Nothing
            If Object.ReferenceEquals(_activeItem, item) Then _activeItem = Nothing
            If Not IsDisposed Then
                RefreshRows({item})
                RefreshActionButtons()
            End If
        End Try
    End Function

    Private Sub StopTasks(sender As Object, e As EventArgs)
        Dim targets = GetOperationTargets(fallbackToActive:=True)
        If targets.Count = 0 Then
            UpdateStatus("请选择要停止的任务，或等待任务开始运行。")
            Return
        End If
        Dim changed = 0
        For Each item In targets
            Select Case item.State
                Case SampleTaskState.Pending
                    SetTaskState(item, SampleTaskState.Stopped)
                    SetBottomLine(item, "任务未启动即被停止；可重置后重新评估", ColorWarning)
                    changed += 1
                Case SampleTaskState.Running, SampleTaskState.Paused
                    SetTaskState(item, SampleTaskState.Stopping)
                    item.Cancellation?.Cancel()
                    changed += 1
            End Select
        Next
        If changed > 0 Then
            UpdateStatus($"正在停止 {changed} 个任务…")
            RefreshRows(targets)
        End If
        RefreshActionButtons()
    End Sub

    Private Sub PauseOrResumeTasks(sender As Object, e As EventArgs)
        Dim targets = GetOperationTargets(fallbackToActive:=True)
        Dim activeTargets = targets.Where(
            Function(item) item.State = SampleTaskState.Running OrElse
                           item.State = SampleTaskState.Paused).ToList()
        If activeTargets.Count = 0 Then
            UpdateStatus("请选择正在运行或已暂停的任务。")
            Return
        End If
        Dim shouldPause = activeTargets.Any(Function(item) item.State = SampleTaskState.Running)
        Dim changed = 0
        Dim lastError = String.Empty
        For Each item In activeTargets
            If item.Runner Is Nothing Then Continue For
            Dim errorMessage As String = Nothing
            If shouldPause AndAlso item.State = SampleTaskState.Running Then
                If item.Runner.TryPause(errorMessage) Then
                    SetTaskState(item, SampleTaskState.Paused)
                    changed += 1
                Else
                    lastError = errorMessage
                End If
            ElseIf Not shouldPause AndAlso item.State = SampleTaskState.Paused Then
                If item.Runner.TryResume(errorMessage) Then
                    SetTaskState(item, SampleTaskState.Running)
                    changed += 1
                Else
                    lastError = errorMessage
                End If
            End If
        Next
        If changed > 0 Then
            UpdateStatus(If(shouldPause,
                            $"已暂停 {changed} 个运行任务；其他状态的任务未受影响。",
                            $"已恢复 {changed} 个暂停任务；其他状态的任务未受影响。"))
            RefreshRows(activeTargets)
        ElseIf lastError <> "" Then
            UpdateStatus("无法切换状态：" & CompactMessage(lastError))
        Else
            UpdateStatus("所选任务中没有可执行此操作的活动任务。")
        End If
        RefreshActionButtons()
    End Sub

    Private Sub RemoveTasks(sender As Object, e As EventArgs)
        Dim targets = GetSelectedQueueItems()
        If targets.Count = 0 Then
            UpdateStatus("请先选择要移除的任务。")
            Return
        End If
        If targets.Any(Function(item) item.State = SampleTaskState.Running OrElse
                                       item.State = SampleTaskState.Paused OrElse
                                       item.State = SampleTaskState.Stopping) Then
            UpdateStatus("运行中、已暂停或正在停止的任务不能直接移除；请先停止。")
            Return
        End If
        _fileList.BeginUpdate()
        Try
            For Each item In targets
                _fileList.Items.Remove(item.Row)
                _items.Remove(item)
            Next
        Finally
            _fileList.EndUpdate()
        End Try
        UpdateStatus($"已移除 {targets.Count} 个任务。")
        RefreshActionButtons()
    End Sub

    Private Sub ResetTasks(sender As Object, e As EventArgs)
        Dim targets = GetSelectedQueueItems()
        If targets.Count = 0 Then
            UpdateStatus("请先选择要重置状态的任务。")
            Return
        End If
        Dim changed = 0
        For Each item In targets
            If item.State = SampleTaskState.Running OrElse
               item.State = SampleTaskState.Paused OrElse
               item.State = SampleTaskState.Stopping Then Continue For
            item.Result = Nothing
            item.ErrorMessage = String.Empty
            item.CommandLine = String.Empty
            For index = 2 To 5
                item.Row.SubItems(index).Text = "—"
            Next
            SetTaskState(item, SampleTaskState.Pending)
            SetBottomLine(item, item.Path, ColorMuted)
            changed += 1
        Next
        If changed > 0 Then
            RefreshRows(targets)
            UpdateStatus($"已重置 {changed} 个任务；运行中的队列会自动继续处理。")
        Else
            UpdateStatus("所选任务正在运行，无法重置。")
        End If
        RefreshActionButtons()
    End Sub

    Private Sub QueueKeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode = Keys.Delete Then
            RemoveTasks(sender, EventArgs.Empty)
            e.Handled = True
        End If
    End Sub

    Private Sub QueueMouseUp(sender As Object, e As MouseEventArgs)
        If e.Button <> MouseButtons.Right Then Return
        _contextMenuTarget = Nothing
        Dim hitIndex = _fileList.HitTest(e.X, e.Y)
        If hitIndex >= 0 Then
            _contextMenuTarget = TryCast(_fileList.Items(hitIndex).Tag, SampleQueueFileItem)
            If Not _fileList.SelectedIndices.Contains(hitIndex) Then _fileList.SelectedIndex = hitIndex
        End If
        RebuildTaskContextMenu()
        If _taskContextMenu.Items.Count > 0 Then _taskContextMenu.Show(_fileList, e.X, e.Y)
    End Sub

    Private Sub RebuildTaskContextMenu()
        _taskContextMenu.Items.Clear()
        Dim selected = GetSelectedQueueItems()
        Dim targets = GetOperationTargets(fallbackToActive:=True)
        Dim activeTargets = targets.Where(
            Function(item) item.State = SampleTaskState.Running OrElse
                           item.State = SampleTaskState.Paused).ToList()

        If _contextMenuTarget IsNot Nothing Then
            AddTaskContextMenuItem("复制此任务的完整命令行", AddressOf CopyTaskCommandLine)
        End If
        Dim hasLifecycleAction = (Not _running AndAlso _items.Any(Function(item) item.State = SampleTaskState.Pending)) OrElse
                                 activeTargets.Count > 0 OrElse
                                 targets.Any(Function(item) item.State = SampleTaskState.Pending OrElse
                                                           item.State = SampleTaskState.Running OrElse
                                                           item.State = SampleTaskState.Paused)
        If hasLifecycleAction AndAlso _taskContextMenu.Items.Count > 0 Then AddTaskContextMenuSeparator()
        If Not _running AndAlso _items.Any(Function(item) item.State = SampleTaskState.Pending) Then
            AddTaskContextMenuItem("开始样本编码", AddressOf StartQueue)
        End If
        If activeTargets.Any(Function(item) item.State = SampleTaskState.Running) Then
            AddTaskContextMenuItem("暂停", AddressOf PauseOrResumeTasks)
        ElseIf activeTargets.Count > 0 Then
            AddTaskContextMenuItem("恢复", AddressOf PauseOrResumeTasks)
        End If
        If targets.Any(Function(item) item.State = SampleTaskState.Pending OrElse
                                      item.State = SampleTaskState.Running OrElse
                                      item.State = SampleTaskState.Paused) Then
            AddTaskContextMenuItem("停止", AddressOf StopTasks, danger:=True)
        End If
        Dim canReset = selected.Any(
            Function(item) item.State <> SampleTaskState.Running AndAlso
                           item.State <> SampleTaskState.Paused AndAlso
                           item.State <> SampleTaskState.Stopping)
        Dim canRemove = selected.Count > 0 AndAlso
                        Not selected.Any(
                            Function(item) item.State = SampleTaskState.Running OrElse
                                           item.State = SampleTaskState.Paused OrElse
                                           item.State = SampleTaskState.Stopping)
        If canReset OrElse canRemove Then
            If _taskContextMenu.Items.Count > 0 Then AddTaskContextMenuSeparator()
            If canReset Then AddTaskContextMenuItem("重置所选任务状态", AddressOf ResetTasks)
            If canRemove Then AddTaskContextMenuItem("移除所选任务", AddressOf RemoveTasks)
        End If
    End Sub

    Private Async Sub CopyTaskCommandLine(sender As Object, e As EventArgs)
        Dim item = _contextMenuTarget
        If item Is Nothing OrElse Not _items.Contains(item) Then
            UpdateStatus("未找到要复制命令行的任务。")
            Return
        End If
        Try
            Dim commandLine = item.CommandLine
            If String.IsNullOrWhiteSpace(commandLine) AndAlso item.Runner IsNot Nothing Then
                commandLine = item.Runner.CurrentCommandLine
            End If
            If String.IsNullOrWhiteSpace(commandLine) Then
                Dim profile = GetProfileForCommands()
                Dim settings = ReadSettings()
                commandLine = Await AbAv1Runner.BuildSampleCommandLineAsync(
                    profile,
                    item.Path,
                    settings,
                    _lifetimeCancellation.Token)
            End If
            CopyCommandLineToClipboard(
                commandLine,
                $"已复制 {Path.GetFileName(item.Path)} 的完整 sample-encode 命令行。")
        Catch ex As OperationCanceledException When _lifetimeCancellation.IsCancellationRequested
        Catch ex As Exception
            ShowCopyCommandLineError(ex)
        End Try
    End Sub

    Private Sub CopyCommandLineToClipboard(commandLine As String, successMessage As String)
        If _resourcesDisposed Then Return
        If String.IsNullOrWhiteSpace(commandLine) Then Throw New InvalidOperationException("生成的命令行为空。")
        Clipboard.SetText(commandLine)
        UpdateStatus(successMessage)
    End Sub

    Private Sub ShowCopyCommandLineError(exception As Exception)
        If _resourcesDisposed Then Return
        Dim message = "无法复制命令行：" & CompactMessage(exception.Message)
        UpdateStatus(message)
        MessageBox.Show(FindForm(), exception.Message, "复制 ab-av1 命令行", MessageBoxButtons.OK, MessageBoxIcon.Warning)
    End Sub

    Private Sub AddTaskContextMenuItem(text As String,
                                       handler As EventHandler,
                                       Optional danger As Boolean = False)
        Dim item As New ModernContextMenu.ModernMenuItem(text) With {
            .Font = Nothing,
            .ForeColor = If(danger, ColorDanger, ColorText),
            .CloseOnClick = True
        }
        AddHandler item.Click, handler
        _taskContextMenu.Items.Add(item)
    End Sub

    Private Sub AddTaskContextMenuSeparator()
        _taskContextMenu.Items.Add(New ModernContextMenu.ModernMenuItem With {.IsSeparator = True})
    End Sub

    Private Sub QueueSelectionChanged(sender As Object, e As EventArgs)
        RefreshActionButtons()
    End Sub

    Private Function GetSelectedQueueItems() As List(Of SampleQueueFileItem)
        Dim result As New List(Of SampleQueueFileItem)()
        For Each row In _fileList.SelectedItems
            Dim item = TryCast(row.Tag, SampleQueueFileItem)
            If item IsNot Nothing AndAlso _items.Contains(item) Then result.Add(item)
        Next
        Return result
    End Function

    Private Function GetOperationTargets(fallbackToActive As Boolean) As List(Of SampleQueueFileItem)
        Dim selected = GetSelectedQueueItems()
        If selected.Count = 0 AndAlso fallbackToActive AndAlso _activeItem IsNot Nothing Then selected.Add(_activeItem)
        Return selected
    End Function

    Friend Function ReadSettings() As SampleEncodeSettings
        Dim crf As Double
        If Not TryParseNumber(_crf.Text, crf) Then Throw New FormatException("CRF 不是有效数字。")
        Dim samples As Integer? = Nothing
        If Not String.IsNullOrWhiteSpace(_samples.Text) Then
            Dim value As Integer
            If Not Integer.TryParse(_samples.Text.Trim(), value) Then Throw New FormatException("采样数量不是有效整数。")
            samples = value
        End If
        Dim model = _vmafModel.Text.Trim()
        Dim settings As New SampleEncodeSettings With {
            .Metric = GetSelectedMetric(), .Crf = crf, .Samples = samples,
            .SampleDuration = _sampleDuration.Text.Trim(),
            .VmafModel = If(String.Equals(model, "Auto", StringComparison.OrdinalIgnoreCase), String.Empty, model)
        }
        settings.Validate()
        Return settings
    End Function

    Private Sub SetRunning(value As Boolean)
        _running = value
        _startButton.Enabled = Not value
        _refreshParametersButton.Enabled = Not value
        For Each editor As Control In New Control() {_scoreMetric, _crf, _samples, _sampleDuration, _vmafModel, _browseModelButton}
            editor.Enabled = Not value
        Next
        _refreshModelsButton.Enabled = Not value AndAlso Not _scanningModels
        _progressRing.Visible = value
        If value Then
            _progressRing.StartAnimation()
        Else
            _progressRing.StopAnimation()
        End If
        RefreshActionButtons()
        PerformLayout()
    End Sub

    Private Shared Function FormatBytes(bytes As Long) As String
        If bytes <= 0 Then Return "—"
        Dim value As Double = bytes
        Dim units = {"B", "KiB", "MiB", "GiB", "TiB"}
        Dim index = 0
        While value >= 1024 AndAlso index < units.Length - 1
            value /= 1024
            index += 1
        End While
        Return value.ToString("0.##", CultureInfo.InvariantCulture) & " " & units(index)
    End Function

    Private Shared Sub FitTaskColumns(list As UltraDetailListView, fixedWidths As Integer())
        Dim scale = list.DeviceDpi / 96.0R
        Dim total = 0
        For index = 0 To fixedWidths.Length - 1
            Dim columnWidth = CInt(Math.Round(fixedWidths(index) * scale))
            list.Columns(index + 1).Width = columnWidth
            total += columnWidth
        Next
        list.Columns(0).Width = Math.Max(CInt(120 * scale), list.ClientSize.Width - total - CInt(12 * scale))
    End Sub

    Private Sub RefreshActionButtons()
        If IsDisposed Then Return
        Dim selected = GetSelectedQueueItems()
        Dim targets = If(selected.Count > 0,
                         selected,
                         If(_activeItem Is Nothing,
                            New List(Of SampleQueueFileItem)(),
                            New List(Of SampleQueueFileItem) From {_activeItem}))
        _stopButton.Enabled = targets.Any(Function(item) item.State = SampleTaskState.Pending OrElse
                                                         item.State = SampleTaskState.Running OrElse
                                                         item.State = SampleTaskState.Paused)
        Dim activeTargets = targets.Where(
            Function(item) item.State = SampleTaskState.Running OrElse
                           item.State = SampleTaskState.Paused).ToList()
        _pauseResumeButton.Enabled = activeTargets.Count > 0
        If activeTargets.Any(Function(item) item.State = SampleTaskState.Running) Then
            _pauseResumeButton.Text = "暂停"
        ElseIf activeTargets.Count > 0 Then
            _pauseResumeButton.Text = "恢复"
        Else
            _pauseResumeButton.Text = "暂停 / 恢复"
        End If
        _removeButton.Enabled = selected.Count > 0 AndAlso
                                Not selected.Any(Function(item) item.State = SampleTaskState.Running OrElse
                                                               item.State = SampleTaskState.Paused OrElse
                                                               item.State = SampleTaskState.Stopping)
        _resetButton.Enabled = selected.Any(Function(item) item.State <> SampleTaskState.Running AndAlso
                                                           item.State <> SampleTaskState.Paused AndAlso
                                                           item.State <> SampleTaskState.Stopping)
    End Sub

    Private Sub SetTaskState(item As SampleQueueFileItem, state As SampleTaskState)
        item.State = state
        If _resourcesDisposed Then Return
        Dim text As String
        Dim color As Color
        Select Case state
            Case SampleTaskState.Pending
                text = "等待"
                color = ColorMuted
            Case SampleTaskState.Running
                text = "样本编码中"
                color = ColorAccent
            Case SampleTaskState.Paused
                text = "已暂停"
                color = ColorWarning
            Case SampleTaskState.Stopping
                text = "正在停止"
                color = ColorWarning
            Case SampleTaskState.Completed
                text = "已完成"
                color = ColorSuccess
            Case SampleTaskState.Failed
                text = "失败"
                color = ColorDanger
            Case SampleTaskState.Stopped
                text = "已停止"
                color = ColorWarning
            Case Else
                text = state.ToString()
                color = ColorMuted
        End Select
        item.Row.SubItems(1).Text = text
        item.Row.SubItems(1).ForeColor = color
    End Sub

    Private Sub SetBottomLine(item As SampleQueueFileItem, text As String, color As Color)
        If _resourcesDisposed Then Return
        item.Row.BottomLines.Clear()
        item.Row.BottomLines.Add(New UltraDetailListView.TextLine(text, _detailFont, color))
    End Sub

    Private Sub QueueProgressRender(item As SampleQueueFileItem, update As SearchProgress)
        If IsDisposed OrElse item Is Nothing OrElse Not _items.Contains(item) Then Return
        If item.State <> SampleTaskState.Running AndAlso item.State <> SampleTaskState.Paused Then Return
        _pendingProgressItem = item
        _pendingProgressStatus = $"{Path.GetFileName(item.Path)} · {CompactMessage(update.Message)}"
        If update.TestedScore.HasValue Then
            Dim metric = update.Metric
            _pendingProgressScore = FormatMetricScore(metric, update.TestedScore.Value)
        End If
        If Not _progressRenderTimer.Enabled Then _progressRenderTimer.Start()
    End Sub

    Private Sub ProgressRenderTimerTick(sender As Object, e As EventArgs)
        _progressRenderTimer.Stop()
        FlushPendingProgressRender()
    End Sub

    Private Sub FlushPendingProgressRender()
        If IsDisposed Then Return
        Dim item = _pendingProgressItem
        Dim statusText = _pendingProgressStatus
        Dim scoreText = _pendingProgressScore
        _pendingProgressItem = Nothing
        _pendingProgressStatus = Nothing
        _pendingProgressScore = Nothing
        If statusText IsNot Nothing Then UpdateStatus(statusText)
        If item Is Nothing OrElse Not _items.Contains(item) Then Return
        If item.State <> SampleTaskState.Running AndAlso item.State <> SampleTaskState.Paused Then Return
        If scoreText IsNot Nothing AndAlso item.Row.SubItems(3).Text <> scoreText Then
            item.Row.SubItems(3).Text = scoreText
            RefreshRows({item})
        End If
    End Sub

    Private Sub ClearPendingProgress(item As SampleQueueFileItem)
        If Not Object.ReferenceEquals(_pendingProgressItem, item) Then Return
        If Not IsDisposed Then _progressRenderTimer.Stop()
        _pendingProgressItem = Nothing
        _pendingProgressStatus = Nothing
        _pendingProgressScore = Nothing
    End Sub

    Private Sub RefreshRows(items As IEnumerable(Of SampleQueueFileItem))
        If IsDisposed OrElse items Is Nothing Then Return
        Dim targets = items.Where(Function(item) item IsNot Nothing AndAlso _items.Contains(item)).Distinct().ToList()
        If targets.Count = 0 Then Return
        _fileList.BeginUpdate()
        Try
            For Each item In targets
                item.Row.InvalidateCache()
            Next
        Finally
            _fileList.EndUpdate()
        End Try
    End Sub

    Private Sub UpdateStatus(message As String)
        If IsDisposed Then Return
        Dim text = CompactMessage(message)
        Dim visibilityChanged = String.IsNullOrWhiteSpace(_status.Text) <> String.IsNullOrWhiteSpace(text)
        If Not String.Equals(_status.Text, text, StringComparison.Ordinal) Then _status.Text = text
        _status.Visible = Not String.IsNullOrWhiteSpace(text)
        If visibilityChanged Then PerformLayout()
    End Sub

    Private Shared Function CompactMessage(message As String) As String
        If String.IsNullOrWhiteSpace(message) Then Return String.Empty
        Dim compact = String.Join(" ", message.Split({ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)).Trim()
        If compact.Length > 260 Then compact = compact.Substring(0, 257) & "…"
        Return compact
    End Function

    Private Shared Function TryParseNumber(text As String, ByRef value As Double) As Boolean
        Return Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, value) OrElse
               Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, value)
    End Function

    Private Shared Function FormatMetricScore(metric As QualityScoreMetric, score As Double) As String
        Return $"{SearchSettings.GetMetricDisplayName(metric)} {score.ToString("0.###", CultureInfo.InvariantCulture)}"
    End Function

    Private Shared Function FormatDuration(seconds As Double) As String
        If Double.IsNaN(seconds) OrElse Double.IsInfinity(seconds) OrElse seconds <= 0 Then Return "—"
        Dim duration = TimeSpan.FromSeconds(seconds)
        If duration.TotalDays >= 1 Then Return $"{CInt(Math.Floor(duration.TotalDays))}天 {duration:hh\:mm\:ss}"
        If duration.TotalHours >= 1 Then Return duration.ToString("h\:mm\:ss", CultureInfo.InvariantCulture)
        Return duration.ToString("m\:ss", CultureInfo.InvariantCulture)
    End Function

    Private Shared Function CreateTaskContextMenu() As ModernContextMenu
        Return New ModernContextMenu With {
            .AnimationFPS = 120,
            .BackdropBlurPasses = 2,
            .BackdropBlurRadius = 30,
            .BackdropMode = ModernContextMenu.BackdropModeEnum.Auto,
            .BackdropNoiseOpacity = 0,
            .BackdropTintColor = Color.FromArgb(40, 0, 0, 0),
            .BackColor1 = ColorBackground,
            .BorderColor = Color.FromArgb(80, 220, 220, 220),
            .BorderSize = 1,
            .HoverBackColor = ColorControlHover,
            .PressedBackColor = ColorControlPressed,
            .HoverRadius = 6,
            .IconSize = 0,
            .ItemHeight = 38,
            .ItemPadding = New Padding(14, 0, 14, 0),
            .MenuFont = New Font("Microsoft YaHei UI", 10.0F),
            .MenuForeColor = ColorText,
            .MenuPadding = New Padding(10),
            .SeparatorColor = Color.FromArgb(80, 220, 220, 220),
            .SeparatorHeight = 14
        }
    End Function

    Private Function CreateTextBox(waterText As String) As ModernTextBox
        Return New ModernTextBox With {
            .AutoScaleMode = AutoScaleMode.None,
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0, 5, 0, 5),
            .Padding = New Padding(10, 0, 10, 0),
            .BackColor1 = ColorControl,
            .BackgroundSource = Me,
            .ForeColor = ColorText,
            .WaterText = waterText,
            .WaterTextForeColor = ColorMuted,
            .CaretColor = ColorText,
            .SelectionColor = ColorControl,
            .BorderColor = Color.Transparent,
            .BorderColorFocus = ColorControlPressed,
            .BorderSize = 0,
            .BorderRadius = 10,
            .MultiLine = False
        }
    End Function

    Private Function CreateComboBox(waterText As String) As ModernComboBox
        Return New ModernComboBox With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0, 5, 0, 5),
            .Padding = New Padding(10, 0, 10, 0),
            .BackColor1 = ColorControl,
            .BackgroundSource = Me,
            .BackColor2 = ColorControl,
            .HoverBackColor1 = ColorControlHover,
            .HoverBackColor2 = ColorControlHover,
            .PressedBackColor1 = ColorControlPressed,
            .PressedBackColor2 = ColorControlPressed,
            .ForeColor = ColorText,
            .WaterText = waterText,
            .WaterTextForeColor = ColorMuted,
            .CaretColor = ColorText,
            .SelectionColor = ColorControl,
            .ArrowColor = ColorMuted,
            .BorderColor = Color.Transparent,
            .BorderColorFocus = ColorControlPressed,
            .BorderSize = 0,
            .BorderRadius = 10,
            .Editable = True,
            .DropDownMode = ModernComboBox.DropDownDisplayMode.Overlay,
            .MaxDropDownItems = 12
        }
    End Function

    Private Function CreateMetricComboBox() As ModernComboBox
        Dim combo = CreateComboBox("选择评分指标")
        combo.Editable = False
        combo.Items.Add("VMAF")
        combo.Items.Add("XPSNR")
        combo.SelectedIndex = 0
        Return combo
    End Function

    Private Function CreateButton(text As String,
                                         handler As EventHandler,
                                         Optional accent As Boolean = False,
                                         Optional danger As Boolean = False) As ModernButton
        Dim baseColor = If(accent,
                           Color.FromArgb(80, 71, 156, 255),
                           If(danger, Color.FromArgb(40, 235, 93, 93), ColorControl))
        Dim hoverColor = If(accent,
                            Color.FromArgb(110, 71, 156, 255),
                            If(danger, Color.FromArgb(60, 235, 93, 93), ColorControlHover))
        Dim pressedColor = If(accent,
                              Color.FromArgb(140, 71, 156, 255),
                              If(danger, Color.FromArgb(80, 235, 93, 93), ColorControlPressed))
        Dim button As New ModernButton With {
            .AutoScaleMode = AutoScaleMode.None,
            .Text = text,
            .Font = Me.Font,
            .ForeColor = If(danger, ColorDanger, ColorText),
            .BackColor1 = baseColor,
            .BackgroundSource = Me,
            .BackColor2 = baseColor,
            .HoverBackColor1 = hoverColor,
            .HoverBackColor2 = hoverColor,
            .PressedBackColor1 = pressedColor,
            .PressedBackColor2 = pressedColor,
            .BorderColor = Color.Transparent,
            .HoverBorderColor = Color.Transparent,
            .BorderSize = 0,
            .BorderRadius = 10,
            .Size = New Size(108, 32),
            .Margin = New Padding(0, 5, 8, 5)
        }
        AddHandler button.Click, handler
        Return button
    End Function

    Private Function CreateFileList() As UltraDetailListView
        'Keep the translucent rounded card and its header in one GPU surface.
        Dim list As New UltraDetailListView With {
            .AutoScaleMode = AutoScaleMode.None,
            .Dock = DockStyle.Fill,
            .Margin = Padding.Empty,
            .BackgroundColor = ColorControl,
            .BackgroundSource = Me,
            .BorderColor = Color.Transparent,
            .BorderSize = 0,
            .BorderRadius = 10,
            .HeaderVisible = True,
            .HeaderHeight = 40,
            .HeaderBackColor = Color.Transparent,
            .HeaderForeColor = ColorText,
            .HeaderBorderColor = Color.FromArgb(32, 255, 255, 255),
            .HeaderBorderWidth = 1,
            .ContentPadding = New Padding(0, 8, 0, 8),
            .ItemPadding = New Padding(14, 11, 14, 11),
            .ItemSpacing = 6,
            .ItemCornerRadius = 10,
            .ItemForeColor = ColorText,
            .ItemHoverBackColor = ColorControl,
            .ItemSelectedBackColor = ColorControlHover,
            .MultiSelect = True,
            .AllowColumnResize = True,
            .AllowDragReorder = False,
            .WordWrap = False,
            .BottomLinesSpacing = 4,
            .TextLineSpacing = 2,
            .ScrollBarWidth = 8,
            .ScrollBarTrackColor = Color.Transparent,
            .ScrollBarThumbColor = ColorControl,
            .ScrollBarThumbHoverColor = ColorControlHover,
            .SelectionRectFillColor = Color.FromArgb(20, 71, 156, 255),
            .SelectionRectBorderColor = Color.FromArgb(80, 71, 156, 255),
            .Font = Me.Font
        }
        list.Columns.Add(New UltraDetailListView.ListColumn("文件", 480))
        list.Columns.Add(New UltraDetailListView.ListColumn("状态", 80))
        list.Columns.Add(New UltraDetailListView.ListColumn("CRF", 56))
        list.Columns.Add(New UltraDetailListView.ListColumn("分数", 88))
        list.Columns.Add(New UltraDetailListView.ListColumn("预测大小（视频流）", 160))
        list.Columns.Add(New UltraDetailListView.ListColumn("预测耗时", 100))
        Dim fitColumns As EventHandler = Sub(sender, e) FitTaskColumns(list, {80, 56, 88, 160, 100})
        AddHandler list.SizeChanged, fitColumns
        AddHandler list.DpiChangedAfterParent, fitColumns
        Return list
    End Function

    Private Function CreateFieldLabel(text As String) As HtmlColorLabel
        Dim label = CreateLabel(text, ColorMuted, 9.0F)
        label.Dock = DockStyle.Fill
        label.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleLeft
        label.Margin = New Padding(2, 0, 2, 0)
        Return label
    End Function

    Private Function CreateLabel(text As String,
                                        color As Color,
                                        size As Single,
                                        Optional style As FontStyle = FontStyle.Regular) As HtmlColorLabel
        Return New HtmlColorLabel With {
            .AutoScaleMode = AutoScaleMode.None,
            .Text = text,
            .ForeColor = color,
            .BackColor = Color.Transparent,
            .BackColor1 = Color.Transparent,
            .BackgroundSource = Me,
            .BorderSize = 0,
            .Font = New Font(Me.Font.FontFamily, size, style),
            .TextAlign = HtmlColorLabel.TextAlignEnum.TopLeft
        }
    End Function

    Private Enum SampleTaskState
        Pending
        Running
        Paused
        Stopping
        Completed
        Failed
        Stopped
    End Enum

    Private NotInheritable Class SampleQueueFileItem
        Public Sub New(path As String, row As UltraDetailListView.ListItem)
            Me.Path = path
            Me.Row = row
        End Sub

        Public ReadOnly Property Path As String
        Public ReadOnly Property Row As UltraDetailListView.ListItem
        Public Property State As SampleTaskState = SampleTaskState.Pending
        Public Property Runner As AbAv1Runner
        Public Property Cancellation As CancellationTokenSource
        Public Property Result As SampleEncodeResult
        Public Property ErrorMessage As String = String.Empty
        Public Property CommandLine As String = String.Empty
    End Class

End Class


