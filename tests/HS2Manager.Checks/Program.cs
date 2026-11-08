using HS2Manager.Core;


try
{
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var gameArg = Array.IndexOf(args, "--game");
var game = gameArg >= 0 ? args[gameArg + 1] : @"Z:\games\hs2\HS two DX - TSYMQ";
var data = Path.Combine(root, "artifacts", "checks");
Directory.CreateDirectory(data);
if (args.Contains("--card-delete")) await CardDeletionChecks.Run(data);
else if (args.Contains("--preview-cache")) await PreviewCacheChecks.Run(data);
else if (args.Contains("--card-export")) await CardExportChecks.Run(data, args.Contains("--real-export") ? game : null);
else if (args.Contains("--audit-unity-compatibility")) await UnityCompatibilityAudit.Run(args[Array.IndexOf(args,"--catalog")+1],args[Array.IndexOf(args,"--player")+1],args[Array.IndexOf(args,"--output")+1]);
else if (args.Contains("--prepare-unity-catalog")) await UnityCatalogChecks.Prepare(game,data,args[Array.IndexOf(args,"--output")+1],args.Contains("--female-only"));
else if (args.Contains("--normalize-unity-catalog")) UnityCatalogChecks.Normalize(args[Array.IndexOf(args,"--catalog")+1],game,data,args[Array.IndexOf(args,"--output")+1]);
else if (args.Contains("--unity-bundle-identities")) UnityBundleIdentityChecks.Run(args[Array.IndexOf(args,"--definition")+1],Path.Combine(data,"cab-check-"+Guid.NewGuid().ToString("N")));
else if (args.Contains("--unity-session")) await UnitySessionChecks.Run(args[Array.IndexOf(args,"--player")+1],args[Array.IndexOf(args,"--definition")+1],Path.Combine(data,"unity-session"));
else if (args.Contains("--prepare-unity-character"))
{
    int cardIndex = Array.IndexOf(args, "--card");
    string cardPath = cardIndex >= 0 ? args[cardIndex + 1] : Directory.EnumerateFiles(Path.Combine(game,"UserData","chara","navi"),"*.png").First();
    var definition = await new UnityCharacterService().PrepareAsync(game,data,new CardParser().Parse(cardPath),new Progress<string>(Console.WriteLine),default);
    string target = Path.Combine(data,"unity-character.json");
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
