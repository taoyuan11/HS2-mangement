using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    // Shared by the independent renderer and the test-only in-game reference collector.
    public static class CharacterDiagnostics
    {
        [Serializable] public sealed class Snapshot { public RendererState[] Renderers; public BoneState[] Bones; public string[] Assemblies; }
        [Serializable] public sealed class BoneState { public string Name; public string Parent; public Vector3 Position; public Quaternion Rotation; public Vector3 Scale; public Vector3 World; }
        [Serializable] public sealed class PoseSnapshot {public float Time;public BoneState[] Bones;public RendererState[] Faces;}
        public static void WritePose(GameObject root,string path,float time)
        {
            var value=new PoseSnapshot{Time=time,Bones=root.GetComponentsInChildren<Transform>(true).Where(t=>t.name.StartsWith("cf_J_")||t.name.StartsWith("j_")).Select(t=>new BoneState{Name=t.name,Parent=t.parent==null?"":t.parent.name,Position=t.localPosition,Rotation=t.localRotation,Scale=t.localScale,World=t.position}).ToArray(),
                Faces=root.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r=>r.name=="o_head"||r.name.Contains("eyebase")||r.name.Contains("eyelash")).Select(r=>new RendererState{Name=r.name,Active=r.enabled,BlendWeights=Enumerable.Range(0,r.sharedMesh.blendShapeCount).Select(i=>r.GetBlendShapeWeight(i)).ToArray()}).ToArray()};
            VisualFixture.Save(path,value);
        }
        [Serializable] public sealed class RendererState { public string Name; public bool Active; public MaterialState[] Materials; public string[] Bones; public float[] BlendWeights; }
        [Serializable] public sealed class MaterialState { public string Name; public string Shader; public string[] Keywords; public int Queue; public Property[] Properties; public TextureState[] Textures; }
        [Serializable] public sealed class Property { public string Name; public Color Color; public float Number; }
        [Serializable] public sealed class TextureState { public string Property; public string Name; public int Width; public int Height; }
        [Serializable] public sealed class EnvironmentSnapshot
        {
            public string RenderingPath,ColorSpace;
            public float Near,Far,Fov,Aspect,AmbientIntensity,ReflectionIntensity,ShadowDistance;
            public bool HDR,MSAA;
            public int AA,TextureQuality,PixelLights,ShadowCascades,ShadowResolution,ShadowProjection;
            public float[] AmbientProbe;
            public LightSnapshot[] Lights;
            public string[] Volumes,CameraComponents;
        }
        [Serializable] public sealed class LightSnapshot {public string Name;public Color Color;public Quaternion Rotation;public float Intensity,Bounce,ShadowStrength,Bias,NormalBias,Near;public int Type,Mask,Mode,Shadow,Resolution;}
        public static void WriteEnvironment(Camera camera,string directory)
        {
            var sh=RenderSettings.ambientProbe;var coefficients=new float[27];for(int channel=0;channel<3;channel++)for(int i=0;i<9;i++)coefficients[channel*9+i]=sh[channel,i];
            var state=new EnvironmentSnapshot{RenderingPath=camera.actualRenderingPath.ToString(),ColorSpace=QualitySettings.activeColorSpace.ToString(),Near=camera.nearClipPlane,Far=camera.farClipPlane,Fov=camera.fieldOfView,Aspect=camera.aspect,HDR=camera.allowHDR,MSAA=camera.allowMSAA,AA=QualitySettings.antiAliasing,TextureQuality=QualitySettings.masterTextureLimit,PixelLights=QualitySettings.pixelLightCount,ShadowCascades=QualitySettings.shadowCascades,ShadowDistance=QualitySettings.shadowDistance,ShadowResolution=(int)QualitySettings.shadowResolution,ShadowProjection=(int)QualitySettings.shadowProjection,AmbientIntensity=RenderSettings.ambientIntensity,ReflectionIntensity=RenderSettings.reflectionIntensity,AmbientProbe=coefficients,
                Lights=UnityEngine.Object.FindObjectsOfType<Light>().Where(l=>l.enabled).Select(l=>new LightSnapshot{Name=l.name,Color=l.color,Rotation=l.transform.rotation,Intensity=l.intensity,Bounce=l.bounceIntensity,ShadowStrength=l.shadowStrength,Bias=l.shadowBias,NormalBias=l.shadowNormalBias,Near=l.shadowNearPlane,Type=(int)l.type,Mask=l.cullingMask,Mode=(int)l.renderMode,Shadow=(int)l.shadows,Resolution=(int)l.shadowResolution}).ToArray(),
                CameraComponents=camera.GetComponents<MonoBehaviour>().Where(c=>c!=null).Select(c=>c.GetType().Name+" "+JsonUtility.ToJson(c)).ToArray(),
                Volumes=UnityEngine.Object.FindObjectsOfType<MonoBehaviour>().Where(c=>c!=null&&c.GetType().Name=="PostProcessVolume").Select(c=>{var t=c.GetType();var p=(UnityEngine.Object)t.GetProperty("profile").GetValue(c,null);var effects=(System.Collections.IEnumerable)p.GetType().GetField("settings").GetValue(p);return c.name+" "+JsonUtility.ToJson(c)+"\n"+string.Join("\n",effects.Cast<UnityEngine.Object>().Select(e=>e.GetType().Name+" "+JsonUtility.ToJson(e)).ToArray());}).ToArray()};
            using(var stream=File.Create(Path.Combine(directory,"render-settings.json")))new DataContractJsonSerializer(typeof(EnvironmentSnapshot)).WriteObject(stream,state);
        }
        public static void Write(GameObject root, string directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,"texture-metadata-note.txt"),"诊断保留材质名称、Shader、关键词、属性、队列和最终画面；部分 MOD 材质在 Unity 2018 中返回无效的原生纹理句柄，因此不枚举可选纹理槽。面部与身体合成贴图仍单独导出。此限制不改变渲染或数值门槛。");
            var state = new Snapshot
            {
                Assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).OrderBy(n => n).ToArray(),
                Bones = root.GetComponentsInChildren<Transform>(true).Select(t => new BoneState { Name=t.name,Parent=t.parent == null ? "" : t.parent.name,Position=t.localPosition,Rotation=t.localRotation,Scale=t.localScale,World=t.position }).ToArray(),
                Renderers = root.GetComponentsInChildren<Renderer>(true).Select(r => new RendererState
                {
                    Name=r.name,Active=r.enabled && r.gameObject.activeInHierarchy,
                    Bones = r is SkinnedMeshRenderer ? ((SkinnedMeshRenderer)r).bones.Select(b => b == null ? "" : b.name).ToArray() : new string[0],
                    BlendWeights = r is SkinnedMeshRenderer && ((SkinnedMeshRenderer)r).sharedMesh != null ? Enumerable.Range(0,((SkinnedMeshRenderer)r).sharedMesh.blendShapeCount).Select(i => ((SkinnedMeshRenderer)r).GetBlendShapeWeight(i)).ToArray() : new float[0],
                    Materials = r.sharedMaterials.Select(m => m == null ? new MaterialState { Name="MISSING" } : new MaterialState
                    {
                        Name=m.name,Shader=m.shader.name,Keywords=m.shaderKeywords,Queue=m.renderQueue,
                        Properties = new[] { "_Color","_Color2","_Color3","_Color4","_Color5","_Color6","_Color7","_Color9","_Color10","_Color11","_Color12","_Color13","_TopColor","_UnderColor","_Specular" }.Where(m.HasProperty).Select(p => new Property {Name=p,Color=m.GetColor(p)})
                            .Concat(new[] {"_Gloss","_Metallic","_Smoothness","_BumpScale2","_OcclusionStrength","_NipScale","_ShadowScale","_alpha_a","_alpha_b","_alpha_c","_alpha_d","_ExGloss"}.Where(m.HasProperty).Select(p => new Property {Name=p,Number=m.GetFloat(p)})).ToArray(),
                        // Some MOD materials return invalid native texture handles in Unity 2018,
                        // including in the independent player. Optional metadata must not crash capture.
                        Textures=new TextureState[0]
                    }).ToArray()
                }).ToArray()
            };
            using (var stream=File.Create(Path.Combine(directory,"state.json")))
                new DataContractJsonSerializer(typeof(Snapshot)).WriteObject(stream,state);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true).Where(r => r.name == "o_head" || r.name.StartsWith("o_body_",StringComparison.Ordinal)))
                if (renderer.sharedMaterial != null)
                    foreach (string property in new[] {"_MainTex","_DetailMainTex"})
                    {
                        var source = renderer.sharedMaterial.GetTexture(property);
                        if (source == null) continue;
                        var target=RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32);
                        var prior=RenderTexture.active;
                        var pixels=new Texture2D(source.width,source.height,TextureFormat.RGBA32,false);
                        try { Graphics.Blit(source,target); RenderTexture.active=target; pixels.ReadPixels(new Rect(0,0,source.width,source.height),0,0);pixels.Apply();File.WriteAllBytes(Path.Combine(directory,renderer.name+property+".png"),pixels.EncodeToPNG()); }
                        finally {RenderTexture.active=prior;RenderTexture.ReleaseTemporary(target);UnityEngine.Object.Destroy(pixels);}
                    }
        }
    }
}
