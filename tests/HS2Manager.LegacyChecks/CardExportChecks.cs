using System.Security.Cryptography;
using HS2Manager.Core;
using static CardExportFixtures;

internal static class CardExportChecks
{
    internal static async Task Run(string output, string? realGame = null)
    {
        string sandbox = Path.Combine(output, "card-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(sandbox);
        int passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
        try
        {
            string game = Path.Combine(sandbox, "游戏"), destination = Path.Combine(sandbox, "导出");
            string plugins = Path.Combine(game, "BepInEx", "plugins"), mods = Path.Combine(game, "mods");
            Directory.CreateDirectory(mods); Directory.CreateDirectory(plugins);
            Plugin(Path.Combine(plugins, "Save.dll"), [Save]);
            Plugin(Path.Combine(plugins, "Sideloader.dll"), [Sideloader], [new(Save)]);
            Plugin(Path.Combine(plugins, "API.dll"), ["marco.kkapi"], [new(Save, "1.0")]);
            string abmx = Plugin(Path.Combine(plugins, "ABMX", "ABMX.dll"), ["KKABMX.Core"], [new("marco.kkapi"), new("optional.absent", Optional: true)], ["Helper"], inherited: true);
            Plugin(Path.Combine(plugins, "Helper.dll"), [], references: ["ABMX"]); // A cycle must not duplicate files or recurse forever.
            Plugin(Path.Combine(plugins, "Overlays.dll"), ["KSOX", "KCOX"], [new("marco.kkapi")]);
            Plugin(Path.Combine(plugins, "MaterialEditor.dll"), [MaterialEditor], [new(Sideloader)], embeddedShaders: "<Shaders><Shader Name=\"Fixture/Builtin\" /></Shaders>");
            File.WriteAllText(Path.Combine(plugins, "ABMX", "lookup.json"), "fixture resource");
            File.WriteAllText(Path.Combine(plugins, "ABMX", "settings.cfg"), "do not export");
            File.WriteAllText(Path.Combine(plugins, "ABMX", "settings.json"), "do not export JSON settings");
            string shared = Mod(Path.Combine(mods, "common.zipmod"), "fixture.common", "2.0", "",
                ("abdata/list/characustom/example.csv", "245\nID,MainAB,MainData\n42,shared/other.unity3d,fixture\n"));
            string rejected = Mod(Path.Combine(mods, "common-old.zipmod"), "fixture.common", "1.0");
            string provider = Mod(Path.Combine(mods, "provider.zipmod"), "fixture.provider", entries: [("abdata/shared/other.unity3d", "resource")]);
            string mapping = Mod(Path.Combine(mods, "migration.zipmod"), "fixture.migrations", manifest:
                "<migrationInfo><info migrationType=\"MigrateAll\" guidOld=\"fixture.old\" guidNew=\"fixture.common\" /><info migrationType=\"StripAll\" guidOld=\"fixture.strip\" /></migrationInfo>");
            string shader = Mod(Path.Combine(mods, "shader.zipmod"), "fixture.shader", manifest: "<MaterialEditor><Shader Name=\"Fixture/Custom\" AssetBundle=\"shader/custom.unity3d\" /></MaterialEditor>", entries: [("abdata/shader/custom.unity3d", "shader")]);
            string overlay = Path.Combine(game, "UserData", "overlay", "共享.png"); Directory.CreateDirectory(Path.GetDirectoryName(overlay)!); File.WriteAllText(overlay, "external texture");
            var extensions = Map((Resolver, References("fixture.old", "fixture.strip")), ("KKABMPlugin.ABMData", Extension(2, [])),
                ("KSOX", Extension(2, Map(("TexturePath", "UserData/overlay/共享.png")))), ("KCOX", Extension(1, [])),
                (MaterialEditor, Extension(1, Map(("MaterialShaderList", Pack(new[] { Map(("ShaderName", "Fixture/Custom")), Map(("ShaderName", "Fixture/Builtin")) }))))));
            var female = Card(Path.Combine(sandbox, "外部A", "人物.png"), "角色/同名. ", 1, extensions);
            var male = Card(Path.Combine(sandbox, "外部B", "人物.png"), "角色/同名. ", 0, extensions);
            var repo = new CardRepository(Path.Combine(sandbox, "Data")); repo.Save(female); repo.Save(male);
            string originalHash = Hash(female.Path); var service = new CardExportService(repo);
            var plan = await service.BuildPlanAsync([female, male], game, destination, CardExportMode.PerCard, null, default);
            Check(plan.Packages.Select(p => p.FolderName).SequenceEqual(["角色_同名", "角色_同名 (2)"]), "Folders use sanitized internal names, not PNG filenames");
            Check(plan.Packages.All(p => p.Issues.Count == 0), "Supported extension and plugin dependency graph resolves without unknowns: " + string.Join("; ", plan.Packages.SelectMany(p => p.Issues).Select(i => i.Message)));
            Check(plan.Packages.All(p => p.Files.Any(f => f.Source == shared) && p.Files.Any(f => f.Source == mapping) && p.Files.Any(f => f.Source == provider) && p.Files.Any(f => f.Source == shader) && p.Files.All(f => f.Source != rejected)), "Migration owners, effective MOD versions, shared bundles and shader providers included");
            Check(plan.Packages.All(p => p.Files.Count(f => f.Source.EndsWith("Overlays.dll")) == 1 && p.Files.Any(f => f.Source.EndsWith("Helper.dll")) && p.Files.Any(f => f.Source.EndsWith("lookup.json")) && p.Files.All(f => !f.Source.EndsWith(".cfg") && !f.Source.EndsWith("settings.json"))), "Plugin aliases, inherited requirements, cycles, companion resources and config exclusion");
            var report = await service.ExportAsync(plan, null, default);
            Check(report.Status == CardExportStatus.Complete && report.ExportedCards == 2 && report.OutputFolders.Count == 2, "Independent export succeeds for both genders");
            Check(report.Files.All(item => item.Success && Hash(item.File.Source) == Hash(Path.Combine(item.Package, item.File.RelativePath))), "Every exported card, archive, plugin and resource matches its source hash");
            Check(File.Exists(Path.Combine(report.OutputFolders[0], "UserData/chara/female/人物.png")) && File.Exists(Path.Combine(report.OutputFolders[1], "UserData/chara/male/人物.png")), "Outside-library cards map to playable gender folders");
            Check(repo.FindById(female.Id)!.Path == female.Path && new CardFileService(repo).Operations().All(o => o.Kind == "Export" && o.Card == null && o.Phase == "Indexed"), "Resource exports journal copies without moving database records");
            Check(report.OutputFolders.All(p => File.ReadAllText(Path.Combine(p, "导出清单.txt")).Contains("[成功]")) && File.Exists(report.ReportPath), "Readable per-package manifests and overall report written");

            var mergedPlan = await service.BuildPlanAsync([female, male, female], game, destination, CardExportMode.Merged, null, default);
            Check(mergedPlan.Packages.Count == 1 && mergedPlan.RequestedCards == 2 && mergedPlan.Packages[0].FolderName == "角色卡合并导出", "Merged export has one fixed root and snapshots unique selections");
            Check(mergedPlan.Packages[0].Files.Count(f => f.Source == shared) == 1 && mergedPlan.Packages[0].Files.Single(f => f.Source == shared).CardIds.Count == 2, "Shared MOD copied once and attributed to both cards");
            var mergedReport = await service.ExportAsync(mergedPlan, null, default);
            Check(mergedReport.Status == CardExportStatus.Complete && Directory.GetDirectories(mergedReport.OutputFolders.Single()).Select(Path.GetFileName).ToHashSet().SetEquals(["UserData", "mods", "BepInEx"]), "Merged package directly exposes game folders");
            var secondFemale = Card(Path.Combine(sandbox, "外部C", "人物.png"), "第二人物", 1);
            var filenamePlan = await service.BuildPlanAsync([female, secondFemale], game, destination, CardExportMode.Merged, null, default);
            Check(filenamePlan.Packages[0].FolderName == "角色卡合并导出 (2)" && filenamePlan.Packages[0].Files.Any(f => f.RelativePath.EndsWith("人物 (2).png")), "Existing output and duplicate PNG filenames preserve both copies");
            Directory.CreateDirectory(Path.Combine(destination, filenamePlan.Packages[0].FolderName));
            string sentinel = Path.Combine(destination, filenamePlan.Packages[0].FolderName, "keep.txt"); File.WriteAllText(sentinel, "keep");
            var collisionReport = await service.ExportAsync(filenamePlan, null, default);
            Check(File.ReadAllText(sentinel) == "keep" && collisionReport.OutputFolders.Single() != Path.GetDirectoryName(sentinel), "An output created after planning is not overwritten");
            Check(ExportPaths.SafeName("CON") == "_CON" && ExportPaths.SafeName("  ... ") == "未命名角色" && ExportPaths.SafeName("A:B*") == "A_B_", "Windows reserved and empty names handled");

            var missing = Card(Path.Combine(sandbox, "缺失.png"), "缺失人物", 1, Map((Resolver, References("missing.mod")), ("unknown.extension", Extension(999, Map(("TexturePath", "C:/outside/file.png"))))));
            var incomplete = await service.ExportAsync(await service.BuildPlanAsync([missing], game, destination, CardExportMode.PerCard, null, default), null, default);
            Check(incomplete.Status == CardExportStatus.Incomplete && incomplete.ExportedCards == 1 && incomplete.Issues.Any(i => i.Message.Contains("missing.mod")) && incomplete.Issues.Any(i => i.Message.Contains("unknown.extension")) && incomplete.Issues.Any(i => i.Message.Contains("无法移植")), "Missing MOD/plugin, unknown extension and absolute paths report incomplete while preserving card");
            Check(!Directory.Exists(Path.Combine(incomplete.OutputFolders.Single(), "mods")), "No empty game folders fabricated");
            var corrupt = Card(Path.Combine(sandbox, "corrupt-extension.png"), "损坏扩展", 1, Map((MaterialEditor, Extension(1, Map(("MaterialShaderList", new byte[] { 0xc1 }))))));
            var corruptPlan = await service.BuildPlanAsync([corrupt], game, destination, CardExportMode.PerCard, null, default);
            Check(corruptPlan.Packages.Single().Issues.Any(i => i.Message.Contains("读取不完整")) && corruptPlan.Packages.Single().Files.Any(f => f.Kind == CardExportFileKind.Card), "Corrupt extension containers do not abort export planning");
            var traversal = Card(Path.Combine(sandbox, "traversal.png"), "越界引用", 1, Map(("KSOX", Extension(2, Map(("TexturePath", "UserData/../../escape.dat"))))));
            var traversalPlan = await service.BuildPlanAsync([traversal], game, destination, CardExportMode.PerCard, null, default);
            Check(traversalPlan.Packages.Single().Issues.Any(i => i.Message.Contains("越界")) && traversalPlan.Packages.Single().Files.All(f => !f.RelativePath.Contains("..")), "Untrusted extension resource paths cannot escape the package");
            await Throws<ArgumentException>(() => service.BuildPlanAsync([female], game, Path.Combine(game, "exports"), CardExportMode.PerCard, null, default));
            Check(true, "Exports cannot write inside original game");

            var lockedPlan = await service.BuildPlanAsync([female], game, destination, CardExportMode.PerCard, null, default);
            using (var locked = new FileStream(abmx, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var lockedReport = await service.ExportAsync(lockedPlan, null, default);
                Check(lockedReport.Status == CardExportStatus.Incomplete && lockedReport.Files.Any(f => !f.Success && f.File.Source == abmx) && lockedReport.ExportedCards == 1, "Locked plugin fails locally while other files continue");
            }
            var changedPlan = await service.BuildPlanAsync([secondFemale], game, destination, CardExportMode.PerCard, null, default);
            File.AppendAllText(secondFemale.Path, "changed after planning");
            var changedReport = await service.ExportAsync(changedPlan, null, default);
            Check(changedReport.Status == CardExportStatus.Incomplete && changedReport.ExportedCards == 0 && changedReport.Issues.Any(i => i.Message.Contains("发生变化")), "Source edits after planning are rejected");

            using (var cancel = new CancellationTokenSource())
            {
                var cancelPlan = await service.BuildPlanAsync([female], game, destination, CardExportMode.PerCard, null, default);
                var canceled = await service.ExportAsync(cancelPlan, new InlineProgress(p => { if (p.Completed > 0) cancel.Cancel(); }), cancel.Token);
                Check(canceled.Status == CardExportStatus.Cancelled && canceled.Files.Any(f => f.Success) && File.ReadAllText(Path.Combine(canceled.OutputFolders.Single(), "导出清单.txt")).Contains("[未复制]"), "Cancellation preserves completed copies and reports pending resources");
            }
            Check(!Directory.EnumerateFiles(destination, "*.tmp", SearchOption.AllDirectories).Any(), "Failures and cancellation leave no staged partial file");
            await CancelDuringCopy();
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); string untouched = Path.Combine(sandbox, "pre-cancelled");
                await Throws<OperationCanceledException>(() => service.BuildPlanAsync([female], game, untouched, CardExportMode.PerCard, null, cancel.Token));
                Check(!Directory.Exists(untouched), "Cancelled planning writes no output");
            }
            var missingSource = new CardRecord { Path = Path.Combine(sandbox, "gone.png") };
            var invalidReport = await service.ExportAsync(await service.BuildPlanAsync([missingSource], game, destination, CardExportMode.PerCard, null, default), null, default);
            Check(invalidReport.Status == CardExportStatus.Incomplete && invalidReport.OutputFolders.Count == 0 && File.Exists(invalidReport.ReportPath), "Unreadable selected cards skipped with a report even if none can export");
            await CheckExternalConflicts();
            await CheckRecovery();
            await CheckDiagnosticAccuracy();
            string newer = Plugin(Path.Combine(plugins, "New", "ABMX.dll"), ["KKABMX.Core"], [new("marco.kkapi")], ["Helper"], version: "2.0.0");
            var versionPlan = await service.BuildPlanAsync([female], game, destination, CardExportMode.PerCard, null, default);
            Check(versionPlan.Packages.Single().Files.Any(f => f.Source == newer) && versionPlan.Packages.Single().Files.All(f => f.Source != abmx), "Assembly references cannot reintroduce an older rejected plugin version");
            Check(Hash(female.Path) == originalHash, "Original selected card is byte-for-byte unchanged");
            passed += await CardExportLevelChecks.Run(output);
            if (realGame != null) await CheckRealGame(realGame);
            Console.WriteLine($"ALL {passed} CARD EXPORT CHECKS PASSED");

            async Task CheckExternalConflicts()
            {
                string extra = Path.Combine(sandbox, "extra-mods");
                Mod(Path.Combine(extra, "a", "same.zipmod"), "external.a"); Mod(Path.Combine(extra, "b", "same.zipmod"), "external.b");
                string config = Path.Combine(game, "BepInEx", "config"); Directory.CreateDirectory(config);
                File.WriteAllText(Path.Combine(config, "com.bepis.bepinex.sideloader.cfg"), "Additional mods directory = " + extra);
                var card = Card(Path.Combine(sandbox, "conflict.png"), "冲突", 1, Map((Resolver, References("external.a", "external.b"))));
                var conflictPlan = await service.BuildPlanAsync([card], game, destination, CardExportMode.PerCard, null, default);
                Check(conflictPlan.Packages[0].Issues.Any(i => i.Message.Contains("内容冲突")) && conflictPlan.Packages[0].Files.All(f => !f.Source.StartsWith(extra)), "Conflicting external archive paths omit both sources instead of silently overwriting");
                var single = Card(Path.Combine(sandbox, "external.png"), "外部资源", 1, Map((Resolver, References("external.a"))));
                var singlePlan = await service.BuildPlanAsync([single], game, destination, CardExportMode.PerCard, null, default);
                Check(singlePlan.Packages[0].Files.Any(f => f.Source.StartsWith(extra) && f.RelativePath.StartsWith("mods")), "Additional MOD directory exports into game mods folder");
            }
            async Task CancelDuringCopy()
            {
                string large = Path.Combine(game, "UserData", "overlay", "large.bin");
                using (var stream = File.Create(large)) stream.SetLength(64 * 1024 * 1024);
                var largeCard = Card(Path.Combine(sandbox, "large-resource.png"), "取消中的资源", 1, Map(("KSOX", Extension(2, Map(("TexturePath", "UserData/overlay/large.bin"))))));
                var largePlan = await service.BuildPlanAsync([largeCard], game, destination, CardExportMode.PerCard, null, default);
                using var cancellation = new CancellationTokenSource();
                using var watcher = new FileSystemWatcher(destination, ".hs2manager-*.tmp") { IncludeSubdirectories = true };
                watcher.Created += (_, e) => { if (e.FullPath.Contains(Path.Combine("UserData", "overlay"))) cancellation.Cancel(); };
                watcher.EnableRaisingEvents = true;
                var cancelled = await service.ExportAsync(largePlan, null, cancellation.Token);
                watcher.EnableRaisingEvents = false;
                Check(cancelled.Status == CardExportStatus.Cancelled && cancelled.ExportedCards == 1 && !File.Exists(Path.Combine(cancelled.OutputFolders.Single(), "UserData/overlay/large.bin")) &&
                    !Directory.EnumerateFiles(destination, ".hs2manager-*.tmp", SearchOption.AllDirectories).Any(), "Cancellation during an active large copy removes its partial staging file");
            }
            async Task CheckRecovery()
            {
                string target = Path.Combine(sandbox, "recovery", "plugin.dll"); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(abmx, target);
                var op = new FileOperationRecord { Kind = "Export", Source = abmx, Destination = target, Phase = "Committed", Hash = Hash(abmx) };
                op.Staging = Path.Combine(Path.GetDirectoryName(target)!, ".hs2manager-" + op.Id + ".tmp");
                AtomicFile.WriteJson(Path.Combine(repo.DataPath, "Operations", op.Id + ".json"), op);
                var fileService = new CardFileService(repo); await fileService.RecoverAsync(default);
                Check(fileService.Operations().Single(o => o.Id == op.Id).Phase == "Indexed" && repo.FindById(female.Id)!.Path == female.Path, "Recovery commits resource exports without indexing them as cards");
            }
            async Task CheckRealGame(string sourceGame)
            {
                string sample = Directory.EnumerateFiles(Path.Combine(sourceGame, "UserData/chara/female"), "*.png").First();
                string copy = Path.Combine(sandbox, "真实卡片.png"); File.Copy(sample, copy); var realCard = new CardParser().Parse(copy);
                string sourceHash = Hash(sample); var realRepo = new CardRepository(Path.Combine(sandbox, "RealData")); var realService = new CardExportService(realRepo);
                var realPlan = await realService.BuildPlanAsync([realCard], sourceGame, Path.Combine(sandbox, "real-export"), CardExportMode.Merged, new InlineProgress(p => { if (p.Total == 0) Console.WriteLine(p.Message); }), default);
                var actual = await realService.ExportAsync(realPlan, null, default);
                Check(actual.ExportedCards == 1 && actual.Files.Any(f => f.Success && f.File.Kind == CardExportFileKind.Mod) && actual.Files.Any(f => f.Success && f.File.Kind == CardExportFileKind.Plugin), "Real card copy exports installed MODs and plugin files");
                Check(actual.Files.Where(f => f.Success).All(f => Hash(f.File.Source) == Hash(Path.Combine(f.Package, f.File.RelativePath))) && Hash(sample) == sourceHash, "Real exported files match sources; original game card remains unchanged");
                Console.WriteLine(actual.Summary);
                foreach (var issue in actual.Issues.Take(18)) Console.WriteLine("EXPECTED/REVIEW: " + issue.Message);
                string[] reportedFiles = ["HS2ChaF_20250615091121672.png", "HS2ChaF_20250814211651049.png", "HS2ChaF_20250814212058710.png"];
                if (reportedFiles.All(name => File.Exists(Path.Combine(sourceGame, "UserData/chara/female", name))))
                {
                    var reportedCards = reportedFiles.Select(name => new CardParser().Parse(Path.Combine(sourceGame, "UserData/chara/female", name))).ToArray();
                    var reportedPlan = await realService.BuildPlanAsync(reportedCards, sourceGame, Path.Combine(sandbox, "reported-export"), CardExportMode.PerCard, null, default);
                    foreach (var package in reportedPlan.Packages)
                    {
                        Console.WriteLine($"REPORTED CARD: {package.FolderName}: {package.Files.Count} files, {package.Issues.Count} issues, {package.Warnings.Count} thumbnail warnings");
                        foreach (var issue in package.Issues.Concat(package.Warnings)) Console.WriteLine(issue.Message);
                    }
                    Check(reportedPlan.Packages.Count == 3 && reportedPlan.Packages.All(p => p.Issues.Count == 0) &&
                        reportedPlan.Packages.Sum(p => p.Warnings.Count) == 1 && reportedPlan.Packages[0].Warnings.Single().Message.Contains("milk217/b097/thumbs"),
                        "All three reported real cards resolve; only the genuinely absent milk217 menu thumbnail remains as a warning");
                }
            }
            async Task CheckDiagnosticAccuracy()
            {
                string textureMod = Mod(Path.Combine(mods, "loose-textures.zipmod"), "fixture.loose", entries:
                [
                    ("abdata/list/characustom/texture.csv", "313\nID,MainAB,AddTex,GlossTex,ThumbAB,ThumbTex\n42,chara/virtual.unity3d,paint,gloss,chara/thumb/virtual.unity3d,preview\n43,chara/unused-missing.unity3d,unused,unused,chara/thumb/unused.unity3d,unused\n"),
                    ("abdata/chara/virtual/paint.png", "paint"),
                    ("abdata/chara/virtual/gloss.png", "gloss"),
                    ("abdata/chara/thumb/virtual/preview.png", "thumbnail")
                ]);
                object Ref(string guid, int slot, int category) => Extension(1, Map(("info", Pack(new[] { Map(("ModID", guid), ("Slot", slot), ("CategoryNo", category)) }))));
                Dictionary<string, object?> InactiveBoobs() => Map(("overridePhysics", false), ("overrideGravity", false), ("butt_overridePhysics", false), ("butt_overrideGravity", false), ("damping", 1));
                var idle = Card(Path.Combine(sandbox, "idle-plugins.png"), "贴图人物", 1, Map((Resolver, Ref("fixture.loose", 42, 313)),
                    ("com.fairbair.hs2_boobsettings", Extension(0, InactiveBoobs())), ("KK_PregnancyPlus", Extension(1, Map(("pluginVersion", "7.8"), ("clothingOffsetVersion", 0))))));
                var idlePlan = await service.BuildPlanAsync([idle], game, destination, CardExportMode.Merged, null, default);
                Check(idlePlan.Packages.Single().Issues.Count == 0 && idlePlan.Packages.Single().Warnings.Count == 0 && idlePlan.Packages.Single().Files.Any(f => f.Source == textureMod),
                    "Exact PNG textures satisfy virtual bundles; unused rows and proven inactive plugin metadata do not report missing resources");

                Mod(Path.Combine(mods, "partial-textures.zipmod"), "fixture.partial", entries:
                [("abdata/list/characustom/texture.csv", "313\nID,MainAB,AddTex,GlossTex,ThumbAB,ThumbTex\n42,chara/partial.unity3d,present,absent,chara/thumb/partial.unity3d,preview\n"),
                    ("abdata/chara/partial/present.png", "only one texture")]);
                var partial = Card(Path.Combine(sandbox, "partial.png"), "缺少贴图", 1, Map((Resolver, Ref("fixture.partial", 42, 313))));
                var partialPlan = await service.BuildPlanAsync([partial], game, destination, CardExportMode.Merged, null, default);
                Check(partialPlan.Packages.Single().Issues.Count == 1 && partialPlan.Packages.Single().Issues[0].Message.Contains("资源包缺失") && partialPlan.Packages.Single().Warnings.Count == 1,
                    "One available PNG cannot hide another missing required texture; thumbnail absence is separate");
                string providerPng = Mod(Path.Combine(mods, "png-provider.zipmod"), "fixture.png-provider", entries: [("abdata/chara/partial/absent.png", "other texture")]);
                var crossPlan = await service.BuildPlanAsync([partial], game, destination, CardExportMode.Merged, null, default);
                Check(crossPlan.Packages.Single().Issues.Count == 0 && crossPlan.Packages.Single().Files.Any(f => f.Source == providerPng), "Cross-archive loose PNG provider is included");
                var crossReport = await service.ExportAsync(crossPlan, null, default);
                Check(crossReport.Status == CardExportStatus.Complete && crossReport.Warnings.Count == 1 && File.ReadAllText(Path.Combine(crossReport.OutputFolders.Single(), "导出清单.txt")).Contains("缩略图提示（不影响人物外观）"),
                    "Only missing menu thumbnails permit complete export while staying visible in the manifest");

                string looseGloss = Path.GetFullPath(Path.Combine(game, "abdata/chara/loose/gloss.png")); Directory.CreateDirectory(Path.GetDirectoryName(looseGloss)!); File.WriteAllText(looseGloss, "physical texture");
                Mod(Path.Combine(mods, "loose-file.zipmod"), "fixture.physical", entries: [("abdata/list/characustom/texture.csv", "313\nID,MainAB,AddTex\n42,chara/loose.unity3d,gloss\n")]);
                var physical = Card(Path.Combine(sandbox, "physical.png"), "散装贴图", 1, Map((Resolver, Ref("fixture.physical", 42, 313))));
                var physicalPlan = await service.BuildPlanAsync([physical], game, destination, CardExportMode.PerCard, null, default);
                Check(physicalPlan.Packages.Single().Issues.Count == 0 && physicalPlan.Packages.Single().Files.Any(f => f.Source == looseGloss), "Physical PNG fallback is copied with its game-relative path");

                foreach (var (label, version, data) in new (string, long, Dictionary<string, object?>)[]
                {
                    ("enabled", 0, Map(("overridePhysics", true), ("overrideGravity", false), ("butt_overridePhysics", false), ("butt_overrideGravity", false))),
                    ("unknown-version", 9, InactiveBoobs()),
                    ("unknown-field", 0, new(InactiveBoobs()) { ["futureFeature"] = false }),
                    ("malformed", 0, new(InactiveBoobs()) { ["overridePhysics"] = "false" })
                })
                {
                    var enabled = Card(Path.Combine(sandbox, label + ".png"), label, 1, Map(("com.fairbair.hs2_boobsettings", Extension(version, data)),
                        ("KK_PregnancyPlus", Extension(1, Map(("pluginVersion", "7.8"), ("inflationSize", 1))))));
                    var enabledPlan = await service.BuildPlanAsync([enabled], game, destination, CardExportMode.PerCard, null, default);
                    Check(enabledPlan.Packages.Single().Issues.Any(i => i.Message.Contains("缺少插件") && i.Message.Contains("boobsettings")) &&
                        enabledPlan.Packages.Single().Issues.Any(i => i.Message.Contains("缺少插件") && i.Message.Contains("PregnancyPlus")),
                        "Active or unrecognized plugin data must still require its plugin: " + label);
                }
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(sandbox).StartsWith(Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe export test cleanup");
            Directory.Delete(sandbox, true);
        }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class InlineProgress(Action<WorkProgress> callback) : IProgress<WorkProgress> { public void Report(WorkProgress value) => callback(value); }
}
