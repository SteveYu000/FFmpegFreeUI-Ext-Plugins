using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using FFmpegFreeUI.Ext.PluginSdk;
using LakeUI;
using VideoEnhancer;

internal static partial class Program
{
    private const BindingFlags Rgb48Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;

    // 仅测试程序提供隔离后端和计划导出，不增加产品 DLL 的命令行入口。
    private static bool RunRgb48ToolFixture(string[] args)
    {
        if (args.Length == 2 && args[0] == "--prepare-rgb48-backend")
        {
            BackendServices.ConfigureRuntime(args[1]);
            typeof(BackendServices).GetMethod("EnsureInterpolationSupportScripts", Rgb48Flags)!.Invoke(null, null);
            return true;
        }
        if (args.Length == 7 && args[0] == "--export-rgb48-plan")
        {
            BackendServices.ConfigureRuntime(args[1]);
            var settings = EnhancementSettings.FromJson(File.ReadAllText(args[2])).ForInput(args[3]);
            typeof(BackendServices).GetMethod("PrepareExtSupport", Rgb48Flags)!.Invoke(null, [settings, args[3]]);
            var media = VideoMediaProbe.ProbeAsync(args[6], args[3], true, CancellationToken.None).GetAwaiter().GetResult();
            var plan = typeof(BackendServices).GetMethod("BuildEnhancementPlan", Rgb48Flags)!.Invoke(null,
                [settings, Preset(settings), args[3], args[4], "", args[5], args[6], media, false])!;
            var steps = (IEnumerable<ExtPluginCommandStep>)plan.GetType().GetField("Steps", Rgb48Flags)!.GetValue(plan)!;
            System.Console.Write(JsonSerializer.Serialize(steps.Select(step => new {
                step.Id, step.ProcessFileName, Arguments = WindowsArguments.Parse(step.Arguments)
            })));
            return true;
        }
        return false;
    }

    private static void TestUpstream1317()
    {
        System.Console.WriteLine("运行：RGB48 同步、独立后端所有权、精度与入口去重");
        SynchronizationContext.SetSynchronizationContext(null);
        var defaults = new EnhancementSettings();
        var config = new videoenhancer.PluginConfig { AutoCheckUpdates = false };
        Check(!defaults.UpscaleHalfPrecision && !defaults.InterpHalfPrecision && defaults.InterpBackend == "cuda"
            && !config.UpscaleHalfPrecision && !config.InterpHalfPrecision && config.InterpBackend == "cuda"
            && (string)typeof(videoenhancer.PluginPanel).GetMethod("InterpBackendValue", Rgb48Flags)!.Invoke(null, [null])! == "cuda",
            "前后端新配置一致采用 FP32 和 CUDA 补帧");
        Check(EnhancementSettings.FromJson("""{"UpscaleHalfPrecision":true,"InterpHalfPrecision":true}""").UpscaleHalfPrecision,
            "已明确保存的半精度选择仍保留");

        var services = typeof(BackendServices);
        string Resolve(string model, string backend, string request) =>
            (string)services.GetMethod("ResolveUpscalePrecision", Rgb48Flags)!.Invoke(null, [model, backend, request])!;
        string Engine(string model, string request) =>
            (string)services.GetMethod("TensorRtPrecisionForModel", Rgb48Flags)!.Invoke(null, [model, request])!;
        foreach (string name in new[] { "AniToon-RPLKSRL-2x.pth", "AniSD-DAT2-2x.onnx", "APISR-GRL-4x.pth" })
        {
            Check(Resolve(name, "tensorrt", "float16") == "float32" && Engine(name, "float16") == "fp32",
                "已知 TensorRT FP16 不稳定模型的推理和构建均使用 FP32：" + name);
        }
        Check(Resolve("Compact-2x.pth", "cuda", "auto") == "float32" && Engine("Compact-2x.pth", "auto") == "fp32",
            "自动精度采用 FP32");
        Check(Resolve("Compact-2x.pth", "tensorrt", "float16") == "float16" && Engine("Compact-2x.pth", "float16") == "fp16",
            "稳定模型仍可显式选择 FP16");
        var validate = services.GetMethod("ValidateExtSettings", Rgb48Flags)!;
        bool rejected = false;
        try { validate.Invoke(null, [new EnhancementSettings { InterpEnabled = true, InterpBackend = "ncnn" }]); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException inner)
        { rejected = inner.Message.Contains("8bit") && inner.Message.Contains("TensorRT"); }
        Check(rejected, "旧预设选择 NCNN 补帧时返回明确诊断，避免 8bit 降级");

        string runtime = Path.Combine(_root, "rgb48-sync"), backend = Path.Combine(runtime, "python", "backend");
        Directory.CreateDirectory(Path.Combine(backend, "src"));
        File.WriteAllText(Path.Combine(backend, "rve-backend.py"), "# 原始后端\n");
        File.WriteAllText(Path.Combine(backend, "src", "version.py"), "version = '2.4.1-dev17'\n");
        BackendServices.ConfigureRuntime(runtime);
        void Sync() => services.GetMethod("EnsureInterpolationSupportScripts", Rgb48Flags)!.Invoke(null, null);
        Dictionary<string, (string Hash, DateTime Time)> Snapshot() =>
            Directory.EnumerateFiles(backend, "*.py", SearchOption.AllDirectories).ToDictionary(
                file => file, file => (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), File.GetLastWriteTimeUtc(file)));
        Sync();
        Check(File.ReadAllText(Path.Combine(backend, ".videoenhancer-rgb48-owner")).Trim() == "ext",
            "匹配基线的旧后端由 Ext 管理 RGB48 修订");
        Check(File.ReadAllText(Path.Combine(backend, "src", "utils", "PixelFormat.py")).Contains("rgb48-v1")
            && File.ReadAllText(Path.Combine(backend, "rve-segmented-backend.py")).Contains("rgb48le")
            && File.Exists(Path.Combine(backend, "rve-frame-backend.py")),
            "RGB48 核心和 Ext 分段入口完整安装");
        Check(File.ReadAllText(Path.Combine(backend, "src", "pytorch", "UpscaleTorch.py")).Contains("torch.jit.load(engine_file"),
            "同步核心保留 Ext 的中文 Engine 文件流加载");
        var first = Snapshot();
        Sync();
        Check(first.All(pair => Snapshot().TryGetValue(pair.Key, out var item) && item == pair.Value),
            "重复同步不改写相同 Python 文件及修改时间");

        File.AppendAllText(Path.Combine(backend, "rve-ordered-backend.py"), "# 独立包自有修订\n");
        File.WriteAllText(Path.Combine(backend, "rve-image-backend.py"), "# 独立包配套入口\n");
        File.WriteAllText(Path.Combine(backend, ".videoenhancer-rgb48-owner"), "external\n");
        var external = Snapshot();
        Sync();
        Check(external.All(pair => Snapshot().TryGetValue(pair.Key, out var item) && item == pair.Value),
            "独立高位深后端的全部 Python 源码保持不变");
        foreach (string file in new[] { "rve-ordered-backend.py", "rve-segmented-backend.py" })
            Check((string)services.GetMethod("ExtToolPath", Rgb48Flags)!.Invoke(null, [file])! == Path.Combine(backend, file),
                "顺序和分段使用独立后端自己的入口：" + file);
        File.WriteAllText(Path.Combine(backend, "src", "utils", "PixelFormat.py"), "FRAME_PROTOCOL = 'unsupported'\n");
        var invalid = Snapshot();
        rejected = false;
        try { Sync(); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { rejected = true; }
        Check(rejected && invalid.All(pair => Snapshot().TryGetValue(pair.Key, out var item) && item == pair.Value),
            "未知帧协议在写入前拒绝，保留全部外部文件");

        string unknown = Path.Combine(_root, "rgb48-unknown", "python", "backend");
        Directory.CreateDirectory(Path.Combine(unknown, "src"));
        File.WriteAllText(Path.Combine(unknown, "rve-backend.py"), "# 不支持的基线\n");
        File.WriteAllText(Path.Combine(unknown, "src", "version.py"), "version = 'other'\n");
        BackendServices.ConfigureRuntime(Path.Combine(_root, "rgb48-unknown"));
        rejected = false;
        try { Sync(); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(unknown, ".videoenhancer-rgb48-owner"))
            && File.ReadAllText(Path.Combine(unknown, "rve-backend.py")) == "# 不支持的基线\n",
            "未知 RVE 版本不强行覆盖核心");
        services.GetMethod("PrepareExtSupport", Rgb48Flags)!.Invoke(null,
            [new EnhancementSettings { UpscaleEnabled = true, Backend = "rtxvsr" }, "input.mkv"]);
        Check(true, "纯 RTX 任务不因无关 RVE 版本无法同步而失败");
        BackendServices.ConfigureRuntime(Path.Combine(_root, "runtime"));

        using var page = new videoenhancer.PluginPanel(config, previewOnly: true, parameterMode: true);
        var panelType = typeof(videoenhancer.PluginPanel);
        panelType.GetField("_uiReady", Rgb48Flags)!.SetValue(page, false);
        IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                yield return control;
                foreach (Control child in Descendants(control)) yield return child;
            }
        }
        var hints = Descendants(page).OfType<HtmlColorLabel>().Where(label => label.Text == "半精度推理").ToArray();
        Check(hints.Length == 2 && hints.All(label => label.InfoIconPosition == HtmlColorLabel.InfoIconPositionEnum.End
            && label.ToolTipText.Contains("FP32") && label.ToolTipText.Contains("宿主预设")),
            "两处半精度控件使用原生信息图标，说明精度和宿主边界");
        panelType.GetMethod("ConfigureDownloadList", Rgb48Flags)!.Invoke(page, null);
        var render = panelType.GetMethod("RenderDownloadModels", Rgb48Flags)!;
        string state = JsonSerializer.Serialize(new { state = "not-installed", installedVersion = "",
            latestVersion = "2026.11.09.2", mode = "full", downloadSize = 12L, fullSize = 12L });
        foreach (string json in new[] { "[]",
            """[{"name":"后端旧包","path":"Backend/python_20261108.7z","size":1},{"name":"后端新包","path":"Backend/python_20261109.7z","size":2}]""" })
        {
            render.Invoke(page, [json, "", 0, state, 0]);
            var list = (UltraDetailListView)panelType.GetField("_downloadList", Rgb48Flags)!.GetValue(page)!;
            var rows = list.Items.Cast<UltraDetailListView.ListItem>().Where(item => item.GroupName == "Backend").ToArray();
            Check(rows.Length == 1 && rows[0].SubItems[0].Text.Contains("2026.11.09.2"),
                "没有归档或存在多个历史后端包时均只显示一条真实更新状态");
        }
        SynchronizationContext.SetSynchronizationContext(null);
    }
}
