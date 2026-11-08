using System;
using System.Collections;
using System.Collections.Concurrent;
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
                        Clear();currentLoad=message.Load;
                        Debug.Log("KERNEL: load "+currentLoad);
                        var data=CharacterData.Read(message.Path);
                        if(data==null || data.SchemaVersion!=1)throw new IOException("Invalid character definition.");
                        character=new OriginalCharacter(data);
                        character.UpdateMotion(0,0);
                        key.enabled=false;environment=character.CreateEnvironment(cameraComponent);
                        cameraComponent.enabled=true;
                        Debug.Log("KERNEL: loaded "+currentLoad);
                        if(currentLoad!=Interlocked.Read(ref latestLoad)){Clear();continue;}
                        var bounds=character.Bounds();
                        if(bounds.size==Vector3.zero)throw new IOException("人物没有可渲染模型。");
                        cameraComponent.transform.position=bounds.center+new Vector3(0,0,33);cameraComponent.transform.LookAt(bounds.center);
                        var head=character.Root.GetComponentsInChildren<Renderer>().FirstOrDefault(r=>r.name=="o_head");
                        var face=head==null?bounds:head.bounds;
                        Send(new KernelMessage{Kind="Loaded",Load=currentLoad,Text=string.Join("\n",character.Diagnostics.ToArray()),Values=new[]{bounds.center.x,bounds.center.y,bounds.center.z,bounds.size.x,bounds.size.y,bounds.size.z,face.center.x,face.center.y,face.center.z,face.size.x,face.size.y,face.size.z}});
                    }
                    else if(message.Kind=="Cancel" && message.Load==currentLoad)Clear();
                    else if(message.Kind=="Resize")Resize(message.Width,message.Height);
                    else if(message.Kind=="Play")playing=message.Playing;
                    else if(message.Load==currentLoad && message.Kind=="Camera" && message.Values.Length==7)
                    {
                        var v=message.Values;cameraComponent.transform.position=new Vector3(v[0],v[1],v[2]);cameraComponent.transform.LookAt(new Vector3(v[3],v[4],v[5]));cameraComponent.fieldOfView=Mathf.Clamp(v[6],5,100);
                    }
                    else if(message.Kind=="Environment" && message.Values.Length==4)
                    {var v=message.Values;environment?.Customize(new Color(v[0],v[1],v[2]),Mathf.Clamp(v[3],0,4));}
                    else if(message.Kind=="Environment" && message.Text=="restore" && message.Load==currentLoad)environment?.Restore();
                    else if(message.Kind=="Screenshot" && character!=null)
                    {RenderFrame();File.WriteAllBytes(message.Path,pixels.EncodeToPNG());Send(new KernelMessage{Kind="Screenshot",Load=currentLoad,Path=message.Path});}
                }
                catch(Exception error){Clear();Send(new KernelMessage{Kind="Error",Load=message.Load,Text=error.Message});Debug.LogException(error);}
            }
            if(character!=null && playing){elapsed+=Time.deltaTime;character.UpdateMotion(Time.deltaTime,elapsed);}
        }
        private IEnumerator OutputFrames()
        {
            while(!disconnected)
            {
                yield return new WaitForEndOfFrame();
                if(character==null || currentLoad!=Interlocked.Read(ref latestLoad))continue;
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
            Clear();if(target!=null){target.Release();Destroy(target);}if(pixels!=null)Destroy(pixels);
            view?.Dispose();map?.Dispose();frameLock?.Dispose();
        }
    }
}
