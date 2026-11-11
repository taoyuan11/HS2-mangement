using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    // Test capture coordinates are generated once by the reference and reused verbatim.
    public static class VisualFixture
    {
        public const int Width=1280,Height=720,WarmFrames=48;
        public static int GraphicsWarmFrame
        {
            get
            {
                int frame;string requested=Environment.GetEnvironmentVariable("HS2_CAPTURE_WARM_FRAME");
                if(string.IsNullOrEmpty(requested))return WarmFrames-3;
                if(!int.TryParse(requested,out frame)||frame<0||frame>=WarmFrames)throw new ArgumentOutOfRangeException("HS2_CAPTURE_WARM_FRAME");
                return frame;
            }
        }
        // Optional native GPU diagnostics. The debugger must already have been
        // injected by the test launcher; normal previews never load it.
        public static IDisposable GraphicsCapture(string view)
        {
            if(Environment.GetEnvironmentVariable("HS2_CAPTURE_GRAPHICS")!=view)return null;
            var module=GetModuleHandle("renderdoc.dll");if(module==IntPtr.Zero)return null;
            var get=(GetGraphicsApi)Marshal.GetDelegateForFunctionPointer(GetProcAddress(module,"RENDERDOC_GetAPI"),typeof(GetGraphicsApi));
            IntPtr api;if(get(10400,out api)!=1)throw new IOException("RenderDoc capture API is unavailable.");
            var trigger=(TriggerGraphicsCapture)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(api,15*IntPtr.Size),typeof(TriggerGraphicsCapture));
            // Unity submits on a separate rendering thread. Queue a capture at
            // Present so native commands are not interrupted inside a chunk.
            trigger();return null;
        }
        [DllImport("kernel32",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32",CharSet=CharSet.Ansi)]private static extern IntPtr GetProcAddress(IntPtr module,string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int GetGraphicsApi(int version,out IntPtr api);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void TriggerGraphicsCapture();
        [Serializable] public sealed class View {public string Name;public Vector3 Position,Target;public float Fov;}
        [Serializable] public sealed class Settings {public View[] Views;public int Seed=1979;public int Rate=30;public int InitialFrames=180;public int ShaderClockVersion=1;public int TemporalPhaseVersion=2;public int PoseSamplingVersion=2;public int FaceTickVersion=1;[System.Runtime.Serialization.OptionalField] public int PhysicsResetVersion=3;[System.Runtime.Serialization.OptionalField] public int GizmoVisibilityVersion=1;[System.Runtime.Serialization.OptionalField] public int ClothResetVersion=1;[System.Runtime.Serialization.OptionalField] public int PartAnimationClockVersion;[System.Runtime.Serialization.OptionalField] public int BlinkClockVersion=1;public int ScreenWidth,ScreenHeight;}
        [Serializable] public sealed class FaceRegion {public float Left,Bottom,Right,Top;}
        public static Settings Create(GameObject root,Renderer[] additional=null)
        {
            var renderers=root.GetComponentsInChildren<Renderer>().Concat(additional??new Renderer[0]).Distinct().Where(r=>r!=null&&r.enabled&&r.gameObject.activeInHierarchy&&!r.name.StartsWith("O_hit_",StringComparison.OrdinalIgnoreCase)).ToArray();
            var bounds=new Bounds();bool started=false;
            foreach(var r in renderers)
            {
                var skin=r as SkinnedMeshRenderer;Mesh mesh=null;bool owns=skin!=null;
                if(owns){mesh=new Mesh();skin.BakeMesh(mesh);}else{var filter=r.GetComponent<MeshFilter>();if(filter!=null)mesh=filter.sharedMesh;}
                if(mesh!=null)
                {
                    var vertices=mesh.vertices;
                    foreach(var index in mesh.triangles.Distinct())
                    {
                        var point=r.transform.TransformPoint(vertices[index]);
                        if(float.IsNaN(point.x)||float.IsNaN(point.y)||float.IsNaN(point.z)||float.IsInfinity(point.x)||float.IsInfinity(point.y)||float.IsInfinity(point.z))continue;
                        if(!started){bounds=new Bounds(point,Vector3.zero);started=true;}else bounds.Encapsulate(point);
                    }
                }
                if(owns)UnityEngine.Object.Destroy(mesh);
            }
            // A few imported meshes contain unused vertices hundreds of units from
            // the visible character. Keep the fixture focused on the body when
            // those vertices would put the entire character outside the viewport.
            var body=root.GetComponentsInChildren<SkinnedMeshRenderer>().FirstOrDefault(r=>r.enabled&&(r.name=="o_body_cf"||r.name=="o_body_cm"));
            if(body!=null)
            {
                var mesh=new Mesh();
                try
                {
                    body.BakeMesh(mesh);var bodyBounds=new Bounds();bool bodyStarted=false;
                    foreach(var vertex in mesh.vertices){var point=body.transform.TransformPoint(vertex);if(!bodyStarted){bodyBounds=new Bounds(point,Vector3.zero);bodyStarted=true;}else bodyBounds.Encapsulate(point);}
                    float bodyHeight=bodyBounds.size.y;
                    if(bodyHeight>.1f&&Mathf.Max(bounds.size.x,Mathf.Max(bounds.size.y,bounds.size.z))>bodyHeight*4)
                    {
                        bounds=bodyBounds;
                        foreach(var renderer in renderers)
                        {
                            var visible=renderer.bounds;
                            if(visible.size.magnitude<bodyHeight*4&&(visible.center-bodyBounds.center).magnitude<bodyHeight*2)bounds.Encapsulate(visible);
                        }
                    }
                }
                finally{UnityEngine.Object.Destroy(mesh);}
            }
            var center=bounds.center;float distance=Mathf.Max(bounds.size.y,Mathf.Max(bounds.size.x,bounds.size.z)*Height/Width)*1.12f/(2*Mathf.Tan(17.5f*Mathf.Deg2Rad));
            var head=root.GetComponentsInChildren<Transform>(true).First(t=>t.name=="cf_J_Head");
            var target=head.position+new Vector3(0,.65f,.05f);
            return new Settings{ScreenWidth=Screen.width,ScreenHeight=Screen.height,Views=new[]{
                new View{Name="front",Position=center+new Vector3(0,0,distance),Target=center,Fov=35},
                new View{Name="side",Position=center+new Vector3(distance,0,0),Target=center,Fov=35},
                new View{Name="back",Position=center+new Vector3(0,0,-distance),Target=center,Fov=35},
                new View{Name="face",Position=target+new Vector3(0,0,5.94f),Target=target,Fov=35}}};
        }
        public static void Set(Camera camera,View view){camera.transform.position=view.Position;camera.transform.LookAt(view.Target);camera.fieldOfView=view.Fov;}
        public static void ResetCloth(GameObject root)
        {
            foreach(var component in root.GetComponentsInChildren<Component>(true).Where(c=>c!=null&&c.GetType().FullName=="UnityEngine.Cloth"))
            {
                var type=component.GetType();var enabled=type.GetProperty("enabled");
                if(!(bool)enabled.GetValue(component,null))continue;
                enabled.SetValue(component,false,null);enabled.SetValue(component,true,null);
                type.GetMethod("ClearTransformMotion").Invoke(component,null);
            }
        }
        // Dithering uses its own System.Random and texture counter, independent
        // from Unity's seeded blink RNG. Seed it equally in both test captures.
        public static void ResetDithering(Camera camera)
        {
            var layer=camera.GetComponents<MonoBehaviour>().FirstOrDefault(c=>c!=null&&c.GetType().Name=="PostProcessLayer");
            if(layer==null)return;
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var dithering=layer.GetType().GetField("dithering",flags).GetValue(layer);
            if(dithering==null)return;
            dithering.GetType().GetField("m_Random",flags).SetValue(dithering,new System.Random(1979));
            dithering.GetType().GetField("m_NoiseTextureIndex",flags).SetValue(dithering,0);
        }
        public static void Save<T>(string path,T value){using(var stream=File.Create(path))new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value);}
        public static Settings Load(string path){using(var stream=File.OpenRead(path))return (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);}
        public static void Pixels(RenderTexture target,string path)
        {
            var prior=RenderTexture.active;var image=new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);
            try{RenderTexture.active=target;image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());}
            finally{RenderTexture.active=prior;UnityEngine.Object.Destroy(image);}
            if(Path.GetFileName(path)=="frame-195.png")
            {
                var motion=Shader.GetGlobalTexture("_CameraMotionVectorsTexture");
                if(motion!=null)
                {
                    var buffer=RenderTexture.GetTemporary(motion.width,motion.height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
                    var sample=new Texture2D(motion.width,motion.height,TextureFormat.RGBAFloat,false,true);
                    try
                    {
                        Graphics.Blit(motion,buffer);RenderTexture.active=buffer;
                        sample.ReadPixels(new Rect(0,0,motion.width,motion.height),0,0);sample.Apply();
                        File.WriteAllBytes(Path.ChangeExtension(path,"motion-f32"),sample.GetRawTextureData());
                        File.WriteAllText(Path.ChangeExtension(path,"motion-size"),motion.width+" "+motion.height);
                    }
                    finally{RenderTexture.active=prior;RenderTexture.ReleaseTemporary(buffer);UnityEngine.Object.Destroy(sample);}
                }
            }
        }
        public static void FaceBounds(Camera camera,GameObject root,string path)
        {
            var head=root.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r=>r.name=="o_head");
            if(head==null)throw new InvalidDataException("No face geometry for the acceptance region.");
            var b=head.bounds;var points=Enumerable.Range(0,8).Select(i=>camera.WorldToViewportPoint(b.center+Vector3.Scale(b.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1)))).ToArray();
            Save(path,new FaceRegion{Left=points.Min(v=>v.x),Right=points.Max(v=>v.x),Bottom=points.Min(v=>v.y),Top=points.Max(v=>v.y)});
        }
    }
}
