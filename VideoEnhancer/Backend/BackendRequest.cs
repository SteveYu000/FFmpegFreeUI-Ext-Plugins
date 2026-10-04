namespace VideoEnhancer;

// 界面直接提交类型化的管理请求；视频任务只走 ExtVideoPipeline。
public enum BackendAction
{
    ListModels, ListModelCatalog, ListInterpolationModels, ListInterpolationCatalog,
    ListUserModels, InspectUpscaleModel, InspectInterpolationModel, ImportModels,
    UpdateUserModel, DeleteUserModel, PrepareInterpolationEngine, CheckEnvironment,
    ListBackends, ValidateEngines, ListDownloadModels, DownloadModel, DeleteDownloadModel,
    CleanDownloadArchives, BackendStatus, UpdateBackend
}

public sealed record UserModelCapabilities(string Architecture,string Purpose,int Scale,int InputMultiple,string[] Backends);

public sealed record BackendRequest(BackendAction Action)
{
    public string Backend { get; init; } = "ncnn";
    public string Path { get; init; } = "";
    public string ModelId { get; init; } = "";
    public UserModelCapabilities? Capabilities { get; init; }
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public bool StaticShape { get; init; }
    public string ChannelSource { get; init; } = "";
    public bool ForceFullPackage { get; init; }
    public string FfmpegPath { get; init; } = "";
    public string FfprobePath { get; init; } = "";
}
