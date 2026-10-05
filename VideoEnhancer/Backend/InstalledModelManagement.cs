namespace VideoEnhancer;

public static partial class BackendServices
{
    // 安装清单覆盖全部后端；成套权重按模型目录显示，未识别的文件仍可管理。
    private static int ListInstalledModelCatalog()
    {
        if (!Directory.Exists(ModelsDir)) return WriteModelCatalogJson([]);
        var users = UserModelCatalog.Load(ModelsDir);
        var containers = Directory.EnumerateDirectories(ModelsDir, "*", SearchOption.AllDirectories)
            .Where(path => IsNcnnModelFolder(path) || IsFlashVsrModelDirectory(path) || IsBasicVsrPlusPlusModelDirectory(path))
            .Concat(users.Select(user => Path.GetFullPath(Path.Combine(ModelsDir, user.RelativePath)))
                .Where(Directory.Exists))
            .Where(path => IsPathUnder(path, ModelsDir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length).ToList();
        containers = containers.Where(path => !containers.Any(other =>
            !other.Equals(path, StringComparison.OrdinalIgnoreCase) && IsPathUnder(path, other))).ToList();
        string[] extensions = [".param", ".pth", ".pkl", ".ckpt", ".pt", ".onnx", ".engine", ".safetensors"];
        var paths = containers.Concat(Directory.EnumerateFiles(ModelsDir, "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                && !containers.Any(directory => IsPathUnder(path, directory))));
        var entries = new List<ModelListCatalogEntry>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DownloadCancellation.Check();
            var relative = UserModelCatalog.NormalizeRelativePath(path, ModelsDir);
            var user = users.FirstOrDefault(item => UserModelCatalog.NormalizeRelativePath(item.RelativePath, ModelsDir)
                .Equals(relative, StringComparison.OrdinalIgnoreCase));
            var backend = IsFlashVsrModelDirectory(path) ? "flashvsr" : IsBasicVsrPlusPlusModel(path) ? "basicvsrpp"
                : Directory.Exists(path) || Path.GetExtension(path).Equals(".param", StringComparison.OrdinalIgnoreCase) ? "ncnn"
                : Path.GetExtension(path).Equals(".onnx", StringComparison.OrdinalIgnoreCase) ? "onnx"
                : IsTensorRTEngineFile(path) ? "tensorrt" : "cuda";
            entries.Add(CreateModelCatalogEntry(path, backend, IsInInterpolationDirectory(path), user));
        }
        return WriteModelCatalogJson(entries.OrderBy(item => item.Task, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ArchitectureGroup, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase));
    }

    // 模型管理和导入页共用用户清单，避免删除文件后留下无效登记。
    public static void RemoveInstalledModel(string modelPath)
    {
        if(EnhancementTaskRegistry.HasActiveTasks)throw new InvalidOperationException("AI 任务运行时不能移除模型");
        string root=Path.GetFullPath(ModelsDir).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string target=Path.GetFullPath(modelPath);
        if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("模型路径超出插件模型目录");
        RejectModelLinks(target);
        var record=UserModelCatalog.Load(ModelsDir).FirstOrDefault(item=>{
            string installed=Path.GetFullPath(Path.Combine(ModelsDir,item.RelativePath.Replace('/',Path.DirectorySeparatorChar)));
            return installed.Equals(target,StringComparison.OrdinalIgnoreCase)||
                target.StartsWith(installed.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        });
        if(record is not null){UserModelCatalog.Delete(ModelsDir,record.Id);return;}
        if(Directory.Exists(target))
        {
            if(!IsNcnnModelFolder(target)&&!IsFlashVsrModelDirectory(target)&&!IsBasicVsrPlusPlusModelDirectory(target))
                throw new InvalidOperationException("只能移除模型目录，不能移除分类目录");
            // 先检查整个目录树，再删除；不跟随模型目录内的联接点或符号链接。
            var directories=new Queue<string>();directories.Enqueue(target);
            while(directories.TryDequeue(out var directory))
                foreach(var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes=File.GetAttributes(child);
                    if((attributes&FileAttributes.ReparsePoint)!=0)
                        throw new InvalidOperationException("模型包含符号链接或联接点，已拒绝移除");
                    if((attributes&FileAttributes.Directory)!=0)directories.Enqueue(child);
                }
            Directory.Delete(target,recursive:true);
            return;
        }
        if(!File.Exists(target))throw new FileNotFoundException("模型文件已被移除",target);
        File.Delete(target);
        if(Path.GetExtension(target).Equals(".param",StringComparison.OrdinalIgnoreCase))
        {
            string binary=Path.ChangeExtension(target,".bin");
            if(File.Exists(binary))File.Delete(binary);
        }
    }

    private static void RejectModelLinks(string path)
    {
        var root=Path.GetFullPath(ModelsDir).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        var current=path;
        while(current is not null)
        {
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("模型路径包含符号链接或联接点，已拒绝移除");
            if(current.Equals(root,StringComparison.OrdinalIgnoreCase))break;
            current=Path.GetDirectoryName(current);
        }
    }
}
