Imports System.Globalization
Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports System.Windows.Forms
Imports FFmpegFreeUI.Ext.AbAv1
Imports FFmpegFreeUI.Ext.PluginSdk
Imports LakeUI

Module Program

    Private _assertions As Integer

    <STAThread>
    Sub Main(args As String())
        If args.Length >= 2 AndAlso String.Equals(args(0), "--real-runner", StringComparison.OrdinalIgnoreCase) Then
            RunRealAbAv1RunnerAsync(args(1)).GetAwaiter().GetResult()
            Return
        End If
        If args.Contains("--visual-smoke", StringComparer.OrdinalIgnoreCase) Then
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)
            RenderVisibleQualitySettingsPanel()
            Return
        End If
        TestPluginDiscoveryContract()
        TestQualitySettingsPanelState()
        TestAutomaticVmafModelScan()
        TestPresetOverviewProviderState()
        TestQualitySettingsPanelStyleInheritance()
        TestPluginRegistrationAndPipelineAsync().GetAwaiter().GetResult()
        TestCommandLineTokenizer()
        TestPresetMappingAndCrfWriteBack()
        TestMultipleEncoderMappingAndExactPassThrough()
        TestAbAv1ReservedArgumentMapping()
        TestVmafModelArguments()
        TestVmafModelHelpParser()
        TestAbAv1RunnerProcessIntegrationAsync().GetAwaiter().GetResult()
        If args.Contains("--render", StringComparer.OrdinalIgnoreCase) Then RenderQualitySettingsPanel()
        Console.WriteLine($"PASS: {_assertions} assertions")
    End Sub

    Private Sub TestQualitySettingsPanelStyleInheritance()
        Dim context As New FakeUiContext With {
            .ProvideNativeStyleTemplates = True,
            .StateJson = New AbAv1PluginState With {
                .Enabled = True,
                .TargetVmaf = 95,
                .MinCrf = 5,
                .MaxCrf = 55,
                .SampleDuration = "20s"
            }.Serialize()
        }

        Using panel As New QualitySettingsPanel(context)
            Dim textBoxes = Descendants(panel).OfType(Of ModernTextBox)().ToArray()
            IsTrue(
                textBoxes.All(
                    Function(editor) editor.BackColor1 = context.NativeTextBox.BackColor1 AndAlso
                                     editor.BorderRadius = context.NativeTextBox.BorderRadius AndAlso
                                     editor.AnimationFPS = context.NativeTextBox.AnimationFPS AndAlso
                                     editor.BackColor = Color.Transparent AndAlso
                                     editor.BackgroundSource Is panel),
                "native text style and explicit transparent GPU backdrop inherited")

            Dim comboBox = Descendants(panel).OfType(Of ModernComboBox)().Single()
            Equal(context.NativeComboBox.BackColor1, comboBox.BackColor1, "native combo background")
            Equal(context.NativeComboBox.BorderRadius, comboBox.BorderRadius, "native combo radius")
            Equal(
                context.NativeComboBox.DropDownBackdropBlurRadius,
                comboBox.DropDownBackdropBlurRadius,
                "native combo popup backdrop")
            Equal(context.NativeComboBox.AnimationFPS, comboBox.AnimationFPS, "native combo animation FPS")
            Equal(Color.Transparent, comboBox.BackColor, "native combo transparent WinForms background")
            IsTrue(comboBox.BackgroundSource Is panel, "native combo uses the plugin GPU root backdrop")
            IsTrue(
                Descendants(panel).OfType(Of ModernButton)().All(
                    Function(button) button.AnimationFPS = context.NativeComboBox.AnimationFPS),
                "buttons follow host animation FPS")
            IsTrue(
                Descendants(panel).OfType(Of ModernCheckBox)().All(
                    Function(checkBox) checkBox.AnimationFPS = context.NativeComboBox.AnimationFPS),
                "checkbox follows host animation FPS")
            IsTrue(
                Descendants(panel).OfType(Of HtmlColorLabel)().All(
                    Function(label) label.Font.FontFamily.Name = context.AnchorControl.Font.FontFamily.Name AndAlso
                                    label.BackColor = Color.Transparent AndAlso
                                    (label.BackgroundSource Is panel OrElse
                                     (label.Name = QualityValueStatusAdornment.StatusControlName AndAlso
                                      label.BackgroundSource Is context.AnchorControl))),
                "LakeUI labels follow host font and transparent explicit backdrop")

            Equal("VMAF", context.NativeMetricComboBox.Text, "native metric selector configured")
            Equal("目标分数", context.NativeTextBox.WaterText, "native target score placeholder")
            Dim target = context.NativeTextBox
            target.Text = "invalid"
            Equal(Color.FromArgb(235, 93, 93), target.BorderColor, "validation border shown")
            target.Text = "95"
            Equal(context.NativeTextBox.BorderColor, target.BorderColor, "native border restored")
            Equal(context.NativeTextBox.BorderSize, target.BorderSize, "native border width restored")
        End Using
    End Sub

    Private Async Function RunRealAbAv1RunnerAsync(inputPath As String) As Task
        If Not File.Exists(inputPath) Then Throw New FileNotFoundException("Real test input not found", inputPath)

        Dim realPreset = JsonNode.Parse(CreateMinimalPresetJson()).AsObject()
        realPreset("视频参数_质量控制_进阶参数集") =
            "-svtav1-params keyint=8s:scd=1:tune=0:crf=31:input-depth=10"
        Dim profile = PresetProfile.LoadJson(realPreset.ToJsonString())
        Dim settings As New SearchSettings With {
            .Metric = QualityScoreMetric.Vmaf,
            .TargetScore = 80,
            .MinCrf = 20,
            .MaxCrf = 55,
            .Samples = 1,
            .SampleDuration = "1s"
        }
        Dim runner As New AbAv1Runner()
        Dim result = Await runner.SearchAsync(
            profile,
            Path.GetFullPath(inputPath),
            settings,
            New InlineProgress(Of SearchProgress)(
                Sub(value)
                    If value IsNot Nothing Then Console.WriteLine("PROGRESS: " & value.Message)
                End Sub),
            CancellationToken.None).ConfigureAwait(False)

        If Double.IsNaN(result.Crf) OrElse Double.IsNaN(result.Score) Then
            Throw New InvalidDataException("Real ab-av1 result contains NaN")
        End If
        Console.WriteLine(
            $"REAL_RESULT: CRF={result.Crf.ToString("0.###", CultureInfo.InvariantCulture)}; " &
            $"{SearchSettings.GetMetricDisplayName(result.Metric)}={result.Score.ToString("0.###", CultureInfo.InvariantCulture)}; " &
            $"BYTES={result.PredictedEncodeSize}; SECONDS={result.PredictedEncodeSeconds:0.###}")
    End Function

    Private Sub TestQualitySettingsPanelState()
        Dim context As New FakeUiContext With {
            .ProvideNativeStyleTemplates = True,
            .StateJson = New AbAv1PluginState With {
                .Enabled = True,
                .TargetVmaf = 97,
                .MinCrf = 8,
                .MaxCrf = 48,
                .Samples = 5,
                .SampleDuration = "12s",
                .Thorough = True,
                .VmafModel = "vmaf_v0.6.1"
            }.Serialize()
        }

        Using panel As New QualitySettingsPanel(context)
            IsTrue(panel.Visible, "settings visible for VMAF mode")
            Equal(114, panel.MinimumSize.Height, "compact settings panel height")
            panel.Width = 807
            panel.PerformLayout()
            IsTrue(
                panel.Controls.Cast(Of Control)().All(
                    Function(child) child.Left >= 0 AndAlso child.Right <= panel.ClientSize.Width),
                "flat settings controls stay inside host width")
            Equal(
                0,
                Descendants(panel).OfType(Of TableLayoutPanel)().Count(),
                "no transparent native table layout HWNDs remain above GPU controls")
            IsTrue(
                Not Descendants(panel).OfType(Of HtmlColorLabel)().Any(
                    Function(label) label.Text.Contains("AB-AV1 目标 VMAF", StringComparison.Ordinal)),
                "redundant settings heading removed")
            Dim searchFields = Descendants(panel).OfType(Of ModernTextBox)().ToArray()
            Equal(4, searchFields.Length, "target score moved to native field")
            IsTrue(searchFields.All(Function(input) input.Height = 32), "native 32 px text field height")
            Equal("VMAF", context.NativeMetricComboBox.Text, "VMAF metric restored into native selector")
            Equal("97", context.NativeTextBox.Text, "target VMAF restored into native value")
            IsTrue(Not context.NativeMetricComboBox.Editable, "metric selector is selection-only")
            Equal(2, context.NativeMetricComboBox.Items.Count, "metric selector only contains supported metrics")
            IsTrue(
                Descendants(panel).OfType(Of ModernTextBox)().Any(Function(input) input.Text = "12s"),
                "sample duration restored")
            IsTrue(
                Descendants(panel).OfType(Of ModernTextBox)().All(
                    Function(input) input.BorderRadius = context.NativeTextBox.BorderRadius AndAlso
                                    input.BackColor1 = context.NativeTextBox.BackColor1),
                "3FUI text field style inherited")
            Dim modelSelector = Descendants(panel).OfType(Of ModernComboBox)().Single()
            Equal(32, modelSelector.Height, "native 32 px model selector height")
            IsTrue(modelSelector.Parent Is panel, "model selector is a direct child of the flat GPU layout")
            Equal(ModernComboBox.DropDownDisplayMode.Overlay, modelSelector.DropDownMode, "3FUI combo overlay")
            Equal(
                context.NativeComboBox.DropDownBackdropBlurRadius,
                modelSelector.DropDownBackdropBlurRadius,
                "3FUI combo backdrop blur")
            Equal(context.NativeComboBox.DropDownPadding, modelSelector.DropDownPadding, "3FUI combo popup padding")
            Equal(2, Descendants(panel).OfType(Of ModernButton)().Count(), "LakeUI action buttons")
            IsTrue(
                Descendants(panel).OfType(Of ModernButton)().All(Function(button) button.Height = 32),
                "native 32 px button height")
            IsTrue(
                Descendants(panel).OfType(Of ModernButton)().All(
                    Function(button) button.Bottom <= button.Parent.ClientSize.Height),
                "model buttons are not vertically clipped")
            Dim thorough = Descendants(panel).OfType(Of ModernCheckBox)().Single()
            Equal(42, thorough.Height, "thorough option uses the aligned two-line control row")
            Equal("彻底搜索", thorough.Text, "thorough primary text")
            Equal("搜索到更贴近目标值", thorough.SubText, "thorough secondary text")
            Equal(New Padding(10, 0, 0, 0), thorough.Margin, "thorough reference spacing")
            Dim captions = New HashSet(Of String)(
                New String() {"最小 CRF", "最大 CRF", "采样数量", "单段时长"})
            Dim captionLabels = Descendants(panel).OfType(Of HtmlColorLabel)().Where(
                Function(label) captions.Contains(label.Text)).ToArray()
            Equal(4, captionLabels.Length, "four native-style field captions")
            IsTrue(
                captionLabels.All(
                    Function(label) label.TextAlign = HtmlColorLabel.TextAlignEnum.BottomLeft AndAlso
                                    label.ForeColor = Color.FromArgb(120, 255, 255, 255)),
                "field captions use native gray bottom-aligned style")
            Dim firstField = searchFields.Single(Function(editor) editor.WaterText = "最小 CRF")
            Dim firstCaption = captionLabels.Single(Function(label) label.Text = "最小 CRF")
            Equal(10, firstField.Top - firstCaption.Bottom, "native label-to-input spacing")
            Equal(
                firstField.Bottom,
                thorough.Bottom,
                "thorough bottom aligns with search input bottoms")

            ' Simulate a host/UI update that increases the native quality controls from
            ' 32 px to 48 px. Plugin controls must follow that actual height and must not
            ' be clamped by a stale, logical-pixel MaximumSize.
            context.NativeTextBox.Height = 48
            context.NativeMetricComboBox.Height = 48
            panel.PerformLayout()
            IsTrue(searchFields.All(Function(input) input.Height = 48), "text fields follow updated host height")
            Equal(48, modelSelector.Height, "model selector follows updated host height")
            IsTrue(
                Descendants(panel).OfType(Of ModernButton)().All(Function(button) button.Height = 48),
                "buttons follow updated host height")
            Equal(146, panel.MinimumSize.Height, "panel rows expand for updated host control height")
            context.NativeTextBox.Height = 32
            context.NativeMetricComboBox.Height = 32
            panel.PerformLayout()
            Equal(114, panel.MinimumSize.Height, "panel rows return to the original host height")

            context.AnchorControl.PerformLayout()
            Dim environmentStatus = context.AnchorControl.Controls.OfType(Of HtmlColorLabel)().Single(
                Function(label) label.Name = QualityValueStatusAdornment.StatusControlName)
            Equal(context.NativeTextBox.Right + 10, environmentStatus.Left, "status follows quality value")
            Equal(context.NativeTextBox.Top, environmentStatus.Top, "status aligns with quality value")
            Equal(context.NativeTextBox.Height, environmentStatus.Height, "status matches quality value height")
            Equal("ab-av1 已就绪", environmentStatus.Text, "compact ready status")
            IsTrue(environmentStatus.BackgroundSource Is context.AnchorControl, "external status uses host GPU backdrop")
            IsTrue(
                Not environmentStatus.Text.Contains("搜索在", StringComparison.Ordinal),
                "task-stage status text removed")

            context.NativeMetricComboBox.Text = "XPSNR"
            Equal(QualityScoreMetric.Xpsnr, AbAv1PluginState.Deserialize(context.StateJson).Metric, "XPSNR metric saved")
            Equal("40", context.NativeTextBox.Text, "XPSNR target has its own default")
            Equal(72, panel.MinimumSize.Height, "XPSNR hides and collapses VMAF model row")
            IsTrue(Not modelSelector.Visible, "VMAF model row hidden for XPSNR")
            context.NativeTextBox.Text = "-2.5"
            Equal(-2.5, AbAv1PluginState.Deserialize(context.StateJson).TargetXpsnr, "negative XPSNR target accepted")
            context.NativeMetricComboBox.Text = "VMAF"
            Equal("97", context.NativeTextBox.Text, "switching back restores VMAF target")
            Equal(114, panel.MinimumSize.Height, "VMAF model row restores full height")
            IsTrue(modelSelector.Visible, "VMAF model row shown again")

            Equal(42, QualitySettingsPanel.ScaleModelRowHeight(72), "100-percent DPI model row")
            Equal(53, QualitySettingsPanel.ScaleModelRowHeight(90), "125-percent DPI model row")
            Equal(63, QualitySettingsPanel.ScaleModelRowHeight(108), "150-percent DPI model row")
            Equal(84, QualitySettingsPanel.ScaleModelRowHeight(144), "200-percent DPI model row")

            ' The host's native CBR/TPE transition clears the shared quality textbox before
            ' RegisterChoice notifies the plugin that it is inactive. That transient empty value
            ' must not leave a stale error after the Ext choice restores its saved score.
            context.NativeTextBox.Text = String.Empty
            IsTrue(
                environmentStatus.Text.Contains("目标 VMAF", StringComparison.Ordinal),
                "transient native clear is observed while the Ext choice is still active")
            panel.SetActive(False)
            panel.SetActive(True)
            Equal("97", context.NativeTextBox.Text, "saved VMAF score restored after native round trip")
            Equal("ab-av1 已就绪", environmentStatus.Text, "transient score error cleared after native round trip")

            context.Restore(New AbAv1PluginState With {.Enabled = False}.Serialize())
            IsTrue(Not panel.Visible, "settings hidden for native mode")
            IsTrue(Not environmentStatus.Visible, "quality value status hidden for native mode")
            IsTrue(context.NativeMetricComboBox.Editable, "native selector editability restored")
            IsTrue(
                context.NativeMetricComboBox.Items.Cast(Of String)().SequenceEqual(New String() {"-crf", "-cq", "-qp"}),
                "native quality parameter items restored")
            Equal("质量值", context.NativeTextBox.WaterText, "native quality placeholder restored")
            panel.SetActive(True)
            IsTrue(panel.Visible, "choice callback shows settings")
            IsTrue(environmentStatus.Visible, "quality value status shown for VMAF mode")
        End Using

        Dim disposedPanel As New QualitySettingsPanel(New FakeUiContext())
        disposedPanel.Dispose()
        disposedPanel.Dispose()
        IsTrue(disposedPanel.IsDisposed, "settings panel disposal is idempotent")
    End Sub

    Private Sub TestAutomaticVmafModelScan()
        Dim scanCalls = 0
        Dim scanner =
            Function(cancellationToken As CancellationToken) As Task(Of VmafModelScanResult)
                cancellationToken.ThrowIfCancellationRequested()
                scanCalls += 1
                Return Task.FromResult(
                    New VmafModelScanResult(
                        New String() {"vmaf_v0.6.1", "vmaf_4k_v0.6.1"},
                        "ffmpeg.exe",
                        String.Empty))
            End Function
        Dim context As New FakeUiContext With {
            .ProvideNativeStyleTemplates = True,
            .StateJson = New AbAv1PluginState With {
                .Enabled = False,
                .Metric = QualityScoreMetric.Vmaf
            }.Serialize()
        }

        Using panel As New QualitySettingsPanel(context, scanModelsAsync:=scanner)
            Dim panelHandle = panel.Handle
            IsTrue(panel.IsHandleCreated, "automatic model scan test panel handle created")
            Equal(0, scanCalls, "inactive native quality mode does not scan VMAF models")

            panel.SetActive(True)
            WaitForCondition(
                Function() scanCalls = 1,
                "selecting target-score mode starts automatic VMAF model scan")
            Equal(1, scanCalls, "selecting target-score mode scans VMAF models automatically")
            Dim modelSelector = Descendants(panel).OfType(Of ModernComboBox)().Single()
            IsTrue(
                modelSelector.Items.Cast(Of String)().Contains("vmaf_v0.6.1"),
                "automatic scan populates standard VMAF model")
            IsTrue(
                modelSelector.Items.Cast(Of String)().Contains("vmaf_4k_v0.6.1"),
                "automatic scan populates 4K VMAF model")

            panel.SetActive(False)
            panel.SetActive(True)
            Application.DoEvents()
            Equal(1, scanCalls, "reselecting target-score mode reuses the populated model list")
            context.NativeMetricComboBox.Text = "XPSNR"
            context.NativeMetricComboBox.Text = "VMAF"
            Equal(1, scanCalls, "switching metrics does not repeat an existing model scan")
        End Using

        Dim xpsnrCalls = 0
        Dim xpsnrContext As New FakeUiContext With {
            .ProvideNativeStyleTemplates = True,
            .StateJson = New AbAv1PluginState With {
                .Enabled = False,
                .Metric = QualityScoreMetric.Xpsnr
            }.Serialize()
        }
        Using panel As New QualitySettingsPanel(
            xpsnrContext,
            scanModelsAsync:=
                Function(cancellationToken)
                    xpsnrCalls += 1
                    Return Task.FromResult(
                        New VmafModelScanResult(Array.Empty(Of String)(), "ffmpeg.exe", String.Empty))
                End Function)
            Dim panelHandle = panel.Handle
            panel.SetActive(True)
            Application.DoEvents()
            Equal(0, xpsnrCalls, "XPSNR target-score mode does not scan VMAF models")
        End Using
    End Sub

    Private Sub WaitForCondition(condition As Func(Of Boolean), description As String)
        Dim timeout = Stopwatch.StartNew()
        Do
            If condition.Invoke() Then Return
            Application.DoEvents()
            Thread.Sleep(10)
        Loop While timeout.Elapsed < TimeSpan.FromSeconds(2)

        Throw New InvalidOperationException("Timed out: " & description)
    End Sub

    Private Sub TestPresetOverviewProviderState()
        Dim migrated = AbAv1PluginState.Deserialize("{""Version"":1,""TargetVmaf"":92.5}")
        Equal(2, migrated.Version, "legacy state migrates to score schema v2")
        Equal(QualityScoreMetric.Vmaf, migrated.Metric, "legacy state remains VMAF")
        Equal(92.5, migrated.TargetScore, "legacy target VMAF is retained")

        Dim storedState As New AbAv1PluginState With {
            .Enabled = False,
            .TargetVmaf = 96.5,
            .MinCrf = 10,
            .MaxCrf = 42,
            .Samples = 4,
            .SampleDuration = "15s",
            .Thorough = True,
            .VmafModel = "vmaf_v0.6.1"
        }
        Dim host As New FakeHost()
        Dim plugin As New AbAv1Plugin()
        plugin.Initialize(host)
        Dim provider = host.PresetOverviewRegistry.RowProviders.Single()
        Dim context As New ExtPluginPresetOverviewContext(
            AbAv1Plugin.PluginId,
            CreateMinimalPresetJson(),
            storedState.Serialize())

        IsTrue(AbAv1PluginState.HasStoredState(context.PluginStateJson), "stored plugin state detected")
        IsTrue(Not AbAv1PluginState.HasStoredState("{}"), "empty plugin state detected")
        provider.Callback.Invoke(context)

        Equal(7, context.Rows.Count, "seven AB-AV1 preset overview rows")
        Equal("AB-AV1 目标 VMAF：96.5", context.Rows(0).Text, "overview target VMAF")
        Equal("AB-AV1 最小 CRF：10", context.Rows(1).Text, "overview minimum CRF")
        Equal("AB-AV1 最大 CRF：42", context.Rows(2).Text, "overview maximum CRF")
        Equal("AB-AV1 采样数量：4", context.Rows(3).Text, "overview samples")
        Equal("AB-AV1 单段时长：15s", context.Rows(4).Text, "overview duration")
        Equal("AB-AV1 彻底搜索：是", context.Rows(5).Text, "overview thorough mode")
        Equal("AB-AV1 VMAF 模型：vmaf_v0.6.1", context.Rows(6).Text, "overview model")
        IsTrue(context.Rows.All(Function(row) row.Level = ExtPluginPresetOverviewRowLevel.Normal), "overview rows use native normal style")
        IsTrue(context.Rows.Select(Function(row) row.Order).SequenceEqual(Enumerable.Range(0, 7)), "overview row order")
        IsTrue(
            context.Rows.All(
                Function(row) Not row.Text.Contains("已启用", StringComparison.Ordinal) AndAlso
                              Not row.Text.Contains("未启用", StringComparison.Ordinal)),
            "overview omits enabled state")

        Dim selectedPresetState As New AbAv1PluginState With {
            .Enabled = True,
            .TargetVmaf = 88,
            .MinCrf = 2,
            .MaxCrf = 40,
            .SampleDuration = "8s"
        }
        Dim selectedPresetContext As New ExtPluginPresetOverviewContext(
            AbAv1Plugin.PluginId,
            "{""预设文件版本"":6}",
            selectedPresetState.Serialize())
        provider.Callback.Invoke(selectedPresetContext)
        Equal("AB-AV1 目标 VMAF：88", selectedPresetContext.Rows(0).Text, "selected preset state is used independently")

        Dim xpsnrState As New AbAv1PluginState With {
            .Enabled = True,
            .Metric = QualityScoreMetric.Xpsnr,
            .TargetXpsnr = 38.75,
            .MinCrf = 4,
            .MaxCrf = 60,
            .SampleDuration = "10s",
            .VmafModel = "missing-model.json"
        }
        Dim xpsnrContext As New ExtPluginPresetOverviewContext(
            AbAv1Plugin.PluginId,
            "{""预设文件版本"":6}",
            xpsnrState.Serialize())
        provider.Callback.Invoke(xpsnrContext)
        Equal(6, xpsnrContext.Rows.Count, "XPSNR overview omits VMAF model row")
        Equal("AB-AV1 目标 XPSNR：38.75", xpsnrContext.Rows(0).Text, "overview target XPSNR")
        IsTrue(
            xpsnrContext.Rows.All(Function(row) Not row.Text.Contains("VMAF 模型", StringComparison.Ordinal)),
            "XPSNR overview contains no VMAF-only setting")

        Dim emptyContext As New ExtPluginPresetOverviewContext(
            AbAv1Plugin.PluginId,
            "{""预设文件版本"":6}",
            "{}")
        provider.Callback.Invoke(emptyContext)
        Equal(0, emptyContext.Rows.Count, "preset without AB-AV1 state has no plugin rows")
    End Sub

    Private Sub RenderQualitySettingsPanel()
        Dim context As New FakeUiContext With {
            .StateJson = New AbAv1PluginState With {
                .Enabled = True,
                .TargetVmaf = 95,
                .MinCrf = 5,
                .MaxCrf = 55,
                .SampleDuration = "20s"
            }.Serialize()
        }
        Using panel As New QualitySettingsPanel(context)
            panel.AutoSize = False
            panel.Dock = DockStyle.None
            panel.Size = New Size(1120, panel.MinimumSize.Height)
            panel.CreateControl()
            For Each child In Descendants(panel)
                child.CreateControl()
            Next
            panel.PerformLayout()

            Using bitmap As New Bitmap(panel.Width, panel.Height)
                panel.DrawToBitmap(bitmap, New Rectangle(Point.Empty, bitmap.Size))
                Dim outputPath = Path.Combine(AppContext.BaseDirectory, "quality-panel.png")
                bitmap.Save(outputPath, Imaging.ImageFormat.Png)
                Console.WriteLine("RENDER: " & outputPath)
            End Using
        End Using
    End Sub

    Private Sub RenderVisibleQualitySettingsPanel()
        Dim context As New FakeUiContext With {
            .StateJson = New AbAv1PluginState With {
                .Enabled = True,
                .TargetVmaf = 95,
                .MinCrf = 5,
                .MaxCrf = 55,
                .Samples = 4,
                .SampleDuration = "20s",
                .Thorough = True,
                .VmafModel = "vmaf_v0.6.1"
            }.Serialize()
        }
        context.AnchorControl.BackColor = Color.FromArgb(24, 24, 24)
        context.AnchorControl.Font = New Font("Microsoft YaHei UI", 10.0F)

        Using form As New Form With {
            .BackColor = Color.FromArgb(24, 24, 24),
            .ClientSize = New Size(1160, 360),
            .FormBorderStyle = FormBorderStyle.None,
            .Location = New Point(40, 40),
            .ShowInTaskbar = False,
            .StartPosition = FormStartPosition.Manual,
            .TopMost = True
        }
            context.AnchorControl.Dock = DockStyle.Fill
            context.AnchorControl.Padding = New Padding(20)
            form.Controls.Add(context.AnchorControl)

            Using panel As New QualitySettingsPanel(context)
                panel.Dock = DockStyle.Top
                context.AnchorControl.Controls.Add(panel)
                form.Show()
                form.Activate()
                form.BringToFront()
                For index = 1 To 10
                    Application.DoEvents()
                    Thread.Sleep(50)
                Next

                Using bitmap As New Bitmap(panel.ClientSize.Width, panel.ClientSize.Height)
                    panel.DrawToBitmap(bitmap, New Rectangle(Point.Empty, bitmap.Size))
                    Dim outputPath = Path.Combine(AppContext.BaseDirectory, "quality-panel-visible.png")
                    bitmap.Save(outputPath, Imaging.ImageFormat.Png)
                    Dim target = context.NativeTextBox
                    Dim model = Descendants(panel).OfType(Of ModernComboBox)().Single()
                    Dim overflowingControl = Descendants(panel).FirstOrDefault(
                        Function(control) control.Parent IsNot Nothing AndAlso
                                          control.Right > control.Parent.ClientSize.Width)
                    If overflowingControl IsNot Nothing Then
                        Throw New InvalidOperationException(
                            $"Visual layout overflow: {overflowingControl.GetType().Name} {overflowingControl.Bounds}")
                    End If
                    Console.WriteLine(
                        $"VISUAL_METRICS: panel={panel.Width}x{panel.Height}; " &
                        $"children={panel.Controls.Count}; " &
                        $"target={target.Width}x{target.Height}; model={model.Width}x{model.Height}")
                    Console.WriteLine("VISUAL_RENDER: " & outputPath)
                End Using
                form.Close()
            End Using
        End Using
    End Sub

    Private Iterator Function Descendants(root As Control) As IEnumerable(Of Control)
        For Each child As Control In root.Controls
            Yield child
            For Each descendant In Descendants(child)
                Yield descendant
            Next
        Next
    End Function

    Private Sub TestPluginDiscoveryContract()
        Dim assembly = GetType(AbAv1Runner).Assembly
        Dim pluginTypes = assembly.GetTypes().Where(
            Function(candidate) candidate.IsClass AndAlso
                Not candidate.IsAbstract AndAlso
                GetType(IExtFFmpegFreeUIPlugin).IsAssignableFrom(candidate)).ToArray()
        Equal(1, pluginTypes.Length, "single Ext plugin entry")

        Dim plugin = DirectCast(Activator.CreateInstance(pluginTypes(0)), IExtFFmpegFreeUIPlugin)
        Equal("ffmpegfreeui.ext.ab-av1", plugin.Id, "stable plugin ID")
        Equal("AB-AV1", plugin.DisplayName, "plugin display name")
        IsTrue(
            Path.GetFileName(assembly.Location).EndsWith(".3fui.dll", StringComparison.OrdinalIgnoreCase),
            "plugin assembly suffix")
    End Sub

    Private Async Function TestPluginRegistrationAndPipelineAsync() As Task
        Dim host As New FakeHost()
        Dim plugin As New AbAv1Plugin()
        plugin.Initialize(host)

        Equal(1, host.UiRegistry.ChoiceExtensions.Count, "quality choice registered")
        Equal(1, host.UiRegistry.Extensions.Count, "settings panel registered")
        Equal(1, host.PresetOverviewRegistry.RowProviders.Count, "preset overview provider registered")
        Equal(2, host.PipelineRegistry.Handlers.Count, "capture sanitizer and search pipeline registered")
        Equal(1, host.CommandRegistry.StepProviders.Count, "command preview provider registered")

        Dim settingsExtension = host.UiRegistry.Extensions.Single(
            Function(extension) extension.Id = "target-vmaf-settings")
        Equal(
            ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityAfterGlobal,
            settingsExtension.AnchorId,
            "settings panel anchor")
        Equal(
            ExtFFmpegFreeUIPluginResources.ParametersVideoQualityFields,
            settingsExtension.ResourceId,
            "settings panel declares native quality-field resource")
        Equal(
            ExtPluginResourceAccess.OrderedTransform,
            settingsExtension.ResourceAccess,
            "native quality fields use composable ordered transform")
        Equal(
            "target-vmaf-overview",
            host.PresetOverviewRegistry.RowProviders.Single().Id,
            "native preset overview provider ID")

        Dim choice = host.UiRegistry.ChoiceExtensions.Single()
        Equal(
            ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityMode,
            choice.AnchorId,
            "quality choice anchor")
        Equal(
            ExtFFmpegFreeUIUiChoices.VideoQualityCrf,
            choice.NativeFallbackChoiceId,
            "quality choice native fallback")
        Equal("使用目标分数（ab-av1）", choice.DisplayText, "generic target-score choice text")
        Equal(0, choice.ValueOverrides.Count, "metric and score are managed dynamically")
        Equal(0, choice.DisabledAnchors.Count, "metric and score remain editable")

        Dim choiceContext As New FakeChoiceContext With {
            .StateJson = New AbAv1PluginState With {.Enabled = False}.Serialize()
        }
        choice.SelectionChanged.Invoke(choiceContext, True)
        IsTrue(AbAv1PluginState.Deserialize(choiceContext.StateJson).Enabled, "choice state persisted")
        IsTrue(choice.RestoreSelection.Invoke(choiceContext), "choice restored from preset state")
        Equal(0, choiceContext.RefreshCount, "choice callback avoids duplicate host refresh")

        Dim settingsContext As New FakeUiContext With {
            .ProvideNativeStyleTemplates = True,
            .StateJson = choiceContext.StateJson
        }
        Dim settingsControl = settingsExtension.CreateControl.Invoke(settingsContext)
        IsTrue(
            settingsContext.AnchorControl.Controls.OfType(Of HtmlColorLabel)().Any(
                Function(label) label.Name = QualityValueStatusAdornment.StatusControlName),
            "ready status decorates native quality row")
        Dim targetEditor = settingsContext.NativeTextBox
        targetEditor.Text = "94"
        Equal(
            94.0,
            AbAv1PluginState.Deserialize(settingsContext.StateJson).TargetVmaf,
            "native target score updates state used by overview refresh")
        settingsExtension.Cleanup.Invoke(settingsContext)
        IsTrue(settingsControl.IsDisposed, "settings panel cleanup")
        IsTrue(settingsContext.NativeMetricComboBox.Editable, "cleanup restores native metric selector")

        Dim commandProvider = host.CommandRegistry.StepProviders.Single()
        Dim previewState As New AbAv1PluginState With {
            .Enabled = True,
            .TargetVmaf = 96.5,
            .MinCrf = 10,
            .MaxCrf = 42,
            .Samples = 4,
            .SampleDuration = "15s",
            .Thorough = True,
            .VmafModel = "vmaf_v0.6.1"
        }
        Dim previewContext As New ExtPluginCommandContext With {
            .IsPreview = True,
            .PluginId = AbAv1Plugin.PluginId,
            .PluginStateJson = previewState.Serialize(),
            .PresetJson = CreateMinimalPresetJson()
        }
        commandProvider.Callback.Invoke(previewContext)
        Equal(1, previewContext.Steps.Count, "enabled VMAF mode contributes preview command")
        Dim previewStep = previewContext.Steps.Single()
        Equal(ExtPluginCommandStepPlacement.BeforeNative, previewStep.Placement, "ab-av1 command precedes ffmpeg")
        Equal(PluginEnvironment.AbAv1Path, previewStep.ProcessFileName, "ab-av1 command executable")
        Equal(PluginEnvironment.PluginDirectory, previewStep.WorkingDirectory, "ab-av1 command working directory")
        IsTrue(
            previewStep.Arguments.StartsWith("crf-search --input ""<输入文件>""", StringComparison.Ordinal),
            "native command template contains ab-av1 input placeholder")
        IsTrue(previewStep.Arguments.Contains("--min-vmaf 96.5", StringComparison.Ordinal), "command target VMAF")
        IsTrue(previewStep.Arguments.Contains("--min-crf 10 --max-crf 42", StringComparison.Ordinal), "command CRF range")
        IsTrue(previewStep.Arguments.Contains("--samples 4", StringComparison.Ordinal), "command samples")
        IsTrue(previewStep.Arguments.Contains("--thorough", StringComparison.Ordinal), "command thorough flag")
        IsTrue(
            previewStep.Arguments.Contains("--vmaf model=version=vmaf_v0.6.1", StringComparison.Ordinal),
            "command VMAF model")
        IsTrue(
            Not previewStep.Arguments.Contains("--stdout-format", StringComparison.Ordinal),
            "preview command stays human-readable")

        Dim xpsnrPreviewState As New AbAv1PluginState With {
            .Enabled = True,
            .Metric = QualityScoreMetric.Xpsnr,
            .TargetXpsnr = 38.5,
            .MinCrf = 10,
            .MaxCrf = 42,
            .SampleDuration = "15s",
            .VmafModel = "missing-model.json"
        }
        Dim xpsnrPreviewContext As New ExtPluginCommandContext With {
            .IsPreview = True,
            .PluginId = AbAv1Plugin.PluginId,
            .PluginStateJson = xpsnrPreviewState.Serialize(),
            .PresetJson = CreateMinimalPresetJson()
        }
        commandProvider.Callback.Invoke(xpsnrPreviewContext)
        Equal(1, xpsnrPreviewContext.Steps.Count, "enabled XPSNR mode contributes preview command")
        Dim xpsnrStep = xpsnrPreviewContext.Steps.Single()
        Equal("AB-AV1 XPSNR CRF 搜索", xpsnrStep.DisplayName, "XPSNR preview label")
        IsTrue(xpsnrStep.Arguments.Contains("--min-xpsnr 38.5", StringComparison.Ordinal), "command target XPSNR")
        IsTrue(Not xpsnrStep.Arguments.Contains("--min-vmaf", StringComparison.Ordinal), "XPSNR command omits VMAF target")
        IsTrue(Not xpsnrStep.Arguments.Contains("--vmaf", StringComparison.Ordinal), "XPSNR command omits VMAF model")

        Dim incompletePreviewContext As New ExtPluginCommandContext With {
            .IsPreview = True,
            .PluginId = AbAv1Plugin.PluginId,
            .PluginStateJson = previewState.Serialize(),
            .PresetJson = New JsonObject From {
                {"预设文件版本", 6}
            }.ToJsonString()
        }
        commandProvider.Callback.Invoke(incompletePreviewContext)
        Equal(1, incompletePreviewContext.Steps.Count, "incomplete preset still contributes preview command")
        Dim incompleteStep = incompletePreviewContext.Steps.Single()
        IsTrue(
            Not incompleteStep.Arguments.Contains("--encoder", StringComparison.Ordinal),
            "empty encoder is not completed in preview")
        IsTrue(
            incompleteStep.Arguments.Contains("--min-vmaf 96.5", StringComparison.Ordinal),
            "incomplete preset retains AB-AV1 search settings")

        Dim incompatiblePreset = JsonNode.Parse(CreateMinimalPresetJson()).AsObject()
        incompatiblePreset("视频参数_分辨率") = "1920x1080"
        Dim fallbackPreviewContext As New ExtPluginCommandContext With {
            .IsPreview = True,
            .PluginId = AbAv1Plugin.PluginId,
            .PluginStateJson = previewState.Serialize(),
            .PresetJson = incompatiblePreset.ToJsonString()
        }
        commandProvider.Callback.Invoke(fallbackPreviewContext)
        Equal(1, fallbackPreviewContext.Steps.Count, "processing-chain mismatch still has an exact preview")
        Dim fallbackStep = fallbackPreviewContext.Steps.Single()
        IsTrue(
            fallbackStep.Arguments.Contains("--encoder libsvtav1", StringComparison.Ordinal),
            "preview retains selected encoder")
        IsTrue(
            fallbackStep.Arguments.Contains("--preset 6", StringComparison.Ordinal) AndAlso
            fallbackStep.Arguments.Contains("--pix-format yuv420p10le", StringComparison.Ordinal),
            "preview retains selected preset mappings")
        IsTrue(
            Not host.LogMessages.Any(
                Function(message) message.Contains("基础预览模板", StringComparison.Ordinal)),
            "preview does not substitute a fallback command")

        Dim actualContext As New ExtPluginCommandContext With {
            .IsPreview = False,
            .PluginStateJson = previewState.Serialize(),
            .PresetJson = CreateMinimalPresetJson()
        }
        commandProvider.Callback.Invoke(actualContext)
        Equal(0, actualContext.Steps.Count, "pipeline remains the sole actual search executor")

        previewState.Enabled = False
        Dim nativeModeContext As New ExtPluginCommandContext With {
            .IsPreview = True,
            .PluginStateJson = previewState.Serialize(),
            .PresetJson = CreateMinimalPresetJson()
        }
        commandProvider.Callback.Invoke(nativeModeContext)
        Equal(0, nativeModeContext.Steps.Count, "native quality mode omits ab-av1 command")

        Dim captureHandler = host.PipelineRegistry.Handlers.Single(
            Function(item) item.StageId = ExtFFmpegFreeUIPipelineStages.PresetAfterCapture)
        Equal(
            ExtFFmpegFreeUIPluginResources.PresetDocument,
            captureHandler.ResourceId,
            "capture sanitizer declares preset resource")
        Dim capturedPreset = JsonNode.Parse(CreateMinimalPresetJson()).AsObject()
        capturedPreset("视频参数_质量控制_参数名") = "XPSNR"
        capturedPreset("视频参数_质量控制_值") = "38"
        Dim captureContext As New ExtPluginPipelineContext With {
            .StageId = ExtFFmpegFreeUIPipelineStages.PresetAfterCapture,
            .PresetJson = capturedPreset.ToJsonString()
        }
        Await captureHandler.Callback(captureContext, CancellationToken.None).AsTask().ConfigureAwait(False)
        Dim sanitizedPreset = JsonNode.Parse(captureContext.PresetJson).AsObject()
        Equal(String.Empty, sanitizedPreset("视频参数_质量控制_参数名").GetValue(Of String)(), "captured metric is not emitted as FFmpeg option")
        Equal(String.Empty, sanitizedPreset("视频参数_质量控制_值").GetValue(Of String)(), "captured score is not emitted as FFmpeg value")
        IsTrue(sanitizedPreset("插件扩展数据") IsNot Nothing, "capture sanitizer retains plugin state")

        Dim nativeCapturedPreset = JsonNode.Parse(CreateMinimalPresetJson()).AsObject()
        nativeCapturedPreset("视频参数_质量控制_参数名") = "crf"
        nativeCapturedPreset("视频参数_质量控制_值") = "23"
        DirectCast(nativeCapturedPreset("插件扩展数据"), JsonObject)(AbAv1Plugin.PluginId) =
            JsonValue.Create(New AbAv1PluginState With {.Enabled = False}.Serialize())
        Dim nativeCaptureContext As New ExtPluginPipelineContext With {
            .StageId = ExtFFmpegFreeUIPipelineStages.PresetAfterCapture,
            .PresetJson = nativeCapturedPreset.ToJsonString()
        }
        Await captureHandler.Callback(nativeCaptureContext, CancellationToken.None).AsTask().ConfigureAwait(False)
        Dim unchangedNativePreset = JsonNode.Parse(nativeCaptureContext.PresetJson).AsObject()
        Equal("crf", unchangedNativePreset("视频参数_质量控制_参数名").GetValue(Of String)(), "native mode parameter remains untouched")
        Equal("23", unchangedNativePreset("视频参数_质量控制_值").GetValue(Of String)(), "native mode value remains untouched")

        Dim handler = host.PipelineRegistry.Handlers.Single(
            Function(item) item.StageId = ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare)
        Equal(
            ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,
            handler.StageId,
            "search pipeline stage")

        Dim inputPath = Path.Combine(Path.GetTempPath(), "ffmpegfreeui-ext-ab-av1-pipeline-input.bin")
        File.WriteAllBytes(inputPath, New Byte() {0})
        Try
            Dim progressMessages As New ConcurrentQueue(Of ExtPluginPipelineProgress)()
            Dim taskResults As New ConcurrentQueue(Of ExtPluginTaskResult)()
            Dim context As New ExtPluginPipelineContext(
                Sub(value) progressMessages.Enqueue(value),
                Sub(value) taskResults.Enqueue(value)) With {
                .StageId = ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,
                .PresetJson = CreateMinimalPresetJson(),
                .InputPath = inputPath,
                .TaskId = "pipeline-test"
            }

            Await handler.Callback(context, CancellationToken.None).AsTask().ConfigureAwait(False)

            Dim updated = JsonNode.Parse(context.PresetJson).AsObject()
            Equal(
                "28",
                updated("视频参数_质量控制_值").GetValue(Of String)(),
                "pipeline writes searched CRF")
            IsTrue(
                taskResults.Any(Function(item) item.Key = "search.crf" AndAlso item.Value = "28"),
                "pipeline reports CRF result")
            IsTrue(
                taskResults.Any(Function(item) item.Key = "search.vmaf" AndAlso item.Value = "95.2"),
                "pipeline reports VMAF result")
            IsTrue(
                progressMessages.Any(Function(item) item.Fraction.HasValue AndAlso item.Fraction.Value = 1),
                "pipeline reports completion")
            IsTrue(updated("插件扩展数据") IsNot Nothing, "pipeline retains extension state")

            Dim xpsnrPreset = JsonNode.Parse(CreateMinimalPresetJson()).AsObject()
            DirectCast(xpsnrPreset("插件扩展数据"), JsonObject)(AbAv1Plugin.PluginId) =
                JsonValue.Create(New AbAv1PluginState With {
                    .Enabled = True,
                    .Metric = QualityScoreMetric.Xpsnr,
                    .TargetXpsnr = 38.5
                }.Serialize())
            Dim xpsnrResults As New ConcurrentQueue(Of ExtPluginTaskResult)()
            Dim xpsnrProgress As New ConcurrentQueue(Of ExtPluginPipelineProgress)()
            Dim xpsnrPipelineContext As New ExtPluginPipelineContext(
                Sub(value) xpsnrProgress.Enqueue(value),
                Sub(value) xpsnrResults.Enqueue(value)) With {
                .StageId = ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,
                .PresetJson = xpsnrPreset.ToJsonString(),
                .InputPath = inputPath,
                .TaskId = "pipeline-xpsnr-test"
            }
            Await handler.Callback(xpsnrPipelineContext, CancellationToken.None).AsTask().ConfigureAwait(False)
            IsTrue(
                xpsnrResults.Any(Function(item) item.Key = "search.xpsnr" AndAlso item.Value = "38.2"),
                "pipeline reports XPSNR result")
            IsTrue(
                xpsnrProgress.Any(Function(item) item.Message.Contains("XPSNR", StringComparison.Ordinal)),
                "pipeline progress names selected metric")
        Finally
            Try
                If File.Exists(inputPath) Then File.Delete(inputPath)
            Catch
            End Try
        End Try
    End Function

    Private Sub TestCommandLineTokenizer()
        Dim tokens = CommandLineTokenizer.Tokenize(
            "-svtav1-params ""tune=0:keyint=240"" -metadata ""title=hello world""")
        Equal(4, tokens.Count, "token count")
        Equal("-svtav1-params", tokens(0), "first option")
        Equal("tune=0:keyint=240", tokens(1), "quoted SVT value")
        Equal("title=hello world", tokens(3), "quoted metadata value")
    End Sub

    Private Sub TestPresetMappingAndCrfWriteBack()
        Dim preset As New JsonObject()
        preset("预设文件版本") = 6
        preset("视频参数_编码器_具体编码") = "libsvtav1"
        preset("视频参数_编码器_编码预设") = "6"
        preset("视频参数_色彩管理_像素格式") = "yuv420p10le"
        preset("输出容器") = "mkv"
        preset("视频参数_质量控制_进阶参数集") =
            "-svtav1-params keyint=8s:scd=1:tune=0:crf=31:input-depth=10:preset=4"
        preset("自定义参数_视频参数") = "-tile-columns 2"
        preset("插件扩展数据") = New JsonObject From {
            {"ffmpegfreeui.ext.ab-av1", JsonValue.Create("{""Enabled"":true}")}
        }
        Dim presetJson = preset.ToJsonString()

        Dim profile = PresetProfile.LoadJson(presetJson)
        Dim inputPath = Path.Combine(Path.GetTempPath(), "ffmpegfreeui-ext-ab-av1-test-input.bin")
        File.WriteAllBytes(inputPath, New Byte() {0})
        Try
            Dim settings As New SearchSettings With {
                .TargetScore = 96.5,
                .MinCrf = 10,
                .MaxCrf = 50,
                .Samples = 4,
                .SampleDuration = "15s",
                .Thorough = True,
                .VmafModel = "vmaf_v0.6.1"
            }
            Dim arguments = profile.BuildSearchArguments(inputPath, settings, jsonOutput:=True)
            ContainsPair(arguments, "--encoder", "libsvtav1")
            ContainsPair(arguments, "--preset", "4")
            ContainsPair(arguments, "--pix-format", "yuv420p10le")
            ContainsPair(arguments, "--keyint", "8s")
            ContainsPair(arguments, "--scd", "true")
            ContainsPair(arguments, "--svt", "tune=0")
            DoesNotContainPair(arguments, "--svt", "keyint=8s")
            DoesNotContainPair(arguments, "--svt", "scd=1")
            DoesNotContainPair(arguments, "--svt", "crf=31")
            DoesNotContainPair(arguments, "--svt", "input-depth=10")
            DoesNotContainPair(arguments, "--svt", "preset=4")
            Equal(1, arguments.Where(Function(argument) argument = "--preset").Count(), "one effective preset argument")
            Equal(1, arguments.Where(Function(argument) argument = "--keyint").Count(), "one keyint argument")
            Equal(1, arguments.Where(Function(argument) argument = "--scd").Count(), "one SCD argument")
            Equal(2, profile.SearchOwnedArgumentCount, "CRF and input depth remain final-encode-only")
            ContainsPair(arguments, "--enc", "tile-columns=2")
            ContainsPair(arguments, "--min-vmaf", "96.5")
            ContainsPair(arguments, "--samples", "4")
            ContainsPair(arguments, "--sample-duration", "15s")
            ContainsPair(arguments, "--vmaf", "model=version=vmaf_v0.6.1")
            ContainsPair(arguments, "--stdout-format", "json")
            IsTrue(arguments.Contains("--thorough"), "thorough flag")
            IsTrue(Not arguments.Contains("--crf-increment"), "ab-av1 keeps encoder-specific search precision")

            Dim xpsnrSettings As New SearchSettings With {
                .Metric = QualityScoreMetric.Xpsnr,
                .TargetScore = -1.25,
                .MinCrf = 10,
                .MaxCrf = 50,
                .SampleDuration = "15s",
                .VmafModel = "missing-model.json"
            }
            Dim xpsnrArguments = profile.BuildSearchArgumentTemplate(xpsnrSettings)
            ContainsPair(xpsnrArguments, "--min-xpsnr", "-1.25")
            IsTrue(Not xpsnrArguments.Contains("--min-vmaf"), "XPSNR search does not send VMAF target")
            IsTrue(Not xpsnrArguments.Contains("--vmaf"), "XPSNR search ignores stored VMAF model")

            Dim updated = JsonNode.Parse(profile.ApplyCrf(27.5)).AsObject()
            Equal(1, updated("视频参数_比特率_控制方式").GetValue(Of Integer)(), "native CRF mode")
            Equal("crf", updated("视频参数_质量控制_参数名").GetValue(Of String)(), "quality parameter")
            Equal("27.5", updated("视频参数_质量控制_值").GetValue(Of String)(), "quality value")
            Dim advanced = updated("视频参数_质量控制_进阶参数集").GetValue(Of String)()
            IsTrue(advanced.Contains("crf=31", StringComparison.Ordinal), "user advanced arguments remain unchanged")
            IsTrue(advanced.Contains("keyint=8s", StringComparison.Ordinal), "dedicated search mapping does not rewrite preset")
            IsTrue(advanced.Contains("input-depth=10", StringComparison.Ordinal), "search-only omission does not rewrite preset")
            IsTrue(updated("插件扩展数据") IsNot Nothing, "plugin state retained")
        Finally
            File.Delete(inputPath)
        End Try
    End Sub

    Private Sub TestMultipleEncoderMappingAndExactPassThrough()
        Dim settings As New SearchSettings With {
            .TargetScore = 95,
            .MinCrf = 5,
            .MaxCrf = 55,
            .SampleDuration = "20s"
        }

        Dim emptyPreset As New JsonObject From {
            {"预设文件版本", 6}
        }
        Dim emptyArguments = PresetProfile.LoadJson(emptyPreset.ToJsonString()).
            BuildSearchArgumentTemplate(settings)
        IsTrue(Not emptyArguments.Contains("--encoder"), "missing encoder is passed through as missing")

        Dim x265Preset As New JsonObject From {
            {"预设文件版本", 6},
            {"视频参数_编码器_具体编码", "libx265"},
            {"视频参数_编码器_编码预设", "slow"},
            {"视频参数_编码器_配置文件", "main10"},
            {"视频参数_编码器_场景优化", "grain"},
            {"视频参数_编码器_threads", "12"},
            {"视频参数_色彩管理_像素格式", "p010le"},
            {"视频参数_质量控制_进阶参数集", "-x265-params aq-mode=3:rd=4 -crf 23 -preset placebo"},
            {"自定义参数_视频参数", "-metadata ""title=user value"" -unknown-option value"},
            {"输出容器", "mkv"}
        }
        Dim x265Profile = PresetProfile.LoadJson(x265Preset.ToJsonString())
        Dim x265Arguments = x265Profile.BuildSearchArgumentTemplate(settings)
        ContainsPair(x265Arguments, "--encoder", "libx265")
        ContainsPair(x265Arguments, "--pix-format", "p010le")
        ContainsPair(x265Arguments, "--enc", "profile:v=main10")
        ContainsPair(x265Arguments, "--enc", "tune=grain")
        ContainsPair(x265Arguments, "--enc", "threads=12")
        ContainsPair(x265Arguments, "--enc", "x265-params=aq-mode=3:rd=4")
        ContainsPair(x265Arguments, "--preset", "placebo")
        DoesNotContainPair(x265Arguments, "--enc", "crf=23")
        DoesNotContainPair(x265Arguments, "--enc", "preset=placebo")
        Equal(1, x265Arguments.Where(Function(argument) argument = "--preset").Count(), "manual preset replaces structured preset once")
        Equal(1, x265Profile.SearchOwnedArgumentCount, "manual CRF remains final-encode-only")
        ContainsPair(x265Arguments, "--enc", "metadata=title=user value")
        ContainsPair(x265Arguments, "--enc", "unknown-option=value")

        Dim foreignSvtPreset = DirectCast(x265Preset.DeepClone(), JsonObject)
        foreignSvtPreset("视频参数_质量控制_进阶参数集") = "-svtav1-params tune=0:keyint=8s"
        Dim foreignSvtArguments = PresetProfile.LoadJson(foreignSvtPreset.ToJsonString()).
            BuildSearchArgumentTemplate(settings)
        ContainsPair(foreignSvtArguments, "--enc", "svtav1-params=tune=0:keyint=8s")
        IsTrue(Not foreignSvtArguments.Contains("--svt"), "SVT syntax is not invented for another encoder")

        Dim nvencPreset As New JsonObject From {
            {"预设文件版本", 6},
            {"视频参数_编码器_具体编码", "av1_nvenc"},
            {"视频参数_编码器_编码预设", "p7"},
            {"视频参数_编码器_场景优化", "uhq"},
            {"视频参数_编码器_gpu", "1"},
            {"解码参数_解码器", "cuda"},
            {"解码参数_解码数据格式", "cuda"}
        }
        Dim nvencProfile = PresetProfile.LoadJson(nvencPreset.ToJsonString())
        Dim nvencArguments = nvencProfile.BuildSearchArgumentTemplate(settings)
        ContainsPair(nvencArguments, "--encoder", "av1_nvenc")
        ContainsPair(nvencArguments, "--preset", "p7")
        ContainsPair(nvencArguments, "--enc", "tune=uhq")
        ContainsPair(nvencArguments, "--enc", "gpu=1")
        ContainsPair(nvencArguments, "--enc-input", "hwaccel=cuda")
        ContainsPair(nvencArguments, "--enc-input", "hwaccel_output_format=cuda")
        Dim nvencUpdated = JsonNode.Parse(nvencProfile.ApplyCrf(34)).AsObject()
        Equal("cq", nvencUpdated("视频参数_质量控制_参数名").GetValue(Of String)(), "NVENC quality mapping")
        Equal("av1_nvenc", nvencUpdated("视频参数_编码器_具体编码").GetValue(Of String)(), "selected encoder unchanged")

        Dim amfPreset = DirectCast(nvencPreset.DeepClone(), JsonObject)
        amfPreset("视频参数_编码器_具体编码") = "av1_amf"
        amfPreset("视频参数_编码器_编码预设") = "high_quality"
        amfPreset("视频参数_编码器_场景优化") = "transcoding"
        Dim amfArguments = PresetProfile.LoadJson(amfPreset.ToJsonString()).
            BuildSearchArgumentTemplate(settings)
        ContainsPair(amfArguments, "--encoder", "av1_amf")
        ContainsPair(amfArguments, "--enc", "quality=high_quality")
        ContainsPair(amfArguments, "--enc", "usage=transcoding")
        IsTrue(Not amfArguments.Contains("--preset"), "AMF quality field is not relabeled as preset")

        Equal("global_quality", PresetProfile.GetNativeQualityParameterName("av1_qsv"), "QSV quality mapping")
        Equal("q", PresetProfile.GetNativeQualityParameterName("hevc_vaapi"), "VAAPI quality mapping")
        Equal("qp", PresetProfile.GetNativeQualityParameterName("av1_vulkan"), "Vulkan quality mapping")
        Equal("q:v", PresetProfile.GetNativeQualityParameterName("hevc_videotoolbox"), "VideoToolbox quality mapping")
        Equal("crf", PresetProfile.GetNativeQualityParameterName("user_encoder"), "unknown encoder follows ab-av1 default")
    End Sub

    Private Sub TestAbAv1ReservedArgumentMapping()
        Dim settings As New SearchSettings With {
            .TargetScore = 95,
            .MinCrf = 5,
            .MaxCrf = 55,
            .SampleDuration = "20s"
        }
        Dim preset As New JsonObject From {
            {"预设文件版本", 6},
            {"视频参数_编码器_具体编码", "libsvtav1"},
            {"视频参数_编码器_编码预设", "6"},
            {"视频参数_色彩管理_像素格式", "yuv420p10le"},
            {"自定义参数_视频滤镜", "crop=1280:720"},
            {"视频参数_质量控制_进阶参数集",
                "-preset=slow -pix_fmt=yuv420p -vf ""scale=640:-2"" -c:v=libx264 -crf=23 -y -metadata ""title=kept"""}
        }

        Dim profile = PresetProfile.LoadJson(preset.ToJsonString())
        Dim arguments = profile.BuildSearchArgumentTemplate(settings)

        ContainsPair(arguments, "--encoder", "libx264")
        ContainsPair(arguments, "--preset", "slow")
        ContainsPair(arguments, "--pix-format", "yuv420p")
        ContainsPair(arguments, "--vfilter", "scale=640:-2")
        ContainsPair(arguments, "--enc", "metadata=title=kept")
        DoesNotContainPair(arguments, "--enc", "preset=slow")
        DoesNotContainPair(arguments, "--enc", "pix_fmt=yuv420p")
        DoesNotContainPair(arguments, "--enc", "vf=scale=640:-2")
        DoesNotContainPair(arguments, "--enc", "c:v=libx264")
        DoesNotContainPair(arguments, "--enc", "crf=23")
        IsTrue(Not arguments.Contains("y"), "overwrite flag is not duplicated into search samples")
        Equal(1, arguments.Where(Function(argument) argument = "--encoder").Count(), "one effective encoder argument")
        Equal(1, arguments.Where(Function(argument) argument = "--preset").Count(), "one effective generic preset argument")
        Equal(1, arguments.Where(Function(argument) argument = "--pix-format").Count(), "one effective pixel format argument")
        Equal(1, arguments.Where(Function(argument) argument = "--vfilter").Count(), "one effective video filter argument")
        Equal(2, profile.SearchOwnedArgumentCount, "manual CRF and overwrite flag remain final-encode-only")

        For Each encoderAlias In {"c:v", "c:v:0", "codec:v", "codec:v:0", "vcodec"}
            Dim aliasPreset = DirectCast(preset.DeepClone(), JsonObject)
            aliasPreset("视频参数_质量控制_进阶参数集") = $"-{encoderAlias} libx265"
            Dim aliasArguments = PresetProfile.LoadJson(aliasPreset.ToJsonString()).
                BuildSearchArgumentTemplate(settings)
            ContainsPair(aliasArguments, "--encoder", "libx265")
            DoesNotContainPair(aliasArguments, "--enc", encoderAlias & "=libx265")
        Next

        For Each filterAlias In {"vf", "filter:v"}
            Dim aliasPreset = DirectCast(preset.DeepClone(), JsonObject)
            aliasPreset("视频参数_质量控制_进阶参数集") = $"-{filterAlias} scale=320:-2"
            Dim aliasArguments = PresetProfile.LoadJson(aliasPreset.ToJsonString()).
                BuildSearchArgumentTemplate(settings)
            ContainsPair(aliasArguments, "--vfilter", "scale=320:-2")
            DoesNotContainPair(aliasArguments, "--enc", filterAlias & "=scale=320:-2")
        Next

        Dim searchOwnedCases = New (Command As String, EncValue As String)() {
            ("-crf 23", "crf=23"),
            ("-i other.mkv", "i=other.mkv"),
            ("-y", "y"),
            ("-n", "n"),
            ("-c:a aac", "c:a=aac"),
            ("-codec:a aac", "codec:a=aac"),
            ("-acodec aac", "acodec=aac")
        }
        For Each testCase In searchOwnedCases
            Dim ownedPreset = DirectCast(preset.DeepClone(), JsonObject)
            ownedPreset("视频参数_质量控制_进阶参数集") = testCase.Command
            Dim ownedProfile = PresetProfile.LoadJson(ownedPreset.ToJsonString())
            Dim ownedArguments = ownedProfile.BuildSearchArgumentTemplate(settings)
            DoesNotContainPair(ownedArguments, "--enc", testCase.EncValue)
            Equal(1, ownedProfile.SearchOwnedArgumentCount, "reserved search-only argument: " & testCase.Command)
        Next

        Dim inputPixelPreset = DirectCast(preset.DeepClone(), JsonObject)
        inputPixelPreset("视频参数_质量控制_进阶参数集") = ""
        inputPixelPreset("解码参数_指定硬件的参数名") = "-pix_fmt"
        inputPixelPreset("解码参数_指定硬件的参数") = "yuv420p"
        Dim inputPixelArguments = PresetProfile.LoadJson(inputPixelPreset.ToJsonString()).
            BuildSearchArgumentTemplate(settings)
        ContainsPair(inputPixelArguments, "--pix-format", "yuv420p")
        DoesNotContainPair(inputPixelArguments, "--enc-input", "pix_fmt=yuv420p")

        Dim inputCrfPreset = DirectCast(inputPixelPreset.DeepClone(), JsonObject)
        inputCrfPreset("解码参数_指定硬件的参数名") = "-crf"
        inputCrfPreset("解码参数_指定硬件的参数") = "22"
        Dim inputCrfProfile = PresetProfile.LoadJson(inputCrfPreset.ToJsonString())
        Dim inputCrfArguments = inputCrfProfile.BuildSearchArgumentTemplate(settings)
        DoesNotContainPair(inputCrfArguments, "--enc-input", "crf=22")
        Equal(1, inputCrfProfile.SearchOwnedArgumentCount, "input CRF remains final-encode-only")
    End Sub

    Private Sub TestVmafModelArguments()
        Equal(
            "model=version=vmaf_v0.6.1",
            PresetProfile.BuildVmafModelArgument("vmaf_v0.6.1"),
            "built-in VMAF model")
        Equal(String.Empty, PresetProfile.BuildVmafModelArgument(String.Empty), "automatic VMAF model")
    End Sub

    Private Sub TestVmafModelHelpParser()
        Dim help = "  model <string> model config (default ""version=vmaf_v0.6.1|version=vmaf_4k_v0.6.1"")"
        Dim models = VmafModelScanner.ParseModelsFromFilterHelp(help)
        Equal(2, models.Count, "model count")
        Equal("vmaf_v0.6.1", models(0), "default model")
        Equal("vmaf_4k_v0.6.1", models(1), "4K model")
    End Sub

    Private Async Function TestAbAv1RunnerProcessIntegrationAsync() As Task
        Dim fakeExecutable = PluginEnvironment.AbAv1Path
        Dim inputPath = Path.Combine(Path.GetTempPath(), "ffmpegfreeui-ext-ab-av1-runner-input.bin")
        If Not File.Exists(fakeExecutable) Then
            Throw New FileNotFoundException("Fake ab-av1 test executable was not copied", fakeExecutable)
        End If
        File.WriteAllBytes(inputPath, New Byte() {0})

        Try
            Dim profile = PresetProfile.LoadJson(CreateMinimalPresetJson())
            Dim settings As New SearchSettings With {
                .TargetScore = 95,
                .MinCrf = 5,
                .MaxCrf = 55,
                .SampleDuration = "20s"
            }
            Dim messages As New ConcurrentQueue(Of SearchProgress)()
            Dim runner As New AbAv1Runner()
            Dim result = Await runner.SearchAsync(
                profile,
                inputPath,
                settings,
                New InlineProgress(Of SearchProgress)(
                    Sub(value)
                        messages.Enqueue(value)
                    End Sub),
                CancellationToken.None).ConfigureAwait(False)

            Equal(28.0, result.Crf, "JSON runner CRF")
            Equal(QualityScoreMetric.Vmaf, result.Metric, "JSON runner VMAF metric")
            Equal(95.2, result.Score, "JSON runner VMAF score")
            Equal(123456789L, result.PredictedEncodeSize, "JSON runner predicted bytes")
            Equal(321.5, result.PredictedEncodeSeconds, "JSON runner predicted seconds")
            IsTrue(
                messages.Any(Function(item) item.TestedCrf.HasValue AndAlso item.TestedCrf.Value = 30),
                "JSON runner progress")
            IsTrue(runner.CurrentCommandLine.Contains("crf-search", StringComparison.Ordinal), "runner command line")

            Dim xpsnrSettings As New SearchSettings With {
                .Metric = QualityScoreMetric.Xpsnr,
                .TargetScore = 38,
                .MinCrf = 5,
                .MaxCrf = 55,
                .SampleDuration = "20s",
                .VmafModel = "missing-model.json"
            }
            Dim xpsnrMessages As New ConcurrentQueue(Of SearchProgress)()
            Dim xpsnrResult = Await New AbAv1Runner().SearchAsync(
                profile,
                inputPath,
                xpsnrSettings,
                New InlineProgress(Of SearchProgress)(Sub(value) xpsnrMessages.Enqueue(value)),
                CancellationToken.None).ConfigureAwait(False)
            Equal(QualityScoreMetric.Xpsnr, xpsnrResult.Metric, "JSON runner XPSNR metric")
            Equal(38.2, xpsnrResult.Score, "JSON runner XPSNR score")
            IsTrue(
                xpsnrMessages.Any(
                    Function(item) item.TestedScore.HasValue AndAlso
                                   item.Metric = QualityScoreMetric.Xpsnr),
                "JSON runner XPSNR progress")

            Dim cancelSettings As New SearchSettings With {
                .TargetScore = 99.999,
                .MinCrf = 5,
                .MaxCrf = 55,
                .SampleDuration = "20s"
            }
            Using cancellation As New CancellationTokenSource(TimeSpan.FromMilliseconds(350))
                Dim timer = Diagnostics.Stopwatch.StartNew()
                Dim canceled = False
                Try
                    Await New AbAv1Runner().SearchAsync(
                        profile,
                        inputPath,
                        cancelSettings,
                        Nothing,
                        cancellation.Token).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    canceled = True
                End Try
                timer.Stop()
                IsTrue(canceled, "runner cancellation propagated")
                IsTrue(timer.Elapsed < TimeSpan.FromSeconds(5), "runner cancellation killed process promptly")
            End Using
        Finally
            Try
                If File.Exists(inputPath) Then File.Delete(inputPath)
            Catch
            End Try
        End Try
    End Function

    Private Function CreateMinimalPresetJson() As String
        Dim preset As New JsonObject()
        preset("预设文件版本") = 6
        preset("视频参数_编码器_具体编码") = "libsvtav1"
        preset("视频参数_编码器_编码预设") = "6"
        preset("视频参数_色彩管理_像素格式") = "yuv420p10le"
        preset("输出容器") = "mkv"
        preset("插件扩展数据") = New JsonObject From {
            {AbAv1Plugin.PluginId, JsonValue.Create(New AbAv1PluginState With {.Enabled = True}.Serialize())}
        }
        Return preset.ToJsonString()
    End Function

    Private Sub ContainsPair(arguments As IReadOnlyList(Of String), optionName As String, value As String)
        For index = 0 To arguments.Count - 2
            If arguments(index) = optionName AndAlso arguments(index + 1) = value Then
                _assertions += 1
                Return
            End If
        Next
        Throw New InvalidOperationException($"Missing argument pair: {optionName} {value}")
    End Sub

    Private Sub DoesNotContainPair(arguments As IReadOnlyList(Of String), optionName As String, value As String)
        For index = 0 To arguments.Count - 2
            If arguments(index) = optionName AndAlso arguments(index + 1) = value Then
                Throw New InvalidOperationException($"Unexpected argument pair: {optionName} {value}")
            End If
        Next
        _assertions += 1
    End Sub

    Private Sub Equal(Of T)(expected As T, actual As T, description As String)
        If Not EqualityComparer(Of T).Default.Equals(expected, actual) Then
            Throw New InvalidOperationException(
                $"Assertion failed ({description}): expected={expected}, actual={actual}")
        End If
        _assertions += 1
    End Sub

    Private Sub IsTrue(value As Boolean, description As String)
        If Not value Then Throw New InvalidOperationException("Assertion failed: " & description)
        _assertions += 1
    End Sub

    Private NotInheritable Class FakeHost
        Implements IExtFFmpegFreeUIHost

        Public Sub New()
            CommandRegistry = New FakeCommandRegistry()
            PresetOverviewRegistry = New FakePresetOverviewRegistry()
            UiRegistry = New FakeUiRegistry()
            PipelineRegistry = New FakePipelineRegistry()
            Ui = UiRegistry
            Pipeline = PipelineRegistry
            PresetOverview = PresetOverviewRegistry
            Commands = CommandRegistry
        End Sub

        Public ReadOnly Property UiRegistry As FakeUiRegistry
        Public ReadOnly Property PipelineRegistry As FakePipelineRegistry
        Public ReadOnly Property PresetOverviewRegistry As FakePresetOverviewRegistry
        Public ReadOnly Property CommandRegistry As FakeCommandRegistry
        Public ReadOnly Property LogMessages As New List(Of String)()

        Public ReadOnly Property ApiVersion As Version = New Version(2, 5, 0) Implements IExtFFmpegFreeUIHost.ApiVersion
        Public ReadOnly Property HostVersion As String = "test-host" Implements IExtFFmpegFreeUIHost.HostVersion
        Public ReadOnly Property Ui As IExtPluginUiRegistry Implements IExtFFmpegFreeUIHost.Ui
        Public ReadOnly Property Pipeline As IExtPluginPipelineRegistry Implements IExtFFmpegFreeUIHost.Pipeline
        Public ReadOnly Property PageEntries As IExtPluginPageEntryRegistry = Nothing Implements IExtFFmpegFreeUIHost.PageEntries
        Public ReadOnly Property EncodingQueueToolbar As IExtPluginEncodingQueueToolbarRegistry = Nothing Implements IExtFFmpegFreeUIHost.EncodingQueueToolbar
        Public ReadOnly Property PluginSettings As IExtPluginSettingsRegistry = Nothing Implements IExtFFmpegFreeUIHost.PluginSettings
        Public ReadOnly Property PresetOverview As IExtPluginPresetOverviewRegistry Implements IExtFFmpegFreeUIHost.PresetOverview
        Public ReadOnly Property Behaviors As IExtPluginBehaviorRegistry = Nothing Implements IExtFFmpegFreeUIHost.Behaviors
        Public ReadOnly Property Resources As IExtPluginResourceRegistry = Nothing Implements IExtFFmpegFreeUIHost.Resources
        Public ReadOnly Property ParameterPanel As IExtPluginParameterPanelCatalog = Nothing Implements IExtFFmpegFreeUIHost.ParameterPanel
        Public ReadOnly Property Commands As IExtPluginCommandRegistry Implements IExtFFmpegFreeUIHost.Commands

        Public Sub Log(level As ExtPluginLogLevel,
                       message As String,
                       Optional exception As Exception = Nothing) Implements IExtFFmpegFreeUIHost.Log
            LogMessages.Add(message)
        End Sub
    End Class

    Private NotInheritable Class FakeUiRegistry
        Implements IExtPluginUiRegistry

        Public ReadOnly Property Extensions As New List(Of ExtPluginUiExtension)()
        Public ReadOnly Property ChoiceExtensions As New List(Of ExtPluginUiChoiceExtension)()
        Public ReadOnly Property AvailableAnchors As IReadOnlyCollection(Of String) =
            New String() {
                ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityAfterGlobal,
                ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityParameterName,
                ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityValue
            } Implements IExtPluginUiRegistry.AvailableAnchors
        Public ReadOnly Property AvailableChoiceAnchors As IReadOnlyCollection(Of String) =
            New String() {ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityMode} Implements IExtPluginUiRegistry.AvailableChoiceAnchors

        Public Function Register(extension As ExtPluginUiExtension) As IDisposable Implements IExtPluginUiRegistry.Register
            Extensions.Add(extension)
            Return New NoopDisposable()
        End Function

        Public Function RegisterChoice(extension As ExtPluginUiChoiceExtension) As IDisposable Implements IExtPluginUiRegistry.RegisterChoice
            ChoiceExtensions.Add(extension)
            Return New NoopDisposable()
        End Function
    End Class

    Private NotInheritable Class FakePresetOverviewRegistry
        Implements IExtPluginPresetOverviewRegistry

        Public ReadOnly Property RowProviders As New List(Of ExtPluginPresetOverviewRowProvider)()

        Public Function RegisterRowProvider(
            provider As ExtPluginPresetOverviewRowProvider) As IDisposable Implements IExtPluginPresetOverviewRegistry.RegisterRowProvider

            RowProviders.Add(provider)
            Return New NoopDisposable()
        End Function
    End Class

    Private NotInheritable Class FakeCommandRegistry
        Implements IExtPluginCommandRegistry

        Public ReadOnly Property ParameterProviders As New List(Of ExtPluginCommandParameterProvider)()
        Public ReadOnly Property StepProviders As New List(Of ExtPluginCommandStepProvider)()

        Public Function RegisterParameterProvider(
            provider As ExtPluginCommandParameterProvider) As IDisposable Implements IExtPluginCommandRegistry.RegisterParameterProvider

            ParameterProviders.Add(provider)
            Return New NoopDisposable()
        End Function

        Public Function RegisterStepProvider(
            provider As ExtPluginCommandStepProvider) As IDisposable Implements IExtPluginCommandRegistry.RegisterStepProvider

            StepProviders.Add(provider)
            Return New NoopDisposable()
        End Function
    End Class

    Private NotInheritable Class FakePipelineRegistry
        Implements IExtPluginPipelineRegistry

        Public ReadOnly Property Handlers As New List(Of ExtPluginPipelineHandler)()
        Public ReadOnly Property AvailableStages As IReadOnlyCollection(Of String) =
            New String() {
                ExtFFmpegFreeUIPipelineStages.PresetAfterCapture,
                ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare
            } Implements IExtPluginPipelineRegistry.AvailableStages

        Public Function Register(handler As ExtPluginPipelineHandler) As IDisposable Implements IExtPluginPipelineRegistry.Register
            Handlers.Add(handler)
            Return New NoopDisposable()
        End Function
    End Class

    Private NotInheritable Class FakeChoiceContext
        Implements IExtPluginUiChoiceContext

        Public ReadOnly Property PluginId As String = AbAv1Plugin.PluginId Implements IExtPluginUiChoiceContext.PluginId
        Public ReadOnly Property ExtensionId As String = "target-vmaf-choice" Implements IExtPluginUiChoiceContext.ExtensionId
        Public ReadOnly Property AnchorId As String = ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityMode Implements IExtPluginUiChoiceContext.AnchorId
        Public ReadOnly Property ChoiceId As String = AbAv1Plugin.QualityChoiceId Implements IExtPluginUiChoiceContext.ChoiceId
        Public ReadOnly Property SurfaceId As String = "test-surface" Implements IExtPluginUiChoiceContext.SurfaceId
        Public ReadOnly Property IsSelected As Boolean = True Implements IExtPluginUiChoiceContext.IsSelected
        Public Property StateJson As String = "{}" Implements IExtPluginUiChoiceContext.StateJson
        Public Property RefreshCount As Integer

        Public Sub RequestParameterRefresh() Implements IExtPluginUiChoiceContext.RequestParameterRefresh
            RefreshCount += 1
        End Sub
    End Class

    Private NotInheritable Class NoopDisposable
        Implements IDisposable

        Public Sub Dispose() Implements IDisposable.Dispose
        End Sub
    End Class

    Private NotInheritable Class FakeUiContext
        Implements IExtPluginUiContext

        Public Sub New(Optional anchorControlValue As Control = Nothing)
            AnchorControl = If(
                anchorControlValue,
                New Panel With {
                    .BackColor = Color.FromArgb(24, 24, 24),
                    .Font = New Font("Microsoft YaHei UI", 10.0F)
                })
            ContainerControl = New Panel()
            NativeTextBox = New ModernTextBox With {
                .AnimationFPS = 47,
                .BackColor1 = Color.FromArgb(55, 80, 90, 100),
                .BackgroundSource = AnchorControl,
                .BorderColor = Color.Magenta,
                .BorderColorFocus = Color.Cyan,
                .BorderRadius = 14,
                .BorderSize = 2,
                .Font = AnchorControl.Font,
                .Location = New Point(470, 10),
                .Name = "MTB_质量值",
                .Size = New Size(100, 32)
            }
            NativeComboBox = New ModernComboBox With {
                .AnimationFPS = 53,
                .BackColor1 = Color.FromArgb(65, 90, 100, 110),
                .BackColor2 = Color.FromArgb(65, 110, 100, 90),
                .BackgroundSource = AnchorControl,
                .BorderRadius = 15,
                .BorderSize = 3,
                .DropDownBackdropBlurRadius = 17,
                .DropDownMode = ModernComboBox.DropDownDisplayMode.Overlay,
                .Font = AnchorControl.Font,
                .Size = New Size(150, 32)
            }
            NativeMetricComboBox = New ModernComboBox With {
                .AnimationFPS = 53,
                .BackColor1 = NativeComboBox.BackColor1,
                .BackColor2 = NativeComboBox.BackColor2,
                .BackgroundSource = AnchorControl,
                .BorderRadius = NativeComboBox.BorderRadius,
                .BorderSize = NativeComboBox.BorderSize,
                .Editable = True,
                .Font = AnchorControl.Font,
                .Location = New Point(300, 10),
                .Name = "MCB_质量参数名称",
                .Size = New Size(150, 32),
                .Text = "-crf",
                .WaterText = "质量参数"
            }
            NativeMetricComboBox.Items.AddRange(New String() {"-crf", "-cq", "-qp"})
            NativeTextBox.Text = "31"
            NativeTextBox.WaterText = "质量值"
            AnchorControl.Size = New Size(807, 42)
            AnchorControl.Controls.Add(NativeMetricComboBox)
            AnchorControl.Controls.Add(NativeTextBox)
        End Sub

        Public ReadOnly Property PluginId As String = "ffmpegfreeui.ext.ab-av1" Implements IExtPluginUiContext.PluginId
        Public ReadOnly Property ExtensionId As String = "test.settings" Implements IExtPluginUiContext.ExtensionId
        Public ReadOnly Property AnchorId As String = ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityAfterGlobal Implements IExtPluginUiContext.AnchorId
        Public ReadOnly Property SurfaceId As String = "test-surface" Implements IExtPluginUiContext.SurfaceId
        Public ReadOnly Property AnchorControl As Control Implements IExtPluginUiContext.AnchorControl
        Public ReadOnly Property ContainerControl As Control Implements IExtPluginUiContext.ContainerControl
        Public ReadOnly Property NativeTextBox As ModernTextBox
        Public ReadOnly Property NativeComboBox As ModernComboBox
        Public ReadOnly Property NativeMetricComboBox As ModernComboBox
        Public Property ProvideNativeStyleTemplates As Boolean
        Public Property StateJson As String = "{}" Implements IExtPluginUiContext.StateJson
        Public Event StateRestored As EventHandler Implements IExtPluginUiContext.StateRestored
        Public Property RefreshCount As Integer

        Public Function GetAnchorControl(anchorId As String) As Control Implements IExtPluginUiContext.GetAnchorControl
            If Not ProvideNativeStyleTemplates Then Return Nothing
            Select Case anchorId
                Case ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityValue
                    Return NativeTextBox
                Case ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityMode
                    Return NativeComboBox
                Case ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityParameterName
                    Return NativeMetricComboBox
                Case Else
                    Return Nothing
            End Select
        End Function

        Public Sub RequestParameterRefresh() Implements IExtPluginUiContext.RequestParameterRefresh
            RefreshCount += 1
        End Sub

        Public Sub Restore(json As String)
            StateJson = json
            RaiseEvent StateRestored(Me, EventArgs.Empty)
        End Sub
    End Class

End Module
