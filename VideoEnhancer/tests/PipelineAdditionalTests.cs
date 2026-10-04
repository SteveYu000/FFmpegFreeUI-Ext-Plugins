using System.IO;
using System.Diagnostics;
using System.Text.Json;
using FFmpegFreeUI.Ext.PluginSdk;
using VideoEnhancer;

internal static partial class Program
{
    private static void TestRvePreview()
    {
        string runtime=Path.Combine(_root,"runtime"),model=Path.Combine(runtime,"models","Param-Bin","2x-preview");
        Directory.CreateDirectory(model);
        File.WriteAllText(Path.Combine(model,"model.param"),"7767517\n0 0\n");
        File.WriteAllBytes(Path.Combine(model,"model.bin"),[0]);
        BackendServices.ConfigureRuntime(runtime);
        var host=new TestHost();
        using var pipeline=new ExtVideoPipeline(host,()=>Ffmpeg,()=>Ffprobe);
        var settings=new EnhancementSettings{UpscaleEnabled=true,Model="Param-Bin/2x-preview"};
        var context=new ExtPluginCommandContext{IsPreview=true,InputPath="<输入文件>",PresetJson=Preset(settings)};
        foreach(var provider in host.CommandsImpl.Steps)provider.Callback(context);
        var rve=context.Steps.Single(step=>step.ProcessFileName.EndsWith("python.exe",StringComparison.OrdinalIgnoreCase));
        var args=WindowsArguments.Parse(rve.Arguments);
        Check(args.Contains("--upscale_model")&&args.Any(arg=>arg==Path.Combine(model,"model.bin")),"命令预览包含真正传给 RVE 的模型路径");
        Check(args.Contains("--custom_encoder")&&args.Any(arg=>arg.Contains("-c:v ffv1")&&arg.Contains("-an -sn -dn")),"RVE 接收到仅视频无损编码参数");
        Check(args.Contains("--process-order")&&args.Contains("upscale-first"),"RVE 实际包装器接收处理顺序");
        Check(!Directory.Exists(Path.Combine(runtime,".work","preview")),"命令预览不创建工作目录或执行后端");

        settings=new(){SegmentedEnabled=true,SegmentedVideos=[new(){Path=Path.Combine(_root,"example.mkv"),Enabled=true,SourceWidth=96,SourceHeight=64,FrameCount=50,DurationSeconds=2,
            Segments=[new(){Start=1,End=50,Backend="ncnn",Model="Param-Bin/2x-preview"}]}]};
        context=new(){IsPreview=true,InputPath="<输入文件>",PresetJson=Preset(settings)};
        foreach(var provider in host.CommandsImpl.Steps)provider.Callback(context);
        args=WindowsArguments.Parse(context.Steps.Last().Arguments);
        var segmentJson=JsonDocument.Parse(Convert.FromBase64String(args[args.ToList().IndexOf("--segments-base64")+1]));
        var segment=segmentJson.RootElement[0];
        Check(segment.GetProperty("outputWidth").GetInt32()==192&&segment.GetProperty("outputHeight").GetInt32()==128,"分段预览使用预设中的源尺寸计算模型输出");
        segmentJson.Dispose();
    }

    private static async Task RunSteps(TestHost host,ExtPluginCommandContext command,bool testPause=false)
    {
        foreach(var provider in host.CommandsImpl.Steps)provider.Callback(command);
        foreach(var step in command.Steps)
        {
            var context=new ExtPluginPipelineContext{TaskId=command.TaskId,InputPath=command.InputPath,OutputPath=command.OutputPath,PresetJson=command.PresetJson,
                ProcessFileName=step.ProcessFileName,CommandLine=step.Arguments,PhaseName=step.DisplayName};
            context.Properties["pluginId"]="videoenhancer";context.Properties["pluginStepId"]=step.Id;context.Properties["isPluginStep"]="true";
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessBeforeStart,context);
            await Run(context.ProcessFileName,WindowsArguments.Parse(context.CommandLine));
            context.ExitCode=0;
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessAfterExit,context);
            Check(context.ExitCode==0,"附加前置步骤成功："+step.Id);
        }
    }

    private static async Task TestRtxPipelineAsync(string input,bool hdr=false)
    {
        System.Console.WriteLine(hdr?"运行：RTX HDR 帧管道回归":"运行：RTX HTTP 与原始帧协议回归");
        string runtime=Path.Combine(_root,"runtime"),backend=Path.Combine(runtime,"bin","rtx-video");
        Directory.CreateDirectory(backend);
        foreach(string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(file,Path.Combine(backend,Path.GetFileName(file)),true);
        File.Copy(Path.Combine(AppContext.BaseDirectory,"VideoEnhancer.Tests.exe"),Path.Combine(backend,"vsr_backend.exe"),true);
        var settings=new EnhancementSettings{UpscaleEnabled=true,Backend="rtxvsr",RtxTarget="2x",RtxQuality=4,RtxHdrEnabled=hdr};
        string preset=Preset(settings),id=hdr?"rtx-hdr-protocol":"rtx-protocol",output=Path.Combine(_root,hdr?"rtx-hdr-final.mkv":"rtx-final.mkv");
        var host=new TestHost();using var pipeline=new ExtVideoPipeline(host,()=>Ffmpeg,()=>Ffprobe);
        var context=new ExtPluginPipelineContext{TaskId=id,InputPath=input,OutputPath=output,PresetJson=preset};
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,context);
        var command=new ExtPluginCommandContext{TaskId=id,InputPath=input,OutputPath=output,PresetJson=preset};
        foreach(var provider in host.CommandsImpl.Parameters)provider.Callback(command);
        string enhanced=WindowsArguments.Parse(command.Arguments[0].Text)[1],work=Path.GetDirectoryName(enhanced)!;
        foreach(var provider in host.CommandsImpl.Steps)provider.Callback(command);
        foreach(var step in command.Steps)
        {
            bool rtx=step.Id=="rtx-0";
            if(rtx)pipeline.OnQueueEvent("task.paused",JsonSerializer.Serialize(new{task=new{id,status="paused"}}));
            context=new(){TaskId=id,InputPath=input,OutputPath=output,PresetJson=preset,ProcessFileName=step.ProcessFileName,CommandLine=step.Arguments,PhaseName=step.DisplayName};
            context.Properties["pluginId"]="videoenhancer";context.Properties["pluginStepId"]=step.Id;context.Properties["isPluginStep"]="true";
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessBeforeStart,context);
            var running=Run(context.ProcessFileName,WindowsArguments.Parse(context.CommandLine));
            if(rtx)
            {
                await Until(()=>File.Exists(Path.Combine(work,"rtx-paused")),TimeSpan.FromSeconds(8));
                Check(!running.IsCompleted,"暂停通知通过 RTX HTTP 生效，帧写入尚未完成");
                pipeline.OnQueueEvent("task.resumed",JsonSerializer.Serialize(new{task=new{id,status="running"}}));
            }
            await running;context.ExitCode=0;
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessAfterExit,context);
            Check(context.ExitCode==0,"RTX 链步骤成功："+step.Id);
        }
        using var request=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(work,"rtx-request.json")));
        var root=request.RootElement;
        Check(root.GetProperty("processing").GetProperty("vsr").GetProperty("quality").GetInt32()==4,"RTX 使用用户设置的真实质量值");
        Check(root.GetProperty("output").GetProperty("audioMode").GetString()=="none"&&root.GetProperty("output").GetProperty("subtitleMode").GetString()=="none","RTX 不处理原始音频和字幕");
        Check(File.Exists(Path.Combine(work,"rtx-resumed")),"恢复通知传给 RTX 服务");
        var info=await VideoMediaProbe.ProbeAsync(Ffprobe,enhanced,true,CancellationToken.None);
        Check(info.StreamCount==1&&info.Videos[0].CodecName=="ffv1"&&info.Videos[0].Width==192&&info.Videos[0].Height==128&&info.Videos[0].Frames==50&&info.Videos[0].Bits==(hdr?16:10),"RTX 管道写入完整的高位深无损视频");
        Check(info.Videos[0].ColorPrimaries==(hdr?"bt2020":"bt709")&&info.Videos[0].ColorTransfer==(hdr?"smpte2084":"bt709"),"RTX 帧管道保留正确的色域与传输标记");
        Check(EnhancementTaskRegistry.Snapshot().Single(task=>task.TaskId==id).BackendRequest.Contains("RTX POST /api/jobs："),"首页任务详情包含实际 RTX JSON");
        int pid=int.Parse(await File.ReadAllTextAsync(Path.Combine(work,"rtx-pid.txt")));
        bool exited;try{using var process=System.Diagnostics.Process.GetProcessById(pid);exited=process.HasExited;}catch(ArgumentException){exited=true;}
        Check(exited,"完成后 RTX 服务退出");
        context=new(){TaskId=id,InputPath=input,OutputPath=output,PresetJson=preset,PhaseName="编码"};
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.CommandBeforeBuild,context);
        context.CommandLine=WindowsArguments.Join(["-hide_banner","-y","-i",input,"-i",enhanced,"-map","VE_AI_VIDEO:v:0?","-map","0:a?","-map","0:s?","-c:v","ffv1","-c:a","copy","-c:s","copy",output]);
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.CommandAfterBuild,context);
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessBeforeStart,context);
        await Run(Ffmpeg,WindowsArguments.Parse(context.CommandLine));
        var finalVideo=(await VideoMediaProbe.ProbeAsync(Ffprobe,output,true,CancellationToken.None)).Videos[0];
        Check(finalVideo.Frames==50&&finalVideo.ColorPrimaries==(hdr?"bt2020":"bt709")&&finalVideo.ColorTransfer==(hdr?"smpte2084":"bt709"),"RTX 增强结果及色彩标记交付原生 FFmpeg");
        context.TaskStatus=ExtPluginTaskStatus.Succeeded;
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskAfterFinish,context);
        Check(!Directory.Exists(work),"RTX 完成后清理任务中间文件");
    }

    private static async Task TestTimingAsync(string original)
    {
        System.Console.WriteLine("运行：视频首帧偏移与宿主裁剪回归");
        foreach(string variant in new[]{"delayed","absolute","zero"})
        {
            bool absolute=variant=="absolute";
        {
            string input=Path.Combine(_root,variant+"-start.mkv");
            var creation=new List<string>{"-v","error","-y"};
            if(variant=="delayed")creation.AddRange(["-itsoffset","0.2"]);
            creation.AddRange(["-i",original,"-i",original,"-map","0:v:0","-map","1:a:0","-c","copy"]);
            if(absolute)creation.AddRange(["-output_ts_offset","3"]);
            creation.Add(input);await Run(Ffmpeg,creation);
            var source=await VideoMediaProbe.ProbeAsync(Ffprobe,input,false,CancellationToken.None);
            Check(Math.Abs(source.Videos[0].Duration-2)<0.05,"视频长度不使用带偏移的容器结束时间");
            var settings=new EnhancementSettings{SegmentedEnabled=true,SegmentedVideos=[new(){Path=input,Enabled=true,SourceWidth=96,SourceHeight=64,FrameCount=50,Segments=[new(){Start=1,End=50,Backend="ffmpeg",Model="lanczos",TargetWidth=96,TargetHeight=64}]}]};
            string preset=Preset(settings),id="timing-"+absolute;
            var host=new TestHost();using var pipeline=new ExtVideoPipeline(host,()=>Ffmpeg,()=>Ffprobe);
            var context=new ExtPluginPipelineContext{TaskId=id,InputPath=input,OutputPath=Path.Combine(_root,"timing.mkv"),PresetJson=preset};
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,context);
            var probe=new ExtPluginPipelineContext{TaskId=id,InputPath=input,PresetJson=preset,PhaseName="ffprobe 获取时长"};
            probe.Properties["commandStage"]="FFprobe获取时长";
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessBeforeStart,probe);
            Check(true,"实际宿主 ffprobe 显示名称不会提前验证 AI 输出");
            var command=new ExtPluginCommandContext{TaskId=id,InputPath=input,OutputPath=context.OutputPath,PresetJson=preset};
            foreach(var provider in host.CommandsImpl.Parameters)provider.Callback(command);
            string enhanced=WindowsArguments.Parse(command.Arguments[0].Text)[1];
            await RunSteps(host,command);
            foreach(string[] timing in new string[][]{[],["-ss","0.5"],["-ss","0.05","-t","1"],["-ss","0.5","-to","1.5"],["-copyts","-ss","0.5"],["-copyts","-start_at_zero","-ss","0.5"]})
            {
                string expected=Path.Combine(_root,"timing-expected.mkv"),actual=Path.Combine(_root,"timing-actual.mkv");
                var baseline=new List<string>{"-v","error","-y"};baseline.AddRange(timing);
                baseline.AddRange(["-i",input,"-map","0:v:0","-map","0:a:0","-c:v","ffv1","-pix_fmt","bgr0","-c:a","copy","-fps_mode","passthrough",expected]);
                await Run(Ffmpeg,baseline);
                var native=baseline.ToList();native[^1]=actual;
                int inputIndex=native.IndexOf(input);
                native.InsertRange(inputIndex+1,["-i",enhanced]);
                native[native.IndexOf("0:v:0")]="VE_AI_VIDEO:v:0";
                context=new(){TaskId=id,InputPath=input,OutputPath=actual,PresetJson=preset,CommandLine=WindowsArguments.Join(native),PhaseName="普通单次"};
                bool offsetCopyTs=timing.Contains("-copyts")&&(Math.Abs(source.Videos[0].RelativeStart)>0.000001||Math.Abs(source.FormatStart)>0.000001);
                if(offsetCopyTs)
                {
                    bool rejected=false;
                    try{await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.CommandAfterBuild,context);}catch(InvalidOperationException){rejected=true;}
                    Check(rejected,"拒绝无法可靠保留偏移时间轴的 copyts 组合");
                    continue;
                }
                await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.CommandAfterBuild,context);
                await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessBeforeStart,context);
                await Run(Ffmpeg,WindowsArguments.Parse(context.CommandLine));
                string VideoHash(string path)=>Capture(Ffmpeg,["-v","error","-i",path,"-map","0:v:0","-pix_fmt","rgb24","-f","hash","-hash","sha256","-"]).GetAwaiter().GetResult();
                if(VideoHash(expected)!=VideoHash(actual))
                {
                    System.Console.WriteLine("基准命令："+WindowsArguments.Join(baseline));
                    System.Console.WriteLine("增强命令："+context.CommandLine);
                    System.Console.WriteLine("基准帧："+await Capture(Ffprobe,["-v","error","-select_streams","v:0","-show_entries","frame=pts_time","-of","csv=p=0",expected]));
                    System.Console.WriteLine("增强帧："+await Capture(Ffprobe,["-v","error","-select_streams","v:0","-show_entries","frame=pts_time","-of","csv=p=0",actual]));
                }
                Check(VideoHash(expected)==VideoHash(actual),"裁剪选取相同原始帧："+string.Join(' ',timing));
                var expectedInfo=await VideoMediaProbe.ProbeAsync(Ffprobe,expected,true,CancellationToken.None);
                var actualInfo=await VideoMediaProbe.ProbeAsync(Ffprobe,actual,true,CancellationToken.None);
                Check(Math.Abs(expectedInfo.Videos[0].RelativeStart-actualInfo.Videos[0].RelativeStart)<0.005&&Math.Abs(expectedInfo.Duration-actualInfo.Duration)<0.05,"裁剪保留音视频时间关系："+string.Join(' ',timing));
            }
            context.TaskStatus=ExtPluginTaskStatus.Succeeded;
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskAfterFinish,context);
        }
        }
    }
    private static void TestModelRemoval()
    {
        string models=Path.Combine(_root,"runtime","models"),user=Path.Combine(models,"User");
        string container=Path.Combine(user,"Restoration","Fixture","sample"),model=Path.Combine(container,"model.pth");
        Directory.CreateDirectory(container);File.WriteAllBytes(model,[1]);
        var catalog=new{
            schemaVersion=1,models=new[]{new{id="fixture",displayName="测试用户模型",relativePath="User/Restoration/Fixture/sample/model.pth",task="upscale",architecture="Fixture",purpose="general",format="pth",
                scale=2,sha256="",size=1,importedAtUtc="",backends=new[]{"cuda"}}}
        };
        File.WriteAllText(Path.Combine(user,"model-catalog.json"),JsonSerializer.Serialize(catalog));
        string unrelated=Path.Combine(user,"preserved.txt");File.WriteAllText(unrelated,"preserved");
        BackendServices.RemoveInstalledModel(model);
        using var after=JsonDocument.Parse(File.ReadAllText(Path.Combine(user,"model-catalog.json")));
        Check(!File.Exists(model)&&after.RootElement.GetProperty("models").GetArrayLength()==0,"删除用户模型同时移除能力清单登记");
        Check(File.Exists(unrelated),"删除模型保留其他用户文件");
        string outside=Path.Combine(_root,"outside.pth");File.WriteAllBytes(outside,[1]);
        bool rejected=false;try{BackendServices.RemoveInstalledModel(outside);}catch(InvalidOperationException){rejected=true;}
        Check(rejected&&File.Exists(outside),"模型删除不能越过插件模型目录");
    }

    private static void TestSegmentFallback()
    {
        string configured=Path.Combine(_root,"configured.mkv"),other=Path.Combine(_root,"other.mkv");
        var settings=new EnhancementSettings{SegmentedEnabled=true,SegmentedVideos=[new(){Path=configured,Enabled=true}]};
        Check(settings.ForInput(configured).SegmentedEnabled,"配置了分段的文件使用分段模式");
        Check(!settings.ForInput(other).IsEnabled,"未配置分段且普通功能关闭时交由宿主处理");
        settings.InterpEnabled=true;
        var effective=settings.ForInput(other);
        Check(effective.InterpEnabled&&!effective.SegmentedEnabled&&settings.SegmentedEnabled,"未配置分段的文件使用普通开关且不修改预设");
    }

    private static async Task TestRtxCancelAsync(string input)
    {
        System.Console.WriteLine("运行：RTX 启动阶段取消与子进程清理");
        var host=new TestHost();using var pipeline=new ExtVideoPipeline(host,()=>Ffmpeg,()=>Ffprobe);
        var settings=new EnhancementSettings{UpscaleEnabled=true,Backend="rtxvsr",RtxTarget="2x"};
        string preset=Preset(settings),id="rtx-cancel";
        var context=new ExtPluginPipelineContext{TaskId=id,InputPath=input,OutputPath=Path.Combine(_root,"cancel.mkv"),PresetJson=preset};
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,context);
        var command=new ExtPluginCommandContext{TaskId=id,InputPath=input,OutputPath=context.OutputPath,PresetJson=preset};
        foreach(var provider in host.CommandsImpl.Parameters)provider.Callback(command);
        string work=Path.GetDirectoryName(WindowsArguments.Parse(command.Arguments[0].Text)[1])!;
        foreach(var provider in host.CommandsImpl.Steps)provider.Callback(command);
        foreach(var step in command.Steps)
        {
            var current=new ExtPluginPipelineContext{TaskId=id,InputPath=input,OutputPath=context.OutputPath,PresetJson=preset,PhaseName=step.DisplayName,
                ProcessFileName=step.ProcessFileName,CommandLine=step.Arguments};
            current.Properties["pluginId"]="videoenhancer";current.Properties["pluginStepId"]=step.Id;current.Properties["isPluginStep"]="true";
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessBeforeStart,current);
            if(step.Id=="rtx-0")break;
            await Run(step.ProcessFileName,WindowsArguments.Parse(step.Arguments));current.ExitCode=0;
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessAfterExit,current);
        }
        await Until(()=>File.Exists(Path.Combine(work,"rtx-pid.txt")),TimeSpan.FromSeconds(5));
        int pid=int.Parse(await File.ReadAllTextAsync(Path.Combine(work,"rtx-pid.txt")));
        pipeline.OnQueueEvent("task.stopped",JsonSerializer.Serialize(new{task=new{id,status="canceled"}}));
        context.TaskStatus=ExtPluginTaskStatus.Canceled;
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskAfterFinish,context);
        bool exited;try{using var process=System.Diagnostics.Process.GetProcessById(pid);exited=process.HasExited;}catch(ArgumentException){exited=true;}
        Check(exited,"RTX 在 FFmpeg 写入器启动前取消也能退出服务");
        Check(!Directory.Exists(work),"RTX 取消清理中间目录");
        Check(EnhancementTaskRegistry.Snapshot().Single(task=>task.TaskId==id).Status=="已停止","取消后任务首页状态正确");
    }

    private static async Task Until(Func<bool> condition,TimeSpan timeout)
    {
        var deadline=DateTime.UtcNow+timeout;
        while(!condition())
        {
            if(DateTime.UtcNow>deadline)throw new TimeoutException("测试条件等待超时");
            await Task.Delay(25);
        }
    }
}
