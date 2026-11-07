using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HS2Manager.Core;

internal static class NativeCompatibilityChecks
{
    public static async Task Run(string output)
    {
        string root = Path.Combine(output, "compatibility-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        CheckTextures(root);
        CheckEyes();
        CheckDiagnostics(root);
        CheckStaticEffects(root);
        await CheckMods(root);
    }
    private static void CheckEyes()
    {
        var mesh=new NativeMesh { Positions=[0,0,0, 2,2,2],Normals=[0,1,0, 0,1,0],
            EyeBlend=new(new([0],[0,-2,0],[0,0,0],100),new([0],[0,2,0],[0,0,0],100)) };
        UnityMeshDecoder.ApplyEyeBlend(mesh,.9791302f);
        if(Math.Abs(mesh.Positions[1]-1.88f)>.0001f || mesh.Positions[4]!=2) throw new Exception("Eye opening did not interpolate the sparse close/open shapes at saved integer percent");
        var invalid=new NativeBlendShape([2],[0,0,0],[0,0,0],100);
        if(invalid.IsValid(2)) throw new Exception("Out-of-range eye blend cache accepted");
        Console.WriteLine("PASS EyeControl sparse shape interpolation, percent quantization and invalid vertex rejection");
    }
    private static void CheckTextures(string root)
    {
        // Two rows distinguish PNG top/bottom orientation, BGRA channels, and alpha blending.
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0,0,255,128, 255,0,0,255 }, 4)));
        using var image = new MemoryStream(); encoder.Save(image); byte[] png = image.ToArray();
        var property = new Dictionary<string, object?>
        {
            ["ObjectType"] = 1L, ["Slot"] = 2L, ["CoordinateIndex"] = 0L, ["MaterialName"] = "fabric", ["Property"] = "MainTex", ["TexID"] = 7L,
            ["Offset"] = new List<object?> { .25f, .5f }, ["Scale"] = new List<object?> { 2f, 3f }
        };
        var extensions = new Dictionary<string, object?>
        {
            ["KSOX"] = Plugin(2, new() { ["_TextureID_7"] = png, ["Lookup"] = NumericMap(0, NumericMap(1, NativeAppearanceChecks.Pack(7L))) }),
            ["KCOX"] = Plugin(1, new() { ["Overlays"] = NumericMap(0, NativeAppearanceChecks.Pack(new Dictionary<string, object?> { ["coat"] = new List<object?> { png, true } })) }),
            ["com.deathweasel.bepinex.materialeditor"] = Plugin(0, new()
            {
                ["TextureDictionary"] = NumericMap(7, NativeAppearanceChecks.Pack(png)),
                ["MaterialTexturePropertyList"] = NativeAppearanceChecks.Pack(new List<object?> { property }),
                ["MaterialShaderList"] = NativeAppearanceChecks.Pack(new List<object?> { new Dictionary<string, object?>
                { ["ObjectType"] = 1L, ["Slot"] = 2L, ["CoordinateIndex"] = 0L, ["MaterialName"] = "fabric", ["ShaderName"] = null, ["RenderQueue"] = 3001L } })
            })
        };
        var read = Read(root, extensions);
        if (read.Warnings.Count != 0 || read.SkinOverlays.Count != 1 || read.ClothesOverlays.Count != 1 || !read.ClothesOverlays[0].Override)
            throw new Exception("Overlay/MaterialEditor data not parsed: " + string.Join(";", read.Warnings));
        var texture = read.SkinOverlays[0].Texture.Decode(default);
        if (texture.Width != 1 || texture.Height != 2 || !texture.Pixels.SequenceEqual(new byte[] { 0,0,255,128, 255,0,0,255 }))
            throw new Exception("Embedded PNG channels, orientation or alpha changed");
        if (!read.ClothesOverlays[0].Texture!.Decode(default).Pixels.SequenceEqual(texture.Pixels)) throw new Exception("KCOX texture range incorrect");
        var material = new NativeMaterial { Name = "fabric (Instance)", MainTexture = new() { Width=1,Height=2,Pixels=[255,255,255,255,255,255,255,255] } };
        var mesh = new NativeMesh { Materials = [material] };
        var appearance = new CardAppearance(1, PreviewColor.Skin, []) { MaterialTextures=read.MaterialTextures, MaterialQueues=read.MaterialQueues };
        NativeCardExtensions.ApplyMaterials([mesh], appearance, 1, 1, t => t.Decode(default));
        if (material.MainTexture.Pixels[0] != 255) throw new Exception("Material texture leaked into another slot");
        NativeCardExtensions.ApplyMaterials([mesh], appearance, 1, 2, t => t.Decode(default));
        if (!material.MainTexture.Pixels.SequenceEqual(texture.Pixels) || !material.UVTransform.SequenceEqual(new[] { 2f,3f,.25f,.5f }) || material.RenderQueue != 3001)
            throw new Exception("MaterialEditor texture/UV/queue not applied");
        var composed = NativeTextureComposer.Compose(new() { MainTexture = new() { Width=1,Height=2,Pixels=[255,255,255,255,255,255,255,255] },
            Layers = [new(texture,new(1,1,1),FullColor:true)] }, default)!;
        if (composed.Pixels[2] != 255 || Math.Abs(composed.Pixels[0] - 127) > 1 || composed.Pixels[4] != 255 || composed.Pixels[6] != 0)
            throw new Exception("Full-color overlay replaced color with a scalar mask or treated opaque blue as empty");
        var replaced = NativeTextureComposer.Compose(new() { Layers = [new(texture,new(1,1,1),FullColor:true,Replace:true)] },default)!;
        if (!replaced.Pixels.SequenceEqual(texture.Pixels)) throw new Exception("Replace overlay lost its alpha");
        var clothing=new NativeMesh { ClothingGroup=0, Materials=[new()] };
        NativeCardExtensions.ApplyClothesOverlays([clothing],[new("ct_clothesTop",read.ClothesOverlays[0].Texture,true)],1,"unrelated",t=>t.Decode(default),read.Warnings);
        if(clothing.Materials[0].Layers.Count!=0) throw new Exception("Clothing overlay leaked to the wrong slot");
        NativeCardExtensions.ApplyClothesOverlays([clothing],[new("ct_clothesTop",read.ClothesOverlays[0].Texture,true)],0,"unrelated",t=>t.Decode(default),read.Warnings);
        if(clothing.Materials[0].Layers.Count!=1) throw new Exception("KCOX runtime clothing names were mistaken for prefab names");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { read.SkinOverlays[0].Texture.Decode(cancel.Token); throw new Exception("Embedded decode ignored cancellation"); } catch (OperationCanceledException) { }

        // The payload is indexed, not allocated. It can be much larger than the parser budget.
        var big = new Dictionary<string, object?> { ["KSOX"] = Plugin(2, new() { ["_TextureID_7"] = new byte[40*1024*1024], ["Lookup"] = NumericMap(0,NumericMap(1,NativeAppearanceChecks.Pack(7L))) }) };
        string path = Path.Combine(root,"large-extension.bin"); File.WriteAllBytes(path,NativeAppearanceChecks.Pack(big));
        using (var stream=File.OpenRead(path))
        {
            long before=GC.GetTotalAllocatedBytes(true);var streaming=new NativeCardExtensions();streaming.Read(stream,stream.Length,new CardRecord{Path=path},default);
            if(GC.GetTotalAllocatedBytes(true)-before>1024*1024 || streaming.SkinOverlays.Count!=1) throw new Exception("Large supported overlay was allocated during metadata parsing");
        }
        Console.WriteLine("PASS KSOX/KCOX/MaterialEditor embedded PNGs, full-color alpha, replacement, UV/queue, slot isolation, streaming and cancellation");
    }
    private static void CheckDiagnostics(string root)
    {
        var inactive = new Dictionary<string, object?>
        {
            ["TestCharaCustomFunctionController"] = Plugin(0,new(){["id"]="identity"}),
            ["com.fairbair.hs2_boobsettings"] = Plugin(0,new(){["overridePhysics"]=false,["overrideGravity"]=false}),
            ["KK_InvisibleBody"] = Plugin(0,new(){["Visible"]=true}),
            ["KK_PregnancyPlus"] = Plugin(1,new(){["pluginVersion"]="7.8",["clothingOffsetVersion"]=0L}),
            ["mikke.pushUpAI"] = Plugin(3,new(){["ENABLE_PUSHUP"]=false,["TOP_ENABLE_PUSHUP"]=false}),
            ["orange.spork.additionalaccessorycontrolsplugin"] = Plugin(0,new(){["accessoryData"]=NativeAppearanceChecks.Pack(new List<object?>{new List<object?>{false,0L,-1L,false,"Saved name",false,new List<object?>(),null}}),["overrideData"]=NativeAppearanceChecks.Pack(new List<object?>{new List<object?>(),new List<object?>()})}),
            ["orange.spork.advikplugin"] = Plugin(0,new(){["ShoulderRotatorEnabled"]=false,["BreathingEnabled"]=false}),
            ["orange.spork.earwiggle"] = Plugin(0,new(){["EarWiggleEnabled"]=false}),
            ["marco.authordata"] = Plugin(1,new(){["Authors"]=new List<object?>{"Author"}}),
            ["orange.spork.outfitpainter"] = Plugin(0,new(){["OutfitPainterData"]=NativeAppearanceChecks.Pack(new List<object?>{new List<object?>{new List<object?>{1L,"1",new List<object?>{0f,0f,0f,1f},0f,0f,new List<object?>()}}})})
        };
        var read=Read(root,inactive);
        if(read.Warnings.Count!=0 || read.Handled.Count!=inactive.Count) throw new Exception("Inactive plugins reported as missing effects");
        inactive["mikke.pushUpAI"]=Plugin(3,new(){["ENABLE_PUSHUP"]=true});
        inactive["com.fairbair.hs2_boobsettings"]=Plugin(0,new(){["overridePhysics"]=true});
        inactive["KK_InvisibleBody"]=Plugin(0,new(){["Visible"]=false});
        inactive["KSOX"]=Plugin(2,new(){["Lookup"]=new byte[]{0xff}});
        inactive["com.deathweasel.bepinex.eyecontrol"]=Plugin(0,new(){["EyeOpenMax"]="invalid"});
        inactive["unknown.active.plugin"]=Plugin(1,new(){["enabled"]=true});
        read=Read(root,inactive);
        if(read.BodyVisible || !read.Warnings.Any(w=>w.Contains("PushUp")) || !read.Warnings.Any(w=>w.StartsWith("BoobSettings")) || !read.Warnings.Any(w=>w.StartsWith("KSOX")) || !read.Warnings.Any(w=>w.Contains("eyecontrol")) || read.Handled.Contains("unknown.active.plugin"))
            throw new Exception("Active/unknown/corrupt extensions silently hidden");
        Console.WriteLine("PASS default plugin data is quiet; enabled, unknown and corrupt effects retain actionable diagnostics");
    }
    private static void CheckStaticEffects(string root)
    {
        var data=new Dictionary<string,object?>{["ENABLE_PUSHUP"]=true,["LIFT"]=.6f,["PUSH_TOGETHER"]=.65f,["SQUEEZE"]=.6f,
            ["CENTER_NIPPLES"]=1f,["FLATTEN_NIPPLES"]=true,["HIDE_NIPPLES"]=true,["HIDE_ACCESSORIES"]=true,["CORSET"]=0f};
        var read=Read(root,new(){["mikke.pushUpAI"]=Plugin(3,data)});
        if(read.Warnings.Count!=0 || read.PushUp==null)throw new Exception("Valid PushUp data not read");
        string[] names=["cf_J_Mune00_t_L","cf_J_Mune00_d_L","cf_J_Mune00_d_R","cf_J_Mune01_t_L","cf_J_Mune_Nip02_s_L"];
        NativePrefab Rig()=>new(){Bones=names.ToDictionary(n=>n,_=>new NativeBone())};
        var rig=Rig();var body=Enumerable.Repeat(.5f,33).ToArray();
        if(!read.PushUp.Apply(rig,body,true,false,[]))throw new Exception("PushUp accessory hiding ignored");
        if(Math.Abs(rig.Bones[names[0]].Position[1]-.03f)>.00001f || Math.Abs(rig.Bones[names[1]].Position[0]-.075f)>.00001f ||
           Math.Abs(rig.Bones[names[2]].Position[0]+.075f)>.00001f || Math.Abs(rig.Bones[names[3]].Position[2]+.01f)>.00001f ||
           Math.Abs(rig.Bones[names[4]].Position[2]+.07f)>.00001f || Math.Abs(rig.Bones[names[4]].Scale[0]-.7f/1.3f)>.00001f)
            throw new Exception("PushUp lift, symmetric spacing, squeeze or flatten formula changed");
        rig=Rig();if(read.PushUp.Apply(rig,body,false,false,[]) || rig.Bones.Values.Any(b=>b.Position.Any(v=>v!=0)))throw new Exception("Missing garments applied PushUp");
        data["CENTER_NIPPLES"]=1.3f;if(Read(root,new(){["mikke.pushUpAI"]=Plugin(3,data)}).Warnings.Count!=0)throw new Exception("Unlocked PushUp slider rejected");
        var meshes=new List<NativeMesh>{new(){ObjectType=2,Slot=0},new(){ObjectType=2,Slot=1},new(){ObjectType=1,Slot=0}};
        var controls=new Dictionary<int,NativeAccessoryControl>{[0]=new("",false,"",[new(10,10)]),[1]=new("",false,"",[new(21,1)])};
        NativeCardExtensions.ApplyAccessoryVisibility(meshes,controls,new Dictionary<int,NativeAssetEntry>{[0]=new(240,1,"","","")});
        if(meshes.Any(m=>m.ObjectType==2&&m.Slot==0) || !meshes.Any(m=>m.ObjectType==2&&m.Slot==1) || !meshes.Any(m=>m.ObjectType==1))throw new Exception("Accessory ON/inverse rules leaked into clothing or used wrong slot");
        Console.WriteLine("PASS PushUp garment gating, lift/spacing/squeeze/flatten bone positions and accessory visibility rules");
    }
    private static async Task CheckMods(string root)
    {
        string game=Path.Combine(root,"game"), mods=Path.Combine(game,"mods"), cache=Path.Combine(root,"cache");Directory.CreateDirectory(mods);Directory.CreateDirectory(cache);
        string old=Archive("old.zipmod","old",10,"oldAsset");
        string good=Archive("a-good.zipmod","shared",20,"newAsset", "<migrationInfo><info guidOld=\"old\" guidNew=\"absent\" category=\"so_hair_b\" idOld=\"10\" idNew=\"99\"/><info guidOld=\"old\" category=\"so_hair_b\" idOld=\"10\" idNew=\"20\"/></migrationInfo>");
        Archive("z-other.zipmod","shared",30,"unrelated");
        var repo=new CardRepository(Path.Combine(root,"Data"));await new ModDependencyService(repo).ScanAsync(game,null,default);
        var resolver=new NativeModResolver(game,repo.DataPath,cache,null,default);
        var resolved=resolver.Resolve(new("old",10,300,"hair.parts[0].id"));
        if(resolved.Asset!="newAsset" || resolved.Archive!=good) throw new Exception("Symbolic migration, duplicate GUID or alternate migration branch ignored");
        if(resolver.Resolve(new("shared",20,300,"hair.parts[0].id")).Asset!="newAsset") throw new Exception("Only the last duplicate GUID archive was searched");
        if(resolver.FindSavedName(300,"newAsset")?.Asset!="newAsset" || resolver.FindSavedName(301,"newAsset")!=null)throw new Exception("Saved-name recovery ignored category or exact name");
        if(!File.ReadAllBytes(resolver.Bundle("shared/texture.unity3d",old)).SequenceEqual(new byte[]{1,2,3,4})) throw new Exception("Cross-archive resource not resolved");
        try { resolver.Bundle("../escape.unity3d",old);throw new Exception("Traversal accepted"); } catch(InvalidDataException){}
        Archive("installed-later.zipmod","later",40,"installedLater");
        if(resolver.Resolve(new("later",40,300,"hair.parts[0].id")).Asset!="installedLater") throw new Exception("Stale MOD index not refreshed");
        Archive("ambiguous.zipmod","ambiguous",41,"newAsset");resolver=new NativeModResolver(game,repo.DataPath,cache,null,default);resolver.Resolve(new("ambiguous",41,300,"hair.parts[0].id"));
        if(resolver.FindSavedName(300,"newAsset")!=null)throw new Exception("Ambiguous saved accessory name replaced the original MOD");
        try { resolver.Resolve(new("missing",40,300,"hair.parts[0].id"));throw new Exception("Missing MOD silently replaced"); } catch(FileNotFoundException){}
        Console.WriteLine("PASS named-category migrations, alternate targets, duplicate GUIDs, newly installed MODs, cross-archive bundles and traversal rejection");
        string Archive(string name,string guid,int slot,string asset,string migration="")
        {
            string path=Path.Combine(mods,name);using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
            using(var writer=new StreamWriter(zip.CreateEntry("manifest.xml").Open()))writer.Write($"<manifest schema-ver=\"1\"><guid>{guid}</guid><game>hs2</game>{migration}</manifest>");
            using(var writer=new StreamWriter(zip.CreateEntry("abdata/list/characustom/test.csv").Open()))writer.Write($"300\nID,MainAB,MainData,Name\n{slot},shared/texture.unity3d,{asset},{asset}\n");
            if(name=="a-good.zipmod")using(var stream=zip.CreateEntry("abdata/shared/texture.unity3d").Open())stream.Write([1,2,3,4]);
            return path;
        }
    }
    private static NativeCardExtensions Read(string root, Dictionary<string,object?> extensions)
    {
        string path=Path.Combine(root,Guid.NewGuid().ToString("N")+".bin");File.WriteAllBytes(path,NativeAppearanceChecks.Pack(extensions));
        using var stream=File.OpenRead(path);var read=new NativeCardExtensions();read.Read(stream,stream.Length,new CardRecord{Path=path},default);return read;
    }
    private static List<object?> Plugin(long version,Dictionary<string,object?> data)=>[version,data];
    private static byte[] NumericMap(byte key, byte[] value)=>[0x81,key,..value];
}
