using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Fbx;
using UnityEngine;
using Object=UnityEngine.Object;

namespace HS2Manager.UnityPreview
{
    internal sealed class ModelMaterialExporter : IDisposable
    {
        [Serializable] internal sealed class MaterialRecord
        {
            public string Id,Name,SourceShader,Role,Kind,BaseColor,Normal,Metallic,Roughness,Occlusion,Emission;
            public string BaseColorSpace="sRGB",EmissionColorSpace="sRGB",DataColorSpace="Linear",NormalConvention="OpenGL +Y";
            public string AlphaMode="Opaque";
            public float AlphaCutoff=.5f;
            public float HairExtraGloss;
            public string HairStreakMask;
            public float[] HairStreakColor = new float[] { 1, 1, 1, 1 };
            public int HairStreakUv = 1;
            public float HairStreakStrength;
            public bool DoubleSided;
        }
        [Serializable] private sealed class MaterialRecords{public int SchemaVersion=3;public MaterialRecord[] Materials;}
        private readonly FbxScene scene;
        private readonly string directory;
        private readonly List<string> warnings;
        private readonly List<string> issues;
        private readonly Dictionary<string,FbxSurfacePhong> cache=new Dictionary<string,FbxSurfacePhong>();
        private readonly List<MaterialRecord> records=new List<MaterialRecord>();
        private readonly HashSet<string> unknown=new HashSet<string>();
        private readonly Material baker;
        private readonly Material defaults;
        internal int Count {get{return records.Count;}}
        internal ModelMaterialExporter(FbxScene target,string output,List<string> diagnostics,List<string> missing)
        {
            scene=target;directory=output;warnings=diagnostics;issues=missing;
            var shader=Shader.Find("Hidden/HS2Manager/ModelExportBake");
            if(shader==null||!shader.isSupported)throw new IOException("PBR 贴图烘焙 Shader 不可用。");
            baker=new Material(shader);defaults=new Material(shader);Directory.CreateDirectory(Path.Combine(output,"Textures"));
        }
        internal FbxSurfacePhong Get(Material source,string role,string renderer,string slot,Func<bool> canceled)
        {
            string key=slot;
            FbxSurfacePhong existing;if(cache.TryGetValue(key,out existing))return existing;
            string id="mat_"+records.Count.ToString("D3"),shader=source==null||source.shader==null?"":source.shader.name;
            var record=new MaterialRecord{Id=id,Name=source==null?"Missing material":source.name,SourceShader=shader,Role=role};
            bool skin=role=="body"||role=="head"&&renderer=="o_head";
            bool eye=renderer.StartsWith("o_eyebase",StringComparison.Ordinal);
            bool hair=shader.IndexOf("hair",StringComparison.OrdinalIgnoreCase)>=0||Has(source,"_TopColor")&&Has(source,"_UnderColor");
            bool lash=renderer=="o_eyelashes";
            bool originalHair=shader=="Shader Forge/hair_07"||shader=="Shader Forge/hair_08";
            record.HairExtraGloss=originalHair?Mathf.Clamp01(Number(source,0,"_ExGloss")):0;
            bool eyeShadow=shader=="AIT/main eyeshadow lambert",tear=shader=="AIT/main namida";
            bool packedClothes=shader.StartsWith("AIT/Clothes",StringComparison.Ordinal);
            record.Kind=skin?"Skin":eye?"Eye":hair?"Hair":lash?"Eyelash":eyeShadow?"EyeShadow":tear?"Tearline":"Surface";
            bool known=skin&&shader.StartsWith("AIT/Skin",StringComparison.Ordinal)||eye&&shader=="AIT/Eye Translucency"||originalHair||lash&&shader=="AIT/eyelashes"||eyeShadow||tear||packedClothes||shader=="Standard";
            if(!known&&unknown.Add(shader))warnings.Add("材质近似："+shader+" 使用通用 PBR 转换，未还原专用 Shader 效果。");
            if(source==null)issues.Add("缺少材质："+renderer);
            bool transparent=source!=null&&(source.renderQueue>=3000||source.IsKeywordEnabled("_ALPHABLEND_ON")||source.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"));
            bool masked=hair||lash||skin||source!=null&&(source.renderQueue>=2450||source.IsKeywordEnabled("_ALPHATEST_ON")||source.GetTag("RenderType",false)=="TransparentCutout");
            record.AlphaMode=transparent&&!hair&&!lash&&!skin?"Blend":masked?"Mask":"Opaque";
            // The original hair shader uses screen-door coverage rather than a
            // single hard threshold. Preserve strand coverage in Blender.
            if(originalHair)record.AlphaMode="Blend";
            if(eyeShadow||tear||shader=="AIT/Clothes Alpha")record.AlphaMode="Blend";
            record.AlphaCutoff=Number(source,.5f,"_Cutoff");
            record.DoubleSided=hair||lash||Number(source,2,"_Cull","_CullMode")==0;
            // Copy only matching properties. The bake mode and feature flags are then set explicitly.
            baker.CopyPropertiesFromMaterial(defaults);
            if(source!=null)baker.CopyPropertiesFromMaterial(source);
            foreach(string property in new[]{"_MainTex","_ColorMask","_ColorMask_B","_AlphaMask","_AlphaMask2","_DetailMainTex","_DetailMask","_BumpMap","_BumpMap2","_MetallicGlossMap","_OcclusionMap","_EmissionMap","_Texture2","_Texture3","_Texture4","_MeshColorMask_B"})
            {
                var texture=TextureOf(source,property);
                bool exists=texture!=null;
                baker.SetFloat("_Has"+property.Substring(1),exists?1:0);
                if(!exists){baker.SetTexture(property,property=="_MainTex"||property=="_OcclusionMap"||property=="_MetallicGlossMap"?Texture2D.whiteTexture:Texture2D.blackTexture);baker.SetTextureScale(property,Vector2.one);baker.SetTextureOffset(property,Vector2.zero);}
            }
            if(originalHair&&TextureOf(source,"_Occlusion")!=null)
            {
                baker.SetTexture("_OcclusionMap",TextureOf(source,"_Occlusion"));baker.SetFloat("_HasOcclusionMap",1);
                baker.SetTextureScale("_OcclusionMap",source.GetTextureScale("_Occlusion"));baker.SetTextureOffset("_OcclusionMap",source.GetTextureOffset("_Occlusion"));
            }
            baker.SetColor("_Color",ColorOf(source,Color.white,"_Color","_BaseColor"));
            baker.SetColor("_TopColor",ColorOf(source,Color.white,"_TopColor"));baker.SetColor("_UnderColor",ColorOf(source,Color.white,"_UnderColor"));
            baker.SetFloat("_MaterialKind",skin?3:eye?2:hair?1:lash?4:eyeShadow?5:tear?6:0);
            baker.SetFloat("_OriginalHair",originalHair?1:0);
            baker.SetFloat("_PackedKind",skin?1:packedClothes?2:0);
            baker.SetFloat("_OriginalEye",shader=="AIT/Eye Translucency"?1:0);
            baker.SetFloat("_TopGradation",Number(source,1,"_TopGradation"));baker.SetFloat("_UnderGradation",Number(source,1,"_UnderGradation"));
            baker.SetFloat("_ColorMask_A0B1",Number(source,0,"_ColorMask_A0B1"));
            baker.SetFloat("_DetailMainScale",Number(source,1,"_DetailMainScale"));
            baker.SetFloat("_DetailScale",Number(source,0,"_DetailScale"));
            baker.SetFloat("_ShadowScale",Number(source,.5f,"_ShadowScale"));
            baker.SetFloat("_NamidaScale",Number(source,0,"_NamidaScale"));
            baker.SetFloat("_OcclusionStrength",Number(source,1,"_OcclusionStrength","_OcculusionStrength"));
            if(originalHair&&Number(source,0,"_Mesh_onoff")>0&&TextureOf(source,"_MeshColorMask_B")!=null)
            {
                var streak=TextureOf(source,"_MeshColorMask_B");
                record.HairStreakMask=Bake(id,"hair_streak",6,streak.width,streak.height,false,canceled);
                var tint=ColorOf(source,Color.white,"_MeshColor_01").linear;
                record.HairStreakColor=new[]{tint.r,tint.g,tint.b,tint.a};
                record.HairStreakStrength=Mathf.Clamp01(Number(source,0,"_Mesh_onoff"));
                warnings.Add(renderer+"：挑染保留独立 UV1 遮罩，原生导出自动接线；通用 FBX 需按材质清单配置。");
            }
            baker.SetFloat("_IgnoreMainAlpha",skin||shader.IndexOf("Clothes True",StringComparison.OrdinalIgnoreCase)>=0?1:0);
            baker.SetFloat("_BakeAlpha",record.AlphaMode=="Opaque"?0:1);
            baker.SetFloat("_BaseMetallic",Mathf.Clamp01(Number(source,0,"_Metallic")));
            baker.SetFloat("_BaseSmoothness",Mathf.Clamp01(Number(source,.5f,"_Smoothness","_Glossiness","_Gloss")));
            baker.SetFloat("_BumpScale",Number(source,1,"_BumpScale"));baker.SetFloat("_BumpScale2",Number(source,1,"_BumpScale2"));
            foreach(string property in new[]{"_alpha_a","_alpha_b","_alpha_c","_alpha_d"})baker.SetFloat(property,Number(source,0,property));
            baker.SetColor("_EmissionColor",ColorOf(source,Color.black,"_EmissionColor"));
            baker.SetFloat("_Emission",Number(source,0,"_Emission"));
            var textures=new[]{"_MainTex","_Texture2","_Texture3","_Texture4","_ColorMask","_ColorMask_B","_AlphaMask","_AlphaMask2"}
                .Select(p=>TextureOf(source,p)).Where(t=>t!=null).ToArray();
            int width=textures.Length==0?1:textures.Max(t=>t.width),height=textures.Length==0?1:textures.Max(t=>t.height);
            record.BaseColor=Bake(id,"basecolor",0,width,height,true,canceled);
            var normals=new[]{TextureOf(source,"_BumpMap"),TextureOf(source,"_BumpMap2"),originalHair?TextureOf(source,"_DetailMask"):null}.Where(t=>t!=null).ToArray();
            // Hair detail normals are sampled at 16x in the source shader. A
            // missing primary normal must not collapse their bake to one pixel.
            int normalWidth=normals.Length==0?1:normals.Max(t=>t.width),normalHeight=normals.Length==0?1:normals.Max(t=>t.height);
            if(originalHair&&TextureOf(source,"_DetailMask")!=null){normalWidth=Math.Max(normalWidth,width);normalHeight=Math.Max(normalHeight,height);}
            record.Normal=Bake(id,"normal",1,normalWidth,normalHeight,false,canceled);
            var detail=TextureOf(source,"_DetailMainTex");var metallicMap=TextureOf(source,"_MetallicGlossMap");
            int dw=Math.Max(detail==null?1:detail.width,metallicMap==null?1:metallicMap.width),dh=Math.Max(detail==null?1:detail.height,metallicMap==null?1:metallicMap.height);
            record.Metallic=Bake(id,"metallic",2,dw,dh,false,canceled);
            record.Roughness=Bake(id,"roughness",3,dw,dh,false,canceled);
            var occlusion=TextureOf(source,"_OcclusionMap")??(originalHair?TextureOf(source,"_Occlusion"):null);
            record.Occlusion=Bake(id,"occlusion",4,occlusion==null?1:occlusion.width,occlusion==null?1:occlusion.height,false,canceled);
            var emission=TextureOf(source,"_EmissionMap");
            float emissionGain=Number(source,1,"_EmissionStrength");baker.SetFloat("_EmissionGain",emissionGain);
            if(emissionGain>0&&(emission!=null||ColorOf(source,Color.black,"_EmissionColor").maxColorComponent>0)||eye&&Number(source,0,"_Emission")>0)
                record.Emission=Bake(id,"emission",5,emission==null?width:emission.width,emission==null?height:emission.height,true,canceled);
            var material=FbxSurfacePhong.Create(scene,id);material.Diffuse.Set(new FbxDouble3(1,1,1));material.DiffuseFactor.Set(1);material.Shininess.Set(20);
            Connect(material.Diffuse,record.BaseColor);
            Connect(material.NormalMap,record.Normal);
            if(record.Emission!=null){material.Emissive.Set(new FbxDouble3(1,1,1));Connect(material.Emissive,record.Emission);}
            records.Add(record);cache.Add(key,material);return material;
        }
        private string Bake(string id,string suffix,int mode,int width,int height,bool color,Func<bool> canceled)
        {
            if(canceled())throw new OperationCanceledException();
            baker.SetFloat("_BakeKind",mode);
            var previous=RenderTexture.active;bool previousSrgb=GL.sRGBWrite;
            RenderTexture target=null;Texture2D image=null;
            try
            {
                target=RenderTexture.GetTemporary(width,height,0,RenderTextureFormat.ARGB32,color?RenderTextureReadWrite.sRGB:RenderTextureReadWrite.Linear);
                GL.sRGBWrite=color;
                Graphics.Blit(baker.GetTexture("_MainTex"),target,baker);
                RenderTexture.active=target;
                image=new Texture2D(width,height,TextureFormat.RGBA32,false,!color);
                image.ReadPixels(new Rect(0,0,width,height),0,0,false);image.Apply(false,false);
                string relative="Textures/"+id+"_"+suffix+".png";
                File.WriteAllBytes(Path.Combine(directory,relative),image.EncodeToPNG());
                return relative;
            }
            finally{GL.sRGBWrite=previousSrgb;RenderTexture.active=previous;if(image!=null)Object.DestroyImmediate(image);if(target!=null)RenderTexture.ReleaseTemporary(target);}
        }
        private void Connect(FbxProperty property,string relative)
        {
            var texture=FbxFileTexture.Create(scene,Path.GetFileNameWithoutExtension(relative));
            texture.SetFileName(Path.Combine(directory,relative));texture.SetRelativeFileName(relative);
            texture.SetTextureUse(FbxTexture.ETextureUse.eStandard);texture.SetMappingType(FbxTexture.EMappingType.eUV);
            texture.UVSet.Set("UV0");property.ConnectSrcObject(texture);
        }
        private static bool Has(Material m,string p){return m!=null&&m.HasProperty(p);}
        private static Texture TextureOf(Material m,string p)
        {
            if(!Has(m,p))return null;
            var value=m.GetTexture(p);
            // Legacy AssetBundles can contain a PPtr of another native type in an
            // optional texture slot. Unity's binding returns it without a CLR cast;
            // reading Texture.width on e.g. a Transform crashes Mono outright.
            if(ReferenceEquals(value,null)||!typeof(Texture).IsAssignableFrom(value.GetType()))return null;
            return value==null?null:value;
        }
        private static float Number(Material m,float fallback,params string[] properties){foreach(string p in properties)if(Has(m,p))return m.GetFloat(p);return fallback;}
        private static Color ColorOf(Material m,Color fallback,params string[] properties){foreach(string p in properties)if(Has(m,p))return m.GetColor(p);return fallback;}
        internal void Save()
        {
            File.WriteAllText(Path.Combine(directory,"materials.json"),JsonUtility.ToJson(new MaterialRecords{Materials=records.ToArray()},true));
        }
        public void Dispose(){Object.DestroyImmediate(baker);Object.DestroyImmediate(defaults);}
    }
}
