using System.IO.Compression;

namespace HS2Manager.Core;

internal sealed class NativeResourceDependencies(string game, NativeModResolver mods, CancellationToken cancellation)
{
    private readonly Dictionary<string, string[]> dependencies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> cabs = new(StringComparer.OrdinalIgnoreCase);
    private bool indexed;
    public string[] Paths(string relative, string archive)
    {
        if (archive.Length > 0)
        {
            return [archive, mods.Bundle(relative, archive)];
        }
        if (!indexed)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(game, "abdata")))
            {
                cancellation.ThrowIfCancellationRequested(); string name = Path.GetFileName(path);
                if (name != "abdata" && !(name.StartsWith("add", StringComparison.Ordinal) && name[3..].All(char.IsDigit))) continue;
                using var bundle = new NativeAssetBundle(path);
                foreach (var pair in bundle.ReadManifest()) dependencies[pair.Key] = pair.Value;
            }
            indexed = true;
        }
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase); Visit(relative, 0);
        return all.Select(p => mods.Bundle(p)).Where(File.Exists).ToArray();
        void Visit(string name, int depth)
        {
            if (depth > 32 || all.Count > 512) throw new InvalidDataException("资源依赖层数超限。");
            if (!all.Add(name)) return;
            foreach (string child in dependencies.GetValueOrDefault(name, [])) Visit(child, depth + 1);
        }
    }
    public NativeTexture Texture(string relative, string archive, string cab, long id)
    {
        if (!cabs.TryGetValue(cab, out var path))
        {
            foreach (var candidate in Candidates())
            {
                cancellation.ThrowIfCancellationRequested();
                string name = NativeAssetBundle.CabName(candidate); cabs[name] = candidate;
                if (name.Equals(cab, StringComparison.OrdinalIgnoreCase)) { path = candidate; break; }
            }
        }
        if (path == null) throw new FileNotFoundException("找不到引用的贴图资源包：" + cab);
        using var bundle = new NativeAssetBundle(path); return bundle.Texture(id);
        IEnumerable<string> Candidates()
        {
            if (archive.Length == 0) { foreach (string p in Paths(relative, archive)) yield return p; yield break; }
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("abdata/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)))
                yield return mods.Bundle(entry.FullName[7..], archive);
        }
    }
}
