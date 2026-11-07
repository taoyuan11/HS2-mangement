using HS2Manager.Core;

internal static class ScanSettingsChecks
{
    public static async Task Run(string output)
    {
        string sandbox = Path.Combine(output, "scan-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        void WritePng(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9xkAAAAASUVORK5CYII="));
        }
        try
        {
            string game = Path.Combine(sandbox, "游戏");
            string female = Path.Combine(game, "UserData", "chara", "female");
            string extra = Path.Combine(sandbox, "额外人物卡");
            var settings = AppSettings.Load(sandbox);
            Check(!settings.RecursiveCardSearch, "New settings disable recursive card search");
            // Existing settings lack the new switch and may contain the old computed roots.
            AtomicFile.WriteJson(Path.Combine(sandbox, "settings.json"), new { GamePath = game, ExtraRoots = new[] { extra }, ScanRoots = new[] { Path.GetDirectoryName(female)! } });
            settings = AppSettings.Load(sandbox);
            Check(!settings.RecursiveCardSearch && settings.ScanRoots.SequenceEqual([female, extra]), "Legacy settings default to shallow female search and retain extra roots");

            string direct = Path.Combine(female, "人物.png");
            string nested = Path.Combine(female, "子目录", "深层", "人物.PNG");
            string extraDirect = Path.Combine(extra, "人物.png");
            string extraNested = Path.Combine(extra, "子目录", "人物.png");
            string male = Path.Combine(game, "UserData", "chara", "male", "人物.png");
            string trash = Path.Combine(female, ".HS2ManagerTrash", "隔离.png");
            foreach (string path in new[] { direct, nested, extraDirect, extraNested, male, trash }) WritePng(path);
            var repo = new CardRepository(Path.Combine(sandbox, "Data"));
            var library = new CardLibraryService(repo);
            var shallow = await library.ScanAsync(settings.ScanRoots, null, CancellationToken.None);
            Check(shallow.Candidates == 2 && repo.All().Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([direct, extraDirect]), "Default scan only indexes the top level of female and extra roots");

            settings.RecursiveCardSearch = true; settings.Save(sandbox); settings = AppSettings.Load(sandbox);
            Check(settings.RecursiveCardSearch, "Enabled recursive setting survives reload");
            var recursive = await library.ScanAsync(settings.ScanRoots, null, CancellationToken.None, settings.RecursiveCardSearch);
            Check(recursive.Candidates == 4 && recursive.Unchanged == 2 && repo.FindByPath(nested) != null && repo.FindByPath(extraNested) != null && repo.FindByPath(male) == null && repo.FindByPath(trash) == null,
                "Recursive scan includes both roots' descendants, skips male and quarantine directories, and reuses existing covers");
            var nestedCard = repo.FindByPath(nested)!;
            repo.UpdateMetadata([nestedCard.Id], c => { c.Favorite = true; c.Tags = ["保留"]; });

            settings.RecursiveCardSearch = false; settings.Save(sandbox); settings = AppSettings.Load(sandbox);
            Check(!settings.RecursiveCardSearch, "Disabled recursive setting survives reload");
            File.Delete(direct);
            await library.ScanAsync(settings.ScanRoots, null, CancellationToken.None, settings.RecursiveCardSearch);
            Check(repo.FindByPath(direct)!.Availability == FileAvailability.Missing && repo.FindByPath(nested) is { Availability: FileAvailability.Available, Favorite: true } retained && retained.Tags.SequenceEqual(["保留"]) && repo.FindByPath(extraNested)!.Availability == FileAvailability.Available,
                "Disabling recursion reconciles direct files while preserving skipped descendants and their metadata");

            string offline = female + "-offline";
            Directory.Move(female, offline);
            var unavailable = await library.ScanAsync(settings.ScanRoots, null, CancellationToken.None, settings.RecursiveCardSearch);
            Check(unavailable.Warnings.Count == 1 && repo.FindByPath(direct)!.Availability == FileAvailability.Offline && repo.FindByPath(nested)!.Availability == FileAvailability.Available,
                "Shallow offline scan only changes availability within its scan depth");
            Directory.Move(offline, female);

            File.Delete(nested);
            await library.ScanAsync(settings.ScanRoots, null, CancellationToken.None, recursive: true);
            Check(repo.FindByPath(nested)!.Availability == FileAvailability.Missing, "Re-enabling recursion detects missing descendants");
            var explicitChild = await library.ScanAsync([extra, Path.GetDirectoryName(extraNested)!], null, CancellationToken.None);
            Check(explicitChild.Candidates == 2 && repo.FindByPath(extraNested)!.Root == Path.GetDirectoryName(extraNested), "Explicit extra child root is scanned even with recursion disabled");
            Console.WriteLine("ALL SCAN SETTINGS CHECKS PASSED");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (!Path.GetFullPath(sandbox).StartsWith(Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
            Directory.Delete(sandbox, true);
        }
    }
}
