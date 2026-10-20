using System.IO;
using System.Text.Json;
using FFmpegFreeUI.Ext.PluginSdk;
using VideoEnhancer;

internal static partial class Program
{
    private static async Task TestParameterErrorsAsync()
    {
        System.Console.WriteLine("运行：参数异常隔离与执行拒绝");
        var host=new TestHost();
        using var pipeline=new ExtVideoPipeline(host,()=>Ffmpeg,()=>Ffprobe);
        var missing=new EnhancementSettings{UpscaleEnabled=true};
        string malformed="{\"插件扩展数据\":{\"videoenhancer\":\"{bad\"}}";
        foreach(var preset in new[]{Preset(missing),malformed})
        {
            var preview=new ExtPluginCommandContext{IsPreview=true,InputPath="<输入文件>",PresetJson=preset};
            preview.Arguments.Add(new(ExtPluginCommandArgumentPosition.AfterInput,"-i other-plugin.srt"));
            foreach(var provider in host.CommandsImpl.Parameters)provider.Callback(preview);
            foreach(var provider in host.CommandsImpl.Steps)provider.Callback(preview);
            Check(preview.Steps.Count==0&&preview.Arguments.Single().Text=="-i other-plugin.srt","无效预览不贡献 AI 命令且保留其他插件参数");
            Check(!string.IsNullOrWhiteSpace(EnhancementTaskRegistry.CurrentParameterError),"预览错误由插件发布诊断");
            var context=new ExtPluginPipelineContext{IsPreview=true,InputPath="<输入文件>",PresetJson=preset,CommandLine="-i original.mkv -c copy out.mkv"};
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.CommandBeforeBuild,context);
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.CommandAfterBuild,context);
            Check(context.PresetJson==preset&&context.CommandLine=="-i original.mkv -c copy out.mkv","预览阶段解析失败不会改写原始预设与命令");
            var overview=new ExtPluginPresetOverviewContext("videoenhancer",preset,preset==malformed?"{bad":missing.ToJson());
            foreach(var provider in host.Overview.Rows)provider.Callback(overview);
            Check(overview.Rows.Any(row=>row.Level==ExtPluginPresetOverviewRowLevel.Error),"参数总览直接显示错误行");
        }
        string id="invalid-parameters-"+Guid.NewGuid().ToString("N");
        pipeline.TrackQueuedTask(id,"input.mkv","output.mkv",malformed);
        Check(EnhancementTaskRegistry.Snapshot().Single(item=>item.TaskId==id).Status=="参数无效","损坏的队列预设由插件记录错误而不抛向 UI");
        var actualCommand=new ExtPluginCommandContext{TaskId=id,InputPath="input.mkv",PresetJson=Preset(missing)};
        foreach(var provider in host.CommandsImpl.Parameters)provider.Callback(actualCommand);
        var actual=new ExtPluginPipelineContext{TaskId=id,InputPath="input.mkv",PresetJson=Preset(missing)};
        bool rejected=false;
        try { await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessBeforeStart,actual); }
        catch(InvalidOperationException ex) { rejected=ex.Message.Contains("参数无效"); }
        Check(rejected,"无效真实任务在进程启动前拒绝执行，不能静默跳过 AI");
        var errors=new List<ExtPluginTaskResult>();
        var prepare=new ExtPluginPipelineContext(null,errors.Add){TaskId=id,PresetJson=malformed,InputPath="input.mkv"};
        try { await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,prepare); }
        catch(JsonException) { }
        Check(errors.Any(error=>error.Key=="ai.parameters.error"),"真实任务准备失败记录结构化诊断");
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskAfterFinish,actual);
        pipeline.OnQueueEvent("task.added","{bad");
        Check(videoenhancer.PluginConfig.FromStateJson("{bad").Enabled==false,"无效页面预设不会引起宿主未处理异常");
        var nullable=videoenhancer.PluginConfig.FromStateJson("{\"Backend\":null,\"Model\":null,\"SegmentedVideos\":null,\"AutoCheckUpdates\":false}");
        Check(nullable.Backend=="ncnn"&&nullable.Model==""&&nullable.SegmentedVideos.Count==0&&!nullable.AutoCheckUpdates,"页面使用后端补全的默认值并保留更新偏好");
        using(var page=new videoenhancer.PluginPanel(new(),previewOnly:true,parameterMode:true))
        {
            var context=new TestPageContext{StateJson="{bad"};
            typeof(videoenhancer.PluginPanel).GetMethod("BindPresetContext",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(page,[context]);
            context.Restore("{bad");
            Check(context.StateJson=="{bad","错误预设显示默认页面时保留原始宿主数据");
        }
        var disabled=new ExtPluginCommandContext{IsPreview=true,InputPath="<输入文件>",PresetJson=Preset(new())};
        foreach(var provider in host.CommandsImpl.Parameters)provider.Callback(disabled);
        Check(EnhancementTaskRegistry.CurrentParameterError.Length==0,"修正参数后清除过期预览诊断");
        EnhancementTaskRegistry.CurrentSettingsJson="{}";
    }

    private static void TestParameterPresentation()
    {
        System.Console.WriteLine("运行：精简参数总览与完整命令模板");
        BackendServices.ConfigureRuntime(Path.Combine(_root,"runtime"));
        var host=new TestHost();
        using var pipeline=new ExtVideoPipeline(host,()=>Ffmpeg,()=>Ffprobe);
        string Overview(EnhancementSettings settings)
        {
            var context=new ExtPluginPresetOverviewContext("videoenhancer",Preset(settings),settings.ToJson());
            foreach(var provider in host.Overview.Rows)provider.Callback(context);
            Check(context.Rows.All(row=>row.Level!=ExtPluginPresetOverviewRowLevel.Error),"有效设置可生成参数总览");
            return string.Join(Environment.NewLine,context.Rows.Select(row=>row.Text));
        }
        var settings=new EnhancementSettings{UpscaleEnabled=true,Model="Param-Bin/2x-preview",OutputScale=3,UpscaleTileSize=256,
            UpscaleHalfPrecision=false,InterpModel="未启用补帧模型",InterpFactor=7,RtxHdrMaxLuminance=1700};
        string overview=Overview(settings);
        Check(overview.Contains("2x-preview")&&overview.Contains("3x")&&overview.Contains("256 px")&&overview.Contains("半精度推理：关闭"),"总览保留用户选择的超分参数");
        Check(!overview.Contains("未启用补帧模型")&&!overview.Contains("1700")&&!overview.Contains("处理顺序"),"关闭功能的残留设置和无效顺序不进入总览");
        Check(!overview.Contains("FFV1")&&!overview.Contains("中间视频")&&!overview.Contains("-c:v")&&!overview.Contains("SchemaVersion"),"总览不显示内部流程、命令或原始 JSON");
        Check(Overview(new()).Length==0,"AI 关闭时不附加参数总览");
        settings.OutputScale=0;settings.UpscaleTileSize=0;settings.UpscaleHalfPrecision=true;
        overview=Overview(settings);
        Check(!overview.Contains("分块")&&!overview.Contains("输出倍率")&&!overview.Contains("半精度"),"总览省略未调整的默认附加项");
        string rife=Path.Combine(_root,"runtime","models","RIFE","rife-v4.25");
        Directory.CreateDirectory(rife);
        File.WriteAllText(Path.Combine(rife,"model.param"),"7767517\n0 0\n");
        File.WriteAllBytes(Path.Combine(rife,"model.bin"),[0]);
        settings.InterpEnabled=true;settings.InterpModel="RIFE/rife-v4.25";settings.InterpFactor=2.5;
        settings.InterpHalfPrecision=false;settings.InterpDynamicScaledOpticalFlow=true;settings.SceneDetectThreshold=5.5;settings.ProcessOrder="interp-first";
        overview=Overview(settings);
        Check(overview.Contains("2.5x")&&overview.Contains("5.5")&&overview.Contains("动态光流：开启")&&overview.Contains("先补帧，再超分"),"总览保留调整的补帧和组合顺序参数");
        settings.UpscaleEnabled=false;
        Check(!Overview(settings).Contains("处理顺序"),"只启用补帧时不显示组合顺序");
        var hdr=new EnhancementSettings{RtxHdrEnabled=true,RtxHdrContrast=115,RtxHdrMaxLuminance=1200};
        overview=Overview(hdr);
        Check(overview.Contains("115")&&overview.Contains("1200 nit")&&!overview.Contains("饱和度")&&!overview.Contains("/api/jobs"),"HDR 总览只显示启用状态和调整值");
        var rtx=new EnhancementSettings{UpscaleEnabled=true,Backend="rtxvsr",RtxTarget="1080p",RtxQuality=4,
            RtxHdrEnabled=true,RtxHdrContrast=115,RtxHdrMaxLuminance=1200};
        overview=Overview(rtx);
        Check(overview.Contains("1080p")&&overview.Contains("质量：4")&&!overview.Contains("POST")&&!overview.Contains("--port"),"RTX 总览显示用户设置并隐藏服务协议");
        ExtPluginCommandContext Commands(bool preview)
        {
            var context=new ExtPluginCommandContext{IsPreview=preview,InputPath="<输入文件>",PresetJson=Preset(rtx)};
            foreach(var provider in host.CommandsImpl.Steps)provider.Callback(context);
            return context;
        }
        var preview=Commands(true).Steps.Single(step=>step.Id=="rtx-0");
        const string prefix="# RTX POST /api/jobs：";
        string request=preview.Arguments.Split(Environment.NewLine).Single(line=>line.StartsWith(prefix))[prefix.Length..];
        using var json=JsonDocument.Parse(request);
        Check(preview.Arguments.Contains("--app-session-id")&&preview.Arguments.Contains("-c:v ffv1"),"命令模板显示 RTX 服务启动参数和实际 FFmpeg 参数");
        Check(json.RootElement.GetProperty("processing").GetProperty("hdr").GetProperty("contrast").GetInt32()==115&&
            json.RootElement.GetProperty("output").GetProperty("framePipePath").GetString()!.EndsWith("-source"),"命令模板使用实际 HTTP 请求字段与帧源管道");
        Check(json.RootElement.GetProperty("processing").GetProperty("vsr").GetProperty("scale").GetString()!.Contains("1080p"),"未确定输入尺寸时不把分辨率目标伪装成固定倍率");
        var actual=Commands(false).Steps.Single(step=>step.Id=="rtx-0");
        Check(!actual.Arguments.Contains("# RTX")&&preview.Arguments.StartsWith(actual.Arguments+Environment.NewLine),"协议注释仅用于预览，实际步骤参数不包含注释");
        var segmented=new EnhancementSettings{SegmentedEnabled=true,SegmentedVideos=[new(){Path="视频.mkv",Enabled=true,BoundaryMode="seconds",
            SourceWidth=96,SourceHeight=64,FrameCount=50,DurationSeconds=2,Segments=[new(){Start=1,End=50,StartSeconds=0,EndSeconds=2,Backend="ffmpeg",Model="lanczos",TargetWidth=192,TargetHeight=128}]},
            new(){Path="未启用.mkv",Enabled=false}]};
        overview=Overview(segmented);
        Check(overview.Contains("视频.mkv")&&overview.Contains("0–2 秒")&&overview.Contains("192×128")&&!overview.Contains("未启用.mkv"),"分段总览只显示启用方案的用户范围和目标尺寸");
        Check(!overview.Contains("StartSeconds")&&!overview.Contains("SourceWidth")&&!overview.Contains("通用命令预览"),"分段总览没有原始 JSON 和内部预览说明");
    }
}
