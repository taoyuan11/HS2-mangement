using System.Security.Cryptography;
using HS2Manager.Core;
using static CardExportFixtures;

internal static class CardExportLevelChecks
{
    internal static async Task<int> Run(string output)
    {
        string sandbox = Path.Combine(output, "export-levels-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        int passed = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); passed++; }
        try
        {
            string game = Path.Combine(sandbox, "game"), plugins = Path.Combine(game, "BepInEx/plugins");
            string main = Mod(Path.Combine(game, "mods/main.zipmod"), "level.main", entries:
                [("abdata/list/characustom/main.csv", "245\nID,MainAB,MainData\n42,shared/model.unity3d,body\n")]);
            string provider = Mod(Path.Combine(game, "mods/provider.zipmod"), "level.provider", entries: [("abdata/shared/model.unity3d", "model")]);
            string migration = Mod(Path.Combine(game, "mods/migration.zipmod"), "level.migration", manifest:
                "<migrationInfo><info migrationType=\"MigrateAll\" guidOld=\"level.old\" guidNew=\"level.main\" /></migrationInfo>");
            string shader = Mod(Path.Combine(game, "mods/shader.zipmod"), "level.shader", manifest:
                "<MaterialEditor><Shader Name=\"Level/Shader\" AssetBundle=\"shader/loose.unity3d\" /></MaterialEditor>");
            string loose = Path.Combine(game, "abdata/shader/loose.unity3d");
            Directory.CreateDirectory(Path.GetDirectoryName(loose)!); File.WriteAllText(loose, "loose shader");
            string overlay = Path.Combine(game, "UserData/overlay/texture.png");
            Directory.CreateDirectory(Path.GetDirectoryName(overlay)!); File.WriteAllText(overlay, "external texture");
            string save = Plugin(Path.Combine(plugins, "Save.dll"), [Save]);
            string side = Plugin(Path.Combine(plugins, "Side.dll"), [Sideloader], [new(Save)]);
            string material = Plugin(Path.Combine(plugins, "Material.dll"), [MaterialEditor]);
            string overlayPlugin = Plugin(Path.Combine(plugins, "Overlay/Overlay.dll"), ["KSOX", "KCOX"], [new(Save)], ["Helper"]);
            string helper = Plugin(Path.Combine(plugins, "Helper.dll"), [], references: ["Overlay"]);
            string companion = Path.Combine(plugins, "Overlay/lookup.json"); File.WriteAllText(companion, "plugin data");
            File.WriteAllText(Path.Combine(plugins, "Overlay/settings.cfg"), "excluded");
            var extensions = Map((Resolver, References("level.old")), ("KSOX", Extension(2, Map(("TexturePath", "UserData/overlay/texture.png")))),
                (MaterialEditor, Extension(1, Map(("MaterialShaderList", Pack(new[] { Map(("ShaderName", "Level/Shader")) }))))));
            var cards = new[] { Card(Path.Combine(sandbox, "female/a.png"), "同名", 1, extensions), Card(Path.Combine(sandbox, "male/b.png"), "同名", 0, extensions) };
            var hashes = cards.ToDictionary(c => c.Path, c => Hash(c.Path));
            var repository = new CardRepository(Path.Combine(sandbox, "Data")); var service = new CardExportService(repository);
            foreach (var level in Enum.GetValues<CardExportLevel>())
            foreach (var mode in Enum.GetValues<CardExportMode>())
            {
                var plan = await service.BuildPlanAsync(cards, game, Path.Combine(sandbox, level + "-" + mode), new CardExportOptions(mode, level), null, default);
                var expected = new HashSet<string>(cards.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
                if (level >= CardExportLevel.WithMods) expected.UnionWith([main, provider, migration, shader]);
                if (level >= CardExportLevel.WithPlugins) expected.UnionWith([save, side, material, overlayPlugin, helper]);
                if (level == CardExportLevel.Full) expected.UnionWith([loose, overlay, companion]);
                expected = expected.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Check(plan.Level == level && expected.SetEquals(plan.Packages.SelectMany(p => p.Files).Select(f => f.Source)), $"{level}/{mode}: exact dependency set");
                Check(plan.Packages.All(p => p.Files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == p.Files.Count) &&
                    (mode == CardExportMode.Merged ? plan.Packages.Single().Cards.Count == 2 : plan.Packages.Select(p => p.FolderName).SequenceEqual(["同名", "同名 (2)"])),
                    $"{level}/{mode}: shared files deduplicate, independent folders preserve both cards");
                var report = await service.ExportAsync(plan, null, default);
                Check(report is { Status: CardExportStatus.Complete, ExportedCards: 2 } && report.Level == level && report.OutputFolders.Count == (mode == CardExportMode.Merged ? 1 : 2),
                    $"{level}/{mode}: export completes within its selected scope ({report.Summary}; {string.Join("; ", report.Issues.Select(i => i.Message))})");
                Check(report.Files.All(f => f.Success && Hash(f.File.Source) == Hash(Path.Combine(f.Package, f.File.RelativePath))) &&
                    report.Files.Where(f => f.File.Kind == CardExportFileKind.Card).All(f => f.File.RelativePath.Contains(f.File.Source == cards[0].Path ? "female" : "male")),
                    $"{level}/{mode}: original bytes and gender paths preserved");
                Check(report.OutputFolders.All(folder => File.ReadAllText(Path.Combine(folder, "导出清单.txt")).Contains(level.ScopeNote())) &&
                    File.ReadAllText(report.ReportPath).Contains(level.Label()) && File.ReadAllText(report.ReportPath).Contains(level.ScopeNote()),
                    $"{level}/{mode}: manifests and report identify selected and omitted scope");
            }
            var legacy = await service.BuildPlanAsync(cards, game, Path.Combine(sandbox, "legacy"), CardExportMode.Merged, null, default);
            Check(legacy.Level == CardExportLevel.Full && legacy.Packages.Single().Files.Any(f => f.Source == Path.GetFullPath(companion)), "Legacy mode overload still exports all resources");

            File.Delete(overlay); File.Delete(loose);
            var third = await Export(cards, CardExportLevel.WithPlugins);
            Check(third.Status == CardExportStatus.Complete, "Missing standalone resources do not fail level three");
            var fourth = await Export(cards, CardExportLevel.Full);
            Check(fourth.Status == CardExportStatus.Incomplete && fourth.Issues.Any(i => i.Message.Contains("texture.png")) && fourth.Issues.Any(i => i.Message.Contains("loose.unity3d")),
                "Level four diagnoses missing external texture and loose bundle");
            File.Delete(overlayPlugin);
            Check((await Export(cards, CardExportLevel.WithMods)).Status == CardExportStatus.Complete, "Missing plugin does not fail level two");
            var missingPlugin = await Export(cards, CardExportLevel.WithPlugins);
            Check(missingPlugin.Status == CardExportStatus.Incomplete && missingPlugin.Issues.Any(i => i.Message.Contains("KSOX")), "Level three diagnoses missing plugin");

            Plugin(Path.Combine(plugins, "Unknown.dll"), ["unknown.extension"]);
            var unknown = Card(Path.Combine(sandbox, "unknown.png"), "未知扩展", 1, Map(("unknown.extension", Extension(999, Map(("TexturePath", "UserData/missing.png"))))));
            Check((await Export([unknown], CardExportLevel.WithPlugins)).Status == CardExportStatus.Complete, "Known plugin DLL can export at level three despite unknown external resource schema");
            var unknownFull = await Export([unknown], CardExportLevel.Full);
            Check(unknownFull.Status == CardExportStatus.Incomplete && unknownFull.Issues.Any(i => i.Message.Contains("未确认扩展")), "Unknown external resource requirements remain visible at level four");
            var missingMod = Card(Path.Combine(sandbox, "missing-mod.png"), "缺失 MOD", 1, Map((Resolver, References("level.absent"))));
            Check((await Export([missingMod], CardExportLevel.WithMods)).Status == CardExportStatus.Incomplete, "Level two diagnoses missing MOD");
            var brokenShader = Card(Path.Combine(sandbox, "broken-shader.png"), "损坏 Shader 引用", 1,
                Map((MaterialEditor, Extension(1, Map(("MaterialShaderList", new byte[] { 0xc1 }))))));
            Check((await Export([brokenShader], CardExportLevel.WithMods)).Status == CardExportStatus.Incomplete, "Level two reports unreadable Shader MOD references");

            var offlineRepo = new CardRepository(Path.Combine(sandbox, "offline-data")); var offlineService = new CardExportService(offlineRepo);
            var offlinePlan = await offlineService.BuildPlanAsync([missingMod], Path.Combine(sandbox, "offline-game"), Path.Combine(sandbox, "offline-export"),
                new CardExportOptions(CardExportMode.PerCard, CardExportLevel.CardOnly), null, default);
            Check((await offlineService.ExportAsync(offlinePlan, null, default)).Status == CardExportStatus.Complete && offlineRepo.LoadMetadata<ModIndex>("mods-v1") == null,
                "Level one exports offline without scanning dependencies or reporting missing MODs");
            Check(hashes.All(p => Hash(p.Key) == p.Value), "Original cards remain byte-for-byte unchanged at every level");

            string settingsFolder = Path.Combine(sandbox, "settings");
            Check(AppSettings.Load(settingsFolder).LastCardExportLevel == CardExportLevel.WithMods, "Fresh settings default to level two");
            Directory.CreateDirectory(settingsFolder);
            foreach (int? value in new int?[] { null, 0, -1, 99 })
            {
                File.WriteAllText(Path.Combine(settingsFolder, "settings.json"), value.HasValue ? $"{{\"Theme\":\"Dark\",\"LastCardExportLevel\":{value}}}" : "{\"Theme\":\"Dark\"}");
                var settings = AppSettings.Load(settingsFolder);
                Check(settings.LastCardExportLevel == CardExportLevel.WithMods && settings.Theme == "Dark", "Legacy or invalid level falls back without resetting other settings: " + value);
            }
            foreach (var level in Enum.GetValues<CardExportLevel>())
            {
                new AppSettings { LastCardExportLevel = level }.Save(settingsFolder);
                Check(AppSettings.Load(settingsFolder).LastCardExportLevel == level, "Last export level survives settings reload: " + level);
            }
            try
            {
                await service.BuildPlanAsync(cards, game, Path.Combine(sandbox, "invalid-level"), new CardExportOptions(CardExportMode.Merged, (CardExportLevel)99), null, default);
                throw new Exception("Invalid export level was accepted");
            }
            catch (ArgumentOutOfRangeException) { Check(!Directory.Exists(Path.Combine(sandbox, "invalid-level")), "Invalid export level rejected before writing output"); }
            return passed;

            async Task<CardExportReport> Export(CardRecord[] selected, CardExportLevel level)
            {
                var plan = await service.BuildPlanAsync(selected, game, Path.Combine(sandbox, "diagnostics-" + Guid.NewGuid().ToString("N")),
                    new CardExportOptions(CardExportMode.Merged, level), null, default);
                return await service.ExportAsync(plan, null, default);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(sandbox).StartsWith(Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Unsafe export level test cleanup");
            Directory.Delete(sandbox, true);
        }
    }

    private static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
