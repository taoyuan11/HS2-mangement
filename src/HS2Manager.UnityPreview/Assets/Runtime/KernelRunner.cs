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
        private bool playing=true;
        private float elapsed;
        private void Start()
        {
            try
            {
                var args=Environment.GetCommandLineArgs();session=args[Array.IndexOf(args,"--session")+1];
                Guid parsed;if(!Guid.TryParseExact(session,"N",out parsed))throw new IOException("Invalid session.");
                Application.runInBackground=true;Application.targetFrameRate=30;QualitySettings.vSyncCount=0;
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
                        var v=message.Values;cameraComponent.transform.position=new Vector3(v[0],v[1],v[2]);cameraComponent.transform.LookAt(new Vector3(v[3],v[4],v[5]));cameraComponent.fieldOfView=Mathf.Clamp(v[6],5,100);
                    }
                    else if(message.Kind=="Environment" && message.Values.Length==4)
                    {var v=message.Values;environment?.Customize(new Color(v[0],v[1],v[2]),Mathf.Clamp(v[3],0,4));}
                    else if(message.Kind=="Environment" && message.Text=="restore" && message.Load==currentLoad)environment?.Restore();
                    else if(message.Kind=="Screenshot" && character!=null && !loading)
                    {RenderFrame();File.WriteAllBytes(message.Path,pixels.EncodeToPNG());Send(new KernelMessage{Kind="Screenshot",Load=currentLoad,Path=message.Path});}
                }
                catch(Exception error){Clear();Send(new KernelMessage{Kind="Error",Load=message.Load,Text=error.Message});Debug.LogException(error);}
            }
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
            while(!disconnected)
            {
                yield return new WaitForEndOfFrame();
                if(character==null || loading || currentLoad!=Interlocked.Read(ref latestLoad))continue;
                try{RenderFrame();}catch(Exception error){Clear();Send(new KernelMessage{Kind="Error",Load=currentLoad,Text=error.Message});}
            }
        }
        private void RenderFrame()
        {
            var previous=RenderTexture.active;
            try{RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0,false);pixels.Apply(false);}
            finally{RenderTexture.active=previous;}
            var rgba=pixels.GetRawTextureData();int stride=target.width*4;
            for(int y=0;y<target.height;y++)for(int x=0;x<stride;x+=4)
            {int src=y*stride+x,dst=(target.height-1-y)*stride+x;bgra[dst]=rgba[src+2];bgra[dst+1]=rgba[src+1];bgra[dst+2]=rgba[src];bgra[dst+3]=255;}
            bool locked=false;
            try
            {
                try{locked=frameLock.WaitOne(0);}catch(AbandonedMutexException){locked=true;}
                if(!locked)return;
                view.WriteArray(KernelWire.Header,bgra,0,bgra.Length);view.Write(0,KernelWire.Version);view.Write(16,currentLoad);view.Write(24,target.width);view.Write(28,target.height);view.Write(8,++sequence);
            }
            finally{if(locked)frameLock.ReleaseMutex();}
        }
        private void Resize(int width,int height)
        {
            width=Mathf.Clamp(width,64,KernelWire.MaxWidth);height=Mathf.Clamp(height,64,KernelWire.MaxHeight);
            if(target!=null && target.width==width && target.height==height)return;
            if(target!=null){cameraComponent.targetTexture=null;target.Release();Destroy(target);Destroy(pixels);}
            target=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32);target.Create();pixels=new Texture2D(width,height,TextureFormat.RGBA32,false);bgra=new byte[width*height*4];
            cameraComponent.targetTexture=target;
            cameraComponent.aspect=(float)width/height;
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
            Clear();assets.Dispose();if(target!=null){target.Release();Destroy(target);}if(pixels!=null)Destroy(pixels);
            view?.Dispose();map?.Dispose();frameLock?.Dispose();
        }
    }
}
