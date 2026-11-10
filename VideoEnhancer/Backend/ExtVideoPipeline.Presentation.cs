using System.Globalization;
using System.Text.Json.Nodes;
using FFmpegFreeUI.Ext.PluginSdk;

namespace VideoEnhancer;

public sealed partial class ExtVideoPipeline
{
    private void AddOverview(ExtPluginPresetOverviewContext context)
    {
        var settings=EnhancementSettings.FromJson(context.PluginStateJson);
        if(!settings.IsEnabled)return;
        var defaults=new EnhancementSettings();
        if(settings.UpscaleEnabled)
        {
            var parts=new List<string>();
            if(settings.Backend=="rtxvsr")
                parts.AddRange(["AI 超分：RTX VSR","目标："+settings.RtxTarget,"质量："+settings.RtxQuality]);
            else
            {
                parts.AddRange(["AI 超分："+settings.Model,"后端："+settings.Backend]);
                if(settings.UpscaleHalfPrecision!=defaults.UpscaleHalfPrecision)parts.Add("半精度推理：开启");
                if(settings.OutputScale>0)parts.Add($"输出倍率：{settings.OutputScale}x");
                if(settings.UpscaleTileSize>0)parts.Add($"分块：{settings.UpscaleTileSize} px");
            }
            context.Rows.Add(new(string.Join("；",parts)));
        }
        if(settings.InterpEnabled)
        {
            var parts=new List<string>{"AI 补帧："+settings.InterpModel,"后端："+settings.InterpBackend,
                "倍率："+settings.InterpFactor.ToString("g",CultureInfo.InvariantCulture)+"x"};
            if(settings.InterpHalfPrecision!=defaults.InterpHalfPrecision)parts.Add("半精度推理：开启");
            if(settings.SceneDetectThreshold!=defaults.SceneDetectThreshold)
                parts.Add("转场阈值："+settings.SceneDetectThreshold.ToString("g",CultureInfo.InvariantCulture));
            if(settings.InterpDynamicScaledOpticalFlow)parts.Add("动态光流：开启");
            context.Rows.Add(new(string.Join("；",parts)));
        }
        if(settings.UpscaleEnabled&&settings.InterpEnabled&&settings.Backend!="rtxvsr"&&settings.ProcessOrder!=defaults.ProcessOrder)
            context.Rows.Add(new("AI 处理顺序：先补帧，再超分"));
        if(settings.RtxHdrEnabled)
        {
            var parts=new List<string>{"RTX HDR：开启"};
            if(settings.RtxHdrContrast!=defaults.RtxHdrContrast)parts.Add("对比度："+settings.RtxHdrContrast);
            if(settings.RtxHdrSaturation!=defaults.RtxHdrSaturation)parts.Add("饱和度："+settings.RtxHdrSaturation);
            if(settings.RtxHdrMiddleGray!=defaults.RtxHdrMiddleGray)parts.Add("中间灰度："+settings.RtxHdrMiddleGray);
            if(settings.RtxHdrMaxLuminance!=defaults.RtxHdrMaxLuminance)parts.Add($"峰值亮度：{settings.RtxHdrMaxLuminance} nit");
            context.Rows.Add(new(string.Join("；",parts)));
        }
        if(!settings.SegmentedEnabled)return;
        var videos=settings.SegmentedVideos.Where(video=>video.Enabled).ToList();
        context.Rows.Add(new($"分段超分：{videos.Count} 个文件方案"));
        foreach(var video in videos)
        {
            string title="分段文件："+Path.GetFileName(video.Path);
            if(video.AllowMixedModelBackends)title+="；跨模型后端混用：开启";
            context.Rows.Add(new(title));
            bool seconds=string.Equals(video.BoundaryMode,"seconds",StringComparison.OrdinalIgnoreCase);
            foreach(var segment in video.Segments)
            {
                string range=seconds
                    ?segment.StartSeconds.ToString("0.###",CultureInfo.InvariantCulture)+"–"+segment.EndSeconds.ToString("0.###",CultureInfo.InvariantCulture)+" 秒"
                    :$"{segment.Start}–{segment.End} 帧";
                string mode=string.IsNullOrWhiteSpace(segment.DisplayName)?segment.Backend+" / "+segment.Model:segment.DisplayName;
                string size=segment.TargetWidth>0&&segment.TargetHeight>0?$"；目标：{segment.TargetWidth}×{segment.TargetHeight}":"";
                context.Rows.Add(new($"  {range}：{mode}{size}"));
            }
        }
    }

    // RTX 通过 HTTP 提交任务；仅在命令模板中附加协议注释，不把请求当作 FFmpeg 参数执行。
    private static ExtPluginCommandStep CreatePreviewStep(ExtPluginCommandStep step,EnhancementPlan plan)
    {
        if(!plan.Rtx.TryGetValue(step.Id,out var options))return step;
        var request=JsonNode.Parse(RtxFrameRuntime.CreateRequestJson(options,plan.Settings))!;
        if(options.Vsr&&options.Width==0&&!plan.Settings.RtxTarget.EndsWith('x'))
            request["processing"]!["vsr"]!["scale"]="<根据输入尺寸与目标 "+plan.Settings.RtxTarget+" 计算>";
        string arguments=step.Arguments+Environment.NewLine+
            "# RTX 服务启动："+WindowsArguments.Quote(RtxVideoBackendClient.FindBackend(PortablePaths.CoreRoot))+
            " --port <运行时本地端口> --app-session-id <任务会话 ID>"+Environment.NewLine+
            "# RTX POST /api/jobs："+request.ToJsonString();
        return new(step.Id,step.DisplayName,step.ProcessFileName,arguments)
        {
            WorkingDirectory=step.WorkingDirectory,Placement=step.Placement,Order=step.Order,
            IncludeInPreview=step.IncludeInPreview,ParseFFmpegProgress=step.ParseFFmpegProgress
        };
    }
}
