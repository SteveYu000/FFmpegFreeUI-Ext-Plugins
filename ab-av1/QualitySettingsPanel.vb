Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Threading
Imports System.Windows.Forms
Imports FFmpegFreeUI.Ext.PluginSdk
Imports LakeUI

''' <summary>
''' 紧贴 3FUI 质量页的 LakeUI 参数区。所有可见输入控件都复用宿主同版本
''' ModernTextBox / ModernComboBox / ModernButton / ModernCheckBox 的尺寸、圆角和颜色。
''' </summary>
Friend NotInheritable Class QualitySettingsPanel
    Inherits UserControl

    Private Shared ReadOnly ColorControl As Color = Color.FromArgb(40, 220, 220, 220)
    Private Shared ReadOnly ColorControlHover As Color = Color.FromArgb(60, 220, 220, 220)
    Private Shared ReadOnly ColorControlPressed As Color = Color.FromArgb(80, 220, 220, 220)
    Private Shared ReadOnly ColorText As Color = Color.FromArgb(220, 220, 220)
    Private Shared ReadOnly ColorMuted As Color = Color.FromArgb(120, 255, 255, 255)
    Private Shared ReadOnly ColorSuccess As Color = Color.FromArgb(63, 205, 135)
    Private Shared ReadOnly ColorDanger As Color = Color.FromArgb(235, 93, 93)
    Private Const RefreshDelayMilliseconds As Integer = 120
    Private Const FieldLabelHeight As Integer = 30
    Private Const HostControlHeight As Integer = 32
    Private Const HostControlRowHeight As Integer = 42
    Private Const ModelRowHeight As Integer = 42
    Private Const CompactPanelHeight As Integer =
        FieldLabelHeight + HostControlRowHeight + ModelRowHeight

    Private ReadOnly _context As IExtPluginUiContext
    Private ReadOnly _stateChanged As Action(Of String)
    Private ReadOnly _qualityValueStatus As QualityValueStatusAdornment
    Private ReadOnly _lifetimeCancellation As New CancellationTokenSource()
    Private ReadOnly _normalTextBoxBorders As New Dictionary(Of ModernTextBox, TextBoxBorderStyle)()
    Private ReadOnly _refreshTimer As System.Windows.Forms.Timer
    Private ReadOnly _targetVmaf As ModernTextBox
    Private ReadOnly _minCrf As ModernTextBox
    Private ReadOnly _maxCrf As ModernTextBox
    Private ReadOnly _samples As ModernTextBox
    Private ReadOnly _sampleDuration As ModernTextBox
    Private ReadOnly _thorough As ModernCheckBox
    Private ReadOnly _vmafModel As ModernComboBox
    Private ReadOnly _scanModelsButton As ModernButton
    Private ReadOnly _modelStatus As Label
    Private ReadOnly _validationStatus As Label
    Private ReadOnly _environmentStatus As Label
    Private ReadOnly _stateRestoredHandler As EventHandler
    Private _resourcesDisposed As Boolean
    Private _restoring As Boolean

    Public Sub New(context As IExtPluginUiContext,
                   Optional stateChanged As Action(Of String) = Nothing)
        If context Is Nothing Then Throw New ArgumentNullException(NameOf(context))
        _context = context
        _stateChanged = stateChanged

        SuspendLayout()
        DoubleBuffered = True
        SetStyle(
            ControlStyles.AllPaintingInWmPaint Or
            ControlStyles.OptimizedDoubleBuffer Or
            ControlStyles.SupportsTransparentBackColor,
            True)
        AutoScaleDimensions = New SizeF(96.0F, 96.0F)
        AutoScaleMode = AutoScaleMode.Dpi
        AutoSize = False
        BackColor = Color.Transparent
        Dock = DockStyle.Top
        Font = context.AnchorControl.Font
        ForeColor = ColorText
        Height = CompactPanelHeight
        Margin = Padding.Empty
        MinimumSize = New Size(0, CompactPanelHeight)
        Padding = Padding.Empty

        Dim nativeTextBox = TryCast(
            context.GetAnchorControl(ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityValue),
            ModernTextBox)
        Dim nativeComboBox = TryCast(
            context.GetAnchorControl(ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityMode),
            ModernComboBox)
        Dim backgroundSource = ResolveBackgroundSource(context.AnchorControl, nativeTextBox, nativeComboBox)
        _targetVmaf = CreateTextBox("目标 VMAF", backgroundSource, nativeTextBox)
        _minCrf = CreateTextBox("最小 CRF", backgroundSource, nativeTextBox)
        _maxCrf = CreateTextBox("最大 CRF", backgroundSource, nativeTextBox)
        _samples = CreateTextBox("留空自动采样", backgroundSource, nativeTextBox)
        _sampleDuration = CreateTextBox("例如 20s", backgroundSource, nativeTextBox)
        RememberNormalBorders(_targetVmaf, _minCrf, _maxCrf, _samples, _sampleDuration)
        _vmafModel = CreateComboBox(
            "留空使用 ab-av1 自动模型",
            backgroundSource,
            nativeComboBox)
        Dim animationFps = If(nativeComboBox Is Nothing, 60, nativeComboBox.AnimationFPS)
        _thorough = CreateCheckBox(backgroundSource, animationFps)
        _scanModelsButton = CreateButton("扫描模型", backgroundSource, animationFps)
        Dim browseModelButton = CreateButton("本地 JSON", backgroundSource, animationFps)

        _modelStatus = CreateLabel("留空 = ab-av1 自动模型", ColorMuted, 8.5F)
        _validationStatus = CreateLabel(String.Empty, ColorDanger, 8.5F)
        _environmentStatus = CreateLabel(String.Empty, ColorMuted, 8.5F)
        _environmentStatus.Name = QualityValueStatusAdornment.StatusControlName

        Controls.Add(BuildLayout(backgroundSource, browseModelButton))
        _qualityValueStatus = New QualityValueStatusAdornment(nativeTextBox, _environmentStatus)

        AddHandler _targetVmaf.TextChanged, AddressOf SettingsChanged
        AddHandler _minCrf.TextChanged, AddressOf SettingsChanged
        AddHandler _maxCrf.TextChanged, AddressOf SettingsChanged
        AddHandler _samples.TextChanged, AddressOf SettingsChanged
        AddHandler _sampleDuration.TextChanged, AddressOf SettingsChanged
        AddHandler _thorough.CheckedChanged, AddressOf SettingsChanged
        AddHandler _vmafModel.TextChanged, AddressOf SettingsChanged
        AddHandler _scanModelsButton.Click, AddressOf ScanModels
        AddHandler browseModelButton.Click, AddressOf BrowseModel

        _refreshTimer = New System.Windows.Forms.Timer With {
            .Interval = RefreshDelayMilliseconds
        }
        AddHandler _refreshTimer.Tick, AddressOf RefreshTimerTick

        _stateRestoredHandler = Sub(sender, args) RestoreState()
        AddHandler _context.StateRestored, _stateRestoredHandler

        RestoreState()
        UpdateEnvironmentStatus()
        ResumeLayout(False)
    End Sub

    Public Sub SetActive(active As Boolean)
        If IsDisposed Then Return
        If InvokeRequired Then
            BeginInvoke(New Action(Of Boolean)(AddressOf SetActive), active)
            Return
        End If
        If Visible <> active Then Visible = active
        _qualityValueStatus.SetActive(active)
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing AndAlso Not _resourcesDisposed Then
            _resourcesDisposed = True
            RemoveHandler _context.StateRestored, _stateRestoredHandler
            _lifetimeCancellation.Cancel()
            _refreshTimer.Stop()
            RemoveHandler _refreshTimer.Tick, AddressOf RefreshTimerTick
            _refreshTimer.Dispose()
            _lifetimeCancellation.Dispose()
            _qualityValueStatus.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub

    Private Function BuildLayout(backgroundSource As Control,
                                 browseModelButton As ModernButton) As Control
        Dim layout As New TableLayoutPanel With {
            .AutoSize = False,
            .BackColor = Color.Transparent,
            .ColumnCount = 6,
            .Dock = DockStyle.Fill,
            .Margin = Padding.Empty,
            .Padding = Padding.Empty,
            .RowCount = 2
        }
        layout.SuspendLayout()
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 13.0F))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 13.0F))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 13.0F))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 20.0F))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 16.0F))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 25.0F))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, FieldLabelHeight + HostControlRowHeight))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, ModelRowHeight))

        layout.Controls.Add(CreateSearchField("目标 VMAF", _targetVmaf, backgroundSource), 0, 0)
        layout.Controls.Add(CreateSearchField("最小 CRF", _minCrf, backgroundSource), 1, 0)
        layout.Controls.Add(CreateSearchField("最大 CRF", _maxCrf, backgroundSource), 2, 0)
        layout.Controls.Add(CreateSearchField("采样数量", _samples, backgroundSource), 3, 0)
        layout.Controls.Add(CreateSearchField("单段时长", _sampleDuration, backgroundSource), 4, 0)
        layout.Controls.Add(CreateThoroughField(_thorough), 5, 0)

        Dim modelRow As New TableLayoutPanel With {
            .BackColor = Color.Transparent,
            .ColumnCount = 5,
            .Dock = DockStyle.Fill,
            .Margin = Padding.Empty,
            .Padding = Padding.Empty,
            .RowCount = 1
        }
        modelRow.SuspendLayout()
        modelRow.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        modelRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 86))
        modelRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        modelRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 102))
        modelRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 108))
        modelRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 220))

        Dim modelCaption = CreateFieldLabel("VMAF 模型", backgroundSource)
        modelCaption.Padding = New Padding(0, 10, 0, 0)
        modelCaption.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleLeft
        modelRow.Controls.Add(modelCaption, 0, 0)
        _vmafModel.Margin = New Padding(0, 10, 10, 0)
        modelRow.Controls.Add(_vmafModel, 1, 0)
        _scanModelsButton.Dock = DockStyle.Fill
        _scanModelsButton.Margin = New Padding(0, 10, 10, 0)
        modelRow.Controls.Add(_scanModelsButton, 2, 0)
        browseModelButton.Dock = DockStyle.Fill
        browseModelButton.Margin = New Padding(0, 10, 10, 0)
        modelRow.Controls.Add(browseModelButton, 3, 0)
        Dim statusHost As New Panel With {
            .BackColor = Color.Transparent,
            .Dock = DockStyle.Fill,
            .Margin = Padding.Empty,
            .Padding = Padding.Empty
        }
        _modelStatus.Dock = DockStyle.Fill
        _modelStatus.Padding = New Padding(0, 10, 0, 0)
        _modelStatus.TextAlign = ContentAlignment.MiddleLeft
        _validationStatus.Dock = DockStyle.Fill
        _validationStatus.Padding = New Padding(0, 10, 0, 0)
        _validationStatus.TextAlign = ContentAlignment.MiddleLeft
        _validationStatus.Visible = False
        statusHost.Controls.Add(_modelStatus)
        statusHost.Controls.Add(_validationStatus)
        modelRow.Controls.Add(statusHost, 4, 0)
        modelRow.ResumeLayout(False)

        layout.Controls.Add(modelRow, 0, 1)
        layout.SetColumnSpan(modelRow, 6)

        layout.ResumeLayout(False)
        Return layout
    End Function

    Private Sub RestoreState()
        If IsDisposed Then Return
        If InvokeRequired Then
            BeginInvoke(New Action(AddressOf RestoreState))
            Return
        End If

        _restoring = True
        SuspendLayout()
        Try
            Dim state = AbAv1PluginState.Deserialize(_context.StateJson)
            SetTextIfChanged(_targetVmaf, SearchSettings.FormatNumber(state.TargetVmaf))
            SetTextIfChanged(_minCrf, SearchSettings.FormatNumber(state.MinCrf))
            SetTextIfChanged(_maxCrf, SearchSettings.FormatNumber(state.MaxCrf))
            SetTextIfChanged(
                _samples,
                If(state.Samples.HasValue,
                   state.Samples.Value.ToString(CultureInfo.InvariantCulture),
                   String.Empty))
            SetTextIfChanged(_sampleDuration, state.SampleDuration)
            If _thorough.Checked <> state.Thorough Then _thorough.Checked = state.Thorough
            If Not String.Equals(_vmafModel.Text, state.VmafModel, StringComparison.Ordinal) Then
                _vmafModel.Text = state.VmafModel
            End If
            ClearValidationState()
            SetActive(state.Enabled)
        Finally
            ResumeLayout(False)
            _restoring = False
        End Try
    End Sub

    Private Sub SettingsChanged(sender As Object, e As EventArgs)
        If _restoring OrElse IsDisposed Then Return

        Dim targetVmaf As Double
        Dim minCrf As Double
        Dim maxCrf As Double
        Dim targetValid = TryParseNumber(_targetVmaf.Text, targetVmaf) AndAlso
                          targetVmaf > 0 AndAlso targetVmaf <= 100
        Dim minValid = TryParseNumber(_minCrf.Text, minCrf) AndAlso minCrf >= 0
        Dim maxValid = TryParseNumber(_maxCrf.Text, maxCrf) AndAlso maxCrf >= 0
        Dim rangeValid = minValid AndAlso maxValid AndAlso minCrf < maxCrf
        SetTextBoxError(_targetVmaf, Not targetValid)
        SetTextBoxError(_minCrf, Not minValid OrElse Not rangeValid)
        SetTextBoxError(_maxCrf, Not maxValid OrElse Not rangeValid)

        If Not targetValid Then
            ShowValidationError("目标 VMAF 必须大于 0 且不超过 100")
            Return
        End If
        If Not rangeValid Then
            ShowValidationError("CRF 范围无效：最小值必须小于最大值")
            Return
        End If

        Dim samplesValue As Integer? = Nothing
        Dim samplesText = If(_samples.Text, String.Empty).Trim()
        If samplesText <> String.Empty Then
            Dim parsedSamples As Integer
            If Not Integer.TryParse(samplesText, NumberStyles.Integer, CultureInfo.InvariantCulture, parsedSamples) OrElse
               parsedSamples <= 0 Then
                SetTextBoxError(_samples, True)
                ShowValidationError("采样数量必须留空或填写正整数")
                Return
            End If
            samplesValue = parsedSamples
        End If
        SetTextBoxError(_samples, False)

        Dim duration = If(_sampleDuration.Text, String.Empty).Trim()
        If duration = String.Empty Then
            SetTextBoxError(_sampleDuration, True)
            ShowValidationError("单段时长不能为空")
            Return
        End If
        SetTextBoxError(_sampleDuration, False)
        ClearValidationMessage()

        Dim state = AbAv1PluginState.Deserialize(_context.StateJson)
        state.TargetVmaf = targetVmaf
        state.MinCrf = minCrf
        state.MaxCrf = maxCrf
        state.Samples = samplesValue
        state.SampleDuration = duration
        state.Thorough = _thorough.Checked
        state.VmafModel = If(_vmafModel.Text, String.Empty).Trim()
        Dim stateJson = state.Serialize()
        _context.StateJson = stateJson
        If _stateChanged IsNot Nothing Then _stateChanged.Invoke(stateJson)
        QueueParameterRefresh()
    End Sub

    Private Async Sub ScanModels(sender As Object, e As EventArgs)
        _scanModelsButton.Enabled = False
        _modelStatus.ForeColor = ColorMuted
        _modelStatus.Text = "正在扫描当前 ffmpeg/libvmaf……"

        Try
            Dim result = Await VmafModelScanner.ScanAsync(_lifetimeCancellation.Token)
            If IsDisposed OrElse _lifetimeCancellation.IsCancellationRequested Then Return

            Dim missing As New List(Of String)()
            For Each model In result.Models
                If Not ComboBoxContains(_vmafModel, model) Then missing.Add(model)
            Next
            If missing.Count > 0 Then
                _restoring = True
                Try
                    _vmafModel.Items.AddRange(missing)
                Finally
                    _restoring = False
                End Try
            End If

            If result.ErrorMessage <> String.Empty Then
                _modelStatus.ForeColor = ColorDanger
                _modelStatus.Text = result.ErrorMessage
            Else
                _modelStatus.ForeColor = ColorSuccess
                _modelStatus.Text = $"发现 {result.Models.Count} 个模型 · {Path.GetFileName(result.FfmpegPath)}"
            End If
        Catch ex As OperationCanceledException
            ' 参数页关闭时正常取消。
        Catch ex As Exception
            If Not IsDisposed Then
                _modelStatus.ForeColor = ColorDanger
                _modelStatus.Text = "扫描失败：" & ex.Message
            End If
        Finally
            If Not IsDisposed Then _scanModelsButton.Enabled = True
        End Try
    End Sub

    Private Sub BrowseModel(sender As Object, e As EventArgs)
        Using dialog As New OpenFileDialog With {
            .AddExtension = True,
            .CheckFileExists = True,
            .Filter = "VMAF 模型 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            .Title = "选择本地 VMAF 模型"
        }
            If dialog.ShowDialog(FindForm()) = DialogResult.OK Then _vmafModel.Text = dialog.FileName
        End Using
    End Sub

    Private Sub UpdateEnvironmentStatus()
        If File.Exists(PluginEnvironment.AbAv1Path) Then
            _environmentStatus.ForeColor = ColorSuccess
            _environmentStatus.Text = "ab-av1 已就绪"
        Else
            _environmentStatus.ForeColor = ColorDanger
            _environmentStatus.Text = "缺少 ab-av1.exe · 请放到插件目录"
        End If
    End Sub

    Private Sub QueueParameterRefresh()
        _refreshTimer.Stop()
        _refreshTimer.Start()
    End Sub

    Private Sub RefreshTimerTick(sender As Object, e As EventArgs)
        _refreshTimer.Stop()
        If Not IsDisposed Then _context.RequestParameterRefresh()
    End Sub

    Private Sub ShowValidationError(message As String)
        If Not String.Equals(_validationStatus.Text, message, StringComparison.Ordinal) Then
            _validationStatus.ForeColor = ColorDanger
            _validationStatus.Text = message
        End If
        If Not _validationStatus.Visible Then _validationStatus.Visible = True
        _validationStatus.BringToFront()
    End Sub

    Private Sub ClearValidationMessage()
        If _validationStatus.Text <> String.Empty Then _validationStatus.Text = String.Empty
        If _validationStatus.Visible Then _validationStatus.Visible = False
    End Sub

    Private Sub ClearValidationState()
        SetTextBoxError(_targetVmaf, False)
        SetTextBoxError(_minCrf, False)
        SetTextBoxError(_maxCrf, False)
        SetTextBoxError(_samples, False)
        SetTextBoxError(_sampleDuration, False)
        ClearValidationMessage()
    End Sub

    Private Sub RememberNormalBorders(ParamArray editors As ModernTextBox())
        For Each editor In editors
            _normalTextBoxBorders(editor) = New TextBoxBorderStyle(editor)
        Next
    End Sub

    Private Sub SetTextBoxError(editor As ModernTextBox, hasError As Boolean)
        Dim normalStyle As TextBoxBorderStyle
        If Not _normalTextBoxBorders.TryGetValue(editor, normalStyle) Then
            normalStyle = New TextBoxBorderStyle(0, Color.Transparent, ColorControlPressed)
        End If

        Dim borderSize = If(hasError, Math.Max(normalStyle.BorderSize, 1), normalStyle.BorderSize)
        Dim borderColor = If(hasError, ColorDanger, normalStyle.BorderColor)
        Dim focusColor = If(hasError, ColorDanger, normalStyle.BorderColorFocus)
        If editor.BorderSize <> borderSize Then editor.BorderSize = borderSize
        If editor.BorderColor <> borderColor Then editor.BorderColor = borderColor
        If editor.BorderColorFocus <> focusColor Then editor.BorderColorFocus = focusColor
    End Sub

    Private Shared Sub SetTextIfChanged(editor As ModernTextBox, value As String)
        Dim normalized = If(value, String.Empty)
        If Not String.Equals(editor.Text, normalized, StringComparison.Ordinal) Then editor.Text = normalized
    End Sub

    Private Shared Function TryParseNumber(text As String, ByRef value As Double) As Boolean
        Dim candidate = If(text, String.Empty).Trim()
        Return Double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, value) OrElse
               Double.TryParse(candidate, NumberStyles.Float, CultureInfo.CurrentCulture, value)
    End Function

    Private Shared Function ComboBoxContains(comboBox As ModernComboBox, value As String) As Boolean
        For index = 0 To comboBox.Items.Count - 1
            If String.Equals(comboBox.Items(index), value, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

    Private Shared Function ResolveBackgroundSource(anchorControl As Control,
                                                     nativeTextBox As ModernTextBox,
                                                     nativeComboBox As ModernComboBox) As Control
        If nativeTextBox IsNot Nothing AndAlso nativeTextBox.BackgroundSource IsNot Nothing Then
            Return nativeTextBox.BackgroundSource
        End If
        If nativeComboBox IsNot Nothing AndAlso nativeComboBox.BackgroundSource IsNot Nothing Then
            Return nativeComboBox.BackgroundSource
        End If
        If anchorControl.Parent IsNot Nothing Then Return anchorControl.Parent
        Return anchorControl
    End Function

    Private Function CreateSearchField(caption As String,
                                       editor As Control,
                                       backgroundSource As Control) As Control
        Dim layout As New TableLayoutPanel With {
            .BackColor = Color.Transparent,
            .ColumnCount = 1,
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0, 0, 10, 0),
            .Padding = Padding.Empty,
            .RowCount = 2
        }
        layout.SuspendLayout()
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, FieldLabelHeight))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, HostControlRowHeight))
        Dim label = CreateFieldLabel(caption, backgroundSource)
        layout.Controls.Add(label, 0, 0)
        layout.Controls.Add(editor, 0, 1)
        layout.ResumeLayout(False)
        Return layout
    End Function

    Private Shared Function CreateThoroughField(editor As Control) As Control
        Dim layout As New TableLayoutPanel With {
            .BackColor = Color.Transparent,
            .ColumnCount = 1,
            .Dock = DockStyle.Fill,
            .Margin = Padding.Empty,
            .Padding = Padding.Empty,
            .RowCount = 2
        }
        layout.SuspendLayout()
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, FieldLabelHeight))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, HostControlRowHeight))
        editor.Margin = New Padding(10, 0, 0, 0)
        layout.Controls.Add(editor, 0, 1)
        layout.ResumeLayout(False)
        Return layout
    End Function

    Private Shared Function CreateTextBox(waterText As String,
                                          backgroundSource As Control,
                                          template As ModernTextBox) As ModernTextBox
        Dim editor As New ModernTextBox With {
            .AutoScaleMode = System.Windows.Forms.AutoScaleMode.None,
            .BackColor1 = ColorControl,
            .BackgroundSource = backgroundSource,
            .BorderColor = Color.Transparent,
            .BorderColorFocus = ColorControlPressed,
            .BorderRadius = 10,
            .BorderSize = 0,
            .CaretColor = ColorText,
            .Dock = DockStyle.Fill,
            .ForeColor = ColorText,
            .Margin = New Padding(0, 10, 0, 0),
            .MaximumSize = New Size(0, HostControlHeight),
            .MinimumSize = Size.Empty,
            .MultiLine = False,
            .Padding = New Padding(10, 0, 10, 0),
            .SelectionColor = ColorControl,
            .WaterText = waterText,
            .WaterTextForeColor = ColorMuted
        }

        If template IsNot Nothing Then
            editor.AnimationDuration = template.AnimationDuration
            editor.AnimationFPS = template.AnimationFPS
            editor.BackColor1 = template.BackColor1
            editor.BorderColor = template.BorderColor
            editor.BorderColorFocus = template.BorderColorFocus
            editor.BorderRadius = template.BorderRadius
            editor.BorderSize = template.BorderSize
            editor.CaretColor = template.CaretColor
            editor.CaretWidth = template.CaretWidth
            editor.Font = template.Font
            editor.ForeColor = template.ForeColor
            editor.SelectionColor = template.SelectionColor
            editor.SuperSamplingScale = template.SuperSamplingScale
            editor.WaterTextForeColor = template.WaterTextForeColor
        End If

        Return editor
    End Function

    Private Shared Function CreateComboBox(waterText As String,
                                            backgroundSource As Control,
                                            template As ModernComboBox) As ModernComboBox
        Dim comboBox As New ModernComboBox With {
            .ArrowColor = ColorMuted,
            .AutoScaleMode = System.Windows.Forms.AutoScaleMode.None,
            .BackColor1 = ColorControl,
            .BackColor2 = ColorControl,
            .BackgroundSource = backgroundSource,
            .BorderColor = Color.Transparent,
            .BorderColorFocus = ColorControlPressed,
            .BorderRadius = 10,
            .BorderSize = 0,
            .CaretColor = ColorText,
            .Dock = DockStyle.Fill,
            .DropDownBackdropBlurPasses = 2,
            .DropDownBackdropBlurRadius = 30,
            .DropDownBackdropMode = PopupBackdropMode.Auto,
            .DropDownHoverColor = Color.FromArgb(20, 220, 220, 220),
            .DropDownMode = ModernComboBox.DropDownDisplayMode.Overlay,
            .DropDownPadding = New Padding(10),
            .DropDownSelectedColor = ColorControl,
            .DropDownSelectedForeColor = Color.White,
            .Editable = True,
            .ForeColor = ColorText,
            .HoverBackColor1 = ColorControlHover,
            .HoverBackColor2 = ColorControlHover,
            .Margin = New Padding(0, 10, 0, 0),
            .MaxDropDownItems = 12,
            .MaximumSize = New Size(0, HostControlHeight),
            .MinimumSize = Size.Empty,
            .Padding = New Padding(10, 0, 10, 0),
            .PressedBackColor1 = ColorControlPressed,
            .PressedBackColor2 = ColorControlPressed,
            .SelectionColor = ColorControl,
            .ToolTipGap = -1,
            .ToolTipMaxWidth = 350,
            .ToolTipPadding = New Padding(15),
            .WaterText = waterText,
            .WaterTextForeColor = ColorMuted
        }

        If template IsNot Nothing Then
            comboBox.AnimationFPS = template.AnimationFPS
            comboBox.ArrowColor = template.ArrowColor
            comboBox.BackColor1 = template.BackColor1
            comboBox.BackColor2 = template.BackColor2
            comboBox.BackColorOrientation = template.BackColorOrientation
            comboBox.BorderColor = template.BorderColor
            comboBox.BorderColorFocus = template.BorderColorFocus
            comboBox.BorderRadius = template.BorderRadius
            comboBox.BorderSize = template.BorderSize
            comboBox.CaretColor = template.CaretColor
            comboBox.CaretWidth = template.CaretWidth
            comboBox.DropDownAnimationDuration = template.DropDownAnimationDuration
            comboBox.DropDownBackColor = template.DropDownBackColor
            comboBox.DropDownBackdropBlurPasses = template.DropDownBackdropBlurPasses
            comboBox.DropDownBackdropBlurRadius = template.DropDownBackdropBlurRadius
            comboBox.DropDownBackdropDownsampleFactor = template.DropDownBackdropDownsampleFactor
            comboBox.DropDownBackdropMode = template.DropDownBackdropMode
            comboBox.DropDownBackdropNoiseOpacity = template.DropDownBackdropNoiseOpacity
            comboBox.DropDownBackdropNoiseScale = template.DropDownBackdropNoiseScale
            comboBox.DropDownBackdropTintColor = template.DropDownBackdropTintColor
            comboBox.DropDownBorderColor = template.DropDownBorderColor
            comboBox.DropDownBorderSize = template.DropDownBorderSize
            comboBox.DropDownGap = template.DropDownGap
            comboBox.DropDownHighlightInsetLeft = template.DropDownHighlightInsetLeft
            comboBox.DropDownHighlightInsetRight = template.DropDownHighlightInsetRight
            comboBox.DropDownHighlightMatchPadding = template.DropDownHighlightMatchPadding
            comboBox.DropDownHoverAnimationDuration = template.DropDownHoverAnimationDuration
            comboBox.DropDownHoverColor = template.DropDownHoverColor
            comboBox.DropDownHoverRadius = template.DropDownHoverRadius
            comboBox.DropDownItemHeight = template.DropDownItemHeight
            comboBox.DropDownMode = template.DropDownMode
            comboBox.DropDownPadding = template.DropDownPadding
            comboBox.DropDownPressedColor = template.DropDownPressedColor
            comboBox.DropDownScrollBarColor = template.DropDownScrollBarColor
            comboBox.DropDownScrollBarHoverColor = template.DropDownScrollBarHoverColor
            comboBox.DropDownScrollBarTrackColor = template.DropDownScrollBarTrackColor
            comboBox.DropDownScrollBarWidth = template.DropDownScrollBarWidth
            comboBox.DropDownSelectedColor = template.DropDownSelectedColor
            comboBox.DropDownSelectedForeColor = template.DropDownSelectedForeColor
            comboBox.Font = template.Font
            comboBox.ForeColor = template.ForeColor
            comboBox.HoverArrowColor = template.HoverArrowColor
            comboBox.HoverBackColor1 = template.HoverBackColor1
            comboBox.HoverBackColor2 = template.HoverBackColor2
            comboBox.HoverBorderColor = template.HoverBorderColor
            comboBox.PressedArrowColor = template.PressedArrowColor
            comboBox.PressedBackColor1 = template.PressedBackColor1
            comboBox.PressedBackColor2 = template.PressedBackColor2
            comboBox.PressedBorderColor = template.PressedBorderColor
            comboBox.SelectionColor = template.SelectionColor
            comboBox.ShowSeparator = template.ShowSeparator
            comboBox.SuperSamplingScale = template.SuperSamplingScale
            comboBox.ToolTipBackColor = template.ToolTipBackColor
            comboBox.ToolTipBorderColor = template.ToolTipBorderColor
            comboBox.ToolTipForeColor = template.ToolTipForeColor
            comboBox.ToolTipGap = template.ToolTipGap
            comboBox.ToolTipMaxWidth = template.ToolTipMaxWidth
            comboBox.ToolTipPadding = template.ToolTipPadding
            comboBox.WaterTextForeColor = template.WaterTextForeColor
        End If

        Return comboBox
    End Function

    Private Shared Function CreateCheckBox(backgroundSource As Control,
                                           animationFps As Integer) As ModernCheckBox
        Return New ModernCheckBox With {
            .AnimationFPS = animationFps,
            .AutoScaleMode = System.Windows.Forms.AutoScaleMode.None,
            .BackgroundSource = backgroundSource,
            .Checked = False,
            .ClickAnywhere = True,
            .Dock = DockStyle.Fill,
            .ForeColor = ColorText,
            .Margin = New Padding(10, 0, 0, 0),
            .SubText = "搜索到更贴近目标值",
            .SubTextForeColor = ColorMuted,
            .Text = "彻底搜索"
        }
    End Function

    Private Shared Function CreateButton(text As String,
                                         backgroundSource As Control,
                                         animationFps As Integer) As ModernButton
        Return New ModernButton With {
            .AnimationFPS = animationFps,
            .AutoScaleMode = System.Windows.Forms.AutoScaleMode.None,
            .BackColor1 = ColorControl,
            .BackColor2 = ColorControl,
            .BackgroundSource = backgroundSource,
            .BorderColor = Color.Transparent,
            .BorderRadius = 10,
            .BorderSize = 0,
            .ForeColor = ColorText,
            .HoverBackColor1 = ColorControlHover,
            .HoverBackColor2 = ColorControlHover,
            .HoverBorderColor = Color.Transparent,
            .MaximumSize = New Size(0, HostControlHeight),
            .MinimumSize = Size.Empty,
            .PressedBackColor1 = ColorControlPressed,
            .PressedBackColor2 = ColorControlPressed,
            .PressedBorderColor = Color.Transparent,
            .Text = text
        }
    End Function

    Private Function CreateFieldLabel(text As String,
                                      backgroundSource As Control) As HtmlColorLabel
        Return New HtmlColorLabel With {
            .AutoScaleMode = System.Windows.Forms.AutoScaleMode.None,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .BackColor1 = Color.Transparent,
            .BackgroundSource = backgroundSource,
            .BorderSize = 0,
            .Dock = DockStyle.Fill,
            .Font = Font,
            .ForeColor = ColorMuted,
            .Margin = New Padding(2, 0, 2, 0),
            .Padding = Padding.Empty,
            .Text = text,
            .TextAlign = HtmlColorLabel.TextAlignEnum.BottomLeft
        }
    End Function

    Private Function CreateLabel(text As String,
                                 color As Color,
                                 size As Single) As Label
        Return New Label With {
            .AutoEllipsis = True,
            .BackColor = Color.Transparent,
            .Font = New Font(Font.FontFamily, size, FontStyle.Regular),
            .ForeColor = color,
            .Margin = Padding.Empty,
            .Text = text,
            .UseMnemonic = False
        }
    End Function

    Private Structure TextBoxBorderStyle

        Public Sub New(editor As ModernTextBox)
            Me.New(editor.BorderSize, editor.BorderColor, editor.BorderColorFocus)
        End Sub

        Public Sub New(borderSize As Integer,
                       borderColor As Color,
                       borderColorFocus As Color)
            Me.BorderSize = borderSize
            Me.BorderColor = borderColor
            Me.BorderColorFocus = borderColorFocus
        End Sub

        Public ReadOnly Property BorderSize As Integer
        Public ReadOnly Property BorderColor As Color
        Public ReadOnly Property BorderColorFocus As Color

    End Structure

End Class
