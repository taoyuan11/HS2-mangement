using System.IO.Compression;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

internal static class PreviewCacheChecks
{
    internal static async Task Run(string output)
    {
        string root = Path.Combine(output, "preview-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cache = new PreviewMetadataCache(root, Path.Combine(root, "Data"));
        string source = Path.Combine(root, "source"); File.WriteAllText(source, "original");
        int builds = 0;
        Dictionary<string, int[]> Read() => cache.Get("test", PreviewMetadataCache.Stamp(source), () => { builds++; return new Dictionary<string, int[]> { ["bones"] = [1, 2, 3] }; }, default);
        var first = Read(); first["bones"][0] = 99;
        Check(Read()["bones"][0] == 1 && builds == 1, "Cache snapshots isolate mutable per-card data");
        PreviewMetadataCache.ClearMemory();
        Check(Read()["bones"][0] == 1 && builds == 1, "Persistent metadata survives a fresh memory cache");
        string entry = Directory.GetFiles(Path.Combine(root, "Data/UnityPreview/metadata-v1"), "*.cache").Single();
        File.WriteAllBytes(entry, new byte[128]); PreviewMetadataCache.ClearMemory();
        Check(Read()["bones"][0] == 1 && builds == 2, "Corrupt metadata rebuilds from source");
        File.AppendAllText(source, "changed"); Read();
        Check(builds == 3, "Changed source size invalidates cached metadata");
        File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(10)); Read();
        Check(builds == 4, "Same-size source timestamp changes invalidate metadata");
        File.Delete(source);
        try { Read(); throw new Exception("Missing source accepted through cache."); } catch (FileNotFoundException) { }
        using (var cancel = new CancellationTokenSource())
        {
            try { cache.Get("cancel", "one", () => { cancel.Cancel(); return "incomplete"; }, cancel.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
        }
        Check(cache.Get("cancel", "one", () => "complete", default) == "complete", "Canceled builds do not publish partial metadata");
        int concurrent = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => cache.Get("concurrent", "one", () => Interlocked.Increment(ref concurrent), default))));
        Check(concurrent == 1, "Concurrent requests share successful metadata construction");
        string smallData = Path.Combine(root, "Budget");
        var small = new PreviewMetadataCache(root, smallData, 750);
        small.Get("budget", "a", () => new string('a', 300), default);
        small.Get("budget", "b", () => new string('b', 300), default);
        small.Get<string>("budget", "a", () => throw new Exception("Lost memory entry"), default);
        small.Get("budget", "c", () => new string('c', 300), default);
        Check(Directory.GetFiles(Path.Combine(smallData, "UnityPreview/metadata-v1"), "*.cache").Sum(p => new FileInfo(p).Length) <= 750, "Disk cache respects its budget");
        PreviewMetadataCache.ClearMemory();
        Check(small.Get("budget", "a", () => "wrong", default).Length == 300, "Disk eviction preserves recently read entries");
        Check(small.Get("budget", "b", () => "evicted", default) == "evicted", "Disk eviction removes the least recently used entry");
        await Archives(root);
        EnvironmentSettings(root);
        Console.WriteLine("PASS preview cache checks");
    }
    private static void EnvironmentSettings(string root)
    {
        string game = Path.Combine(root, "EnvironmentGame"), data = Path.Combine(root, "EnvironmentCache");
        Directory.CreateDirectory(Path.Combine(game, "UserData/config"));
        Directory.CreateDirectory(Path.Combine(game, "UserData/custom"));
        Directory.CreateDirectory(Path.Combine(game, "DHH_Data"));
        string setup = Path.Combine(game, "UserData/setup.xml"), custom = Path.Combine(game, "UserData/custom/customscene.dat");
        File.WriteAllText(setup, "<Setup><Quality>2</Quality><Width>1920</Width><Height>1080</Height></Setup>");
        File.WriteAllText(Path.Combine(game, "UserData/config/system.xml"), "<System><SelfShadow>False</SelfShadow></System>");
        using (var writer = new BinaryWriter(File.Create(custom))) { writer.Write("test"); writer.Write(.1f); writer.Write(.2f); writer.Write(.3f); }
        File.WriteAllText(Path.Combine(game, "DHH_Data/UserSetting.ini"), "?tag=GraphicEnable&=False&");
        File.WriteAllText(Path.Combine(game, "DHH_Data/GraphicSetting.dhs"), "?tag=GSfloat_2&=0.25&");
        var cache = new PreviewMetadataCache(game, data);
        EnvironmentDefinition Read()
        {
            var original = cache.Get("scene-fixture", "unchanged-scene", () => new EnvironmentDefinition(), default);
            return UnityEnvironmentReader.ApplySettings(game, data, original, Enumerable.Range(0, 6).Select(i => new Parameter { Number = i }).ToArray(), 0, [], [], [], default);
        }
        var first = Read();
        Check(first.Quality.Number == 5 && first.Camera.Float("gameScreenWidth") == 1920 && first.Background[0] == .1f && first.DhhFloats[2] == .25f, "Current quality, screen, background and DHH settings apply to cached scene data");
        File.WriteAllText(setup, "<Setup><Quality>0</Quality><Width>1280</Width><Height>720</Height></Setup>");
        File.WriteAllText(Path.Combine(game, "UserData/config/system.xml"), "<System><SelfShadow>True</SelfShadow></System>");
        File.WriteAllText(Path.Combine(game, "DHH_Data/GraphicSetting.dhs"), "?tag=GSfloat_2&=0.75&");
        File.Delete(custom);
        var changed = Read();
        Check(changed.Quality.Number == 0 && changed.Camera.Float("gameScreenWidth") == 1280 && changed.Background[0] != .1f && changed.DhhFloats[2] == .75f, "Changing environment files takes effect without rebuilding static scene data");
        Check(first.Quality.Number == 5 && first.Background[0] == .1f, "Environment edits do not mutate prior characters");
    }
    private static async Task Archives(string root)
    {
        string game = Path.Combine(root, "Game"), data = Path.Combine(root, "ArchiveData"), extraction = Path.Combine(data, "Extracted");
        Directory.CreateDirectory(extraction); Directory.CreateDirectory(Path.Combine(game, "mods"));
        Directory.CreateDirectory(Path.Combine(game, "abdata")); Directory.CreateDirectory(Path.Combine(game, "BepInEx/plugins"));
        string archive = Path.Combine(game, "mods", "sample.zipmod");
        void Write(string contents)
        {
            using var file = File.Create(archive); using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            Add("manifest.xml", "<manifest schema-ver=\"1\"><guid>preview.test</guid><name>Test</name><version>1</version><game>hs2</game></manifest>");
            Add("abdata/test/sample.unity3d", contents);
            Add("abdata/test/sample/texture.png", contents);
            Add("abdata/list/characustom/test.csv", "300\nID,MainAB,MainData\n1,test/sample.unity3d,asset\n");
            void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        }
        Write("original");
        var repository = new CardRepository(data);
        var scanned = await new ModDependencyService(repository).ScanAsync(game, null, default);
        Check(scanned.Files.Single().ArchiveEntries?.Length == 3, "MOD scans persist archive entry paths and sizes");
        scanned.Files.Single().ArchiveEntries = null;
        repository.StoreMetadata("mods-v1", scanned);
        NativeModResolver Resolver() => new(game, data, extraction, null, default);
        var resolver = Resolver();
        string bundle = resolver.Bundle("test/sample.unity3d");
        string texture = resolver.LooseTexture("test/sample.unity3d", "texture")!;
        Check(repository.LoadMetadata<ModIndex>("mods-v1")!.Files.Single().ArchiveEntries?.Length == 3, "Legacy MOD inventories upgrade their archive directories once");
        Check(File.ReadAllText(bundle) == "original" && File.ReadAllText(texture) == "original", "Cross-archive resources and loose PNG resolve through the catalog");
        Check(resolver.Resolve(new ModReference("preview.test", 1, 300)).Asset == "asset", "Archive CSV parses before caching");
        using (new PreviewPerformance(data, 123))
        {
            resolver = Resolver();
            Check(resolver.Bundle("test/sample.unity3d") == bundle && resolver.LooseTexture("test/sample.unity3d", "texture") == texture, "Extraction cache paths remain stable");
            Check(resolver.Resolve(new ModReference("preview.test", 1, 300)).Asset == "asset", "Archive CSV persists across resolver instances");
        }
        using var metrics = JsonDocument.Parse(File.ReadLines(Path.Combine(data, "Logs/preview-performance.jsonl")).Last());
        Check(!metrics.RootElement.GetProperty("Counters").TryGetProperty("archiveOpens", out _), "Warm lookup does not reopen ZIP archives");
        Write("replacement-with-new-size");
        bundle = Resolver().Bundle("test/sample.unity3d");
        Check(File.ReadAllText(bundle) == "replacement-with-new-size", "Modified archives invalidate resources without requiring a manifest refresh");
        void Missing() { try { Resolver().Bundle("test/missing.unity3d"); throw new Exception("Missing resource accepted"); } catch (FileNotFoundException) { } }
        Missing(); var refreshed = repository.LoadMetadata<ModIndex>("mods-v1")!.UpdatedUtc;
        Missing(); Check(repository.LoadMetadata<ModIndex>("mods-v1")!.UpdatedUtc == refreshed, "Repeated misses do not rescan the same MOD index");
        await new ModDependencyService(repository).ScanAsync(game, null, default);
        Check(Resolver().Bundle("test/sample.unity3d") == bundle, "Explicit index refresh remains compatible with extraction caches");
        File.Delete(archive);
        try { Resolver().Bundle("test/sample.unity3d", archive); throw new Exception("Deleted archive served from cache"); } catch (FileNotFoundException) { }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
