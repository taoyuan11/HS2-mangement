using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.VisualBasic.FileIO;

namespace HS2Manager.Core;

public sealed record ExportShader(string Name, string[] Bundles);
public sealed class ModExportMetadata
{
    public List<ExportShader> Shaders { get; set; } = [];
    public bool BonesFramework { get; set; }
    internal static ModExportMetadata Read(XElement root) => new()
    {
        BonesFramework = root.Descendants().Any(e => e.Name.LocalName.Equals("BonesFramework", StringComparison.OrdinalIgnoreCase)),
        Shaders = root.Elements().Where(e => e.Name.LocalName is "MaterialEditor" or "HS2_MaterialEditor")
            .SelectMany(e => e.Descendants("Shader")).Select(e => new ExportShader((string?)e.Attribute("Name") ?? "",
                e.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "AssetBundle" or "DefaultValueAssetBundle").Select(a => a.Value).Where(s => s.Length > 0).Distinct().ToArray())).ToList()
    };
}

internal sealed class CardExportDependencies
{
    private readonly string game;
    private readonly ModIndex index;
    private readonly CancellationToken cancellation;
    private readonly ModFileInfo[] mods;
    private readonly Dictionary<string, ModFileInfo> byGuid;
    private readonly Dictionary<string, ModFileInfo> bundles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ModFileInfo Mod, ExportShader Shader)> shaders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (PluginFileInfo File, PluginDefinition Definition)[]> plugins;
    private sealed record ListEntry(int Category, int Slot, Dictionary<string, string> Fields);
    private readonly Dictionary<string, ListEntry[]> lists = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? embeddedShaders;
    private HashSet<string>? stockBundles;
    private readonly List<string> stockWarnings = [];

    internal CardExportDependencies(string game, ModIndex index, CancellationToken cancellation)
    {
        this.game = game; this.index = index; this.cancellation = cancellation;
        mods = NativeModSelection.Select(index.Files).OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        byGuid = mods.ToDictionary(m => m.Guid, StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            foreach (var entry in mod.ArchiveEntries ?? []) bundles[entry.Name.Replace('\\', '/')] = mod;
            foreach (var shader in mod.ExportMetadata?.Shaders ?? []) shaders[shader.Name] = (mod, shader);
        }
        plugins = index.PluginFiles.SelectMany(f => f.Plugins.Select(p => (File: f, Definition: p)))
            .GroupBy(p => p.Definition.Guid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => PluginFileIndex.VersionOf(p.Definition.Version)).ThenBy(p => p.File.Path, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    internal (List<ExportCandidate> Files, List<string> Issues, List<string> Warnings) Collect(CardRecord card, CardExportLevel level)
    {
        bool includePlugins = level >= CardExportLevel.WithPlugins, includeResources = level == CardExportLevel.Full;
        var files = new List<ExportCandidate>(); var issues = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var resources = CardExportResources.Read(card, cancellation, level);
        var visitedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!index.Complete) issues.Add("MOD 索引扫描不完整；未找到的资源仍需确认。");
        if (includePlugins)
            foreach (string warning in index.Warnings.Where(w => w.StartsWith("插件"))) issues.Add(warning);
        foreach (var reference in card.Dependencies)
        {
            var resolution = Resolve(reference, new(StringComparer.OrdinalIgnoreCase));
            foreach (var mod in resolution.Mods) AddMod(mod);
            if (!resolution.Found) issues.Add("缺失或无法确认 MOD：" + reference.Guid + $"（类别 {reference.Category} / Slot {reference.Slot}）");
            else if (resolution.Reference is { } effective) CheckListEntry(byGuid[effective.Guid], effective);
        }
        if (card.Dependencies.Count > 0) AddPlugin("com.bepis.bepinex.sideloader");
        if (card.PluginDataIds.Count > 0) AddPlugin("com.bepis.bepinex.extendedsave");
        foreach (string id in card.PluginDataIds)
        {
            if (!includePlugins) break;
            if (resources.InactivePlugins.Contains(id)) continue;
            string guid = PluginFileIndex.PluginGuid(id);
            if (guid == id && !plugins.ContainsKey(guid))
            {
                var aliases = index.PluginFiles.SelectMany(f => f.Plugins).Where(p => p.DataIds.Contains(id, StringComparer.Ordinal)).Select(p => p.Guid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (aliases.Length == 1) guid = aliases[0];
            }
            AddPlugin(guid);
            if (includeResources && !CardExportResources.KnownExtension(id)) issues.Add("未确认扩展的外部资源需求：" + id);
        }
        foreach (string warning in resources.Issues) issues.Add(warning);
        if (includeResources)
            foreach (string path in resources.Paths) AddUserData(path);
        foreach (string shader in resources.Shaders)
        {
            if (shaders.TryGetValue(shader, out var source))
            {
                AddMod(source.Mod);
                foreach (string bundle in source.Shader.Bundles) AddBundle(source.Mod, bundle);
            }
            else if (includeResources && !EmbeddedShaders().Contains(shader)) issues.Add("未确认自定义 Shader 的资源来源：" + shader);
        }
        if (includeResources)
            foreach (string warning in stockWarnings) issues.Add(warning);
        return (files.Distinct().ToList(), issues.ToList(), warnings.ToList());

        void AddMod(ModFileInfo mod)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!visitedMods.Add(mod.Path)) return;
            var stat = new FileInfo(mod.Path);
            if (!stat.Exists || stat.Length != mod.Size || stat.LastWriteTimeUtc.Ticks != mod.ModifiedUtcTicks)
            { issues.Add("MOD 在索引后发生变化或已缺失，请重新导出：" + mod.Path); return; }
            string relative = ExportPaths.Within(mod.Path, Path.Combine(game, "mods"))
                ? Path.GetRelativePath(game, mod.Path) : Path.Combine("mods", "外部MOD", Path.GetFileName(mod.Path));
            files.Add(new(mod.Path, relative, CardExportFileKind.Mod));
            if (mod.ExportMetadata?.BonesFramework == true) AddPlugin("com.joan6694.illusionplugins.bonesframework");
        }
        void CheckListEntry(ModFileInfo mod, ModReference reference)
        {
            try
            {
                // The entire archive is copied, but only rows referenced by this card are its dependencies.
                var entry = ReadLists(mod).LastOrDefault(e => e.Category == reference.Category && e.Slot == reference.Slot);
                if (entry == null)
                { issues.Add($"MOD 清单项缺失：{reference.Guid}（类别 {reference.Category} / Slot {reference.Slot}）"); return; }
                foreach (var (column, bundle) in entry.Fields.Where(p => p.Key.EndsWith("AB", StringComparison.Ordinal) && p.Value.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)))
                {
                    bool thumbnail = column == "ThumbAB", texture = column != "MainAB";
                    string[] assets;
                    if (column == "MainAB")
                    {
                        texture = !entry.Fields.Any(p => p.Key is "MainData" or "MainData02" && p.Value is not ("" or "0"));
                        assets = texture ? entry.Fields.Where(p => p.Key is "MainTex" or "AddTex" or "GlossTex" or "NormalMapTex" or "OcclusionMapTex" &&
                            entry.Fields.GetValueOrDefault(p.Key + "AB", "0") is "" or "0").Select(p => p.Value).ToArray() : [];
                    }
                    else assets = [entry.Fields.GetValueOrDefault(thumbnail ? "ThumbTex" : column[..^2], "")];
                    AddBundle(mod, bundle, assets.Where(a => a is not ("" or "0")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), texture, thumbnail);
                }
            }
            catch (Exception ex) when (ExportPaths.IsFileError(ex) || ex is MalformedLineException)
            { issues.Add("无法完整检查 MOD 的资源引用：" + Path.GetFileName(mod.Path) + "：" + ex.Message); }
        }
        void AddBundle(ModFileInfo owner, string relative, string[]? assets = null, bool texture = false, bool thumbnail = false)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative == "0" || relative.StartsWith("Resources.", StringComparison.Ordinal)) return;
            string normalized = relative.Replace('\\', '/');
            if (!normalized.StartsWith("abdata/", StringComparison.OrdinalIgnoreCase)) normalized = "abdata/" + normalized;
            try
            {
                string physical = ExportPaths.Destination(game, normalized);
                if (texture && assets is { Length: > 0 })
                {
                    // Sideloader can supply a logical bundle's textures as <bundle-without-extension>/<asset>.png.
                    // Check each asset, not merely whether that directory contains any PNG.
                    bool allFound = true;
                    foreach (string asset in assets)
                    {
                        string png = normalized[..^Path.GetExtension(normalized).Length] + "/" + asset + ".png";
                        string loose = ExportPaths.Destination(game, png);
                        if (owner.ArchiveEntries?.Any(e => e.Name.Replace('\\', '/').Equals(png, StringComparison.OrdinalIgnoreCase)) == true) continue;
                        if (File.Exists(loose)) { if (includeResources) files.Add(new(loose, png, CardExportFileKind.Resource)); }
                        else if (bundles.TryGetValue(png, out var pngProvider)) AddMod(pngProvider);
                        else allFound = false;
                    }
                    if (allFound) return;
                }
                if (owner.ArchiveEntries?.Any(e => e.Name.Replace('\\', '/').Equals(normalized, StringComparison.OrdinalIgnoreCase)) == true) return;
                // The game gives an installed loose bundle precedence over other archives.
                if (File.Exists(physical))
                {
                    // Official bundles are supplied by the target game. Unlisted loose bundles can be manually installed MODs.
                    if (includeResources && !StockBundles().Contains(normalized)) files.Add(new(physical, normalized, CardExportFileKind.Resource));
                    return;
                }
                if (bundles.TryGetValue(normalized, out var provider)) AddMod(provider);
                else if (includeResources)
                {
                    if (thumbnail) warnings.Add("菜单缩略图缺失，不影响人物外观：" + normalized + "（" + owner.Guid + "）");
                    else issues.Add("资源包缺失：" + normalized + "（" + owner.Guid + "）");
                }
            }
            catch (Exception ex) when (ExportPaths.IsFileError(ex)) { if (includeResources) issues.Add("资源包路径无效：" + relative + "：" + ex.Message); }
        }
        void AddUserData(string path)
        {
            try
            {
                string normalized = path.Replace('/', Path.DirectorySeparatorChar);
                bool absolute = Path.IsPathRooted(normalized);
                string source = absolute ? Path.GetFullPath(normalized) : ExportPaths.Destination(game, normalized);
                if (!ExportPaths.Within(source, Path.Combine(game, "UserData")))
                { issues.Add("无法移植的外部资源路径：" + path); return; }
                if (absolute) issues.Add("卡片保存了绝对资源路径，复制到其它游戏位置后可能需重新指定：" + path);
                files.Add(new(source, Path.GetRelativePath(game, source), CardExportFileKind.Resource));
            }
            catch (Exception ex) when (ExportPaths.IsFileError(ex)) { issues.Add("外部资源路径无法确认：" + path + "：" + ex.Message); }
        }
        void AddPlugin(string guid, string minimumVersion = "", bool optional = false)
        {
            if (!includePlugins) return;
            cancellation.ThrowIfCancellationRequested();
            if (!plugins.TryGetValue(guid, out var candidates))
            { if (!optional) issues.Add("缺少插件或无法映射扩展标识：" + guid); return; }
            var selected = candidates[0];
            if (minimumVersion.Length > 0 && PluginFileIndex.VersionOf(selected.Definition.Version) < PluginFileIndex.VersionOf(minimumVersion))
                issues.Add($"插件版本不足：{guid}，需要 {minimumVersion}，找到 {selected.Definition.Version}");
            if (candidates.Any(c => c.File.Path != selected.File.Path && c.Definition.Version == selected.Definition.Version))
                issues.Add("同一插件 GUID 存在相同版本的多个文件，已按路径确定一个来源，需确认：" + guid);
            if (!visitedPlugins.Add(guid)) return;
            AddAssembly(selected.File);
        }
        void AddAssembly(PluginFileInfo assembly)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!visitedAssemblies.Add(assembly.Path)) return;
            var stat = new FileInfo(assembly.Path);
            if (!stat.Exists || stat.Length != assembly.Size || stat.LastWriteTimeUtc.Ticks != assembly.ModifiedUtcTicks)
            { issues.Add("插件在索引后发生变化或已缺失，请重新导出：" + assembly.Path); return; }
            if (!ExportPaths.Within(assembly.Path, Path.Combine(game, "BepInEx", "plugins")))
            { issues.Add("插件不在游戏 plugins 目录内：" + assembly.Path); return; }
            files.Add(new(assembly.Path, Path.GetRelativePath(game, assembly.Path), CardExportFileKind.Plugin));
            if (assembly.Error.Length > 0) issues.Add(assembly.Path + "：" + assembly.Error);
            // One DLL may contain several plugins. All of them will be loaded by BepInEx.
            foreach (var definition in assembly.Plugins)
                foreach (var dependency in definition.Dependencies) AddPlugin(dependency.Guid, dependency.MinimumVersion, dependency.Optional);
            foreach (var reference in assembly.References)
            {
                var candidates = index.PluginFiles.Where(f => f.AssemblyName.Equals(reference.Name, StringComparison.OrdinalIgnoreCase) &&
                        f.Plugins.All(p => plugins[p.Guid][0].File.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(f => f.Version == reference.Version).ThenByDescending(f => PluginFileIndex.VersionOf(f.Version)).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                if (candidates.Length > 0)
                {
                    var dependency = candidates[0];
                    if (PluginFileIndex.VersionOf(dependency.Version) < PluginFileIndex.VersionOf(reference.Version))
                        issues.Add("依赖库版本不足：" + reference.Name + " " + reference.Version);
                    AddAssembly(dependency);
                }
                else if (!BaseAssembly(reference.Name)) issues.Add("缺少依赖库：" + reference.Name + "（" + Path.GetFileName(assembly.Path) + "）");
            }
            foreach (string native in assembly.NativeReferences.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (SystemNativeLibrary(native)) continue;
                string fileName = native.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? native : native + ".dll";
                if (fileName != Path.GetFileName(fileName)) { issues.Add("原生依赖库路径需确认：" + native); continue; }
                var match = index.PluginFiles.Where(f => Path.GetFileName(f.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => Path.GetDirectoryName(f.Path) == Path.GetDirectoryName(assembly.Path)).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (match != null) files.Add(new(match.Path, Path.GetRelativePath(game, match.Path), CardExportFileKind.Plugin));
                else if (!File.Exists(Path.Combine(game, "HoneySelect2_Data", "Plugins", fileName)) && !File.Exists(Path.Combine(game, fileName)))
                    issues.Add("缺少或无法确认原生依赖库：" + native + "（" + Path.GetFileName(assembly.Path) + "）");
            }
            if (includeResources) AddCompanions(assembly);
        }
        void AddCompanions(PluginFileInfo assembly)
        {
            string folder = Path.GetDirectoryName(assembly.Path)!, stem = Path.GetFileNameWithoutExtension(assembly.Path);
            try
            {
                // Explicit ownership: same-stem sidecars/directories, or a folder containing only this plugin.
                foreach (var path in Directory.EnumerateFiles(folder).Where(p => Path.GetFileNameWithoutExtension(p).Equals(stem, StringComparison.OrdinalIgnoreCase) && ResourceFile(p))) Add(path);
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string named = Path.Combine(folder, stem); if (Directory.Exists(named)) roots.Add(named);
                if (!folder.Equals(Path.Combine(game, "BepInEx", "plugins"), StringComparison.OrdinalIgnoreCase) &&
                    index.PluginFiles.Where(f => f.Plugins.Count > 0 && ExportPaths.Within(f.Path, folder)).All(f => f.Path.Equals(assembly.Path, StringComparison.OrdinalIgnoreCase))) roots.Add(folder);
                foreach (string root in roots)
                    foreach (string path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }).Where(ResourceFile)) Add(path);
            }
            catch (Exception ex) when (ExportPaths.IsFileError(ex)) { issues.Add("插件配套资源无法完整读取：" + stem + "：" + ex.Message); }
            void Add(string path) => files.Add(new(path, Path.GetRelativePath(game, path), CardExportFileKind.Resource));
        }
    }

    private (bool Found, List<ModFileInfo> Mods, ModReference? Reference) Resolve(ModReference reference, HashSet<string> visited)
    {
        cancellation.ThrowIfCancellationRequested();
        string key = reference.Guid + ":" + reference.Category + ":" + reference.Slot;
        if (visited.Count > 32 || !visited.Add(key)) return (false, [], null);
        if (index.MigrationEnabled)
        {
            var rules = mods.SelectMany(m => m.Migrations.Select(rule => (Owner: m, Rule: rule)))
                .Where(r => r.Rule.OldGuid.Equals(reference.Guid, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Rule.Kind.EndsWith("All", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            foreach (var (owner, rule) in rules)
            {
                bool all = rule.Kind.EndsWith("All", StringComparison.OrdinalIgnoreCase);
                if (!all && (rule.OldSlot != reference.Slot || !NativeModCategories.Matches(rule.Category, reference.Category))) continue;
                if (rule.Kind is "Strip" or "StripAll") return (true, [owner], null);
                if (rule.Kind is not ("Migrate" or "MigrateAll")) continue;
                var child = Resolve(reference with { Guid = rule.NewGuid, Slot = all ? reference.Slot : rule.NewSlot }, new(visited, StringComparer.OrdinalIgnoreCase));
                if (child.Found) return (true, child.Mods.Prepend(owner).Distinct().ToList(), child.Reference);
            }
        }
        return byGuid.TryGetValue(reference.Guid, out var mod) ? (true, [mod], reference) : (false, [], null);
    }
    private ListEntry[] ReadLists(ModFileInfo mod)
    {
        if (lists.TryGetValue(mod.Path, out var cached)) return cached;
        var entries = new List<ListEntry>();
        using var zip = ZipFile.OpenRead(mod.Path);
        foreach (var entry in zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith("abdata/list/characustom/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("MOD 列表过大。");
            using var input = entry.Open(); using var csv = new TextFieldParser(input, Encoding.UTF8, true) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
            csv.SetDelimiters(","); int rows = 0;
            var first = csv.ReadFields();
            if (first == null || !int.TryParse(first.FirstOrDefault(), out int category)) throw new InvalidDataException("MOD 清单类别无效。");
            string[]? headers = null;
            for (int i = 0; i < 8 && !csv.EndOfData; i++) { var row = csv.ReadFields(); if (row?.FirstOrDefault() == "ID") { headers = row; break; } }
            if (headers == null) throw new InvalidDataException("MOD 清单缺少 ID 表头。");
            while (!csv.EndOfData)
            {
                cancellation.ThrowIfCancellationRequested(); if (++rows > 100000) throw new InvalidDataException("MOD 列表行数超限。");
                var values = csv.ReadFields(); if (values == null || !int.TryParse(values.FirstOrDefault(), out int slot)) continue;
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < Math.Min(headers.Length, values.Length); i++) if (headers[i].Length > 0) fields[headers[i]] = values[i];
                entries.Add(new(category, slot, fields));
            }
        }
        return lists[mod.Path] = entries.ToArray();
    }
    private bool BaseAssembly(string name) => name is "mscorlib" or "netstandard" or "System" or "Microsoft.CSharp" or "BepInEx" or "0Harmony" or "Assembly-CSharp" or "Assembly-CSharp-firstpass" or
        "System.Core" or "System.Xml" or "System.Xml.Linq" or "System.Drawing" or "System.Data" or "System.Windows.Forms" or "System.Configuration" or "System.Numerics" or "System.Net.Http" or "System.Runtime.Serialization" or "System.Security" or "System.Transactions" ||
        name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
        File.Exists(Path.Combine(game, "BepInEx", "core", name + ".dll")) || File.Exists(Path.Combine(game, "HoneySelect2_Data", "Managed", name + ".dll"));
    private static bool SystemNativeLibrary(string name) => Path.GetFileNameWithoutExtension(name).ToLowerInvariant() is
        "kernel32" or "user32" or "gdi32" or "shell32" or "shlwapi" or "advapi32" or "ole32" or "oleaut32" or "comdlg32" or "comctl32" or "winmm" or "ntdll" or "dwmapi" or "uxtheme" or "msvcrt" or "version" or "psapi" or "opengl32";
    private static bool ResourceFile(string path) => !Path.GetFileName(path).Contains("config", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(path).Contains("settings", StringComparison.OrdinalIgnoreCase) &&
        Path.GetExtension(path).ToLowerInvariant() is ".xml" or ".json" or ".png" or ".jpg" or ".jpeg" or ".dds" or ".tga" or ".unity3d" or ".assetbundle" or ".bytes" or ".dat" or ".csv" or ".shader" or ".bin";
    private HashSet<string> StockBundles()
    {
        if (stockBundles != null) return stockBundles;
        stockBundles = new(StringComparer.OrdinalIgnoreCase);
        string root = Path.Combine(game, "abdata"); if (!Directory.Exists(root)) return stockBundles;
        foreach (string path in Directory.EnumerateFiles(root))
        {
            cancellation.ThrowIfCancellationRequested(); string name = Path.GetFileName(path);
            if (name != "abdata" && !(name.StartsWith("add", StringComparison.Ordinal) && name.Length > 3 && name[3..].All(char.IsDigit))) continue;
            try
            {
                using var bundle = new NativeAssetBundle(path);
                foreach (string entry in bundle.ReadManifest().Keys) stockBundles.Add("abdata/" + entry.Replace('\\', '/'));
            }
            catch (Exception ex) when (ExportPaths.IsFileError(ex)) { stockWarnings.Add("原版资源清单无法读取，散装资源来源需确认：" + name + "：" + ex.Message); }
        }
        return stockBundles;
    }
    private HashSet<string> EmbeddedShaders()
    {
        if (embeddedShaders != null) return embeddedShaders;
        embeddedShaders = new(StringComparer.Ordinal);
        foreach (var file in index.PluginFiles.Where(f => f.Plugins.Any(p => p.Guid == "com.deathweasel.bepinex.materialeditor")))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(file.Path);
                var resource = assembly.MainModule.Resources.OfType<Mono.Cecil.EmbeddedResource>().FirstOrDefault(r => r.Name == "KK_Plugins.Resources.default.xml");
                if (resource == null) continue;
                using var input = resource.GetResourceStream();
                using var reader = System.Xml.XmlReader.Create(input, new() { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
                foreach (var shader in XDocument.Load(reader).Descendants("Shader")) if ((string?)shader.Attribute("Name") is { } name) embeddedShaders.Add(name);
            }
            catch (Exception ex) when (ExportPaths.IsFileError(ex) || ex is BadImageFormatException) { }
        }
        return embeddedShaders;
    }
}
