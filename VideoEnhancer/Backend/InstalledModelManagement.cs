namespace VideoEnhancer;

public static partial class BackendServices
{
    // 首页和导入页共用用户模型清单，避免删除文件后留下无效的模型登记。
    public static void RemoveInstalledModel(string modelPath)
    {
        if(EnhancementTaskRegistry.HasActiveTasks)throw new InvalidOperationException("AI 任务运行时不能移除模型");
        string root=Path.GetFullPath(ModelsDir).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string target=Path.GetFullPath(modelPath);
        if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("模型路径超出插件模型目录");
        var record=UserModelCatalog.Load(ModelsDir).FirstOrDefault(item=>{
            string installed=Path.GetFullPath(Path.Combine(ModelsDir,item.RelativePath.Replace('/',Path.DirectorySeparatorChar)));
            return installed.Equals(target,StringComparison.OrdinalIgnoreCase)||
                target.StartsWith(installed.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        });
        if(record is not null){UserModelCatalog.Delete(ModelsDir,record.Id);return;}
        if(!File.Exists(target))throw new FileNotFoundException("模型文件已被移除",target);
        File.Delete(target);
        if(Path.GetExtension(target).Equals(".param",StringComparison.OrdinalIgnoreCase))
        {
            string binary=Path.ChangeExtension(target,".bin");
            if(File.Exists(binary))File.Delete(binary);
        }
    }
}
