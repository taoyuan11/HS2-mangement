using System.Globalization;
using System.Text.Json;
using HS2Manager.Core;

internal static class CharacterEditGameChecks
{
    internal static async Task Prepare(string game,string output)
    {
        if(Directory.Exists(output))throw new IOException("Use a fresh edited-card fixture directory.");
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var repository=new CardRepository(Path.Combine(output,"Data"));var saves=new CardEditService(repository);
        var catalog=new CharacterAssetCatalog(game,repository.DataPath);await catalog.LoadAsync(null,default);
        var entries=new List<UnityCatalogChecks.Entry>();
        var sources=new[]{Path.Combine(game,"UserData/chara/navi/navi.png"),Path.Combine(game,"UserData/chara/male/HS2_ill_M_000.png"),Directory.EnumerateFiles(Path.Combine(game,"UserData/chara/female"),"*.png").Order(StringComparer.OrdinalIgnoreCase).First()};
        for(int i=0;i<sources.Length;i++)
        {
            string source=Path.Combine(output,"source-"+i+".png");File.Copy(sources[i],source);
            var card=new CardParser().Parse(source);card.Root=output;repository.Save(card);
            var draft=new CharacterEditSession(card,CharacterDocument.Open(source));
            var changed=new HashSet<string>();var resources=new List<object>();
            foreach(var pair in new[]{("body.shapeValueBody[0]",.73f),("body.shapeValueBody[12]",.48f),("face.shapeValueFace[0]",.43f),("face.shapeValueFace[20]",.57f),("body.skinGlossPower",.37f),("face.detailPower",.39f),("face.pupilY",.46f),("hair.parts[0].smoothness",.62f),("clothes.parts[0].colorInfo[0].glossPower",.41f)})
            {draft.SetValue(pair.Item1,pair.Item2);changed.Add(pair.Item1);}
            Color("hair.parts[0].baseColor",[.35f,.21f,.17f,1]);Color("face.eyebrowColor",[.3f,.19f,.15f,1]);Color("clothes.parts[0].colorInfo[0].baseColor",[.18f,.28f,.38f,1]);
            var originalHair=catalog.Selected(draft.Document,"hair.parts[0].id");
            var hair=i!=1?(originalHair?.IsMod==true?originalHair:Pick(300,true)):Pick(300,false);
            await Select("hair.parts[0].id","hair",0,hair);
            await Select("clothes.parts[0].id","clothes",0,Pick(draft.Document.Sex==0?140:240,false));
            draft.SetValue("accessory.parts[0].type",351L);changed.Add("accessory.parts[0].type");
            await Select("accessory.parts[0].id","accessory",0,Pick(351,false));
            draft.SetValue("accessory.parts[0].parentKey","N_Head");changed.Add("accessory.parts[0].parentKey");
            draft.SetValue("accessory.parts[0].addMove[2][0][0]",1.4f);changed.Add("accessory.parts[0].addMove[2][0][0]");
            draft.SetValue("accessory.parts[0].addMove[2][1][1]",15f);changed.Add("accessory.parts[0].addMove[2][1][1]");
            var saved=await saves.SaveAsync(draft.Snapshot(),card,false);
            if(!saved.Document.Cover.Span.SequenceEqual(draft.Document.Cover.Span))throw new Exception("Game fixture cover changed");
            var values=changed.Select(path=>
            {
                object? value=draft.Document.GetValue(path);bool text=value is string or bool;
                return new{Path=path,Kind=text?value is bool?"bool":"text":"number",Text=text?Convert.ToString(value,CultureInfo.InvariantCulture):null,Number=text?0:Convert.ToDouble(value,CultureInfo.InvariantCulture)};
            }).ToArray();
            File.WriteAllText(saved.Card.Path+".edit-expectations.json",JsonSerializer.Serialize(new{Values=values,Assets=resources},new JsonSerializerOptions{WriteIndented=true}));
            var definition=await new UnityCharacterService().PrepareAsync(game,repository.DataPath,saved.Card,null,default);
            string path=Path.Combine(output,"edited-"+i+".json");File.WriteAllText(path,JsonSerializer.Serialize(definition,UnityCharacterService.JsonOptions));
            entries.Add(new("edited-"+i,saved.Card.Path,saved.Card.Name,saved.Card.Sex,saved.Document.SourceHash,path,definition.Diagnostics,null));
            Console.WriteLine($"Prepared game edit {i}: {values.Length} parameters, {resources.Count} resources");
            CharacterAsset Pick(int category,bool mod)=>catalog.Query(category,source:mod?2:1).First(a=>a.Entry.Bundle.Length>0&&a.Entry.Asset is not ("" or "0" or "p_dummy"));
            void Color(string field,float[] value){draft.SetColor(field,value);for(int channel=0;channel<4;channel++)changed.Add(field+"["+channel+"]");}
            async Task Select(string field,string role,int slot,CharacterAsset asset)
            {draft.Apply(await catalog.SelectAsync(draft.Document,field,asset,default));resources.Add(new{Role=role,Slot=slot,Bundle=asset.Entry.Bundle,Name=asset.Entry.Asset});}
        }
        File.WriteAllText(Path.Combine(output,"catalog.json"),JsonSerializer.Serialize(entries,new JsonSerializerOptions{WriteIndented=true}));
    }
}
