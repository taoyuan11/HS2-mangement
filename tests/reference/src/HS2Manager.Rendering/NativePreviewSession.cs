using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Core.Simulation;
using HS2Manager.Preview.Contracts;
using SharpGen.Runtime;

namespace HS2Manager.Rendering;

/// <summary>Parsing runs away from the render thread; the render thread owns every GPU object and committed character.</summary>
public sealed class NativePreviewSession:IPreviewSession
{
    private readonly string gamePath;
    private readonly CancellationTokenSource lifetime=new();
    private readonly BlockingCollection<Action> commands=new(new ConcurrentQueue<Action>());
    private readonly Thread thread;
    private readonly TaskCompletionSource stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object requestLock=new();
    private readonly HashSet<Task> parsing=[];
    private CancellationTokenSource? pending;
    private long requestSerial,requestedLoad,requestedRevision,appliedRevision,currentLoad;
    private int disposed;
    private GraphicsDevice? graphics;
    private NativeFramePipeline? pipeline;
    private NativeSwapChainSurface? surface;
    private CharacterRuntime? character;
    private PreviewViewportSize size=new(1280,720);
    private PreviewView view=new(new(0,8.5f,33),new(0,8.5f,0));
    private PreviewEnvironment environment=new();
    private PreviewLoadRequest? lastRequest;
    private bool visible,paused,dirty,firstFrame,faulted;
    private float elapsed;
    private long lastTick;
    private long nextFrame;
    private long interactionUntil;
    public bool IsUsable=>Volatile.Read(ref disposed)==0&&!lifetime.IsCancellationRequested;
    public long CurrentLoad=>Interlocked.Read(ref currentLoad);
    public long AppliedRevision=>Interlocked.Read(ref appliedRevision);
    public PreviewViewportSize ViewportSize=>Volatile.Read(ref size);
    public long RenderedFrames {get;private set;}
    public bool HasFrame=>Volatile.Read(ref firstFrame);
    public event Action<PreviewSessionEvent>? Changed;
    internal Action<long>? FrameReadyDiagnostic {get;set;}
    public NativePreviewSession(string gamePath)
    {
        this.gamePath=Path.GetFullPath(gamePath);
        thread=new(Run){IsBackground=true,Name="HS2 native render"};thread.Start();
    }
    private sealed class CharacterRuntime:IDisposable
    {
        internal readonly NativeGpuAssetStore Assets;
        internal readonly NativeCharacterScene Scene;
        internal readonly NativePhysXClothSimulation Cloth;
        internal readonly NativeCharacterMotion Motion;
        internal CharacterRuntime(NativeGpuAssetStore assets,NativeCharacterScene scene)
        {
            Assets=assets;Scene=scene;Cloth=new(scene);
            try{Motion=new(scene,assets,Cloth.HasCloth?Cloth:null);Motion.UpdateMotion(0,0);scene.PoseFace(1);}
            catch{Cloth.Dispose();throw;}
        }
        public void Dispose(){Cloth.Dispose();Assets.Dispose();}
    }
    public Task<PreviewLoadResult> LoadAsync(PreviewLoadRequest request,CancellationToken cancellation=default)=>PrepareAsync(request,false,cancellation);
    public Task<PreviewLoadResult> UpdateAsync(PreviewLoadRequest request,CancellationToken cancellation=default)=>PrepareAsync(request,true,cancellation);
    private Task<PreviewLoadResult> PrepareAsync(PreviewLoadRequest request,bool update,CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(!IsUsable,this);
        if(request.Load<0||request.Revision<0||string.IsNullOrWhiteSpace(request.DefinitionPath))throw new ArgumentException("Invalid preview request.");
        CancellationTokenSource source;long serial;Task<PreviewLoadResult> task;
        lock(requestLock)
        {
            if(request.Load<requestedLoad||request.Load==requestedLoad&&request.Revision<requestedRevision)throw new OperationCanceledException("A newer preview request already exists.");
            if(update&&request.Load!=requestedLoad)throw new ArgumentException("An edit must target the current load.");
            pending?.Cancel();source=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);pending=source;
            requestedLoad=request.Load;requestedRevision=request.Revision;serial=++requestSerial;lastRequest=request;
            task=Prepare();parsing.Add(task);
        }
        _=task.ContinueWith(_=>{lock(requestLock)parsing.Remove(task);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        return task;
        async Task<PreviewLoadResult> Prepare()
        {
            CharacterRuntime? prepared=null;
            try
            {
                Publish(request.Load,request.Revision,PreviewSessionEventKind.Parsing,"正在读取人物资源…");
                prepared=await Task.Run(()=>
                {
                    source.Token.ThrowIfCancellationRequested();
                    var definition=JsonSerializer.Deserialize<CharacterDefinition>(File.ReadAllText(request.DefinitionPath),UnityCharacterService.JsonOptions)??throw new InvalidDataException("Empty character definition.");
                    if(definition.SchemaVersion!=1||definition.EditRevision!=request.Revision)throw new InvalidDataException("Character snapshot schema or edit revision differs from its request.");
                    var assets=new NativeGpuAssetStore(definition.BundlePaths,lifetime.Token,gamePath);
                    try
                    {
                        var scene=new NativeCharacterScene(definition,assets,source.Token);source.Token.ThrowIfCancellationRequested();
                        return new CharacterRuntime(assets,scene);
                    }
                    catch{assets.Dispose();throw;}
                },source.Token).ConfigureAwait(false);
                var ready=prepared;
                var result=await InvokeAsync(()=>
                {
                    source.Token.ThrowIfCancellationRequested();if(serial!=Volatile.Read(ref requestSerial))throw new OperationCanceledException(source.Token);
                    Publish(request.Load,request.Revision,PreviewSessionEventKind.Composing,"正在合成人物材质…");
                    EnsureGraphics();NativeFramePipeline? next=null;
                    try
                    {
                        next=new(graphics!,ready.Scene,ready.Assets);next.SetEnvironment(environment);source.Token.ThrowIfCancellationRequested();
                        if(serial!=Volatile.Read(ref requestSerial))throw new OperationCanceledException(source.Token);
                        pipeline?.Dispose();character?.Dispose();pipeline=next;next=null;character=ready;
                        prepared=null;currentLoad=request.Load;appliedRevision=request.Revision;faulted=false;dirty=true;firstFrame=false;
                        if(!update){elapsed=0;var bounds=Bounds(ready.Scene,false);view=view with{Position=bounds.Center+new Vector3(0,0,33),Target=bounds.Center};}
                        lastTick=Stopwatch.GetTimestamp();
                        var diagnostics=ready.Scene.Definition.Diagnostics.Concat(ready.Scene.Diagnostics).Concat(ready.Motion.Diagnostics).Concat(pipeline.SourceReferenceDiagnostics).Distinct().ToArray();
                        var result=new PreviewLoadResult(request.Load,request.Revision,Bounds(ready.Scene,false),Bounds(ready.Scene,true),diagnostics);
                        Publish(request.Load,request.Revision,PreviewSessionEventKind.Loaded,"原生人物场景已就绪。",result.Diagnostics);return result;
                    }
                    finally{next?.Dispose();}
                },source.Token).ConfigureAwait(false);
                return result;
            }
            catch(OperationCanceledException){throw;}
            catch(Exception error){if(serial==Volatile.Read(ref requestSerial))Publish(request.Load,request.Revision,PreviewSessionEventKind.Failed,error.Message);throw;}
            finally{prepared?.Dispose();lock(requestLock)if(ReferenceEquals(pending,source))pending=null;source.Dispose();}
        }
    }
    private static PreviewBox Bounds(NativeCharacterScene scene,bool face)
    {
        var min=new Vector3(float.MaxValue);var max=new Vector3(float.MinValue);bool any=false;
        // Camera framing uses the same posed renderer bounds as the previous
        // host. Tight per-vertex bounds change reset/focus distances, and a
        // hidden head still supplies the face focus target.
        foreach(var renderer in scene.Renderers.Where(r=>face?r.Node.Name=="o_head"&&r.Node.ActiveInHierarchy:r.Visible))
        {
            var bounds=renderer.Bounds;
            if(!Finite(bounds.Center)||!Finite(bounds.Extents))continue;
            min=Vector3.Min(min,bounds.Center-bounds.Extents);max=Vector3.Max(max,bounds.Center+bounds.Extents);any=true;
            if(face)break;
        }
        if(!any)return face?Bounds(scene,false):throw new InvalidDataException("The character has no visible geometry.");return new(min,max);
    }
    public Task SetViewAsync(PreviewView next,CancellationToken cancellation=default)=>InvokeAsync(()=>
    {
        if(!Finite(next.Position)||!Finite(next.Target)||Vector3.DistanceSquared(next.Position,next.Target)<1e-8f||next.FieldOfView is <=0 or >=179||next.Near<=0||next.Far<=next.Near)throw new ArgumentException("Invalid native camera.");
        bool changed=view!=next;view=next;if(changed){interactionUntil=Stopwatch.GetTimestamp()+(long)(Stopwatch.Frequency*.15);pipeline?.SetInteractive(true);dirty=true;}return true;
    },cancellation);
    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
    public Task ResizeAsync(PreviewViewportSize next,CancellationToken cancellation=default)=>InvokeAsync(()=>
    {
        if(next.Width is <1 or >8192||next.Height is <1 or >8192||!float.IsFinite(next.RasterizationScale)||next.RasterizationScale<=0)throw new ArgumentException("Invalid native viewport size.");
        if(size!=next){size=next;surface?.Resize(size);pipeline?.ResetHistory();dirty=true;}return true;
    },cancellation);
    public Task SetPausedAsync(bool value,CancellationToken cancellation=default)=>InvokeAsync(()=>{paused=value;lastTick=Stopwatch.GetTimestamp();return true;},cancellation);
    public Task SetVisibleAsync(bool value,CancellationToken cancellation=default)=>InvokeAsync(()=>{visible=value;lastTick=Stopwatch.GetTimestamp();if(value)dirty=true;return true;},cancellation);
    public Task SetEnvironmentAsync(PreviewEnvironment next,CancellationToken cancellation=default)=>InvokeAsync(()=>
    {
        if(!float.IsFinite(next.KeyLightMultiplier)||next.KeyLightMultiplier is <0 or >10||next.Background is {} color&&
            (!float.IsFinite(color.X)||!float.IsFinite(color.Y)||!float.IsFinite(color.Z)||!float.IsFinite(color.W)))throw new ArgumentException("Invalid preview environment.");
        environment=next;pipeline?.SetEnvironment(next);dirty=true;return true;
    },cancellation);
    public Task<NativeSwapChainLease> AcquireSwapChainAsync(CancellationToken cancellation=default)=>InvokeAsync(()=>
    {EnsureGraphics();if(surface==null){surface=new(graphics!,size);Publish(CurrentLoad,AppliedRevision,PreviewSessionEventKind.SurfaceChanged,"原生显示表面已创建。");}return surface.Acquire();},cancellation);
    public Task<PreviewScreenshot> CaptureAsync(CancellationToken cancellation=default)=>InvokeAsync(()=>
    {
        if(character==null||faulted)throw new InvalidOperationException("No ready native character is loaded.");
        SettleInteraction();
        if(dirty||pipeline==null)Render(false);
        var pixels=graphics!.Readback(pipeline!.LastFrame.Texture,size.Width,size.Height);for(int i=3;i<pixels.Length;i+=4)pixels[i]=255;
        return new PreviewScreenshot(CurrentLoad,AppliedRevision,size.Width,size.Height,pixels);
    },cancellation);
    public Task UnloadAsync(CancellationToken cancellation=default)
    {
        lock(requestLock){pending?.Cancel();++requestSerial;}
        return InvokeAsync(()=>{pipeline?.Dispose();pipeline=null;character?.Dispose();character=null;firstFrame=false;dirty=false;Publish(CurrentLoad,AppliedRevision,PreviewSessionEventKind.Unloaded,"人物场景已释放。");return true;},cancellation);
    }
    public Task RetryAsync(CancellationToken cancellation=default)
    {
        PreviewLoadRequest? request;lock(requestLock)request=lastRequest;
        if(request!=null&&(request.Load!=CurrentLoad||request.Revision!=AppliedRevision))return LoadAsync(request,cancellation);
        return InvokeAsync(()=>{RecoverDevice();return true;},cancellation);
    }
    private void EnsureGraphics(){graphics??=new();}
    private void RecoverDevice()
    {
        Publish(CurrentLoad,AppliedRevision,PreviewSessionEventKind.DeviceRecovering,"正在恢复原生图形设备…");
        pipeline?.Dispose();pipeline=null;surface?.Dispose();surface=null;graphics?.Dispose();graphics=null;EnsureGraphics();
        try
        {
            if(character!=null){pipeline=new(graphics!,character.Scene,character.Assets);pipeline.SetEnvironment(environment);}
            faulted=false;dirty=true;firstFrame=false;lastTick=Stopwatch.GetTimestamp();
            Publish(CurrentLoad,AppliedRevision,PreviewSessionEventKind.SurfaceChanged,"原生显示表面需要重新绑定。");
            Publish(CurrentLoad,AppliedRevision,PreviewSessionEventKind.Recovered,"原生图形设备已恢复。");
        }
        catch{faulted=true;throw;}
    }
    private void Render(bool advance)
    {
        if(character==null||pipeline==null||faulted)return;
        SettleInteraction();
        long now=Stopwatch.GetTimestamp();float seconds=(float)Math.Clamp(Stopwatch.GetElapsedTime(lastTick,now).TotalSeconds,0,.1);lastTick=now;
        if(advance&&!paused){elapsed+=seconds;character.Motion.UpdateMotion(seconds,elapsed);}
        var camera=new NativeRenderCamera(view.Position,view.Target,view.FieldOfView,view.Near,view.Far,view.Up);
        var frame=pipeline.Draw(camera,size.Width,size.Height,elapsed);surface?.Present(frame);RenderedFrames++;dirty=false;
        FrameReadyDiagnostic?.Invoke(Stopwatch.GetTimestamp());
        if(!firstFrame){firstFrame=true;Publish(CurrentLoad,AppliedRevision,PreviewSessionEventKind.FirstFrame,"原生首帧已生成。");}
    }
    private void Run()
    {
        lastTick=Stopwatch.GetTimestamp();
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                SettleInteraction();
                bool settling=interactionUntil!=0;
                bool active=visible&&character!=null&&!faulted&&(!paused||dirty||settling);
                long wake=paused&&!dirty&&settling?interactionUntil:nextFrame;
                int wait=active?Math.Clamp((int)Math.Ceiling((wake-Stopwatch.GetTimestamp())*1000d/Stopwatch.Frequency),0,17):Timeout.Infinite;
                if(commands.TryTake(out var command,wait,lifetime.Token))command();
                SettleInteraction();
                if(visible&&character!=null&&!faulted&&(!paused||dirty)&&(dirty||Stopwatch.GetTimestamp()>=nextFrame))
                {
                    try{long frameStart=Stopwatch.GetTimestamp();Render(true);nextFrame=frameStart+Stopwatch.Frequency/60;}
                    catch(SharpGenException error)when(IsDeviceLoss(error.HResult))
                    {try{RecoverDevice();}catch(Exception recovery){faulted=true;Publish(CurrentLoad,AppliedRevision,PreviewSessionEventKind.Failed,recovery.Message);}}
                    catch(Exception error){faulted=true;Publish(CurrentLoad,AppliedRevision,PreviewSessionEventKind.Failed,error.Message);}
                }
            }
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        finally
        {
            while(commands.TryTake(out var command))command();
            pipeline?.Dispose();character?.Dispose();surface?.Dispose();graphics?.Dispose();pipeline=null;character=null;surface=null;graphics=null;
            Publish(CurrentLoad,AppliedRevision,PreviewSessionEventKind.Closed,"原生预览已关闭。");stopped.TrySetResult();
        }
    }
    private void SettleInteraction()
    {
        if(interactionUntil==0||Stopwatch.GetTimestamp()<interactionUntil)return;
        interactionUntil=0;pipeline?.SetInteractive(false);dirty=true;
    }
    private static bool IsDeviceLoss(int result)=>unchecked((uint)result) is 0x887A0005 or 0x887A0006 or 0x887A0007;
    private Task<T> InvokeAsync<T>(Func<T> action,CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(!IsUsable,this);var completion=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        int state=0;
        // Once execution starts, ownership stays on the render thread until the
        // command completes. Early cancellation must not let its caller dispose
        // a scene while GPU composition is still consuming that scene's assets.
        var registration=cancellation.Register(()=>{if(Interlocked.CompareExchange(ref state,2,0)==0)completion.TrySetCanceled(cancellation);});
        try
        {
            commands.Add(()=>
            {
                if(Interlocked.CompareExchange(ref state,1,0)!=0){registration.Dispose();return;}
                try{if(lifetime.IsCancellationRequested)completion.TrySetCanceled(lifetime.Token);else if(cancellation.IsCancellationRequested)completion.TrySetCanceled(cancellation);else completion.TrySetResult(action());}
                catch(OperationCanceledException error){completion.TrySetCanceled(error.CancellationToken);}
                catch(Exception error){completion.TrySetException(error);}
                finally{Interlocked.Exchange(ref state,2);registration.Dispose();}
            },lifetime.Token);
        }
        catch{registration.Dispose();throw;}
        return completion.Task;
    }
    private void Publish(long load,long revision,PreviewSessionEventKind kind,string message,IReadOnlyList<string>? diagnostics=null)
    {
        var listeners=Changed;if(listeners==null)return;
        var value=new PreviewSessionEvent(load,revision,kind,message,firstFrame&&load==CurrentLoad&&revision==AppliedRevision,diagnostics??[]);
        foreach(Action<PreviewSessionEvent> listener in listeners.GetInvocationList())try{listener(value);}catch{/* UI observers do not own the render loop. */}
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0){await stopped.Task.ConfigureAwait(false);return;}
        Task[] tasks;lock(requestLock){pending?.Cancel();++requestSerial;tasks=parsing.ToArray();}
        lifetime.Cancel();commands.CompleteAdding();
        try{await Task.WhenAll(tasks).ConfigureAwait(false);}catch{/* Every parsing task is observed; its caller retains the original failure. */}
        await stopped.Task.ConfigureAwait(false);thread.Join();commands.Dispose();lifetime.Dispose();
    }
}
