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
    public NativeAssetEntry Resolve(ModReference reference)
    {
        var repository = new CardRepository(data);
        index ??= repository.LoadMetadata<ModIndex>("mods-v1");
        if (index == null || !index.GamePath.Equals(Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase)) Scan();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int depth = 0; depth < 16; depth++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(reference.Guid + ":" + reference.Category + ":" + reference.Slot)) break;
            var mod = index!.Files.LastOrDefault(m => m.Compatible && m.Error.Length == 0 && m.Guid.Equals(reference.Guid, StringComparison.OrdinalIgnoreCase) && File.Exists(m.Path));
            if (mod != null)
            {
                if (!lists.TryGetValue(mod.Path, out var entries)) lists[mod.Path] = entries = ReadLists(mod.Path);
                return entries.LastOrDefault(e => e.Category == reference.Category && e.Id == reference.Slot)
                    ?? throw new InvalidDataException($"MOD {reference.Guid} 中找不到类别 {reference.Category} / Slot {reference.Slot}。");
            }
            if (!scanned) { Scan(); visited.Clear(); continue; }
            var rule = index.MigrationEnabled ? index.Files.SelectMany(m => m.Migrations).LastOrDefault(m =>
                m.OldGuid.Equals(reference.Guid, StringComparison.OrdinalIgnoreCase) && (m.Kind == "MigrateAll" || m.Kind == "Migrate" && m.OldSlot == reference.Slot && int.TryParse(m.Category, out int cat) && cat == reference.Category)) : null;
            if (rule == null) break;
            reference = reference with { Guid = rule.NewGuid, Slot = rule.Kind == "MigrateAll" ? reference.Slot : rule.NewSlot };
        }
        throw new FileNotFoundException($"缺少 MOD {reference.Guid}（类别 {reference.Category} / Slot {reference.Slot}）。");
        void Scan()
        {
            progress?.Report("正在建立 MOD 模型资源索引…");
            var service = new ModDependencyService(repository);
            index = service.ScanAsync(game, null, cancellation).GetAwaiter().GetResult(); scanned = true;
        }
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
        if (archive.Length == 0) return physical;
        using var zip = ZipFile.OpenRead(archive);
        string wanted = "abdata/" + relative.Replace('\\','/');
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\','/').Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return File.Exists(physical) ? physical : throw new FileNotFoundException("MOD 包内缺少资源：" + relative);
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
