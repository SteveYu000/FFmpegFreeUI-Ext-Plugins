using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FFmpegFreeUI.Ext.PluginSdk;
using VideoEnhancer;

internal static partial class Program
{
    private sealed class ProgressClock : TimeProvider
    {
        private long _stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _stamp;
        internal void Advance(int seconds) => _stamp += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private static void TestUpstreamPort()
    {
        System.Console.WriteLine("运行：上游倍率命令、初始帧偏移和组件哈希更新");
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = typeof(BackendServices).Assembly.GetType("VideoEnhancer.RenderProgressTracker", true)!;
        var clock = new ProgressClock();
        var tracker = Activator.CreateInstance(type, instance, null, [clock], null)!;
        string Line(string value) => (string)type.GetMethod("RewriteLine", instance)!.Invoke(tracker, [value])!;
        void Pause(bool value) => type.GetMethod("SetPaused", instance)!.Invoke(tracker, [value]);
        Line("Total Output Frames: 1000");
        const string first = "FPS: 999 Current Frame: 100 ETA: 0:00:00";
        Check(Line(first) == first, "首条进度只建立采样基准");
        clock.Advance(2);
        Check(Line("FPS: 999 Current Frame: 120 ETA: 0:00:00").Contains("FPS: 10.00 Current Frame: 120 ETA: 0:01:28"),
            "100 帧初始偏移不计入实测速度和剩余时间");
        Pause(true); clock.Advance(20); Pause(false); clock.Advance(2);
        Check(Line("FPS: 999 Current Frame: 140 ETA: 0:00:00").Contains("FPS: 10.00 Current Frame: 140 ETA: 0:01:26"),
            "暂停时长不降低速度或增加剩余时间");
        Check(Line("原样保留普通日志") == "原样保留普通日志", "进度修正不改写无关输出");
        Check(Line("FPS: 0 Current Frame: 1 ETA: 0:00:00").StartsWith("FPS: 0"), "帧号重置后重新建立基准");

        string runtime=Path.Combine(_root,"upstream-runtime"),models=Path.Combine(runtime,"models");
        void FileAt(string relative)
        {
            string path=Path.Combine(models,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,[1]);
        }
        FileAt("PTH/realesr-animevideov3.pth");
        FileAt("Param-Bin/fixture-4x/model.param");FileAt("Param-Bin/fixture-4x/model.bin");
        FileAt("ONNX/RealESRGAN-x4-jp-Illustration-fix1-d.onnx");
        FileAt("FlashVSR/model.pth");
        FileAt("BasicVSR++/basicvsr_plusplus_c64n7_8x1_600k_reds4_20210217-db622b2f.pth");
        BackendServices.ConfigureRuntime(runtime);
        var host=new TestHost();using(var pipeline=new ExtVideoPipeline(host,()=>Ffmpeg,()=>Ffprobe))
        {
            foreach(var (backend,model) in new[]{("ncnn","Param-Bin/fixture-4x"),("cuda","PTH/realesr-animevideov3.pth"),
                ("tensorrt","PTH/realesr-animevideov3.pth"),("onnx","ONNX/RealESRGAN-x4-jp-Illustration-fix1-d.onnx"),
                ("flashvsr","FlashVSR"),("basicvsrpp","BasicVSR++/basicvsr_plusplus_c64n7_8x1_600k_reds4_20210217-db622b2f.pth")})
            {
                var settings=new EnhancementSettings{UpscaleEnabled=true,Backend=backend,Model=model,OutputScale=2};
                var context=new ExtPluginCommandContext{IsPreview=true,InputPath="<输入文件>",PresetJson=Preset(settings)};
                foreach(var provider in host.CommandsImpl.Steps)provider.Callback(context);
                var args=WindowsArguments.Parse(context.Steps.Last().Arguments).ToList();
                string Value(string key)=>args[args.IndexOf(key)+1];
                Check(Value("--output-scale")=="2",backend+" 启动器传入目标倍率");
                if(backend is not ("flashvsr" or "basicvsrpp"))
                    Check(Value("--target-script").EndsWith("rve-ordered-backend.py")&&Value("--override_upscale_scale")=="2",
                        backend+" 单独超分也在输出或补帧前落实目标尺寸");
                else Check(Value("--target-script").EndsWith("rve-backend.py"),backend+" 保留专用时序后端入口");
            }
        }
        // 模型管理与参数菜单共享多倍率显示名称。
        var catalog=Capture(new(BackendAction.ListModelCatalog){Backend="tensorrt"});
        Check(catalog.Code==0&&catalog.Output.Contains("realesr-animevideov3 2/3/4x"), "TensorRT 清单标明官方 4x 权重支持的图内输出倍率");

        var status=typeof(videoenhancer.PluginPanel).Assembly.GetType("videoenhancer.DownloadInstallStatus",true)!;
        const BindingFlags statics=BindingFlags.NonPublic|BindingFlags.Static;
        bool Installed(string remote)=>(bool)status.GetMethod("IsDownloadInstalled",statics)!.Invoke(null,["Bin/ffmpeg.7z",runtime,remote])!;
        string marker=(string)status.GetMethod("ComponentArchiveMarkerPath",statics)!.Invoke(null,[runtime,"Bin/ffmpeg.7z"])!;
        string core=Path.Combine(runtime,"bin","ffmpeg","ffmpeg.exe");Directory.CreateDirectory(Path.GetDirectoryName(core)!);File.WriteAllBytes(core,[1]);
        Check(!Installed("AAA"),"只有核心文件的组件需要核验版本");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);File.WriteAllText(marker,"AAA",Encoding.UTF8);
        Check(Installed("aaa")&&!Installed("BBB"),"同日期同路径内容更新由 SHA-256 区分");
        File.WriteAllText(Path.Combine(runtime,"bin","ffmpeg.7z.pending"),"处理中");
        Check(!Installed("AAA"),"未完成组件替换不会显示安装成功");
        Check(typeof(videoenhancer.PluginPanel).Assembly.GetType("videoenhancer.QuadGridForm") is null,
            "最终前端不再包含已取消的四宫格窗体");
        TestHttpDownload(runtime);
        BackendServices.ConfigureRuntime(Path.Combine(_root,"runtime"));
    }

    private static void TestHttpDownload(string runtime)
    {
        string project=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        string aria=Environment.GetEnvironmentVariable("VIDEOENHANCER_TEST_ARIA2")??Path.Combine(project,"Backend","obj","third-party","aria2-next","2.8.3","aria2-next.exe");
        if(!File.Exists(aria)){System.Console.WriteLine("跳过：未提供 aria2-next 的 HTTP 集成测试");return;}
        string executable=Path.Combine(runtime,"bin","aria2-next","aria2-next.exe");Directory.CreateDirectory(Path.GetDirectoryName(executable)!);File.Copy(aria,executable);
        byte[] payload=Encoding.UTF8.GetBytes(new string('x',8192));
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server=Task.Run(async()=>
        {
            using var client=await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream=client.GetStream();
            using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);
            while(await reader.ReadLineAsync(timeout.Token) is {Length:>0}){}
            var header=Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header,timeout.Token);await stream.WriteAsync(payload,timeout.Token);
        });
        var backend=typeof(BackendServices).Assembly;
        const BindingFlags statics=BindingFlags.NonPublic|BindingFlags.Static,fields=BindingFlags.NonPublic|BindingFlags.Instance;
        var invocationType=backend.GetType("VideoEnhancer.BackendInvocation",true)!;
        var invocation=Activator.CreateInstance(invocationType,nonPublic:true)!;
        var cancellation=(CancellationTokenSource)invocationType.GetField("Cancellation",fields)!.GetValue(invocation)!;
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        var slot=invocationType.GetField("Slot",statics)!.GetValue(null)!;
        var value=slot.GetType().GetProperty("Value")!;
        using var errors=new StringWriter();invocationType.GetField("Error",fields)!.SetValue(invocation,errors);
        string destination=Path.Combine(runtime,"downloads","http-fixture.bin");
        try
        {
            value.SetValue(slot,invocation);
            int result=(int)typeof(BackendServices).GetMethod("DownloadWithAria",statics)!.Invoke(null,[$"http://127.0.0.1:{port}/file",destination,true])!;
            Check(result==0,"HTTP 下载成功并正常退出："+errors);
            Check(File.ReadAllBytes(destination).SequenceEqual(payload),"真实 aria2-next 下载字节完整");
            Check(Directory.Exists(Path.Combine(runtime,"cache","aria2-next")),"HTTP 下载续传数据库留在插件便携缓存");
            server.GetAwaiter().GetResult();
        }
        finally
        {
            value.SetValue(slot,null);cancellation.Cancel();timeout.Cancel();listener.Stop();
            try{server.GetAwaiter().GetResult();}catch(OperationCanceledException){}catch(SocketException){}
        }
    }
}
