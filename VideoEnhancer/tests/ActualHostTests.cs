using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using FFmpegFreeUI.Ext.PluginSdk;
using VideoEnhancer;

internal static partial class Program
{
    private static async Task TestActualHostAsync(string input)
    {
        string? directory=Environment.GetEnvironmentVariable("VIDEOENHANCER_TEST_HOST");
        if(string.IsNullOrWhiteSpace(directory))return;
        directory=Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
        System.Console.WriteLine("运行：Ext 宿主真实命令生成与插件组合");
        Assembly? Resolve(AssemblyLoadContext context,AssemblyName name)
        {
            string candidate=Path.Combine(directory,name.Name+".dll");
            return File.Exists(candidate)?context.LoadFromAssemblyPath(candidate):null;
        }
        AssemblyLoadContext.Default.Resolving+=Resolve;
        try
        {
            var core=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory,"FFmpegFreeUI.dll"));
            TestActualHostPluginDiscovery(core);
            var hostAssembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory,"FFmpegFreeUI.Ext.PluginHost.dll"));
            const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
            var hostType=hostAssembly.GetType("FFmpegFreeUI.Ext插件扩展宿主_v2",true)!;
            var bridge=core.GetType("FFmpegFreeUI.Ext插件扩展桥接_v2",true)!;
            // 测试进程内接通现有桥接；宿主源码、配置和安装目录均不修改。
            bridge.GetField("初始化完成",flags)!.SetValue(null,true);
            bridge.GetField("宿主类型",flags)!.SetValue(null,hostType);
            var settingsType=core.GetType("FFmpegFreeUI.设置_v6",true)!;
            var hostSettings=Activator.CreateInstance(settingsType)!;
            settingsType.GetProperty("工作目录")!.SetValue(hostSettings,_root);
            settingsType.GetProperty("实例对象",flags)!.SetValue(null,hostSettings);
            TestActualHostPreviewAdapter(core);
            var createScope=hostType.GetMethod("创建插件作用域",flags)!;
            var compatible=(IExtFFmpegFreeUIHost)createScope.Invoke(null,["compat-fixture","组合测试"])!;
            using var compatibleLifetime=(IDisposable)compatible;
            using var extraInput=compatible.Commands.RegisterParameterProvider(new("subtitle",context=>{
                if(context.PhaseName=="FFprobe获取时长")return;
                context.Arguments.Add(new(ExtPluginCommandArgumentPosition.AfterInput,"-i "+WindowsArguments.Quote(Path.Combine(_root,"字幕.srt"))));
                context.Arguments.Add(new(ExtPluginCommandArgumentPosition.BeforeOutput,"-map 1:s:0 -c:s copy"));
            }));
            var host=(IExtFFmpegFreeUIHost)createScope.Invoke(null,["videoenhancer","视频超分"])!;
            using var hostLifetime=(IDisposable)host;
            using var pipeline=new ExtVideoPipeline(host,()=>Ffmpeg,()=>Ffprobe);
            var enhancement=new EnhancementSettings{SegmentedEnabled=true,SegmentedVideos=[new(){Path=input,Enabled=true,SourceWidth=96,SourceHeight=64,FrameCount=50,
                Segments=[new(){Start=1,End=50,Backend="ffmpeg",Model="lanczos",TargetWidth=192,TargetHeight=128}]}]};
            var data=JsonNode.Parse(Preset(enhancement))!;
            data["插件扩展数据"]=new JsonObject{["videoenhancer"]=enhancement.ToJson(),["other-plugin"]="{}"};
            data["视频参数_编码器_类型"]="视频";
            data["视频参数_编码器_分类名称"]="FFv1";
            data["视频参数_编码器_具体编码"]="ffv1 -level 3";
            data["音频参数_编码器_代号"]="audio.copy";
            data["流控制_将音频参数应用于指定流"]=new JsonArray("0:a:0");
            data["流控制_启用保留其他字幕流"]=true;
            data["流控制_元数据选项"]="保留元数据";data["流控制_章节选项"]="保留章节";
            data["自定义参数_开头参数"]="-hide_banner -y";
            string presetJson=data.ToJsonString(),id="actual-host",output=Path.Combine(_root,"actual-host.mkv");
            var contextType=core.GetType("FFmpegFreeUI.Ext插件管线上下文_v2",true)!;
            object NewContext(string phase="",string command="")
            {
                var context=Activator.CreateInstance(contextType)!;
                foreach(var (name,value) in new[]{("TaskId",id),("InputPath",input),("OutputPath",output),("PresetJson",presetJson),("PhaseName",phase),("CommandLine",command)})
                    contextType.GetProperty(name)!.SetValue(context,value);
                return context;
            }
            async Task Stage(string stage,object context)
            {
                var execution=(Task)bridge.GetMethod("执行异步阶段Async",flags)!.Invoke(null,[stage,context,CancellationToken.None])!;
                await execution;
            }
            await Stage(ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,NewContext());
            var presetType=core.GetType("FFmpegFreeUI.预设数据_v6",true)!;
            var jsonOptions=(JsonSerializerOptions)core.GetType("FFmpegFreeUI.Module1",true)!.GetField("JsonSO",flags)!.GetValue(null)!;
            var preset=JsonSerializer.Deserialize(presetJson,presetType,jsonOptions)!;
            var manager=core.GetType("FFmpegFreeUI.预设管理_v6",true)!;
            var build=manager.GetMethod("生成阶段化命令行",flags)!;
            var generated=(IEnumerable)build.Invoke(null,[preset,input,output,"2",id])!;
            string nativeCommand="";
            foreach(var step in generated.Cast<object>())
            {
                var type=step.GetType();
                string command=type.GetProperty("命令行")!.GetValue(step)!.ToString()!;
                bool plugin=(bool)type.GetProperty("是插件步骤")!.GetValue(step)!;
                string file=type.GetProperty("进程文件名")!.GetValue(step)?.ToString()??"";
                var context=NewContext(type.GetProperty("显示名称")!.GetValue(step)?.ToString()??"",command);
                contextType.GetProperty("ProcessFileName")!.SetValue(context,file);
                var properties=(IDictionary<string,string>)contextType.GetProperty("Properties")!.GetValue(context)!;
                properties["isPluginStep"]=plugin?"true":"false";
                properties["pluginId"]=type.GetProperty("插件ID")!.GetValue(step)?.ToString()??"";
                properties["pluginStepId"]=type.GetProperty("插件步骤ID")!.GetValue(step)?.ToString()??"";
                properties["commandStage"]=type.GetProperty("阶段")!.GetValue(step)!.ToString()!;
                await Stage(ExtFFmpegFreeUIPipelineStages.ProcessBeforeStart,context);
                await Run(string.IsNullOrWhiteSpace(file)?Ffmpeg:file,WindowsArguments.Parse(command));
                contextType.GetProperty("ExitCode")!.SetValue(context,0);
                await Stage(ExtFFmpegFreeUIPipelineStages.ProcessAfterExit,context);
                if(!plugin)nativeCommand=command;
            }
            var tokens=WindowsArguments.Parse(nativeCommand);
            Check(tokens.Contains("2:v:0?")&&tokens.Contains("0:a:0?")&&tokens.Contains("1:s:0"),"宿主生成命令动态绑定 AI，并保留其他插件的外挂字幕输入");
            var result=await VideoMediaProbe.ProbeAsync(Ffprobe,output,true,CancellationToken.None);
            Check(result.Videos[0].Width==192&&result.Videos[0].Frames==50,"宿主原生 FFmpeg 使用 AI 中间文件");
            Check((await Capture(Ffmpeg,["-v","error","-i",input,"-map","0:a:0","-f","hash","-hash","sha256","-"]))==
                (await Capture(Ffmpeg,["-v","error","-i",output,"-map","0:a:0","-f","hash","-hash","sha256","-"])),"宿主实际生成命令保留原始音频内容");
            // 需要时长探测时，宿主先只生成探测阶段，之后才插入 AI 前置步骤。
            presetType.GetProperty("剪辑区间_方法")!.SetValue(preset,Enum.Parse(presetType.GetProperty("剪辑区间_方法")!.PropertyType,"掐头去尾"));
            presetType.GetProperty("剪辑区间_入点")!.SetValue(preset,"0.1");
            presetType.GetProperty("剪辑区间_出点")!.SetValue(preset,"0.1");
            var first=((IEnumerable)build.Invoke(null,[preset,input,output,"",id])!).Cast<object>().ToArray();
            Check(!first.Any(step=>(bool)step.GetType().GetProperty("是插件步骤")!.GetValue(step)!),"宿主时长探测前不执行 AI 步骤");
            var finish=NewContext();contextType.GetProperty("TaskStatus")!.SetValue(finish,"succeeded");
            await Stage(ExtFFmpegFreeUIPipelineStages.TaskAfterFinish,finish);
            // 直接调用宿主 UI 的真实预览路径，验证未选模型不会被包装成宿主未处理异常。
            var invalidData=data.DeepClone();
            invalidData["插件扩展数据"]!["videoenhancer"]=new EnhancementSettings{UpscaleEnabled=true}.ToJson();
            var invalidPreset=JsonSerializer.Deserialize(invalidData.ToJsonString(),presetType,jsonOptions)!;
            var preview=((IEnumerable)build.Invoke(null,[invalidPreset,input,output,"2",""])!).Cast<object>().ToArray();
            Check(preview.Length>0&&!preview.Any(step=>(bool)step.GetType().GetProperty("是插件步骤")!.GetValue(step)!),
                "真实宿主预览遇到未选模型仍正常返回，不插入不完整的 AI 步骤");
            Check(EnhancementTaskRegistry.CurrentParameterError.Contains("尚未选择超分模型"),"真实宿主预览的模型错误保留在插件诊断中");
            var rtxData=data.DeepClone();
            rtxData["插件扩展数据"]!["videoenhancer"]=new EnhancementSettings{UpscaleEnabled=true,Backend="rtxvsr",RtxTarget="2x",RtxQuality=4,
                RtxHdrEnabled=true,RtxHdrMaxLuminance=1200}.ToJson();
            var rtxPreset=JsonSerializer.Deserialize(rtxData.ToJsonString(),presetType,jsonOptions)!;
            string template=(string)manager.GetMethod("生成命令行展示文本",flags)!.Invoke(null,[rtxPreset,input,output])!;
            Check(template.Contains("# RTX POST /api/jobs：")&&template.Contains("--app-session-id")&&template.Contains("\"maxLuminance\":1200"),
                "真实宿主命令模板展示 RTX 完整协议和服务参数");
            Check(template.Contains("-c:v ffv1")&&template.Contains("-map 1:s:0"),"RTX 协议注释保留宿主及其他插件的实际命令参数");
        }
        finally{AssemblyLoadContext.Default.Resolving-=Resolve;}
    }
    private static void TestActualHostPreviewAdapter(Assembly core)
    {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Static|BindingFlags.Instance;
        var assembly=typeof(videoenhancer.Entry).Assembly;
        var runtime=assembly.GetType("videoenhancer.HostRuntime",true)!;
        var overrideHost=runtime.GetProperty("TestAssembly",flags)!;
        object? previous=overrideHost.GetValue(null);
        var taskType=core.GetType("FFmpegFreeUI.编码任务_v6",true)!;
        var stepType=core.GetType("FFmpegFreeUI.编码步骤_v6",true)!;
        var task=Activator.CreateInstance(taskType)!;
        var step=Activator.CreateInstance(stepType)!;
        string id=(string)taskType.GetProperty("ID")!.GetValue(task)!;
        try
        {
            overrideHost.SetValue(null,core);
            taskType.GetProperty("输出文件")!.SetValue(task,Path.Combine(_root,"final-not-muxed.mkv"));
            ((IList)taskType.GetProperty("步骤")!.GetValue(task)!).Add(step);
            taskType.GetProperty("当前步骤索引")!.SetValue(task,0);
            void Set(string name,object value)=>stepType.GetProperty(name)!.SetValue(step,value);
            var wrapped=assembly.GetType("videoenhancer.HostTask",true)!.GetMethod("Wrap",flags)!.Invoke(null,[task])!;
            var resolver=assembly.GetType("videoenhancer.PreviewEngine",true)!.GetMethod("ResolvePreviewSource",flags)!;
            object Source()=>resolver.Invoke(null,[wrapped])!;
            object Value(object source,string name)=>source.GetType().GetField(name,flags)!.GetValue(source)!;
            Check(ReferenceEquals(wrapped.GetType().GetProperty("当前步骤",flags)!.GetValue(wrapped),step),"真实 Ext 当前步骤适配读取原生步骤对象");
            Set("显示名称","音频编码");Set("实际执行参数","-vn audio.flac");
            Check((bool)Value(Source(),"Waiting"),"真实 Ext 音频步骤等待视频预览");
            string video=Path.Combine(_root,"actual-step-video.mkv");
            Set("显示名称","视频编码");Set("实际执行参数",WindowsArguments.Join(["-i","input.mkv","-an",video,"-y"]));
            Check((string)Value(Source(),"Output")==video,"真实 Ext 视频步骤读取临时输出而不是任务最终输出");
            typeof(EnhancementTaskRegistry).GetMethod("SetPreviewSources",flags)!.Invoke(null,
                [id,new Dictionary<string,EnhancementPreviewSource>{{"rve-first-1",new(video,60,true)}}]);
            Set("是插件步骤",true);Set("插件ID","videoenhancer");Set("插件步骤ID","rve-first-1");
            Set("进程文件名","python.exe");Set("实际执行参数","launch.py --ffprobe-path C:/tools/ffprobe.exe");
            object source=Source();
            Check((string)Value(source,"Output")==video&&(double)Value(source,"FrameRate")==60&&(bool)Value(source,"UseTelemetry"),
                "真实 Ext RVE 步骤使用明确中间视频、补帧后帧率及本阶段遥测");
        }
        finally
        {
            typeof(EnhancementTaskRegistry).GetMethod("ClearPreviewSources",flags)!.Invoke(null,[id]);
            overrideHost.SetValue(null,previous);
        }
    }

}
