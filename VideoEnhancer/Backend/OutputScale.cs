using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoEnhancer;

/// <summary>输出倍率独立于推理倍率；编码前缩放，不修改权重或 Engine。</summary>
internal static class OutputScale
{
    internal static int Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale)
            && scale is >= 1 and <= 8) return scale;
        throw new ArgumentException("-output-scale 必须是 1–8 的整数");
    }

    internal static string Encoder(string encoder, int width, int height, int targetScale)
    {
        if (targetScale == 0) return encoder;
        if (width <= 0 || height <= 0) throw new ArgumentException("无法探测输入尺寸，不能设置目标输出倍率");
        // 显式目标倍率决定输出尺寸，避免预设里的 -s 再次覆盖 Lanczos 的结果。
        encoder = Regex.Replace(encoder, "(?:^|\\s)-s(?::v(?::0)?)?\\s+(?:\"[^\"]*\"|[^\\s]+)", "").Trim();
        var filter = $"scale={checked(width * targetScale)}:{checked(height * targetScale)}:flags=lanczos";
        // 在已有普通视频滤镜之后确定目标尺寸，保证最终输出符合选择的倍率。
        var match = Regex.Match(encoder, "(?:^|\\s)(?:-vf|-filter:v(?::0)?)\\s+(?<filter>\"[^\"]*\"|[^\\s]+)");
        if (match.Success)
        {
            var value = match.Groups["filter"];
            var existing = value.Value.Trim('"');
            var combined = existing + "," + filter;
            if (value.Value.StartsWith('"')) combined = "\"" + combined + "\"";
            return encoder[..value.Index] + combined + encoder[(value.Index + value.Length)..];
        }
        return encoder + " -vf " + filter;
    }
}
