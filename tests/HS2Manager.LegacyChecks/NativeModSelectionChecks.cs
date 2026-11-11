using System.IO.Compression;
using HS2Manager.Core;

internal static class NativeModSelectionChecks
{
    internal static async Task Run(string root)
    {
        ModFileInfo Mod(string path, string version, long modified) => new() { Path=path, Guid="same", Version=version, ModifiedUtcTicks=modified };
        void Winner(string expected, params ModFileInfo[] files)
        {
            if (NativeModSelection.Select(files).Single().Path != expected) throw new Exception("Sideloader duplicate selection differs: " + expected);
        }
        Winner("new", Mod("殷紫萍/old", "", 1), Mod("new", "", 2));
        Winner("new", Mod("high-version", "99.0", 1), Mod("new", "", 2));
        Winner("new-version", Mod("old", "1.9", 3), Mod("new-version", "1.10", 1));
        Winner("long/path", Mod("short", "1.0", 3), Mod("long/path", "1.0", 1));
        Winner("extended", Mod("short", "1.0", 3), Mod("extended", "1.0.0", 1));
        Winner("beta10", Mod("beta2", "1.0-beta-2", 3), Mod("beta10", "1.0-beta-10", 1));
        Winner("numeric", Mod("numeric", "1_10", 1), Mod("older", "1-9", 3));
        var invalid = Mod("invalid", "99", 99); invalid.Compatible=false;
        var corrupt = Mod("corrupt", "99", 99); corrupt.Error="bad manifest";
        Winner("valid", invalid, corrupt, Mod("valid", "1", 1));

        string game=Path.Combine(root,"duplicate-game"), mods=Path.Combine(game,"mods"), cache=Path.Combine(root,"duplicate-cache");
        Directory.CreateDirectory(mods); Directory.CreateDirectory(cache); Directory.CreateDirectory(Path.Combine(game,"BepInEx/plugins"));
        string old=Archive("long-old-name.zipmod","striped",true), current=Archive("new.zipmod","solid",false);
        File.SetLastWriteTimeUtc(old,new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(current,new DateTime(2021,1,1,0,0,0,DateTimeKind.Utc));
        var repository=new CardRepository(Path.Combine(root,"DuplicateData"));
        var service=new ModDependencyService(repository); await service.ScanAsync(game,null,default);
        var resolver=new NativeModResolver(game,repository.DataPath,cache,null,default);
        if(resolver.Resolve(new("same",101,245)).Asset!="solid" || resolver.InstalledMods().Single().Path!=current)
            throw new Exception("Old duplicate supplied pantyhose despite a newer unversioned archive");
        if(File.ReadAllText(resolver.Bundle("test/panst.unity3d"))!="solid" || File.ReadAllText(resolver.LooseTexture("test/panst.unity3d","mask")!)!="solid")
            throw new Exception("Bundle/PNG catalog disagrees with selected clothing list");
        if(resolver.FindSavedName(245,"striped")!=null || resolver.FindSavedName(245,"solid")?.Archive!=current)
            throw new Exception("Saved-name lookup searched a rejected duplicate");
        Missing(()=>resolver.Resolve(new("same",102,245)));
        Missing(()=>resolver.Bundle("test/old-only.unity3d"));
        if(resolver.LooseTexture("test/panst.unity3d","old-only")!=null)throw new Exception("Rejected duplicate PNG leaked into the catalog");
        Missing(()=>resolver.Resolve(new("legacy",101,245)));
        if(service.Diagnose(new CardRecord{Dependencies=[new("legacy",101,245)]},game).Single().State!=DependencyState.Missing)
            throw new Exception("Dependency diagnostics used a rejected duplicate migration");
        Console.WriteLine("PASS Sideloader duplicate versions, timestamps, path ties and active-only clothing/resources/migrations");

        string Archive(string name,string asset,bool rejected)
        {
            string path=Path.Combine(mods,name); using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
            Add("manifest.xml","<manifest schema-ver=\"1\"><guid>same</guid><game>hs2</game>"+
                (rejected?"<migrationInfo><info guidOld=\"legacy\" category=\"245\" idOld=\"101\" idNew=\"101\"/></migrationInfo>":"")+"</manifest>");
            Add("abdata/list/characustom/panst.csv",$"245\nID,MainAB,MainData,Name\n101,test/panst.unity3d,{asset},{asset}\n"+(rejected?"102,test/panst.unity3d,removed,removed\n":""));
            Add("abdata/test/panst.unity3d",asset); Add("abdata/test/panst/mask.png",asset);
            if(rejected){Add("abdata/test/old-only.unity3d",asset);Add("abdata/test/panst/old-only.png",asset);}
            return path;
            void Add(string entry,string contents){using var writer=new StreamWriter(zip.CreateEntry(entry).Open());writer.Write(contents);}
        }
        static void Missing(Action action)
        {
            try{action();throw new Exception("Rejected duplicate supplied a missing resource");}catch(FileNotFoundException){}
        }
    }
}
