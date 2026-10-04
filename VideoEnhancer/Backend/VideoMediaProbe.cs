using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace VideoEnhancer;

public sealed record VideoStreamInfo(int Ordinal,int Width,int Height,double FrameRate,string Rate,long Frames,
    double Duration,double RelativeStart,bool Hdr,int Bits,int Rotation,string ColorPrimaries,string ColorTransfer,string ColorSpace,string SampleAspectRatio,
    IReadOnlyDictionary<string,string>? Tags=null,string Disposition="",double FormatStart=0,double FormatDuration=0,bool DurationKnown=true,bool VariableFrameRate=false,string CodecName="");
public sealed record VideoMediaInfo(IReadOnlyList<VideoStreamInfo> Videos,double Duration,double FormatStart=0,int StreamCount=0);

public static class VideoMediaProbe
{
    public static async Task<VideoMediaInfo> ProbeAsync(string ffprobe,string input,bool countFrames,CancellationToken token)
    {
        var start=new ProcessStartInfo(ffprobe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-v","error","-show_streams","-show_format","-of","json"})start.ArgumentList.Add(arg);
        if(countFrames)start.ArgumentList.Add("-count_frames");
        start.ArgumentList.Add(input);
        using var process=new System.Diagnostics.Process{StartInfo=start};
        process.Start();
        using var cancellation=token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
        var output=process.StandardOutput.ReadToEndAsync(token);
        var error=process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        if(process.ExitCode!=0)throw new InvalidOperationException("FFprobe 探测失败："+await error.ConfigureAwait(false));
        using var document=JsonDocument.Parse(await output.ConfigureAwait(false));
        var root=document.RootElement;
        double formatStart=0,duration=0;
        if(root.TryGetProperty("format",out var format)){formatStart=Number(format,"start_time");duration=Number(format,"duration");}
        var videos=new List<VideoStreamInfo>();
        foreach(var stream in root.GetProperty("streams").EnumerateArray())
        {
            if(Text(stream,"codec_type")!="video")continue;
            string average=Text(stream,"avg_frame_rate"),nominal=Text(stream,"r_frame_rate");
            double rate=Rate(average);if(rate<=0){average=nominal;rate=Rate(nominal);}
            int width=(int)Number(stream,"width"),height=(int)Number(stream,"height");
            var rotation=0;
            if(stream.TryGetProperty("side_data_list",out var side))
                foreach(var data in side.EnumerateArray())if(data.TryGetProperty("rotation",out var rotated)&&rotated.TryGetInt32(out var value))rotation=value;
            var transfer=Text(stream,"color_transfer");
            var hdr=transfer is "smpte2084" or "arib-std-b67";
            int bits=(int)Number(stream,"bits_per_raw_sample");
            if(bits==0)bits=Text(stream,"pix_fmt").Contains("16",StringComparison.Ordinal)?16:Text(stream,"pix_fmt").Contains("12",StringComparison.Ordinal)?12:Text(stream,"pix_fmt").Contains("10",StringComparison.Ordinal)?10:8;
            double streamStart=stream.TryGetProperty("start_time",out _)?Number(stream,"start_time"):formatStart;
            long frames=(long)Number(stream,countFrames?"nb_read_frames":"nb_frames");
            double videoDuration=Number(stream,"duration");
            bool durationKnown=videoDuration>0;
            if(!durationKnown&&stream.TryGetProperty("tags",out var durationTags))
            {
                // Matroska 的 DURATION 标签记录结束时间；减去视频首帧时间才能得到视频长度。
                double end=durationTags.EnumerateObject().Where(tag=>tag.Name.Equals("DURATION",StringComparison.OrdinalIgnoreCase)).Select(tag=>Timestamp(tag.Value.ToString())).FirstOrDefault();
                videoDuration=Math.Max(0,end-streamStart);durationKnown=videoDuration>0;
            }
            if(frames>0&&rate>0){videoDuration=frames/rate;durationKnown=true;}
            if(!durationKnown)videoDuration=Math.Max(0,duration-(streamStart-formatStart));
            if(frames<=0&&rate>0&&videoDuration>0)frames=(long)Math.Round(rate*videoDuration);
            var tags=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            if(stream.TryGetProperty("tags",out var tagData))
                foreach(var tag in tagData.EnumerateObject())
                    if(!tag.Name.Equals("DURATION",StringComparison.OrdinalIgnoreCase)&&!tag.Name.StartsWith("BPS",StringComparison.OrdinalIgnoreCase)&&!tag.Name.StartsWith("NUMBER_OF_",StringComparison.OrdinalIgnoreCase)&&!tag.Name.StartsWith("_STATISTICS",StringComparison.OrdinalIgnoreCase))
                        tags[tag.Name]=tag.Value.ToString();
            var dispositions=new List<string>();
            if(stream.TryGetProperty("disposition",out var dispositionData))
                foreach(var flag in dispositionData.EnumerateObject())if(flag.Value.TryGetInt32(out var value)&&value!=0)dispositions.Add(flag.Name);
            bool vfr=rate>0&&Rate(nominal)>0&&Math.Abs(rate-Rate(nominal))>Math.Max(0.02,rate*0.001);
            videos.Add(new(videos.Count,width,height,rate,average,frames,videoDuration,streamStart-formatStart,hdr,bits,rotation,
                Text(stream,"color_primaries"),transfer,Text(stream,"color_space"),Text(stream,"sample_aspect_ratio"),tags,string.Join("+",dispositions),formatStart,duration,durationKnown,vfr,Text(stream,"codec_name")));
        }
        return new(videos,duration,formatStart,root.GetProperty("streams").GetArrayLength());
    }
    private static string Text(JsonElement element,string key)=>element.TryGetProperty(key,out var value)?value.ToString():"";
    private static double Number(JsonElement element,string key)=>double.TryParse(Text(element,key),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)?value:0;
    public static double Timestamp(string value)
    {
        var parts=value.Split(':');double result=0;
        foreach(var part in parts){if(!double.TryParse(part,NumberStyles.Float,CultureInfo.InvariantCulture,out var number))return 0;result=result*60+number;}
        return result;
    }
    public static double Rate(string value)
    {
        var parts=value.Split('/');
        if(parts.Length==2&&double.TryParse(parts[0],NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out var d)&&d!=0)return n/d;
        return double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var result)?result:0;
    }
}
