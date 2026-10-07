using System.Globalization;
using System.Text;
using System.Text.Json;
using FFmpegFreeUI.Ext.PluginSdk;

namespace VideoEnhancer;

internal sealed record RtxStepOptions(string Input,string Output,string PipeName,bool Vsr,bool Hdr,double Scale,
    int Width,int Height,string Rate,string PixelFormat);
internal sealed class EnhancementPlan
{
    internal required EnhancementSettings Settings;
    internal required string Input;
    internal required string WorkDirectory;
    internal required string PauseName;
    internal List<ExtPluginCommandStep> Steps = [];
    internal Dictionary<int,string> Videos = [];
    internal Dictionary<int,VideoStreamInfo> Sources = [];
    internal Dictionary<string,RtxStepOptions> Rtx = [];
    internal int VideoCount;
    internal string BackendDescription => string.Join(Environment.NewLine,Steps.Select(step => WindowsArguments.Quote(step.ProcessFileName)+" "+step.Arguments));
}

public static partial class BackendServices
{
    public static void ConfigureRuntime(string root) => PortablePaths.Configure(root);
    internal static string ExtToolPath(string file)=>Path.Combine(PortablePaths.EmbeddedToolsRoot(ToolVersion),file);

    internal static void PrepareExtSupport()
    {
        EnsureInterpolationSupportScripts();
        EnsureEmbeddedFile("VideoEnhancer.Embedded.rve-ext-launch.py","rve-ext-launch.py");
        EnsureEmbeddedFile(EmbeddedOrderedBackendResource,"rve-ordered-backend.py");
        EnsureEmbeddedFile(EmbeddedSegmentedBackendResource,"rve-segmented-backend.py");
    }

    internal static EnhancementPlan BuildEnhancementPlan(EnhancementSettings settings,string preset,string input,
        string work,string pauseName,string ffmpeg,string ffprobe,VideoMediaInfo? media,bool prepareModels)
    {
        ValidateExtSettings(settings);
        var plan=new EnhancementPlan{Settings=settings,Input=input,WorkDirectory=work,PauseName=pauseName,VideoCount=media?.Videos.Count ?? 0};
        int[] selected=NativeVideoBinder.SelectedOrdinals(preset);
        var segmentPreview=media is null&&settings.SegmentedEnabled?FindSegmentConfig(settings,input,true):null;
        foreach(int ordinal in selected)
        {
            var source=media?.Videos.ElementAtOrDefault(ordinal);
            if(source is null&&segmentPreview is not null)
            {
                double rate=segmentPreview.DurationSeconds>0?(double)segmentPreview.FrameCount/segmentPreview.DurationSeconds:0;
                source=new(ordinal,segmentPreview.SourceWidth,segmentPreview.SourceHeight,rate,rate>0?rate.ToString(CultureInfo.InvariantCulture):"<帧率>",
                    segmentPreview.FrameCount,segmentPreview.DurationSeconds,0,false,8,0,"","","","1:1");
            }
            source??=new VideoStreamInfo(ordinal,0,0,0,"<帧率>",0,0,0,false,8,0,"","","","1:1");
            if(source.VariableFrameRate)throw new InvalidOperationException("选中的视频流是变帧率；请先转为恒定帧率，再启用 AI 增强");
            if(media is not null&&(source.Width<=0||source.Height<=0||source.FrameRate<=0))throw new InvalidOperationException("所选视频流不存在或无法探测尺寸、帧率：0:v:"+ordinal);
            plan.Sources[ordinal]=source;
            var sourcePath=Path.Combine(work,$"source-{ordinal}.mkv");
            var output=Path.Combine(work,$"enhanced-{ordinal}.mkv");
            plan.Videos[ordinal]=output;
            int width=source.Width,height=source.Height;
            if(Math.Abs(source.Rotation)%180==90)(width,height)=(height,width);
            var hdr=source.Hdr;
            if(settings.RtxHdrEnabled&&hdr)throw new InvalidOperationException("输入已是 HDR，不能再次启用 RTX HDR");
            if(hdr&&((settings.UpscaleEnabled&&settings.Backend is "ncnn" or "onnx" or "flashvsr")||(settings.InterpEnabled&&settings.InterpBackend=="ncnn")||settings.SegmentedEnabled))
                throw new InvalidOperationException("该 AI 组合不支持 HDR 的 16-bit RGB 帧；请使用 CUDA / TensorRT");
            var encoder=LosslessEncoder(hdr,source);
            var extract=new List<string>{"-hide_banner","-y","-i",input,"-map",$"0:v:{ordinal}","-an","-sn","-dn","-vf","setpts=PTS-STARTPTS","-c:v","ffv1","-level","3","-pix_fmt",hdr?"gbrp16le":(!settings.InterpEnabled&&(!settings.UpscaleEnabled||settings.Backend=="rtxvsr")&&source.Bits>8?"yuv420p10le":"bgr0"),"-metadata:s:v:0","rotate=0",sourcePath};
            AddStep(plan,$"source-{ordinal}","读取原始视频流 "+ordinal,ffmpeg,extract,true);
            string current=sourcePath;

            if(settings.SegmentedEnabled)
            {
                if(settings.InterpEnabled||settings.RtxHdrEnabled)throw new InvalidOperationException("分段模式暂不与补帧、RTX HDR 同时启用；请关闭这些开关后执行分段超分");
                BuildSegmentSteps(plan,ordinal,current,output,width,height,source,ffmpeg,ffprobe,prepareModels);
                continue;
            }
            bool vsr=settings.UpscaleEnabled&&settings.Backend=="rtxvsr";
            bool useRve=(settings.UpscaleEnabled&&!vsr)||settings.InterpEnabled;
            if(useRve)
            {
                var rveOutput=settings.RtxHdrEnabled||vsr?Path.Combine(work,$"rve-{ordinal}.mkv"):output;
                string model=settings.UpscaleEnabled&&!vsr?ResolveExtModel(settings.Model,settings.Backend,false):"";
                string? interp=settings.InterpEnabled?ResolveExtModel(settings.InterpModel,settings.InterpBackend,true):null;
                var scale=model.Length>0?(settings.Backend=="basicvsrpp"?BasicVsrPlusPlusScale(model):DetectScale(model)):null;
                int nativeScale=int.TryParse(scale,out var native)?native:1;
                if(model.Length>0&&settings.OutputScale>0&&ModelCapabilityCatalog.TryGet(model,ModelsDir,out var capability)&&capability.InferenceScales.Contains(settings.OutputScale))
                {scale=settings.OutputScale.ToString(CultureInfo.InvariantCulture);nativeScale=settings.OutputScale;}
                int outputScale=settings.OutputScale>0?settings.OutputScale:nativeScale;
                // TRT 低倍率直接编入 GPU 图；其他后端在超分结果进入补帧前缩放。
                int engineScale=settings.Backend=="tensorrt"&&settings.OutputScale>0?Math.Min(nativeScale,settings.OutputScale):nativeScale;
                if(model.Length>0&&settings.OutputScale>0&&settings.Backend is not ("flashvsr" or "basicvsrpp"))
                    scale=settings.OutputScale.ToString(CultureInfo.InvariantCulture);
                if(prepareModels&&model.Length>0&&settings.Backend=="tensorrt")
                {
                    int engineWidth=width,engineHeight=height;
                    if(ModelCapabilityCatalog.TryGet(model,ModelsDir,out var capability2))
                    {
                        int multiple=Math.Max(1,capability2.InputMultiple);
                        engineWidth=(engineWidth+multiple-1)/multiple*multiple;engineHeight=(engineHeight+multiple-1)/multiple*multiple;
                    }
                    model=EnsureTensorRtEngine(model,engineWidth,engineHeight,settings.UpscaleTileSize,engineScale,settings.UpscaleHalfPrecision?"auto":"float32");
                    if(model.Length==0)throw new InvalidOperationException("TensorRT 超分 Engine 构建失败");
                }
                string upPrecision=ResolveUpscalePrecision(model,settings.Backend,settings.UpscaleHalfPrecision?"auto":"float32");
                string interpPrecision=ResolveInterpPrecision(interp,settings.InterpBackend,settings.InterpHalfPrecision?"auto":"float32");
                if(prepareModels&&interp is not null&&settings.InterpBackend=="tensorrt")
                {
                    int prepScale=outputScale;
                    int iw=settings.ProcessOrder=="upscale-first"&&model.Length>0?width*prepScale:width;
                    int ih=settings.ProcessOrder=="upscale-first"&&model.Length>0?height*prepScale:height;
                    if(PrepareRifeTensorRTEngine(interp,iw,ih,false)!=0)
                        throw new InvalidOperationException("TensorRT 补帧 Engine 构建失败");
                }
                string rveEncoder=encoder;
                if(model.Length>0&&settings.OutputScale>0&&width>0)
                    rveEncoder=OutputScale.Encoder(encoder,width,height,settings.OutputScale);
                if(model.Length>0&&interp is not null&&settings.Backend!=settings.InterpBackend)
                {
                    bool upFirst=settings.ProcessOrder=="upscale-first";
                    string between=Path.Combine(work,$"rve-between-{ordinal}.mkv");
                    AddRve(plan,$"rve-first-{ordinal}",upFirst?"超分（第一阶段）":"补帧（第一阶段）",current,between,
                        upFirst?model:"",upFirst?null:interp,upFirst?scale:null,upFirst?settings.Backend:settings.InterpBackend,
                        upFirst?rveEncoder:encoder,upPrecision,interpPrecision,ffmpeg,ffprobe,hdr);
                    AddRve(plan,$"rve-second-{ordinal}",upFirst?"补帧（第二阶段）":"超分（第二阶段）",between,rveOutput,
                        upFirst?"":model,upFirst?interp:null,upFirst?null:scale,upFirst?settings.InterpBackend:settings.Backend,
                        upFirst?encoder:rveEncoder,upPrecision,interpPrecision,ffmpeg,ffprobe,hdr);
                }
                else
                {
                    string backend=model.Length>0?settings.Backend:settings.InterpBackend;
                    AddRve(plan,$"rve-{ordinal}","RVE 视频增强",current,rveOutput,model,interp,scale,backend,rveEncoder,
                        upPrecision,interpPrecision,ffmpeg,ffprobe,hdr);
                }
                current=rveOutput;
                if(model.Length>0){width*=outputScale;height*=outputScale;}
            }
            if(vsr||settings.RtxHdrEnabled)
            {
                // RTX 仅支持硬件可解码输入；将 FFV1 中间视频转换为lossless 模式的 HEVC。
                string rtxInput=Path.Combine(work,$"rtx-input-{ordinal}.mkv");
                var conversion=new List<string>{"-hide_banner","-y","-i",current,"-map","0:v:0","-an","-sn","-dn","-c:v","libx265","-preset","ultrafast","-x265-params",hdr?"lossless=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc":"lossless=1","-pix_fmt","yuv420p10le",rtxInput};
                AddStep(plan,$"rtx-input-{ordinal}","准备 RTX 视频输入",ffmpeg,conversion,true);
                double rtxScale=vsr?ExtRtxScale(settings.RtxTarget,width,height):1;
                int ow=width>0?Math.Max(2,(int)Math.Round(width*rtxScale)):0,oh=height>0?Math.Max(2,(int)Math.Round(height*rtxScale)):0;
                if((ow&1)!=0)ow++;if((oh&1)!=0)oh++;
                double factor=settings.InterpEnabled?settings.InterpFactor:1;
                string rate=source.FrameRate>0?(source.FrameRate*factor).ToString("0.#########",CultureInfo.InvariantCulture):"<帧率>";
                string pipeName="videoenhancer-write-"+Path.GetFileName(work)+"-"+ordinal;
                string raw=settings.RtxHdrEnabled?"x2bgr10le":"p010le";
                var rtx=new RtxStepOptions(rtxInput,output,pipeName,vsr,settings.RtxHdrEnabled,rtxScale,ow,oh,rate,raw);
                var write=new List<string>{"-hide_banner","-y","-f","rawvideo","-pix_fmt",raw,"-s:v",ow>0?$"{ow}x{oh}":"<输出宽度>x<输出高度>","-r",rate,"-i",@"\\.\pipe\"+pipeName,"-map","0:v:0","-an","-sn","-dn","-c:v","ffv1","-level","3","-pix_fmt",settings.RtxHdrEnabled?"gbrp16le":"yuv420p10le"};
                if(settings.RtxHdrEnabled)write.AddRange(["-color_primaries","bt2020","-color_trc","smpte2084","-colorspace","bt2020nc"]);
                else
                {
                    if(source.ColorPrimaries.Length>0&&source.ColorPrimaries!="unknown")write.AddRange(["-color_primaries",source.ColorPrimaries]);
                    if(source.ColorTransfer.Length>0&&source.ColorTransfer!="unknown")write.AddRange(["-color_trc",source.ColorTransfer]);
                    if(source.ColorSpace.Length>0&&source.ColorSpace is not ("unknown" or "gbr" or "rgb"))write.AddRange(["-colorspace",source.ColorSpace]);
                }
                string frameColor=ColorFrameFilter(source,settings.RtxHdrEnabled,true);
                if(frameColor.Length>0)write.AddRange(["-vf",frameColor]);
                write.Add(output);
                string id=$"rtx-{ordinal}";AddStep(plan,id,"RTX 视频增强 → 无损视频",ffmpeg,write,true);plan.Rtx[id]=rtx;
            }
        }
        return plan;
    }

    private static void AddStep(EnhancementPlan plan,string id,string name,string process,IEnumerable<string> args,bool progress)
    {
        var arguments=args.ToList();
        var source=plan.Sources.Values.LastOrDefault();
        if(progress&&source is not null)
        {
            var colors=new List<string>();
            if(source.ColorPrimaries.Length>0&&source.ColorPrimaries!="unknown"&&!arguments.Contains("-color_primaries"))
                colors.AddRange(["-color_primaries",source.ColorPrimaries]);
            if(source.ColorTransfer.Length>0&&source.ColorTransfer!="unknown"&&!arguments.Contains("-color_trc"))
                colors.AddRange(["-color_trc",source.ColorTransfer]);
            // 原始帧和拼接步骤不会自动继承色域标记；显式写入，不覆盖 RTX HDR 的新色域。
            arguments.InsertRange(arguments.Count-1,colors);
        }
        plan.Steps.Add(new(id,name,process,WindowsArguments.Join(arguments)){Placement=ExtPluginCommandStepPlacement.BeforeNative,
            Order=plan.Steps.Count,WorkingDirectory=CoreRoot,ParseFFmpegProgress=progress});
    }

    private static void AddRve(EnhancementPlan plan,string id,string name,string input,string output,string model,string? interp,
        string? scale,string backend,string encoder,string upscalePrecision,string interpPrecision,string ffmpeg,string ffprobe,bool hdr)
    {
        var settings=plan.Settings;
        string script=model.Length>0&&backend is not ("flashvsr" or "basicvsrpp")?ExtToolPath("rve-ordered-backend.py"):BackendScript;
        var args=BuildBackendArgs(input,output,model,encoder,true,scale,plan.PauseName,interp,
            settings.InterpFactor.ToString("0.########",CultureInfo.InvariantCulture),backend,script,hdr,
            settings.InterpDynamicScaledOpticalFlow,settings.SceneDetectThreshold,settings.UpscaleTileSize,model.Length>0?upscalePrecision:interpPrecision);
        int ff=args.IndexOf("--ffmpeg_path");args[ff+1]=ffmpeg;
        AddPythonStep(plan,id,name,script,args.Skip(1),upscalePrecision,interpPrecision,model,ffprobe);
    }

    private static void AddPythonStep(EnhancementPlan plan,string id,string name,string script,IEnumerable<string> forwarded,
        string upPrecision,string interpPrecision,string model,string ffprobe)
    {
        int multiple=ModelCapabilityCatalog.TryGet(model,ModelsDir,out var cap)?Math.Max(1,cap.InputMultiple):1;
        var args=new List<string>{"-u","-X","utf8",ExtToolPath("rve-ext-launch.py"),"--backend-dir",Path.GetDirectoryName(BackendScript)!,
            "--work-dir",plan.WorkDirectory,"--target-script",script,"--upscale-precision",upPrecision,"--interp-precision",interpPrecision,
            "--process-order",plan.Settings.ProcessOrder,"--output-scale",(model.Length>0?plan.Settings.OutputScale:0).ToString(CultureInfo.InvariantCulture),"--input-multiple",multiple.ToString(CultureInfo.InvariantCulture),"--ffprobe-path",ffprobe,"--"};
        args.AddRange(forwarded);
        AddStep(plan,id,name,PythonExe,args,false);
    }

    private static string ColorFrameFilter(VideoStreamInfo source,bool rtxHdr=false,bool includeSpace=false)
    {
        var fields=new List<string>();
        string primaries=rtxHdr?"bt2020":source.ColorPrimaries,transfer=rtxHdr?"smpte2084":source.ColorTransfer;
        if(primaries.Length>0&&primaries!="unknown")fields.Add("color_primaries="+primaries);
        if(transfer.Length>0&&transfer!="unknown")fields.Add("color_trc="+transfer);
        string space=rtxHdr?"gbr":source.ColorSpace;
        if(includeSpace&&space.Length>0&&space!="unknown")fields.Add("colorspace="+space);
        return fields.Count>0?"setparams="+string.Join(":",fields):"";
    }

    private static string LosslessEncoder(bool hdr,VideoStreamInfo source)
    {
        string result="-c:v ffv1 -level 3 -pix_fmt "+(hdr?"gbrp16le":"bgr0")+" -an -sn -dn -map_metadata -1 -map_chapters -1";
        var filters=new List<string>();
        if(source.SampleAspectRatio.Length>0&&source.SampleAspectRatio!="0:1")filters.Add("setsar="+source.SampleAspectRatio.Replace(':','/'));
        // 原始帧输入没有色彩属性，必须在帧上标记，避免被编码器覆盖。
        string color=ColorFrameFilter(source);
        if(color.Length>0)filters.Add(color);
        if(filters.Count>0)result+=" -vf "+string.Join(",",filters);
        if(source.ColorPrimaries.Length>0&&source.ColorPrimaries!="unknown")result+=" -color_primaries "+source.ColorPrimaries;
        else if(hdr)result+=" -color_primaries bt2020";
        if(source.ColorTransfer.Length>0&&source.ColorTransfer!="unknown")result+=" -color_trc "+source.ColorTransfer;
        return result;
    }

    private static string ResolveExtModel(string requested,string backend,bool interpolation)
    {
        string raw=Environment.ExpandEnvironmentVariables(requested.Trim().Trim('"')).Replace('/',Path.DirectorySeparatorChar);
        if(raw.Length==0)throw new InvalidOperationException(interpolation?"尚未选择补帧模型":"尚未选择超分模型");
        var candidates=new List<string>{Path.Combine(ModelsDir,raw)};
        if(Path.IsPathRooted(raw))candidates.Insert(0,raw);
        if(interpolation)candidates.Add(Path.Combine(FrameInterpolationDir,raw));
        bool Eligible(string path)
        {
            if(backend=="ncnn")return Directory.Exists(path)&&Directory.EnumerateFiles(path,"*.param").Any(p=>File.Exists(Path.ChangeExtension(p,".bin")));
            if(backend is "flashvsr" or "basicvsrpp")return File.Exists(path)||Directory.Exists(path);
            if(!File.Exists(path))return false;
            var extension=Path.GetExtension(path).ToLowerInvariant();
            return backend=="onnx"?extension==".onnx":backend=="tensorrt"?extension is ".pth" or ".pkl" or ".ckpt" or ".pt" or ".onnx" or ".engine":extension is ".pth" or ".pkl" or ".ckpt" or ".pt" or ".safetensors";
        }
        foreach(var candidate in candidates)
        {
            if(Eligible(candidate))return Path.GetFullPath(candidate);
            if(backend=="ncnn"&&File.Exists(candidate)&&Eligible(Path.GetDirectoryName(candidate)!))return Path.GetDirectoryName(Path.GetFullPath(candidate))!;
        }
        if(Directory.Exists(ModelsDir))
        {
            var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string normalized=raw.Replace('\\','/').Trim('/');
            foreach(var file in Directory.EnumerateFiles(ModelsDir,"*",SearchOption.AllDirectories))
            {
                string candidate=backend=="ncnn"?Path.GetDirectoryName(file)!:file;
                if(!Eligible(candidate))continue;
                string relative=Path.GetRelativePath(ModelsDir,candidate).Replace('\\','/');
                string withoutExtension=backend=="ncnn"?relative:Path.ChangeExtension(relative,null);
                bool match=relative.Equals(normalized,StringComparison.OrdinalIgnoreCase)||withoutExtension.Equals(normalized,StringComparison.OrdinalIgnoreCase)||
                    Path.GetFileNameWithoutExtension(candidate).Equals(Path.GetFileNameWithoutExtension(raw),StringComparison.OrdinalIgnoreCase);
                if(!match)continue;
                bool interpPath=relative.Contains("Interpolation",StringComparison.OrdinalIgnoreCase)||relative.Split('/').Any(part=>part.StartsWith("RIFE",StringComparison.OrdinalIgnoreCase));
                if(interpolation!=interpPath&& !candidates.Any(p=>p.Equals(candidate,StringComparison.OrdinalIgnoreCase)))continue;
                found.Add(candidate);
            }
            if(found.Count==1)return found.Single();
            if(found.Count>1)throw new InvalidOperationException("模型路径不唯一，请选择包含分类目录的相对路径："+requested);
        }
        throw new FileNotFoundException("未安装所选模型："+requested);
    }

    private static double ExtRtxScale(string target,int width,int height)
    {
        if(target.EndsWith('x')&&double.TryParse(target[..^1],NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&value>=1&&value<=4)return value;
        if(target is not ("1080p" or "1440p" or "2160p" or "4320p"))throw new InvalidOperationException("无效的 RTX 输出规格："+target);
        var match=System.Text.RegularExpressions.Regex.Match(target,@"(\d+)");
        if(!match.Success)throw new InvalidOperationException("无效的 RTX 输出规格："+target);
        if(width<=0||height<=0)return 1;
        int edge=int.Parse(match.Value,CultureInfo.InvariantCulture);
        // 短边目标兼容横向和竖向素材。
        return Math.Clamp((double)edge/Math.Min(width,height),1,4);
    }

    private static void ValidateExtSettings(EnhancementSettings s)
    {
        if(s.UpscaleEnabled&&s.Backend is not ("ncnn" or "cuda" or "tensorrt" or "onnx" or "flashvsr" or "basicvsrpp" or "rtxvsr"))
            throw new InvalidOperationException("未知超分后端："+s.Backend);
        if(s.InterpEnabled&&s.InterpBackend is not ("ncnn" or "cuda" or "tensorrt"))throw new InvalidOperationException("未知补帧后端："+s.InterpBackend);
        if(s.SchemaVersion!=1)throw new InvalidOperationException("不支持的 AI 参数版本："+s.SchemaVersion);
        if(s.InterpEnabled&&(!double.IsFinite(s.InterpFactor)||s.InterpFactor<=1||s.InterpFactor>16))throw new InvalidOperationException("补帧倍率应大于 1 且不超过 16");
        if(s.OutputScale<0||s.OutputScale>8||s.UpscaleTileSize<0)throw new InvalidOperationException("超分倍率或分块大小无效");
        if(!double.IsFinite(s.SceneDetectThreshold)||s.SceneDetectThreshold<0)throw new InvalidOperationException("转场阈值无效");
        if(s.ProcessOrder is not ("upscale-first" or "interp-first"))throw new InvalidOperationException("处理顺序无效");
        if(s.RtxQuality is <1 or >4||s.RtxHdrContrast is <0 or >200||s.RtxHdrSaturation is <0 or >200||s.RtxHdrMiddleGray is <10 or >100||s.RtxHdrMaxLuminance is <400 or >2000)throw new InvalidOperationException("RTX 参数超出允许范围");
    }

    private static SegmentedVideoSettings? FindSegmentConfig(EnhancementSettings settings,string input,bool preview)
    {
        var enabled=settings.SegmentedVideos.Where(item=>item.Enabled).ToArray();
        var match=enabled.FirstOrDefault(item=>!string.IsNullOrWhiteSpace(item.Path)&&
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(item.Path)).Equals(Path.GetFullPath(input),StringComparison.OrdinalIgnoreCase));
        if(match is not null)return match;
        // 没有具体输入的参数预览以首个启用的方案为例；每个任务的预览使用其实际方案。
        if(preview&&input.StartsWith('<')&&enabled.Length>0)return enabled[0];
        return null;
    }

    private static void BuildSegmentSteps(EnhancementPlan plan,int ordinal,string input,string output,int width,int height,
        VideoStreamInfo source,string ffmpeg,string ffprobe,bool prepare)
    {
        var config=FindSegmentConfig(plan.Settings,plan.Input,!prepare);
        if(config is null||config.Segments.Count==0)throw new InvalidOperationException("该输入文件尚未配置连续覆盖全片的分段："+plan.Input);
        if(source.Hdr)throw new InvalidOperationException("分段超分暂不支持 HDR 输入");
        var ranges=config.Segments;
        bool seconds=ranges.Any(r=>r.EndSeconds>0);
        var prepared=new List<PreparedSegment>();
        long next=1;double nextSeconds=0;int fixedScale=0;
        var modelBackends=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var range in ranges)
        {
            long start=range.Start,end=range.End;
            if(seconds&&source.FrameRate>0)
            {
                if(Math.Abs(range.StartSeconds-nextSeconds)>0.002||range.EndSeconds<=range.StartSeconds)throw new InvalidOperationException("秒级分段存在空洞、重叠或倒置");
                start=next;end=prepared.Count==ranges.Count-1?source.Frames:(long)Math.Round(range.EndSeconds*source.FrameRate,MidpointRounding.AwayFromZero);
                nextSeconds=range.EndSeconds;
            }
            if(start!=next||end<start)throw new InvalidOperationException("分段应从第 "+next+" 帧连续开始");
            next=end+1;
            string backend=range.Backend.ToLowerInvariant(),model=range.Model;int scale=0,multiple=1;
            if(IsSegmentModelBackend(backend))
            {
                modelBackends.Add(backend);model=ResolveExtModel(model,backend,false);
                scale=int.TryParse(DetectScale(model),out var detected)?detected:0;
                if(scale<=0)throw new InvalidOperationException("无法识别分段模型倍率："+range.Model);
                if(fixedScale==0)fixedScale=scale;else if(fixedScale!=scale)throw new InvalidOperationException("分段固定倍率模型的倍率应相同");
                if(ModelCapabilityCatalog.TryGet(model,ModelsDir,out var capability))multiple=Math.Max(1,capability.InputMultiple);
                if(prepare&&backend=="tensorrt")
                {
                    model=EnsureTensorRtEngine(model,(width+multiple-1)/multiple*multiple,(height+multiple-1)/multiple*multiple,
                        plan.Settings.UpscaleTileSize,scale,plan.Settings.UpscaleHalfPrecision?"auto":"float32");
                    if(model.Length==0)throw new InvalidOperationException("分段 TensorRT Engine 构建失败");
                }
            }
            else if(backend=="ffmpeg")
            {
                if(!new[]{"fast_bilinear","bilinear","bicubic","neighbor","area","bicublin","lanczos","spline"}.Contains(model))throw new InvalidOperationException("不支持的 FFmpeg 缩放算法："+model);
            }
            else if(backend!="anime4k")throw new InvalidOperationException("不支持的分段后端："+backend);
            prepared.Add(new(){Start=start,End=end,StartSeconds=range.StartSeconds,EndSeconds=range.EndSeconds,Backend=backend,Model=model,Scale=scale,InputMultiple=multiple,
                OutputWidth=range.TargetWidth,OutputHeight=range.TargetHeight});
        }
        if(modelBackends.Count>1&&!config.AllowMixedModelBackends)throw new InvalidOperationException("请先启用分段的“跨模型后端混用”");
        if(source.Frames>0&&prepared[^1].End!=source.Frames)throw new InvalidOperationException("分段必须完整覆盖到第 "+source.Frames+" 帧");
        if(seconds&&source.Duration>0&&Math.Abs(nextSeconds-source.Duration)>Math.Max(0.1,2/source.FrameRate))throw new InvalidOperationException("秒级分段未覆盖完整视频时长");
        int ow=fixedScale>0?width*fixedScale:prepared[0].OutputWidth,oh=fixedScale>0?height*fixedScale:prepared[0].OutputHeight;
        if(ow<=0||oh<=0)throw new InvalidOperationException("分段必须设置统一目标分辨率");
        if(fixedScale==0&&prepared.Any(r=>r.OutputWidth!=ow||r.OutputHeight!=oh))throw new InvalidOperationException("全部分段的目标分辨率应相同");
        foreach(var range in prepared){range.OutputWidth=ow;range.OutputHeight=oh;}
        string precision=plan.Settings.UpscaleHalfPrecision?"auto":"float32";
        if(prepared.Any(r=>IsSegmentModelBackend(r.Backend)&&ResolveUpscalePrecision(r.Model,r.Backend,precision)=="float32"))precision="float32";

        if(prepared.All(r=>IsSegmentModelBackend(r.Backend)))
        {
            AddSegmentPython(plan,$"segments-{ordinal}",input,output,prepared,precision,ffmpeg,ffprobe);
            return;
        }
        // 混合段分别生成视频流，再使用无损编码统一拼接；音频和字幕始终由宿主读取原始文件。
        var clips=new List<string>();
        for(int i=0;i<prepared.Count;i++)
        {
            var segment=prepared[i];
            string clip=Path.Combine(plan.WorkDirectory,$"segment-{ordinal}-{i}.mkv");clips.Add(clip);
            string trim=$"trim=start_frame={segment.Start-1}:end_frame={segment.End},setpts=PTS-STARTPTS";
            if(IsSegmentModelBackend(segment.Backend))
            {
                string raw=Path.Combine(plan.WorkDirectory,$"segment-source-{ordinal}-{i}.mkv");
                AddStep(plan,$"segment-source-{ordinal}-{i}","读取模型分段",ffmpeg,
                    ["-hide_banner","-y","-i",input,"-map","0:v:0","-an","-sn","-vf",trim,"-c:v","ffv1","-level","3","-pix_fmt","bgr0",raw],true);
                var rebased=new PreparedSegment{Start=1,End=segment.End-segment.Start+1,Backend=segment.Backend,Model=segment.Model,Scale=segment.Scale,InputMultiple=segment.InputMultiple,OutputWidth=ow,OutputHeight=oh};
                AddSegmentPython(plan,$"segment-model-{ordinal}-{i}",raw,clip,[rebased],precision,ffmpeg,ffprobe);
            }
            else
            {
                string filter=trim+","+BuildDirectCustomFilter(segment)+",setsar=1,format=rgb24";
                var args=new List<string>{"-hide_banner","-y"};
                if(segment.Backend=="anime4k")args.AddRange(["-init_hw_device","vulkan","-filter_hw_device","vulkan"]);
                args.AddRange(["-i",input,"-map","0:v:0","-an","-sn","-dn","-vf",filter,"-c:v","ffv1","-level","3","-pix_fmt","bgr0",clip]);
                AddStep(plan,$"segment-custom-{ordinal}-{i}","处理 "+segment.Backend+" 分段",ffmpeg,args,true);
            }
        }
        var concat=new List<string>{"-hide_banner","-y"};
        foreach(var clip in clips)concat.AddRange(["-i",clip]);
        string graph=string.Join(";",clips.Select((_,i)=>$"[{i}:v:0]setpts=PTS-STARTPTS,setsar=1,format=bgr0[s{i}]"))+";"+
            string.Concat(clips.Select((_,i)=>$"[s{i}]"))+$"concat=n={clips.Count}:v=1:a=0[out]";
        concat.AddRange(["-filter_complex",graph,"-map","[out]","-an","-sn","-dn","-c:v","ffv1","-level","3","-pix_fmt","bgr0",output]);
        AddStep(plan,$"segment-join-{ordinal}","拼接增强视频流",ffmpeg,concat,true);
    }

    private static void AddSegmentPython(EnhancementPlan plan,string id,string input,string output,List<PreparedSegment> segments,string precision,string ffmpeg,string ffprobe)
    {
        string script=ExtToolPath("rve-segmented-backend.py");
        var args=new List<string>{"--input",input,"--output",output,"--segments-base64",EncodePreparedSegments(segments),
            "--encoder-args-base64",EncodeStringList(WindowsArguments.Parse(LosslessEncoder(false,plan.Sources.Values.Last()))),
            "--ffmpeg-path",ffmpeg,"--tile-size",plan.Settings.UpscaleTileSize.ToString(CultureInfo.InvariantCulture),"--pause-shm",plan.PauseName,"--overwrite"};
        AddPythonStep(plan,id,"分段 RVE 视频增强",script,args,precision,"auto","",ffprobe);
    }
}
