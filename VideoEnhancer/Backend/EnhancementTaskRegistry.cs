using System.Collections.Concurrent;
namespace VideoEnhancer;

public sealed record EnhancementTaskView(string TaskId,string InputPath,string OutputPath,string Status,string Phase,string Settings,string BackendRequest,DateTime UpdatedAt);
public sealed record EnhancementPreviewSource(string OutputPath,double FrameRate,bool UseTelemetry);
public static class EnhancementTaskRegistry
{
    private static readonly ConcurrentDictionary<string,EnhancementTaskView> Records=new();
    private static readonly ConcurrentDictionary<string,EnhancementTelemetry> Telemetry=new();
    private static readonly ConcurrentDictionary<string,IReadOnlyDictionary<string,EnhancementPreviewSource>> PreviewSources=new();
    public static EnhancementPreviewSource? GetPreviewSource(string taskId,string stepId)=>
        PreviewSources.TryGetValue(taskId,out var sources)&&sources.TryGetValue(stepId,out var source)?source:null;
    internal static void SetPreviewSources(string taskId,IReadOnlyDictionary<string,EnhancementPreviewSource> sources)=>
        PreviewSources[taskId]=new Dictionary<string,EnhancementPreviewSource>(sources);
    internal static void ClearTelemetry(string taskId)=>Telemetry.TryRemove(taskId,out _);
    internal static void ClearPreviewSources(string taskId){PreviewSources.TryRemove(taskId,out _);ClearTelemetry(taskId);}
    public static EnhancementTelemetry? GetTelemetry(string id)=>Telemetry.GetValueOrDefault(id);
    public static void SetTelemetry(string id,long frame,long total,double fps)=>Telemetry[id]=new(frame,total,fps);
    public static event EventHandler? Changed;
    private static string _currentSettingsJson="{}";
    public static string CurrentSettingsJson {get=>_currentSettingsJson;set{_currentSettingsJson=value;Changed?.Invoke(null,EventArgs.Empty);}}
    private static string _currentParameterError="";
    public static string CurrentParameterError
    {
        get=>_currentParameterError;
        set
        {
            if(_currentParameterError==value)return;
            _currentParameterError=value;
            Changed?.Invoke(null,EventArgs.Empty);
        }
    }
    public static bool HasActiveTasks=>Records.Values.Any(task=>task.Status is "准备中" or "准备完成" or "增强中" or "编码中" or "正在处理" or "已暂停" or "正在停止" or "收尾中");
    public static IReadOnlyList<EnhancementTaskView> Snapshot()=>Records.Values.OrderByDescending(x=>x.UpdatedAt).ToArray();
    public static void Update(string id,string input,string output,string status,string phase,string settings,string request="")
    {
        if(string.IsNullOrWhiteSpace(id))return;
        Records.AddOrUpdate(id,new EnhancementTaskView(id,input,output,status,phase,settings,request,DateTime.Now),(_,previous)=>
            new(id,input,output,status,phase,settings,previous.BackendRequest.Contains("RTX POST /api/jobs：",StringComparison.Ordinal)?previous.BackendRequest:request,DateTime.Now));Changed?.Invoke(null,EventArgs.Empty);
    }
    public static void SetBackendDetails(string id,string details)
    {
        if(Records.TryGetValue(id,out var previous))
        {
            Records[id]=previous with{BackendRequest=previous.BackendRequest+Environment.NewLine+details,UpdatedAt=DateTime.Now};
            Changed?.Invoke(null,EventArgs.Empty);
        }
    }
    public static void SetStatus(string id,string status)
    {
        if(Records.TryGetValue(id,out var value)){Records[id]=value with{Status=status,UpdatedAt=DateTime.Now};Changed?.Invoke(null,EventArgs.Empty);}
    }
}

public sealed record EnhancementTelemetry(long Frame,long TotalFrames,double Fps);
