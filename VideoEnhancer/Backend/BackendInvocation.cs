using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
namespace VideoEnhancer;

// 每个 DLL 服务调用拥有独立的输出、取消令牌和子进程集合。
internal sealed class BackendInvocation
{
    internal static readonly AsyncLocal<BackendInvocation?> Slot = new();
    internal static BackendInvocation? Current => Slot.Value;
    internal readonly CancellationTokenSource Cancellation = new();
    internal readonly ConcurrentDictionary<int, System.Diagnostics.Process> Children = new();
    internal TextWriter Output = TextWriter.Null;
    internal TextWriter Error = TextWriter.Null;
    internal string FfmpegPath = "";
    internal string FfprobePath = "";
    internal ConsoleCancelEventHandler? CancelKeyPress;
    internal void Stop()
    {
        Cancellation.Cancel();
        foreach(var process in Children.Values)
            try { if(!process.HasExited) process.Kill(true); } catch(InvalidOperationException) {} catch(System.ComponentModel.Win32Exception) {}
    }
}

// 不更改宿主的全局 Console；既有后端服务输出通过当前调用返回给界面。
internal static class Console
{
    internal static TextWriter Out => BackendInvocation.Current?.Output ?? TextWriter.Null;
    internal static TextWriter Error => BackendInvocation.Current?.Error ?? TextWriter.Null;
    internal static Encoding OutputEncoding { get; set; } = Encoding.UTF8;
    internal static Encoding InputEncoding { get; set; } = Encoding.UTF8;
    internal static void Write(string? value) => Out.Write(value);
    internal static void WriteLine() => Out.WriteLine();
    internal static void WriteLine(string? value) => Out.WriteLine(value);
    internal static Stream OpenStandardOutput() => BackendInvocation.Current?.Output is ServiceTextWriter writer ? writer.OutputStream : Stream.Null;
    internal static event ConsoleCancelEventHandler CancelKeyPress { add { if(BackendInvocation.Current is {} c) c.CancelKeyPress += value; } remove { if(BackendInvocation.Current is {} c) c.CancelKeyPress -= value; } }
}

// 后端创建的子进程随当前 DLL 调用取消，宿主自身不会被加入该集合。
internal sealed class Process : System.Diagnostics.Process
{
    private CancellationTokenRegistration _registration;
    public new bool Start()
    {
        PortablePaths.ConfigureChildProcess(StartInfo);
        var started = base.Start();
        if(started && BackendInvocation.Current is {} invocation)
        {
            invocation.Children[Id] = this;
            _registration = invocation.Cancellation.Token.Register(() => {try {if(!HasExited)Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}});
        }
        return started;
    }
    public new static Process Start(ProcessStartInfo info) { var process = new Process{StartInfo=info}; process.Start(); return process; }
    protected override void Dispose(bool disposing) { _registration.Dispose(); base.Dispose(disposing); }
}

internal sealed class ServiceOutputStream : Stream
{
    private readonly BlockingCollection<byte[]> _chunks = new();
    private byte[]? _current;
    private int _offset;
    internal void Append(string text) { if(!_chunks.IsAddingCompleted) _chunks.Add(Encoding.UTF8.GetBytes(text)); }
    internal void Complete() { if(!_chunks.IsAddingCompleted)_chunks.CompleteAdding(); }
    public override int Read(byte[] buffer,int offset,int count)
    {
        while(_current is null || _offset >= _current.Length)
        {
            if(!_chunks.TryTake(out _current,Timeout.Infinite)) return 0;
            _offset=0;
        }
        int size=Math.Min(count,_current.Length-_offset);Array.Copy(_current,_offset,buffer,offset,size);_offset+=size;return size;
    }
    public override void Write(byte[] buffer,int offset,int count) { if(!_chunks.IsAddingCompleted)_chunks.Add(buffer.AsSpan(offset,count).ToArray()); }
    public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;
    public override long Length=>throw new NotSupportedException();
    public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Flush(){}
}

internal sealed class ServiceTextWriter : TextWriter
{
    internal readonly ServiceOutputStream OutputStream = new();
    private readonly Action<string> _line;
    private readonly StringBuilder _pending = new();
    private readonly object _gate=new();
    internal ServiceTextWriter(Action<string> line) { _line=line; }
    public override Encoding Encoding=>Encoding.UTF8;
    public override void Write(char value)=>Write(value.ToString());
    public override void Write(string? value)
    {
        if(value is null)return;
        lock(_gate)
        {
            OutputStream.Append(value);
            foreach(char c in value)
                if(c=='\n'){_line(_pending.ToString().TrimEnd('\r'));_pending.Clear();}else _pending.Append(c);
        }
    }
    internal void Complete() {lock(_gate){if(_pending.Length>0){_line(_pending.ToString());_pending.Clear();}OutputStream.Complete();}}
}
