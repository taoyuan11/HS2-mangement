using System.IO.Compression;
using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace HS2Manager.Core;

internal sealed class NativeResourceDependencies(string game, NativeModResolver mods, CancellationToken cancellation, PreviewMetadataCache? metadata = null)
{
    private readonly Dictionary<string, string[]> dependencies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> cabs = new(StringComparer.OrdinalIgnoreCase);
    private bool indexed;
    private AssetTypeTemplateField? meshTemplate;
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
                Dictionary<string, string[]> Read() { using var bundle = new NativeAssetBundle(path); return bundle.ReadManifest(); }
                var manifest = metadata == null ? Read() : metadata.Get("bundle-manifest", PreviewMetadataCache.Stamp(path), Read, cancellation);
                foreach (var pair in manifest) dependencies[pair.Key] = pair.Value;
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
        string path = ResolveCab(relative, archive, cab);
        using var bundle = new NativeAssetBundle(path); return bundle.Texture(id);
    }
    public NativeMesh Mesh(string relative, string archive, string cab, long id, Matrix4x4 world)
    {
        cancellation.ThrowIfCancellationRequested();
        if(cab is "unity default resources" or "unity_builtin_extra")
        {
            if(meshTemplate==null)
            {
                using var source=new NativeAssetBundle(mods.Bundle("chara/oo_base.unity3d"));
                meshTemplate=source.MeshTemplate();
            }
            var manager=new AssetsManager();
            try
            {
                var file=manager.LoadAssetsFile(Path.Combine(game,"HoneySelect2_Data","Resources",cab),false);
                if(!file.file.Metadata.UnityVersion.StartsWith("2018.",StringComparison.Ordinal)) throw new NotSupportedException("内置模型版本不匹配。");
                var info=file.file.GetAssetInfo(id);
                if(info?.TypeId!=(int)AssetClassID.Mesh) throw new InvalidDataException("内置模型引用不存在。");
                var field=meshTemplate.MakeValue(file.file.Reader,info.GetAbsoluteByteOffset(file.file));
                return UnityMeshDecoder.Read(field,world,cancellation);
            }
            finally { manager.UnloadAll(); }
        }
        using var bundle=new NativeAssetBundle(ResolveCab(relative,archive,cab));
        return bundle.Mesh(id,world,cancellation);
    }
    private string ResolveCab(string relative, string archive, string cab)
    {
        if (!cabs.TryGetValue(cab, out var path))
        {
            foreach (var candidate in Candidates())
            {
                cancellation.ThrowIfCancellationRequested();
                string name;
                try { name = NativeAssetBundle.CabName(candidate); }
                catch(Exception ex) when(ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or NullReferenceException)
                { continue; } // An unrelated legacy/corrupt bundle cannot abort dependency discovery.
                cabs[name] = candidate;
                if (name.Equals(cab, StringComparison.OrdinalIgnoreCase)) { path = candidate; break; }
            }
        }
        if (path == null) throw new FileNotFoundException("找不到引用的资源包：" + cab);
        return path;
        IEnumerable<string> Candidates()
        {
            if (archive.Length == 0) { foreach (string p in Paths(relative, archive)) yield return p; yield break; }
            using (var zip = ZipFile.OpenRead(archive))
                foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("abdata/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)))
                    yield return mods.Bundle(entry.FullName[7..], archive);
            // A MOD can retain a serialized CAB reference to stock hair/material data.
            foreach (var path in Directory.EnumerateFiles(Path.Combine(game,"abdata","chara"),"*.unity3d",SearchOption.AllDirectories))
                yield return path;
        }
    }
}
