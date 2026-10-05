using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FFmpegFreeUI.Ext.PluginSdk;
using VideoEnhancer;

internal static partial class Program
{
    private static int _checks;
    private static string _root="";
    private static readonly string Ffmpeg=Environment.GetEnvironmentVariable("VIDEOENHANCER_TEST_FFMPEG")??"ffmpeg.exe";
    private static readonly string Ffprobe=Environment.GetEnvironmentVariable("VIDEOENHANCER_TEST_FFPROBE")??"ffprobe.exe";
    [STAThread]
    static int Main(string[] args)
    {
        if(args.Length>0&&args[0]=="--port")return RtxSidecarFixture.Run(args).GetAwaiter().GetResult();
        if(args.Length>1&&Path.GetFileName(args[0])=="inspect_upscale_models.py"&&Environment.GetEnvironmentVariable("VIDEOENHANCER_TEST_SERVICE_CHILD") is {} marker)
        {
            File.WriteAllText(marker,Environment.ProcessId.ToString());
            Thread.Sleep(TimeSpan.FromSeconds(30));
            return 0;
        }
        _root=Path.Combine(Path.GetTempPath(),"VideoEnhancer-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("VIDEOENHANCER_ROOT",Path.Combine(_root,"runtime"));
        try
        {
            System.Console.WriteLine("运行：TestArguments");
            TestArguments();
            System.Console.WriteLine("运行：TestBinder");
            TestBinder();
            System.Console.WriteLine("运行：TestPreset");
            TestPreset();
            System.Console.WriteLine("运行：TestFrontend");
            TestFrontend();
            SynchronizationContext.SetSynchronizationContext(null);
            System.Console.WriteLine("运行：TestMergedAssembly");
            TestMergedAssembly();
            System.Console.WriteLine("运行：TestNativePreview");
            TestNativePreview();
            TestRveLicenseNotices();
            System.Console.WriteLine("运行：TestRvePreview");
            TestRvePreview();
            TestManagementServices();
            TestModelRemoval();
            TestSegmentFallback();
            System.Console.WriteLine("运行：TestMediaPipelineAsync");
            TestMediaPipelineAsync().GetAwaiter().GetResult();
            System.Console.WriteLine($"PASS {_checks} 个断言：预设、界面组件、单 DLL、媒体链、错误处理与清理");
            return 0;
        }
        catch(Exception ex){System.Console.Error.WriteLine(ex);return 1;}
        finally
        {
            // 测试只清理本次生成的临时目录。
            if(Path.GetFullPath(_root).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))
                try{Directory.Delete(_root,true);}catch(IOException){}
        }
    }
    private static void Check(bool success,string message)
    {
        if(!success)throw new InvalidOperationException("失败："+message);
        _checks++;
    }
    private static void TestArguments()
    {
        string[] tokens=["","C:\\文件 目录\\","a\\\"b","--custom_encoder","-vf scale=128:72,setsar=1","C:\\路径\\video.mkv","a'b","x\"y","-1"];
        Check(WindowsArguments.Parse(WindowsArguments.Join(tokens)).SequenceEqual(tokens),"Windows 参数转义往返");
    }
    private static void TestBinder()
    {
        string ai=@"C:\输出目录\enhanced.mkv";
        var videos=new Dictionary<int,string>{{0,ai}};
        string result=NativeVideoBinder.Bind(WindowsArguments.Join(["-i","原始.mkv","-i","cover.jpg","-i","外挂.srt","-i",ai,"-map","VE_AI_VIDEO:v:0?","-map","0:a?","-map","2:s:0","-map_metadata","0","out.mkv"]),videos,1);
        var parsed=WindowsArguments.Parse(result);
        Check(parsed.Contains("3:v:0?"),"额外封面和字幕后动态分配 AI 输入编号");
        Check(parsed.Contains("0:a?")&&parsed.Contains("2:s:0"),"原始音频及外挂字幕映射不变");
        Check(parsed.Contains("原始.mkv"),"原始输入保持索引 0");
        result=NativeVideoBinder.Bind(WindowsArguments.Join(["-i","原始.mkv","-i",ai,"-filter_complex","[VE_AI_VIDEO:v:0]scale=128:72[out];[0:v:1]null[other]","-map","[out]","-map","0:v?","-map","-VE_AI_VIDEO:v:0?","out.mkv"]),videos,2);
        parsed=WindowsArguments.Parse(result);
        Check(parsed.Contains("[1:v:0]scale=128:72[out];[0:v:1]null[other]"),"滤镜只绑定本插件的标记来源");
        Check(parsed.Contains("-0:v:0?")&&parsed.Contains("0:v?"),"保留其他视频时负向映射排除原选中视频");
        result=NativeVideoBinder.Bind(WindowsArguments.Join(["-i","原始.mkv","-i",ai,"-map","0:v?","-c:v:0","libx264","out.mkv"]),videos,2);
        parsed=WindowsArguments.Parse(result);
        Check(parsed.Contains("1:v:0?")&&parsed.Contains("0:v:1?"),"没有滤镜时保留全部视频，逐个替换选中流");
        result=NativeVideoBinder.CopyInputTiming(WindowsArguments.Join(["-ss","0.5","-hwaccel","cuda","-i","原始.mkv","-i",ai,"out.mkv"]),ai,0.1);
        parsed=WindowsArguments.Parse(result);
        Check(parsed.Count(token=>token=="-ss")==2&&parsed.Count(token=>token=="-hwaccel")==1,"同步输入裁剪，保留独立解码选项");
        Check(parsed.Contains("0.4")&&!parsed.Contains("-itsoffset"),"裁剪增强输入时扣除原始视频首帧偏移");
        bool rejected=false;try{NativeVideoBinder.Bind("-i source -i "+WindowsArguments.Quote(ai)+" -map 0:0 out.mkv",videos,1);}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"拒绝无法确认视频选择的完整自写命令");
        var marked=JsonNode.Parse(NativeVideoBinder.MarkPreset("""{"流控制_将视频参数应用于指定流":["0:v:1"],"插件扩展数据":{"other":{"value":123}},"音频参数_编码器_代号":"aac"}"""))!;
        Check(marked["插件扩展数据"]!["other"]!["value"]!.GetValue<int>()==123,"局部命令快照保留其他插件状态");
        Check(marked["音频参数_编码器_代号"]!.GetValue<string>()=="aac","局部命令快照保留音频设置");
    }
    private static void TestPreset()
    {
        var settings=new EnhancementSettings{UpscaleEnabled=true,Model="Param-Bin/测试2x",InterpEnabled=true,InterpFactor=2.5,
            RtxHdrMaxLuminance=1200,SegmentedEnabled=true,SegmentedVideos=[new(){Path="视频.mkv",Enabled=true,Segments=[new(){Start=1,End=50,Backend="ncnn",Model="模型"}]}]};
        var copy=EnhancementSettings.FromJson(settings.ToJson());
        Check(copy.UpscaleEnabled&&copy.InterpFactor==2.5&&copy.RtxHdrMaxLuminance==1200,"任务参数预设往返");
        Check(copy.SegmentedVideos[0].Segments[0].End==50,"分段方案随预设恢复");
        string preset=Preset(settings);
        Check(EnhancementSettings.FromJson(EnhancementSettings.FromPreset(preset,"videoenhancer")).Model==settings.Model,"按插件 ID 获取预设快照");
        var data=JsonNode.Parse(preset)!;
        Check(data["插件扩展数据"]!["other-plugin"]!["keep"]!.GetValue<bool>(),"其他插件数据保持");
        settings.Model="后来改的模型";
        Check(EnhancementSettings.FromJson(EnhancementSettings.FromPreset(preset,"videoenhancer")).Model=="Param-Bin/测试2x","排队参数与当前面板隔离");
    }
    private static void TestFrontend()
    {
        var host=new TestHost();
        var plugin=new videoenhancer.Entry();
        plugin.Initialize(host);
        Check(host.Pages.Pages.Count==2,"注册两个独立导航入口");
        var parameters=host.Pages.Pages.Single(page=>page.Title=="视频参数 | AI增强");
        Check(parameters.TargetId==ExtFFmpegFreeUIPageTargets.ParametersVideoFrame&&parameters.Position==ExtPluginRelativePosition.After,"AI 入口位于画面帧之后");
        var tools=host.Pages.Pages.Single(page=>page.Title=="视频超分");
        Check(tools.TargetId==ExtFFmpegFreeUIPageTargets.MainParameters,"工具入口位于主导航参数面板之后");
        using var page=new videoenhancer.PluginPanel(new(),previewOnly:true,parameterMode:true);
        var context=new TestPageContext();
        typeof(videoenhancer.PluginPanel).GetMethod("BindPresetContext",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(page,[context]);
        var switchControl=typeof(videoenhancer.PluginPanel).GetField("_switchUpscale",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(page)!;
        switchControl.GetType().GetProperty("Checked")!.SetValue(switchControl,true);
        Check(EnhancementSettings.FromJson(context.StateJson).UpscaleEnabled,"没有总开关也能直接启用超分并保存预设");
        Check(context.RefreshCount>0,"参数改动通过正式接口请求总览刷新");
        context.Restore(new EnhancementSettings{InterpEnabled=true,InterpFactor=4}.ToJson());
        var interp=typeof(videoenhancer.PluginPanel).GetField("_switchInterp",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(page)!;
        Check((bool)interp.GetType().GetProperty("Checked")!.GetValue(interp)!,"恢复预设更新功能开关");
        using var bitmap=new System.Drawing.Bitmap(2,2);
        bitmap.SetPixel(1,0,System.Drawing.Color.FromArgb(128,80,120,240));
        var pictureFactory=typeof(videoenhancer.Entry).Assembly.GetType("videoenhancer.PreviewPictureSource",true)!;
        var source=(LakeUI.IPixelPictureSource)pictureFactory.GetMethod("Create",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[bitmap])!;
        Check(source.Width==2&&source.Height==2&&source.ColorSpace==LakeUI.PixelPictureColorSpace.SRgb&&!source.IsHdr,"预览图像源保留尺寸和 SDR 色彩声明");
        LakeUI.PixelPicturePixel pixel=default;
        Check(source.TryGetPixel(1,0,0,ref pixel)&&Math.Abs(pixel.Red-80f/255)<0.00001f&&Math.Abs(pixel.Blue-240f/255)<0.00001f&&Math.Abs(pixel.Alpha-128f/255)<0.00001f,"预览源返回原帧 RGBA 像素");
        Check(!source.TryGetPixel(-1,0,0,ref pixel)&&!source.TryGetPixel(2,0,0,ref pixel),"预览像素查询拒绝越界坐标");
        using var picture=new LakeUI.PixelPictureBox{Source=source};
        Check(picture.ImageWidth==2&&picture.ImageHeight==2,"LakeUI 5.112 通过 Source 接收预览帧");
        picture.Source=null;
        pictureFactory.GetMethod("Release",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[bitmap]);
        Check(!source.TryGetPixel(0,0,0,ref pixel),"释放后的预览帧不会继续读取 GDI 资源");
        using var toolPage=new videoenhancer.PluginPanel(new(),previewOnly:true);
        var tabs=typeof(videoenhancer.PluginPanel).GetField("_tabs",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(toolPage)!;
        var items=(System.Collections.IEnumerable)tabs.GetType().GetProperty("Items")!.GetValue(tabs)!;
        var titles=items.Cast<object>().Select(item=>item.GetType().GetProperty("Text")!.GetValue(item)?.ToString()).ToArray();
        Check(tabs.GetType().Name=="ModernTabListControl"&&titles[0]=="首页","工具使用竖排二级导航且首页在最上方");
        Check(!titles.Any(title=>title is "超分工作台" or "图片超分" or "右键超分" or "分段超分"),"迁移或移除的旧页面不再出现在工具导航");
    }
    private static void TestMergedAssembly()
    {
        string merged=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..","dist","videoenhancer.3fui.dll"));
        if(!File.Exists(merged))throw new FileNotFoundException("缺少合并后的 DLL",merged);
        var context=new AssemblyLoadContext("merged-plugin-test",true);
        context.Resolving+=(load,name)=>AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly=>assembly.GetName().Name==name.Name);
        var assembly=context.LoadFromAssemblyPath(merged);
        Check(assembly.EntryPoint is null,"合并产物为 DLL，没有 CLI 入口");
        Check(assembly.GetType("VideoEnhancer.BackendServices") is not null&&assembly.GetType("videoenhancer.Entry") is not null,"同一 DLL 同时包含前端和后端");
        Check(!assembly.GetReferencedAssemblies().Any(reference=>reference.Name is "VideoEnhancer.Backend" or "SharpCompress" or "FFmpegFreeUI"),"没有私有依赖 DLL 或宿主主程序集引用");
        Check(assembly.GetManifestResourceNames().Contains("VideoEnhancer.Embedded.rve-ext-launch.py"),"真实 RVE 启动资源包含在 DLL 中");
        var services=assembly.GetType("VideoEnhancer.BackendServices")!;
        Check((string)services.GetProperty("Version")!.GetValue(null)! == "0.1.0","合并 DLL 的版本从 0.1.0 开始");
        Check(assembly.GetType("VideoEnhancer.ServiceRequestParser") is null&&assembly.GetType("VideoEnhancer.ServiceOptions") is null&&
            assembly.GetType("VideoEnhancer.BackendOperation") is null,"合并 DLL 没有旧 CLI 参数解析和伪进程类型");
        services.GetMethod("ConfigureRuntime")!.Invoke(null,[Path.Combine(_root,"merged-runtime")]);
        var requestType=assembly.GetType("VideoEnhancer.BackendRequest")!;
        var actionType=assembly.GetType("VideoEnhancer.BackendAction")!;
        var jobType=assembly.GetType("VideoEnhancer.BackendServiceJob")!;
        var request=Activator.CreateInstance(requestType,Enum.Parse(actionType,"ListUserModels"))!;
        using var operation=(IDisposable)jobType.GetMethod("Start",[requestType,typeof(CancellationToken)])!.Invoke(null,[request,CancellationToken.None])!;
        using var reader=(StreamReader)jobType.GetProperty("Output")!.GetValue(operation)!;
        jobType.GetMethod("Wait",Type.EmptyTypes)!.Invoke(operation,null);
        using var result=JsonDocument.Parse(reader.ReadToEnd());
        Check((int)jobType.GetProperty("ResultCode")!.GetValue(operation)! == 0&&result.RootElement.GetArrayLength()==0,"合并 DLL 通过类型化请求直接读取模型目录");
        context.Unload();
    }
    private static async Task TestMediaPipelineAsync()
    {
        string input=Path.Combine(_root,"原始 输入.mkv"),output=Path.Combine(_root,"完成.mkv");
        string subtitle=Path.Combine(_root,"字幕.srt"),metadata=Path.Combine(_root,"元数据.txt");
        await File.WriteAllTextAsync(subtitle,"1\n00:00:00,100 --> 00:00:01,800\noriginal subtitle\n",new UTF8Encoding(false));
        await File.WriteAllTextAsync(metadata,";FFMETADATA1\ntitle=original-title\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=chapter-one\n",new UTF8Encoding(false));
        await Run(Ffmpeg,["-hide_banner","-y","-f","lavfi","-i","testsrc2=size=96x64:rate=25:duration=2","-f","lavfi","-i","sine=frequency=400:duration=2",
            "-i",subtitle,"-f","ffmetadata","-i",metadata,"-map","0:v","-map","1:a","-map","2:s","-map_metadata","3","-map_chapters","3",
            "-metadata:s:v:0","title=original-video-title","-vf","setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709","-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709","-metadata:s:a:0","language=jpn","-c:v","ffv1","-c:a","pcm_s16le","-c:s","srt",input]);
        var source=await VideoMediaProbe.ProbeAsync(Ffprobe,input,true,CancellationToken.None);
        Check(source.Videos[0].Frames==50,"媒体探测得到完整帧数");
        var settings=new EnhancementSettings{SegmentedEnabled=true,SegmentedVideos=[new(){Path=input,Enabled=true,FrameCount=50,Segments=[
            new(){Start=1,End=25,Backend="ffmpeg",Model="lanczos",TargetWidth=192,TargetHeight=128},
            new(){Start=26,End=50,Backend="ffmpeg",Model="bicubic",TargetWidth=192,TargetHeight=128}]}]};
        string preset=Preset(settings),id="integration-"+Guid.NewGuid().ToString("N");
        var host=new TestHost();
        using var pipeline=new ExtVideoPipeline(host,()=>Ffmpeg,()=>Ffprobe);
        string workRoot=Path.Combine(_root,"runtime");
        BackendServices.ConfigureRuntime(workRoot);
        var results=new Dictionary<string,string>();
        var context=new ExtPluginPipelineContext(null,result=>results[result.Key]=result.Value){TaskId=id,InputPath=input,OutputPath=output,PresetJson=preset};
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,context);
        var command=new ExtPluginCommandContext{TaskId=id,InputPath=input,OutputPath=output,PresetJson=preset};
        foreach(var provider in host.CommandsImpl.Steps)provider.Callback(command);
        Check(command.Steps.Count>=4&&command.Steps.All(step=>step.Placement==ExtPluginCommandStepPlacement.BeforeNative),"全部 AI 步骤在原生编码前执行");
        foreach(var step in command.Steps)
        {
            var stepContext=new ExtPluginPipelineContext{TaskId=id,InputPath=input,OutputPath=output,PresetJson=preset,ProcessFileName=step.ProcessFileName,CommandLine=step.Arguments,PhaseName=step.DisplayName};
            stepContext.Properties["pluginId"]="videoenhancer";stepContext.Properties["pluginStepId"]=step.Id;stepContext.Properties["isPluginStep"]="true";
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessBeforeStart,stepContext);
            await Run(stepContext.ProcessFileName,WindowsArguments.Parse(stepContext.CommandLine));
            stepContext.ExitCode=0;
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessAfterExit,stepContext);
            Check(stepContext.ExitCode==0,"AI 前置步骤完成："+step.Id);
        }
        Check(results.ContainsKey("ai.parameters")&&results.ContainsKey("ai.backend.commands"),"任务保存真实参数和后端命令结果");
        var native=new ExtPluginPipelineContext{TaskId=id,InputPath=input,OutputPath=output,PresetJson=preset,PhaseName="普通单次"};
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.CommandBeforeBuild,native);
        command=new(){TaskId=id,InputPath=input,OutputPath=output,PresetJson=native.PresetJson};
        foreach(var provider in host.CommandsImpl.Parameters)provider.Callback(command);
        string enhanced=WindowsArguments.Parse(command.Arguments[0].Text)[1];
        native.CommandLine=WindowsArguments.Join(["-hide_banner","-y","-i",input,"-i",enhanced,"-map","VE_AI_VIDEO:v:0?","-map","0:a?","-map","0:s?",
            "-map_metadata","0","-map_chapters","0","-c:v","ffv1","-c:a","copy","-c:s","copy",output]);
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.CommandAfterBuild,native);
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessBeforeStart,native);
        await Run(Ffmpeg,WindowsArguments.Parse(native.CommandLine));
        using var final=JsonDocument.Parse(await Capture(Ffprobe,["-v","error","-show_streams","-show_format","-show_chapters","-of","json",output]));
        var streams=final.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        Check(streams.Count(s=>s.GetProperty("codec_type").GetString()=="video")==1&&streams[0].GetProperty("width").GetInt32()==192,"最终视频使用增强尺寸");
        Check(streams[0].GetProperty("tags").GetProperty("title").GetString()=="original-video-title","原视频流的用户元数据保留");
        if(!streams[0].TryGetProperty("color_primaries",out var primaries)||!streams[0].TryGetProperty("color_transfer",out var transfer))
        {
            Console.Error.WriteLine("源视频色彩："+source.Videos[0].ColorPrimaries+"/"+source.Videos[0].ColorTransfer);
            Console.Error.WriteLine("增强视频："+await Capture(Ffprobe,["-v","error","-show_streams","-of","json",enhanced]));
            Console.Error.WriteLine("最终命令："+native.CommandLine);
            Console.Error.WriteLine("最终视频："+streams[0].GetRawText());
            throw new InvalidOperationException("最终视频缺少色彩标记");
        }
        Check(primaries.GetString()=="bt709"&&transfer.GetString()=="bt709","原始 SDR 色域和传输标记保留");
        Check(streams.Any(s=>s.GetProperty("codec_type").GetString()=="audio"&&s.GetProperty("tags").GetProperty("language").GetString()=="jpn"),"最终音频及其语言元数据来自原始输入");
        Check(streams.Any(s=>s.GetProperty("codec_type").GetString()=="subtitle"),"最终字幕来自原始输入");
        Check(final.RootElement.GetProperty("format").GetProperty("tags").GetProperty("title").GetString()=="original-title","最终全局元数据来自原始输入");
        Check(final.RootElement.GetProperty("chapters").GetArrayLength()==1,"原始章节保留");
        string originalAudio=await Capture(Ffmpeg,["-v","error","-i",input,"-map","0:a:0","-f","hash","-hash","sha256","-"]);
        string finalAudio=await Capture(Ffmpeg,["-v","error","-i",output,"-map","0:a:0","-f","hash","-hash","sha256","-"]);
        Check(originalAudio==finalAudio,"原始音频内容没有经过 AI 或重新编码");
        var finalInfo=await VideoMediaProbe.ProbeAsync(Ffprobe,output,true,CancellationToken.None);
        Check(finalInfo.Videos[0].Frames==50&&Math.Abs(finalInfo.Duration-2)<0.05,"全流程帧数与时长保持一致");
        string beforeSecondPass=command.Arguments[0].Text;
        var secondPass=new ExtPluginCommandContext{TaskId=id,InputPath=input,OutputPath=output,PresetJson=preset,PhaseName="二次编码第二遍"};
        foreach(var provider in host.CommandsImpl.Parameters)provider.Callback(secondPass);
        Check(secondPass.Arguments[0].Text==beforeSecondPass,"两遍编码复用同一 AI 视频");
        context.TaskStatus=ExtPluginTaskStatus.Succeeded;
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskAfterComplete,context);
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskAfterFinish,context);
        Check(!Directory.Exists(Path.GetDirectoryName(enhanced)),"成功后清理任务中间文件");
        Check(File.Exists(output),"保留最终文件");
        settings.SegmentedVideos[0].Segments[1].Start=27;
        context=new(){TaskId="invalid",InputPath=input,OutputPath=output,PresetJson=Preset(settings)};
        bool rejected=false;
        try{await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,context);}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"分段空洞在后端启动前拒绝");
        context.TaskStatus=ExtPluginTaskStatus.Failed;
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskAfterFailed,context);
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskAfterFinish,context);
        Check(!Directory.EnumerateDirectories(Path.Combine(workRoot,".work"),"task-*").Any(),"准备失败也清理中间目录");
        await TestRtxPipelineAsync(input);
        await TestRtxPipelineAsync(input,true);
        await TestRtxCancelAsync(input);
        await TestTimingAsync(input);
        await TestActualHostAsync(input);
    }
    private static string Preset(EnhancementSettings settings)=>new JsonObject{
        ["流控制_将视频参数应用于指定流"]=new JsonArray("0:v:0"),
        ["插件扩展数据"]=new JsonObject{["videoenhancer"]=JsonNode.Parse(settings.ToJson()),["other-plugin"]=new JsonObject{["keep"]=true}}
    }.ToJsonString();
    private static async Task Run(string tool,IEnumerable<string> args){await Capture(tool,args);}
    private static async Task<string> Capture(string tool,IEnumerable<string> args)
    {
        var start=new ProcessStartInfo(tool){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=new System.Diagnostics.Process{StartInfo=start};
        process.Start();
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try{await process.WaitForExitAsync(deadline.Token);}catch{process.Kill(true);throw;}
        if(process.ExitCode!=0)throw new InvalidOperationException(tool+"："+await error);
        return await output;
    }
}

internal sealed class TestHost : IExtFFmpegFreeUIHost
{
    internal readonly TestPipeline PipelineImpl=new();
    internal readonly TestCommands CommandsImpl=new();
    internal readonly TestOverview Overview=new();
    internal readonly TestPages Pages=new();
    public Version ApiVersion=>new(2,5);public string HostVersion=>"test";
    public IExtPluginPipelineRegistry Pipeline=>PipelineImpl;public IExtPluginCommandRegistry Commands=>CommandsImpl;
    public IExtPluginPresetOverviewRegistry PresetOverview=>Overview;public IExtPluginPageEntryRegistry PageEntries=>Pages;
    public IExtPluginUiRegistry Ui=>null!;public IExtPluginEncodingQueueToolbarRegistry EncodingQueueToolbar=>null!;
    public IExtPluginSettingsRegistry PluginSettings=>null!;public IExtPluginBehaviorRegistry Behaviors=>null!;
    public IExtPluginResourceRegistry Resources=>null!;public IExtPluginParameterPanelCatalog ParameterPanel=>null!;
    public void Log(ExtPluginLogLevel level,string message,Exception? exception=null){}
}
internal sealed class EmptyRegistration : IDisposable{public void Dispose(){}}
internal sealed class TestPipeline : IExtPluginPipelineRegistry
{
    public IReadOnlyCollection<string> AvailableStages=>ExtFFmpegFreeUIPipelineStages.All;
    internal readonly List<ExtPluginPipelineHandler> Handlers=[];
    public IDisposable Register(ExtPluginPipelineHandler handler){Handlers.Add(handler);return new EmptyRegistration();}
    internal async Task Invoke(string stage,ExtPluginPipelineContext context)
    {
        context.StageId=stage;
        foreach(var handler in Handlers.Where(handler=>handler.StageId==stage))await handler.Callback(context,CancellationToken.None);
    }
}
internal sealed class TestCommands : IExtPluginCommandRegistry
{
    internal readonly List<ExtPluginCommandStepProvider> Steps=[];internal readonly List<ExtPluginCommandParameterProvider> Parameters=[];
    public IDisposable RegisterStepProvider(ExtPluginCommandStepProvider provider){Steps.Add(provider);return new EmptyRegistration();}
    public IDisposable RegisterParameterProvider(ExtPluginCommandParameterProvider provider){Parameters.Add(provider);return new EmptyRegistration();}
}
internal sealed class TestOverview : IExtPluginPresetOverviewRegistry
{
    internal readonly List<ExtPluginPresetOverviewRowProvider> Rows=[];
    public IDisposable RegisterRowProvider(ExtPluginPresetOverviewRowProvider provider){Rows.Add(provider);return new EmptyRegistration();}
}
internal sealed class TestPages : IExtPluginPageEntryRegistry
{
    internal readonly List<ExtPluginPageExtension> Pages=[];
    public IReadOnlyCollection<ExtPluginPageTargetDescriptor> AvailableTargets=>Array.Empty<ExtPluginPageTargetDescriptor>();
    public IDisposable RegisterPage(ExtPluginPageExtension page){Pages.Add(page);return new EmptyRegistration();}
}
internal sealed class TestPageContext : IExtPluginPageContext
{
    public string PluginId=>"videoenhancer";public string ExtensionId=>"ai-video-parameters";public string TargetId=>ExtFFmpegFreeUIPageTargets.ParametersVideoFrame;
    public ExtPluginPageEntryArea Area=>ExtPluginPageEntryArea.ParameterPanelTabList;public ExtPluginRelativePosition Position=>ExtPluginRelativePosition.After;
    public System.Windows.Forms.Control? PageControl=>null;public bool SupportsPresetState=>true;public string StateJson{get;set;}="{}";
    public event EventHandler? StateRestored;public int RefreshCount;
    public void RequestParameterRefresh()=>RefreshCount++;
    public void Restore(string json){StateJson=json;StateRestored?.Invoke(this,EventArgs.Empty);}
}
