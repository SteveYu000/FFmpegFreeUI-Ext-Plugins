using System.Text;
namespace VideoEnhancer;

public sealed class BackendOutputEventArgs(string? data) : EventArgs { public string? Data { get; }=data; }

// 每个管理操作是独立的 DLL 内任务，接收类型化请求和取消令牌。
public sealed class BackendServiceJob : IDisposable
{
    private readonly BackendRequest _request;
    private readonly BackendInvocation _invocation=new();
    private readonly CancellationToken _token;
    private CancellationTokenRegistration _registration;
    private Task<int>? _task;
    private readonly ServiceTextWriter _output,_error;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingOutput=new(),_pendingError=new();
    private bool _readOutput,_readError,_disposed;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<BackendInvocation,byte> Active=new();
    static BackendServiceJob(){AppDomain.CurrentDomain.ProcessExit+=(_,_)=>{foreach(var invocation in Active.Keys)invocation.Stop();};}
    public BackendServiceJob(BackendRequest request,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request=request with {Capabilities=request.Capabilities is {} c?c with {Backends=c.Backends.ToArray()}:null};
        _token=cancellationToken;
        _output=new ServiceTextWriter(line=>{_pendingOutput.Enqueue(line);FlushLines(false);});
        _error=new ServiceTextWriter(line=>{_pendingError.Enqueue(line);FlushLines(true);});
        _invocation.Output=_output;_invocation.Error=_error;
    }
    public event EventHandler<BackendOutputEventArgs>? OutputLine,ErrorLine;
    public event EventHandler? Completed;
    public bool HasCompleted=>_task?.IsCompleted??false;
    public int ResultCode=>_task?.GetAwaiter().GetResult()??throw new InvalidOperationException("服务任务尚未启动");
    public StreamReader Output=>new(_output.OutputStream,Encoding.UTF8,leaveOpen:true);
    public StreamReader Error=>new(_error.OutputStream,Encoding.UTF8,leaveOpen:true);
    public static BackendServiceJob Start(BackendRequest request,CancellationToken token=default)
    {
        var job=new BackendServiceJob(request,token);job.Start();return job;
    }
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(_task is not null)throw new InvalidOperationException("服务任务不能重复启动");
        Active[_invocation]=0;
        _registration=_token.Register(_invocation.Stop);
        _task=Task.Run(()=>
        {
            BackendInvocation.Slot.Value=_invocation;
            try {return BackendServices.Execute(_request);}
            finally
            {
                _registration.Dispose();Active.TryRemove(_invocation,out _);
                _output.Complete();_error.Complete();BackendInvocation.Slot.Value=null;
            }
        });
        _=_task.ContinueWith(_=>Completed?.Invoke(this,EventArgs.Empty),TaskScheduler.Default);
    }
    private void FlushLines(bool error)
    {
        if(error?!_readError:!_readOutput)return;
        var lines=error?_pendingError:_pendingOutput;
        while(lines.TryDequeue(out var line))
            if(error)ErrorLine?.Invoke(this,new(line));else OutputLine?.Invoke(this,new(line));
    }
    public void BeginOutputRead(){_readOutput=true;FlushLines(false);}
    public void BeginErrorRead(){_readError=true;FlushLines(true);}
    public void Wait()=>_task!.GetAwaiter().GetResult();
    public bool Wait(int milliseconds)=>_task!.Wait(milliseconds);
    public Task WaitAsync(CancellationToken token=default)=>_task!.WaitAsync(token);
    public void Cancel()=>_invocation.Stop();
    public void Dispose(){if(_disposed)return;_disposed=true;if(_task is not null&&!HasCompleted)Cancel();}
}
