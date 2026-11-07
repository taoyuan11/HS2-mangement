using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using HS2Manager.Core;

internal static class NativeAppearanceChecks
{
    public static async Task Run(string game, string output, string female, string? modSample)
    {
        var parser = new CardParser(); var service = new NativePreviewService();
        var card = parser.Parse(female);
        var baseline = await service.LoadAsync(game, output, card, null, default);
        string variant = Path.Combine(output,"shape-and-color-variant.png");
        Rewrite(female,variant,(name,index,map) =>
        {
            string? key = name == "Custom" ? index == 0 ? "shapeValueFace" : index == 1 ? "shapeValueBody" : null : null;
            if (key != null) foreach (int i in Enumerable.Range(0,CardAppearanceReader.List(map,key).Count)) CardAppearanceReader.List(map,key)[i] = .75f;
            if (name == "Coordinate" && index == 0)
                foreach (var part in CardAppearanceReader.List(map,"parts"))
                    foreach (var color in CardAppearanceReader.List(CardAppearanceReader.Map(part),"colorInfo"))
                        CardAppearanceReader.Map(color)["baseColor"] = new List<object?>{.85f,.05f,.1f,1f};
        });
        var changed = await service.LoadAsync(game, output, parser.Parse(variant), null, default);
        NativePreviewChecks.Validate(changed);
        foreach (string prefix in new[]{"o_body_","o_head/","clothes["})
        {
            var original = baseline.Meshes.Where(m=>m.Name.StartsWith(prefix,StringComparison.Ordinal)).ToArray();
            if (original.Length == 0) throw new Exception("No expected part "+prefix);
            if (!original.Any(m=>changed.Meshes.First(n=>n.Name==m.Name).Positions.Where((p,i)=>Math.Abs(p-m.Positions[i])>.0001f).Any())) throw new Exception("Shape values did not deform "+prefix);
        }
        var beforeColors = baseline.Meshes.Where(m=>m.Name.StartsWith("clothes[")).SelectMany(m=>m.Materials).SelectMany(m=>m.Colors);
        var afterColors = changed.Meshes.Where(m=>m.Name.StartsWith("clothes[")).SelectMany(m=>m.Materials).SelectMany(m=>m.Colors);
        if (beforeColors.SequenceEqual(afterColors)) throw new Exception("Clothing colors were not applied");
        var again = await service.LoadAsync(game,output,card,null,default);
        if (!baseline.Meshes.SelectMany(m=>m.Positions).SequenceEqual(again.Meshes.SelectMany(m=>m.Positions))) throw new Exception("Appearance leaked through cache");
        if (!baseline.Meshes.SelectMany(m=>m.Materials).Any(m=>m.MainTexture?.IsValid==true)) throw new Exception("No decoded textures");
        Console.WriteLine("PASS face/body/clothing deformation, texture data, card colors and cache isolation");

        var mask = new NativeTexture { Width=4, Height=1, Pixels=[0,0,0,255, 0,0,255,255, 0,255,0,255, 255,0,0,255] };
        var colored = NativeTextureComposer.Compose(new NativeMaterial { ColorMask=mask, Colors=[new(1,0,0),new(0,1,0),new(0,0,1),new(1,1,1)] },default)!;
        if (!colored.Pixels.SequenceEqual(new byte[]{0,0,255,255, 0,255,0,255, 255,0,0,255, 255,255,255,255})) throw new Exception("Black/R/G/B color mask mapping regressed");
        var cutout = NativeTextureComposer.Compose(new NativeMaterial { MainTexture=mask, MaskOnly=true, Transparent=true, Tint=true, Colors=[new(0,0,0)] },default)!;
        if (cutout.Pixels[3] != 0 || cutout.Pixels[7] != 255 || cutout.Pixels[4] != 0) throw new Exception("Scalar eyelash mask became colored or opaque");
        Console.WriteLine("PASS color mask channels and transparent scalar masks");
        var cleanSkin=NativeTextureComposer.Compose(new NativeMaterial
        {
            MainTexture=new(){Width=4,Height=4,Pixels=Enumerable.Repeat((byte)255,64).ToArray()},
            Layers=[new(new(){Width=1,Height=1,Pixels=[0,32,181,255]},new(0,0,0),Eyebrow:true)]
        },default)!;
        if(cleanSkin.Pixels.Any(p=>p!=255)) throw new Exception("Eyebrow shader data produced a rectangular patch on skin");
        Console.WriteLine("PASS eyebrow packed channels preserve uncovered skin");

        var rig = new NativePrefab { Bones = new() { ["root"]=new(), ["child"]=new(){Parent="root",Position=[0,1,0]} } };
        NativeCardExtensions.Apply(rig,[new("root",[2,2,2],1,[1,0,0],[0,0,0])]);
        var world = NativeShapeRig.World(rig,Matrix4x4.Identity);
        if (Vector3.Distance(Vector3.Transform(Vector3.Zero,world["child"]),new(1,2,0))>.0001f) throw new Exception("ABMX transform inheritance incorrect");
        rig.Bones["root"].Parent="child";
        try { NativeShapeRig.World(rig,Matrix4x4.Identity); throw new Exception("Cyclic bone graph accepted"); } catch(InvalidDataException) {}
        Console.WriteLine("PASS bone adjustments and cycle rejection");

        var largest = Directory.EnumerateFiles(Path.Combine(game,"UserData","chara"),"*.png",SearchOption.AllDirectories).Select(p=>new FileInfo(p)).MaxBy(f=>f.Length)!;
        var largeCard = parser.Parse(largest.FullName);
        long allocated = GC.GetTotalAllocatedBytes(true); _ = CardAppearanceReader.Read(largeCard); allocated = GC.GetTotalAllocatedBytes(true)-allocated;
        if (allocated > 32*1024*1024) throw new Exception("Large embedded textures were allocated by appearance reader");
        Console.WriteLine($"PASS large-card appearance streaming: {largest.Length/1048576d:F1} MB card / {allocated/1048576d:F2} MB allocated");

        if (modSample != null)
        {
            var hash = Hash(modSample); string copy = Path.Combine(output,"mod-sample.png"); File.Copy(modSample,copy);
            var modCard=parser.Parse(copy); var appearance=CardAppearanceReader.Read(modCard);
            if (!appearance.Parts.Any(p=>p.IsMod)) throw new Exception("MOD fixture has no resolvable references");
            var scene=await service.LoadAsync(game,output,modCard,null,default); NativePreviewChecks.Validate(scene);
            if (!scene.Meshes.Any(m=>m.Name.StartsWith("clothes[")) || !scene.Meshes.Any(m=>m.Name.StartsWith("accessory["))) throw new Exception("MOD clothing or accessories absent");
            foreach(var hidden in appearance.Renderers.Where(r=>!r.Enabled))
                if(scene.Meshes.Any(m=>m.Name.StartsWith(hidden.Name+"/"))) throw new Exception("Hidden renderer reappeared");
            if(Hash(modSample)!=hash) throw new Exception("MOD source was modified");
            Console.WriteLine($"PASS MOD GUID/Slot, clothes/accessories, {appearance.BoneModifiers.Count} bone modifiers, renderer overrides; {scene.Meshes.Sum(m=>m.Indices.Length/3)} triangles; {scene.Warnings.Count} diagnostics");
        }
    }
    private static string Hash(string path) { using var file=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }

    // Test-only card rewriting preserves every unmodified data block; no game files are written.
    private static void Rewrite(string source,string destination,Action<string,int,Dictionary<string,object?>> change)
    {
        using var input=File.OpenRead(source); CardParser.FindPngEnd(input); using var reader=new BinaryReader(input,Encoding.UTF8,true);
        reader.ReadInt32();reader.ReadString();reader.ReadString();reader.ReadInt32();reader.ReadString();reader.ReadString();
        int headerPosition=checked((int)input.Position), size=reader.ReadInt32(); long headerEnd=input.Position+size;
        var header=CardAppearanceReader.Map(new BoundedMessagePack(input,headerEnd).Read());input.Position=headerEnd;
        reader.ReadInt64();long start=input.Position; using var data=new MemoryStream();
        foreach(var item in CardAppearanceReader.List(header,"lstInfo"))
        {
            var block=CardAppearanceReader.Map(item); string name=CardAppearanceReader.Text(block,"name");
            input.Position=start+CardAppearanceReader.Integer(block,"pos"); byte[] bytes=reader.ReadBytes(checked((int)CardAppearanceReader.Integer(block,"size")));
            if(name is "Custom" or "Coordinate")
            {
                using var from=new MemoryStream(bytes);using var partReader=new BinaryReader(from);using var to=new MemoryStream();using var writer=new BinaryWriter(to,Encoding.UTF8,true);
                for(int i=0;i<(name=="Custom"?3:2);i++)
                {
                    long end=from.Position+4+partReader.ReadInt32();var map=CardAppearanceReader.Map(new BoundedMessagePack(from,end,4*1024*1024).Read());from.Position=end;change(name,i,map);
                    byte[] packed=Pack(map);writer.Write(packed.Length);writer.Write(packed);
                }
                from.CopyTo(to);bytes=to.ToArray();
            }
            block["pos"]=data.Position;block["size"]=(long)bytes.Length;data.Write(bytes);
        }
        input.Position=0;using var output=File.Create(destination);using var binary=new BinaryWriter(output);binary.Write(reader.ReadBytes(headerPosition));
        byte[] table=Pack(header);binary.Write(table.Length);binary.Write(table);binary.Write(data.Length);data.Position=0;data.CopyTo(output);
    }
    internal static byte[] Pack(object? value)
    {
        using var stream=new MemoryStream();Write(value);return stream.ToArray();
        void Number(byte prefix,ulong n,int count) {stream.WriteByte(prefix);for(int i=count-1;i>=0;i--)stream.WriteByte((byte)(n>>(8*i)));}
        void Write(object? item)
        {
            switch(item)
            {
                case null:stream.WriteByte(0xc0);break;
                case bool b:stream.WriteByte(b?(byte)0xc3:(byte)0xc2);break;
                case long n:Number(0xd3,unchecked((ulong)n),8);break;
                case float f:Number(0xca,BitConverter.SingleToUInt32Bits(f),4);break;
                case double d:Number(0xcb,unchecked((ulong)BitConverter.DoubleToInt64Bits(d)),8);break;
                case string s:byte[] text=Encoding.UTF8.GetBytes(s);Number(0xdb,(ulong)text.Length,4);stream.Write(text);break;
                case byte[] bytes:Number(0xc6,(ulong)bytes.Length,4);stream.Write(bytes);break;
                case List<object?> list:Number(0xdd,(ulong)list.Count,4);foreach(var child in list)Write(child);break;
                case Dictionary<string,object?> map:Number(0xdf,(ulong)map.Count,4);foreach(var pair in map){Write(pair.Key);Write(pair.Value);}break;
                default:throw new InvalidDataException("Unsupported fixture type");
            }
        }
    }
}
