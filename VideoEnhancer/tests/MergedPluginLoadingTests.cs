using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using FFmpegFreeUI.Ext.PluginSdk;

internal static partial class Program
{
    private static void TestMergedPluginLoading(Assembly assembly)
    {
        Check(assembly.GetName().Name == "videoenhancer.ext.3fui", "合并产物的程序集名称使用 Ext 标识");
        // 宿主同时扫描公开与内部的顶层 Entry，仅降低可见性无法消除冲突。
        var entries = assembly.GetTypes().Where(type => !type.IsNested && type.Name == "Entry").ToArray();
        Check(entries.Length == 1 && entries[0].FullName == "videoenhancer.Entry", "合并 DLL 只有一个顶层 Entry");
        Check(assembly.GetType("SharpCompress.Common.Entry") is null &&
            !assembly.GetExportedTypes().Any(type => type.Namespace?.StartsWith("SharpCompress", StringComparison.Ordinal) == true),
            "私有 SharpCompress 类型已内部化并重命名");
        var entry = entries[0];
        Check(entry.IsPublic && typeof(IExtFFmpegFreeUIPlugin).IsAssignableFrom(entry), "插件入口公开并实现实际 Ext SDK 接口");
        var host = new TestHost();
        int subscriptions = 0;
        Action<string, object> subscribe = (name, callback) =>
        {
            Check(name == "*" && callback is Action<string, string>, "合并 DLL 接收宿主队列事件注入");
            subscriptions++;
        };
        entry.GetMethod("SetHost_SubscribeQueueEvents")!.Invoke(null, [subscribe]);
        try
        {
            var plugin = (IExtFFmpegFreeUIPlugin)Activator.CreateInstance(entry)!;
            plugin.Initialize(host);
            entry.GetMethod("Entry", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            Check(plugin.Id == "videoenhancer" && host.Pages.Pages.Count == 2, "合并 DLL 正常初始化并注册两处页面入口");
            Check(host.PipelineImpl.Handlers.Count > 0 && host.CommandsImpl.Steps.Count > 0 && host.Overview.Rows.Count > 0,
                "合并 DLL 注册处理链、命令与参数总览");
            Check(subscriptions == 1, "合并 DLL 订阅一次宿主队列事件");
        }
        finally
        {
            entry.GetMethod("SetHost_SubscribeQueueEvents")!.Invoke(null, [null]);
            var field = entry.GetField("_pipeline", BindingFlags.NonPublic | BindingFlags.Static)!;
            (field.GetValue(null) as IDisposable)?.Dispose();
            field.SetValue(null, null);
        }
    }

    private static void TestMergedArchives(Assembly assembly)
    {
        string source = Path.Combine(_root, "merged-archive-source"), nested = Path.Combine(source, "模型 子目录");
        Directory.CreateDirectory(nested);
        byte[] content = Encoding.UTF8.GetBytes("合并后压缩包内容\n模型配置");
        File.WriteAllBytes(Path.Combine(nested, "配置.txt"), content);
        File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
        var extractor = assembly.GetType("VideoEnhancer.ManagedArchiveExtractor", true)!;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        string sevenZip = Path.Combine(_root, "merged-test.7z");
        extractor.GetMethod("CreateSevenZip", flags)!.Invoke(null, [source, sevenZip]);
        string zip = Path.Combine(_root, "merged-test.zip");
        ZipFile.CreateFromDirectory(source, zip);
        foreach (var archive in new[] { sevenZip, zip })
        {
            string output = Path.Combine(_root, "merged-extract" + Path.GetExtension(archive));
            extractor.GetMethod("Extract", flags)!.Invoke(null, [archive, output, null]);
            Check(File.ReadAllBytes(Path.Combine(output, "模型 子目录", "配置.txt")).SequenceEqual(content),
                "私有类型重命名后仍能解压中文路径与内容：" + Path.GetExtension(archive));
            Check(File.Exists(Path.Combine(output, "empty.bin")) && new FileInfo(Path.Combine(output, "empty.bin")).Length == 0,
                "合并后的解压保留空文件：" + Path.GetExtension(archive));
        }
    }

    private static void TestActualHostPluginDiscovery(Assembly core)
    {
        System.Console.WriteLine("运行：真实宿主扫描合并后的 Ext 插件入口");
        string merged = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "dist", "videoenhancer.ext.3fui.dll"));
        var context = new AssemblyLoadContext("actual-host-plugin-discovery", true);
        context.Resolving += (load, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == name.Name);
        try
        {
            var assembly = context.LoadFromAssemblyPath(merged);
            var manager = core.GetType("FFmpegFreeUI.插件管理", true)!;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var entry = (Type)manager.GetMethod("查找官方Entry类", flags)!.Invoke(null, [assembly])!;
            Check(entry.FullName == "videoenhancer.Entry" && entry.Assembly == assembly, "真实宿主从合并 DLL 唯一识别本插件入口");
            entry.GetMethod("Entry", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            Check((bool)manager.GetMethod("是插件程序集文件", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [merged])!,
                "真实宿主识别新的 .ext.3fui.dll 文件名");
            var metadata = manager.GetMethod("读取插件元数据", flags)!.Invoke(null, [merged])!;
            Check(string.IsNullOrEmpty((string?)metadata.GetType().GetProperty("元数据错误")!.GetValue(metadata)) &&
                metadata.GetType().GetProperty("接口类型")!.GetValue(metadata)!.ToString() == "官方与Ext",
                "真实宿主正确读取合并 DLL 的官方与 Ext 接口元数据");
        }
        finally { context.Unload(); }
    }
}
