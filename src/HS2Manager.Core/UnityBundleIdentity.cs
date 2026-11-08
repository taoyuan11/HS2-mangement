using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

// Some independently authored MODs reuse a CAB name. Unity cannot load both at once.
// Only self-contained, unreferenced collisions are rewritten, in our cache only.
internal static class UnityBundleIdentity
{
    private sealed record Identity(string Name, Lazy<string[]> Externals);
    private static readonly ConcurrentDictionary<string, Identity> identities = new();

    internal static bool Normalize(CharacterDefinition definition, string cache, CancellationToken cancellation)
    {
        var paths = definition.BundlePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var names = paths.ToDictionary(p => p, p => { cancellation.ThrowIfCancellationRequested(); return ReadIdentity(p); }, StringComparer.OrdinalIgnoreCase);
        var collisions = paths.GroupBy(p => names[p].Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToArray();
        if (collisions.Length == 0) return false;
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in collisions)
        {
            cancellation.ThrowIfCancellationRequested();
            string original = group.First();
            string firstHash = Hash(original);
            foreach (var path in group.Skip(1))
            {
                cancellation.ThrowIfCancellationRequested();
                string hash = Hash(path);
                if (hash == firstHash) { replacements[path] = original; continue; }
                if (!group.Key.StartsWith("CAB-", StringComparison.Ordinal) || group.Key.Length is < 12 or > 68
                    || names[path].Externals.Value.Any(e => !Builtin(e))
                    || names.Any(p => p.Key != path && p.Value.Externals.Value.Any(e => Path.GetFileName(e).Equals(group.Key, StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException("MOD 内部名称冲突且依赖关系无法唯一确定：" + group.Key + " @ " + path);
                string target = Path.Combine(cache, "unique-cab-v1", hash + ".unity3d");
                string unique = "CAB-" + hash[..(group.Key.Length - 4)].ToLowerInvariant();
                if (!ValidCache(target, unique))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        var manager = new AssetsManager();
                        byte[] bytes;
                        try
                        {
                            var bundle = manager.LoadBundleFile(path, false);
                            using var unpacked = new MemoryStream();
                            using (var writer = new AssetsFileWriter(unpacked))
                            {
                                bundle.file.Unpack(writer);
                                bytes = unpacked.ToArray();
                            }
                        }
                        finally { manager.UnloadAll(); }
                        // Equal-length replacement preserves offsets and serialized string lengths.
                        // It includes archive:/CAB/CAB.resS streaming references, not just the directory.
                        byte[] before = Encoding.ASCII.GetBytes(group.Key), after = Encoding.ASCII.GetBytes(unique);
                        int offset = 0, count = 0;
                        while (offset <= bytes.Length - before.Length)
                        {
                            int found = bytes.AsSpan(offset).IndexOf(before);
                            if (found < 0) break;
                            offset += found; after.CopyTo(bytes, offset); offset += before.Length; count++;
                        }
                        if (count == 0) throw new InvalidDataException("CAB 名称未出现在解压资源包中。");
                        cancellation.ThrowIfCancellationRequested();
                        File.WriteAllBytes(temporary, bytes);
                        if (ReadIdentity(temporary).Name != unique) throw new InvalidDataException("CAB 缓存重命名验证失败。");
                        File.Move(temporary, target, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                replacements[path] = target;
            }
        }
        foreach (var reference in definition.Parts.Select(p => p.Prefab).Concat(definition.Selections.Select(s => s.Reference))
            .Concat(definition.Shaders.Select(s => s.Source)).Concat(definition.Shaders.SelectMany(s => s.Defaults.Select(d => d.Texture))))
            if (reference != null && replacements.TryGetValue(reference.Bundle, out string? replacement)) reference.Bundle = replacement;
        definition.BundlePaths = paths.Select(p => replacements.GetValueOrDefault(p, p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        definition.Diagnostics = definition.Diagnostics.Append("已在独立缓存中消除 MOD 资源包内部名称冲突，原始资源未修改。").Distinct().ToArray();
        return true;
    }

    private static Identity ReadIdentity(string path)
    {
        var info = new FileInfo(path);
        string key = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        return identities.GetOrAdd(key, _ =>
        {
            string name = NativeAssetBundle.CabName(path);
            return new(name, new Lazy<string[]>(() => ReadExternals(path)));
        });
    }
    private static string[] ReadExternals(string path)
    {
            var manager = new AssetsManager();
            try
            {
                var bundle = manager.LoadBundleFile(path, true);
                var file = manager.LoadAssetsFileFromBundle(bundle, 0, false);
                return file.file.Metadata.Externals.Select(e => e.PathName).ToArray();
            }
            finally { manager.UnloadAll(); }
    }
    private static bool ValidCache(string path, string name)
    {
        try { return File.Exists(path) && ReadIdentity(path).Name == name; }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or NullReferenceException) { return false; }
    }
    private static bool Builtin(string path) => path.Contains("unity_builtin_extra", StringComparison.OrdinalIgnoreCase) || path.Contains("unity default resources", StringComparison.OrdinalIgnoreCase);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
