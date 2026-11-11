using System.Numerics;
using System.Text.Json;
using HS2Manager.Core;

namespace HS2Manager.Rendering;

public sealed class NativePbrMaterialRecord
{
    public string Id="",Name="",SourceShader="",Role="",Kind="",BaseColor="",Normal="",Metallic="",Roughness="",Occlusion="";
    public string Emission="",HairStreakMask="";
    public string BaseColorSpace="sRGB",EmissionColorSpace="sRGB",DataColorSpace="Linear",NormalConvention="OpenGL +Y",AlphaMode="Opaque";
    public float AlphaCutoff=.5f,HairExtraGloss,HairStreakStrength;
    public float[] HairStreakColor=[1,1,1,1];
    public int HairStreakUv=1;
    public bool DoubleSided;
}

/// <summary>Preserves the established material manifest and PBR texture conversion for all three export targets.</summary>
public sealed class NativePbrMaterialExporter:IDisposable
{
    private readonly GpuMaterialComposer composer;
    private readonly NativePbrBakePass baker;
    private readonly string directory;
    private readonly Dictionary<string,NativePbrMaterialRecord> records=new(StringComparer.Ordinal);
    private readonly HashSet<string> unknown=new(StringComparer.Ordinal);
    public List<string> Warnings {get;}=[];
    public List<string> Issues {get;}=[];
    public IReadOnlyCollection<NativePbrMaterialRecord> Records=>records.Values;
    public NativePbrMaterialExporter(GraphicsDevice graphics,GpuMaterialComposer composer,string directory)
    {this.composer=composer;this.directory=Path.GetFullPath(directory);Directory.CreateDirectory(Path.Combine(this.directory,"Textures"));baker=new(graphics,composer);}
    public NativePbrMaterialRecord Get(GpuMaterialInstance source,string role,string renderer,string slot,CancellationToken cancellation=default)
    {
        if(records.TryGetValue(slot,out var existing))return existing;
        cancellation.ThrowIfCancellationRequested();string shader=source.Shader.Name;
        var record=new NativePbrMaterialRecord{Id="mat_"+records.Count.ToString("D3"),Name=source.Name,SourceShader=shader,Role=role};
        bool skin=role=="body"||role=="head"&&renderer=="o_head",eye=renderer.StartsWith("o_eyebase",StringComparison.Ordinal);
        bool hair=shader.Contains("hair",StringComparison.OrdinalIgnoreCase)||source.HasProperty("_TopColor")&&source.HasProperty("_UnderColor");
        bool originalHair=shader is "Shader Forge/hair_07" or "Shader Forge/hair_08",lash=renderer=="o_eyelashes",eyeShadow=shader=="AIT/main eyeshadow lambert",tear=shader=="AIT/main namida",packedClothes=shader.StartsWith("AIT/Clothes",StringComparison.Ordinal);
        record.Kind=skin?"Skin":eye?"Eye":hair?"Hair":lash?"Eyelash":eyeShadow?"EyeShadow":tear?"Tearline":"Surface";
        bool known=skin&&shader.StartsWith("AIT/Skin",StringComparison.Ordinal)||eye&&shader=="AIT/Eye Translucency"||originalHair||lash&&shader=="AIT/eyelashes"||eyeShadow||tear||packedClothes||shader=="Standard";
        if(!known&&unknown.Add(shader))Warnings.Add("材质近似："+shader+" 使用通用 PBR 转换，未还原专用 Shader 效果。");
        bool transparent=source.Queue>=3000||source.Keywords.Contains("_ALPHABLEND_ON")||source.Keywords.Contains("_ALPHAPREMULTIPLY_ON");
        bool masked=hair||lash||skin||source.Queue>=2450||source.Keywords.Contains("_ALPHATEST_ON")||source.Tag("RenderType")=="TransparentCutout";
        record.AlphaMode=transparent&&!hair&&!lash&&!skin?"Blend":masked?"Mask":"Opaque";
        if(originalHair||eyeShadow||tear||shader=="AIT/Clothes Alpha")record.AlphaMode="Blend";
        record.AlphaCutoff=Number(.5f,"_Cutoff");record.DoubleSided=hair||lash||Number(2,"_Cull","_CullMode")==0;
        record.HairExtraGloss=originalHair?Math.Clamp(Number(0,"_ExGloss"),0,1):0;
        var textures=NativePbrBakePass.TextureNames.ToDictionary(n=>n,n=>composer.AssignedTexture(source,n),StringComparer.Ordinal);
        if(originalHair&&composer.AssignedTexture(source,"_Occlusion") is {} ao)textures["_OcclusionMap"]=ao;
        var values=new Dictionary<string,float[]>(StringComparer.Ordinal)
        {
            ["_Color"]=ShaderColor(Vector4.One,"_Color","_BaseColor"),["_TopColor"]=ShaderColor(Vector4.One,"_TopColor"),["_UnderColor"]=ShaderColor(Vector4.One,"_UnderColor"),
            ["_MaterialKind"]=[skin?3:eye?2:hair?1:lash?4:eyeShadow?5:tear?6:0],["_OriginalHair"]=[originalHair?1:0],["_PackedKind"]=[skin?1:packedClothes?2:0],
            ["_OriginalEye"]=[shader=="AIT/Eye Translucency"?1:0],["_TopGradation"]=[Number(1,"_TopGradation")],["_UnderGradation"]=[Number(1,"_UnderGradation")],
            ["_ColorMask_A0B1"]=[Number(0,"_ColorMask_A0B1")],["_DetailMainScale"]=[Number(1,"_DetailMainScale")],["_DetailScale"]=[Number(0,"_DetailScale")],
            ["_ShadowScale"]=[Number(.5f,"_ShadowScale")],["_NamidaScale"]=[Number(0,"_NamidaScale")],["_OcclusionStrength"]=[Number(1,"_OcclusionStrength","_OcculusionStrength")],
            ["_IgnoreMainAlpha"]=[skin||shader.Contains("Clothes True",StringComparison.OrdinalIgnoreCase)?1:0],["_BakeAlpha"]=[record.AlphaMode=="Opaque"?0:1],
            ["_BaseMetallic"]=[Math.Clamp(Number(0,"_Metallic"),0,1)],["_BaseSmoothness"]=[Math.Clamp(Number(.5f,"_Smoothness","_Glossiness","_Gloss"),0,1)],
            ["_BumpScale"]=[Number(1,"_BumpScale")],["_BumpScale2"]=[Number(1,"_BumpScale2")],["_EmissionColor"]=ShaderColor(new(0,0,0,1),"_EmissionColor"),["_Emission"]=[Number(0,"_Emission")],
            ["_EmissionGain"]=[Number(1,"_EmissionStrength")]
        };
        foreach(string property in new[]{"_alpha_a","_alpha_b","_alpha_c","_alpha_d"})values[property]=[Number(0,property)];
        if(originalHair&&source.Textures.GetValueOrDefault("_Occlusion") is {} binding)
            values["_OcclusionMap_ST"]=[binding.Scale.X,binding.Scale.Y,binding.Offset.X,binding.Offset.Y];
        (int Width,int Height) Size(params string[] names)
        {var assigned=names.Select(n=>textures.GetValueOrDefault(n)).Where(t=>t!=null).ToArray();return assigned.Length==0?(1,1):(assigned.Max(t=>t!.Width),assigned.Max(t=>t!.Height));}
        string Bake(string suffix,int kind,(int Width,int Height) size,bool color)
        {
            cancellation.ThrowIfCancellationRequested();values["_BakeKind"]=[kind];
            using var target=baker.Draw(source,textures,values,size.Width,size.Height,color);
            string relative="Textures/"+record.Id+"_"+suffix+".png";
            ImageCodec.SavePngAsync(Path.Combine(directory,relative),size.Width,size.Height,composer.ReadImage(target)).GetAwaiter().GetResult();cancellation.ThrowIfCancellationRequested();return relative;
        }
        var baseSize=Size("_MainTex","_Texture2","_Texture3","_Texture4","_ColorMask","_ColorMask_B","_AlphaMask","_AlphaMask2");
        if(originalHair&&Number(0,"_Mesh_onoff")>0&&textures["_MeshColorMask_B"] is {} streak)
        {
            record.HairStreakMask=Bake("hair_streak",6,(streak.Width,streak.Height),false);record.HairStreakColor=Linear(Color(Vector4.One,"_MeshColor_01"));record.HairStreakStrength=Math.Clamp(Number(0,"_Mesh_onoff"),0,1);
            Warnings.Add(renderer+"：挑染保留独立 UV1 遮罩，原生导出自动接线；通用 FBX 需按材质清单配置。");
        }
        record.BaseColor=Bake("basecolor",0,baseSize,true);
        var normalSize=originalHair?Size("_BumpMap","_BumpMap2","_DetailMask"):Size("_BumpMap","_BumpMap2");
        if(originalHair&&textures["_DetailMask"]!=null)normalSize=(Math.Max(normalSize.Width,baseSize.Width),Math.Max(normalSize.Height,baseSize.Height));
        record.Normal=Bake("normal",1,normalSize,false);var dataSize=Size("_DetailMainTex","_MetallicGlossMap");
        record.Metallic=Bake("metallic",2,dataSize,false);record.Roughness=Bake("roughness",3,dataSize,false);record.Occlusion=Bake("occlusion",4,Size("_OcclusionMap"),false);
        var emission=Color(new(0,0,0,1),"_EmissionColor");
        if(Number(1,"_EmissionStrength")>0&&(textures["_EmissionMap"]!=null||Math.Max(emission.X,Math.Max(emission.Y,emission.Z))>0)||eye&&Number(0,"_Emission")>0)
            record.Emission=Bake("emission",5,textures["_EmissionMap"]==null?baseSize:Size("_EmissionMap"),true);
        records.Add(slot,record);return record;
        float Number(float fallback,params string[] names){foreach(string name in names)if(source.HasProperty(name)&&source.Values.TryGetValue(name,out var v))return v[0];return fallback;}
        Vector4 Color(Vector4 fallback,params string[] names){foreach(string name in names)if(source.HasProperty(name)&&source.Values.TryGetValue(name,out var v)&&v.Length==4)return new(v[0],v[1],v[2],v[3]);return fallback;}
        float[] ShaderColor(Vector4 fallback,params string[] names)
        {
            foreach(string name in names)if(source.HasProperty(name)&&source.Values.TryGetValue(name,out var v)&&v.Length==4)
                // CopyPropertiesFromMaterial retains the source property's upload
                // type. An alias such as _BaseColor -> _Color has no source
                // _Color color slot, so its explicitly assigned value stays raw.
                return source.ColorProperties.Contains(names[0])||source.GammaProperties.Contains(names[0])?Linear(new(v[0],v[1],v[2],v[3])):(float[])v.Clone();
            return [fallback.X,fallback.Y,fallback.Z,fallback.W];
        }
    }
    private static float[] Linear(Vector4 value)=>[GpuMaterialComposer.Linear(value.X),GpuMaterialComposer.Linear(value.Y),GpuMaterialComposer.Linear(value.Z),value.W];
    public void Save()=>File.WriteAllText(Path.Combine(directory,"materials.json"),JsonSerializer.Serialize(new{SchemaVersion=3,Materials=Records},new JsonSerializerOptions{IncludeFields=true,WriteIndented=true}));
    public void Dispose()=>baker.Dispose();
}
