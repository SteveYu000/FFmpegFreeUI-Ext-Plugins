using System.IO;
using System.Diagnostics;
using System.Text.Json;
using VideoEnhancer;

internal static partial class Program
{
    private static (int Code,string Output,string Error) Capture(BackendRequest request,CancellationToken token=default)
    {
        using var job=BackendServiceJob.Start(request,token);
        var output=job.Output.ReadToEndAsync();
        var errors=job.Error.ReadToEndAsync();
        if(!job.Wait(10000)){job.Cancel();throw new TimeoutException("管理服务测试超时");}
        return (job.ResultCode,output.GetAwaiter().GetResult(),errors.GetAwaiter().GetResult());
    }

    private static void TestManagementServices()
    {
        System.Console.WriteLine("运行：DLL 管理服务、并发隔离与取消");
        string runtime=Path.Combine(_root,"services"),user=Path.Combine(runtime,"models","User");
        string model=Path.Combine(user,"SR","Fixture","sample","model.pth");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllBytes(model,[1]);
        File.WriteAllText(Path.Combine(user,"model-catalog.json"),JsonSerializer.Serialize(new{
            schemaVersion=1,models=new[]{new{id="direct",displayName="类型化测试模型",relativePath="User/SR/Fixture/sample/model.pth",
                task="upscale",architecture="Fixture",purpose="SR",format="pth",scale=2,sha256="",size=1,importedAtUtc="",backends=new[]{"cuda"}}}}));
        BackendServices.ConfigureRuntime(runtime);
        var list=Task.Run(()=>Capture(new(BackendAction.ListUserModels)));
        var invalid=Task.Run(()=>Capture(new(BackendAction.DeleteUserModel){ModelId="missing"}));
        Task.WaitAll(list,invalid);
        using var catalog=JsonDocument.Parse(list.Result.Output);
        Check(list.Result.Code==0&&catalog.RootElement[0].GetProperty("id").GetString()=="direct"&&list.Result.Error=="","并发模型清单读取不混入其他操作的错误");
        Check(invalid.Result.Code!=0&&invalid.Result.Error.Contains("未找到指定")&&invalid.Result.Output=="","并发失败操作返回独立诊断");

        var update=Capture(new(BackendAction.UpdateUserModel){ModelId="direct",Capabilities=new("Fixture","SR",4,8,["cuda"])});
        using var updated=JsonDocument.Parse(update.Output);
        Check(update.Code==0&&updated.RootElement[0].GetProperty("scale").GetInt32()==4&&updated.RootElement[0].GetProperty("inputMultiple").GetInt32()==8,
            "类型化模型能力修改写入目录清单");
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        var cancelled=Capture(new(BackendAction.DeleteUserModel){ModelId="direct"},cancel.Token);
        Check(cancelled.Code!=0&&File.Exists(model),"已取消请求不执行模型删除");

        TestManagementChildCancellation(runtime,model);
        var removed=Capture(new(BackendAction.DeleteUserModel){ModelId="direct"});
        using var empty=JsonDocument.Parse(Capture(new(BackendAction.ListUserModels)).Output);
        Check(removed.Code==0&&!File.Exists(model)&&empty.RootElement.GetArrayLength()==0,"类型化删除同步清理模型与清单");
        BackendServices.ConfigureRuntime(Path.Combine(_root,"runtime"));
    }

    private static void TestManagementChildCancellation(string runtime,string model)
    {
        string python=Path.Combine(runtime,"python","python"),backend=Path.Combine(runtime,"python","backend");
        Directory.CreateDirectory(python);Directory.CreateDirectory(backend);
        foreach(string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(file,Path.Combine(python,Path.GetFileName(file)),true);
        File.Copy(Path.Combine(AppContext.BaseDirectory,"VideoEnhancer.Tests.exe"),Path.Combine(python,"python.exe"),true);
        string marker=Path.Combine(runtime,"child.pid");
        Environment.SetEnvironmentVariable("VIDEOENHANCER_TEST_SERVICE_CHILD",marker);
        try
        {
            using var token=new CancellationTokenSource();
            using var job=BackendServiceJob.Start(new(BackendAction.InspectUpscaleModel){Path=model},token.Token);
            var output=job.Output.ReadToEndAsync();var errors=job.Error.ReadToEndAsync();
            Until(()=>File.Exists(marker),TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            int pid=int.Parse(File.ReadAllText(marker));token.Cancel();
            Check(job.Wait(10000)&&job.ResultCode!=0,"取消令牌终止等待中的 DLL 模型检测任务");
            bool exited;try{using var child=Process.GetProcessById(pid);exited=child.HasExited;}catch(ArgumentException){exited=true;}
            Check(exited&&File.Exists(model),"取消管理操作关闭其真实子进程并保留模型");
            Task.WaitAll(output,errors);
        }
        finally{Environment.SetEnvironmentVariable("VIDEOENHANCER_TEST_SERVICE_CHILD",null);}
    }
}
