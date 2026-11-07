using HS2Manager.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

internal static class CoreChecks
{
    public static async Task Run(string game, string output)
    {
        NativeRenderingDataChecks.Run();
        await NativeCompatibilityChecks.Run(output);
        await ScanSettingsChecks.Run(output);
        var sandbox = Path.Combine(output, "core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        int passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
        string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
        var none = CancellationToken.None;
        try
        {
            var parser = new CardParser();
            var source = Directory.EnumerateFiles(Path.Combine(game, "UserData", "chara", "male"), "*.png", SearchOption.AllDirectories).First();
            var originals = Directory.EnumerateFiles(Path.Combine(game, "UserData", "chara"), "*.png", SearchOption.AllDirectories).Select(p => new FileInfo(p)).ToList();
            string originalHash = Hash(source);
            string chinese = Path.Combine(sandbox, "中文人物卡 🧩.png"); File.Copy(source, chinese);
            var parsed = parser.Parse(chinese);
            Check(parsed.Status == CardStatus.Valid && parsed.Sex == 0 && parsed.Name.Length > 0, "Chinese and Unicode path parses real character metadata");
            Check(parsed.PngLength > 0 && parsed.PngLength < parsed.Size, "PNG cover boundary excludes appended character data");
            string image = Path.Combine(sandbox, "普通图片.png"); await CardParser.CopyCoverAsync(chinese, parsed.PngLength, image, none);
            Check(parser.Parse(image).Status == CardStatus.ImageOnly, "Ordinary PNG identified without character data");
            string bad = Path.Combine(sandbox, "损坏.png"); File.WriteAllBytes(bad, [1, 2, 3, 4]);
            Check(parser.Parse(bad).Status == CardStatus.Corrupt, "Truncated PNG isolated as corrupt");
            string truncated = Path.Combine(sandbox, "截断人物.png"); File.Copy(chinese, truncated);
            using (var file = File.OpenWrite(truncated)) file.SetLength(parsed.PngLength + 3);
            Check(parser.Parse(truncated).Status == CardStatus.Corrupt, "Truncated appended character data detected");
            var largest = originals.MaxBy(x => x.Length)!;
            long allocated = GC.GetTotalAllocatedBytes(true); var big = parser.Parse(largest.FullName); allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            Check(big.Status == CardStatus.Valid && allocated < 32 * 1024 * 1024, $"Large card streamed: {largest.Length / 1048576d:F1} MB file, {allocated / 1048576d:F2} MB managed allocations");

            string fakeGame = Path.Combine(sandbox, "测试游戏"); string cardRoot = Path.Combine(fakeGame, "UserData", "chara");
            var repo = new CardRepository(Path.Combine(sandbox, "Data")); var files = new CardFileService(repo); var library = new CardLibraryService(repo);
            var imported = await files.ImportAsync([chinese], fakeGame, null, null, none);
            var second = await files.ImportAsync([chinese], fakeGame, null, null, none);
            Check(imported.Single().Success && second.Single().Success && imported[0].Destination != second[0].Destination, "Import keeps both copies on filename conflict");
            Check(Hash(imported[0].Destination!) == originalHash && Hash(second[0].Destination!) == originalHash, "Import preserves complete PNG and tail SHA-256");
            var first = repo.FindByPath(imported[0].Destination!)!;
            repo.UpdateMetadata([first.Id], c => { c.Favorite = true; c.Tags = ["测试", "常用"]; c.Collection = "原创"; c.Notes = "持久化检查"; });
            var scan = await library.ScanAsync([cardRoot], null, none, recursive: true);
            Check(scan.Valid == 2 && repo.FindById(first.Id)!.Favorite, "Incremental scan preserves user metadata");
            var dupes = await library.FindDuplicatesAsync(null, none);
            Check(dupes.Count == 1 && dupes[0].Count == 2, "Duplicates compare complete file hashes");
            var export = await files.ExportAsync([first], Path.Combine(sandbox, "导出"), null, none);
            Check(export.Single().Success && Hash(export[0].Destination!) == originalHash, "Original PNG export preserves every byte");
            string oldPath = first.Path; await files.RenameAsync(first, "重命名人物", none); first = repo.FindById(first.Id)!;
            Check(!File.Exists(oldPath) && Hash(first.Path) == originalHash && first.Tags.Count == 2, "Rename preserves bytes and metadata");
            await files.MoveAsync(first, Path.Combine(cardRoot, "自定义子目录"), none); first = repo.FindById(first.Id)!;
            Check(Hash(first.Path) == originalHash && first.Collection == "原创", "Move preserves complete data and virtual collection");
            string restoreTo = first.Path; await files.QuarantineAsync(first, fakeGame, none); first = repo.FindById(first.Id)!;
            Check(first.Availability == FileAvailability.Quarantined && !File.Exists(restoreTo) && File.Exists(first.Path), "Quarantine is recoverable outside character folders");
            var wideScan = await library.ScanAsync([fakeGame], null, none, recursive: true);
            Check(wideScan.Candidates == 1 && repo.FindById(first.Id)!.Availability == FileAvailability.Quarantined, "Quarantine excluded even when scanning a parent folder");
            File.Copy(source, restoreTo); await files.RestoreAsync(first, none); first = repo.FindById(first.Id)!;
            Check(first.Path != restoreTo && File.Exists(restoreTo) && Hash(first.Path) == originalHash && first.Favorite, "Restore collision keeps existing file and metadata");
            await library.ScanAsync([cardRoot], null, none, recursive: true);

            string unavailable = cardRoot + "-offline"; Directory.Move(cardRoot, unavailable);
            var offline = await library.ScanAsync([cardRoot], null, none, recursive: true);
            Check(offline.Warnings.Count == 1 && repo.All().Where(c => c.Root == cardRoot).All(c => c.Availability == FileAvailability.Offline), "Offline root retains cached cards without marking deleted");
            Directory.Move(unavailable, cardRoot); await library.ScanAsync([cardRoot], null, none, recursive: true);
            Check(repo.FindById(first.Id)!.Availability == FileAvailability.Available, "Reconnect restores availability");
            first = repo.FindById(first.Id)!;
            using (var locked = new FileStream(first.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var failed = await files.ExportAsync([first], Path.Combine(sandbox, "占用导出"), null, none);
                Check(!failed.Single().Success && File.Exists(first.Path), "Locked file fails safely without changing source");
            }
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); bool cancelled = false;
                try { await files.ExportAsync([first], Path.Combine(sandbox, "取消导出"), null, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && !Directory.EnumerateFiles(Path.Combine(sandbox, "取消导出")).Any(), "Cancellation leaves no partial exported PNG");
            }

            string committedTarget = Path.Combine(first.Folder, "中断后已提交.png");
            var committed = new FileOperationRecord { Kind = "Rename", Source = first.Path, Destination = committedTarget, Card = first, Hash = Hash(first.Path), Phase = "Committed" };
            committed.Staging = Path.Combine(first.Folder, ".hs2manager-" + committed.Id + ".tmp");
            AtomicFile.WriteJson(Path.Combine(repo.DataPath, "Operations", committed.Id + ".json"), committed); File.Move(first.Path, committedTarget);
            await files.RecoverAsync(none); first = repo.FindById(first.Id)!;
            Check(first.Path == committedTarget && first.Favorite && files.Operations().Single(x => x.Id == committed.Id).Phase == "Indexed", "Recovery reconciles committed rename and preserves metadata");
            var interrupted = new FileOperationRecord { Kind = "Export", Source = first.Path, Destination = Path.Combine(sandbox, "未完成.png"), Card = first, Phase = "Interrupted" };
            interrupted.Staging = Path.Combine(sandbox, ".hs2manager-" + interrupted.Id + ".tmp"); File.WriteAllBytes(interrupted.Staging, [1, 2]);
            AtomicFile.WriteJson(Path.Combine(repo.DataPath, "Operations", interrupted.Id + ".json"), interrupted);
            await files.RecoverAsync(none);
            Check(File.Exists(first.Path) && !File.Exists(interrupted.Staging) && files.Operations().Single(x => x.Id == interrupted.Id).Phase == "RolledBack", "Interrupted copy recovery removes only its staged partial copy");

            File.Copy(bad, Path.Combine(cardRoot, "坏卡.png")); File.Copy(image, Path.Combine(cardRoot, "图片.png"));
            var mixed = await library.ScanAsync([cardRoot, Path.Combine(cardRoot, "male")], null, none, recursive: true);
            Check(mixed.Invalid == 2 && mixed.Valid == 3, "Bad file does not abort scan; overlapping roots deduplicated");
            string missingPath = repo.All().First(c => c.Path == restoreTo).Path; File.Delete(missingPath); await library.ScanAsync([cardRoot], null, none, recursive: true);
            Check(repo.FindByPath(missingPath)!.Availability == FileAvailability.Missing, "Confirmed missing file retained as a missing index entry");
            string backup = Path.Combine(sandbox, "Backup", "library.db"); Directory.CreateDirectory(Path.GetDirectoryName(backup)!); repo.Backup(backup);
            Check(new CardRepository(Path.GetDirectoryName(backup)!).FindById(first.Id)!.Notes == "持久化检查", "SQLite backup contains persisted metadata");

            Directory.CreateDirectory(Path.Combine(fakeGame, "mods")); Directory.CreateDirectory(Path.Combine(fakeGame, "BepInEx", "plugins"));
            using (var zip = ZipFile.Open(Path.Combine(fakeGame, "mods", "fixture.zipmod"), ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.xml").Open(), Encoding.UTF8))
                writer.Write("<manifest schema-ver=\"1\"><guid>fixture.present</guid><name>Fixture</name><game>HS2</game><migrationInfo><info migrationType=\"MigrateAll\" guidOld=\"fixture.old\" guidNew=\"fixture.present\" /></migrationInfo></manifest>");
            var mods = new ModDependencyService(repo); await mods.ScanAsync(fakeGame, null, none);
            var dependencies = new CardRecord { Dependencies = [new("fixture.present"), new("fixture.missing"), new("fixture.old")], PluginDataIds = ["unknown.extension"] };
            var diagnosis = mods.Diagnose(dependencies, fakeGame);
            Check(diagnosis.Single(x => x.Guid == "fixture.present").State == DependencyState.Found && diagnosis.Single(x => x.Guid == "fixture.old").State == DependencyState.Found, "Manifest and migration dependencies found");
            Check(diagnosis.Single(x => x.Guid == "fixture.missing").State == DependencyState.Missing && diagnosis.Single(x => x.Guid == "unknown.extension").State == DependencyState.Unknown, "Missing MOD distinguished from unknown plugin data");
            File.WriteAllBytes(Path.Combine(fakeGame, "mods", "broken.zipmod"), [3, 4]); await mods.ScanAsync(fakeGame, null, none);
            Check(mods.Diagnose(dependencies, fakeGame).Single(x => x.Guid == "fixture.missing").State == DependencyState.Unknown, "Incomplete MOD scan never asserts missing");
            var settings = new AppSettings { GamePath = fakeGame, Theme = "Dark", Collections = ["原创"] }; settings.Save(repo.DataPath);
            Check(AppSettings.Load(repo.DataPath).Collections.SequenceEqual(["原创"]), "Portable settings round trip");
            Check(Hash(source) == originalHash, "Original game character card remains unchanged");
            Console.WriteLine($"ALL {passed} CORE CHECKS PASSED");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(sandbox).StartsWith(Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
            Directory.Delete(sandbox, true);
        }
    }
}
