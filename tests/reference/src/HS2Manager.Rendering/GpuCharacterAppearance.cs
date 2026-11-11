using System.Numerics;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Rendering;

/// <summary>Instance-owned materials and composited textures for the shared character scene.</summary>
public sealed class GpuCharacterAppearance : IDisposable
{
    private readonly NativeCharacterScene scene;
    private readonly CharacterDefinition definition;
    private readonly GpuMaterialComposer composer;
    private readonly List<GpuTexture> generated = [];
    private readonly Dictionary<NativeSceneRenderer, GpuMaterialInstance?[]> materials = [];
    private readonly Dictionary<bool, (GpuTexture Color, GpuTexture Detail)> skin = [];
    public IReadOnlyDictionary<NativeSceneRenderer, GpuMaterialInstance?[]> Materials => materials;
    public GpuCharacterAppearance(NativeCharacterScene scene, GpuMaterialComposer composer)
    {
        this.scene=scene;definition=scene.Definition;this.composer=composer;
        try
        {
            foreach(var renderer in scene.Renderers)
                materials.Add(renderer,renderer.Materials.Select(m=>m==null?null:composer.CreateMaterial(m)).ToArray());
            foreach(var part in scene.Parts)
            {
                if(part.Definition.Role=="clothes")Clothes(part.Definition);
                foreach(var renderer in PartRenderers(part.Definition))
                    foreach(var material in materials[renderer].Where(m=>m!=null).Cast<GpuMaterialInstance>())
                    {
                        string role=part.Definition.Role,name=renderer.Node.Name;
                        if(role=="body"&&material.HasProperty("_MainTex")||role=="head"&&name=="o_head")Skin(material,role=="head");
                        else if(role=="head"&&name.StartsWith("o_eyebase",StringComparison.Ordinal))Eye(material,name.EndsWith("_R",StringComparison.Ordinal)?1:0);
                        else if(role=="head"&&name=="o_eyelashes")
                        {Select(material,"_MainTex","face.eyelashes.AddTex");material.SetColor("_Color",Color(definition.Face.Get("eyelashesColor"),Black));}
                        else if(role=="head"&&name=="o_eyeshadow")material.Set("_ShadowScale",Lerp(.1f,.9f,definition.Face.Float("whiteShadowScale")));
                        else if(role=="hair")Hair(material,part.Definition,renderer);
                        else if(role=="accessory")Accessory(material,part.Definition,renderer);
                        if(role=="body"||role=="clothes"&&part.Definition.Slot==2&&name=="o_bra_a")
                            foreach(string property in new[]{"_alpha_a","_alpha_b"})if(material.HasProperty(property))material.Set(property,1);
                    }
            }
            ClothingMasks();
            foreach(var part in scene.Parts)Extensions(part.Definition);
            DryMaker();
        }
        catch { Dispose();throw; }
    }
    private IEnumerable<NativeSceneRenderer> PartRenderers(CharacterPart part)=>scene.Renderers.Where(r=>r.Part==part);
    private GpuTexture Own(GpuTexture texture){generated.Add(texture);return texture;}
    private void Select(GpuMaterialInstance material,string property,string key)=>composer.SelectTexture(material,property,key);
    private static readonly Vector4 Black=new(0,0,0,1);
    private static Vector4 Color(Parameter value,Vector4 fallback)
    {
        if(value.Has("r"))return new(value.Float("r"),value.Float("g"),value.Float("b"),value.Float("a",1));
        return value.Children.Length>=3?new((float)value.At(0).Number,(float)value.At(1).Number,(float)value.At(2).Number,value.Children.Length>3?(float)value.At(3).Number:1):fallback;
    }
    private static float C(Parameter value,int axis){string key=new[]{"x","y","z","w"}[axis];return value.Has(key)?value.Float(key):(float)value.At(axis).Number;}
    private static float Lerp(float a,float b,float t)=>a+(b-a)*Math.Clamp(t,0,1);
    private static void Vector(GpuMaterialInstance material,string key,Vector4 value)=>material.Set(key,value.X,value.Y,value.Z,value.W);
    private void Skin(GpuMaterialInstance material,bool face)
    {
        string name=face?"face":"body";
        if(!skin.TryGetValue(face,out var textures))
        {textures=new GpuSkinComposer(composer).Compose(definition,face);Own(textures.Color);Own(textures.Detail);skin.Add(face,textures);}
        material.TextureOverrides["_MainTex"]=textures.Color;material.TextureOverrides["_DetailMainTex"]=textures.Detail;
        if(face)
        {
            Select(material,"_BumpMap","face.skin.NormalMapTex");Select(material,"_Texture3","face.eyebrow.AddTex");
            material.SetColor("_Color3",Color(definition.Face.Get("eyebrowColor"),Black));var brow=definition.Face.Get("eyebrowLayout");
            material.Set("_Texture3UV",Lerp(-.2f,.2f,C(brow,0)),Lerp(.16f,0,C(brow,1)),Lerp(2,.5f,C(brow,2)),Lerp(2,.5f,C(brow,3)));
            material.Set("_Texture3Rotator",Lerp(-.15f,.15f,definition.Face.Float("eyebrowTilt")));
        }
        else
        {
            Select(material,"_Texture2","body.nip.AddTex");material.SetColor("_Color2",Color(definition.Body.Get("nipColor"),Vector4.One));
            material.Set("_NipScale",definition.Body.Float("areolaSize",.5f));material.Set("_NipGloss",definition.Body.Float("nipGlossPower"));
            Select(material,"_Texture3","body.underhair.AddTex");material.SetColor("_Color3",Color(definition.Body.Get("underhairColor"),Black));
            material.SetColor("_Color13",Color(definition.Body.Get("nailColor"),Vector4.One));material.Set("_NailGloss",definition.Body.Float("nailGlossPower"));
        }
        Select(material,"_OcclusionMap",name+(face?".skin":".detail")+".OcclusionMapTex");
        Select(material,"_BumpMap2",name+".detail."+(face?"AddTex":"NormalMapTex"));
        material.Set("_BumpScale2",(face?definition.Face:definition.Body).Float("detailPower"));
        material.Set("_Gloss",Lerp(0,.8f,definition.Body.Float("skinGlossPower")));material.Set("_Metallic",definition.Body.Float("skinMetallicPower"));material.Set("_Texture4Scale",0);
    }
    private void Eye(GpuMaterialInstance material,int index)
    {
        var pupil=definition.Face.Get("pupil").At(index);
        Select(material,"_Texture2","eye."+index+".AddTex");Select(material,"_Texture3","pupil."+index+".AddTex");Select(material,"_Texture4","highlight.AddTex");
        material.SetColor("_Color",Color(pupil.Get("whiteColor"),Vector4.One));material.SetColor("_Color2",Color(pupil.Get("pupilColor"),Vector4.One));
        material.SetColor("_Color3",Color(pupil.Get("blackColor"),Black));material.SetColor("_Color4",Color(definition.Face.Get("hlColor"),Vector4.One));
        material.Set("_Emission",pupil.Float("pupilEmission"));material.Set("_Smoothness",1);
        var layout=material.Vector("_texture2uv");layout.X=Lerp(2,.5f,pupil.Float("pupilW",.5f));layout.Y=Lerp(2,.5f,pupil.Float("pupilH",.5f));layout.W=Lerp(.5f,-.5f,definition.Face.Float("pupilY"));Vector(material,"_texture2uv",layout);
        layout=material.Vector("_texture3uv");layout.X=Lerp(4,.4f,pupil.Float("blackW",.5f));layout.Y=Lerp(4,.4f,pupil.Float("blackH",.5f));Vector(material,"_texture3uv",layout);
        var hl=definition.Face.Get("hlLayout");material.Set("_Texture4UV",Lerp(1.8f,.2f,C(hl,0)),Lerp(1.8f,.2f,C(hl,1)),Lerp(-.3f,.3f,C(hl,2)),Lerp(-.3f,.3f,C(hl,3)));
        material.Set("_Texture4Rotator",Lerp(-1,1,definition.Face.Float("hlTilt")));
    }
    private void HairShader(GpuMaterialInstance material,string key)
    {
        int queue=material.Queue;
        if(composer.HasSelection("template.hair"))material.SetShader(composer.Material("template.hair").Shader);
        composer.OptimizeShader(material);Select(material,"_MainTex",key+(definition.Hair.Float("shaderType")==0?".TexD":".TexC"));
        material.Queue=queue;
    }
    private void Hair(GpuMaterialInstance material,CharacterPart part,NativeSceneRenderer renderer)
    {
        var component=part.Components.FirstOrDefault(c=>c.Type=="CmpHair");var values=definition.Hair.Get("parts").At(part.Slot);
        if(component!=null&&!component.Data.Get("rendHair").Children.Any(p=>p.Text==renderer.Node.Name))
        {
            if(component.Data.Get("rendAccessory").Children.Any(p=>p.Text==renderer.Node.Name))
                for(int i=0;i<Math.Min(3,component.Data.Get("acsDefColor").Children.Length);i++)
                    if(component.Data.Float("useAcsColor0"+(i+1))!=0){var c=Color(values.Get("acsColorInfo").At(i).Get("color"),Vector4.One);c.W=MathF.Max(1,c.W);material.SetColor(i==0?"_Color":"_Color"+(i+1),c);}
            return;
        }
        HairShader(material,"hair."+part.Slot);
        if(material.HasProperty("_Ringoff"))material.Set("_Ringoff",part.List.String("RingOff")=="1"?1:0);
        Vector4 Opaque(string key){var color=Color(values.Get(key),Vector4.One);color.W=MathF.Max(1,color.W);return color;}
        material.SetColor("_Color",Opaque("baseColor"));
        if(component==null||component.Data.Float("useTopColor")!=0)material.SetColor("_TopColor",Opaque("topColor"));
        if(component==null||component.Data.Float("useUnderColor")!=0)material.SetColor("_UnderColor",Opaque("underColor"));
        material.SetColor("_Specular",Color(values.Get("specular"),Vector4.One));material.Set("_Metallic",values.Float("metallic"));material.Set("_Smoothness",values.Float("smoothness"));
        if(component!=null&&component.Data.Float("useMesh")!=0)
        {
            Select(material,"_MeshColorMask_B","hair."+part.Slot+".mesh.MainTex");var color=Color(values.Get("meshColor"),Vector4.One);color.W=1;material.SetColor("_MeshColor_01",color);
            var layout=values.Get("meshLayout");material.SetTextureTransform("_MeshColorMask_B",new(Lerp(.5f,1,C(layout,0)),Lerp(.5f,1,C(layout,1))),new(Lerp(.5f,1.5f,C(layout,2)),Lerp(.5f,1.5f,C(layout,3))));
        }
    }
    private void Accessory(GpuMaterialInstance material,CharacterPart part,NativeSceneRenderer renderer)
    {
        var component=part.Components.FirstOrDefault(c=>c.Type=="CmpAccessory");if(component==null||materials[renderer].FirstOrDefault()!=material)return;
        var data=component.Data;bool alpha=data.Get("rendAlpha").Children.Any(p=>p.Text==renderer.Node.Name),normal=data.Get("rendNormal").Children.Any(p=>p.Text==renderer.Node.Name);
        var values=part.Parameters.Get("colorInfo");
        if(data.Float("typeHair")!=0&&normal)
        {
            HairShader(material,"accessory."+part.Slot);
            for(int i=0;i<3;i++)if(data.Float("useColor0"+(i+1))!=0)material.SetColor(new[]{"_Color","_TopColor","_UnderColor"}[i],Color(values.At(i).Get("color"),Vector4.One));
            material.SetColor("_Specular",Color(values.At(3).Get("color"),Vector4.One));material.Set("_Smoothness",values.At(0).Float("smoothnessPower"));material.Set("_Metallic",values.At(0).Float("metallicPower"));return;
        }
        for(int i=0;i<4;i++)
        {
            if(i==3?!alpha:!normal||data.Float("useColor0"+(i+1))==0)continue;
            var value=values.At(i);var color=value.Has("color")?Color(value.Get("color"),Vector4.One):part.Colors.Length>i*4+3?new(part.Colors[i*4],part.Colors[i*4+1],part.Colors[i*4+2],part.Colors[i*4+3]):Vector4.One;
            material.SetColor(i==0||alpha?"_Color":"_Color"+(i+1),color);material.Set(alpha?"_Glossiness4":i==0?"_Glossiness":"_Glossiness"+(i+1),value.Float("glossPower"));
            material.Set(alpha?"_Metallic4":i==0?"_Metallic":"_Metallic"+(i+1),value.Float("metallicPower"));if(alpha)renderer.Node.Active=color.W!=0;
        }
    }
    private void Clothes(CharacterPart part)
    {
        var component=part.Components.FirstOrDefault(c=>c.Type=="CmpClothes");if(component==null)return;
        var config=component.Data;var values=definition.Clothes.Get("parts").At(part.Slot);string prefix="clothes."+part.Slot+".";
        if(config.Float("useBreak")!=0)
        {
            var names=new[]{"rendNormal01","rendNormal02","rendNormal03"}.SelectMany(k=>config.Get(k).Children).Select(p=>p.Text).ToHashSet(StringComparer.Ordinal);
            foreach(var renderer in PartRenderers(part).Where(r=>names.Contains(r.Node.Name)))
                if(materials[renderer].FirstOrDefault() is {} material&&material.HasProperty("_AlphaEx"))material.Set("_AlphaEx",1-values.Float("breakRate"));
        }
        if(!composer.HasSelection(prefix+"MainTex")||!composer.HasSelection(prefix+"ColorMaskTex"))return;
        if(!composer.TryTexture(prefix+"MainTex",out _)||!composer.TryTexture(prefix+"ColorMaskTex",out _))return;
        for(int group=0;group<3;group++)
        {
            var names=config.Get("rendNormal0"+(group+1)).Children.Select(p=>p.Text).Where(n=>n.Length!=0).ToHashSet(StringComparer.Ordinal);if(names.Count==0)continue;
            string key=prefix+(group==0?"MainTex":"MainTex0"+(group+1));if(!composer.HasSelection(key))continue;
            if(!composer.TryTexture(key,out var main))continue;
            var creator=composer.Material("template.create_clothes");var detail=composer.Material("template.create_clothes detail");
            string mask=prefix+(group==0?"ColorMaskTex":"ColorMask0"+(group+1)+"Tex");Select(creator,"_ColorMask",mask);Select(detail,"_ColorMask",mask);
            var scale=config.Get("uvScalePattern");creator.Set("_UVScalePattern",C(scale,0),C(scale,1),C(scale,2),C(scale,3));
            for(int i=0;i<3;i++)
            {
                var value=values.Get("colorInfo").At(i);var color=Color(value.Get("baseColor"),Vector4.One);if(config.Float("useColorA0"+(i+1))==0)color.W=1;
                creator.SetColor(i==0?"_Color":"_Color"+(i+1),color);creator.SetColor("_Color"+(i+1)+"_2",Color(value.Get("patternColor"),Vector4.One));
                var layout=value.Get("layout");creator.Set("_patternuv"+(i+1),Lerp(20,1,C(layout,0)),Lerp(20,1,C(layout,1)),Lerp(-1,1,C(layout,2)),Lerp(-1,1,C(layout,3)));
                creator.Set("_patternuv"+(i+1)+"Rotator",Lerp(-1,1,value.Float("rotation")));Select(creator,"_PatternMask"+(i+1),prefix+"pattern."+i+".MainTex");
                detail.Set(i==0?"_Glossiness":"_Glossiness"+(i+1),value.Float("glossPower"));detail.Set(i==0?"_Metallic":"_Metallic"+(i+1),value.Float("metallicPower"));
            }
            creator.SetColor("_Color4",Color(config.Get("defMainColor04"),Vector4.One));detail.Set("_Glossiness4",config.Float("defGloss04"));detail.Set("_Metallic4",config.Float("defMetallic04"));
            var diffuse=Own(composer.Compose(main,creator,main.Width,main.Height));
            var properties=Own(composer.Compose(composer.Default("opaque-black"),detail,main.Width,main.Height));
            foreach(var renderer in PartRenderers(part).Where(r=>names.Contains(r.Node.Name)))
                if(materials[renderer].FirstOrDefault() is {} material){material.TextureOverrides["_MainTex"]=diffuse;material.TextureOverrides["_DetailMainTex"]=properties;}
        }
        ClothesOverlay(part,config);
    }
    private void ClothesOverlay(CharacterPart part,Parameter config)
    {
        string[] names=["ct_clothesTop","ct_clothesBot","ct_bra","ct_shorts","ct_gloves","ct_panst","ct_socks","ct_shoes"];
        if((uint)part.Slot>=names.Length)return;var overlay=definition.Overlays.FirstOrDefault(o=>o.Name==names[part.Slot]);if(overlay==null)return;
        for(int group=1;group<=3;group+=2)
        {
            if(config.Get("rendNormal0"+group).Children.Length==0)continue;
            for(int index=group;index<=Math.Min(3,group+1);index++)
            {
                string? name=config.Get("rendNormal0"+index).Children.FirstOrDefault()?.Text;var renderer=PartRenderers(part).FirstOrDefault(r=>r.Node.Name==name);
                var material=renderer==null?null:materials[renderer].FirstOrDefault();if(material==null||!material.TextureOverrides.TryGetValue("_MainTex",out var target))continue;
                var source=overlay.Replace?composer.Default("black"):target;
                var output=overlay.Texture==null?Own(composer.Copy(source,target.Width,target.Height)):Own(composer.Overlay(source,overlay,target.Width,target.Height));
                // All renderers sharing this generated map see the same update.
                foreach(var item in materials.Values.SelectMany(m=>m).Where(m=>m?.TextureOverrides.GetValueOrDefault("_MainTex")==target))item!.TextureOverrides["_MainTex"]=output;
            }
            break;
        }
    }
    private void ClothingMasks()
    {
        var visibility=definition.Visibility??new CharacterPreviewVisibility();
        var lower=scene.Parts.Select(p=>p.Definition).Where(p=>p.Role=="clothes"&&p.Slot is 0 or 1).LastOrDefault(p=>composer.HasSelection("clothes."+p.Slot+".OverBodyBMask"));
        bool enabled=visibility.ClothesVisible(1)&&lower!=null&&(lower.List.String("BreakDisableMask")!="1"||definition.Clothes.Get("parts").At(lower.Slot).Float("breakRate")==0);
        foreach(var renderer in scene.Renderers)
        {
            var part=renderer.Part;string? mask=part.Role=="body"?"OverBodyMask":part.Role=="clothes"&&part.Slot==2?"OverBraMask":null;
            string? bottom=part.Role=="body"?"OverBodyBMask":part.Role=="clothes"?part.Slot switch{2=>"OverInnerTBMask",3=>"OverInnerBMask",5=>"OverPanstMask",_=>null}:null;
            foreach(var material in materials[renderer].Where(m=>m!=null).Cast<GpuMaterialInstance>())
            {
                if(mask!=null&&(part.Role=="body"||renderer.Node.Name=="o_bra_a"))
                {
                    foreach(string property in new[]{"_alpha_a","_alpha_b"})if(material.HasProperty(property))material.Set(property,visibility.ClothesVisible(0)?1:0);
                    if(material.HasProperty("_AlphaMask")&&composer.HasSelection("clothes.0."+mask))Select(material,"_AlphaMask","clothes.0."+mask);
                    if(material.HasProperty("_alpha_c"))material.Set("_alpha_c",definition.Clothes.Get("parts").At(0).Float("breakRate")==0?0:1);
                }
                string maskRenderer=part.Role=="body"?renderer.Node.Name:part.Slot==5?"o_panst_a":"o_shorts_a";
                if(bottom!=null&&renderer.Node.Name==maskRenderer)
                {
                    if(lower!=null&&material.HasProperty("_AlphaMask2"))Select(material,"_AlphaMask2","clothes."+lower.Slot+"."+bottom);
                    if(material.HasProperty("_alpha_d"))material.Set("_alpha_d",enabled?1:0);
                }
            }
        }
    }
    private void Extensions(CharacterPart part)
    {
        if(part.Role is "rig" or "headRig")return;int type=part.Role switch{"clothes"=>1,"accessory"=>2,"hair"=>3,_=>4};
        var edits=definition.MaterialEdits.Where(e=>e.ObjectType==type&&(type==4||e.Slot==part.Slot)).ToArray();
        foreach(var renderer in PartRenderers(part))
        {
            foreach(var edit in edits.Where(e=>e.Kind=="renderer"&&e.Name==renderer.Node.Name))
            {if(edit.Property=="shadowCasting")renderer.ShadowCasting=(int)edit.Number;else if(edit.Property=="receiveShadows")renderer.ReceiveShadows=edit.Number!=0;else renderer.Enabled=edit.Number!=0;}
            foreach(var material in materials[renderer].Where(m=>m!=null).Cast<GpuMaterialInstance>())
                foreach(var edit in edits.Where(e=>e.Kind!="renderer"&&e.Name==material.Name.Replace("(Instance)","",StringComparison.Ordinal).Trim()).OrderBy(e=>e.Kind=="shader"?0:1))
                {
                    if(edit.Kind=="shader")
                    {
                        material.SetShader(composer.Shader(edit.Text));
                        // Material.shader clears the prior custom queue. Saved queue
                        // edits, when present, are applied after this shader change.
                        material.Queue=-1;
                        var source=definition.Shaders.FirstOrDefault(s=>s.Name==edit.Text);
                        if(source!=null)
                        {
                            if(source.RenderQueue>=0)material.Queue=source.RenderQueue;
                            foreach(var value in source.Defaults)
                                if(value.Type=="Texture"&&value.Texture!=null)composer.SelectTexture(material,value.Property,value.Texture);
                                else if(value.Type is "Float" or "Color")material.Set(value.Property,value.Values.ToArray());
                        }
                        continue;
                    }
                    if(edit.Kind=="queue"){material.Queue=(int)edit.Number;continue;}
                    if(!material.HasProperty(edit.Property))continue;
                    if(edit.Kind=="float")material.Set(edit.Property,edit.Number);
                    else if(edit.Kind=="color")material.Set(edit.Property,edit.Values.ToArray());
                    else if(edit.Kind=="texture")
                    {
                        if(edit.Texture!=null)material.TextureOverrides[edit.Property]=composer.Embedded(edit.Texture);
                        material.SetTextureTransform(edit.Property,edit.Scale is {Length:2}?new(edit.Scale[0],edit.Scale[1]):null,edit.Offset is {Length:2}?new(edit.Offset[0],edit.Offset[1]):null);
                    }
                }
        }
    }
    private void DryMaker()
    {
        foreach(var part in scene.Parts.Select(p=>p.Definition))
        {
            string? type=part.Role=="hair"?"CmpHair":part.Role=="clothes"?"CmpClothes":null;var component=part.Components.FirstOrDefault(c=>c.Type==type);
            var names=component==null?[]:new[]{"rendHair","rendAccessory","rendNormal01","rendNormal02","rendNormal03"}.SelectMany(k=>component.Data.Get(k).Children.Select(p=>p.Text)).ToArray();
            foreach(var renderer in PartRenderers(part))
                if(part.Role=="body"||part.Role=="head"&&renderer.Node.Name=="o_head"||names.Contains(renderer.Node.Name))
                    foreach(var material in materials[renderer].Where(m=>m!=null).Cast<GpuMaterialInstance>())if(material.HasProperty("_ExGloss"))material.Set("_ExGloss",0);
        }
    }
    public void Dispose(){foreach(var texture in generated)texture.Dispose();generated.Clear();materials.Clear();skin.Clear();}
}
