using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

internal static partial class Program
{
    private static void TestNativePreview()
    {
        var assembly = typeof(videoenhancer.Entry).Assembly;
        string nativeDirectory = Path.Combine(Path.GetDirectoryName(assembly.Location)!, "videoenhancer", "bin", "fff-native-11");
        string[] files = ["FFF.Native.dll", "ass-9.dll", "brotlicommon.dll", "brotlidec.dll", "bz2.dll", "freetype.dll", "fribidi-0.dll", "harfbuzz.dll", "libpng16.dll", "z.dll"];
        Check(files.All(file => File.Exists(Path.Combine(nativeDirectory, file)) && new FileInfo(Path.Combine(nativeDirectory, file)).Length > 0), "构建和项目引用传递复制全部十个预览原生 DLL");
        var api = LoadPreviewNativeApi(assembly);
        Check((uint)api.GetType().GetMethod("GetApiVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(api, null)! == 11, "数据目录重定向后仍可从安装目录加载真实 FFF.Native API 11");
        // 同时加载字幕库，验证字体和压缩依赖能在独立 DLL 布局中解析。
        var ass = NativeLibrary.Load(Path.Combine(nativeDirectory, "ass-9.dll"));
        try { Check(NativeLibrary.GetExport(ass, "ass_library_init") != IntPtr.Zero, "独立分发的 libass 及传递原生依赖可加载"); }
        finally { NativeLibrary.Free(ass); }
        Check(!Directory.Exists(Path.Combine(_root, "runtime", "bin", "fff-native-11")), "预览加载不会向重定向的数据目录释放原生库");

        string merged = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "dist", "videoenhancer.ext.3fui.dll"));
        string incompleteDirectory = Path.Combine(_root, "不完整 ZIP 安装");
        Directory.CreateDirectory(incompleteDirectory);
        string entry = Path.Combine(incompleteDirectory, "videoenhancer.ext.3fui.dll");
        File.Copy(merged, entry);
        var unloading = TestIncompletePreviewInstallation(entry, incompleteDirectory);
        // 卸载是异步生效的；先回收隔离加载上下文，再删除测试临时 DLL。
        for (int attempt = 0; unloading.IsAlive && attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Check(!unloading.IsAlive, "不完整 ZIP 的隔离测试加载上下文已卸载");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference TestIncompletePreviewInstallation(string entry, string incompleteDirectory)
    {
        var context = new AssemblyLoadContext("missing-preview-native-test", true);
        var unloading = new WeakReference(context, trackResurrection: true);
        context.Resolving += (load, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(item => item.GetName().Name == name.Name);
        try
        {
            var incomplete = context.LoadFromAssemblyPath(entry);
            Check(incomplete.GetType("videoenhancer.EmbeddedFffNativePayload") is null, "发行 DLL 不再包含原生二进制的 Base64 载荷模块");
            bool rejected = false;
            try { LoadPreviewNativeApi(incomplete); }
            catch (TargetInvocationException ex) when (ex.InnerException is FileNotFoundException missing)
            {
                rejected = missing.FileName == Path.Combine(incompleteDirectory, "videoenhancer", "bin", "fff-native-11", "FFF.Native.dll") && missing.Message.Contains("完整解压");
            }
            Check(rejected, "缺少随包预览库时提示完整解压 ZIP，并报告所需安装路径");
            Check(!Directory.Exists(Path.Combine(incompleteDirectory, "videoenhancer")), "不完整安装不会触发自解压或创建原生库目录");
        }
        finally { context.Unload(); }
        return unloading;
    }

    private static object LoadPreviewNativeApi(Assembly assembly)
    {
        var preview = assembly.GetType("videoenhancer.FffPreviewSession", true)!;
        var api = preview.GetNestedType("NativeApiTable", BindingFlags.NonPublic)!;
        return api.GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
    }
}
