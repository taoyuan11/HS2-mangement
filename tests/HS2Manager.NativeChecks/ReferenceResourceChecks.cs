using AssetsTools.NET;
using AssetsTools.NET.Extra;
using HS2Manager.Core;

// Offline test fixture only: add our own bake shader to a disposable reference player.
internal static class ReferenceResourceChecks
{
    internal static void Inject(string resources,string bundlePath,string output)
    {
        if(Path.GetFullPath(resources).Equals(Path.GetFullPath(output),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Reference injection requires a separate output file.");
        var manager=new AssetsManager();
        try
        {
            var target=manager.LoadAssetsFile(resources,false);var globals=manager.LoadAssetsFile(Path.Combine(Path.GetDirectoryName(resources)!,"globalgamemanagers"),false);
            var bundle=manager.LoadBundleFile(bundlePath,true);var source=manager.LoadAssetsFileFromBundle(bundle,0,false);
            using var schema=typeof(CardParser).Assembly.GetManifestResourceStream("HS2Manager.Core.Resources.classdata.tpk")!;
            manager.LoadClassPackage(schema);manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);
            var managerInfo=globals.file.GetAssetsOfType(AssetClassID.ResourceManager).Single();var root=manager.GetBaseField(globals,managerInfo);var container=root["m_Container"]["Array"];
            long nextId=target.file.AssetInfos.Max(i=>i.PathId)+1;bool added=false;
            foreach(var info in source.file.GetAssetsOfType(AssetClassID.Shader))
            {
                var field=manager.GetBaseField(source,info);string name=field["m_ParsedForm"]["m_Name"].AsString;
                if(name!="Hidden/HS2Manager/ModelSceneBake")continue;
                var entry=container.Children.First(e=>e["first"].AsString.Equals("ModelExportBake",StringComparison.OrdinalIgnoreCase)).Clone();entry["first"].AsString="modelscenebake";
                entry["second"]["m_PathID"].AsLong=nextId;container.Children.Add(entry);
                var asset=AssetFileInfo.Create(target.file,nextId++,(int)AssetClassID.Shader,manager.ClassDatabase);
                asset.SetNewData(field);target.file.AssetInfos.Add(asset);added=true;
            }
            if(!added)throw new InvalidDataException("The reference bake shader is missing.");managerInfo.SetNewData(root);
            using(var writer=new AssetsFileWriter(output))target.file.Write(writer);
            using(var writer=new AssetsFileWriter(Path.Combine(Path.GetDirectoryName(output)!,"globalgamemanagers-patched")))globals.file.Write(writer);
        }
        finally{manager.UnloadAll();}
    }
}
