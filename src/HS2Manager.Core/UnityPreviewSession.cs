using System.IO.MemoryMappedFiles;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using HS2Manager.Preview.Contracts;
using HS2Manager.BuildTools;

namespace HS2Manager.Core;

/// <summary>Data-only IPC to the owned Unity player. The Windows job owns its entire process tree.</summary>
public sealed class UnityPreviewSession : IDisposable
{
    private readonly string session=Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource lifetime=new();
    private readonly object writes=new();
    private NamedPipeServerStream? pipe;
    private NamedPipeServerStream? events;
    private MemoryMappedFile? map;
    private MemoryMappedViewAccessor? view;
    private Mutex? frameLock;
    private Task? process,reader;
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<long,TaskCompletionSource<KernelMessage>> loads=[];
    private long sequence;
    private byte[]? frameBuffer;
    private int disposed;
    private string dataDirectory="";
    private long presentationLoad,presentationStarted;
    private bool presented;
    public bool IsUsable => Volatile.Read(ref disposed)==0 && !lifetime.IsCancellationRequested;
    public int ProcessId {get;private set;}
    public long CurrentLoad {get;private set;}
    public UnityFrame? LatestFrame {get;private set;}
    public event Action<string>? Failed;
    public event Action<KernelMessage>? Progress;
    public void TrackPresentation(long load,long started)
    {presentationLoad=load;presentationStarted=started;presented=false;}
    public void ReportFramePresented(long load)
    {
        if(presented || load!=presentationLoad || load!=CurrentLoad)return;
        presented=true;
        PreviewPerformance.Write(dataDirectory,new{Kind="firstFrame",Session=session,Load=load,Milliseconds=Stopwatch.GetElapsedTime(presentationStarted).TotalMilliseconds});
    }
    public static string DefaultPlayer
    {
        get
        {
            string packaged=Path.Combine(AppContext.BaseDirectory,"UnityPreview","HS2Manager.UnityPreview.exe");
            if(File.Exists(packaged))return packaged;
            // Source launches use the last successful local build. Portable installs stay self-contained.
            for(var folder=new DirectoryInfo(AppContext.BaseDirectory);folder!=null;folder=folder.Parent)
                if(File.Exists(Path.Combine(folder.FullName,"HS2Manager.slnx")))
                {
                    string marker=Path.Combine(folder.FullName,"artifacts","unity-preview","current-player.txt");
                    if(File.Exists(marker)){string player=File.ReadAllText(marker).Trim();if(Path.IsPathFullyQualified(player)&&File.Exists(player))return player;}
                    break;
                }
            return packaged;
        }
    }

    public async Task StartAsync(string player,string data,CancellationToken cancellation)
    {
        dataDirectory=data;
        var startupWatch=Stopwatch.StartNew();
        using var startup=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);
        cancellation=startup.Token;
        if(!File.Exists(player))throw new FileNotFoundException("便携包缺少独立 Unity 内核，请重新解压完整安装包。",player);
        Directory.CreateDirectory(Path.Combine(data,"Logs"));
        pipe=new NamedPipeServerStream("HS2Manager.Unity."+session,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        events=new NamedPipeServerStream("HS2Manager.Unity.Events."+session,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        map=MemoryMappedFile.CreateNew("Local\\HS2Manager.Unity.Frames."+session,KernelWire.MapBytes);
        view=map.CreateViewAccessor();frameLock=new Mutex(false,"Local\\HS2Manager.Unity.Lock."+session);
        string log=Path.GetFullPath(Path.Combine(data,"Logs","Unity-"+session+".log"));
        process=Task.Run(()=>
        {
            try
            {
                int code=OwnedToolProcess.Run(Path.GetFullPath(player),["-force-d3d11","-screen-fullscreen","0","--session",session,"-logFile",log],Path.GetDirectoryName(Path.GetFullPath(player))!,int.MaxValue,null!,null,lifetime.Token,id=>ProcessId=id);
                if(!lifetime.IsCancellationRequested)Fault(new IOException("独立内核已退出（"+code+"），日志："+log));
            }
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
            catch(Exception ex){if(!lifetime.IsCancellationRequested)Fault(ex);}
        });
        try
        {
            await Task.WhenAll(pipe.WaitForConnectionAsync(cancellation),events.WaitForConnectionAsync(cancellation)).WaitAsync(TimeSpan.FromSeconds(60),cancellation).ConfigureAwait(false);
            reader=Task.Run(Read);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(60),cancellation).ConfigureAwait(false);
            PreviewPerformance.Write(data,new{Kind="startup",Session=session,Milliseconds=startupWatch.Elapsed.TotalMilliseconds});
        }
        catch{Dispose();throw;}
    }
    private void Read()
    {
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                var message=JsonSerializer.Deserialize<KernelMessage>(KernelWire.Read(events!),UnityCharacterService.JsonOptions)??throw new IOException("Empty kernel response.");
                if(message.Session!=session || message.Version!=KernelWire.Version)throw new IOException("独立内核协议不匹配。");
                if(message.Kind=="Ready")ready.TrySetResult();
                if(message.Kind=="Progress")Progress?.Invoke(message);
                if(message.Kind=="Metrics")PreviewPerformance.Write(dataDirectory,new{Kind="kernel",Session=session,Load=message.Load,Stages=JsonSerializer.Deserialize<JsonElement>(message.Text)});
                TaskCompletionSource<KernelMessage>? pending;
                lock(loads)loads.TryGetValue(message.Load,out pending);
                if(message.Kind=="Loaded")pending?.TrySetResult(message);
                if(message.Kind=="Error")
                {
                    pending?.TrySetException(new IOException(message.Text));
                    if(message.Load==CurrentLoad)Failed?.Invoke(message.Text);
                }
            }
        }
        catch(Exception ex){if(!lifetime.IsCancellationRequested)Fault(ex);}
    }
    private void Fault(Exception error)
    {
        lifetime.Cancel();
        ready.TrySetException(error);
        lock(loads)foreach(var pending in loads.Values)pending.TrySetException(error);
        Failed?.Invoke(error.Message);
    }
    public void Send(KernelMessage message)
    {
        ObjectDisposedException.ThrowIf(disposed!=0,this);
        message.Session=session;
        lock(writes)KernelWire.Write(pipe!,JsonSerializer.Serialize(message,UnityCharacterService.JsonOptions));
    }
    public async Task<KernelMessage> LoadAsync(string definition,long generation,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        CurrentLoad=generation;sequence=0;LatestFrame=null;
        var completion=new TaskCompletionSource<KernelMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(loads)loads[generation]=completion;
        try
        {
            Send(new KernelMessage {Kind="Load",Load=generation,Path=Path.GetFullPath(definition)});
            using var registration=cancellation.Register(()=>{try{Send(new KernelMessage{Kind="Cancel",Load=generation});}catch{}completion.TrySetCanceled(cancellation);});
            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(3),cancellation);
        }
        finally{lock(loads)loads.Remove(generation);}
    }
    public void Unload()
    {
        long previous=CurrentLoad;
        CurrentLoad=0;sequence=0;LatestFrame=null;
        if(previous==0 || !IsUsable)return;
        lock(loads)if(loads.TryGetValue(previous,out var pending))pending.TrySetCanceled();
        try{Send(new KernelMessage{Kind="Cancel",Load=previous});}
        catch(IOException ex){Fault(ex);}
    }
    /// <summary>With reuseBuffer, pixels remain valid only until the next reusable read. The viewport copies them immediately.</summary>
    public UnityFrame? ReadFrame(bool reuseBuffer=false)
    {
        if(disposed!=0 || frameLock==null || view==null)return null;
        bool locked=false;
        try
        {
            try{locked=frameLock.WaitOne(0);}catch(AbandonedMutexException){locked=true;}
            if(!locked || view.ReadInt32(0)!=KernelWire.Version)return null;
            long next=view.ReadInt64(8),load=view.ReadInt64(16);
            if(next==sequence || load!=CurrentLoad)return null;
            int width=view.ReadInt32(24),height=view.ReadInt32(28);
            if(width<1 || width>KernelWire.MaxWidth || height<1 || height>KernelWire.MaxHeight)throw new IOException("Invalid frame size.");
            int size=width*height*4;
            byte[] pixels;
            if(reuseBuffer){if(frameBuffer?.Length!=size)frameBuffer=new byte[size];pixels=frameBuffer;}
            else pixels=new byte[size];
            view.ReadArray(KernelWire.Header,pixels,0,pixels.Length);sequence=next;
            return LatestFrame=new UnityFrame(width,height,pixels,next,load);
        }
        finally{if(locked)frameLock.ReleaseMutex();}
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        lifetime.Cancel();pipe?.Dispose();events?.Dispose();
        try{process?.GetAwaiter().GetResult();}catch{}
        try{reader?.GetAwaiter().GetResult();}catch{}
        lock(loads)foreach(var pending in loads.Values)pending.TrySetCanceled();
        view?.Dispose();map?.Dispose();frameLock?.Dispose();lifetime.Dispose();frameBuffer=null;LatestFrame=null;
    }
}
public sealed record UnityFrame(int Width,int Height,byte[] Pixels,long Sequence,long Load);
