using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    // Optional read-only diagnostics for the isolated historical test player.
    internal static class TemporalPixelDiagnostics
    {
        internal static void WriteCamera(string path,int frame,Camera camera)
        {
            var m=camera.projectionMatrix;var n=camera.nonJitteredProjectionMatrix;
            File.AppendAllText(path,frame+","+string.Join(",",new[]{m.m02,m.m12,n.m02,n.m12}.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)).ToArray())+"\n");
        }
        internal static void WriteRoots(string path,GameObject root)
        {
            foreach(var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if(renderer.rootBone==null)continue;
                var bone=renderer.rootBone;var q=bone.rotation;var p=bone.position;
                var trs=Matrix4x4.TRS(p,q,Vector3.one);var world=bone.localToWorldMatrix;
                var values=new[]{q.x,q.y,q.z,q.w,p.x,p.y,p.z}.Concat(Enumerable.Range(0,16).Select(i=>trs[i])).Concat(Enumerable.Range(0,16).Select(i=>world[i]));
                File.AppendAllText(path,renderer.name+","+string.Join(",",values.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)).ToArray())+"\n");
                if(Environment.GetEnvironmentVariable("HS2_BLEND_TRACE")==renderer.name&&Environment.GetEnvironmentVariable("HS2_REFERENCE_NATIVE_MATRIX_TRACE")=="1")
                {
                    string output=path+".skin";Directory.CreateDirectory(output);
                    ExportReferenceRunner.WriteNativeSkinRoot(renderer,output,true);
                }
            }
        }
        internal static void WriteBounds(string path,string view,GameObject root)
        {
            foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var bounds=renderer.bounds;var c=bounds.center;var e=bounds.extents;
                var values=new[]{c.x,c.y,c.z,e.x,e.y,e.z};
                var material=renderer.sharedMaterial;
                File.AppendAllText(path+".bounds.csv",view+","+renderer.name+","+renderer.GetInstanceID()+","+(material==null?0:material.GetInstanceID())+","+
                    string.Join(",",values.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)).ToArray())+"\n");
            }
        }
        internal static void Write(string path,int frame,params RenderTexture[] textures)
        {
            string[] xy=(Environment.GetEnvironmentVariable("HS2_TAA_TRACE_PIXEL")??"623,363").Split(',');
            int x=int.Parse(xy[0]),y=int.Parse(xy[1]);
            var values=new System.Collections.Generic.List<float>();
            var active=RenderTexture.active;
            var pixel=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);
            try
            {
                foreach(var texture in textures)
                {
                    RenderTexture.active=texture;pixel.ReadPixels(new Rect(x,y,1,1),0,0,false);pixel.Apply();
                    var c=pixel.GetPixel(0,0);values.AddRange(new[]{c.r,c.g,c.b,c.a});
                }
            }
            finally{RenderTexture.active=active;UnityEngine.Object.Destroy(pixel);}
            File.AppendAllText(path,frame+","+string.Join(",",values.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)).ToArray())+"\n");
        }
    }
}
