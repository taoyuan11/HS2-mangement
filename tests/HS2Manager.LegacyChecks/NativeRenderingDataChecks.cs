using HS2Manager.Core;

internal static class NativeRenderingDataChecks
{
    public static void Run()
    {
        float[][] keys = [[10,0,0,350,0,0,1,1,1],[15,0,0,355,0,0,1.5f,1,1],[20,0,0,0,0,0,2,1,1]];
        if (NativeShapeRig.Interpolate(keys,"face",-.5f,0,true)!=5 || NativeShapeRig.Interpolate(keys,"face",1.5f,6,true)!=2.5f ||
            NativeShapeRig.Interpolate(keys,"face",1.5f,3,true)!=365 || NativeShapeRig.Interpolate(keys,"cf_s_Mouth",1.5f,3,true)!=0 ||
            NativeShapeRig.Interpolate(keys,"face",-.5f,0,false)!=10 || NativeShapeRig.Interpolate(keys,"face",.25f,0,true)!=12.5f)
            throw new Exception("Slider extrapolation lost position/scale, rotation wrap, protected rotations or stock limits.");
        Console.WriteLine("PASS unlocked shape sliders, angular wrap and stock limits");
        var colors = new List<object?>
        {
            Property(1, 0, "cloth", "Color", new List<object?> { .1f, .3f, .7f, 1f }),
            Property(1, 1, "cloth", "Color", new List<object?> { 1f, 0f, 0f, 1f }),
            Property(3, 0, "cloth", "Color", new List<object?> { 0f, 1f, 0f, 1f }),
            Property(1, 0, "cloth", "Color2", new List<object?> { .7f, .6f, .5f, 1f }),
            Property(1, 0, "cloth", "EmissionColor", new List<object?> { 2f, .1f, 0f, 1f })
        };
        var extension = new Dictionary<string, object?>
        {
            ["com.deathweasel.bepinex.materialeditor"] = new List<object?> { 1L, new Dictionary<string, object?>
            {
                ["MaterialColorPropertyList"] = NativeAppearanceChecks.Pack(colors),
                ["MaterialFloatPropertyList"] = NativeAppearanceChecks.Pack(new List<object?> { Property(1, 0, "cloth", "Cutoff", "0.7"),Property(1,0,"cloth","Glossiness","0.8"),
                    Property(1,0,"cloth","Metallic","0.6"),Property(1,0,"cloth","EmissionStrength","0.5"),Property(1,1,"cloth","Glossiness","0.1") }),
                ["TextureDictionary"] = new Dictionary<string, object?> { ["1"] = new byte[1024 * 1024] }
            } }
        };
        byte[] data = NativeAppearanceChecks.Pack(extension);
        using var stream = new MemoryStream(data); var reader = new NativeCardExtensions();
        reader.Read(stream, stream.Length, new CardRecord(), default);
        var appearance = new CardAppearance(1, PreviewColor.Skin, []) { MaterialColors = reader.MaterialColors, MaterialFloats = reader.MaterialFloats };
        var mesh = new NativeMesh { Materials = [new() { Name = "cloth (Instance)", Colors = [new(1,1,1)], ColorMask = new() { Width=1, Height=1, Pixels=[0,0,0,255] } }] };
        NativeCardExtensions.ApplyMaterials([mesh], appearance, 1, 0);
        if (mesh.Materials[0].Colors[0] != new PreviewColor(.1f,.3f,.7f) || mesh.Materials[0].Colors[1] != new PreviewColor(.7f,.6f,.5f) || mesh.Materials[0].AlphaCutoff != .7f)
            throw new Exception("MaterialEditor properties mixed between object types/slots or ignored palette/cutoff");
        if (mesh.Materials[0].Tint) throw new Exception("Material override disabled the clothing color mask");
        var lit=mesh.Materials[0];if(lit.Smoothness!=.8f || lit.Metallic!=.6f || lit.EmissionColor.R!=2 || lit.EmissionStrength!=.5f)throw new Exception("Material lighting lost HDR values or leaked across slots");
        Console.WriteLine("PASS MaterialEditor color/cutoff parsing, object/slot isolation and embedded texture skipping");

        var iris = new NativeTexture { Width=4, Height=1, Pixels=[255,0,32,255, 255,0,96,255, 255,0,176,255, 255,0,240,255] };
        var parameters = new Dictionary<string, object?> { ["pupilColor"] = new List<object?> { 1f,1f,1f,1f } };
        var plain = NativeTextureComposer.Eye(iris, parameters, default);
        int dark = (120 * 512 + 160) * 4, light = (120 * 512 + 352) * 4;
        if (plain.Pixels[light] - plain.Pixels[dark] < 50) throw new Exception("Iris radial shading flattened into a solid disc");
        var highlight = new NativeTexture { Width=1, Height=1, Pixels=[255,255,255,255] };
        var bright = NativeTextureComposer.Eye(iris, parameters, default, highlight: highlight,
            face: new() { ["hlColor"] = new List<object?> { 1f,1f,1f,1f } });
        if (bright.Pixels[dark] <= plain.Pixels[dark] + 60) throw new Exception("Saved eye highlight was ignored");
        var pupil = new NativeTexture { Width=2, Height=2, Pixels=[255,255,255,255, 255,255,255,255, 255,255,255,255, 255,255,255,255] };
        var narrow = NativeTextureComposer.Eye(iris, parameters, default, pupil);
        parameters["blackW"] = .95f;
        var wide = NativeTextureComposer.Eye(iris, parameters, default, pupil);
        if (narrow.Pixels.SequenceEqual(wide.Pixels)) throw new Exception("Saved pupil size was ignored");
        var alpha = NativeTextureComposer.Compose(new() { MainTexture=new(){Width=1,Height=1,Pixels=[255,255,255,128]}, Transparent=true },default)!;
        if (alpha.Pixels[3] != 128) throw new Exception("Coverage was quantized before texture filtering");
        Console.WriteLine("PASS iris detail, pupil size, saved highlights and continuous cutout coverage");

        static Dictionary<string, object?> Property(int type, int slot, string name, string property, object value) => new()
        { ["ObjectType"]=(long)type, ["CoordinateIndex"]=0L, ["Slot"]=(long)slot, ["MaterialName"]=name, ["Property"]=property, ["Value"]=value };
    }
}
