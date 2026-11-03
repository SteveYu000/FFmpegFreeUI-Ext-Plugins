using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using LakeUI;
using VideoEnhancer;

internal static partial class Program
{
    private static void TestUpstream1315()
    {
        System.Console.WriteLine("运行：上游 1.3.15 ONNX TensorRT 与 HDR 保存");
        SynchronizationContext.SetSynchronizationContext(null);
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Static|BindingFlags.Instance;
        string runtime=videoenhancer.PluginConfig.ApplicationRoot,models=Path.Combine(runtime,"models");
        BackendServices.ConfigureRuntime(runtime);
        string CreateModel(string relative)
        {
            string path=Path.Combine(models,relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path,[1,2,3]);
            return path;
        }
        string builtin=CreateModel("ONNX/RealHatGAN-JP-Illustration-2x-fix1.onnx");
        string user=CreateModel("User/SR/HAT/用户 模型/model.onnx");
        string userCatalog=Path.Combine(models,"User","model-catalog.json");
        File.WriteAllText(userCatalog,JsonSerializer.Serialize(new {schemaVersion=1,models=new[]{
            new {id="onnx-1315",displayName="用户约束模型",relativePath="User/SR/HAT/用户 模型/model.onnx",
                task="upscale",architecture="HAT",purpose="SR",format="onnx",scale=2,inputMultiple=32,
                sha256="",size=3,importedAtUtc="",backends=new[]{"onnx","tensorrt"}}}}));
        var catalog=Capture(new(BackendAction.ListModelCatalog){Backend="tensorrt"});
        Check(catalog.Code==0,"TensorRT 模型清单可通过 DLL 服务读取");
        using(var document=JsonDocument.Parse(catalog.Output))
        {
            var entries=document.RootElement.EnumerateArray().ToArray();
            var hat=entries.Single(item=>item.GetProperty("relativePath").GetString()=="ONNX/RealHatGAN-JP-Illustration-2x-fix1.onnx");
            Check(hat.GetProperty("inputMultiple").GetInt32()==16&&
                hat.GetProperty("backends").EnumerateArray().Select(item=>item.GetString()).Contains("onnx"),
                "TensorRT 显示 ONNX 模型，同时保留 ONNX Runtime 与 HAT 输入倍数");
            Check(entries.Single(item=>item.GetProperty("source").GetString()=="user").GetProperty("inputMultiple").GetInt32()==32,
                "TensorRT 清单包含已登记用户 ONNX 的尺寸约束");
        }
        using(var document=JsonDocument.Parse(Capture(new(BackendAction.ListModelCatalog){Backend="onnx"}).Output))
            Check(document.RootElement.EnumerateArray().Any(item=>item.GetProperty("relativePath").GetString()!.EndsWith("model.onnx")),
                "ONNX Runtime 入口继续列出用户模型");

        System.Console.WriteLine("运行：ONNX 手动转换参数与缓存");
        var panelType=typeof(videoenhancer.PluginPanel);
        var readMultiple=panelType.GetMethod("ReadConverterInputMultiple",flags)!;
        foreach(var (path,expected) in new[]{(builtin,16),(user,32),(Path.Combine(models,"未登记 模型.onnx"),1)})
            Check((int)readMultiple.Invoke(null,[path])! == expected,"手动转换复用 DLL 能力清单："+expected);
        var createStart=panelType.GetMethod("CreateTensorRtConversionStartInfo",flags)!;
        string output=Path.Combine(runtime,"Engine 输出");
        var start=(ProcessStartInfo)createStart.Invoke(null,[Environment.ProcessPath!,"--echo-converter-args",user,output,32])!;
        start.WorkingDirectory=AppContext.BaseDirectory;
        using(var child=Process.Start(start)!)
        {
            string stdout=child.StandardOutput.ReadToEnd(),stderr=child.StandardError.ReadToEnd();
            child.WaitForExit();
            var actual=JsonSerializer.Deserialize<string[]>(stdout);
            Check(child.ExitCode==0&&actual!.SequenceEqual(new[]{user,"--output-dir",output,"--input-multiple","32"}),
                "实际转换子进程收到独立的中文路径及 ONNX 尺寸参数："+stderr);
        }
        var pthStart=(ProcessStartInfo)createStart.Invoke(null,[Environment.ProcessPath!,"--echo-converter-args","权重.pth",output,32])!;
        Check(!pthStart.ArgumentList.Contains("--input-multiple"),"PTH 转换不混入 ONNX 专用参数");
        var accept=panelType.GetMethod("IsTensorRtSourceExtension",flags)!;
        Check((bool)accept.Invoke(null,["模型.ONNX"])!&&!(bool)accept.Invoke(null,["模型.rar"])!,
            "转换入口接受 ONNX，不恢复压缩包直接转换");

        var runtimeType=typeof(BackendServices).GetNestedType("TensorRtRuntime",flags)!;
        var device=Activator.CreateInstance(runtimeType,flags,null,["GPU","TRT","Torch-TRT"],null)!;
        var cacheKey=typeof(BackendServices).GetMethod("BuildTensorRtCachePath",flags)!;
        string Cache()=>(string)cacheKey.Invoke(null,[user,device,64,48,16,2,"fp32"])!;
        string first=Cache();
        var update=Capture(new(BackendAction.UpdateUserModel){ModelId="onnx-1315",Capabilities=new("HAT","SR",2,64,["onnx","tensorrt"])});
        Check(update.Code==0,"用户 ONNX 能力允许明确选择 TensorRT");
        Check(first!=Cache(),"修改输入尺寸倍数后不复用旧 ONNX Engine 缓存");

        var settings=new EnhancementSettings{UpscaleEnabled=true,Backend="tensorrt",Model=user,UpscaleTileSize=16};
        var media=new VideoMediaInfo([new(0,64,48,24,"24/1",48,2,0,false,8,0,"","","","1:1")],2);
        var plan=typeof(BackendServices).GetMethod("BuildEnhancementPlan",flags)!.Invoke(null,
            [settings,Preset(settings),"source.mkv",Path.Combine(runtime,"plan"),"pause",Ffmpeg,Ffprobe,media,false])!;
        var steps=(IEnumerable<FFmpegFreeUI.Ext.PluginSdk.ExtPluginCommandStep>)plan.GetType().GetField("Steps",flags)!.GetValue(plan)!;
        var args=WindowsArguments.Parse(steps.Last().Arguments).ToList();
        Check(args[args.IndexOf("--input-multiple")+1]=="64"&&args[args.IndexOf("-b")+1]=="tensorrt",
            "Ext 计划把用户约束和明确的 TensorRT 后端传给 RVE");
        Check(args[args.IndexOf("--custom_encoder")+1].Contains("-c:v ffv1")&&!steps.Any(step=>step.Arguments.Contains("libsvtav1")),
            "AI 步骤保留无损中间视频，不注入上游的最终编码兜底");

        System.Console.WriteLine("运行：HDR 保存故障回滚");
        TestHdrSave1315();
        BackendServices.ConfigureRuntime(Path.Combine(_root,"runtime"));
        SynchronizationContext.SetSynchronizationContext(null);
    }

    private static void TestHdrSave1315()
    {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var config=new videoenhancer.PluginConfig{AutoCheckUpdates=false};
        int notifications=0;
        config.Saved+=(_,_)=>notifications++;
        using var page=new videoenhancer.PluginPanel(config,previewOnly:true,parameterMode:true);
        // 故障逻辑测试不显示模态消息框，窗口交互另行验证。
        typeof(videoenhancer.PluginPanel).GetField("_uiReady",flags)!.SetValue(page,false);
        var context=new TestPageContext();
        typeof(videoenhancer.PluginPanel).GetMethod("BindPresetContext",flags)!.Invoke(page,[context]);
        notifications=0;
        config.Save();
        Check(notifications==1,"一次配置保存只发出一次成功通知");
        string path=videoenhancer.PluginConfig.ConfigPath;
        var toggle=(LakeUI.BooleanSwitch)typeof(videoenhancer.PluginPanel).GetField("_switchRtxHdr",flags)!.GetValue(page)!;
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))
        {
            toggle.Checked=true;
            Check(!config.RtxHdrEnabled&&!toggle.Checked&&!EnhancementSettings.FromJson(context.StateJson).RtxHdrEnabled,
                "配置被占用时 HDR 开关、内存参数及宿主预设一并保持关闭");
            Check(notifications==1&&!File.Exists(path+".new"),"保存失败不通知宿主成功，并清理临时文件");
        }
        toggle.Checked=true;
        string queued=context.StateJson;
        Check(config.RtxHdrEnabled&&toggle.Checked&&EnhancementSettings.FromJson(queued).RtxHdrEnabled&&
            JsonSerializer.Deserialize<videoenhancer.PluginConfig>(File.ReadAllText(path))!.RtxHdrEnabled,
            "解除占用后 HDR 同步保存到配置与宿主预设");
        toggle.Checked=false;
        Check(!EnhancementSettings.FromJson(context.StateJson).RtxHdrEnabled&&EnhancementSettings.FromJson(queued).RtxHdrEnabled,
            "后续关闭 HDR 不改变已经取得的入队参数快照");
        EventHandler failure=(_,_)=>throw new IOException("预设通知失败");
        config.Saved+=failure;
        try
        {
            toggle.Checked=true;
            Check(!config.RtxHdrEnabled&&!toggle.Checked&&!EnhancementSettings.FromJson(context.StateJson).RtxHdrEnabled&&
                !JsonSerializer.Deserialize<videoenhancer.PluginConfig>(File.ReadAllText(path))!.RtxHdrEnabled,
                "宿主预设通知失败也由插件处理，并恢复 HDR 的界面、预设和磁盘值");
        }
        finally{config.Saved-=failure;}
        EnhancementTaskRegistry.CurrentParameterError="";
        EnhancementTaskRegistry.CurrentSettingsJson="{}";
    }
}
