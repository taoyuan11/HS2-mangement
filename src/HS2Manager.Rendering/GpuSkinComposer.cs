using System.Numerics;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Rendering;

/// <summary>Original skin creation passes, evaluated with card parameters on the GPU.</summary>
public sealed class GpuSkinComposer(GpuMaterialComposer composer)
{
    public (GpuTexture Color, GpuTexture Detail) Compose(CharacterDefinition definition, bool face)
    {
        int before=face?4:3, after=face?2:1;
        string name=face?"face":"body"; int size=face?2048:4096;
        var color=composer.Material("template.create_skin_"+name);
        var detail=composer.Material("template.create_skin detail_"+name);
        color.SetColor("_Color",definition.Body.Get("skinColor"),Vector4.One);
        if(face)
        {
            var makeup=definition.Face.Get("makeup");
            foreach(var (layer,number) in new[]{("eyeshadow",11),("cheek",10),("lip",9)})
            {
                composer.SelectTexture(color,"_Texture"+number,"face."+layer+".AddTex");
                color.SetColor("_Color"+number,makeup.Get(layer+"Color"),Vector4.Zero);
                composer.SelectTexture(detail,"_Texture"+number,"face."+layer+".GlossTex");
                detail.Set("_Gloss"+number,makeup.Float(layer+"Gloss"));
            }
            composer.SelectTexture(color,"_Texture12","face.mole.AddTex");
            color.SetColor("_Color12",definition.Face.Get("moleColor"),Vector4.Zero);
            var mole=definition.Face.Get("moleLayout");
            color.Set("_Texture12UV",Lerp(5,1,C(mole,0)),Lerp(5,1,C(mole,1)),Lerp(.3f,-.3f,C(mole,2)),Lerp(.3f,-.3f,C(mole,3)));
        }
        else
        {
            composer.SelectTexture(color,"_Texture7","body.sunburn.AddTex");
            color.SetColor("_Color7",definition.Body.Get("sunburnColor"),Vector4.Zero);
        }
        var paints=(face?definition.Face.Get("makeup"):definition.Body).Get("paintInfo");
        for(int i=0;i<2;i++)
        {
            var paint=paints.At(i);string number=(i+5).ToString(), key=name+".paint."+i;
            composer.SelectTexture(color,"_Texture"+number,key+".AddTex");
            composer.SelectTexture(detail,"_Texture"+number,key+".GlossTex");
            color.SetColor("_Color"+number,paint.Get("color"),Vector4.Zero);
            detail.Set("_Gloss"+number,paint.Float("glossPower"));detail.Set("_Metallic"+number,paint.Float("metallicPower"));
            var layout=paint.Get("layout");
            if(face)
                foreach(var material in new[]{color,detail})
                {
                    material.Set("_Texture"+number+"UV",Lerp(10,1,C(layout,0)),Lerp(10,1,C(layout,1)),Lerp(.28f,-.3f,C(layout,2)),Lerp(.28f,-.3f,C(layout,3)));
                    material.Set("_Texture"+number+"Rotator",Lerp(1,-1,paint.Float("rotation")));
                }
            else if(paint.Has("resolvedLayout"))
            {
                var config=paint.Get("resolvedLayout");
                foreach(var material in new[]{color,detail})
                {
                    material.Set("_Texture"+number+"UV",Lerp(config.Float("CenterScale")+config.Float("AddScale"),config.Float("CenterScale")-config.Float("AddScale"),C(layout,0)),
                        Lerp(config.Float("CenterScale")+config.Float("AddScale"),config.Float("CenterScale")-config.Float("AddScale"),C(layout,1)),
                        Lerp(config.Float("CenterX")+config.Float("MoveX"),config.Float("CenterX")-config.Float("MoveX"),C(layout,2)),
                        Lerp(config.Float("CenterY")+config.Float("MoveY"),config.Float("CenterY")-config.Float("MoveY"),C(layout,3)));
                    material.Set("_Texture"+number+"Rotator",Lerp(1,-1,paint.Float("rotation")));
                }
            }
        }
        var main=ComposeLayers(composer.Texture(name+".skin.MainTex"),color);
        try { return (main,ComposeLayers(composer.Texture("template.black"+size),detail)); }
        catch { main.Dispose();throw; }
        GpuTexture ComposeLayers(GpuTexture source, GpuMaterialInstance material)
        {
            var temporary = new List<GpuTexture>();
            try
            {
                foreach (var overlay in definition.Overlays.Where(o => o.Type == before))
                { source = composer.Overlay(source, overlay, size); temporary.Add(source); }
                source = composer.Compose(source, material, size); temporary.Add(source);
                foreach (var overlay in definition.Overlays.Where(o => o.Type == after))
                { source = composer.Overlay(source, overlay, size); temporary.Add(source); }
                temporary.Remove(source); return source;
            }
            finally { foreach (var texture in temporary) texture.Dispose(); }
        }
    }
    private static float C(Parameter value,int axis) { string name=new[]{"x","y","z","w"}[axis];return value.Has(name)?value.Float(name):(float)value.At(axis).Number; }
    private static float Lerp(float a,float b,float t)=>a+(b-a)*Math.Clamp(t,0,1);
}
