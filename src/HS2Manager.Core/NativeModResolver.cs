using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace HS2Manager.Core;

internal sealed class NativeModResolver(string game, string data, string cache, IProgress<string>? progress, CancellationToken cancellation)
{
    private ModIndex? index;
    private bool scanned;
    private readonly Dictionary<string, List<NativeAssetEntry>> lists = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? bundles;
    private CardRepository Repository => new(data);
    public NativeAssetEntry Resolve(ModReference reference)
    {
        EnsureIndex();
        var result = Find(reference, new(StringComparer.OrdinalIgnoreCase), 0);
        if (result == null && !scanned)
        {
            Scan(); result = Find(reference, new(StringComparer.OrdinalIgnoreCase), 0);
        }
        if (result != null) return result;
        if (UsableMods().Any(m => m.Guid.Equals(reference.Guid, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"MOD {reference.Guid} 已安装，但没有类别 {reference.Category} / Slot {reference.Slot}，且未找到可用迁移。");
        throw new FileNotFoundException($"缺少 MOD {reference.Guid}（类别 {reference.Category} / Slot {reference.Slot}）。");
    }
    public NativeAssetEntry? FindSavedName(int category,string name)
    {
        if(string.IsNullOrWhiteSpace(name))return null;
        EnsureIndex();
        var matches=new Dictionary<string,NativeAssetEntry>(StringComparer.OrdinalIgnoreCase);
        foreach(var mod in UsableMods().OrderByDescending(m=>Version.TryParse(m.Version.TrimStart('v','V'),out var version)?version:new Version()))
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
        foreach (var mod in UsableMods().Where(m => m.Guid.Equals(reference.Guid, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => Version.TryParse(m.Version.TrimStart('v', 'V'), out var version) ? version : new Version()).ThenByDescending(m => m.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(mod.Path)) continue;
            if (!lists.TryGetValue(mod.Path, out var entries)) lists[mod.Path] = entries = ReadLists(mod.Path);
            var entry = entries.LastOrDefault(e => e.Category == reference.Category && e.Id == reference.Slot);
            if (entry != null) return entry;
        }
        return null;
    }
    private IEnumerable<ModFileInfo> UsableMods() => index!.Files.Where(m => m.Compatible && m.Error.Length == 0);
    private void EnsureIndex()
    {
        index ??= Repository.LoadMetadata<ModIndex>("mods-v1");
        if (index == null || !index.GamePath.Equals(Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase)) Scan();
    }
    private void Scan()
    {
        progress?.Report("正在更新 MOD 模型资源索引…");
        index = new ModDependencyService(Repository).ScanAsync(game, null, cancellation).GetAwaiter().GetResult();
        scanned = true; lists.Clear(); bundles = null;
    }
    private List<NativeAssetEntry> ReadLists(string path)
    {
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
        if (!bundles!.ContainsKey(relative.Replace('\\', '/')) && !scanned) { Scan(); EnsureBundles(); }
        if (bundles!.TryGetValue(relative.Replace('\\', '/'), out var other) && Extract(other, relative) is { } found) return found;
        throw new FileNotFoundException("游戏及已安装 MOD 中缺少资源包：" + relative);
    }
    private void EnsureBundles()
    {
        if (bundles != null) return;
        EnsureIndex(); bundles = new(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in UsableMods())
        {
            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(mod.Path)) continue;
            try
            {
                using var zip = ZipFile.OpenRead(mod.Path);
                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (name.StartsWith("abdata/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)) bundles[name[7..]] = mod.Path;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { /* An unrelated broken archive must not stop bundle discovery. */ }
        }
    }
    private string? Extract(string archive, string relative)
    {
        using var zip = ZipFile.OpenRead(archive);
        string wanted = "abdata/" + relative.Replace('\\','/');
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\','/').Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        if (entry.Length is < 1 or > 512 * 1024 * 1024) throw new InvalidDataException("MOD 资源大小超限。");
        var info = new FileInfo(archive);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + entry.FullName)));
        string target = Path.Combine(cache, key + ".unity3d");
        if (File.Exists(target) && new FileInfo(target).Length == entry.Length) return target;
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var source = entry.Open()) using (var output = File.Create(temporary))
            {
                byte[] buffer = new byte[128 * 1024]; int length; long copied = 0;
                while ((length = source.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); copied += length; if (copied > entry.Length) throw new InvalidDataException("MOD 资源长度不匹配。"); output.Write(buffer, 0, length); }
                if (copied != entry.Length) throw new InvalidDataException("MOD 资源截断。");
            }
            File.Move(temporary, target, true); return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
