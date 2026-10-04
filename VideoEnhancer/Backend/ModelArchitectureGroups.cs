namespace VideoEnhancer;

/// <summary>菜单按网络家族归组，保留检测器返回的具体架构，不改变模型路径。</summary>
internal static class ModelArchitectureGroups
{
    internal static string Get(string architecture) => architecture.Trim().ToLowerInvariant() switch
    {
        "esrgan" or "esrgan-lite" or "rrdbnet" or "rrdb" => "ESRGAN / RRDB",
        "compact" or "srvggnetcompact" or "realesrgan compact" or "realesrgan (compact)" or "realesr-compact" or "esrgan-refiner" => "Compact",
        "realplksr" or "realplksr-l" or "realplksr-s" => "RealPLKSR",
        "dat" or "dat2" => "DAT",
        "spanplus" or "sudo_spanplus" => "SPANPlus",
        "span" or "spanf3" => "SPAN",
        "" or "unknown" or "onnx" or "ncnn" or "realesrgan" or "其他模型" => "其他模型",
        _ => architecture.Trim(),
    };
}
