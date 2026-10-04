using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace VideoEnhancer;

/// <summary>已审计 NCNN 图按内容匹配；重命名或导入目录不改变架构、倍率。</summary>
internal static class NcnnModelSignatures
{
    private static readonly Lazy<Dictionary<string, (string Architecture, int Scale)>> Signatures = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VideoEnhancer.Embedded.ncnn-model-signatures.json")!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.EnumerateArray().ToDictionary(
            item => item.GetProperty("sha256").GetString()!,
            item => (item.GetProperty("architecture").GetString()!, item.GetProperty("scale").GetInt32()),
            StringComparer.OrdinalIgnoreCase);
    });

    internal static (string Architecture, int Scale)? Get(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        var param = Directory.EnumerateFiles(directory, "*.param").FirstOrDefault();
        if (param is null) return null;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(param)));
        return Signatures.Value.TryGetValue(hash, out var signature) ? signature : null;
    }
}
