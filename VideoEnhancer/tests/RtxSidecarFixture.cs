using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal sealed class RtxSidecarFixture
{
    private readonly CancellationTokenSource _shutdown=new();
    private readonly string _session;
    private volatile bool _paused;
    private volatile string _state="running";
    private string _error="",_work="";
    private int _frames;
    private long _total;
    private RtxSidecarFixture(string session)=>_session=session;

    internal static async Task<int> Run(string[] args)
    {
        int port=int.Parse(args[Array.IndexOf(args,"--port")+1]);
        var fixture=new RtxSidecarFixture(args[Array.IndexOf(args,"--app-session-id")+1]);
        var listener=new TcpListener(IPAddress.Loopback,port);listener.Start();
        try
        {
            while(!fixture._shutdown.IsCancellationRequested)
            {
                var connection=await listener.AcceptTcpClientAsync(fixture._shutdown.Token);
                _=fixture.Handle(connection);
            }
        }
        catch(OperationCanceledException){}
        finally{listener.Stop();}
        return 0;
    }

    private async Task Handle(TcpClient connection)
    {
        using(connection)
        {
            var stream=connection.GetStream();
            try
            {
                var header=new List<byte>();
                while(header.Count<32768)
                {
                    var single=new byte[1];
                    if(await stream.ReadAsync(single,_shutdown.Token)==0)return;
                    header.Add(single[0]);
                    if(header.Count>=4&&header[^4]==13&&header[^3]==10&&header[^2]==13&&header[^1]==10)break;
                }
                var lines=Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var request=lines[0].Split(' ');
                var headers=lines.Skip(1).Where(line=>line.Contains(':')).Select(line=>line.Split(':',2))
                    .ToDictionary(pair=>pair[0],pair=>pair[1].Trim(),StringComparer.OrdinalIgnoreCase);
                if(!headers.TryGetValue("X-App-Session-Id",out var session)||session!=_session)
                {await Reply(stream,401,"{}");return;}
                int length=headers.TryGetValue("Content-Length",out var rawLength)?int.Parse(rawLength):0;
                var bytes=new byte[length];await stream.ReadExactlyAsync(bytes,_shutdown.Token);
                string body=Encoding.UTF8.GetString(bytes),endpoint=request[1];
                string response="{}";
                if(endpoint=="/api/capabilities")
                    response="""{"d3d11Available":true,"rtxSdkFound":true,"vsrAvailable":true,"truehdrAvailable":true,"nvencH264Available":false,"nvencHevcMain10Available":false,"nvencAv1Available":false,"messages":[]}""";
                else if(endpoint=="/api/jobs"&&request[0]=="POST")
                {
                    using var json=JsonDocument.Parse(body);
                    _work=Path.GetDirectoryName(json.RootElement.GetProperty("inputPath").GetString())!;
                    await File.WriteAllTextAsync(Path.Combine(_work,"rtx-request.json"),body);
                    await File.WriteAllTextAsync(Path.Combine(_work,"rtx-pid.txt"),Environment.ProcessId.ToString());
                    // 仅模拟协议和原始帧传输；本测试不模拟显卡推理质量。
                    _=ProduceFrames(body);
                    response="""{"id":"fixture-job"}""";
                }
                else if(endpoint.EndsWith("/pause"))
                {_paused=true;await File.WriteAllTextAsync(Path.Combine(_work,"rtx-paused"),"1");}
                else if(endpoint.EndsWith("/resume"))
                {_paused=false;await File.WriteAllTextAsync(Path.Combine(_work,"rtx-resumed"),"1");}
                else if(endpoint.EndsWith("/cancel")){_state="canceled";_paused=false;}
                else if(endpoint=="/api/jobs/fixture-job")
                    response=JsonSerializer.Serialize(new{state=_state,framesDone=_frames,framesTotal=_total,fps=25,etaSeconds=0,error=new{message=_error}});
                await Reply(stream,200,response);
                if(endpoint=="/api/app/shutdown")_shutdown.Cancel();
            }
            catch(Exception ex){System.Console.Error.WriteLine(ex);}
        }
    }
    private static async Task Reply(NetworkStream stream,int status,string body)
    {
        byte[] data=Encoding.UTF8.GetBytes(body);
        byte[] header=Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Type: application/json\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);await stream.WriteAsync(data);await stream.FlushAsync();
    }
    private async Task ProduceFrames(string request)
    {
        try
        {
            using var json=JsonDocument.Parse(request);var root=json.RootElement;
            string input=root.GetProperty("inputPath").GetString()!;
            var output=root.GetProperty("output");
            if(output.GetProperty("container").GetString()!="rawvideo"||output.GetProperty("audioMode").GetString()!="none"||
                output.GetProperty("subtitleMode").GetString()!="none"||output.GetProperty("audioStreamIndices").GetArrayLength()!=0||output.GetProperty("subtitleStreamIndices").GetArrayLength()!=0)
                throw new InvalidOperationException("RTX 请求必须仅传视频原始帧");
            string ffprobe=Environment.GetEnvironmentVariable("VIDEOENHANCER_TEST_FFPROBE")??"ffprobe.exe";
            var media=await VideoEnhancer.VideoMediaProbe.ProbeAsync(ffprobe,input,true,_shutdown.Token);
            var source=media.Videos[0];
            double scale=root.GetProperty("processing").GetProperty("vsr").GetProperty("scale").GetDouble();
            int width=(int)Math.Round(source.Width*scale),height=(int)Math.Round(source.Height*scale);
            if((width&1)!=0)width++;if((height&1)!=0)height++;
            bool hdr=root.GetProperty("processing").GetProperty("hdr").GetProperty("enabled").GetBoolean();
            string pixel=hdr?"x2bgr10le":"p010le";
            string pipe=output.GetProperty("framePipePath").GetString()!;
            _total=source.Frames;
            using var client=new NamedPipeClientStream(".",pipe[@"\\.\pipe\".Length..],PipeDirection.Out,PipeOptions.Asynchronous);
            await client.ConnectAsync(10000,_shutdown.Token);
            string ffmpeg=Environment.GetEnvironmentVariable("VIDEOENHANCER_TEST_FFMPEG")??"ffmpeg.exe";
            var start=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"-v","error","-i",input,"-vf",$"scale={width}:{height}","-pix_fmt",pixel,"-f","rawvideo","-"})start.ArgumentList.Add(arg);
            using var process=new Process{StartInfo=start};process.Start();
            using var stop=_shutdown.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
            var error=process.StandardError.ReadToEndAsync();
            int frameBytes=hdr?width*height*4:width*height*3;
            byte[] frame=new byte[frameBytes];
            await Task.Delay(250,_shutdown.Token);
            while(true)
            {
                int used=0;
                while(used<frame.Length)
                {
                    int count=await process.StandardOutput.BaseStream.ReadAsync(frame.AsMemory(used),_shutdown.Token);
                    if(count==0)break;
                    used+=count;
                }
                if(used==0)break;
                if(used!=frame.Length)throw new InvalidDataException("原始帧不完整");
                while(_paused)await Task.Delay(20,_shutdown.Token);
                if(_state=="canceled")break;
                await client.WriteAsync(frame,_shutdown.Token);_frames++;
                await Task.Delay(10,_shutdown.Token);
            }
            await client.FlushAsync(_shutdown.Token);
            await process.WaitForExitAsync(_shutdown.Token);
            if(process.ExitCode!=0)throw new InvalidOperationException(await error);
            if(_state!="canceled")_state="succeeded";
        }
        catch(Exception ex){_error=ex.Message;_state="failed";System.Console.Error.WriteLine(ex);}
    }
}
