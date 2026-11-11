using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace HS2Manager.Core;

internal sealed class NativeModResolutionException(string message) : FileNotFoundException(message);

internal sealed class NativeModResolver(string game, string data, string cache, IProgress<string>? progress, CancellationToken cancellation,string cacheNamespace="UnityPreview")
{
    private readonly PreviewMetadataCache metadata = new(game, data,cacheNamespace:cacheNamespace);
    private readonly Dictionary<string, (string Stamp, ModArchiveEntry[] Entries)> directories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object refreshLock = new();
    private sealed class RefreshState
    {
        internal DateTime Version;
        internal HashSet<string> Misses = new(StringComparer.OrdinalIgnoreCase);
    }
    private static readonly Dictionary<string, RefreshState> refreshed = new(StringComparer.OrdinalIgnoreCase);
    private ModIndex? index;
    private bool scanned;
    private readonly Dictionary<string, List<NativeAssetEntry>> lists = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string[]>? bundles;
    internal void BeginRequest(IProgress<string>? nextProgress,CancellationToken nextCancellation)
    {progress=nextProgress;cancellation=nextCancellation;scanned=false;}
    private CardRepository Repository => new(data);
    public NativeAssetEntry Resolve(ModReference reference)
    {
        EnsureIndex();
        var result = Find(reference, new(StringComparer.OrdinalIgnoreCase), 0);
        if (result == null && !scanned)
        {
            RefreshOnce("slot|" + reference.Guid + "|" + reference.Category + "|" + reference.Slot); result = Find(reference, new(StringComparer.OrdinalIgnoreCase), 0);
        }
        if (result != null) return result;
        if (UsableMods().Any(m => m.Guid.Equals(reference.Guid, StringComparison.OrdinalIgnoreCase)))
            throw new NativeModResolutionException($"MOD {reference.Guid} 已安装，但没有类别 {reference.Category} / Slot {reference.Slot}，且未找到可用迁移。");
        throw new NativeModResolutionException($"缺少 MOD {reference.Guid}（类别 {reference.Category} / Slot {reference.Slot}）。");
    }
    public NativeAssetEntry? FindSavedName(int category,string name)
    {
        if(string.IsNullOrWhiteSpace(name))return null;
        EnsureIndex();
        var matches=new Dictionary<string,NativeAssetEntry>(StringComparer.OrdinalIgnoreCase);
        foreach(var mod in UsableMods())
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if(!lists.TryGetValue(mod.Path,out var entries))lists[mod.Path]=entries=ReadLists(mod.Path);
                foreach(var entry in entries.Where(e=>e.Category==category && e.Get("Name").Equals(name,StringComparison.Ordinal)))
                    matches.TryAdd(mod.Guid+":"+entry.Category+":"+entry.Id,entry);
                if(matches.Count>1)return null; // A display name is not a globally unique MOD identifier.
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or MalformedLineException) { }
        }
        return matches.Count==1?matches.Values.Single():null;
    }
    private NativeAssetEntry? Find(ModReference reference, HashSet<string> visited, int depth)
    {
        cancellation.ThrowIfCancellationRequested();
        if (depth >= 16 || !visited.Add(reference.Guid + ":" + reference.Category + ":" + reference.Slot)) return null;
        if (index!.MigrationEnabled)
        {
            var rules = UsableMods().SelectMany(m => m.Migrations).Where(m => m.OldGuid.Equals(reference.Guid, StringComparison.OrdinalIgnoreCase) &&
                (m.Kind == "MigrateAll" || m.Kind == "Migrate" && m.OldSlot == reference.Slot && NativeModCategories.Matches(m.Category, reference.Category)))
                .OrderBy(m => m.Kind == "MigrateAll" ? 1 : 0);
            foreach (var rule in rules)
            {
                var migrated = Find(reference with { Guid = rule.NewGuid, Slot = rule.Kind == "MigrateAll" ? reference.Slot : rule.NewSlot }, visited, depth + 1);
                if (migrated != null) return migrated;
            }
        }
        foreach (var mod in UsableMods().Where(m => m.Guid.Equals(reference.Guid, StringComparison.OrdinalIgnoreCase)))
        {
            if (!File.Exists(mod.Path)) continue;
            if (!lists.TryGetValue(mod.Path, out var entries)) lists[mod.Path] = entries = ReadLists(mod.Path);
            var entry = entries.LastOrDefault(e => e.Category == reference.Category && e.Id == reference.Slot);
            if (entry != null) return entry;
        }
        return null;
    }
    private IEnumerable<ModFileInfo> UsableMods() => NativeModSelection.Select(index!.Files);
    internal IEnumerable<ModFileInfo> InstalledMods() { EnsureIndex(); return UsableMods(); }
    private void EnsureIndex()
    {
        index ??= Repository.LoadMetadata<ModIndex>("mods-v1");
        if (index == null || !index.GamePath.Equals(Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase)
            || index.Files.Any(m => m.Error.Length == 0 && m.ArchiveEntries == null)) Scan();
    }
    private void Scan()
    {
        using var timing = PreviewPerformance.Measure("modIndexScan");
        PreviewPerformance.Count("modScans");
        progress?.Report("正在更新 MOD 模型资源索引…");
        index = new ModDependencyService(Repository).ScanAsync(game, null, cancellation).GetAwaiter().GetResult();
        scanned = true; lists.Clear(); bundles = null; directories.Clear();
    }
    private void RefreshOnce(string missing)
    {
        // Suppress repeated misses, while a different missing GUID can discover a newly
        // installed archive. Auto-refreshes with identical inventories preserve negatives;
        // an explicit ScanAsync publishes a new version and clears them all.
        while (!Monitor.TryEnter(refreshLock, 25)) cancellation.ThrowIfCancellationRequested();
        try
        {
            string key = Path.GetFullPath(data) + "|" + Path.GetFullPath(game);
            var latest = Repository.LoadMetadata<ModIndex>("mods-v1");
            if (latest != null && latest.GamePath.Equals(Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase)) index = latest;
            if (!refreshed.TryGetValue(key, out var state)) refreshed[key] = state = new RefreshState();
            if (state.Version != index!.UpdatedUtc) state.Misses.Clear();
            if (!state.Misses.Contains(missing))
            {
                string before = Inventory(index);
                Scan();
                if (before != Inventory(index!)) state.Misses.Clear();
                state.Version = index!.UpdatedUtc; state.Misses.Add(missing);
            }
            else { scanned = true; lists.Clear(); bundles = null; directories.Clear(); }
        }
        finally { Monitor.Exit(refreshLock); }
        static string Inventory(ModIndex value) => string.Join("\n", value.Files.Select(f => f.Path + "|" + f.Size + "|" + f.ModifiedUtcTicks + "|" + f.Error));
    }
    internal List<NativeAssetEntry> ReadLists(string path)
    {
        return metadata.Get("archive-lists", PreviewMetadataCache.Stamp(path), () => ReadListsUncached(path), cancellation);
    }
    private List<NativeAssetEntry> ReadListsUncached(string path)
    {
        PreviewPerformance.Count("archiveOpens");
        using var zip = ZipFile.OpenRead(path); var result = new List<NativeAssetEntry>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.Replace('\\','/').StartsWith("abdata/list/characustom/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("MOD 清单过大。");
            using var stream = entry.Open(); using var csv = new TextFieldParser(stream, Encoding.UTF8, true) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
            csv.SetDelimiters(","); var first = csv.ReadFields();
            if (first == null || !int.TryParse(first[0], out int category)) continue;
            string[]? keys = null;
            for (int i = 0; i < 8 && !csv.EndOfData; i++) { var row = csv.ReadFields(); if (row?.FirstOrDefault() == "ID") { keys = row; break; } }
            if (keys == null) continue;
            int rows = 0;
            while (!csv.EndOfData)
            {
                cancellation.ThrowIfCancellationRequested();
                if (++rows > 100000) throw new InvalidDataException("MOD 清单行数超限。");
                var row = csv.ReadFields(); if (row == null || !int.TryParse(row[0], out int id)) continue;
                var values = new Dictionary<string, string>();
                for (int i = 0; i < Math.Min(keys.Length, row.Length); i++) if (keys[i].Length > 0) values[keys[i]] = row[i];
                result.Add(new(category, id, values.GetValueOrDefault("MainAB", ""), values.GetValueOrDefault("MainData", ""), values.GetValueOrDefault("MainData02", "")) { Values = values, Archive = path });
            }
        }
        return result;
    }
    public string Bundle(string relative, string archive = "")
    {
        // Validate even archive references; no archive path is ever used as an extraction destination.
        string physical = NativePreviewService.ResolveBundlePath(Path.Combine(game, "abdata"), relative);
        if (archive.Length > 0 && Extract(archive, relative) is { } owned) return owned;
        if (File.Exists(physical)) return physical;
        // Lists can reference bundles supplied by a different installed zipmod.
        EnsureBundles();
        if (!bundles!.ContainsKey(relative.Replace('\\', '/')) && !scanned) { RefreshOnce("bundle|" + relative.Replace('\\', '/')); EnsureBundles(); }
        if (bundles!.TryGetValue(relative.Replace('\\', '/'), out var others))
            foreach (var other in others) if (Extract(other, relative) is { } found) return found;
        throw new FileNotFoundException("游戏及已安装 MOD 中缺少资源包：" + relative);
    }
    // Sideloader redirects individual exported assets even when the vanilla
    // bundle exists. A replacement can contain only some of that bundle's assets.
    internal (string Path, string Archive) AssetBundle(string relative, string asset, string archive = "")
    {
        _ = NativePreviewService.ResolveBundlePath(Path.Combine(game, "abdata"), relative);
        EnsureBundles();
        if (bundles!.TryGetValue(relative.Replace('\\', '/'), out var candidates))
            foreach (string candidate in candidates)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Extract(candidate, relative) is not { } path) continue;
                var names = metadata.Get("bundle-exports-v1", PreviewMetadataCache.Stamp(path), () =>
                { using var reader = new NativeAssetBundle(path); return reader.ExportNames(); }, cancellation);
                if (names.Any(n => n.Equals(asset, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(n).Equals(asset, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileNameWithoutExtension(n).Equals(asset, StringComparison.OrdinalIgnoreCase))) return (path, candidate);
            }
        return (Bundle(relative, archive), archive);
    }
    public string? LooseTexture(string bundle,string asset,string archive="")
    {
        string relative=Path.ChangeExtension(bundle,null).Replace('\\','/')+"/"+asset+".png";
        string physical=NativePreviewService.ResolveBundlePath(Path.Combine(game,"abdata"),relative);
        if(archive.Length>0&&Extract(archive,relative) is {} own)return own;
        if(File.Exists(physical))return physical;
        EnsureBundles();
        return bundles!.TryGetValue(relative,out var others)?others.Select(other=>Extract(other,relative)).FirstOrDefault(p=>p!=null):null;
    }
    private void EnsureBundles()
    {
        if (bundles != null) return;
        using var timing = PreviewPerformance.Measure("modCatalog");
        EnsureIndex();
        // Validate archive metadata without opening their ZIP directories. An updated or
        // deleted archive changes this key even if the user has not refreshed manifests.
        var sources = new List<(string Path, string Stamp)>();
        foreach (var mod in UsableMods())
        {
            cancellation.ThrowIfCancellationRequested();
            var file = new FileInfo(mod.Path);
            if (!file.Exists) continue;
            sources.Add((mod.Path, file.FullName.ToUpperInvariant() + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks));
        }
        bool complete = true;
        try
        {
            var saved = metadata.Get("bundle-catalog-v2", "active-mods-v1|" + index!.UpdatedUtc.Ticks + "|" + string.Join("\n", sources.Select(s => s.Stamp)), () =>
            {
                var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in sources)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var entry in DirectoryOf(source.Path).Entries)
                        {
                            string name = entry.Name.Replace('\\', '/');
                            if (name.StartsWith("abdata/",StringComparison.OrdinalIgnoreCase) &&
                                (name.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                                result[name[7..]] = [..result.GetValueOrDefault(name[7..],[]),source.Path];
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { complete = false; }
                }
                // Do not persist an incomplete inventory as a successful negative lookup.
                if (!complete) throw new IncompleteCatalogException(result);
                return result;
            }, cancellation);
            bundles = new(saved, StringComparer.OrdinalIgnoreCase);
        }
        catch (IncompleteCatalogException error) { bundles = error.Entries; }
    }
    private sealed class IncompleteCatalogException(Dictionary<string, string[]> entries) : IOException
    { internal Dictionary<string, string[]> Entries { get; } = entries; }
    private (string Stamp, ModArchiveEntry[] Entries) DirectoryOf(string archive)
    {
        cancellation.ThrowIfCancellationRequested();
        var file = new FileInfo(archive);
        string stamp = file.FullName.ToUpperInvariant() + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        if (directories.TryGetValue(archive, out var saved) && saved.Stamp==stamp) return saved;
        lists.Remove(archive);
        EnsureIndex();
        var indexed = index!.Files.FirstOrDefault(m => m.Path.Equals(file.FullName, StringComparison.OrdinalIgnoreCase));
        var entries = indexed?.ArchiveEntries != null && indexed.Size == file.Length && indexed.ModifiedUtcTicks == file.LastWriteTimeUtc.Ticks
            ? indexed.ArchiveEntries : PreviewArchiveIndex.Get(archive, metadata, cancellation);
        return directories[archive] = (stamp, entries);
    }
    private string? Extract(string archive, string relative)
    {
        using var timing = PreviewPerformance.Measure("modExtract");
        var directory = DirectoryOf(archive);
        string wanted = "abdata/" + relative.Replace('\\','/');
        var entry = directory.Entries.FirstOrDefault(e => e.Name.Replace('\\','/').Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        if (entry.Length is < 1 or > 512 * 1024 * 1024) throw new InvalidDataException("MOD 资源大小超限。");
        var info = new FileInfo(archive);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + entry.Name)));
        string target = Path.Combine(cache, key + (relative.EndsWith(".png",StringComparison.OrdinalIgnoreCase)?".png":".unity3d"));
        if (File.Exists(target) && new FileInfo(target).Length == entry.Length) return target;
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            cancellation.ThrowIfCancellationRequested();
            PreviewPerformance.Count("archiveOpens");
            using var zip = ZipFile.OpenRead(archive);
            var original = zip.GetEntry(entry.Name) ?? throw new IOException("MOD 归档在读取期间发生变化。");
            using (var source = original.Open()) using (var output = File.Create(temporary))
            {
                byte[] buffer = new byte[128 * 1024]; int length; long copied = 0;
                while ((length = source.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); copied += length; if (copied > entry.Length) throw new InvalidDataException("MOD 资源长度不匹配。"); output.Write(buffer, 0, length); }
                if (copied != entry.Length) throw new InvalidDataException("MOD 资源截断。");
            }
            if (PreviewMetadataCache.Stamp(archive) != directory.Stamp) throw new IOException("MOD 归档在读取期间发生变化。");
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, target, true); PreviewPerformance.Count("extractedBytes", entry.Length); return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
