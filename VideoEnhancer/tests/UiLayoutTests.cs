using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using LakeUI;

internal static partial class Program
{
    private static void TestUiLayouts()
    {
        System.Console.WriteLine("运行：首页、分段布局与教程结构");
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var type=typeof(videoenhancer.PluginPanel);
        foreach(float scale in new[]{1f,1.25f,1.5f,2f})
        {
            using var panel=new videoenhancer.PluginPanel(new(),previewOnly:true,parameterMode:true);
            panel.Scale(new SizeF(scale,scale));
            var page=(Control)type.GetField("_pageSegmented",flags)!.GetValue(panel)!;
            page.Size=new Size((int)(1000*scale),(int)(760*scale));
            page.PerformLayout();
            var root=(Control)type.GetField("_segmentRoot",flags)!.GetValue(panel)!;
            var actual=(SizeF)root.GetType().GetProperty("LayoutScale",flags)!.GetValue(root)!;
            Check(Math.Abs(actual.Height-scale)<0.001,"参数模式未挂载的分段页参与 DPI 缩放："+scale);
            for(int index=1;index<root.Controls.Count;index++)
                Check(root.Controls[index].Top>=root.Controls[index-1].Bottom,"分段行布局无重叠："+scale+" / "+index);
            Check(((ModernPanel)page).AutoScroll==false&&((ModernPanel)page).ScrollBarMode==ModernPanel.ScrollMode.Vertical,
                "分段页使用 LakeUI 滚动条，避免原生白色滚动条");
        }
        using var tools=new videoenhancer.PluginPanel(new(),previewOnly:true);
        var modelPage=(ModernPanel)type.GetField("_pageModelManagement",flags)!.GetValue(tools)!;
        var modelList=(UltraDetailListView)type.GetField("_installedModels",flags)!.GetValue(tools)!;
        Check(type.GetField("_homeTasks",flags)!.GetValue(tools) is UltraDetailListView&&modelPage.Contains(modelList)&&
            !((Control)type.GetField("_pageHome",flags)!.GetValue(tools)!).Contains(modelList),"模型列表移到独立管理页，首页保留任务列表");
        foreach(float scale in new[]{1f,1.25f,1.5f,2f})
        {
            using var scaled=new videoenhancer.PluginPanel(new(),previewOnly:true);
            scaled.Scale(new SizeF(scale,scale));
            var page=(ModernPanel)type.GetField("_pageModelManagement",flags)!.GetValue(scaled)!;
            page.Size=new Size((int)(520*scale),(int)(360*scale));
            page.PerformLayout();
            var grid=page.Controls[0];
            for(int index=1;index<grid.Controls.Count;index++)
                Check(grid.Controls[index].Top>=grid.Controls[index-1].Bottom,"模型管理在窄窗口和 DPI 缩放下无重叠："+scale+" / "+index);
            Check(page.BackColor.A==0&&page.BackColor1.A==0&&page.BackgroundSource is not null,
                "模型管理页保留宿主透明背景："+scale);
        }
        var home=(ModernPanel)type.GetField("_pageHome",flags)!.GetValue(tools)!;
        home.Size=new Size(520,440);
        home.PerformLayout();
        var layout=home.Controls[0];
        for(int index=1;index<layout.Controls.Count;index++)
            Check(layout.Controls[index].Top>=layout.Controls[index-1].Bottom,"窄窗口首页各区块无重叠："+index);
        var compose=type.GetMethod("ComposeTutorialMarkdown",BindingFlags.NonPublic|BindingFlags.Static)!;
        string tutorial=(string)compose.Invoke(null,["## 原作者测试章节\n原文内容","离线缓存提示"])!;
        Check(tutorial.Contains("ARXChem")&&tutorial.IndexOf("原作者测试章节",StringComparison.Ordinal)<tutorial.IndexOf("## Ext 版补充",StringComparison.Ordinal),
            "教程保留原作者署名和原文，并在原文后追加 Ext 差异");
        Check(tutorial.Contains("视频参数 | AI增强")&&tutorial.Contains("videoenhancer.ext.3fui.dll")&&tutorial.Contains("FFV1/MKV"),"Ext 教程包含入口、安装名称和处理链区别");
    }
}
