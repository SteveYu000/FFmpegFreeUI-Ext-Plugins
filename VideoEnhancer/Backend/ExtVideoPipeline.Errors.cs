using System.Collections.Concurrent;
using FFmpegFreeUI.Ext.PluginSdk;

namespace VideoEnhancer;

public sealed partial class ExtVideoPipeline
{
    private readonly ConcurrentDictionary<string,string> _parameterFailures=new();
    private string? _lastPreviewError;

    // 预览回调不向宿主抛异常；真实任务在准备阶段和启动进程前明确失败，避免无声跳过 AI。
    private async ValueTask GuardPipeline(ExtPluginPipelineContext context,CancellationToken token,ExtPluginPipelineCallback callback)
    {
        string preset=context.PresetJson,command=context.CommandLine;
        try { await callback(context,token); }
        catch(OperationCanceledException) { throw; }
        catch(Exception ex)
        {
            bool parameterStage=context.IsPreview||context.StageId is ExtFFmpegFreeUIPipelineStages.TaskBeforePrepare or
                ExtFFmpegFreeUIPipelineStages.CommandBeforeBuild or ExtFFmpegFreeUIPipelineStages.CommandAfterBuild;
            context.ReportProgress((parameterStage?"AI 参数检查失败：":"AI 阶段执行失败：")+ex.Message);
            context.ReportResult(parameterStage?"ai.parameters.error":"ai.execution.error",ex.Message,"AI 处理错误");
            if(parameterStage)ReportParameterError(context.IsPreview?"":context.TaskId,context.InputPath,context.OutputPath,preset,ex);
            else
            {
                EnhancementTaskRegistry.SetStatus(context.TaskId,"失败");
                _host.Log(ExtPluginLogLevel.Error,"VideoEnhancer 阶段执行失败："+ex.Message,ex);
            }
            if(!context.IsPreview)throw;
            context.PresetJson=preset;
            context.CommandLine=command;
            context.Properties["videoenhancer.parameterError"]=ex.Message;
        }
    }

    private void GuardCommand(ExtPluginCommandContext context,Action<ExtPluginCommandContext> callback)
    {
        var arguments=context.Arguments.ToArray();
        var steps=context.Steps.ToArray();
        try
        {
            callback(context);
            if(context.IsPreview)
            {
                _lastPreviewError=null;
                EnhancementTaskRegistry.CurrentParameterError="";
            }
        }
        catch(Exception ex)
        {
            // 回滚本提供器的贡献，保留宿主和其他插件已经加入的参数与步骤。
            context.Arguments.Clear();
            foreach(var argument in arguments)context.Arguments.Add(argument);
            context.Steps.Clear();
            foreach(var step in steps)context.Steps.Add(step);
            context.Properties["videoenhancer.parameterError"]=ex.Message;
            ReportParameterError(context.IsPreview?"":context.TaskId,context.InputPath,context.OutputPath,context.PresetJson,ex);
        }
    }

    private void GuardOverview(ExtPluginPresetOverviewContext context)
    {
        try
        {
            AddOverview(context);
            var settings=EnhancementSettings.FromJson(context.PluginStateJson);
            if(settings.IsEnabled)PreviewPlan(settings.ForInput("<输入文件>",true),context.PresetJson,"<输入文件>");
        }
        catch(Exception ex)
        {
            context.Rows.Add(new("AI 参数检查失败："+ex.Message){Level=ExtPluginPresetOverviewRowLevel.Error});
            ReportParameterError("","","",context.PluginStateJson,ex);
        }
    }

    private void ReportParameterError(string id,string input,string output,string preset,Exception error)
    {
        if(string.IsNullOrWhiteSpace(id))
        {
            EnhancementTaskRegistry.CurrentParameterError=error.Message;
            if(_lastPreviewError==error.Message)return;
            _lastPreviewError=error.Message;
        }
        else
        {
            _parameterFailures[id]=error.Message;
            EnhancementTaskRegistry.Update(id,input,output,"参数无效",error.Message,preset);
        }
        _host.Log(ExtPluginLogLevel.Error,"VideoEnhancer 参数检查失败："+error.Message,error);
    }
}
