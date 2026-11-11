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
        [Serializable] public sealed class BoneState { public string Name,Path; public int Id;public string Parent; public Vector3 Position; public Quaternion Rotation; public Vector3 Scale; public Vector3 World; public float[] Matrix; }
        private static float[] TraceMatrix(Transform transform)
        {
            if(Environment.GetEnvironmentVariable("HS2_MATRIX_TRACE")!="1")return null;
            var m=transform.localToWorldMatrix;var values=new float[16];for(int i=0;i<16;i++)values[i]=m[i];return values;
        }
        [Serializable] public sealed class PoseSnapshot {public float Time;public BoneState[] Bones;public RendererState[] Faces;}
        public static void WritePose(GameObject root,string path,float time)
        {
            var value=new PoseSnapshot{Time=time,Bones=root.GetComponentsInChildren<Transform>(true).Select(t=>new BoneState{Id=t.GetInstanceID(),Name=t.name,Path=TransformPath(t,root.transform),Parent=t.parent==null?"":t.parent.name,Position=t.localPosition,Rotation=t.localRotation,Scale=t.localScale,World=t.position,Matrix=TraceMatrix(t)}).ToArray(),
                Faces=root.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r=>r.name=="o_head"||r.name.Contains("eyebase")||r.name.Contains("eyelash")).Select(r=>new RendererState{Name=r.name,Active=r.enabled,BlendWeights=Enumerable.Range(0,r.sharedMesh.blendShapeCount).Select(i=>r.GetBlendShapeWeight(i)).ToArray()}).ToArray()};
            VisualFixture.Save(path,value);
        }
        [Serializable] public sealed class RendererState { public string Name,Path,RootBone; public int Id;public uint[] SubmeshIndices;public int[] BoneIds; public Vector3 World,Scale; public bool Active,Visible,SkinnedMotion,UpdateOffscreen,Cloth,ReceiveShadows,Occlusion;public int MotionMode,SortingLayer,SortingOrder,SkinQuality,Layer,Casting,LightProbe,ReflectionProbe;public Bounds Bounds,LocalBounds; public MaterialState[] Materials; public string[] Bones,BonePaths; public float[] BlendWeights; }
        [Serializable] public sealed class MaterialState { public string Name; public int Id;public string Shader; public bool MotionPass,Instancing,DoubleSidedGI;public string[] Keywords,Passes,Tags; public int Queue; public Property[] Properties; public TextureState[] Textures; }
        [Serializable] public sealed class Property { public string Name; public Color Color; public float Number; }
        [Serializable] public sealed class TextureState { public string Property; public string Name; public int Width; public int Height; }
        [Serializable] public sealed class ExtraProperties {public string[] Float=new string[0],Color=new string[0],Texture=new string[0];}
        [Serializable] public sealed class ClothState {public string Path;public string[] Properties,Colliders;public float[][] Coefficients;public float FixedDelta,MaximumDelta;public Vector3 Gravity;}
        [Serializable] public sealed class EnvironmentSnapshot
        {
            public string RenderingPath,ColorSpace;
            public float Near,Far,Fov,Aspect,AmbientIntensity,ReflectionIntensity,ShadowDistance;
            public bool HDR,MSAA;
            public int AA,TextureQuality,PixelLights,ShadowCascades,ShadowResolution,ShadowProjection;
            public int TransparencySort,GlobalTransparencySort;
            public Vector3 TransparencyAxis,GlobalTransparencyAxis;
            public float[] AmbientProbe;
            public LightSnapshot[] Lights;
            public string[] Volumes,CameraComponents;
        }
        [Serializable] public sealed class LightSnapshot {public string Name;public Color Color;public Quaternion Rotation;public float Intensity,Bounce,ShadowStrength,Bias,NormalBias,Near;public int Type,Mask,Mode,Shadow,Resolution;}
        public static void WriteEnvironment(Camera camera,string directory)
        {
            var sh=RenderSettings.ambientProbe;var coefficients=new float[27];for(int channel=0;channel<3;channel++)for(int i=0;i<9;i++)coefficients[channel*9+i]=sh[channel,i];
            var state=new EnvironmentSnapshot{RenderingPath=camera.actualRenderingPath.ToString(),ColorSpace=QualitySettings.activeColorSpace.ToString(),Near=camera.nearClipPlane,Far=camera.farClipPlane,Fov=camera.fieldOfView,Aspect=camera.aspect,HDR=camera.allowHDR,MSAA=camera.allowMSAA,AA=QualitySettings.antiAliasing,TextureQuality=QualitySettings.masterTextureLimit,PixelLights=QualitySettings.pixelLightCount,ShadowCascades=QualitySettings.shadowCascades,ShadowDistance=QualitySettings.shadowDistance,ShadowResolution=(int)QualitySettings.shadowResolution,ShadowProjection=(int)QualitySettings.shadowProjection,AmbientIntensity=RenderSettings.ambientIntensity,ReflectionIntensity=RenderSettings.reflectionIntensity,AmbientProbe=coefficients,
                TransparencySort=(int)camera.transparencySortMode,TransparencyAxis=camera.transparencySortAxis,GlobalTransparencySort=(int)UnityEngine.Rendering.GraphicsSettings.transparencySortMode,GlobalTransparencyAxis=UnityEngine.Rendering.GraphicsSettings.transparencySortAxis,
                Lights=UnityEngine.Object.FindObjectsOfType<Light>().Where(l=>l.enabled).Select(l=>new LightSnapshot{Name=l.name,Color=l.color,Rotation=l.transform.rotation,Intensity=l.intensity,Bounce=l.bounceIntensity,ShadowStrength=l.shadowStrength,Bias=l.shadowBias,NormalBias=l.shadowNormalBias,Near=l.shadowNearPlane,Type=(int)l.type,Mask=l.cullingMask,Mode=(int)l.renderMode,Shadow=(int)l.shadows,Resolution=(int)l.shadowResolution}).ToArray(),
                CameraComponents=camera.GetComponents<MonoBehaviour>().Where(c=>c!=null).Select(c=>c.GetType().Name+" "+JsonUtility.ToJson(c)).ToArray(),
                Volumes=UnityEngine.Object.FindObjectsOfType<MonoBehaviour>().Where(c=>c!=null&&c.GetType().Name=="PostProcessVolume").Select(c=>{var t=c.GetType();var p=(UnityEngine.Object)t.GetProperty("profile").GetValue(c,null);var effects=(System.Collections.IEnumerable)p.GetType().GetField("settings").GetValue(p);return c.name+" "+JsonUtility.ToJson(c)+"\n"+string.Join("\n",effects.Cast<UnityEngine.Object>().Select(e=>e.GetType().Name+" "+JsonUtility.ToJson(e)).ToArray());}).ToArray()};
            using(var stream=File.Create(Path.Combine(directory,"render-settings.json")))new DataContractJsonSerializer(typeof(EnvironmentSnapshot)).WriteObject(stream,state);
        }
        public static void Write(GameObject root, string directory)
        {
            var extraProperties=new ExtraProperties();
            string propertyFile=Environment.GetEnvironmentVariable("HS2_MATERIAL_PROPERTIES");
            if(!string.IsNullOrEmpty(propertyFile)&&File.Exists(propertyFile))
                extraProperties=JsonUtility.FromJson<ExtraProperties>(File.ReadAllText(propertyFile));
            Func<string,string[]> extra=kind=>kind=="Float"?extraProperties.Float:kind=="Color"?extraProperties.Color:extraProperties.Texture;
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory,"native-animations.txt"),root.GetComponentsInChildren<Animator>(true).Select(a=>
                TransformPath(a.transform,root.transform)+" Animator enabled="+a.enabled+" speed="+a.speed+" state="+(a.isInitialized?a.GetCurrentAnimatorStateInfo(0).fullPathHash+" "+a.GetCurrentAnimatorStateInfo(0).normalizedTime:"uninitialized")+" clips="+(a.runtimeAnimatorController==null?"":string.Join(";",a.runtimeAnimatorController.animationClips.Select(c=>c.name+":"+c.length).ToArray())))
                .Concat(root.GetComponentsInChildren<Animation>(true).Select(a=>TransformPath(a.transform,root.transform)+" Animation enabled="+a.enabled+" clip="+(a.clip==null?"":a.clip.name)+" states="+string.Join(";",a.Cast<AnimationState>().Select(s=>s.name+":"+s.time+":"+s.speed+":"+s.enabled).ToArray()))));
            File.WriteAllText(Path.Combine(directory,"texture-metadata-note.txt"),"诊断保留材质名称、Shader、关键词、属性、队列和最终画面；部分 MOD 材质在 Unity 2018 中返回无效的原生纹理句柄，因此不枚举可选纹理槽。面部与身体合成贴图仍单独导出。此限制不改变渲染或数值门槛。");
            var state = new Snapshot
            {
                Assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).OrderBy(n => n).ToArray(),
                Bones = root.GetComponentsInChildren<Transform>(true).Select(t => new BoneState { Id=t.GetInstanceID(),Name=t.name,Path=TransformPath(t,root.transform),Parent=t.parent == null ? "" : t.parent.name,Position=t.localPosition,Rotation=t.localRotation,Scale=t.localScale,World=t.position }).ToArray(),
                Renderers = root.GetComponentsInChildren<Renderer>(true).Select(r => new RendererState
                {
                    Name=r.name,Id=r.GetInstanceID(),SubmeshIndices=r is SkinnedMeshRenderer&&((SkinnedMeshRenderer)r).sharedMesh!=null?Enumerable.Range(0,((SkinnedMeshRenderer)r).sharedMesh.subMeshCount).Select(i=>((SkinnedMeshRenderer)r).sharedMesh.GetIndexCount(i)).ToArray():new uint[0],Path=TransformPath(r.transform,root.transform),World=r.transform.position,Scale=r.transform.lossyScale,Active=r.enabled && r.gameObject.activeInHierarchy,Visible=r.isVisible,
                    SortingLayer=r.sortingLayerID,SortingOrder=r.sortingOrder,Bounds=SafeBounds(r),LocalBounds=r is SkinnedMeshRenderer?((SkinnedMeshRenderer)r).localBounds:new Bounds(),
                    Layer=r.gameObject.layer,Casting=(int)r.shadowCastingMode,ReceiveShadows=r.receiveShadows,LightProbe=(int)r.lightProbeUsage,ReflectionProbe=(int)r.reflectionProbeUsage,Occlusion=r.allowOcclusionWhenDynamic,
                    MotionMode=(int)r.motionVectorGenerationMode,SkinnedMotion=r is SkinnedMeshRenderer&&((SkinnedMeshRenderer)r).skinnedMotionVectors,UpdateOffscreen=r is SkinnedMeshRenderer&&((SkinnedMeshRenderer)r).updateWhenOffscreen,
                    RootBone=r is SkinnedMeshRenderer&&((SkinnedMeshRenderer)r).rootBone!=null?TransformPath(((SkinnedMeshRenderer)r).rootBone,root.transform):"",SkinQuality=r is SkinnedMeshRenderer?(int)((SkinnedMeshRenderer)r).quality:0,Cloth=r.GetComponent("Cloth")!=null,
                    Bones = r is SkinnedMeshRenderer ? ((SkinnedMeshRenderer)r).bones.Select(b => b == null ? "" : b.name).ToArray() : new string[0],
                    BonePaths = r is SkinnedMeshRenderer ? ((SkinnedMeshRenderer)r).bones.Select(b => b == null ? "" : TransformPath(b,root.transform)).ToArray() : new string[0],
                    BoneIds = r is SkinnedMeshRenderer ? ((SkinnedMeshRenderer)r).bones.Select(b => b == null ? 0 : b.GetInstanceID()).ToArray() : new int[0],
                    BlendWeights = r is SkinnedMeshRenderer && ((SkinnedMeshRenderer)r).sharedMesh != null ? Enumerable.Range(0,((SkinnedMeshRenderer)r).sharedMesh.blendShapeCount).Select(i => ((SkinnedMeshRenderer)r).GetBlendShapeWeight(i)).ToArray() : new float[0],
                    Materials = r.sharedMaterials.Select(m => m == null ? new MaterialState { Name="MISSING" } : new MaterialState
                    {
                        Name=m.name,Id=m.GetInstanceID(),Shader=m.shader.name,Keywords=m.shaderKeywords,Queue=m.renderQueue,MotionPass=m.GetShaderPassEnabled("MOTIONVECTORS"),
                        Instancing=m.enableInstancing,DoubleSidedGI=m.doubleSidedGI,
                        Passes=Enumerable.Range(0,m.passCount).Select(i=>m.GetPassName(i)+"="+m.GetShaderPassEnabled(m.GetPassName(i))).ToArray(),
                        Tags=new[]{"RenderType","Queue","DisableBatching","IgnoreProjector","ForceNoShadowCasting","PreviewType"}.Select(n=>n+"="+m.GetTag(n,false,"")).ToArray(),
                        Properties = new[] { "_Color","_Color2","_Color3","_Color4","_Color5","_Color6","_Color7","_Color9","_Color10","_Color11","_Color12","_Color13","_TopColor","_UnderColor","_Specular","_EmissionColor","_EmissionColor2" }.Concat(extra("Color")).Distinct().Where(m.HasProperty).Select(p => new Property {Name=p,Color=m.GetColor(p)})
                            .Concat(new[] {"_Gloss","_Metallic","_Metallic2","_Metallic3","_Metallic4","_Glossiness","_Glossiness2","_Glossiness3","_Glossiness4","_Roughness","_Roughness2","_Roughness3","_Roughness4","_Float0","_NormalScale","_BumpScale","_Cutoff","_Anisotropy","_FresnelPower","_FresnelScale","_ZWrite","_Smoothness","_BumpScale2","_OcclusionStrength","_NipScale","_ShadowScale","_alpha_a","_alpha_b","_alpha_c","_alpha_d","_ExGloss"}.Concat(extra("Float")).Distinct().Where(m.HasProperty).Select(p => new Property {Name=p,Number=m.GetFloat(p)})).ToArray(),
                        // Some MOD materials return invalid native texture handles in Unity 2018,
                        // including in the independent player. Optional metadata must not crash capture.
                        Textures=new TextureState[0]
                    }).ToArray()
                }).ToArray()
            };
            using (var stream=File.Create(Path.Combine(directory,"state.json")))
                new DataContractJsonSerializer(typeof(Snapshot)).WriteObject(stream,state);
            var cloths=root.GetComponentsInChildren<Cloth>(true);
            if(cloths.Length>0)
                VisualFixture.Save(Path.Combine(directory,"cloth-state.json"),cloths.Select(c=>new ClothState
                {
                    Path=TransformPath(c.transform,root.transform),FixedDelta=Time.fixedDeltaTime,MaximumDelta=Time.maximumDeltaTime,Gravity=Physics.gravity,
                    Properties=new[]{"enabled","bendingStiffness","stretchingStiffness","damping","externalAcceleration","randomAcceleration","useGravity","friction","collisionMassScale","enableContinuousCollision","useVirtualParticles","worldVelocityScale","worldAccelerationScale","clothSolverFrequency","stiffnessFrequency","useTethers","sleepThreshold","selfCollisionDistance","selfCollisionStiffness"}.Select(n=>n+"="+Convert.ToString(typeof(Cloth).GetProperty(n).GetValue(c,null),System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
                    Coefficients=c.coefficients.Select(v=>new[]{v.maxDistance,v.collisionSphereDistance}).ToArray(),
                    Colliders=c.capsuleColliders.Where(v=>v!=null).Select(v=>v.name+" "+v.center.ToString("R")+" "+v.radius.ToString("R")+" "+v.height.ToString("R")+" "+v.direction+" "+v.transform.position.ToString("R")+" "+v.transform.lossyScale.ToString("R")).Concat(c.sphereColliders.Select(v=>v.first==null?"null":v.first.name+" "+v.first.center.ToString("R")+" "+v.first.radius.ToString("R")+" "+v.first.transform.position.ToString("R")+" "+v.first.transform.lossyScale.ToString("R"))).ToArray()
                }).ToArray());
            string textureTrace=Environment.GetEnvironmentVariable("HS2_TEXTURE_TRACE");
            if(!string.IsNullOrEmpty(textureTrace))
            {
                var names=textureTrace.Split(',');int index=0;
                foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
                foreach(var material in renderer.sharedMaterials.Where(m=>m!=null&&(names.Contains(renderer.name)||names.Contains("material:"+m.name.Replace("(Instance)","").Trim()))))
                foreach(var property in extra("Texture").Distinct().Where(material.HasProperty))
                {
                    var source=material.GetTexture(property);if(source==null)continue;
                    string prefix=Path.Combine(directory,"texture-"+(index++).ToString("D3"));
                    File.WriteAllText(prefix+".txt",TransformPath(renderer.transform,root.transform)+"\n"+material.name+"\n"+property+"\n"+source.name+"\n"+source.GetType().Name+" "+source.width+" "+source.height+" "+source.filterMode+" "+source.wrapMode+" "+source.anisoLevel+" "+source.mipMapBias+"\n"+material.GetTextureScale(property).ToString("R")+" "+material.GetTextureOffset(property).ToString("R"));
                    if(!(source is Texture2D)&&!(source is RenderTexture))continue;
                    var target=RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32);
                    var prior=RenderTexture.active;var pixels=new Texture2D(source.width,source.height,TextureFormat.RGBA32,false);
                    try{Graphics.Blit(source,target);RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,source.width,source.height),0,0);pixels.Apply();File.WriteAllBytes(prefix+".png",pixels.EncodeToPNG());}
                    finally{RenderTexture.active=prior;RenderTexture.ReleaseTemporary(target);UnityEngine.Object.Destroy(pixels);}
                }
            }
            if(Environment.GetEnvironmentVariable("HS2_MESH_TRACE")=="1"||root.GetComponentsInChildren<Cloth>(true).Length>0)
            {
                int index=0;
                foreach(var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r=>r.sharedMesh!=null))
                {
                    string prefix=Path.Combine(directory,"mesh-"+(index++).ToString("D3"));
                    File.WriteAllText(prefix+".txt",TransformPath(renderer.transform,root.transform));
                    var mesh=new Mesh();
                    try
                    {
                        renderer.BakeMesh(mesh);var vertices=mesh.vertices;var normals=mesh.normals;
                        var cloth=renderer.GetComponent<Cloth>();
                        if(cloth!=null)
                            using(var writer=new BinaryWriter(File.Create(prefix+"-cloth.bin")))
                            {var simulated=cloth.vertices;writer.Write(simulated.Length);foreach(var v in simulated){writer.Write(v.x);writer.Write(v.y);writer.Write(v.z);}}
                        using(var writer=new BinaryWriter(File.Create(prefix+".bin")))
                        {
                            writer.Write(vertices.Length);
                            for(int i=0;i<vertices.Length;i++)
                            {
                                var p=renderer.transform.TransformPoint(vertices[i]);var n=renderer.transform.TransformDirection(i<normals.Length?normals[i]:Vector3.zero);
                                writer.Write(p.x);writer.Write(p.y);writer.Write(p.z);writer.Write(n.x);writer.Write(n.y);writer.Write(n.z);
                            }
                        }
                    }
                    finally{UnityEngine.Object.Destroy(mesh);}
                }
            }
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
        public static void MaterialTrace(GameObject root,string stage)
        {
            string path=Environment.GetEnvironmentVariable("HS2_MATERIAL_TRACE");if(string.IsNullOrEmpty(path))return;
            File.AppendAllLines(path,root.GetComponentsInChildren<Renderer>(true).Where(r=>r.name=="top.002"||r.name=="bot").SelectMany(r=>r.sharedMaterials.Where(m=>m!=null).Select(m=>stage+" "+r.name+" "+m.name+" "+m.shader.name+" alpha="+(m.HasProperty("_AlphaEx")?m.GetFloat("_AlphaEx").ToString("R"):"absent"))));
        }
        private static string TransformPath(Transform node,Transform root)
        {
            string path=node.name;
            while(node.parent!=null&&node.parent!=root){node=node.parent;path=node.name+"/"+path;}
            return path;
        }
        private static Bounds SafeBounds(Renderer renderer)
        {
            var bounds=renderer.bounds;
            if(float.IsNaN(bounds.center.x)||float.IsInfinity(bounds.center.x)||float.IsNaN(bounds.extents.x)||float.IsInfinity(bounds.extents.x))
                return new Bounds(renderer.transform.position,Vector3.zero);
            return bounds;
        }
    }
}
