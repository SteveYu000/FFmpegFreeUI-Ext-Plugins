using System.IO;
using System.Reflection;
using System.Text;
using VideoEnhancer;

internal static partial class Program
{
    private static void TestRveLicenseNotices()
    {
        var writer = typeof(BackendServices).GetMethod("WritePatchedRveSource", BindingFlags.Static | BindingFlags.NonPublic)!;
        string path = Path.Combine(_root, "modified-rve.py");
        const string original = "# 原始作者版权声明\r\nfrom __future__ import annotations\r\nvalue = 1\r\n";
        writer.Invoke(null, [path, original, true]);
        var first = File.ReadAllBytes(path);
        var text = File.ReadAllText(path, Encoding.UTF8);
        Check(first.AsSpan().StartsWith(Encoding.UTF8.Preamble), "RVE 修改声明保留 UTF-8 BOM");
        Check(text.Contains(original, StringComparison.Ordinal), "RVE 修改声明不覆盖原始版权与源码");
        Check(text.Contains("AGPL-3.0-only") && text.Contains($"# VideoEnhancer 修改声明：{BackendServices.Version}，2026-10-07。") && text.Contains("licenses/VideoEnhancer"), "RVE 修改文件记录版本、许可、日期和对应源码");
        Check(!text.Replace("\r\n", "").Contains('\n'), "RVE 修改声明保留 CRLF");
        writer.Invoke(null, [path, text, true]);
        Check(File.ReadAllBytes(path).SequenceEqual(first), "重复写入 RVE 修改声明保持内容不变");
        writer.Invoke(null, [path, "# original notice\nvalue = 1\n", false]);
        Check(!File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.Preamble) && !File.ReadAllText(path).Contains('\r'), "RVE 无 BOM / LF 源码维持原编码与换行");
    }
}
