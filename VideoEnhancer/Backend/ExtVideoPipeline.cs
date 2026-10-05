using System.Collections.Concurrent;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FFmpegFreeUI.Ext.PluginSdk;

namespace VideoEnhancer;

// 生命周期状态以 TaskId 隔离；命令预览只构造计划，不启动后端或创建临时文件。
public sealed partial class ExtVideoPipeline : IDisposable
{
    public const string PluginId="videoenhancer";
    private readonly IExtFFmpegFreeUIHost _host;
    private readonly Func<string> _ffmpeg,_ffprobe;
    private readonly ConcurrentDictionary<string,TaskScope> _tasks=new();
    private readonly List<IDisposable> _registrations=[];
    private readonly SemaphoreSlim _prepareGate=new(1,1);
    public ExtVideoPipeline(IExtFFmpegFreeUIHost host,Func<string> ffmpeg,Func<string> ffprobe)
    {
        _host=host;_ffmpeg=ffmpeg;_ffprobe=ffprobe;
        Register(ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,PrepareAsync);
        Register(ExtFFmpegFreeUIPipelineStages.CommandBeforeBuild,BeforeBuild);
        Register(ExtFFmpegFreeUIPipelineStages.CommandAfterBuild,AfterBuild);
        Register(ExtFFmpegFreeUIPipelineStages.ProcessBeforeStart,BeforeProcessAsync);
        Register(ExtFFmpegFreeUIPipelineStages.ProcessAfterExit,AfterProcessAsync);
        Register(ExtFFmpegFreeUIPipelineStages.TaskAfterComplete,(context,_)=>{EnhancementTaskRegistry.SetStatus(context.TaskId,"收尾中");return ValueTask.CompletedTask;});
        Register(ExtFFmpegFreeUIPipelineStages.TaskAfterFailed,(context,_)=>{EnhancementTaskRegistry.SetStatus(context.TaskId,"失败");return ValueTask.CompletedTask;});
        Register(ExtFFmpegFreeUIPipelineStages.TaskAfterFinish,FinishAsync);
        _registrations.Add(host.Commands.RegisterParameterProvider(new("enhanced-video-inputs",context=>GuardCommand(context,AddInputs))));
        _registrations.Add(host.Commands.RegisterStepProvider(new("ai-video-steps",context=>GuardCommand(context,AddSteps))));
        _registrations.Add(host.PresetOverview.RegisterRowProvider(new("ai-parameters",GuardOverview)));
    }
    private void Register(string stage,ExtPluginPipelineCallback callback)=>_registrations.Add(_host.Pipeline.Register(new("ai-"+stage,stage,(context,token)=>GuardPipeline(context,token,callback))));
    private static EnhancementSettings Settings(string preset,string input,bool preview=false)=>EnhancementSettings.FromJson(EnhancementSettings.FromPreset(preset,PluginId)).ForInput(input,preview);
    private static string? Property(ExtPluginPipelineContext context,string key)=>context.Properties.TryGetValue(key,out var value)?value:null;
    private static bool IsProbe(string phase)=>phase.Replace(" ","").Equals("FFprobe获取时长",StringComparison.OrdinalIgnoreCase);
    private static bool IsProbe(ExtPluginPipelineContext context)=>IsProbe(Property(context,"commandStage")??context.PhaseName);
    private EnhancementPlan PreviewPlan(string preset,string input)
    {
        var settings=Settings(preset,input,true);
        return PreviewPlan(settings,preset,input);
    }
    private EnhancementPlan PreviewPlan(EnhancementSettings settings,string preset,string input)
    {
        string work=Path.Combine(PortablePaths.WorkRoot,"preview");
        return BackendServices.BuildEnhancementPlan(settings,preset,input,work,"VE-preview-pause",_ffmpeg(),_ffprobe(),null,false);
    }
    private EnhancementPlan ResolvePlan(string id,string preset,string input)
    {
        if(_tasks.TryGetValue(id,out var task))return task.Plan;
        return PreviewPlan(preset,input);
    }

    private async ValueTask PrepareAsync(ExtPluginPipelineContext context,CancellationToken token)
    {
        _parameterFailures.TryRemove(context.TaskId,out _);
        var settings=Settings(context.PresetJson,context.InputPath,context.IsPreview);
        if(!settings.IsEnabled)return;
        if(string.IsNullOrWhiteSpace(context.TaskId))throw new InvalidOperationException("AI 增强任务缺少宿主任务 ID");
        if(_tasks.TryRemove(context.TaskId,out var previous))await previous.DisposeAsync();
        token.ThrowIfCancellationRequested();
        if(!File.Exists(context.InputPath))throw new FileNotFoundException("AI 增强找不到原始输入文件",context.InputPath);
        EnhancementTaskRegistry.Update(context.TaskId,context.InputPath,context.OutputPath,"准备中","检查输入与模型",settings.ToJson());
        string work=PortablePaths.CreateWorkDirectory("task");
        string pause="VE-pause-"+Guid.NewGuid().ToString("N");
        TaskScope? scope=null;
        try
        {
            var media=await VideoMediaProbe.ProbeAsync(_ffprobe(),context.InputPath,settings.SegmentedEnabled,token).ConfigureAwait(false);
            if(media.Videos.Count==0)throw new InvalidOperationException("输入文件没有视频流");
            using var invocationCancellation=token.Register(()=>scope?.Preparation.Stop());
            var invocation=new BackendInvocation{Output=new ServiceTextWriter(line=>context.ReportProgress(line)),Error=new ServiceTextWriter(line=>context.ReportProgress(line)),FfmpegPath=_ffmpeg(),FfprobePath=_ffprobe()};
            using var cancel=token.Register(invocation.Stop);
            // 运行组件补丁和 Engine 缓存创建串行，避免并发任务改写同一模型缓存。
            await _prepareGate.WaitAsync(token).ConfigureAwait(false);
            EnhancementPlan plan;
            try
            {
                plan=await Task.Run(()=>
                {
                    BackendInvocation.Slot.Value=invocation;
                    try
                    {
                        BackendServices.PrepareExtSupport();
                        token.ThrowIfCancellationRequested();
                        return BackendServices.BuildEnhancementPlan(settings,context.PresetJson,context.InputPath,work,pause,_ffmpeg(),_ffprobe(),media,true);
                    }
                    finally{BackendInvocation.Slot.Value=null;}
                },token).ConfigureAwait(false);
            }
            finally{_prepareGate.Release();}
            token.ThrowIfCancellationRequested();
            if(plan.Steps.Any(step=>step.ProcessFileName.EndsWith("python.exe",StringComparison.OrdinalIgnoreCase))&&!File.Exists(plan.Steps.First(step=>step.ProcessFileName.EndsWith("python.exe",StringComparison.OrdinalIgnoreCase)).ProcessFileName))
                throw new FileNotFoundException("未安装 RVE 便携 Python，请在模型下载页安装所需运行组件");
            scope=new TaskScope(plan,invocation,message=>_host.Log(ExtPluginLogLevel.Warning,message));
            if(!_tasks.TryAdd(context.TaskId,scope))throw new InvalidOperationException("任务已存在，拒绝覆盖正在执行的 AI 上下文");
            scope.Cancellation=token.Register(scope.Cancel);
            context.ReportResult("ai.parameters",settings.ToJson(),"AI 任务参数");
            context.ReportResult("ai.backend.commands",plan.BackendDescription,"真实 AI 后端命令");
            EnhancementTaskRegistry.Update(context.TaskId,context.InputPath,context.OutputPath,"准备完成","等待 AI 前置步骤",settings.ToJson(),plan.BackendDescription);
        }
        catch
        {
            if(scope is not null)await scope.DisposeAsync();
            else DeleteWorkDirectory(work);
            throw;
        }
    }

    private ValueTask BeforeBuild(ExtPluginPipelineContext context,CancellationToken _)
    {
        if(IsProbe(context.PhaseName)||!Settings(context.PresetJson,context.InputPath,context.IsPreview).IsEnabled)return ValueTask.CompletedTask;
        if(_tasks.TryGetValue(context.TaskId,out var scope)&&!Path.GetFullPath(context.InputPath).Equals(Path.GetFullPath(scope.Plan.Input),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("其他插件在 AI 方案准备后更换了任务输入，不能将旧视频和新文件的音频混流");
        if(context.IsPreview)ResolvePlan(context.TaskId,context.PresetJson,context.InputPath);
        context.PresetJson=NativeVideoBinder.MarkPreset(context.PresetJson);
        return ValueTask.CompletedTask;
    }

    private void AddInputs(ExtPluginCommandContext context)
    {
        if(IsProbe(context.PhaseName)||!Settings(context.PresetJson,context.InputPath,context.IsPreview).IsEnabled)return;
        var plan=ResolvePlan(context.TaskId,context.PresetJson,context.InputPath);
        foreach(var video in plan.Videos)
            context.Arguments.Add(new(ExtPluginCommandArgumentPosition.AfterInput,"-i "+WindowsArguments.Quote(video.Value)){Description="AI 增强视频流 "+video.Key});
    }
    private void AddSteps(ExtPluginCommandContext context)
    {
        if(!Settings(context.PresetJson,context.InputPath,context.IsPreview).IsEnabled)return;
        var plan=ResolvePlan(context.TaskId,context.PresetJson,context.InputPath);
        foreach(var step in plan.Steps)context.Steps.Add(step);
    }
    private ValueTask AfterBuild(ExtPluginPipelineContext context,CancellationToken _)
    {
        if(IsProbe(context.PhaseName)||!Settings(context.PresetJson,context.InputPath,context.IsPreview).IsEnabled)return ValueTask.CompletedTask;
        var plan=ResolvePlan(context.TaskId,context.PresetJson,context.InputPath);
        string command=NativeVideoBinder.Bind(context.CommandLine,plan.Videos,plan.VideoCount,context.IsPreview||!_tasks.ContainsKey(context.TaskId));
        foreach(var video in plan.Videos)
            command=NativeVideoBinder.CopyInputTiming(command,video.Value,plan.Sources[video.Key].RelativeStart,plan.Sources[video.Key].FormatStart,plan.Sources[video.Key].FormatDuration);
        using var presetDocument=JsonDocument.Parse(context.PresetJson);
        bool keepOther=presetDocument.RootElement.TryGetProperty("流控制_启用保留其他视频流",out var retain)&&retain.ValueKind==JsonValueKind.True;
        context.CommandLine=NativeVideoBinder.PreserveVideoMetadata(command,context.OutputPath,plan.Videos.Keys,keepOther,plan.Sources);
        return ValueTask.CompletedTask;
    }

    private async ValueTask BeforeProcessAsync(ExtPluginPipelineContext context,CancellationToken token)
    {
        if(_parameterFailures.TryGetValue(context.TaskId,out var parameterError))
            throw new InvalidOperationException("AI 参数无效，任务未执行："+parameterError);
        if(!_tasks.TryGetValue(context.TaskId,out var task))
        {
            if(!IsProbe(context)&&Settings(context.PresetJson,context.InputPath).IsEnabled)
                throw new InvalidOperationException("AI 任务尚未成功准备，拒绝执行缺少中间视频上下文的命令");
            return;
        }
        bool ours=context.Properties.TryGetValue("pluginId",out var id)&&id==PluginId;
        context.Properties.TryGetValue("pluginStepId",out var stepId);
        if(ours)
        {
            EnhancementTaskRegistry.Update(context.TaskId,task.Plan.Input,context.OutputPath,"增强中",context.PhaseName,task.Plan.Settings.ToJson(),task.Plan.BackendDescription);
            if(stepId is not null&&task.Plan.Rtx.TryGetValue(stepId,out var options))
            {
                var runtime=await RtxFrameRuntime.StartAsync(options,task.Plan.Settings,task.Plan.PauseName,task,context,token).ConfigureAwait(false);
                task.Rtx[stepId]=runtime;
            }
        }
        else if(Property(context,"isPluginStep")!="true"&&!IsProbe(context))
        {
            // 首个原生命令前验证视频契约，所有原生遍数复用同一中间视频。
            if(!task.Validated)
            {
                foreach(var video in task.Plan.Videos)
                {
                    token.ThrowIfCancellationRequested();
                    if(!File.Exists(video.Value)||new FileInfo(video.Value).Length==0)throw new InvalidOperationException("AI 后端未生成有效中间视频");
                    var enhanced=await VideoMediaProbe.ProbeAsync(_ffprobe(),video.Value,false,token).ConfigureAwait(false);
                    var original=task.Plan.Sources[video.Key];
                    if(enhanced.Videos.Count!=1||enhanced.StreamCount!=1)throw new InvalidOperationException("AI 中间文件必须仅包含一个视频流");
                    if(enhanced.Videos[0].CodecName!="ffv1"||enhanced.Videos[0].Frames<=0||enhanced.Videos[0].FrameRate<=0||enhanced.Videos[0].Width<=0||enhanced.Videos[0].Height<=0)
                        throw new InvalidOperationException("AI 中间视频没有有效帧或未按无损 FFV1 契约写入");
                    if(original.DurationKnown&&original.Duration>0&&enhanced.Videos[0].Duration>0&&Math.Abs(enhanced.Videos[0].Duration-original.Duration)>Math.Max(0.15,2/original.FrameRate))
                        throw new InvalidOperationException("AI 输出时长与原视频不一致，拒绝将不同时间轴交给宿主混流");
                }
                task.Validated=true;
            }
            EnhancementTaskRegistry.Update(context.TaskId,task.Plan.Input,context.OutputPath,"编码中","宿主 FFmpeg："+context.PhaseName,task.Plan.Settings.ToJson(),task.Plan.BackendDescription);
        }
    }

    private async ValueTask AfterProcessAsync(ExtPluginPipelineContext context,CancellationToken token)
    {
        if(!_tasks.TryGetValue(context.TaskId,out var task))return;
        if(Property(context,"pluginId")!=PluginId)return;
        var stepId=Property(context,"pluginStepId")??"";
        if(task.Rtx.TryRemove(stepId,out var runtime))
        {
            try
            {
                if(context.ExitCode!=0)runtime.Cancel();
                var result=await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(30),token).ConfigureAwait(false);
                if(!result.Succeeded)
                {
                    context.ExitCode=1;
                    context.ReportProgress("RTX 后端失败："+result.Error);
                }
            }
            finally{await runtime.DisposeAsync();}
        }
        if(task.FatalBackendError)context.ExitCode=1;
    }

    private async ValueTask FinishAsync(ExtPluginPipelineContext context,CancellationToken _)
    {
        _parameterFailures.TryRemove(context.TaskId,out var ignoredError);
        if(context.TaskStatus==ExtPluginTaskStatus.Canceled)EnhancementTaskRegistry.SetStatus(context.TaskId,"已停止");
        else if(context.TaskStatus==ExtPluginTaskStatus.Succeeded)EnhancementTaskRegistry.SetStatus(context.TaskId,"已完成");
        if(_tasks.TryRemove(context.TaskId,out var task))
        {
            await task.DisposeAsync();
        }
    }

    private void AddOverview(ExtPluginPresetOverviewContext context)
    {
        var settings=EnhancementSettings.FromJson(context.PluginStateJson);
        if(!settings.IsEnabled)return;
        context.Rows.Add(new("视频参数 | AI增强："+settings.Summary()));
        if(settings.UpscaleEnabled)context.Rows.Add(new($"超分输出倍率：{(settings.OutputScale==0?"模型原生":settings.OutputScale+"x")}；分块：{settings.UpscaleTileSize}"));
        if(settings.InterpEnabled)context.Rows.Add(new($"补帧转场阈值：{settings.SceneDetectThreshold:g}；动态光流：{settings.InterpDynamicScaledOpticalFlow}；精度：{(settings.InterpHalfPrecision?"auto":"float32")}"));
        if(settings.SegmentedEnabled)
            {
                foreach(var video in settings.SegmentedVideos.Where(video=>video.Enabled))
                    context.Rows.Add(new("分段："+Path.GetFileName(video.Path)+" "+JsonSerializer.Serialize(video.Segments)));
                context.Rows.Add(new("未配置分段的文件沿用普通 AI 开关；通用命令预览以首个启用的分段方案为例"));
            }
        if((settings.UpscaleEnabled&&settings.Backend=="rtxvsr")||settings.RtxHdrEnabled)
        {
            context.Rows.Add(new("RTX 服务启动："+WindowsArguments.Quote(RtxVideoBackendClient.FindBackend(PortablePaths.CoreRoot))+" --port <运行时本地端口> --app-session-id <任务会话 ID>"));
            var request=RtxVideoBackendClient.CreateJobJson("<选中视频的无损输入>","<中间视频>",settings.UpscaleEnabled&&settings.Backend=="rtxvsr",settings.RtxQuality,
                settings.RtxTarget.EndsWith('x')&&double.TryParse(settings.RtxTarget[..^1],out var factor)?factor:1,
                settings.RtxHdrEnabled,settings.RtxHdrContrast,settings.RtxHdrSaturation,settings.RtxHdrMiddleGray,settings.RtxHdrMaxLuminance,
                settings.RtxHdrEnabled?"hevc":"h264","rawvideo","none","p010le",new Dictionary<string,string>(),[],[],@"\\.\pipe\<任务帧管道>");
            context.Rows.Add(new("RTX POST /api/jobs："+request));
            context.Rows.Add(new("RTX 目标："+settings.RtxTarget+"；端口、输入尺寸与最终 scale 在执行前确定并写入任务结果"));
            if(settings.InterpEnabled)context.Rows.Add(new("RTX VSR 与补帧组合的实际顺序：先 RVE 补帧，再 RTX 增强"));
        }
        context.Rows.Add(new("中间视频：仅视频 FFV1 / MKV；最终编码、音频、字幕、章节、元数据及后处理由宿主预设控制"));
    }

    // 官方队列事件用于界面状态、RVE 遥测和暂停。不会接管宿主队列或改写全局 FFmpeg 路径。
    public void OnQueueEvent(string name,string json)
    {
        try { OnQueueEventCore(name,json); }
        catch(Exception ex) { ReportParameterError("","","",json,ex); }
    }
    private void OnQueueEventCore(string name,string json)
    {
        using var doc=JsonDocument.Parse(json);
        if(!doc.RootElement.TryGetProperty("task",out var item))return;
        string id=GetString(item,"id");
        if(name=="task.added"&&!EnhancementTaskRegistry.Snapshot().Any(task=>task.TaskId==id))return;
        string state=GetString(item,"status");
        if(_tasks.TryGetValue(id,out var scope))
        {
            if(name=="task.paused")scope.SetPaused(true);
            else if(name=="task.resumed")scope.SetPaused(false);
            else if(name is "task.stopped" or "task.removed")scope.Cancel();
            if(doc.RootElement.TryGetProperty("log",out var log)&&log.ValueKind==JsonValueKind.Object)
            {
                string line=GetString(log,"text");
                if(line.Contains("VIDEOENHANCER_FATAL",StringComparison.Ordinal)||line.Contains("Traceback (most recent call last)",StringComparison.Ordinal))scope.FatalBackendError=true;
                var total=Regex.Match(line,@"Total Output Frames:\s*(\d+)");
                if(total.Success)scope.TotalFrames=long.Parse(total.Groups[1].Value,CultureInfo.InvariantCulture);
                var frame=Regex.Match(line,@"FPS:\s*([\d.]+).*Current Frame:\s*(\d+)");
                if(frame.Success)EnhancementTaskRegistry.SetTelemetry(id,long.Parse(frame.Groups[2].Value,CultureInfo.InvariantCulture),scope.TotalFrames,double.Parse(frame.Groups[1].Value,CultureInfo.InvariantCulture));
            }
        }
        if(name is "task.paused" or "task.resumed" or "task.stopped" or "task.completed" or "task.failed" or "task.reset" or "task.removed")
            EnhancementTaskRegistry.SetStatus(id,(name=="task.stopped"&&scope is not null)?"正在停止":StatusText(state));
    }
    private static string StatusText(string state)=>state switch
    {
        "pending"=>"排队中","running"=>"正在处理","paused"=>"已暂停","succeeded"=>"已完成","failed"=>"失败","canceled"=>"已停止","removed"=>"已移除",_=>state
    };
    private static string GetString(JsonElement item,string key)=>item.TryGetProperty(key,out var value)&&value.ValueKind!=JsonValueKind.Null?value.ToString():"";
    public void TrackQueuedTask(string id,string input,string output,string preset)
    {
        try
        {
            var settings=Settings(preset,input);
            if(settings.IsEnabled)EnhancementTaskRegistry.Update(id,input,output,"排队中","等待宿主启动",settings.ToJson());
        }
        catch(Exception ex) { ReportParameterError(id,input,output,preset,ex); }
    }
    public void Dispose()
    {
        foreach(var registration in _registrations)registration.Dispose();
        foreach(var item in _tasks.ToArray())
            if(_tasks.TryRemove(item.Key,out var task)){task.Cancel();_ = Task.Run(async()=>await task.DisposeAsync());}
    }
    internal static void DeleteWorkDirectory(string work)
    {
        string root=Path.GetFullPath(PortablePaths.WorkRoot).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string target=Path.GetFullPath(work);
        if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("拒绝清理工作目录之外的路径");
        if(Directory.Exists(target))Directory.Delete(target,true);
    }

    internal sealed class TaskScope : IAsyncDisposable
    {
        internal readonly EnhancementPlan Plan;
        internal readonly BackendInvocation Preparation;
        private readonly Action<string> _cleanupWarning;
        private int _disposed;
        private readonly MemoryMappedFile _pause;
        private readonly MemoryMappedViewAccessor _pauseView;
        internal readonly ConcurrentDictionary<string,RtxFrameRuntime> Rtx=new();
        internal readonly CancellationTokenSource Lifetime=new();
        internal CancellationTokenRegistration Cancellation;
        internal volatile bool Paused,FatalBackendError;
        internal bool Validated;
        internal long TotalFrames;
        internal TaskScope(EnhancementPlan plan,BackendInvocation invocation,Action<string> cleanupWarning)
        {
            Plan=plan;Preparation=invocation;_cleanupWarning=cleanupWarning;
            _pause=MemoryMappedFile.CreateNew(plan.PauseName,8);_pauseView=_pause.CreateViewAccessor();_pauseView.Write(0,(byte)0);
        }
        internal void SetPaused(bool paused){if(Volatile.Read(ref _disposed)!=0)return;Paused=paused;try{_pauseView.Write(0,(byte)(paused?1:0));}catch(ObjectDisposedException){}}
        internal void Cancel(){try{Lifetime.Cancel();}catch(ObjectDisposedException){}Preparation.Stop();foreach(var runtime in Rtx.Values)runtime.Cancel();}
        public async ValueTask DisposeAsync()
        {
            if(Interlocked.Exchange(ref _disposed,1)!=0)return;
            Cancellation.Dispose();Cancel();
            foreach(var runtime in Rtx.Values)await runtime.DisposeAsync();
            Rtx.Clear();_pauseView.Dispose();_pause.Dispose();Lifetime.Dispose();
            for(int attempt=0;attempt<3;attempt++)
            {
                try{DeleteWorkDirectory(Plan.WorkDirectory);break;}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException)
                {
                    if(attempt==2)_cleanupWarning("中间目录清理失败："+Plan.WorkDirectory+"；"+error.Message);
                    else await Task.Delay(100*(attempt+1)).ConfigureAwait(false);
                }
            }
        }
    }
}

internal sealed class RtxFrameRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancel;
    private readonly NamedPipeServerStream _source,_destination;
    private readonly RtxVideoBackendClient _client;
    private readonly Task _relay;
    internal Task<RtxVideoBackendClient.JobResult> Completion {get;}
    private RtxFrameRuntime(RtxVideoBackendClient client,NamedPipeServerStream source,NamedPipeServerStream destination,
        CancellationTokenSource cancel,RtxStepOptions options,EnhancementSettings settings,ExtVideoPipeline.TaskScope scope,ExtPluginPipelineContext context)
    {
        _client=client;_source=source;_destination=destination;_cancel=cancel;
        _relay=Task.Run(async()=>{
            await Task.WhenAll(_source.WaitForConnectionAsync(cancel.Token),_destination.WaitForConnectionAsync(cancel.Token)).ConfigureAwait(false);
            await _source.CopyToAsync(_destination,4*1024*1024,cancel.Token).ConfigureAwait(false);
            await _destination.FlushAsync(cancel.Token).ConfigureAwait(false);
            _destination.Dispose();
        },cancel.Token);
        Completion=Task.Run(async()=>{
            try
            {
                var result=await client.RunAsync(options.Input,options.Output,options.Vsr,settings.RtxQuality,options.Scale,
                    options.Hdr,settings.RtxHdrContrast,settings.RtxHdrSaturation,settings.RtxHdrMiddleGray,settings.RtxHdrMaxLuminance,
                    options.Hdr?"hevc":"h264","rawvideo","none","p010le",new Dictionary<string,string>(),[],[],
                    @"\\.\pipe\"+options.PipeName+"-source",()=>cancel.IsCancellationRequested,()=>scope.Paused,cancel.Token).ConfigureAwait(false);
                if(!result.Succeeded){cancel.Cancel();_source.Dispose();_destination.Dispose();}
                else await _relay.WaitAsync(TimeSpan.FromSeconds(30),cancel.Token).ConfigureAwait(false);
                return result;
            }
            catch(Exception ex){cancel.Cancel();_source.Dispose();_destination.Dispose();return new(false,cancel.IsCancellationRequested,ex.Message,[]);}
        });
    }
    internal static async Task<RtxFrameRuntime> StartAsync(RtxStepOptions options,EnhancementSettings settings,string pauseName,
        ExtVideoPipeline.TaskScope scope,ExtPluginPipelineContext context,CancellationToken token)
    {
        var cancel=CancellationTokenSource.CreateLinkedTokenSource(token,scope.Lifetime.Token);
        RtxVideoBackendClient? client=null;NamedPipeServerStream? source=null,destination=null;
        try
        {
            client=await RtxVideoBackendClient.StartAsync(RtxVideoBackendClient.FindBackend(PortablePaths.CoreRoot),cancel.Token).ConfigureAwait(false);
            client.Progress=line=>context.ReportProgress(line);
            var caps=await client.GetCapabilitiesAsync(cancel.Token).ConfigureAwait(false);
            if(!caps.D3d11Available||!caps.RtxSdkFound||(options.Vsr&&!caps.VsrAvailable)||(options.Hdr&&!caps.TruehdrAvailable))
                throw new InvalidOperationException("RTX 运行环境不支持当前任务："+string.Join("；",caps.Messages));
            source=new(options.PipeName+"-source",PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4*1024*1024,4*1024*1024);
            destination=new(options.PipeName,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4*1024*1024,4*1024*1024);
            string json=RtxVideoBackendClient.CreateJobJson(options.Input,options.Output,options.Vsr,settings.RtxQuality,options.Scale,options.Hdr,
                settings.RtxHdrContrast,settings.RtxHdrSaturation,settings.RtxHdrMiddleGray,settings.RtxHdrMaxLuminance,options.Hdr?"hevc":"h264",
                "rawvideo","none","p010le",new Dictionary<string,string>(),[],[],@"\\.\pipe\"+options.PipeName+"-source");
            context.ReportResult("ai.rtx.command",client.StartCommand,"RTX 实际启动命令");
            context.ReportResult("ai.rtx.request",json,"RTX 实际请求 JSON");
            EnhancementTaskRegistry.SetBackendDetails(context.TaskId,"RTX 实际启动："+client.StartCommand+Environment.NewLine+"RTX POST /api/jobs："+json);
            context.ReportProgress("RTX 实际启动："+client.StartCommand);
            context.ReportProgress("RTX POST /api/jobs："+json);
            return new(client,source,destination,cancel,options,settings,scope,context);
        }
        catch{source?.Dispose();destination?.Dispose();client?.Dispose();cancel.Dispose();throw;}
    }
    internal void Cancel(){try{_cancel.Cancel();}catch(ObjectDisposedException){}_source.Dispose();_destination.Dispose();}
    public async ValueTask DisposeAsync()
    {
        Cancel();
        try{await Completion.WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){}catch(OperationCanceledException){}
        try{await _relay.WaitAsync(TimeSpan.FromSeconds(1));}catch(Exception){}
        _client.Dispose();_cancel.Dispose();
    }
}
