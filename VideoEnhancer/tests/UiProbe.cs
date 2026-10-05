using System.Drawing;
using System.Drawing.Drawing2D;
using LakeUI;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Forms;

internal static partial class Program
{
    private static int RunUiProbe()
    {
        string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".test-tools", "ui-probe-data"));
        Directory.CreateDirectory(fixture);
        CreateInstalledModelFixtures(Path.Combine(fixture, "models"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => File.WriteAllText(Path.Combine(fixture, "ui-probe-error.txt"), e.ExceptionObject.ToString());
        Environment.SetEnvironmentVariable("VIDEOENHANCER_ROOT", fixture);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var context = new AssemblyLoadContext("ui-probe-merged", true);
        context.Resolving += (load, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == name.Name) ??
            (File.Exists(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll")) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll")) : null);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(fixture, "..", "..", "dist", "videoenhancer.ext.3fui.dll")));
        assembly.GetType("VideoEnhancer.BackendServices", true)!.GetMethod("ConfigureRuntime")!.Invoke(null, [fixture]);
        var configType = assembly.GetType("videoenhancer.PluginConfig", true)!;
        var panelType = assembly.GetType("videoenhancer.PluginPanel", true)!;
        object Config()
        {
            var config = Activator.CreateInstance(configType)!;
            configType.GetProperty("AutoCheckUpdates")!.SetValue(config, false);
            return config;
        }
        var tools = (Control)Activator.CreateInstance(panelType, Config(), true, false)!;
        var parameters = (Control)Activator.CreateInstance(panelType, Config(), true, true)!;
        using var form = new Form { Text = "VideoEnhancer UI Probe", AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96), ClientSize = new Size(1400, 860), MinimumSize = new Size(840, 620),
            BackColor = Color.FromArgb(24, 26, 30), Font = new Font("Microsoft YaHei UI", 10) };
        // 彩色背景能直接暴露不透明黑底，模拟宿主对根面板的背景映射。
        using var backdrop = new Bitmap(1400, 860);
        using (var graphics = Graphics.FromImage(backdrop))
        using (var brush = new LinearGradientBrush(new Rectangle(0, 0, 1400, 860), Color.FromArgb(35, 92, 135), Color.FromArgb(125, 63, 87), 25f))
        {
            graphics.FillRectangle(brush, 0, 0, 1400, 860);
            graphics.FillEllipse(Brushes.SteelBlue, 50, 400, 700, 700);
            graphics.FillEllipse(Brushes.DarkGoldenrod, 950, -80, 420, 420);
        }
        using var chrome = new ThisIsYourWindow { BackdropMode = ThisIsYourWindow.BackdropModeEnum.Image,
            BackdropImage = backdrop, BackdropFirstWindowOnly = false, BackdropTintColor = Color.FromArgb(125, 0, 0, 0),
            BackdropTintInactiveColor = Color.FromArgb(125, 0, 0, 0), CaptionHeight = 34,
            CaptionBackColor = Color.Transparent, CaptionInactiveBackColor = Color.Transparent };
        foreach (var panel in new[] { tools, parameters })
        {
            var root = (ModernPanel)panelType.GetField("ModernPanel1", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
            root.BackgroundSource = form;
        }
        chrome.Attach(form);
        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(tools);
        parameters.Visible = false;
        content.Controls.Add(parameters);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, BackColor = Color.FromArgb(45, 48, 54) };
        void Button(string text, Action callback)
        {
            var button = new Button { Text = text, Width = 160, Height = 34, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            button.Click += (_, _) => callback();
            actions.Controls.Add(button);
        }
        Button("首页", () => { parameters.Visible = false; tools.Visible = true; tools.BringToFront(); });
        Button("AI 参数", () => { tools.Visible = false; parameters.Visible = true; parameters.BringToFront(); });
        Button("分段编辑", () => panelType.GetMethod("OpenSegmentedEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(parameters, null));
        Button("使用教程", () => {
            parameters.Visible = false; tools.Visible = true; tools.BringToFront();
            var tabs = panelType.GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tools)!;
            tabs.GetType().GetProperty("SelectedIndex")!.SetValue(tabs,
                (int)panelType.GetField("_tabIndexTutorial", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tools)!);
            var page = panelType.GetField("_pageTutorial", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tools)!;
            panelType.GetMethod("EnsureMarkdownPage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(tools, [page]);
        });
        form.Controls.Add(content);
        form.Controls.Add(actions);
        Application.Run(form);
        context.Unload();
        return 0;
    }
}
