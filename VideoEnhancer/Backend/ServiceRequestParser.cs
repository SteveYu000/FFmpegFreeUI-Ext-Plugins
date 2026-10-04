namespace VideoEnhancer;

internal static class ServiceRequestParser
{
    internal static ServiceOptions Parse(string[] args)
    {
        var o = new ServiceOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var (name, inlineValue) = SplitOption(args[i]);
            switch (name)
            {
                case "-h":
                case "--help":
                    o.ShowHelp = true;
                    break;
                case "-v":
                case "--version":
                    o.ShowVersion = true;
                    break;
                case "--list-models":
                case "--search-models":
                    o.ListModels = true;
                    break;
                case "--list-model-catalog":
                    o.ListModelCatalog = true;
                    break;
                case "--list-interp-model-catalog":
                    o.ListInterpModelCatalog = true;
                    break;
                case "--list-user-models":
                    o.ListUserModels = true;
                    break;
                case "--json":
                    o.Json = true;
                    break;
                case "--check":
                    o.CheckOnly = true;
                    break;
                case "--list-backends":
                case "--list_backends":
                    o.ListBackends = true;
                    break;
                case "--validate-engines":
                    o.ValidateEngines = true;
                    break;
                case "--inspect-interp-model":
                    o.InspectInterpModel = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--inspect-upscale-model":
                    o.InspectUpscaleModel = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--import-model":
                    o.ImportModel = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--update-user-model":
                    o.UpdateUserModel = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--delete-user-model":
                    o.DeleteUserModel = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--user-architecture":
                    o.UserArchitecture = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--user-purpose":
                    o.UserPurpose = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--user-scale":
                    o.UserScale = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--user-input-multiple":
                    o.UserInputMultiple = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--user-backends":
                    o.UserBackends = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--prepare-interp-engine":
                    o.PrepareInterpEngine = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--prepare-width":
                    o.PrepareWidth = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--prepare-height":
                    o.PrepareHeight = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--prepare-static-shape":
                    o.PrepareStaticShape = true;
                    break;
                case "--list-download-models":
                    o.ListDownloadModels = true;
                    break;
                case "--clean-download-archives":
                    o.CleanDownloadArchives = true;
                    break;
                case "--download-model":
                    o.DownloadModel = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--delete-download-model":
                    o.DeleteDownloadModel = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--backend-status":
                    o.BackendStatus = true;
                    break;
                case "--update-backend":
                    o.UpdateBackend = true;
                    break;
                case "--force-backend-full":
                    o.ForceBackendFull = true;
                    break;
                case "--apply-backend-patch":
                    o.ApplyBackendPatch = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--backend-channel":
                    o.BackendChannel = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--download-url":
                    o.DownloadUrl = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--download-output":
                    o.DownloadOutput = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--extract-archive":
                    o.ExtractArchive = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--extract-output":
                    o.ExtractOutput = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--debug-split":
                    o.DebugSplit = true;
                    break;
                case "-i":
                case "--input":
                    o.Input = TakeValue(args, ref i, name, inlineValue);
                    o.HasInput = true;
                    break;
                case "-modelpath":
                case "--modelpath":
                case "--model":
                    o.Model = TakeValue(args, ref i, name, inlineValue);
                    o.HasModel = true;
                    break;
                case "-ffmpeg-settings":
                case "--ffmpeg-settings":
                    o.FfmpegSettings = TakeValue(args, ref i, name, inlineValue);
                    o.HasFfmpegSettings = true;
                    break;
                case "--ffmpeg-path":
                    o.FfmpegPath = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--ffprobe-path":
                    o.FfprobePath = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "-output-scale":
                case "--output-scale":
                    o.OutputScale = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "-scale":
                case "--scale":
                    o.ScaleOverride = TakeValue(args, ref i, name, inlineValue);
                    o.HasScaleOverride = true;
                    break;
                case "-pause-shm":
                case "--pause-shm":
                    o.PauseShm = TakeValue(args, ref i, name, inlineValue);
                    o.HasPauseShm = true;
                    break;
                case "-stop-shm":
                case "--stop-shm":
                    o.StopShm = TakeValue(args, ref i, name, inlineValue);
                    o.HasStopShm = true;
                    break;
                case "-interp-model":
                case "--interp-model":
                case "--interp-modelpath":
                    o.InterpModel = TakeValue(args, ref i, name, inlineValue);
                    o.HasInterpModel = true;
                    break;
                case "-interp-factor":
                case "--interp-factor":
                    o.InterpFactor = TakeValue(args, ref i, name, inlineValue);
                    o.HasInterpFactor = true;
                    break;
                case "-no-upscale":
                case "--no-upscale":
                    o.NoUpscale = true;
                    break;
                case "-rtx-hdr":
                case "--rtx-hdr":
                    o.RtxHdr = true;
                    break;
                case "-rtx-target":
                case "--rtx-target":
                    o.RtxTarget = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "-rtx-quality":
                case "--rtx-quality":
                    o.RtxQuality = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "-rtx-hdr-contrast":
                case "--rtx-hdr-contrast":
                    o.RtxHdrContrast = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "-rtx-hdr-saturation":
                case "--rtx-hdr-saturation":
                    o.RtxHdrSaturation = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "-rtx-hdr-middle-gray":
                case "--rtx-hdr-middle-gray":
                    o.RtxHdrMiddleGray = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "-rtx-hdr-max-luminance":
                case "--rtx-hdr-max-luminance":
                    o.RtxHdrMaxLuminance = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--segments-base64":
                    o.SegmentsBase64 = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--allow-mixed-segment-backends":
                    o.AllowMixedSegmentBackends = true;
                    break;
                case "--list-interp-models":
                case "--search-interp-models":
                    o.ListInterpModels = true;
                    break;
                case "-backend":
                case "--backend":
                    o.Backend = TakeValue(args, ref i, name, inlineValue);
                    o.HasBackend = true;
                    break;
                case "-interp-backend":
                case "--interp-backend":
                    o.InterpBackend = TakeValue(args, ref i, name, inlineValue);
                    o.HasInterpBackend = true;
                    break;
                case "-process-order":
                case "--process-order":
                    o.ProcessOrder = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "-upscale-precision":
                case "--upscale-precision":
                    o.UpscalePrecision = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "-interp-precision":
                case "--interp-precision":
                    o.InterpPrecision = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "-scene-threshold":
                case "--scene-threshold":
                    o.SceneThreshold = TakeValue(args, ref i, name, inlineValue);
                    o.HasSceneThreshold = true;
                    break;
                case "-dynamic-optical-flow":
                case "--dynamic-optical-flow":
                    o.DynamicOpticalFlow = true;
                    break;
                case "-tile-size":
                case "--tile-size":
                case "--tilesize":
                    o.TileSize = TakeValue(args, ref i, name, inlineValue);
                    o.HasTileSize = true;
                    break;
                case "--image-input":
                    o.ImageInputs.Add(TakeValue(args, ref i, name, inlineValue));
                    break;
                case "--image-folder":
                    o.ImageFolders.Add(TakeValue(args, ref i, name, inlineValue));
                    break;
                case "--image-output":
                    o.ImageOutput = TakeValue(args, ref i, name, inlineValue);
                    break;
                case "--image-output-original":
                    o.ImageOutputOriginal = true;
                    break;
                case "--image-suffix":
                    o.ImageSuffix = TakeValue(args, ref i, name, inlineValue).Trim().ToLowerInvariant();
                    if (o.ImageSuffix is not ("timestamp" or "model"))
                    {
                        throw new ArgumentException("--image-suffix 仅支持 timestamp 或 model");
                    }
                    break;
                case "--image-png":
                    o.ImagePng = true;
                    break;
                case "--image-source-format":
                    o.ImagePng = false;
                    break;
                default:
                    throw new ArgumentException("未知参数：" + args[i] + "（使用 -h 查看帮助）");
            }
        }
        return o;
    }

    private static string TakeValue(string[] args, ref int i, string name, string? inlineValue)
    {
        if (inlineValue is not null)
        {
            return inlineValue;
        }
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException("参数 " + name + " 缺少值");
        }
        return args[++i];
    }

    private static (string Name, string? Value) SplitOption(string arg)
    {
        var eq = arg.IndexOf('=');
        if (eq > 1 && arg.StartsWith('-'))
        {
            return (arg[..eq], arg[(eq + 1)..]);
        }
        return (arg, null);
    }

}
