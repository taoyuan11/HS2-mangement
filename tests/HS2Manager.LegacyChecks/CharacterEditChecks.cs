using System.Security.Cryptography;
using System.Text;
using HS2Manager.Core;

internal static class CharacterEditChecks
{
    internal static async Task Run(string data, string? game)
    {
        string folder = Path.Combine(data, "edit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "原卡.png"); var bytes = Fixture(); File.WriteAllBytes(source, bytes);
        var document = CharacterDocument.Open(source);
        Check(document.ToBytes().SequenceEqual(bytes), "unmodified document preserves all bytes");
        var changed = document.WithValue("body.shapeValueBody[0]", .73).WithValue("hair.parts[0].dictBundle{7}.moveRate[1]", .45);
        var loaded = CharacterDocument.FromBytes(changed.ToBytes(), source);
        Check((float)loaded.GetValue("body.shapeValueBody[0]")! == .73f, "body value roundtrip");
        Check((float)loaded.GetValue("hair.parts[0].dictBundle{7}.moveRate[1]")! == .45f, "integer map keys roundtrip");
        Check(loaded.Cover.Span.SequenceEqual(document.Cover.Span), "PNG bytes unchanged");
        Check(loaded.Node("body.unknown").Raw.Span.SequenceEqual(document.Node("body.unknown").Raw.Span), "unknown extension marker and payload unchanged");
        Check(loaded.Extensions!.Raw.Span.SequenceEqual(document.Extensions!.Raw.Span), "unrelated plugin block unchanged");
        Check(loaded.ToBytes().AsSpan()[^9..].SequenceEqual(bytes.AsSpan()[^9..]), "opaque file trailer preserved");
        Check(changed.WithValue("body.shapeValueBody[0]", .5f).WithValue("hair.parts[0].dictBundle{7}.moveRate[1]", .5f).ToBytes().SequenceEqual(bytes), "returning values to baseline restores original bytes");
        Throws(() => document.WithValue("body.shapeValueBody[0]", double.NaN), "reject NaN");
        Throws(() => CharacterDocument.FromBytes(bytes[..^15], source), "reject truncated card");
        var unknown = EditableMessagePack.Parse(new byte[] { 0x82, 0xcf, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xd4, 0xff, 0xab, 0xa1, 0x78, 0xcb, 0x3f, 0xf0, 0, 0, 0, 0, 0, 0 });
        Check(unknown.Set("x", unknown.Require("x").WithScalar(2d)).Require(ulong.MaxValue).ToBytes().SequenceEqual(new byte[] { 0xd4, 0xff, 0xab }), "uint64 key and signed extension tag preserved");
        foreach(var (number,tag) in new (long,byte)[]{(42,42),(128,0xcc),(300,0xcd),(100000,0xce),(-1,0xff),(-33,0xd0),(-300,0xd1),(int.MinValue,0xd2),(long.MinValue,0xd3)})
            Check(EditableMessagePack.Scalar(number).ToBytes()[0]==tag,"typed game integer compatibility: "+number);

        var card = new CardParser().Parse(source); card.Favorite = true; card.Tags = ["测试"]; card.Notes = "保留备注"; card.Collection = "编辑测试"; card.Root = folder;
        var session = new CharacterEditSession(card, document);
        session.BeginGesture(); session.SetValue("body.shapeValueBody[0]", .6f); session.SetValue("body.shapeValueBody[0]", .8f); session.EndGesture();
        session.Undo(); Check(!session.IsDirty && session.CanRedo && !session.CanUndo, "gesture is one undo step");
        session.Redo(); Check(session.IsDirty, "redo"); session.Restore("body.shapeValueBody[0]"); Check(!session.IsDirty, "restore field");
        var asset = new CharacterAsset(300, 42, "测试头发", "fixture.mod", "测试 MOD", new(300, 42, "", "", ""));
        var mod = CharacterAssetCatalog.Select(document, "hair.parts[0].id", asset);
        string modPath = Path.Combine(folder, "mod.png"); File.WriteAllBytes(modPath, mod.ToBytes());
        var parsedMod = new CardParser().Parse(modPath);
        Check(parsedMod.Status == CardStatus.Valid && parsedMod.Dependencies.Single().Guid == "fixture.mod" && parsedMod.Dependencies.Single().Property == "ChaFileHair.HairBack", "MOD reference roundtrip");
        Check(mod.Extensions!.Require("unknown.plugin").Raw.Span.SequenceEqual(document.Extensions!.Require("unknown.plugin").Raw.Span), "other plugin values untouched");
        var stock = CharacterAssetCatalog.Select(mod, "hair.parts[0].id", asset with { Id = 2, Guid = "" });
        File.WriteAllBytes(modPath, stock.ToBytes()); Check(new CardParser().Parse(modPath).Dependencies.Count == 0, "stock selection removes old MOD reference");
        var modSession = new CharacterEditSession(card, document); modSession.Apply(mod); modSession.Restore("hair.parts[0].id");
        Check(!modSession.IsDirty, "restoring resource restores its resolver entry");

        var repository = new CardRepository(Path.Combine(folder, "Data")); repository.Save(card); var service = new CardEditService(repository);
        session.SetValue("body.shapeValueBody[0]", .83f);
        var saved = await service.SaveAsync(session.Snapshot(), card, false);
        Check(File.ReadAllBytes(source).SequenceEqual(bytes), "save-as leaves source unchanged");
        Check(saved.Card.Id != card.Id && saved.Card.Favorite && saved.Card.Notes == card.Notes && saved.Card.Tags.SequenceEqual(card.Tags), "save-as inherits metadata with new identity");
        Check(saved.Card.FileName == "原卡-编辑.png" && saved.Document.GetValue("body.shapeValueBody[0]") is float f && f == .83f, "save-as output");
        var saved2 = await service.SaveAsync(session.Snapshot(), card, false); Check(saved2.Card.FileName == "原卡-编辑 (2).png", "save-as collision naming");
        var overwritten = await service.SaveAsync(session.Snapshot(), card, true);
        Check(overwritten.Card.Id == card.Id && File.ReadAllBytes(overwritten.BackupPath).SequenceEqual(bytes), "overwrite preserves identity and verified original backup");
        var operation = new CardFileService(repository).Operations().Single(o => o.Id == overwritten.OperationId);
        var restored = await service.RestoreBackupAsync(operation);
        Check(File.ReadAllBytes(source).SequenceEqual(bytes) && restored.Card.Id == card.Id, "backup restoration");
        var beforeExternal = CharacterDocument.Open(source); File.AppendAllText(source, "external");
        await ThrowsAsync(() => service.SaveAsync(new(beforeExternal, 1), restored.Card, true), "refuse externally modified source");
        Check(File.ReadAllBytes(source).AsSpan()[^8..].SequenceEqual(Encoding.UTF8.GetBytes("external")), "external edits not overwritten");
        var rescued=await service.SaveAsync(new(beforeExternal,1),restored.Card,false);
        Check(File.ReadAllBytes(rescued.Card.Path).SequenceEqual(beforeExternal.ToBytes())&&File.ReadAllBytes(source).AsSpan()[^8..].SequenceEqual(Encoding.UTF8.GetBytes("external")),"save-as rescues draft after external source edits");
        File.WriteAllBytes(source, bytes);
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await ThrowsAsync(() => service.SaveAsync(new(document, 1), card, true), "file lock");
            var unlockedCopy=await service.SaveAsync(new(document,1),card,false);
            Check(File.ReadAllBytes(unlockedCopy.Card.Path).SequenceEqual(bytes),"save-as does not reread locked source");
        }
        using (var canceled = new CancellationTokenSource())
        { canceled.Cancel(); await ThrowsAsync(() => service.SaveAsync(new(document, 1), card, false, cancellation: canceled.Token), "save cancellation"); }
        Check(!Directory.EnumerateFiles(folder, ".hs2manager-*", SearchOption.TopDirectoryOnly).Any(), "temporary files cleaned");
        var completed = new CardFileService(repository).Operations().Single(o => o.Id == saved.OperationId);
        completed.Phase = "Committed"; repository.Remove(saved.Card.Id); AtomicFile.WriteJson(Path.Combine(repository.DataPath, "Operations", completed.Id + ".json"), completed);
        var recovery = await new CardFileService(repository).RecoverAsync(default);
        Check(repository.FindById(saved.Card.Id) != null && recovery.Count > 0, "committed save recovery indexes without rewriting");
        Check(File.ReadAllBytes(source).SequenceEqual(bytes), "recovery does not overwrite original");

        if (game != null)
        {
            var paths = Directory.EnumerateFiles(Path.Combine(game, "UserData", "chara", "female"), "*.png", SearchOption.TopDirectoryOnly)
                .Concat(new[] { Path.Combine(game, "UserData", "chara", "navi", "navi.png"), Path.Combine(game, "UserData", "chara", "male", "HS2_ill_M_000.png") }).ToArray();
            int tested = 0;
            var coverage=new Dictionary<string,int>();
            var schema=new Dictionary<string,CharacterField>();
            foreach (string path in paths)
            {
                var real = CharacterDocument.Open(path);
                Check(Convert.ToHexString(SHA256.HashData(real.ToBytes())) == real.SourceHash, "real lossless " + Path.GetFileName(path));
                float value = Convert.ToSingle(real.GetValue("body.shapeValueBody[0]"));
                var edit = real.WithValue("body.shapeValueBody[0]", value + .01f); var roundtrip = CharacterDocument.FromBytes(edit.ToBytes(), path);
                Check(roundtrip.Cover.Span.SequenceEqual(real.Cover.Span) && (float)roundtrip.GetValue("body.shapeValueBody[0]")! == value + .01f, "real edited roundtrip " + Path.GetFileName(path));
                // Exercise every control's serialized location, including nested maps,
                // Unity color/vector arrays and every original accessory slot.
                var allFields=CharacterEditSchema.Build(real,CharacterSliderLimits.Read(game));
                var allEdited=real;
                foreach(var field in allFields)
                {
                    coverage[field.Path]=coverage.GetValueOrDefault(field.Path)+1;
                    schema[field.Path]=field;
                    if(field.Kind==CharacterFieldKind.Color)
                    {
                        for(int channel=0;channel<real.Node(field.Path).Count;channel++)
                            allEdited=allEdited.WithValue(field.Path+$"[{channel}]",.123f+channel*.1f);
                    }
                    else if(field.Kind==CharacterFieldKind.Number)
                        allEdited=allEdited.WithValue(field.Path,field.Minimum+(field.Maximum-field.Minimum)*.37);
                    else if(field.Kind==CharacterFieldKind.Toggle)allEdited=allEdited.WithValue(field.Path,!(bool)real.GetValue(field.Path)!);
                    else if(field.Kind==CharacterFieldKind.Choice)allEdited=allEdited.WithValue(field.Path,field.Choices![0].Value);
                    else if(field.Kind==CharacterFieldKind.Asset)allEdited=allEdited.WithValue(field.Path,123L);
                }
                var allRead=CharacterDocument.FromBytes(allEdited.ToBytes(),path);
                foreach(var field in allFields)Check(allRead.Node(field.Path).ContentEquals(allEdited.Node(field.Path)),"field roundtrip "+field.Path);
                Check(allRead.Cover.Span.SequenceEqual(real.Cover.Span),"all fields cover preservation");
                if(real.Extensions!=null)Check(allRead.Extensions!.Raw.Span.SequenceEqual(real.Extensions.Raw.Span),"all fields preserve plugins");
                tested++;
            }
            File.WriteAllText(Path.Combine(folder,"field-coverage.json"),System.Text.Json.JsonSerializer.Serialize(coverage,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(Path.Combine(folder,"field-schema.csv"),"Path,Category,Group,Label,Kind,Minimum,Maximum,DisplayScale,CardsVerified\n"+
                string.Join("\n",schema.Values.OrderBy(f=>f.Path,StringComparer.Ordinal).Select(f=>string.Join(",",new object[]{f.Path,f.Category,f.Group,f.Label,f.Kind,f.Minimum,f.Maximum,f.DisplayScale,coverage[f.Path]}.Select(v=>"\""+Convert.ToString(v,System.Globalization.CultureInfo.InvariantCulture)!.Replace("\"","\"\"")+"\"")))),new UTF8Encoding(true));
            Console.WriteLine($"Editable field paths verified: {coverage.Count}");
            Console.WriteLine($"Real card lossless/edited roundtrips: {tested}");
        }
        Console.WriteLine("Character editing checks passed: " + folder);
    }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception("Character edit check failed: " + label); }
    private static void Throws(Action action, string label)
    { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException) { return; } throw new Exception("Expected failure: " + label); }
    private static async Task ThrowsAsync(Func<Task> action, string label)
    { try { await action(); } catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or OperationCanceledException) { return; } throw new Exception("Expected failure: " + label); }
    internal static byte[] Fixture()
    {
        EditableMessagePack Scalar(object? v) => EditableMessagePack.Scalar(v);
        EditableMessagePack Vector() => EditableMessagePack.Array(Scalar(.5f), Scalar(.5f), Scalar(.5f));
        var face = EditableMessagePack.Map(("shapeValueFace", Vector()), ("headId", Scalar(1)), ("version", Scalar("1.0.0")));
        var body = EditableMessagePack.Map(("shapeValueBody", Vector()), ("skinColor", EditableMessagePack.Array(Scalar(.8f), Scalar(.7f), Scalar(.6f), Scalar(1f))),
            ("unknown", EditableMessagePack.Parse(new byte[] { 0xd6, 0xf7, 0, 255, 128, 1 })));
        var hair = EditableMessagePack.Map(("parts", EditableMessagePack.Array(EditableMessagePack.Map(("id", Scalar(2)),
            ("dictBundle", EditableMessagePack.Map((7, EditableMessagePack.Map(("moveRate", Vector())))))))));
        var clothes = EditableMessagePack.Map(("parts", EditableMessagePack.Array()));
        var accessory = EditableMessagePack.Map(("parts", EditableMessagePack.Array()));
        var parameter = EditableMessagePack.Map(("fullname", Scalar("测试人物")), ("sex", Scalar(1)));
        var extras = EditableMessagePack.Map(("unknown.plugin", EditableMessagePack.Array(Scalar(7), EditableMessagePack.Map(("blob", EditableMessagePack.Binary(new byte[] { 0, 255, 3, 1, 129 }))))));
        byte[] Parts(params EditableMessagePack[] parts)
        { using var ms = new MemoryStream(); using var writer = new BinaryWriter(ms); foreach (var part in parts) { var p = part.ToBytes(); writer.Write(p.Length); writer.Write(p); } writer.Write(new byte[] { 201, 202, 203 }); return ms.ToArray(); }
        var blocks = new (string Name, byte[] Data)[] { ("Custom", Parts(face, body, hair)), ("UnknownBlock", new byte[] { 255, 3, 201 }), ("Coordinate", Parts(clothes, accessory)), ("Parameter", parameter.ToBytes()), ("KKEx", extras.ToBytes()) };
        long position = 2; var rows = new List<EditableMessagePack>();
        foreach (var block in blocks)
        { rows.Add(EditableMessagePack.Map(("name", Scalar(block.Name)), ("version", Scalar("1.0.0")), ("pos", Scalar(position)), ("size", Scalar(block.Data.Length)))); position += block.Data.Length + 1; }
        var table = EditableMessagePack.Map(("lstInfo", EditableMessagePack.Array(rows.ToArray())), ("unknown", Scalar("保留"))).ToBytes();
        using var output = new MemoryStream(); using var binary = new BinaryWriter(output);
        binary.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9xkAAAAASUVORK5CYII="));
        binary.Write(100); binary.Write("【AIS_Chara】"); binary.Write("1.0.0"); binary.Write(0); binary.Write(""); binary.Write("");
        binary.Write(table.Length); binary.Write(table); binary.Write(position); binary.Write(new byte[] { 100, 101 });
        foreach (var block in blocks) { binary.Write(block.Data); binary.Write((byte)230); }
        binary.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }); return output.ToArray();
    }
}
