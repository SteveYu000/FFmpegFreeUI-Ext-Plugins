using System.Runtime.InteropServices;
namespace VideoEnhancer;
public static partial class BackendServices
{
    // DLL 启动的 RTX sidecar 独立于队列的 FFmpeg 根进程，需要单独绑定宿主退出生命周期。
    internal static IDisposable OwnExtChild(System.Diagnostics.Process process)
    {
        var job=CreateKillOnCloseJob();
        if(job==IntPtr.Zero||!AssignProcessToJobObject(job,process.Handle))
        {
            int error=Marshal.GetLastWin32Error();
            if(job!=IntPtr.Zero)CloseHandle(job);
            try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}
            throw new System.ComponentModel.Win32Exception(error,"不能绑定 RTX 子进程的退出生命周期");
        }
        return new ExtChildJob(job);
    }
    private sealed class ExtChildJob(IntPtr handle) : IDisposable
    {
        private IntPtr _handle=handle;
        public void Dispose(){var owned=Interlocked.Exchange(ref _handle,IntPtr.Zero);if(owned!=IntPtr.Zero)CloseHandle(owned);}
    }
}
