using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using VideoEnhancer;

internal static partial class Program
{
    private const BindingFlags PreviewFlags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    private static bool RunPreviewToolFixture(string[] args)
    {
        string? root=Environment.GetEnvironmentVariable("VIDEOENHANCER_TEST_PREVIEW_TOOL");
        if(string.IsNullOrEmpty(root)||!args.Contains("-v"))return false;
        bool capture=args.Contains("-frames:v");
        File.AppendAllText(Path.Combine(root,"calls.jsonl"),JsonSerializer.Serialize(args)+"\n");
        File.WriteAllText(Path.Combine(root,capture?"capture.pid":"probe.pid"),Environment.ProcessId.ToString());
        string mode=File.ReadAllText(Path.Combine(root,"mode"));
        if(mode=="stall"||(capture&&mode=="slow-capture"))Thread.Sleep(5000);
        if(capture)
        {
            if(mode=="no-frame")return true;
            using var bitmap=new Bitmap(32,24);
            using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Color.Coral);
            bitmap.Save(Console.OpenStandardOutput(),ImageFormat.Jpeg);
        }
        else if(args.Contains("stream=avg_frame_rate"))Console.WriteLine("24/1");
        else Console.Write(File.ReadAllText(Path.Combine(root,"packets")));
        return true;
    }

    private static void PumpPreview(Func<bool> done,int timeout=4000)
    {
        var clock=Stopwatch.StartNew();
        while(!done()&&clock.ElapsedMilliseconds<timeout){Application.DoEvents();Thread.Sleep(10);}
        Application.DoEvents();
        if(!done())throw new TimeoutException("预览回归等待超时");
    }
    private static object? PreviewField(object value,string name)=>value.GetType().GetField(name,PreviewFlags)!.GetValue(value);
    private static void SetPreviewField(object value,string name,object data)=>value.GetType().GetField(name,PreviewFlags)!.SetValue(value,data);
    private static object? PreviewCall(object value,string name,params object?[] args)=>value.GetType().GetMethod(name,PreviewFlags)!.Invoke(value,args);
    private static void PreviewProperty(object value,string name,object data)=>value.GetType().GetProperty(name,PreviewFlags)!.SetValue(value,data);

    private static void TestPreviewEngine()
    {
        Console.WriteLine("运行：预览异步探测、关键帧、取消与 Ext 阶段输出");
        string root=Path.Combine(_root,"preview");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"mode"),"");
        File.WriteAllText(Path.Combine(root,"packets"),"0,K_\n1,__\n2,K_\n");
        string output=Path.Combine(root,"当前视频.mkv");
        File.WriteAllBytes(output,[1,2,3]);
        string tool=Path.ChangeExtension(typeof(Program).Assembly.Location,".exe");
        var assembly=typeof(videoenhancer.Entry).Assembly;
        var engineType=assembly.GetType("videoenhancer.PreviewEngine",true)!;
        var runtime=assembly.GetType("videoenhancer.HostRuntime",true)!;
        var overrideHost=runtime.GetProperty("TestAssembly",PreviewFlags)!;
        var previousHost=overrideHost.GetValue(null);
        string? previousTool=Environment.GetEnvironmentVariable("VIDEOENHANCER_TEST_PREVIEW_TOOL");
        using var owner=new Control();
        _=owner.Handle;
        var task=new FFmpegFreeUI.PreviewTaskFixture{输入文件=output,输出文件=Path.Combine(root,"最终未封装.mkv")};
        FFmpegFreeUI.编码队列_v6.Tasks=[task];
        overrideHost.SetValue(null,typeof(FFmpegFreeUI.编码队列_v6).Assembly);
        Environment.SetEnvironmentVariable("VIDEOENHANCER_TEST_PREVIEW_TOOL",root);
        int frames=0;
        var frameHashes=new HashSet<string>();
        object CreateEngine()
        {
            object engine=Activator.CreateInstance(engineType,new videoenhancer.PluginConfig(),owner)!;
            PreviewCall(engine,"Start");
            ((System.Windows.Forms.Timer)PreviewField(engine,"_timer")!).Stop();
            SetPreviewField(engine,"_ffmpeg",tool);SetPreviewField(engine,"_ffprobe",tool);
            SetPreviewField(engine,"_lastTaskId",task.ID);
            ((IDictionary)PreviewField(engine,"_inputFpsCache")!)[task.输入文件]=24d;
            Action<object,Image> onFrame=(_,image)=>
            {
                using(image)using(var bytes=new MemoryStream())
                {image.Save(bytes,ImageFormat.Png);frameHashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())));}
                frames++;
            };
            var ready=engineType.GetEvent("FrameReady")!;
            ready.AddEventHandler(engine,Delegate.CreateDelegate(ready.EventHandlerType!,onFrame.Target,onFrame.Method));
            return engine;
        }
        void Tick(object engine)=>PreviewCall(engine,"OnTick",null,EventArgs.Empty);
        bool Idle(object engine)=>!(bool)PreviewField(engine,"_busy")!&&!(bool)PreviewField(engine,"_tickBusy")!;
        void Mode(string value)=>File.WriteAllText(Path.Combine(root,"mode"),value);
        string[][] Calls()=>File.Exists(Path.Combine(root,"calls.jsonl"))?File.ReadAllLines(Path.Combine(root,"calls.jsonl")).Select(line=>JsonSerializer.Deserialize<string[]>(line)!).ToArray():[];
        void Rewind(object engine)=>SetPreviewField(engine,"_lastExtractStartAt",DateTime.MinValue);
        try
        {
            using(var engine=(IDisposable)CreateEngine())
            {
                task.当前步骤=new(){显示名称="音频编码",实际执行参数="-i input -vn audio.flac"};
                task.进度.当前时间=TimeSpan.FromSeconds(80);
                int before=frames;
                Tick(engine);
                Check(!(bool)PreviewField(engine,"_busy")!&&frames==before,"音频阶段不抽视频帧或消费视频进度");
                task.当前步骤=new(){显示名称="视频编码",实际执行参数=WindowsArguments.Join(["-i",output,"-an",output,"-y"])};
                task.当前进程ID++;
                task.进度.当前时间=TimeSpan.FromSeconds(.2);
                Tick(engine);PumpPreview(()=>frames>before&&Idle(engine));
                Check(Calls().Any(args=>Array.IndexOf(args,"-i") is var i&&i>=0&&args[i+1]==output),"读取当前视频步骤输出，不等待最终封装路径");
                task.进度.当前时间=TimeSpan.FromSeconds(70);Rewind(engine);Tick(engine);PumpPreview(()=>Idle(engine));
                before=frames;task.进度.当前时间=TimeSpan.FromSeconds(.3);
                Tick(engine);PumpPreview(()=>frames>before&&Idle(engine));
                Check((double)PreviewField(engine,"_lastProgressSeconds")! == .3,"同任务进度回退后新阶段恢复低时间预览");
            }
            // 同一宿主任务中，RVE 的帧号、RTX 的原生进度和音频阶段必须相互隔离。
            string enhanced=Path.Combine(root,"AI 中间视频.mkv");
            File.WriteAllBytes(enhanced,[4,5,6]);
            typeof(EnhancementTaskRegistry).GetMethod("SetPreviewSources",PreviewFlags)!.Invoke(null,
                [task.ID,new Dictionary<string,EnhancementPreviewSource>{{"rve-0",new(enhanced,48,true)},{"rtx-0",new(enhanced,48,false)}}]);
            using(var engine=(IDisposable)CreateEngine())
            {
                task.当前步骤=new(){是插件步骤=true,插件ID="videoenhancer",插件步骤ID="rve-0",进程文件名="python.exe",
                    显示名称="RVE 视频增强",实际执行参数="launch.py --ffprobe-path C:/tools/ffprobe.exe"};
                task.进度.当前时间=TimeSpan.FromSeconds(80);
                EnhancementTaskRegistry.SetTelemetry(task.ID,48,96,30);
                int before=frames;Tick(engine);PumpPreview(()=>frames>before&&Idle(engine));
                Check((double)PreviewField(engine,"_lastProgressSeconds")! == 1,"RVE 帧号使用补帧后的输出帧率，不读取上阶段音频时间");
                Check(Calls().Last(args=>args.Contains("-frames:v"))[Array.IndexOf(Calls().Last(args=>args.Contains("-frames:v")),"-i")+1]==enhanced,
                    "RVE 从登记的中间视频抽帧，不误取 Python 参数中的工具路径");
                EnhancementTaskRegistry.SetTelemetry(task.ID,12,96,30);before=frames;
                Tick(engine);PumpPreview(()=>frames>before&&Idle(engine));
                Check((double)PreviewField(engine,"_lastProgressSeconds")! == .25,"AI 遥测帧号回退后恢复抽帧");
                task.当前步骤.插件步骤ID="rtx-0";task.当前步骤.进程文件名="ffmpeg.exe";
                task.进度.当前时间=TimeSpan.FromSeconds(.5);before=frames;
                Tick(engine);PumpPreview(()=>frames>before&&Idle(engine));
                Check((double)PreviewField(engine,"_lastProgressSeconds")! == .5,"RTX 写入使用当前 FFmpeg 进度，忽略遗留 RVE 遥测");
                task.当前步骤=new(){显示名称="音频编码",实际执行参数="-vn"};task.当前进程ID++;before=frames;
                Tick(engine);Check(Idle(engine)&&frames==before,"存在旧 AI 遥测时音频阶段仍等待视频");
            }
            task.当前步骤=null;task.输出文件=output;
            using(var engine=(IDisposable)CreateEngine())
            {
                PreviewCall(engine,"SetKeyframeMode",true);
                File.WriteAllText(Path.Combine(root,"packets"),"0,K_\n");
                Mode("no-frame");int before=frames;
                Tick(engine);PumpPreview(()=>Idle(engine));
                Check(frames==before&&(double)PreviewField(engine,"_lastKeyframePts")! == -1,"未写完整的零时间关键帧不被提前消费，也不冒充原帧");
                Mode("");Rewind(engine);Tick(engine);PumpPreview(()=>frames>before&&Idle(engine));
                Check((double)PreviewField(engine,"_lastKeyframePts")! == 0,"文件未增长时重试零时间关键帧并成功显示");
                Check(Calls().Any(args=>Array.IndexOf(args,"-ss") is var i&&i>=0&&args[i+1]=="0"),"零时间关键帧按零秒定位");
                var probe=Calls().Last(args=>args.Contains("-show_packets"));
                Check(probe.Contains("-read_intervals")&&!probe.Contains("-show_frames"),"关键帧采用有界包窗口，不全量解码文件");
                File.AppendAllText(output,"growth");
                File.WriteAllText(Path.Combine(root,"packets"),"1,__\n2,K_\n");
                SetPreviewField(engine,"_lastProbeAt",DateTime.MinValue);Rewind(engine);before=frames;
                Tick(engine);PumpPreview(()=>frames>before&&Idle(engine));
                Check((double)PreviewField(engine,"_lastKeyframePts")! == 2,"增长的输出继续显示新的关键帧");
            }
            foreach(double interval in new[]{.5,1d,2d,3d})
            {
                using var engine=(IDisposable)CreateEngine();
                PreviewProperty(engine,"IntervalSeconds",interval);
                task.输出文件="";task.进度.当前时间=TimeSpan.FromSeconds(.1);
                SetPreviewField(engine,"_lastExtractStartAt",DateTime.UtcNow);Tick(engine);
                task.进度.当前时间=TimeSpan.FromSeconds(.2);Tick(engine);
                SetPreviewField(engine,"_lastExtractStartAt",DateTime.UtcNow.AddSeconds(-interval-.1));
                task.进度.当前时间=TimeSpan.FromSeconds(.3);int before=frames;
                Tick(engine);PumpPreview(()=>frames>before&&Idle(engine));
                Check((double)PreviewField(engine,"_lastProgressSeconds")! == .3,$"{interval}秒模式按上次抽帧累计慢速进度");
            }
            task.输出文件=output;task.进度.当前时间=TimeSpan.FromSeconds(8);
            using(var engine=(IDisposable)CreateEngine())
            {
                PreviewCall(engine,"SetKeyframeMode",true);Mode("stall");
                int calls=Calls().Length;
                var clock=Stopwatch.StartNew();Tick(engine);
                Check(clock.ElapsedMilliseconds<500,"慢探测立即让出 UI 线程");
                int heartbeat=0;using var timer=new System.Windows.Forms.Timer{Interval=20};
                timer.Tick+=(_,_)=>heartbeat++;timer.Start();
                PumpPreview(()=>heartbeat>=5);
                Tick(engine);
                PumpPreview(()=>Idle(engine),4500);
                Check(Calls().Length==calls+1,"慢探测期间 Tick 不重入启动第二个探测进程");
                int pid=int.Parse(File.ReadAllText(Path.Combine(root,"probe.pid")));
                Check(!Process.GetProcesses().Any(p=>p.Id==pid),"探测超时后真实结束子进程");
            }
            using(var engine=(IDisposable)CreateEngine())
            {
                ((IDictionary)PreviewField(engine,"_inputFpsCache")!).Clear();Mode("stall");
                var first=(Task<double>)PreviewCall(engine,"ProbeInputFpsAsync",output,CancellationToken.None)!;
                PumpPreview(()=>first.IsCompleted,4500);
                Check(first.GetAwaiter().GetResult()==0&&!((IDictionary)PreviewField(engine,"_inputFpsCache")!).Contains(output),"探测超时不永久缓存零帧率");
                Mode("");var second=(Task<double>)PreviewCall(engine,"ProbeInputFpsAsync",output,CancellationToken.None)!;
                PumpPreview(()=>second.IsCompleted);
                Check(second.GetAwaiter().GetResult()==24,"同一输入探测超时后可恢复并缓存有效帧率");
            }
            foreach(string action in new[]{"interval","task","hide","stop","audio"})
            {
                using var engine=(IDisposable)CreateEngine();
                Mode("slow-capture");task.当前步骤=null;task.进度.当前时间=TimeSpan.FromSeconds(8);
                File.Delete(Path.Combine(root,"capture.pid"));int before=frames;
                Tick(engine);PumpPreview(()=>File.Exists(Path.Combine(root,"capture.pid")));
                if(action=="interval")PreviewProperty(engine,"IntervalSeconds",2d);
                else if(action=="task")PreviewProperty(engine,"SelectedTaskId","another-task");
                else if(action=="hide")PreviewProperty(engine,"PreviewVisible",false);
                else if(action=="stop")PreviewCall(engine,"Stop_");
                else {task.当前步骤=new(){显示名称="音频编码",实际执行参数="-vn"};task.当前进程ID++;Tick(engine);}
                PumpPreview(()=>Idle(engine));
                Check(frames==before,"旧画面在切换或取消后不会回写："+action);
                Mode("");task.当前步骤=null;
            }
            using(var engine=(IDisposable)CreateEngine())
            {
                task.当前步骤=new(){显示名称="FFprobe获取时长",进程文件名="ffprobe.exe",实际执行参数="-show_format "+output};
                int before=frames;Tick(engine);
                Check(frames==before&&Idle(engine),"探测阶段不误读工具输出为视频");
                task.当前步骤=new(){显示名称="其他插件处理",进程文件名="python.exe",实际执行参数="tool.py --ffprobe-path "+output};
                Tick(engine);Check(frames==before&&Idle(engine),"未知插件进程的末尾参数不会被误认为视频文件");
            }
            // 实际 FFmpeg/FFprobe 验证进程协议及连续画面内容，避免夹具掩盖参数错误。
            string media=Path.Combine(root,"真实预览.mkv");
            SynchronizationContext.SetSynchronizationContext(null);
            Run(Ffmpeg,["-v","error","-y","-f","lavfi","-i","testsrc2=size=96x64:rate=10:duration=2",
                "-c:v","ffv1","-g","10",media]).GetAwaiter().GetResult();
            task.输入文件=media;task.输出文件=media;task.当前步骤=null;frameHashes.Clear();
            using(var engine=(IDisposable)CreateEngine())
            {
                SetPreviewField(engine,"_ffmpeg",Ffmpeg);SetPreviewField(engine,"_ffprobe",Ffprobe);
                ((IDictionary)PreviewField(engine,"_inputFpsCache")!).Clear();
                task.进度.当前时间=TimeSpan.FromSeconds(.2);int before=frames;
                Tick(engine);PumpPreview(()=>frames>before&&Idle(engine));
                task.进度.当前时间=TimeSpan.FromSeconds(1.4);Rewind(engine);before=frames;
                Tick(engine);PumpPreview(()=>frames>before&&Idle(engine));
                Check(frameHashes.Count>=2,"实际 FFmpeg 在不同进度输出不同画面，未返回重复测试图");
                Check((double)((IDictionary)PreviewField(engine,"_inputFpsCache")!)[media]! == 10,"实际 FFprobe 返回并缓存输入帧率");
                PreviewCall(engine,"SetKeyframeMode",true);Tick(engine);PumpPreview(()=>Idle(engine));
                Check((double)PreviewField(engine,"_lastKeyframePts")!>=0,"实际 FFprobe packet 输出可定位并显示关键帧");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("VIDEOENHANCER_TEST_PREVIEW_TOOL",previousTool);
            overrideHost.SetValue(null,previousHost);
            FFmpegFreeUI.编码队列_v6.Tasks=[];
            typeof(EnhancementTaskRegistry).GetMethod("ClearPreviewSources",PreviewFlags)!.Invoke(null,[task.ID]);
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }
}

namespace FFmpegFreeUI
{
    // 只读适配夹具保留当前 Ext 任务字段，不提供旧宿主兼容分支。
    public static class 编码队列_v6
    {
        public static IReadOnlyList<PreviewTaskFixture> Tasks {get;set;}=[];
        public static IReadOnlyList<PreviewTaskFixture> 获取队列快照()=>Tasks;
    }
    public sealed class PreviewTaskFixture
    {
        public string ID {get;set;}="preview-fixture";
        public string 任务名称 {get;set;}="预览夹具";
        public string 输入文件 {get;set;}="";
        public string 输出文件 {get;set;}="";
        public string 命令行 {get;set;}="";
        public bool 正在执行 {get;set;}=true;
        public int 当前进程ID {get;set;}=1;
        public string 状态 {get;set;}="正在执行";
        public PreviewStepFixture? 当前步骤 {get;set;}
        public PreviewProgressFixture 进度 {get;set;}=new();
    }
    public sealed class PreviewStepFixture
    {
        public string 显示名称 {get;set;}="视频编码";
        public string 阶段 {get;set;}="普通单次";
        public string 进程文件名 {get;set;}="";
        public string 工作目录 {get;set;}="";
        public string 实际执行文件名 {get;set;}="";
        public string 实际执行参数 {get;set;}="";
        public string 命令行 {get;set;}="";
        public bool 是插件步骤 {get;set;}
        public string 插件ID {get;set;}="";
        public string 插件步骤ID {get;set;}="";
    }
    public sealed class PreviewProgressFixture
    {
        public TimeSpan 当前时间 {get;set;}
        public TimeSpan 总时长 {get;set;}=TimeSpan.FromSeconds(100);
        public string 当前阶段 {get;set;}="";
        public string 输出大小文本 {get;set;}="";
    }
}

