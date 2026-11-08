using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using HS2Manager.Preview.Contracts;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace HS2Manager.UnityPreview
{
    public sealed class KernelRunner : MonoBehaviour
    {
        private string session;
        private NamedPipeClientStream pipe;
        private NamedPipeClientStream events;
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private Mutex frameLock;
        private Thread reader;
        private readonly ConcurrentQueue<KernelMessage> commands=new ConcurrentQueue<KernelMessage>();
        private readonly object writes=new object();
        private volatile bool disconnected;
        private long latestLoad,currentLoad,sequence;
        private OriginalCharacter character;
        private OriginalEnvironment environment;
        private readonly OriginalAssetStore assets=new OriginalAssetStore();
        private readonly Stack<IEnumerator> loadSteps=new Stack<IEnumerator>();
        private KernelMessage pendingLoad;
        private bool loading;
        private Camera cameraComponent;
        private Light key;
        private RenderTexture target;
        private Texture2D pixels;
        private byte[] bgra;
        private sealed class PendingFrame
        {
            public AsyncGPUReadbackRequest Request;
            public long Load;
        }
        private readonly Queue<PendingFrame> readbacks=new Queue<PendingFrame>();
        private bool asyncReadback;
        private float interactionUntil;
        private bool playing=true;
        private float elapsed;
        private void Start()
        {
            try
            {
                var args=Environment.GetCommandLineArgs();session=args[Array.IndexOf(args,"--session")+1];
                Guid parsed;if(!Guid.TryParseExact(session,"N",out parsed))throw new IOException("Invalid session.");
                Application.runInBackground=true;Application.targetFrameRate=60;QualitySettings.vSyncCount=0;
                asyncReadback=SystemInfo.supportsAsyncGPUReadback && Environment.GetEnvironmentVariable("HS2_PREVIEW_SYNC_READBACK")!="1";
                Debug.Log("KERNEL: asynchronous frame readback="+asyncReadback);
                Debug.Log("KERNEL: connecting command pipe");
                // Mono's FileStream serializes read/write on one handle. Separate one-way
                // pipes keep an idle blocking read from blocking status writes.
                pipe=new NamedPipeClientStream(".","HS2Manager.Unity."+session,PipeDirection.In);
                pipe.Connect(30000);
                events=new NamedPipeClientStream(".","HS2Manager.Unity.Events."+session,PipeDirection.Out);events.Connect(30000);
                Debug.Log("KERNEL: opening shared frame mapping");
                map=MemoryMappedFile.OpenExisting("Local\\HS2Manager.Unity.Frames."+session);
                view=map.CreateViewAccessor();frameLock=Mutex.OpenExisting("Local\\HS2Manager.Unity.Lock."+session);
                cameraComponent=new GameObject("Preview camera").AddComponent<Camera>();cameraComponent.enabled=false;
                cameraComponent.clearFlags=CameraClearFlags.SolidColor;cameraComponent.backgroundColor=new Color(.16f,.17f,.19f);
                cameraComponent.fieldOfView=35;cameraComponent.nearClipPlane=.01f;cameraComponent.farClipPlane=2000;
                cameraComponent.renderingPath=RenderingPath.Forward;
                RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;RenderSettings.ambientLight=new Color(.35f,.35f,.35f);
                RenderSettings.reflectionIntensity=0;RenderSettings.ambientIntensity=1;QualitySettings.antiAliasing=0;
                key=new GameObject("Preview light").AddComponent<Light>();key.type=LightType.Directional;key.intensity=1;key.transform.rotation=Quaternion.Euler(25,145,0);
                Resize(1280,720);
                reader=new Thread(Read){IsBackground=true,Name="Preview commands"};reader.Start();
                Send(new KernelMessage{Kind="Ready"});
                StartCoroutine(OutputFrames());
                Debug.Log("KERNEL: ready");
            }
            catch(Exception error){Debug.LogException(error);Application.Quit(1);}
        }
        private void Read()
        {
            try
            {
                while(!disconnected)
                {
                    var message=JsonUtility.FromJson<KernelMessage>(KernelWire.Read(pipe));
                    if(message.Session!=session || message.Version!=KernelWire.Version)throw new IOException("Protocol mismatch.");
                    if(message.Kind=="Load")Interlocked.Exchange(ref latestLoad,message.Load);
                    if(message.Kind=="Cancel")Interlocked.CompareExchange(ref latestLoad,-message.Load,message.Load);
                    commands.Enqueue(message);
                }
            }
            catch {disconnected=true;}
        }
        private void Update()
        {
            if(disconnected){Application.Quit(0);return;}
            KernelMessage message;
            while(commands.TryDequeue(out message))
            {
                try
                {
                    if(message.Kind=="Quit"){Application.Quit(0);return;}
                    if(message.Kind=="Load")
                    {
                        if(message.Load!=Interlocked.Read(ref latestLoad))continue;
                        pendingLoad=message;
                    }
                    else if(message.Kind=="Cancel" && message.Load==currentLoad && !loading)Clear();
                    else if(message.Kind=="Resize")Resize(message.Width,message.Height);
                    else if(message.Kind=="Play")playing=message.Playing;
                    else if(message.Load==currentLoad && message.Kind=="Camera" && message.Values.Length==7)
                    {
                        var v=message.Values;var position=new Vector3(v[0],v[1],v[2]);
                        var rotation=Quaternion.LookRotation(new Vector3(v[3],v[4],v[5])-position);float fov=Mathf.Clamp(v[6],5,100);
                        if(cameraComponent.transform.position!=position || cameraComponent.transform.rotation!=rotation || cameraComponent.fieldOfView!=fov)
                        {
                            cameraComponent.transform.SetPositionAndRotation(position,rotation);cameraComponent.fieldOfView=fov;
                            interactionUntil=Time.unscaledTime+.15f;environment?.SetInteractive(true);
                        }
                    }
                    else if(message.Kind=="Environment" && message.Values.Length==4)
                    {var v=message.Values;environment?.Customize(new Color(v[0],v[1],v[2]),Mathf.Clamp(v[3],0,4));}
                    else if(message.Kind=="Environment" && message.Text=="restore" && message.Load==currentLoad)environment?.Restore();
                    else if(message.Kind=="Screenshot" && character!=null && !loading)
                    {ReadPixels();File.WriteAllBytes(message.Path,pixels.EncodeToPNG());Send(new KernelMessage{Kind="Screenshot",Load=currentLoad,Path=message.Path});}
                }
                catch(Exception error){Clear();Send(new KernelMessage{Kind="Error",Load=message.Load,Text=error.Message});Debug.LogException(error);}
            }
            if(Time.unscaledTime>=interactionUntil)environment?.SetInteractive(false);
            if(!loading && pendingLoad!=null)
            {
                var next=pendingLoad;pendingLoad=null;
                if(next.Load==Interlocked.Read(ref latestLoad)){loading=true;StartCoroutine(DriveLoad(next));}
            }
            if(character!=null && playing && !loading){elapsed+=Time.deltaTime;character.UpdateMotion(Time.deltaTime,elapsed);}
        }
        // Drive nested iterators ourselves so every exception and cancellation is observed,
        // including exceptions raised after yielding an asynchronous native bundle request.
        private IEnumerator DriveLoad(KernelMessage message)
        {
            bool success=false;
            var previousPriority=Application.backgroundLoadingPriority;
            // The host displays a loading state; give Unity enough integration time per
            // frame instead of stretching native async loads across many idle frames.
            Application.backgroundLoadingPriority=UnityEngine.ThreadPriority.High;
            loadSteps.Push(LoadCharacter(message));
            try
            {
                while(loadSteps.Count>0)
                {
                    object next=null;Exception error=null;
                    try
                    {
                        var step=loadSteps.Peek();
                        if(!step.MoveNext()){(loadSteps.Pop() as IDisposable)?.Dispose();continue;}
                        next=step.Current;
                    }
                    catch(Exception failure){error=failure;}
                    if(error!=null)
                    {
                        if(!(error is OperationCanceledException) && message.Load==Interlocked.Read(ref latestLoad))
                        {Send(new KernelMessage{Kind="Error",Load=message.Load,Text=error.Message});Debug.LogException(error);}
                        yield break;
                    }
                    var nested=next as IEnumerator;
                    if(nested!=null){loadSteps.Push(nested);continue;}
                    yield return next;
                }
                success=message.Load==Interlocked.Read(ref latestLoad);
            }
            finally
            {
                while(loadSteps.Count>0)(loadSteps.Pop() as IDisposable)?.Dispose();
                if(!success)Clear();
                Application.backgroundLoadingPriority=previousPriority;
                loading=false;
            }
        }
        [Serializable] private sealed class LoadMetrics
        {
            public double BundleMilliseconds,CharacterMilliseconds,EnvironmentMilliseconds;
            public int RetainedBundles,LoadedBundles,BundleCount;
        }
        private IEnumerator LoadCharacter(KernelMessage message)
        {
            Clear();currentLoad=message.Load;
            // Destroy is deferred by Unity. Give old material/environment instances a frame
            // to release their references before trimming shared source bundles.
            yield return null;
            Func<bool> canceled=()=>disconnected || message.Load!=Interlocked.Read(ref latestLoad);
            Action<string> progress=text=>Send(new KernelMessage{Kind="Progress",Load=message.Load,Text=text});
            if(canceled())throw new OperationCanceledException();
            var data=CharacterData.Read(message.Path);
            if(data==null || data.SchemaVersion!=1)throw new IOException("Invalid character definition.");
            var watch=System.Diagnostics.Stopwatch.StartNew();var metrics=new LoadMetrics();
            yield return assets.Prepare(data,canceled,progress);
            metrics.BundleMilliseconds=watch.Elapsed.TotalMilliseconds;watch.Restart();
            if(canceled())throw new OperationCanceledException();
            character=new OriginalCharacter(data,assets);
            yield return character.Build(canceled,progress);
            metrics.CharacterMilliseconds=watch.Elapsed.TotalMilliseconds;watch.Restart();
            if(canceled())throw new OperationCanceledException();
            character.UpdateMotion(0,0);
            key.enabled=false;environment=character.CreateEnvironment(cameraComponent);
            if(canceled())throw new OperationCanceledException();
            var bounds=character.Bounds();
            if(bounds.size==Vector3.zero)throw new IOException("人物没有可渲染模型。");
            cameraComponent.transform.position=bounds.center+new Vector3(0,0,33);cameraComponent.transform.LookAt(bounds.center);
            var head=character.Root.GetComponentsInChildren<Renderer>().FirstOrDefault(r=>r.name=="o_head");
            var face=head==null?bounds:head.bounds;
            metrics.EnvironmentMilliseconds=watch.Elapsed.TotalMilliseconds;
            metrics.LoadedBundles=assets.LoadedBundles;metrics.RetainedBundles=assets.RetainedBundles;metrics.BundleCount=assets.BundleCount;
            Debug.Log("KERNEL: load "+message.Load+" "+JsonUtility.ToJson(metrics));
            Send(new KernelMessage{Kind="Metrics",Load=message.Load,Text=JsonUtility.ToJson(metrics)});
            cameraComponent.enabled=true;
            Send(new KernelMessage{Kind="Loaded",Load=message.Load,Text=string.Join("\n",character.Diagnostics.ToArray()),Values=new[]{bounds.center.x,bounds.center.y,bounds.center.z,bounds.size.x,bounds.size.y,bounds.size.z,face.center.x,face.center.y,face.center.z,face.size.x,face.size.y,face.size.z}});
        }
        private IEnumerator OutputFrames()
        {
            var endOfFrame=new WaitForEndOfFrame();
            while(!disconnected)
            {
                yield return endOfFrame;
                try{CompleteReadbacks();}catch(Exception error){Clear();Send(new KernelMessage{Kind="Error",Load=currentLoad,Text=error.Message});}
                if(character==null || loading || currentLoad!=Interlocked.Read(ref latestLoad))continue;
                try
                {
                    if(asyncReadback)
                    {
                        // D3D11 readback spans three player frames. Keep that pipeline fed,
                        // but skip captures rather than building an unbounded queue when the GPU falls behind.
                        if(readbacks.Count<3)readbacks.Enqueue(new PendingFrame{Request=AsyncGPUReadback.Request(target,0,TextureFormat.RGBA32),Load=currentLoad});
                    }
                    else {ReadPixels();Publish(pixels.GetRawTextureData<uint>(),currentLoad);}
                }
                catch(Exception error){Clear();Send(new KernelMessage{Kind="Error",Load=currentLoad,Text=error.Message});}
            }
        }
        private void CompleteReadbacks()
        {
            PendingFrame newest=null;
            while(readbacks.Count>0 && readbacks.Peek().Request.done)
            {
                var pending=readbacks.Dequeue();
                if(pending.Request.hasError)
                {
                    asyncReadback=false;Debug.LogWarning("KERNEL: async readback failed; using synchronous capture.");
                    // Do not let an older queued capture overwrite a newer synchronous frame.
                    DrainReadbacks();return;
                }
                if(character!=null && !loading && pending.Load==currentLoad && currentLoad==Interlocked.Read(ref latestLoad))newest=pending;
            }
            if(newest!=null)Publish(newest.Request.GetData<uint>(),newest.Load);
        }
        private void DrainReadbacks()
        {
            // A render texture cannot be released while a readback still references it.
            while(readbacks.Count>0)readbacks.Dequeue().Request.WaitForCompletion();
        }
        private void ReadPixels()
        {
            if(pixels==null)pixels=new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);
            var previous=RenderTexture.active;
            // ReadPixels already updates CPU storage; Apply would upload the entire frame back to the GPU.
            try{RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0,false);}
            finally{RenderTexture.active=previous;}
        }
        private unsafe void Publish(NativeArray<uint> rgba,long load)
        {
            int width=target.width,height=target.height;
            if(rgba.Length!=width*height)throw new IOException("Invalid GPU frame size.");
            var source=(uint*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(rgba);
            fixed(byte* bytes=bgra)
                for(int y=0;y<height;y++)
                {
                    uint* input=source+y*width,output=(uint*)bytes+(height-1-y)*width;
                    for(int x=0;x<width;x++){uint color=input[x];output[x]=0xff000000u|(color&0x0000ff00u)|((color&0xffu)<<16)|((color>>16)&0xffu);}
                }
            bool locked=false;
            try
            {
                try{locked=frameLock.WaitOne(0);}catch(AbandonedMutexException){locked=true;}
                if(!locked)return;
                view.WriteArray(KernelWire.Header,bgra,0,bgra.Length);view.Write(0,KernelWire.Version);view.Write(16,load);view.Write(24,width);view.Write(28,height);view.Write(8,++sequence);
            }
            finally{if(locked)frameLock.ReleaseMutex();}
        }
        private void Resize(int width,int height)
        {
            width=Mathf.Clamp(width,64,KernelWire.MaxWidth);height=Mathf.Clamp(height,64,KernelWire.MaxHeight);
            if(target!=null && target.width==width && target.height==height)return;
            DrainReadbacks();
            if(target!=null){cameraComponent.targetTexture=null;target.Release();Destroy(target);if(pixels!=null)Destroy(pixels);pixels=null;}
            target=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32);target.Create();bgra=new byte[width*height*4];
            cameraComponent.targetTexture=target;
            cameraComponent.aspect=(float)width/height;
            environment?.ResetHistory();
        }
        private void Send(KernelMessage message)
        {
            message.Session=session;lock(writes)KernelWire.Write(events,JsonUtility.ToJson(message));
        }
        private void Clear(){if(cameraComponent!=null)cameraComponent.enabled=false;environment?.Dispose();environment=null;if(character!=null)character.Dispose();character=null;elapsed=0;}
        private void OnDestroy()
        {
            disconnected=true;pipe?.Dispose();events?.Dispose();if(reader!=null && reader.IsAlive)reader.Join(1000);
            StopAllCoroutines();while(loadSteps.Count>0)(loadSteps.Pop() as IDisposable)?.Dispose();
            DrainReadbacks();Clear();assets.Dispose();if(target!=null){target.Release();Destroy(target);}if(pixels!=null)Destroy(pixels);
            view?.Dispose();map?.Dispose();frameLock?.Dispose();
        }
    }
}
