using System.IO;
using System.Text.Json;
using FFmpegFreeUI.Ext.PluginSdk;
using VideoEnhancer;

internal static partial class Program
{
    private static async Task TestParameterErrorsAsync()
    {
        System.Console.WriteLine("运行：参数异常隔离与执行拒绝");
        var host=new TestHost();
        using var pipeline=new ExtVideoPipeline(host,()=>Ffmpeg,()=>Ffprobe);
        var missing=new EnhancementSettings{UpscaleEnabled=true};
        string malformed="{\"插件扩展数据\":{\"videoenhancer\":\"{bad\"}}";
        foreach(var preset in new[]{Preset(missing),malformed})
        {
            var preview=new ExtPluginCommandContext{IsPreview=true,InputPath="<输入文件>",PresetJson=preset};
            preview.Arguments.Add(new(ExtPluginCommandArgumentPosition.AfterInput,"-i other-plugin.srt"));
            foreach(var provider in host.CommandsImpl.Parameters)provider.Callback(preview);
            foreach(var provider in host.CommandsImpl.Steps)provider.Callback(preview);
            Check(preview.Steps.Count==0&&preview.Arguments.Single().Text=="-i other-plugin.srt","无效预览不贡献 AI 命令且保留其他插件参数");
            Check(!string.IsNullOrWhiteSpace(EnhancementTaskRegistry.CurrentParameterError),"预览错误由插件发布诊断");
            var context=new ExtPluginPipelineContext{IsPreview=true,InputPath="<输入文件>",PresetJson=preset,CommandLine="-i original.mkv -c copy out.mkv"};
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.CommandBeforeBuild,context);
            await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.CommandAfterBuild,context);
            Check(context.PresetJson==preset&&context.CommandLine=="-i original.mkv -c copy out.mkv","预览阶段解析失败不会改写原始预设与命令");
            var overview=new ExtPluginPresetOverviewContext("videoenhancer",preset,preset==malformed?"{bad":missing.ToJson());
            foreach(var provider in host.Overview.Rows)provider.Callback(overview);
            Check(overview.Rows.Any(row=>row.Level==ExtPluginPresetOverviewRowLevel.Error),"参数总览直接显示错误行");
        }
        string id="invalid-parameters-"+Guid.NewGuid().ToString("N");
        pipeline.TrackQueuedTask(id,"input.mkv","output.mkv",malformed);
        Check(EnhancementTaskRegistry.Snapshot().Single(item=>item.TaskId==id).Status=="参数无效","损坏的队列预设由插件记录错误而不抛向 UI");
        var actualCommand=new ExtPluginCommandContext{TaskId=id,InputPath="input.mkv",PresetJson=Preset(missing)};
        foreach(var provider in host.CommandsImpl.Parameters)provider.Callback(actualCommand);
        var actual=new ExtPluginPipelineContext{TaskId=id,InputPath="input.mkv",PresetJson=Preset(missing)};
        bool rejected=false;
        try { await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.ProcessBeforeStart,actual); }
        catch(InvalidOperationException ex) { rejected=ex.Message.Contains("参数无效"); }
        Check(rejected,"无效真实任务在进程启动前拒绝执行，不能静默跳过 AI");
        var errors=new List<ExtPluginTaskResult>();
        var prepare=new ExtPluginPipelineContext(null,errors.Add){TaskId=id,PresetJson=malformed,InputPath="input.mkv"};
        try { await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare,prepare); }
        catch(JsonException) { }
        Check(errors.Any(error=>error.Key=="ai.parameters.error"),"真实任务准备失败记录结构化诊断");
        await host.PipelineImpl.Invoke(ExtFFmpegFreeUIPipelineStages.TaskAfterFinish,actual);
        pipeline.OnQueueEvent("task.added","{bad");
        Check(videoenhancer.PluginConfig.FromStateJson("{bad").Enabled==false,"无效页面预设不会引起宿主未处理异常");
        var nullable=videoenhancer.PluginConfig.FromStateJson("{\"Backend\":null,\"Model\":null,\"SegmentedVideos\":null,\"AutoCheckUpdates\":false}");
        Check(nullable.Backend=="ncnn"&&nullable.Model==""&&nullable.SegmentedVideos.Count==0&&!nullable.AutoCheckUpdates,"页面使用后端补全的默认值并保留更新偏好");
        using(var page=new videoenhancer.PluginPanel(new(),previewOnly:true,parameterMode:true))
        {
            var context=new TestPageContext{StateJson="{bad"};
            typeof(videoenhancer.PluginPanel).GetMethod("BindPresetContext",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(page,[context]);
            context.Restore("{bad");
            Check(context.StateJson=="{bad","错误预设显示默认页面时保留原始宿主数据");
        }
        var disabled=new ExtPluginCommandContext{IsPreview=true,InputPath="<输入文件>",PresetJson=Preset(new())};
        foreach(var provider in host.CommandsImpl.Parameters)provider.Callback(disabled);
        Check(EnhancementTaskRegistry.CurrentParameterError.Length==0,"修正参数后清除过期预览诊断");
        EnhancementTaskRegistry.CurrentSettingsJson="{}";
    }
}
