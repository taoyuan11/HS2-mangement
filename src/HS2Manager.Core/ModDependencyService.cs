using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml;
using System.Xml.Linq;

namespace HS2Manager.Core;

public sealed record ModMigration(string Kind, string OldGuid, string NewGuid, string Category, int OldSlot, int NewSlot);
public sealed class ModFileInfo
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Error { get; set; } = "";
    public bool Compatible { get; set; } = true;
    public List<ModMigration> Migrations { get; set; } = [];
}
public sealed class ModIndex
{
    public string GamePath { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public bool Complete { get; set; }
    public bool MigrationEnabled { get; set; } = true;
    public List<ModFileInfo> Files { get; set; } = [];
    public Dictionary<string, string> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; set; } = [];
}

public sealed class ModDependencyService(CardRepository repository)
{
    public ModIndex? Index { get; private set; } = repository.LoadMetadata<ModIndex>("mods-v1");
    private Dictionary<string, ModFileInfo> byGuid = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<ModMigration>> migrations = new(StringComparer.OrdinalIgnoreCase);
    public async Task<ModIndex> ScanAsync(string gamePath, IProgress<WorkProgress>? progress, CancellationToken cancellation)
    {
        return await Task.Run(async () =>
        {
            var index = new ModIndex { GamePath = Path.GetFullPath(gamePath), UpdatedUtc = DateTime.UtcNow, Complete = true };
            var roots = new List<string> { Path.Combine(gamePath, "mods") };
            string config = Path.Combine(gamePath, "BepInEx", "config", "com.bepis.bepinex.sideloader.cfg");
            if (File.Exists(config))
            {
                foreach (var line in File.ReadLines(config))
                {
                    if (line.StartsWith("Additional mods directory =", StringComparison.OrdinalIgnoreCase))
                    { var additional = line[(line.IndexOf('=') + 1)..].Trim(); if (additional.Length > 0) roots.Add(Path.GetFullPath(additional, gamePath)); }
                    if (line.StartsWith("Migration enabled =", StringComparison.OrdinalIgnoreCase)) index.MigrationEnabled = !line.EndsWith("false", StringComparison.OrdinalIgnoreCase);
                }
            }
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
                    { cancellation.ThrowIfCancellationRequested(); if (Path.GetExtension(path).ToLowerInvariant() is ".zipmod" or ".zip") files.Add(path); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { index.Complete = false; index.Warnings.Add(root + "：" + ex.Message); }
            }
            var cached = Index?.GamePath.Equals(index.GamePath, StringComparison.OrdinalIgnoreCase) == true
                ? Index.Files.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ModFileInfo>(StringComparer.OrdinalIgnoreCase);
            var results = new ConcurrentBag<ModFileInfo>(); int completed = 0;
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellation }, (path, ct) =>
            {
                ct.ThrowIfCancellationRequested(); var file = new FileInfo(path); ModFileInfo entry;
                try
                {
                    if (cached.TryGetValue(path, out var old) && old.Size == file.Length && old.ModifiedUtcTicks == file.LastWriteTimeUtc.Ticks && old.Error.Length == 0) entry = old;
                    else entry = ReadManifest(path);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException)
                { entry = new ModFileInfo { Path = path, Error = ex.Message }; }
                results.Add(entry); int done = Interlocked.Increment(ref completed);
                if (done % 10 == 0 || done == files.Count) progress?.Report(new(done, files.Count, "读取 MOD 清单 " + done + " / " + files.Count));
                return ValueTask.CompletedTask;
            });
            index.Files = results.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var error in index.Files.Where(x => x.Error.Length > 0)) { index.Complete = false; index.Warnings.Add(Path.GetFileName(error.Path) + "：" + error.Error); }
            ReadPlugins(Path.Combine(gamePath, "BepInEx", "plugins"), index, cancellation);
            repository.StoreMetadata("mods-v1", index); Index = index; RebuildLookup(); return index;
        }, cancellation);
    }
    private static ModFileInfo ReadManifest(string path)
    {
        var file = new FileInfo(path);
        var result = new ModFileInfo { Path = path, Size = file.Length, ModifiedUtcTicks = file.LastWriteTimeUtc.Ticks };
        using var archive = ZipFile.OpenRead(path);
        var manifest = archive.Entries.FirstOrDefault(x => x.FullName.Equals("manifest.xml", StringComparison.OrdinalIgnoreCase));
        if (manifest == null) { result.Compatible = false; return result; }
        if (manifest.Length > 2 * 1024 * 1024) throw new InvalidDataException("MOD 清单过大");
        using var stream = manifest.Open();
        using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        var root = XDocument.Load(xml).Root ?? throw new InvalidDataException("空 MOD 清单");
        if (root.Attribute("schema-ver")?.Value is not (null or "1")) throw new InvalidDataException("尚不支持的 MOD 清单版本");
        result.Guid = root.Element("guid")?.Value.Trim() ?? "";
        if (result.Guid.Length == 0) throw new InvalidDataException("MOD 清单缺少 GUID");
        result.Name = root.Element("name")?.Value.Trim() ?? result.Guid; result.Version = root.Element("version")?.Value.Trim() ?? "";
        var games = root.Elements().Where(x => x.Name.LocalName.Equals("game", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToList();
        result.Compatible = games.Count == 0 || games.Any(x => x is "ai" or "hs2" || x.Contains("honeyselect2") || x.Contains("honey select 2") || x.Contains("ai girl") || x.Contains("ai-girl") || x.Contains("ai shoujo") || x.Contains("ai-shoujo") || x.Contains("ai-syoujyo") || x.Contains("aisyoujyo"));
        foreach (var entry in root.Element("migrationInfo")?.Elements("info") ?? [])
        {
            string old = entry.Attribute("guidOld")?.Value ?? ""; if (old.Length == 0) continue;
            result.Migrations.Add(new(entry.Attribute("migrationType")?.Value ?? "Migrate", old, entry.Attribute("guidNew")?.Value ?? result.Guid,
                entry.Attribute("category")?.Value ?? "", int.TryParse(entry.Attribute("idOld")?.Value, out int oldSlot) ? oldSlot : -1,
                int.TryParse(entry.Attribute("idNew")?.Value, out int newSlot) ? newSlot : -1));
        }
        return result;
    }
    private static void ReadPlugins(string folder, ModIndex index, CancellationToken cancellation)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    using var file = File.OpenRead(path); using var pe = new PEReader(file);
                    if (!pe.HasMetadata) continue; var metadata = pe.GetMetadataReader();
                    foreach (var typeHandle in metadata.TypeDefinitions)
                        foreach (var attributeHandle in metadata.GetTypeDefinition(typeHandle).GetCustomAttributes())
                        {
                            var attribute = metadata.GetCustomAttribute(attributeHandle);
                            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                            var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                            if (metadata.GetString(type.Namespace) != "BepInEx" || metadata.GetString(type.Name) != "BepInPlugin") continue;
                            var blob = metadata.GetBlobReader(attribute.Value); if (blob.ReadUInt16() != 1) continue;
                            var guid = blob.ReadSerializedString(); if (!string.IsNullOrEmpty(guid)) index.Plugins[guid] = Path.GetFileName(path);
                        }
                }
                catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { index.Warnings.Add("插件扫描：" + Path.GetFileName(path) + " " + ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { index.Warnings.Add("插件目录：" + ex.Message); }
    }
    private void RebuildLookup()
    {
        byGuid = new(StringComparer.OrdinalIgnoreCase); migrations = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Index?.Files ?? [])
        {
            if (!file.Compatible || file.Error.Length > 0 || file.Guid.Length == 0) continue;
            byGuid.TryAdd(file.Guid, file);
            foreach (var migration in file.Migrations)
            { if (!migrations.TryGetValue(migration.OldGuid, out var list)) migrations[migration.OldGuid] = list = []; list.Add(migration); }
        }
    }
    public List<DependencyResult> Diagnose(CardRecord card, string gamePath, bool sourceAvailable = true)
    {
        if (byGuid.Count == 0 && Index != null) RebuildLookup();
        bool usable = sourceAvailable && Index != null && string.Equals(Index.GamePath, Path.GetFullPath(gamePath), StringComparison.OrdinalIgnoreCase);
        var results = new List<DependencyResult>();
        foreach (var group in card.Dependencies.GroupBy(x => x.Guid, StringComparer.OrdinalIgnoreCase))
        {
            if (!usable) { results.Add(new(group.Key, DependencyState.Unknown, "请先更新 MOD 索引，或检查游戏目录连接")); continue; }
            if (byGuid.TryGetValue(group.Key, out var mod)) { results.Add(new(group.Key, DependencyState.Found, mod.Name + " " + mod.Version)); continue; }
            var matches = group.Select(reference => ResolveMigration(reference, new HashSet<string>(StringComparer.OrdinalIgnoreCase))).ToList();
            if (matches.All(x => x == DependencyState.Found)) results.Add(new(group.Key, DependencyState.Found, "已通过 MOD 迁移映射满足依赖"));
            else if (matches.Any(x => x == DependencyState.Unknown) || Index!.Complete == false) results.Add(new(group.Key, DependencyState.Unknown, "清单扫描不完整，或迁移规则需要游戏确认"));
            else results.Add(new(group.Key, DependencyState.Missing, "当前已扫描的 MOD 清单中未找到"));
        }
        foreach (var id in card.PluginDataIds)
        {
            string pluginId = id is "com.bepis.sideloader.universalautoresolver" or "EC.Core.Sideloader.UniversalAutoResolver" ? "com.bepis.bepinex.sideloader" : id;
            if (usable && Index!.Plugins.TryGetValue(pluginId, out var file)) results.Add(new(id, DependencyState.Found, "插件文件：" + file + "（未验证启用状态）"));
            else results.Add(new(id, DependencyState.Unknown, "扩展数据标识；无法仅凭标识确认插件依赖"));
        }
        if (!card.DependenciesComplete) results.Add(new("部分扩展数据", DependencyState.Unknown, card.Error));
        return results;
    }
    private DependencyState ResolveMigration(ModReference reference, HashSet<string> visited)
    {
        if (byGuid.ContainsKey(reference.Guid)) return DependencyState.Found;
        if (!visited.Add(reference.Guid) || visited.Count > 16) return DependencyState.Unknown;
        if (Index?.MigrationEnabled != true || !migrations.TryGetValue(reference.Guid, out var rules)) return DependencyState.Missing;
        bool unknown = false;
        foreach (var rule in rules)
        {
            bool all = rule.Kind.EndsWith("All", StringComparison.OrdinalIgnoreCase);
            if (!all)
            {
                if (rule.OldSlot != reference.Slot) continue;
                if (!NativeModCategories.TryParse(rule.Category, out int category)) { unknown = true; continue; }
                if (category != reference.Category) continue;
            }
            if (rule.Kind is "Strip" or "StripAll") return DependencyState.Found;
            if (rule.Kind is not ("Migrate" or "MigrateAll")) { unknown = true; continue; }
            var state = ResolveMigration(reference with { Guid = rule.NewGuid, Slot = all ? reference.Slot : rule.NewSlot }, new(visited, StringComparer.OrdinalIgnoreCase));
            if (state == DependencyState.Found) return state; unknown |= state == DependencyState.Unknown;
        }
        return unknown ? DependencyState.Unknown : DependencyState.Missing;
    }
}
