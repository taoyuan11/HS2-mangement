using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace HS2Manager.Core;

/// <summary>Resolves installed body selections from data. Does not load plugin assemblies.</summary>
internal static class UnityBodyCatalog
{
    internal sealed record Entry(string Kind, string Guid, string Name, int Sex, bool Random, string Archive, string Bundle, string Asset);
    internal sealed record Catalog(Entry[] Entries, Dictionary<string, string[]> Migrations);
    internal static Entry[] Resolve(string game, string cache, NativeModResolver mods, CardAppearance appearance, CancellationToken cancellation)
    {
        string plugins = Path.Combine(game, "BepInEx", "plugins");
        if (!Directory.Exists(plugins) || !Directory.EnumerateFiles(plugins, "HS2_UncensorSelector.dll", SearchOption.AllDirectories).Any()) return [];
        var installed = mods.InstalledMods().ToArray();
        string stamp = string.Join("\n", installed.Select(m => m.Path + "|" + m.Size + "|" + m.ModifiedUtcTicks));
        string file = Path.Combine(cache, "body-catalog-v2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp))) + ".json");
        Catalog? catalog = null;
        try { if (File.Exists(file)) catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(file)); }
        catch (Exception error) when (error is IOException or JsonException) { }
        if (catalog == null)
        {
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var migrations = new Dictionary<string, string[]>(StringComparer.Ordinal);
            Add(new("Body", "Default.Body.Female", "Default Body F", 1, false, "", "chara/oo_base.unity3d", "p_cf_body_00"));
            Add(new("Penis", "Default.Penis", "Mosaic Penis", -1, false, "", "chara/oo_base.unity3d", "p_cm_body_00"));
            Add(new("Balls", "Default.Balls", "Mosaic Balls", -1, false, "", "chara/oo_base.unity3d", "p_cm_body_00"));
            foreach (var mod in installed)
            {
                cancellation.ThrowIfCancellationRequested();
                using var zip = ZipFile.OpenRead(mod.Path);
                var manifest = zip.Entries.FirstOrDefault(e => e.FullName.Equals("manifest.xml", StringComparison.OrdinalIgnoreCase));
                if (manifest == null || manifest.Length > 2 * 1024 * 1024) continue;
                using var stream = manifest.Open();
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
                var root = XDocument.Load(reader).Root?.Element("KK_UncensorSelector");
                if (root == null) continue;
                foreach (string kind in new[] { "Body", "Penis", "Balls" }) foreach (var value in root.Elements(kind.ToLowerInvariant()))
                {
                    string guid = Value(value, "guid"); if (guid.Length == 0) continue;
                    bool body = kind == "Body"; int sex = body ? (Value(value, "sex").Equals("female", StringComparison.OrdinalIgnoreCase) ? 1 : 0) : -1;
                    var source = body ? value.Element("oo_base") : value;
                    string bundle = Value(source, "file", body ? "chara/oo_base.unity3d" : "");
                    string asset = Value(source, "asset", body ? (sex == 0 ? "p_cm_body_00" : "p_cf_body_00") : "");
                    if (bundle.Length == 0 || asset.Length == 0 || body && bundle == "chara/oo_base.unity3d" || !body && Value(value, "displayName").Length == 0) continue;
                    Add(new(kind, guid, Value(value, "displayName", guid), sex, Value(value, "allowRandom").ToLowerInvariant() is not ("false" or "0"), mod.Path, bundle, asset));
                }
                foreach (var migration in root.Elements("migration"))
                    if (Value(migration, "guidUncensor") is { Length: > 0 } guid)
                        migrations[guid] = new[] { "Body", "Penis", "Balls" }.Select(k => Value(migration, "guid" + k)).ToArray();
            }
            catalog = new(entries.Values.ToArray(), migrations);
            File.WriteAllText(file, JsonSerializer.Serialize(catalog));
            void Add(Entry entry) => entries[entry.Kind + ":" + entry.Guid] = entry;
        }
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        string configFile = Path.Combine(game, "BepInEx", "config", "com.deathweasel.bepinex.uncensorselector.cfg"), section = "";
        if (File.Exists(configFile)) foreach (string raw in File.ReadLines(configFile))
        {
            string line = raw.Trim();
            if (line.StartsWith('[')) { section = line.Trim('[', ']'); continue; }
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var pair = line.Split('=', 2); if (pair.Length == 2) config[section + "/" + pair[0].Trim()] = pair[1].Trim();
        }
        string legacy = CardAppearanceReader.Text(appearance.Uncensor, "UncensorGUID");
        catalog.Migrations.TryGetValue(legacy, out var migrated);
        float voiceRate = Convert.ToSingle(appearance.Parameter.GetValueOrDefault("voiceRate") ?? .5f);
        if(!Directory.EnumerateFiles(plugins,"HS2_SliderUnlocker.dll",SearchOption.AllDirectories).Any())voiceRate=Math.Clamp(voiceRate,0,1);
        float voicePitch = .94f + (1.06f - .94f) * voiceRate;
        int seed = checked((int)(CardAppearanceReader.Integer(appearance.Parameter, "birthDay") + CardAppearanceReader.Integer(appearance.Parameter, "personality"))) + (int)(voicePitch * 100f);
        var selected = new List<Entry>(); int part = 0;
        foreach (string kind in new[] { "Body", "Penis", "Balls" })
        {
            string guid = migrated?[part++] ?? CardAppearanceReader.Text(appearance.Uncensor, kind + "GUID");
            var values = catalog.Entries.Where(e => e.Kind == kind).ToArray();
            var entry = values.FirstOrDefault(e => e.Guid == guid && (kind != "Body" || e.Sex == appearance.Sex));
            string sex = appearance.Sex == 0 ? "Male" : "Female";
            if (entry == null)
            {
                string choice = config.GetValueOrDefault("Config/Default " + sex + " " + kind, "Random");
                entry = values.FirstOrDefault(e => (kind == "Body" ? "[" + (e.Sex == 0 ? "Male" : "Female") + "] " : "") + e.Name == choice);
            }
            if (entry == null)
            {
                var excluded = config.GetValueOrDefault("Random Excluded/" + sex + " " + kind, "").Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
                var candidates = values.Where(e => e.Random && (kind != "Body" || e.Sex == appearance.Sex) && !excluded.Contains(e.Guid)).ToArray();
                entry = candidates.Length > 0 ? candidates[new Random(seed).Next(candidates.Length)] : values.FirstOrDefault(e => e.Guid == "Default." + kind + (kind == "Body" ? "." + sex : ""));
            }
            if (entry != null) selected.Add(entry);
        }
        return selected.ToArray();
    }
    private static string Value(XElement? root, string name, string fallback = "") => root?.Element(name)?.Value is { } value && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}
