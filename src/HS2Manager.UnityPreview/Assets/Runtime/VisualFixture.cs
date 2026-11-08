using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    // Test capture coordinates are generated once by the reference and reused verbatim.
    public static class VisualFixture
    {
        public const int Width=1280,Height=720,WarmFrames=48;
        [Serializable] public sealed class View {public string Name;public Vector3 Position,Target;public float Fov;}
        [Serializable] public sealed class Settings {public View[] Views;public int Seed=1979;public int Rate=30;public int InitialFrames=180;}
        [Serializable] public sealed class FaceRegion {public float Left,Bottom,Right,Top;}
        public static Settings Create(GameObject root)
        {
            var renderers=root.GetComponentsInChildren<Renderer>().Where(r=>r.enabled&&!r.name.StartsWith("O_hit_",StringComparison.OrdinalIgnoreCase)).ToArray();
            var bounds=new Bounds();bool started=false;
            foreach(var r in renderers)
            {
                var skin=r as SkinnedMeshRenderer;Mesh mesh=null;bool owns=skin!=null;
                if(owns){mesh=new Mesh();skin.BakeMesh(mesh);}else{var filter=r.GetComponent<MeshFilter>();if(filter!=null)mesh=filter.sharedMesh;}
                if(mesh!=null)foreach(var vertex in mesh.vertices){var point=r.transform.TransformPoint(vertex);if(!started){bounds=new Bounds(point,Vector3.zero);started=true;}else bounds.Encapsulate(point);}
                if(owns)UnityEngine.Object.Destroy(mesh);
            }
            var center=bounds.center;float distance=Mathf.Max(bounds.size.y,Mathf.Max(bounds.size.x,bounds.size.z)*Height/Width)*1.12f/(2*Mathf.Tan(17.5f*Mathf.Deg2Rad));
            var head=root.GetComponentsInChildren<Transform>(true).First(t=>t.name=="cf_J_Head");
            var target=head.position+new Vector3(0,.65f,.05f);
            return new Settings{Views=new[]{
                new View{Name="front",Position=center+new Vector3(0,0,distance),Target=center,Fov=35},
                new View{Name="side",Position=center+new Vector3(distance,0,0),Target=center,Fov=35},
                new View{Name="back",Position=center+new Vector3(0,0,-distance),Target=center,Fov=35},
                new View{Name="face",Position=target+new Vector3(0,0,5.94f),Target=target,Fov=35}}};
        }
        public static void Set(Camera camera,View view){camera.transform.position=view.Position;camera.transform.LookAt(view.Target);camera.fieldOfView=view.Fov;}
        public static void Save<T>(string path,T value){using(var stream=File.Create(path))new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value);}
        public static Settings Load(string path){using(var stream=File.OpenRead(path))return (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);}
        public static void Pixels(RenderTexture target,string path)
        {
            var prior=RenderTexture.active;var image=new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);
            try{RenderTexture.active=target;image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());}
            finally{RenderTexture.active=prior;UnityEngine.Object.Destroy(image);}
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
