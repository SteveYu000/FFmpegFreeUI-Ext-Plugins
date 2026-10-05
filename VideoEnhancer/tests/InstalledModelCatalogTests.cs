using System.IO;
using System.Reflection;
using System.Text.Json;
using LakeUI;
using VideoEnhancer;

internal static partial class Program
{
    // 模拟真实安装目录的结构；无需运行推理或下载模型。
    private static void CreateInstalledModelFixtures(string models)
    {
        void FileAt(string relative, string content = "测试模型")
        {
            string path=Path.Combine(models,relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,content);
        }
        foreach(var folder in new[]{"Param-Bin/AnimeJaNai-V2-2x-Compact-36K","Param-Bin/RealESRGAN-x4plus-Anime-4x","Frame-Interpolation/RIFE/rife-v4.6"})
        {
            // 权重文件名刻意不同于目录名，验证管理页显示逻辑模型名称。
            FileAt(folder+"/weights.param");FileAt(folder+"/weights.bin");
        }
        FileAt("ONNX/AniSD-DC-SPAN-92500-fp32-2x.onnx");
        FileAt("User/SR/RRDBNet/custom-model/model.pth");
        FileAt("Unknown/unnamed.pth");
        foreach(var file in new[]{"diffusion_pytorch_model_streaming_dmd.safetensors","LQ_proj_in.ckpt","TCDecoder.ckpt","Wan2.1_VAE.pth"})
            FileAt("FlashVSR/"+file);
        FileAt("User/model-catalog.json",JsonSerializer.Serialize(new{
            schemaVersion=1,models=new[]{new{id="catalog-test-user",displayName="用户模型",
                relativePath="User/SR/RRDBNet/custom-model/model.pth",task="upscale",architecture="RRDBNet",purpose="SR",
                format="pth",scale=4,sha256="",size=1,importedAtUtc="",backends=new[]{"cuda","tensorrt"}}}}));
    }

    private static void TestInstalledModelCatalog()
    {
        Console.WriteLine("运行：模型管理分类、显示名称与安装路径");
        string runtime=Path.Combine(_root,"model-management"),models=Path.Combine(runtime,"models");
        CreateInstalledModelFixtures(models);
        BackendServices.ConfigureRuntime(runtime);
        try
        {
            var result=Capture(new(BackendAction.ListInstalledModelCatalog));
            Check(result.Code==0,"全部已安装模型通过 DLL 服务读取");
            using var installed=JsonDocument.Parse(result.Output);
            var entries=installed.RootElement.EnumerateArray().ToArray();
            Check(entries.Length==7,"NCNN 成对文件和 FlashVSR 成套权重分别只显示一条模型");
            foreach(var request in new[]{
                new BackendRequest(BackendAction.ListModelCatalog){Backend="ncnn"},
                new BackendRequest(BackendAction.ListModelCatalog){Backend="onnx"},
                new BackendRequest(BackendAction.ListModelCatalog){Backend="cuda"},
                new BackendRequest(BackendAction.ListInterpolationCatalog){Backend="ncnn"}})
            {
                var selected=Capture(request);
                Check(selected.Code==0,"AI 增强模型目录可读取："+request.Backend+" / "+request.Action);
                using var choices=JsonDocument.Parse(selected.Output);
                foreach(var choice in choices.RootElement.EnumerateArray())
                {
                    var managed=entries.Single(item=>item.GetProperty("relativePath").GetString()==choice.GetProperty("relativePath").GetString());
                    Check(managed.GetProperty("displayName").GetString()==choice.GetProperty("displayName").GetString()&&
                        managed.GetProperty("architectureGroup").GetString()==choice.GetProperty("architectureGroup").GetString()&&
                        managed.GetProperty("scale").GetInt32()==choice.GetProperty("scale").GetInt32(),
                        "管理清单名称、架构分类、倍率与 AI 增强一致："+choice.GetProperty("id").GetString());
                }
            }
            var ncnn=entries.Single(item=>item.GetProperty("relativePath").GetString()=="Param-Bin/AnimeJaNai-V2-2x-Compact-36K");
            Check(ncnn.GetProperty("displayName").GetString()=="AnimeJaNai-V2-2x-Compact-36K"&&
                ncnn.GetProperty("architectureGroup").GetString()=="Compact","NCNN 显示解析后的模型目录名，按实际架构分类");
            var user=entries.Single(item=>item.GetProperty("source").GetString()=="user");
            Check(user.GetProperty("architectureGroup").GetString()=="ESRGAN / RRDB"&&
                user.GetProperty("backends").EnumerateArray().Select(item=>item.GetString()).SequenceEqual(new[]{"cuda","tensorrt"}),
                "用户模型归入网络家族，并保留全部支持后端");
            Check(entries.Any(item=>item.GetProperty("displayName").GetString()=="unnamed"),"未知模型仍出现在管理清单中");

            using var panel=new videoenhancer.PluginPanel(new(),previewOnly:true);
            var panelType=typeof(videoenhancer.PluginPanel);
            var itemType=panelType.Assembly.GetType("videoenhancer.ModelCatalogItem",true)!;
            var listType=typeof(List<>).MakeGenericType(itemType);
            var catalog=JsonSerializer.Deserialize(result.Output,listType,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
            panelType.GetMethod("ApplyInstalledModelCatalog",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(panel,[catalog]);
            var list=(UltraDetailListView)panelType.GetField("_installedModels",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(panel)!;
            Check(list.Items.Count==7&&list.Groups.Any(group=>group.Name=="视频补帧 · RIFE")&&
                list.Groups.Any(group=>group.Name=="视频超分 · ESRGAN / RRDB"),"模型管理按任务和 AI 架构生成真实分组");
            Check(list.Items.Any(item=>item.SubItems[0].Text=="AnimeJaNai-V2-2x-Compact-36K  · 2x")&&
                !list.Items.Any(item=>item.SubItems[0].Text=="weights"),"列表使用 AI 菜单的模型名和倍率，无裸权重文件名");
            var userRow=list.Items.Single(item=>item.SubItems[2].Text=="User/SR/RRDBNet/custom-model/model.pth");
            Check(userRow.SubItems[0].Text.EndsWith("[用户]")&&userRow.SubItems[1].Text=="CUDA / TensorRT",
                "管理列表显示用户标记与后端名称");
            int displayIndex=0;
            foreach(var group in list.Groups)
            {
                displayIndex++;
                if(group.IsCollapsed)continue;
                foreach(var row in list.Items.Where(item=>item.GroupName==group.Name))
                {
                    if(ReferenceEquals(row,userRow))list.SelectedIndex=displayIndex;
                    displayIndex++;
                }
            }
            Check(ReferenceEquals(list.SelectedItem,userRow),"按包含分组标题的 LakeUI 显示索引选中用户模型");
            panelType.GetMethod("ApplyInstalledModelCatalog",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(panel,[catalog]);
            Check(list.SelectedItem?.SubItems[2].Text==userRow.SubItems[2].Text,"刷新模型清单按真实路径保留选择");
            list.Groups.First(group=>group.Name=="视频补帧 · RIFE").IsCollapsed=true;
            panelType.GetMethod("ApplyInstalledModelCatalog",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(panel,[catalog]);
            Check(list.Groups.First(group=>group.Name=="视频补帧 · RIFE").IsCollapsed&&list.SelectedItem?.SubItems[2].Text==userRow.SubItems[2].Text,
                "刷新清单保留组折叠状态，选中项不偏移到其他模型");

            var mixedCase=JsonSerializer.Deserialize(
                "[{\"id\":\"a\",\"displayName\":\"模型 A\",\"relativePath\":\"a.pth\",\"task\":\"upscale\",\"architectureGroup\":\"CustomNet\"},"+
                "{\"id\":\"b\",\"displayName\":\"模型 B\",\"relativePath\":\"b.pth\",\"task\":\"upscale\",\"architectureGroup\":\"customnet\"}]",
                listType,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
            panelType.GetMethod("ApplyInstalledModelCatalog",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(panel,[mixedCase]);
            panelType.GetMethod("ApplyInstalledModelCatalog",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(panel,[mixedCase]);
            Check(list.Groups.Count==1&&list.Items.Count==2,"自定义架构名称大小写不同也归入同组，刷新不会因重复分组失败");

            string folder=Path.Combine(models,"Param-Bin","AnimeJaNai-V2-2x-Compact-36K");
            BackendServices.RemoveInstalledModel(folder);
            Check(!Directory.Exists(folder)&&Directory.Exists(Path.Combine(models,"Param-Bin","RealESRGAN-x4plus-Anime-4x")),
                "移除 NCNN 模型删除整对权重，并保留同分类的其他模型");
            string flash=Path.Combine(models,"FlashVSR");
            BackendServices.RemoveInstalledModel(flash);
            Check(!Directory.Exists(flash),"移除成套模型时统一删除其模型目录");
            bool rejected=false;try{BackendServices.RemoveInstalledModel(Path.Combine(models,"Param-Bin"));}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&Directory.Exists(Path.Combine(models,"Param-Bin")),"拒绝把模型分类目录作为模型移除");
            rejected=false;try{BackendServices.RemoveInstalledModel(models);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&Directory.Exists(models),"拒绝删除模型根目录");
            string loose=Path.Combine(models,"loose.param");File.WriteAllText(loose,"参数");File.WriteAllText(Path.ChangeExtension(loose,".bin"),"权重");
            BackendServices.RemoveInstalledModel(loose);
            Check(!File.Exists(loose)&&!File.Exists(Path.ChangeExtension(loose,".bin")),"单独 NCNN 文件仍成对移除");
        }
        finally{BackendServices.ConfigureRuntime(Path.Combine(_root,"runtime"));}
    }
}
