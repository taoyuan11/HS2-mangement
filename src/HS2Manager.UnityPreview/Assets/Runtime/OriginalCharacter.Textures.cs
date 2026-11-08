using System;
using System.Linq;
using HS2Manager.Preview.Contracts;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    public sealed partial class OriginalCharacter
    {
        private static float Coordinate(Parameter value, int axis)
        {
            string key = new[] { "x", "y", "z", "w" }[axis];
            return value.Has(key) ? value.Float(key) : (float)value.At(axis).Number;
        }

        private void SkinPaint(Material color, Material detail, bool face)
        {
            var paints = (face ? definition.Face.Get("makeup") : definition.Body).Get("paintInfo");
            for (int i = 0; i < 2; i++)
            {
                var paint = paints.At(i);
                string number = (i + 5).ToString(), key = (face ? "face" : "body") + ".paint." + i;
                SetTexture(color, "_Texture" + number, key + ".AddTex");
                SetTexture(detail, "_Texture" + number, key + ".GlossTex");
                color.SetColor("_Color" + number, ColorValue(paint.Get("color"), Color.clear));
                detail.SetFloat("_Gloss" + number, paint.Float("glossPower"));
                detail.SetFloat("_Metallic" + number, paint.Float("metallicPower"));
                if (face)
                {
                    var layout = paint.Get("layout");
                    var uv = new Vector4(Mathf.Lerp(10,1,Coordinate(layout,0)),Mathf.Lerp(10,1,Coordinate(layout,1)),Mathf.Lerp(.28f,-.3f,Coordinate(layout,2)),Mathf.Lerp(.28f,-.3f,Coordinate(layout,3)));
                    foreach (var material in new[] { color, detail })
                    {
                        material.SetVector("_Texture" + number + "UV", uv);
                        material.SetFloat("_Texture" + number + "Rotator", Mathf.Lerp(1,-1,paint.Float("rotation")));
                    }
                }
                else if(paint.Has("resolvedLayout"))
                {
                    var config=paint.Get("resolvedLayout");var layout=paint.Get("layout");
                    var uv=new Vector4(Mathf.Lerp(config.Float("CenterScale")+config.Float("AddScale"),config.Float("CenterScale")-config.Float("AddScale"),Coordinate(layout,0)),Mathf.Lerp(config.Float("CenterScale")+config.Float("AddScale"),config.Float("CenterScale")-config.Float("AddScale"),Coordinate(layout,1)),Mathf.Lerp(config.Float("CenterX")+config.Float("MoveX"),config.Float("CenterX")-config.Float("MoveX"),Coordinate(layout,2)),Mathf.Lerp(config.Float("CenterY")+config.Float("MoveY"),config.Float("CenterY")-config.Float("MoveY"),Coordinate(layout,3)));
                    foreach(var material in new[]{color,detail}){material.SetVector("_Texture"+number+"UV",uv);material.SetFloat("_Texture"+number+"Rotator",Mathf.Lerp(1,-1,paint.Float("rotation")));}
                }
            }
        }

        private void Clothes(CharacterPart part, GameObject obj)
        {
            var component = part.Components.FirstOrDefault(c => c.Type == "CmpClothes");
            if (component == null) return;
            var config = component.Data;
            var values = definition.Clothes.Get("parts").At(part.Slot);
            string baseKey="clothes."+part.Slot+".";
            if(!selections.ContainsKey(baseKey+"MainTex")||!selections.ContainsKey(baseKey+"ColorMaskTex"))return;
            try {Selected<Texture>(baseKey+"MainTex");Selected<Texture>(baseKey+"ColorMaskTex");}
            catch(System.IO.IOException error){Diagnostics.Add("服装纹理合成未启用："+error.Message);return;}
            for (int group = 0; group < 3; group++)
            {
                string prefix = "clothes." + part.Slot + ".";
                string mainKey = prefix + (group == 0 ? "MainTex" : "MainTex0" + (group + 1));
                if (!selections.ContainsKey(mainKey)) continue;
                Texture main;
                try {main=Selected<Texture>(mainKey);}
                catch(System.IO.IOException error){Diagnostics.Add(mainKey+"："+error.Message);continue;}
                var creator = Own(new Material(Selected<Material>("template.create_clothes")));
                var detail = Own(new Material(Selected<Material>("template.create_clothes detail")));
                string maskKey = prefix + (group == 0 ? "ColorMaskTex" : "ColorMask0" + (group + 1) + "Tex");
                SetTexture(creator, "_ColorMask", maskKey);
                SetTexture(detail, "_ColorMask", maskKey);
                var scale = config.Get("uvScalePattern");
                creator.SetVector("_UVScalePattern", new Vector4(Coordinate(scale,0),Coordinate(scale,1),Coordinate(scale,2),Coordinate(scale,3)));
                for (int i = 0; i < 3; i++)
                {
                    var value = values.Get("colorInfo").At(i);
                    var color = ColorValue(value.Get("baseColor"), Color.white);
                    if (config.Float("useColorA0" + (i + 1)) == 0) color.a = 1;
                    creator.SetColor(i == 0 ? "_Color" : "_Color" + (i+1), color);
                    creator.SetColor("_Color" + (i+1) + "_2", ColorValue(value.Get("patternColor"), Color.white));
                    var layout = value.Get("layout");
                    creator.SetVector("_patternuv" + (i+1), new Vector4(Mathf.Lerp(20,1,Coordinate(layout,0)),Mathf.Lerp(20,1,Coordinate(layout,1)),Mathf.Lerp(-1,1,Coordinate(layout,2)),Mathf.Lerp(-1,1,Coordinate(layout,3))));
                    creator.SetFloat("_patternuv" + (i+1) + "Rotator", Mathf.Lerp(-1,1,value.Float("rotation")));
                    string pattern = prefix + "pattern." + i + ".MainTex";
                    creator.SetTexture("_PatternMask" + (i+1), selections.ContainsKey(pattern) ? Selected<Texture>(pattern) : null);
                    detail.SetFloat(i == 0 ? "_Glossiness" : "_Glossiness" + (i+1), value.Float("glossPower"));
                    detail.SetFloat(i == 0 ? "_Metallic" : "_Metallic" + (i+1), value.Float("metallicPower"));
                }
                creator.SetColor("_Color4", ColorValue(config.Get("defMainColor04"), Color.white));
                detail.SetFloat("_Glossiness4",config.Float("defGloss04"));
                detail.SetFloat("_Metallic4",config.Float("defMetallic04"));
                var diffuse = Compose(main,creator,main.width,main.height);
                var properties = Compose(Texture2D.blackTexture,detail,main.width,main.height);
                var names = config.Get("rendNormal0" + (group+1)).Children.Select(p => p.Text).ToArray();
                foreach (var renderer in PartRenderers(part).Where(r => names.Contains(r.name)))
                {
                    var material = renderer.sharedMaterial;
                    if (material == null) continue;
                    material.SetTexture("_MainTex",diffuse);
                    material.SetTexture("_DetailMainTex",properties);
                    if (material.HasProperty("_AlphaEx")) material.SetFloat("_AlphaEx",1-values.Float("breakRate"));
                }
            }
            ClothesOverlay(part,config);
        }
    }
}
