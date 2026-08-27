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
        If args.Contains("--visual-overview", StringComparer.OrdinalIgnoreCase) Then
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)
            RenderVisibleOverviewSummaryTextBox()
            Return
        End If

        TestPluginDiscoveryContract()
        TestQualitySettingsPanelState()
        TestOverviewSummaryDecoratorState()
        TestQualitySettingsPanelStyleInheritance()
        TestPluginRegistrationAndPipelineAsync().GetAwaiter().GetResult()
        TestCommandLineTokenizer()
        TestPresetMappingAndCrfWriteBack()
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
                                     editor.BackgroundSource Is context.AnchorControl),
                "native text style and render source inherited")

            Dim comboBox = Descendants(panel).OfType(Of ModernComboBox)().Single()
            Equal(context.NativeComboBox.BackColor1, comboBox.BackColor1, "native combo background")
            Equal(context.NativeComboBox.BorderRadius, comboBox.BorderRadius, "native combo radius")
            Equal(
                context.NativeComboBox.DropDownBackdropBlurRadius,
                comboBox.DropDownBackdropBlurRadius,
                "native combo popup backdrop")
            Equal(context.NativeComboBox.AnimationFPS, comboBox.AnimationFPS, "native combo animation FPS")
            IsTrue(comboBox.BackgroundSource Is context.AnchorControl, "native combo render source inherited")
            IsTrue(
                Descendants(panel).OfType(Of ModernButton)().All(
                    Function(button) button.AnimationFPS = context.NativeComboBox.AnimationFPS),
                "buttons follow host animation FPS")
            IsTrue(
                Descendants(panel).OfType(Of ModernCheckBox)().All(
                    Function(checkBox) checkBox.AnimationFPS = context.NativeComboBox.AnimationFPS),
                "checkbox follows host animation FPS")
            IsTrue(
                Descendants(panel).OfType(Of Label)().All(
                    Function(label) label.Font.FontFamily.Name = context.AnchorControl.Font.FontFamily.Name),
                "labels follow host font family")
            IsTrue(
                Descendants(panel).OfType(Of HtmlColorLabel)().All(
                    Function(label) label.Font.FontFamily.Name = context.AnchorControl.Font.FontFamily.Name),
                "LakeUI captions follow host font family")

            Dim target = textBoxes.Single(Function(editor) editor.WaterText = "目标 VMAF")
            target.Text = "invalid"
            Equal(Color.FromArgb(235, 93, 93), target.BorderColor, "validation border shown")
            target.Text = "95"
            Equal(context.NativeTextBox.BorderColor, target.BorderColor, "native border restored")
            Equal(context.NativeTextBox.BorderSize, target.BorderSize, "native border width restored")
        End Using
    End Sub

    Private Async Function RunRealAbAv1RunnerAsync(inputPath As String) As Task
        If Not File.Exists(inputPath) Then Throw New FileNotFoundException("Real test input not found", inputPath)

        Dim profile = PresetProfile.LoadJson(CreateMinimalPresetJson())
        Dim settings As New SearchSettings With {
            .TargetVmaf = 80,
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

        If Double.IsNaN(result.Crf) OrElse Double.IsNaN(result.Vmaf) Then
            Throw New InvalidDataException("Real ab-av1 result contains NaN")
        End If
        Console.WriteLine(
            $"REAL_RESULT: CRF={result.Crf.ToString("0.###", CultureInfo.InvariantCulture)}; " &
            $"VMAF={result.Vmaf.ToString("0.###", CultureInfo.InvariantCulture)}; " &
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
            Dim rootLayout = DirectCast(panel.Controls(0), TableLayoutPanel)
            rootLayout.PerformLayout()
            Equal(panel.ClientSize.Width, rootLayout.Width, "settings layout constrained to host width")
            IsTrue(
                rootLayout.Controls.Cast(Of Control)().All(
                    Function(child) child.Left >= 0 AndAlso child.Right <= rootLayout.ClientSize.Width),
                "settings rows stay inside host width")
            IsTrue(
                Descendants(panel).OfType(Of TableLayoutPanel)().All(
                    Function(table) table.Controls.Cast(Of Control)().All(
                        Function(child) child.Left >= 0 AndAlso child.Right <= table.ClientSize.Width)),
                "nested settings controls stay inside their cells")
            IsTrue(
                Not Descendants(panel).OfType(Of HtmlColorLabel)().Any(
                    Function(label) label.Text.Contains("AB-AV1 目标 VMAF", StringComparison.Ordinal)),
                "redundant settings heading removed")
            Dim searchFields = Descendants(panel).OfType(Of ModernTextBox)().ToArray()
            Equal(5, searchFields.Length, "LakeUI search fields")
            IsTrue(searchFields.All(Function(input) input.Height = 32), "native 32 px text field height")
            IsTrue(
                Descendants(panel).OfType(Of ModernTextBox)().Any(Function(input) input.Text = "97"),
                "target VMAF restored")
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
            Dim modelRow = DirectCast(modelSelector.Parent, TableLayoutPanel)
            IsTrue(
                modelRow.Controls.Cast(Of Control)().All(
                    Function(control) control.Top >= 0 AndAlso control.Bottom <= modelRow.ClientSize.Height),
                "all model-row controls stay inside the row")
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
                New String() {"目标 VMAF", "最小 CRF", "最大 CRF", "采样数量", "单段时长"})
            Dim captionLabels = Descendants(panel).OfType(Of HtmlColorLabel)().Where(
                Function(label) captions.Contains(label.Text)).ToArray()
            Equal(5, captionLabels.Length, "five native-style field captions")
            IsTrue(
                captionLabels.All(
                    Function(label) label.TextAlign = HtmlColorLabel.TextAlignEnum.BottomLeft AndAlso
                                    label.ForeColor = Color.FromArgb(120, 255, 255, 255)),
                "field captions use native gray bottom-aligned style")
            Dim target = searchFields.Single(Function(editor) editor.WaterText = "目标 VMAF")
            Dim targetCaption = captionLabels.Single(Function(label) label.Text = "目标 VMAF")
            Equal(10, target.Top - targetCaption.Bottom, "native label-to-input spacing")
            Equal(
                target.Parent.Top + target.Bottom,
                thorough.Parent.Top + thorough.Bottom,
                "thorough bottom aligns with search input bottoms")

            context.AnchorControl.PerformLayout()
            Dim environmentStatus = context.AnchorControl.Controls.OfType(Of Label)().Single(
                Function(label) label.Name = QualityValueStatusAdornment.StatusControlName)
            Equal(context.NativeTextBox.Right + 10, environmentStatus.Left, "status follows quality value")
            Equal(context.NativeTextBox.Top, environmentStatus.Top, "status aligns with quality value")
            Equal(context.NativeTextBox.Height, environmentStatus.Height, "status matches quality value height")
            Equal("ab-av1 已就绪", environmentStatus.Text, "compact ready status")
            IsTrue(
                Not environmentStatus.Text.Contains("搜索在", StringComparison.Ordinal),
                "task-stage status text removed")

            context.Restore(New AbAv1PluginState With {.Enabled = False}.Serialize())
            IsTrue(Not panel.Visible, "settings hidden for native mode")
            IsTrue(Not environmentStatus.Visible, "quality value status hidden for native mode")
            panel.SetActive(True)
            IsTrue(panel.Visible, "choice callback shows settings")
            IsTrue(environmentStatus.Visible, "quality value status shown for VMAF mode")
        End Using

        Dim disposedPanel As New QualitySettingsPanel(New FakeUiContext())
        disposedPanel.Dispose()
        disposedPanel.Dispose()
        IsTrue(disposedPanel.IsDisposed, "settings panel disposal is idempotent")
    End Sub

    Private Sub TestOverviewSummaryDecoratorState()
        Dim storedState As New AbAv1PluginState With {
            .Enabled = True,
            .TargetVmaf = 96.5,
            .MinCrf = 10,
            .MaxCrf = 42,
            .Samples = 4,
            .SampleDuration = "15s",
            .Thorough = True,
            .VmafModel = "vmaf_v0.6.1"
        }
        Dim nativeTextBox As New ModernTextBox With {
            .MultiLine = True,
            .Name = AbAv1Plugin.OverviewControlName,
            .Text = "输出容器：.mkv"
        }
        Dim context As New FakeOverviewUiContext(nativeTextBox) With {
            .StateJson = storedState.Serialize()
        }

        IsTrue(AbAv1PluginState.HasStoredState(context.StateJson), "stored plugin state detected")
        IsTrue(Not AbAv1PluginState.HasStoredState("{}"), "empty plugin state detected")

        Using decorator As New OverviewSummaryDecorator(context)
            IsTrue(nativeTextBox.Text.StartsWith("输出容器：.mkv", StringComparison.Ordinal), "native overview retained")
            IsTrue(nativeTextBox.Text.Contains("AB-AV1 目标 VMAF：96.5", StringComparison.Ordinal), "overview target VMAF")
            IsTrue(nativeTextBox.Text.Contains("AB-AV1 最小 CRF：10", StringComparison.Ordinal), "overview minimum CRF")
            IsTrue(nativeTextBox.Text.Contains("AB-AV1 最大 CRF：42", StringComparison.Ordinal), "overview maximum CRF")
            IsTrue(nativeTextBox.Text.Contains("AB-AV1 采样数量：4", StringComparison.Ordinal), "overview samples")
            IsTrue(nativeTextBox.Text.Contains("AB-AV1 单段时长：15s", StringComparison.Ordinal), "overview duration")
            IsTrue(nativeTextBox.Text.Contains("AB-AV1 彻底搜索：是", StringComparison.Ordinal), "overview thorough mode")
            IsTrue(nativeTextBox.Text.Contains("AB-AV1 VMAF 模型：vmaf_v0.6.1", StringComparison.Ordinal), "overview model")
            IsTrue(
                Not nativeTextBox.Text.Contains("已启用", StringComparison.Ordinal) AndAlso
                Not nativeTextBox.Text.Contains("未启用", StringComparison.Ordinal),
                "overview omits enabled state")

            nativeTextBox.Text = "视频编码器：libsvtav1"
            IsTrue(nativeTextBox.Text.StartsWith("视频编码器：libsvtav1", StringComparison.Ordinal), "native refresh retained")
            Equal(
                1,
                nativeTextBox.Text.Split({"AB-AV1 目标 VMAF："}, StringSplitOptions.None).Length - 1,
                "native refresh appends overview block once")

            storedState.Enabled = False
            storedState.TargetVmaf = 94
            context.Restore(storedState.Serialize())
            IsTrue(nativeTextBox.Text.Contains("AB-AV1 目标 VMAF：94", StringComparison.Ordinal), "stored details retained in native mode")
            IsTrue(Not nativeTextBox.Text.Contains("未启用", StringComparison.Ordinal), "disabled label omitted")

            context.Restore("{}")
            Equal("视频编码器：libsvtav1", nativeTextBox.Text, "empty plugin state removes owned overview lines")

            context.Restore(storedState.Serialize())
        End Using
        Equal("视频编码器：libsvtav1", nativeTextBox.Text, "decorator cleanup restores native overview")
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
                    Dim target = Descendants(panel).OfType(Of ModernTextBox)().Single(
                        Function(editor) editor.WaterText = "目标 VMAF")
                    Dim model = Descendants(panel).OfType(Of ModernComboBox)().Single()
                    Dim rootLayout = DirectCast(panel.Controls(0), TableLayoutPanel)
                    Dim overflowingControl = Descendants(panel).FirstOrDefault(
                        Function(control) control.Parent IsNot Nothing AndAlso
                                          control.Right > control.Parent.ClientSize.Width)
                    If overflowingControl IsNot Nothing Then
                        Throw New InvalidOperationException(
                            $"Visual layout overflow: {overflowingControl.GetType().Name} {overflowingControl.Bounds}")
                    End If
                    Console.WriteLine(
                        $"VISUAL_METRICS: panel={panel.Width}x{panel.Height}; " &
                        $"layout={rootLayout.Width}x{rootLayout.Height}; " &
                        $"target={target.Width}x{target.Height}; model={model.Width}x{model.Height}")
                    Console.WriteLine("VISUAL_RENDER: " & outputPath)
                End Using
                form.Close()
            End Using
        End Using
    End Sub

    Private Sub RenderVisibleOverviewSummaryTextBox()
        Dim summaryTextBox As New ModernTextBox With {
            .BackColor1 = Color.FromArgb(40, 220, 220, 220),
            .Dock = DockStyle.Fill,
            .Font = New Font("Microsoft YaHei UI", 11.0F),
            .LineHeight = 20,
            .MultiLine = True,
            .Padding = New Padding(10, 8, 10, 8),
            .ReadOnly = True,
            .ShowLineNumbers = True,
            .Text = "输出容器：.mkv" & vbCrLf & "视频编码器：libsvtav1"
        }
        Dim context As New FakeOverviewUiContext(summaryTextBox) With {
            .StateJson = New AbAv1PluginState With {
                .Enabled = True,
                .TargetVmaf = 96.5,
                .MinCrf = 10,
                .MaxCrf = 42,
                .Samples = 4,
                .SampleDuration = "15s",
                .Thorough = True,
                .VmafModel = "vmaf_v0.6.1"
            }.Serialize()
        }
        Using form As New Form With {
            .BackColor = Color.FromArgb(24, 24, 24),
            .ClientSize = New Size(600, 420),
            .FormBorderStyle = FormBorderStyle.None,
            .Location = New Point(40, 40),
            .ShowInTaskbar = False,
            .StartPosition = FormStartPosition.Manual,
            .TopMost = True
        }
            form.Controls.Add(summaryTextBox)

            Using decorator As New OverviewSummaryDecorator(context)
                form.Show()
                form.Activate()
                form.BringToFront()
                For index = 1 To 6
                    Application.DoEvents()
                    Thread.Sleep(50)
                Next

                Using bitmap As New Bitmap(summaryTextBox.ClientSize.Width, summaryTextBox.ClientSize.Height)
                    summaryTextBox.DrawToBitmap(bitmap, New Rectangle(Point.Empty, bitmap.Size))
                    Dim outputPath = Path.Combine(AppContext.BaseDirectory, "overview-textbox-visible.png")
                    bitmap.Save(outputPath, Imaging.ImageFormat.Png)
                    Console.WriteLine("VISUAL_OVERVIEW: " & outputPath)
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
        Equal(2, host.UiRegistry.Extensions.Count, "settings panel and overview decorator registered")
        Equal(1, host.PipelineRegistry.Handlers.Count, "pipeline handler registered")
        Equal(1, host.CommandRegistry.StepProviders.Count, "command preview provider registered")

        Dim settingsExtension = host.UiRegistry.Extensions.Single(
            Function(extension) extension.Id = "target-vmaf-settings")
        Equal(
            ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityAfterGlobal,
            settingsExtension.AnchorId,
            "settings panel anchor")
        IsTrue(String.IsNullOrEmpty(settingsExtension.ResourceId), "settings panel no longer changes scroll resources")
        Dim overviewExtension = host.UiRegistry.Extensions.Single(
            Function(extension) extension.Id = "target-vmaf-overview")
        Equal(FakeParameterPanelCatalog.OverviewAnchorId, overviewExtension.AnchorId, "overview textbox anchor")
        Equal(FakeParameterPanelCatalog.OverviewResourceId, overviewExtension.ResourceId, "overview resource lease")
        Equal(
            ExtPluginResourceAccess.OrderedTransform,
            overviewExtension.ResourceAccess,
            "overview uses ordered text transform")

        Dim choice = host.UiRegistry.ChoiceExtensions.Single()
        Equal(
            ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityMode,
            choice.AnchorId,
            "quality choice anchor")
        Equal(
            ExtFFmpegFreeUIUiChoices.VideoQualityCrf,
            choice.NativeFallbackChoiceId,
            "quality choice native fallback")
        Equal(
            "-crf",
            choice.ValueOverrides(ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityParameterName),
            "quality parameter override")
        IsTrue(
            choice.DisabledAnchors.Contains(ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityValue),
            "native quality value disabled")

        Dim choiceContext As New FakeChoiceContext With {
            .StateJson = New AbAv1PluginState With {.Enabled = False}.Serialize()
        }
        Dim overviewTextBox As New ModernTextBox With {
            .MultiLine = True,
            .Name = AbAv1Plugin.OverviewControlName,
            .Text = "输出容器：.mkv"
        }
        Dim overviewContext As New FakeOverviewUiContext(overviewTextBox) With {
            .StateJson = choiceContext.StateJson
        }
        Dim overviewControl = overviewExtension.CreateControl.Invoke(overviewContext)
        IsTrue(overviewControl Is Nothing, "overview anchor is decorated without an extra control")
        choice.SelectionChanged.Invoke(choiceContext, True)
        IsTrue(AbAv1PluginState.Deserialize(choiceContext.StateJson).Enabled, "choice state persisted")
        IsTrue(choice.RestoreSelection.Invoke(choiceContext), "choice restored from preset state")
        Equal(0, choiceContext.RefreshCount, "choice callback avoids duplicate host refresh")
        IsTrue(
            overviewTextBox.Text.Contains(
                "AB-AV1 目标 VMAF：95",
                StringComparison.Ordinal),
            "choice synchronizes native overview textbox")

        Dim settingsContext As New FakeUiContext With {
            .ProvideNativeStyleTemplates = True,
            .StateJson = choiceContext.StateJson
        }
        Dim settingsControl = settingsExtension.CreateControl.Invoke(settingsContext)
        IsTrue(
            settingsContext.AnchorControl.Controls.OfType(Of Label)().Any(
                Function(label) label.Name = QualityValueStatusAdornment.StatusControlName),
            "ready status decorates native quality row")
        Dim targetEditor = Descendants(settingsControl).OfType(Of ModernTextBox)().Single(
            Function(editor) editor.WaterText = "目标 VMAF")
        targetEditor.Text = "94"
        IsTrue(
            overviewTextBox.Text.Contains(
                "AB-AV1 目标 VMAF：94",
                StringComparison.Ordinal),
            "quality settings synchronize native overview textbox")
        settingsExtension.Cleanup.Invoke(settingsContext)
        IsTrue(settingsControl.IsDisposed, "settings panel cleanup")
        overviewExtension.Cleanup.Invoke(overviewContext)
        Equal("输出容器：.mkv", overviewTextBox.Text, "overview decorator cleanup")

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

        Dim handler = host.PipelineRegistry.Handlers.Single()
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
            "-svtav1-params keyint=240:scd=1:tune=0:crf=31"
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
                .TargetVmaf = 96.5,
                .MinCrf = 10,
                .MaxCrf = 50,
                .Samples = 4,
                .SampleDuration = "15s",
                .Thorough = True,
                .VmafModel = "vmaf_v0.6.1"
            }
            Dim arguments = profile.BuildSearchArguments(inputPath, settings, jsonOutput:=True)
            ContainsPair(arguments, "--encoder", "libsvtav1")
            ContainsPair(arguments, "--preset", "6")
            ContainsPair(arguments, "--pix-format", "yuv420p10le")
            ContainsPair(arguments, "--keyint", "240")
            ContainsPair(arguments, "--scd", "true")
            ContainsPair(arguments, "--svt", "tune=0")
            ContainsPair(arguments, "--enc", "tile-columns=2")
            ContainsPair(arguments, "--min-vmaf", "96.5")
            ContainsPair(arguments, "--samples", "4")
            ContainsPair(arguments, "--sample-duration", "15s")
            ContainsPair(arguments, "--vmaf", "model=version=vmaf_v0.6.1")
            ContainsPair(arguments, "--stdout-format", "json")
            IsTrue(arguments.Contains("--thorough"), "thorough flag")

            Dim updated = JsonNode.Parse(profile.ApplyCrf(27.5)).AsObject()
            Equal(1, updated("视频参数_比特率_控制方式").GetValue(Of Integer)(), "native CRF mode")
            Equal("crf", updated("视频参数_质量控制_参数名").GetValue(Of String)(), "quality parameter")
            Equal("27.5", updated("视频参数_质量控制_值").GetValue(Of String)(), "quality value")
            Dim advanced = updated("视频参数_质量控制_进阶参数集").GetValue(Of String)()
            IsTrue(advanced.Contains("crf=27.5", StringComparison.Ordinal), "embedded SVT CRF replaced")
            IsTrue(updated("插件扩展数据") IsNot Nothing, "plugin state retained")
        Finally
            File.Delete(inputPath)
        End Try
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
                .TargetVmaf = 95,
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
            Equal(95.2, result.Vmaf, "JSON runner VMAF")
            Equal(123456789L, result.PredictedEncodeSize, "JSON runner predicted bytes")
            Equal(321.5, result.PredictedEncodeSeconds, "JSON runner predicted seconds")
            IsTrue(
                messages.Any(Function(item) item.TestedCrf.HasValue AndAlso item.TestedCrf.Value = 30),
                "JSON runner progress")
            IsTrue(runner.CurrentCommandLine.Contains("crf-search", StringComparison.Ordinal), "runner command line")

            Dim cancelSettings As New SearchSettings With {
                .TargetVmaf = 99.999,
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
            ParameterPanelCatalog = New FakeParameterPanelCatalog()
            CommandRegistry = New FakeCommandRegistry()
            UiRegistry = New FakeUiRegistry()
            PipelineRegistry = New FakePipelineRegistry()
            Ui = UiRegistry
            Pipeline = PipelineRegistry
            ParameterPanel = ParameterPanelCatalog
            Commands = CommandRegistry
        End Sub

        Public ReadOnly Property UiRegistry As FakeUiRegistry
        Public ReadOnly Property PipelineRegistry As FakePipelineRegistry
        Public ReadOnly Property ParameterPanelCatalog As FakeParameterPanelCatalog
        Public ReadOnly Property CommandRegistry As FakeCommandRegistry
        Public ReadOnly Property LogMessages As New List(Of String)()

        Public ReadOnly Property ApiVersion As Version = New Version(2, 4, 0) Implements IExtFFmpegFreeUIHost.ApiVersion
        Public ReadOnly Property HostVersion As String = "test-host" Implements IExtFFmpegFreeUIHost.HostVersion
        Public ReadOnly Property Ui As IExtPluginUiRegistry Implements IExtFFmpegFreeUIHost.Ui
        Public ReadOnly Property Pipeline As IExtPluginPipelineRegistry Implements IExtFFmpegFreeUIHost.Pipeline
        Public ReadOnly Property PageEntries As IExtPluginPageEntryRegistry = Nothing Implements IExtFFmpegFreeUIHost.PageEntries
        Public ReadOnly Property EncodingQueueToolbar As IExtPluginEncodingQueueToolbarRegistry = Nothing Implements IExtFFmpegFreeUIHost.EncodingQueueToolbar
        Public ReadOnly Property PluginSettings As IExtPluginSettingsRegistry = Nothing Implements IExtFFmpegFreeUIHost.PluginSettings
        Public ReadOnly Property Behaviors As IExtPluginBehaviorRegistry = Nothing Implements IExtFFmpegFreeUIHost.Behaviors
        Public ReadOnly Property Resources As IExtPluginResourceRegistry = Nothing Implements IExtFFmpegFreeUIHost.Resources
        Public ReadOnly Property ParameterPanel As IExtPluginParameterPanelCatalog Implements IExtFFmpegFreeUIHost.ParameterPanel
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
                ExtFFmpegFreeUIUiAnchors.ParametersVideoQualityValue,
                FakeParameterPanelCatalog.OverviewAnchorId
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

    Private NotInheritable Class FakeParameterPanelCatalog
        Implements IExtPluginParameterPanelCatalog

        Public Const OverviewAnchorId As String = "ext.parameters.control.overview.mtb-parameters-overview"
        Public Const OverviewResourceId As String = "ext.parameters.control-resource.overview.mtb-parameters-overview"

        Private Shared ReadOnly OverviewDescriptor As New ExtPluginParameterControlDescriptor(
            "overview/MTB_参数总览",
            AbAv1Plugin.OverviewPageId,
            "Form_v6_参数面板_参数总览/Panel1/MTB_参数总览",
            AbAv1Plugin.OverviewControlName,
            GetType(ModernTextBox).FullName,
            OverviewAnchorId,
            OverviewResourceId,
            NameOf(Control.Text))

        Public ReadOnly Property AvailablePages As IReadOnlyCollection(Of ExtPluginParameterPageDescriptor) =
            Array.Empty(Of ExtPluginParameterPageDescriptor)() Implements IExtPluginParameterPanelCatalog.AvailablePages

        Public ReadOnly Property AvailableControls As IReadOnlyCollection(Of ExtPluginParameterControlDescriptor) =
            New ExtPluginParameterControlDescriptor() {OverviewDescriptor} Implements IExtPluginParameterPanelCatalog.AvailableControls
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
            New String() {ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare} Implements IExtPluginPipelineRegistry.AvailableStages

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
            AnchorControl.Size = New Size(807, 42)
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

    Private NotInheritable Class FakeOverviewUiContext
        Implements IExtPluginUiContext

        Public Sub New(summaryTextBox As ModernTextBox)
            If summaryTextBox Is Nothing Then Throw New ArgumentNullException(NameOf(summaryTextBox))
            AnchorControl = summaryTextBox
        End Sub

        Public ReadOnly Property PluginId As String = AbAv1Plugin.PluginId Implements IExtPluginUiContext.PluginId
        Public ReadOnly Property ExtensionId As String = "target-vmaf-overview" Implements IExtPluginUiContext.ExtensionId
        Public ReadOnly Property AnchorId As String = FakeParameterPanelCatalog.OverviewAnchorId Implements IExtPluginUiContext.AnchorId
        Public ReadOnly Property SurfaceId As String = "test-surface" Implements IExtPluginUiContext.SurfaceId
        Public ReadOnly Property AnchorControl As Control Implements IExtPluginUiContext.AnchorControl
        Public ReadOnly Property ContainerControl As Control = Nothing Implements IExtPluginUiContext.ContainerControl
        Public Property StateJson As String = "{}" Implements IExtPluginUiContext.StateJson
        Public Event StateRestored As EventHandler Implements IExtPluginUiContext.StateRestored
        Public Property RefreshCount As Integer

        Public Function GetAnchorControl(anchorIdValue As String) As Control Implements IExtPluginUiContext.GetAnchorControl
            If String.Equals(anchorIdValue, AnchorId, StringComparison.OrdinalIgnoreCase) Then Return AnchorControl
            Return Nothing
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
