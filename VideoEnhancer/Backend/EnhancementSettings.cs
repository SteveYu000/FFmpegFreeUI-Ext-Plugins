using System.Text.Json;
namespace VideoEnhancer;

public sealed class EnhancementSettings
{
    public int SchemaVersion {get;set;}=1;
    public bool UpscaleEnabled {get;set;}
    public string Model {get;set;}="";
    public string Backend {get;set;}="ncnn";
    public bool UpscaleHalfPrecision {get;set;}=true;
    public int UpscaleTileSize {get;set;}
    public int OutputScale {get;set;}
    public bool InterpEnabled {get;set;}
    public string InterpModel {get;set;}="";
    public string InterpBackend {get;set;}="ncnn";
    public bool InterpHalfPrecision {get;set;}=true;
    public double InterpFactor {get;set;}=2;
    public bool InterpDynamicScaledOpticalFlow {get;set;}
    public double SceneDetectThreshold {get;set;}=4;
    public string ProcessOrder {get;set;}="upscale-first";
    public bool RtxHdrEnabled {get;set;}
    public int RtxHdrContrast {get;set;}=100;
    public int RtxHdrSaturation {get;set;}=100;
    public int RtxHdrMiddleGray {get;set;}=44;
    public int RtxHdrMaxLuminance {get;set;}=1000;
    public string RtxTarget {get;set;}="2x";
    public int RtxQuality {get;set;}=3;
    public bool SegmentedEnabled {get;set;}
    public List<SegmentedVideoSettings> SegmentedVideos {get;set;}=[];
    public bool IsEnabled=>UpscaleEnabled||InterpEnabled||RtxHdrEnabled||SegmentedEnabled;
    private static readonly JsonSerializerOptions Options=new(){PropertyNameCaseInsensitive=true,WriteIndented=true};
    public static EnhancementSettings FromJson(string? json)
    {
        var result=JsonSerializer.Deserialize<EnhancementSettings>(string.IsNullOrWhiteSpace(json)?"{}":json,Options)??new();
        result.Model??="";result.Backend??="ncnn";result.InterpModel??="";result.InterpBackend??="ncnn";
        result.RtxTarget??="2x";result.ProcessOrder??="upscale-first";result.SegmentedVideos??=[];
        foreach(var video in result.SegmentedVideos)
        {
            if(video is null)throw new JsonException("分段视频配置不能为 null");
            video.Path??="";video.BoundaryMode??="";video.Segments??=[];
            foreach(var segment in video.Segments)
            {
                if(segment is null)throw new JsonException("分段不能为 null");
                segment.Model??="";segment.Backend??="";segment.DisplayName??="";
            }
        }
        return result;
    }
    public string ToJson()=>JsonSerializer.Serialize(this,Options);
    public static string NormalizeJson(string json)=>FromJson(json).ToJson();
    public static string FromPreset(string preset,string pluginId)
    {
        using var doc=JsonDocument.Parse(string.IsNullOrWhiteSpace(preset)?"{}":preset);
        if(doc.RootElement.ValueKind==JsonValueKind.Object&&doc.RootElement.TryGetProperty("插件扩展数据",out var data)&&data.ValueKind==JsonValueKind.Object&&data.TryGetProperty(pluginId,out var item))
            return item.ValueKind==JsonValueKind.String?item.GetString()??"{}":item.GetRawText();
        return "{}";
    }
    public EnhancementSettings ForInput(string input,bool preview=false)
    {
        if(!SegmentedEnabled)return this;
        if(preview&&input.StartsWith('<')&&SegmentedVideos.Any(item=>item.Enabled))return this;
        bool configured=SegmentedVideos.Any(item=>item.Enabled&&!string.IsNullOrWhiteSpace(item.Path)&&
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(item.Path)).Equals(Path.GetFullPath(input),StringComparison.OrdinalIgnoreCase));
        if(configured)return this;
        var effective=FromJson(ToJson());
        effective.SegmentedEnabled=false;
        return effective;
    }

    public string Summary()
    {
        var parts=new List<string>();
        if(SegmentedEnabled)parts.Add("分段超分："+SegmentedVideos.Count(item=>item.Enabled)+" 个文件方案");
        if(UpscaleEnabled)parts.Add($"超分：{Backend} / {(Backend=="rtxvsr"?RtxTarget:Model)} / {(UpscaleHalfPrecision?"自动精度":"FP32")}");
        if(InterpEnabled)parts.Add($"补帧：{InterpBackend} / {InterpModel} / {InterpFactor:g} 倍");
        if(RtxHdrEnabled)parts.Add($"RTX HDR：{RtxHdrMaxLuminance} nit");
        if(parts.Count==0)return "AI 增强已关闭";
        parts.Add(ProcessOrder=="interp-first"?"先补帧，再超分":"先超分，再补帧");
        return string.Join("；",parts);
    }
}
public sealed class SegmentedVideoSettings
{
    public string Path{get;set;}="";public long FrameCount{get;set;}public double DurationSeconds{get;set;}
    public int SourceWidth{get;set;}public int SourceHeight{get;set;}public string BoundaryMode{get;set;}="";
    public bool AllowMixedModelBackends{get;set;}public bool Enabled{get;set;}public List<SegmentRange> Segments{get;set;}=[];
}
public sealed class SegmentRange
{
    public long Start{get;set;}public long End{get;set;}public double StartSeconds{get;set;}public double EndSeconds{get;set;}
    public string Backend{get;set;}="";public string Model{get;set;}="";public string DisplayName{get;set;}="";
    public int Scale{get;set;}public int TargetWidth{get;set;}public int TargetHeight{get;set;}
}
