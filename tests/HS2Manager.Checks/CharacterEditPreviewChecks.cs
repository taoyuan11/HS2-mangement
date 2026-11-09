using System.Diagnostics;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

internal static class CharacterEditPreviewChecks
{
    internal static async Task Run(string game,string data,string player)
    {
        string output=Path.Combine(data,"edit-preview-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        Console.WriteLine("Editing preview checks: "+output);
        var service=new UnityCharacterService();using var kernel=new UnityPreviewSession();
        var repository=new CardRepository(Path.Combine(output,"Data"));var saves=new CardEditService(repository);
        var catalog=new CharacterAssetCatalog(game,repository.DataPath);await catalog.LoadAsync(null,default);
        string copy=Path.Combine(output,"female.png");File.Copy(Path.Combine(game,"UserData","chara","navi","navi.png"),copy);
        var card=new CardParser().Parse(copy);card.Root=output;repository.Save(card);
        var draft=new CharacterEditSession(card,CharacterDocument.Open(copy));int pid=0;long load=100;
        try
        {
            await kernel.StartAsync(player,repository.DataPath,default);pid=kernel.ProcessId;
            await Apply(false);kernel.Send(new(){Kind="Resize",Width=640,Height=480});kernel.Send(new(){Kind="Play",Playing=false});
            var first=await Frame();
            var previousIds=Objects(load);
            draft.SetValue("body.shapeValueBody[0]",.9f);await Apply();var shape=await Frame();
            Check(shape.Pixels.Where((b,i)=>i%4!=3&&Math.Abs(b-first.Pixels[i])>5).Count()>500,"shape changes rendered pixels");
            Check(Objects(load).All(p=>previousIds[p.Key]==p.Value),"shape retains all part objects");
            Check(!Metrics(load).GetProperty("MaterialsRefreshed").GetBoolean(),"shape reuses composed textures");
            draft.SetColor("hair.parts[0].baseColor",[.9f,.1f,.1f,1]);await Apply();var color=await Frame();
            Check(color.Pixels.Where((b,i)=>i%4!=3&&Math.Abs(b-shape.Pixels[i])>5).Count()>100,"hair color changes rendered pixels");
            Check(Objects(load).All(p=>previousIds[p.Key]==p.Value),"color retains all part objects");
            var original=catalog.Selected(draft.Document,"hair.parts[0].id");
            var stock=catalog.Query(300).First(a=>!a.IsMod&&a.Entry.Bundle.Length>0&&a.Entry.Asset is not ("" or "0" or "p_dummy")&&a.Key!=original?.Key);
            draft.Apply(await catalog.SelectAsync(draft.Document,"hair.parts[0].id",stock,default));await Apply();await Frame();
            var changed=Objects(load);
            Check(changed["hair:0"]!=previousIds["hair:0"]&&changed["body:0"]==previousIds["body:0"],"hair selection rebuilds hair and retains body");
            var mod=original?.IsMod==true?original:catalog.Query(300,source:2).First(a=>a.Entry.Bundle.Length>0&&a.Entry.Asset is not ("" or "0" or "p_dummy"));
            draft.Apply(await catalog.SelectAsync(draft.Document,"hair.parts[0].id",mod,default));await Apply();await Frame();
            var saved=await saves.SaveAsync(draft.Snapshot(),draft.Card,false);
            Check(new CardParser().Parse(saved.Card.Path).Dependencies.Any(r=>r.Guid==mod.Guid&&r.Property=="ChaFileHair.HairBack"),"saved MOD resolver matches selected hair");
            File.WriteAllText(Path.Combine(output,"saved-female.txt"),saved.Card.Path);
            // The model source is a retained immutable document; changing the source file
            // after opening must neither corrupt the draft nor get reread for preview.
            File.AppendAllText(copy,"external-change");draft.SetValue("body.shapeValueBody[0]",.72f);await Apply();await Frame();
            try{await saves.SaveAsync(draft.Snapshot(),draft.Card,true);throw new Exception("External write overwritten");}catch(IOException){}
            // Supersede an actual in-progress material update, then observe only the final revision.
            draft.SetColor("body.skinColor",[.8f,.6f,.4f,1]);var obsolete=await Prepare();
            using(var cancel=new CancellationTokenSource())
            {
                long oldLoad=++load;bool reached=false;
                void Progress(KernelMessage m){if(m.Load==oldLoad&&m.Text.StartsWith("更新材质")){reached=true;cancel.Cancel();}}
                kernel.Progress+=Progress;
                try{try{await kernel.UpdateAsync(obsolete,oldLoad,draft.Revision,true,cancel.Token);throw new Exception("Update cancellation ignored");}catch(OperationCanceledException){}Check(reached,"cancel reached native update");}
                finally{kernel.Progress-=Progress;}
            }
            draft.SetColor("body.skinColor",[.95f,.85f,.8f,1]);await Apply();var latest=await Frame();Check(latest.Revision==draft.Revision,"obsolete update cannot publish latest frame");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,SourceReadFromDraft=true,LastRevision=latest.Revision,SelectedMod=mod.Guid,SavedCard=saved.Card.Path}));
            Console.WriteLine("PASS editing preview: shape/color pixels, object/texture reuse, MOD-stock-MOD, revisions, superseded material updates, source isolation, save reference");
        }
        finally{kernel.Dispose();service.ReleaseEditSources();if(pid>0&&Running(pid))throw new Exception("Editor preview process leaked: "+pid);}
        async Task<string> Prepare()
        {
            var definition=await service.PrepareAsync(game,repository.DataPath,draft.Card,draft.Snapshot(),null,default,load+1);
            string path=Path.Combine(output,"revision-"+draft.Revision+".json");File.WriteAllText(path,JsonSerializer.Serialize(definition,UnityCharacterService.JsonOptions));return path;
        }
        async Task Apply(bool update=true)
        {
            var path=await Prepare();var result=await kernel.UpdateAsync(path,++load,draft.Revision,update,default);
            Check(result.Revision==draft.Revision,"acknowledgment revision");
            if(!update){var b=result.Values;kernel.Send(new(){Kind="Camera",Load=load,Values=[b[0],b[1],b[2]+35,b[0],b[1],b[2],35]});}
        }
        async Task<UnityFrame> Frame()
        {
            await Task.Delay(300);var deadline=DateTime.UtcNow.AddSeconds(20);
            while(DateTime.UtcNow<deadline)
            {var frame=kernel.ReadFrame();if(frame?.Revision==draft.Revision&&frame.Width==640&&frame.Height==480)return frame;await Task.Delay(30);}
            throw new TimeoutException("Missing current edit frame");
        }
        JsonElement Metrics(long generation)
        {
            foreach(string line in File.ReadLines(Path.Combine(repository.DataPath,"Logs","preview-performance.jsonl")).Reverse())
            {using var json=JsonDocument.Parse(line);var entry=json.RootElement;if(entry.GetProperty("Kind").GetString()=="kernel"&&entry.GetProperty("Load").GetInt64()==generation)return entry.GetProperty("Stages").Clone();}
            throw new Exception("Missing metrics");
        }
        Dictionary<string,int> Objects(long generation)
        {var metrics=Metrics(generation);return metrics.GetProperty("PartKeys").EnumerateArray().Select(p=>p.GetString()!).Zip(metrics.GetProperty("PartObjectIds").EnumerateArray().Select(p=>p.GetInt32())).ToDictionary(p=>p.First,p=>p.Second);}
        static void Check(bool value,string label){if(!value)throw new Exception("Editing preview failed: "+label);}
    }
    private static bool Running(int pid){try{using var p=Process.GetProcessById(pid);return !p.HasExited;}catch(ArgumentException){return false;}}
}
