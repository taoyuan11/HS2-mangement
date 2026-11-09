using HS2Manager.Core;


try
{
var projectFolder = new DirectoryInfo(AppContext.BaseDirectory);
while (projectFolder != null && !File.Exists(Path.Combine(projectFolder.FullName, "HS2Manager.slnx"))) projectFolder = projectFolder.Parent;
var root = Environment.GetEnvironmentVariable("HS2MANAGER_TEST_ROOT") ?? projectFolder?.FullName ?? Environment.CurrentDirectory;
if (StoragePaths.IsNetwork(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HS2Manager", "Tests");
var gameArg = Array.IndexOf(args, "--game");
var game = gameArg >= 0 ? args[gameArg + 1] : @"Z:\games\hs2\HS two DX - TSYMQ";
var data = Path.Combine(root, "artifacts", "checks");
Directory.CreateDirectory(data);
if (args.Contains("--data-location")) { DataLocationChecks.Run(); return; }
if (args.Contains("--nas-files")) { await NasFileChecks.Run(args[Array.IndexOf(args, "--workspace") + 1], data); return; }
if (args.Contains("--model-export-lifecycle")) await ModelExportLifecycleChecks.Run(args[Array.IndexOf(args,"--player")+1],args[Array.IndexOf(args,"--definition")+1],args[Array.IndexOf(args,"--output")+1]);
else if (args.Contains("--model-export-catalog")) await ModelExportCatalogChecks.Run(args[Array.IndexOf(args,"--catalog")+1],args[Array.IndexOf(args,"--player")+1],args[Array.IndexOf(args,"--output")+1],args.Contains("--keep-assets"));
else if (args.Contains("--model-export")) await ModelExportChecks.Run(game,data,args[Array.IndexOf(args,"--player")+1],args[Array.IndexOf(args,"--output")+1],args.Contains("--card")?args[Array.IndexOf(args,"--card")+1]:null,args.Contains("--catalog")?args[Array.IndexOf(args,"--catalog")+1]:null,args.Contains("--count")?int.Parse(args[Array.IndexOf(args,"--count")+1]):1,args.Contains("--draft"),args.Contains("--clothing")?int.Parse(args[Array.IndexOf(args,"--clothing")+1]):0,!args.Contains("--no-accessories"));
else if (args.Contains("--prepare-edit-game")) await CharacterEditGameChecks.Prepare(game,args[Array.IndexOf(args,"--output")+1]);
else if (args.Contains("--edit-preview")) await CharacterEditPreviewChecks.Run(game,data,args[Array.IndexOf(args,"--player")+1]);
else if (args.Contains("--card-edit")) await CharacterEditChecks.Run(data, args.Contains("--real-edit") ? game : null);
else if (args.Contains("--card-delete")) await CardDeletionChecks.Run(data);
else if (args.Contains("--preview-cache")) await PreviewCacheChecks.Run(data);
else if (args.Contains("--card-export")) await CardExportChecks.Run(data, args.Contains("--real-export") ? game : null);
else if (args.Contains("--audit-unity-compatibility")) await UnityCompatibilityAudit.Run(args[Array.IndexOf(args,"--catalog")+1],args[Array.IndexOf(args,"--player")+1],args[Array.IndexOf(args,"--output")+1]);
else if (args.Contains("--prepare-unity-catalog")) await UnityCatalogChecks.Prepare(game,data,args[Array.IndexOf(args,"--output")+1],args.Contains("--female-only"),args.Contains("--catalog")?args[Array.IndexOf(args,"--catalog")+1]:null);
else if (args.Contains("--asset-overrides")) await NativeAssetOverrideChecks.Run(game,data);
else if (args.Contains("--body-selection"))
{
    var appearance=CardAppearanceReader.Read(new CardParser().Parse(args[Array.IndexOf(args,"--card")+1]));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {appearance.Parameter,appearance.Uncensor,appearance.PushUp,BoneCount=appearance.BoneModifiers.Count}));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(appearance.BoneModifiers.Where(b=>b.Name.Contains("Foot")||b.Name.Contains("Toes"))));
    var mods=new NativeModResolver(game,data,Path.Combine(data,"UnityPreview","definitions-v1"),null,default);
    var shoe=appearance.Parts.FirstOrDefault(p=>p.Category==247);if(shoe?.Reference!=null)Console.WriteLine("Shoe "+mods.Resolve(shoe.Reference)?.Archive);
    foreach(var bone in appearance.BoneModifiers)Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(bone));
    foreach(var entry in UnityBodyCatalog.Resolve(game,Path.Combine(data,"UnityPreview","definitions-v1"),mods,appearance,default))
    {
        Console.WriteLine(entry);
        var source=mods.AssetBundle(entry.Bundle,"additional_bones",entry.Archive);
        using var bundle=new NativeAssetBundle(source.Path);
        Console.WriteLine(string.Join(",",bundle.ExportNames().Where(n=>n.Contains("additional"))));
        if(bundle.HasExport("additional_bones"))Console.WriteLine(System.Text.Encoding.UTF8.GetString(bundle.ReadText("additional_bones")));
    }
}
else if (args.Contains("--native-curves")) NativeAssetOverrideChecks.Curves(args[Array.IndexOf(args,"--definition")+1]);
else if (args.Contains("--material-properties"))
{
    var definition=System.Text.Json.JsonSerializer.Deserialize<HS2Manager.Preview.Contracts.CharacterDefinition>(File.ReadAllText(args[Array.IndexOf(args,"--definition")+1]),UnityCharacterService.JsonOptions)!;
    var names=new Dictionary<string,HashSet<string>>{["Float"]=[],["Color"]=[],["Texture"]=[]};
    foreach(var path in definition.BundlePaths)
    {
        using var bundle=new NativeAssetBundle(path);
        foreach(var pair in bundle.MaterialPropertyNames())foreach(var name in pair.Value)names[pair.Key].Add(name);
    }
    File.WriteAllText(args[Array.IndexOf(args,"--output")+1],System.Text.Json.JsonSerializer.Serialize(names));
}
else if(args.Contains("--animation-dump"))
{
    using var bundle=new NativeAssetBundle(args[Array.IndexOf(args,"--bundle")+1]);
    bundle.DumpAnimationData(args[Array.IndexOf(args,"--output")+1]);
}
else if(args.Contains("--shader-dump"))
{
    using var bundle=new NativeAssetBundle(args[Array.IndexOf(args,"--bundle")+1]);
    bundle.DumpShaderData(args[Array.IndexOf(args,"--output")+1]);
}
else if(args.Contains("--material-values"))
{
    using var bundle=new NativeAssetBundle(args[Array.IndexOf(args,"--bundle")+1]);
    File.WriteAllText(args[Array.IndexOf(args,"--output")+1],System.Text.Json.JsonSerializer.Serialize(bundle.MaterialFloatValues()));
}
else if (args.Contains("--normalize-unity-catalog")) UnityCatalogChecks.Normalize(args[Array.IndexOf(args,"--catalog")+1],game,data,args[Array.IndexOf(args,"--output")+1]);
else if (args.Contains("--unity-bundle-identities")) UnityBundleIdentityChecks.Run(args[Array.IndexOf(args,"--definition")+1],Path.Combine(data,"cab-check-"+Guid.NewGuid().ToString("N")));
else if (args.Contains("--unity-session")) await UnitySessionChecks.Run(args[Array.IndexOf(args,"--player")+1],args[Array.IndexOf(args,"--definition")+1],Path.Combine(data,"unity-session"));
else if (args.Contains("--prepare-unity-character"))
{
    int cardIndex = Array.IndexOf(args, "--card");
    string cardPath = cardIndex >= 0 ? args[cardIndex + 1] : Directory.EnumerateFiles(Path.Combine(game,"UserData","chara","navi"),"*.png").First();
    var definition = await new UnityCharacterService().PrepareAsync(game,data,new CardParser().Parse(cardPath),new Progress<string>(Console.WriteLine),default);
    string target = Array.IndexOf(args,"--output") is int outputIndex&&outputIndex>=0?Path.GetFullPath(args[outputIndex+1]):Path.Combine(data,"unity-character.json");
    File.WriteAllText(target,System.Text.Json.JsonSerializer.Serialize(definition,UnityCharacterService.JsonOptions));
    Console.WriteLine($"Prepared {definition.Parts.Length} original parts and {definition.Selections.Length} selections: {target}");
    foreach (string warning in definition.Diagnostics) Console.WriteLine(warning);
}
else if (args.Contains("--prepare-unity-probe")) UnityResourceProbeChecks.Prepare(game, data, Array.IndexOf(args,"--mod-bundle") is int modBundle && modBundle>=0 ? args[modBundle+1] : null);
else if (args.Contains("--audit-preview")) await NativePreviewAudit.Run(game,data,Array.IndexOf(args,"--report") is int report && report>=0 ? args[report+1] : "female-audit",Array.IndexOf(args,"--file") is int only && only>=0 ? args[only+1] : null);
else if (args.Contains("--native-preview")) await NativePreviewChecks.Run(game, data, Array.IndexOf(args,"--card") is int sample && sample >= 0 ? args[sample+1] : null);
else if (args.Contains("--core")) await CoreChecks.Run(game, data);
else if (args.Contains("--scan-settings")) await ScanSettingsChecks.Run(data);
else if (args.Contains("--scan"))
{
    var repo = new CardRepository(Path.Combine(data, "library"));
    var library = new CardLibraryService(repo);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var report = await library.ScanAsync([Path.Combine(game, "UserData", "chara")], null, CancellationToken.None, recursive: true);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
    var cards = repo.All();
    Console.WriteLine($"Parsed {cards.Count} cards in {watch.Elapsed.TotalSeconds:F2}s; {cards.Sum(c => c.Dependencies.Count)} mod references");
    foreach (var group in cards.GroupBy(c => c.Status)) Console.WriteLine($"{group.Key}: {group.Count()}");
    foreach (var card in cards.Where(c => c.Status != CardStatus.Valid).Take(5)) Console.WriteLine(card.FileName + " " + card.Error);
    if (cards.Any(c => c.Status == CardStatus.Valid && c.Sex is not (0 or 1))) throw new Exception("Invalid sex parsed");
}
else if (args.Contains("--mods"))
{
    var repo = new CardRepository(Path.Combine(data, "library")); var mods = new ModDependencyService(repo);
    var watch = System.Diagnostics.Stopwatch.StartNew(); var index = await mods.ScanAsync(game, null, CancellationToken.None);
    Console.WriteLine($"Indexed {index.Files.Count} archives, {index.Files.Count(x => x.Compatible && x.Guid.Length > 0)} compatible manifests, {index.Plugins.Count} plugins in {watch.Elapsed.TotalSeconds:F2}s; complete={index.Complete}");
    foreach (var warning in index.Warnings.Take(5)) Console.WriteLine(warning);
    var results = repo.All().Where(c => c.Status == CardStatus.Valid).SelectMany(c => mods.Diagnose(c, game)).ToList();
    foreach (var group in results.GroupBy(x => x.State)) Console.WriteLine($"{group.Key}: {group.Count()}");
    Console.WriteLine($"Partial dependency parses: {repo.All().Count(x => !x.DependenciesComplete)}");
}
else if (args.Contains("--preview")) throw new NotSupportedException("Studio 桥接已禁用。使用 --prepare-unity-probe 验证独立 Unity 资源。");
else Console.WriteLine("Use --core, --scan, --mods, --native-preview or --prepare-unity-probe. Optional: --game <game directory>.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
