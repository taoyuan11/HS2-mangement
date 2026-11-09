using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HS2Manager.Preview.Contracts;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    public sealed partial class OriginalCharacter
    {
        private readonly Dictionary<string,Texture2D> embeddedTextures=new Dictionary<string,Texture2D>();
        private Material overlayMaterial;
        private RenderTexture Overlay(Texture original,OverlayDefinition overlay,int width,int height=0)
        {
            if(overlayMaterial==null)overlayMaterial=Own(new Material(Selected<Shader>("overlay.composite")));
            overlayMaterial.SetTexture("_Overlay",Embedded(overlay.Texture));
            return Compose(original,overlayMaterial,width,height,true);
        }
        private Texture SkinOverlay(Texture original,int type,int size)
        {
            Texture result=original;
            foreach(var overlay in definition.Overlays.Where(o=>o.Type==type))
            {
                result=Overlay(result,overlay,size);
            }
            return result;
        }
        private void ClothesOverlay(CharacterPart part,Parameter config)
        {
            string[] names={"ct_clothesTop","ct_clothesBot","ct_bra","ct_shorts","ct_gloves","ct_panst","ct_socks","ct_shoes"};
            if(part.Slot<0||part.Slot>=names.Length)return;
            var overlay=definition.Overlays.FirstOrDefault(o=>o.Name==names[part.Slot]);
            if(overlay==null)return;
            for(int group=1;group<=3;group+=2)
            {
                var first=config.Get("rendNormal0"+group).Children.FirstOrDefault();
                if(first==null)continue;
                for(int index=group;index<=Math.Min(3,group+1);index++)
                {
                    string name=config.Get("rendNormal0"+index).Children.Select(p=>p.Text).FirstOrDefault();
                    var renderer=PartRenderers(part).FirstOrDefault(r=>r.name==name);
                    var target=renderer==null||renderer.sharedMaterial==null?null:renderer.sharedMaterial.mainTexture as RenderTexture;
                    if(target==null)continue;
                    var prior=RenderTexture.active;
                    if(overlay.Replace){RenderTexture.active=target;GL.Clear(false,true,Color.clear);RenderTexture.active=prior;}
                    if(overlay.Texture!=null){var composed=Overlay(target,overlay,target.width,target.height);Graphics.Blit(composed,target);}
                }
                break;
            }
        }
        public void PoseFace(float eyes,bool dynamic=false)
        {
            foreach(var entry in parts.Where(p=>p.Key.Role=="head"))
            {
                var component=entry.Key.Components.FirstOrDefault(c=>c.Type=="FaceBlendShape");
                if(component==null)continue;
                foreach(var controller in new[]{"EyesCtrl","EyebrowCtrl","MouthCtrl"})
                {
                    float maximum=definition.EyeOpenMax;
                    if(dynamic&&controller=="EyesCtrl"&&eyeLook!=null&&!string.IsNullOrEmpty(component.Data.String("EyeLookController")))
                    {
                        float up=component.Data.Float("EyeLookUpCorrect",.1f),down=component.Data.Float("EyeLookDownCorrect",.3f),side=component.Data.Float("EyeLookSideCorrect",.1f);
                        float h=eyeLook.HorizontalRate,v=eyeLook.VerticalRate;
                        float correction=(v>0?up*Mathf.Sqrt(v):-down*Mathf.Sqrt(-v))-side*Mathf.Sqrt(Mathf.Abs(h));
                        maximum=Mathf.Min(1-up,maximum)+Mathf.Clamp(correction,-Mathf.Max(down,side),up)*maximum;
                    }
                    float amount=controller=="EyesCtrl" ? Mathf.Clamp01(eyes*maximum) : controller=="EyebrowCtrl" ? (dynamic?eyes:1) : 0;
                    int percent=(int)(amount*100);
                    foreach(var target in component.Data.Get(controller).Get("FBSTarget").Children)
                    {
                        var renderer=PartRenderers(entry.Key).OfType<SkinnedMeshRenderer>().FirstOrDefault(r=>r.name==target.String("ObjTarget"));
                        if(renderer==null || renderer.sharedMesh==null)continue;
                        var pattern=target.Get("PtnSet").At(0);
                        int close=(int)pattern.Float("Close",-1),open=(int)pattern.Float("Open",-1);
                        if(close>=0 && close<renderer.sharedMesh.blendShapeCount)renderer.SetBlendShapeWeight(close,100-percent);
                        if(open>=0 && open<renderer.sharedMesh.blendShapeCount)renderer.SetBlendShapeWeight(open,open==close?100:percent);
                    }
                }
            }
        }
        private void AccessoryMaterial(CharacterPart part,Renderer renderer,Material material)
        {
            var component=part.Components.FirstOrDefault(c=>c.Type=="CmpAccessory");
            if(component==null)return;
            // ChaControl.ChangeAccessoryColor writes renderer.material, which is
            // the first submesh material. Other materials retain their own values.
            if(renderer.sharedMaterial!=material)return;
            var data=component.Data;
            bool alpha=data.Get("rendAlpha").Children.Any(p=>p.Text==renderer.name);
            bool normal=data.Get("rendNormal").Children.Any(p=>p.Text==renderer.name);
            if(data.Float("typeHair")!=0 && normal)
            {
                SetHairShader(material,"accessory."+part.Slot);
                var values=part.Parameters.Get("colorInfo");
                for(int channel=0;channel<3;channel++)
                    if(data.Float("useColor0"+(channel+1))!=0)
                        material.SetColor(new[]{"_Color","_TopColor","_UnderColor"}[channel],ColorValue(values.At(channel).Get("color"),Color.white));
                material.SetColor("_Specular",ColorValue(values.At(3).Get("color"),Color.white));
                material.SetFloat("_Smoothness",values.At(0).Float("smoothnessPower"));
                material.SetFloat("_Metallic",values.At(0).Float("metallicPower"));
                return;
            }
            for(int channel=0;channel<4;channel++)
            {
                if(channel==3 ? !alpha : !normal || data.Float("useColor0"+(channel+1))==0)continue;
                var value=part.Parameters.Get("colorInfo").At(channel);
                var color=value.Has("color")?ColorValue(value.Get("color"),Color.white)
                    : part.Colors.Length>channel*4+3 ? new Color(part.Colors[channel*4],part.Colors[channel*4+1],part.Colors[channel*4+2],part.Colors[channel*4+3]) : Color.white;
                material.SetColor(channel==0||alpha?"_Color":"_Color"+(channel+1),color);
                material.SetFloat(alpha?"_Glossiness4":channel==0?"_Glossiness":"_Glossiness"+(channel+1),value.Float("glossPower"));
                material.SetFloat(alpha?"_Metallic4":channel==0?"_Metallic":"_Metallic"+(channel+1),value.Float("metallicPower"));
                if(alpha)renderer.gameObject.SetActive(color.a!=0);
            }
        }
        private void HairBones(CharacterPart part,Dictionary<string,Transform> nodes)
        {
            var component=part.Components.FirstOrDefault(c=>c.Type=="CmpHair");
            if(component==null)return;
            var bundle=definition.Hair.Get("parts").At(part.Slot).Get("dictBundle");
            var bones=component.Data.Get("boneInfo");
            for(int i=0;i<bones.Children.Length;i++)
            {
                if(!bundle.Has(i.ToString()))continue;
                var parameters=bundle.Get(i.ToString());var config=bones.At(i);
                Transform bone;if(!nodes.TryGetValue(config.String("trfCorrect"),out bone))continue;
                Vector3 Interpolate(string min,string max,string key)
                {
                    var a=config.Get(min);var b=config.Get(max);var t=parameters.Get(key);
                    float Value(int axis){float rate=Coordinate(t,axis);if(definition.Hair.Float("unlockedSliders")==0)rate=Mathf.Clamp01(rate);return Coordinate(a,axis)+(Coordinate(b,axis)-Coordinate(a,axis))*rate;}
                    return new Vector3(Value(0),Value(1),Value(2));
                }
                bone.localPosition=Interpolate("posMin","posMax","moveRate");
                bone.localEulerAngles=Interpolate("rotMin","rotMax","rotRate");
            }
        }
        private Texture2D Embedded(EmbeddedTextureReference reference)
        {
            if(reference==null)return null;
            string key=reference.Path+":"+reference.Offset+":"+reference.Length;
            Texture2D result;if(embeddedTextures.TryGetValue(key,out result))return result;
            if(reference.Length<1 || reference.Length>64*1024*1024 || reference.Offset<0)throw new IOException("内嵌贴图范围无效。");
            var bytes=new byte[reference.Length];
            using(var input=File.OpenRead(reference.Path))
            {
                if(reference.Offset>input.Length || reference.Length>input.Length-reference.Offset)throw new IOException("内嵌贴图已截断。");
                input.Position=reference.Offset;int read=0;while(read<bytes.Length){int count=input.Read(bytes,read,bytes.Length-read);if(count==0)throw new EndOfStreamException();read+=count;}
            }
            // Encoded image data only. No assembly loading or texture downsampling.
            result=Own(new Texture2D(2,2,TextureFormat.RGBA32,true));
            if(!result.LoadImage(bytes) || result.width>8192 || result.height>8192)throw new IOException("内嵌贴图无法解码或超过尺寸限制。");
            embeddedTextures.Add(key,result);return result;
        }
        private void ApplyExtensions(CharacterPart part,GameObject obj)
        {
            int type=part.Role=="clothes"?1:part.Role=="accessory"?2:part.Role=="hair"?3:4;
            if(part.Role=="rig" || part.Role=="headRig")return;
            if(part.Role=="body" && !definition.BodyVisible)
                foreach(var renderer in PartRenderers(part))renderer.enabled=false;
            var edits=definition.MaterialEdits.Where(e=>e.ObjectType==type && (type==4 || e.Slot==part.Slot)).ToArray();
            foreach(var renderer in PartRenderers(part))
            {
                foreach(var edit in edits.Where(e=>e.Kind=="renderer" && e.Name==renderer.name))
                {
                    if(edit.Property=="shadowCasting")renderer.shadowCastingMode=(UnityEngine.Rendering.ShadowCastingMode)(int)edit.Number;
                    else if(edit.Property=="receiveShadows")renderer.receiveShadows=edit.Number!=0;
                    else renderer.enabled=edit.Number!=0;
                }
                foreach(var material in renderer.sharedMaterials.Where(m=>m!=null))
                {
                    string name=material.name.Replace("(Instance)","").Trim();
                    foreach(var edit in edits.Where(e=>e.Kind!="renderer" && e.Name==name).OrderBy(e=>e.Kind=="shader"?0:1))
                    {
                        try
                        {
                            if(edit.Kind=="shader")
                            {
                                var source=(definition.Shaders??new ShaderDefinition[0]).FirstOrDefault(s=>s.Name==edit.Text);
                                var shader=source==null?assets.FindShader(edit.Text):assets.LoadShader(source);
                                if(shader==null || !shader.isSupported)throw new IOException("自定义 Shader 无法加载："+edit.Text);
                                material.shader=shader;
                                if(source!=null)
                                {
                                    if(source.RenderQueue>=0)material.renderQueue=source.RenderQueue;
                                    foreach(var value in source.Defaults)
                                    {
                                        if(value.Type=="Texture"&&value.Texture!=null)material.SetTexture(value.Property,assets.Load<Texture>(value.Texture));
                                        else if(value.Type=="Float"&&value.Values.Length==1)material.SetFloat(value.Property,value.Values[0]);
                                        else if(value.Type=="Color"&&value.Values.Length==4)material.SetColor(value.Property,new Color(value.Values[0],value.Values[1],value.Values[2],value.Values[3]));
                                    }
                                }
                                continue;
                            }
                            if(edit.Kind=="queue"){material.renderQueue=(int)edit.Number;continue;}
                            // Saved edits can outlive a shader change. MaterialEditor skips
                            // properties absent from the current shader; they have no effect.
                            if(!material.HasProperty(edit.Property))continue;
                            if(edit.Kind=="float")material.SetFloat(edit.Property,edit.Number);
                            else if(edit.Kind=="color")material.SetColor(edit.Property,new Color(edit.Values[0],edit.Values[1],edit.Values[2],edit.Values[3]));
                            else if(edit.Kind=="texture")
                            {
                                if(edit.Texture!=null)material.SetTexture(edit.Property,Embedded(edit.Texture));
                                if(edit.Offset!=null && edit.Offset.Length==2)material.SetTextureOffset(edit.Property,new Vector2(edit.Offset[0],edit.Offset[1]));
                                if(edit.Scale!=null && edit.Scale.Length==2)material.SetTextureScale(edit.Property,new Vector2(edit.Scale[0],edit.Scale[1]));
                            }
                        }
                        catch(Exception error){Diagnostics.Add("MaterialEditor "+name+": "+error.Message);}
                    }
                }
            }
        }
    }
}
