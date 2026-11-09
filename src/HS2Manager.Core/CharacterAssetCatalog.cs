using System.Security.Cryptography;
using System.Text;

namespace HS2Manager.Core;

public sealed record CharacterAsset(int Category, int Id, string Name, string Guid, string Source, NativeAssetEntry Entry)
{
    public string Key => Category + ":" + Guid + ":" + Id;
    public bool IsMod => Guid.Length > 0;
    public string Label => Name.Length > 0 ? Name : "素材 " + Id;
}

/// <summary>Shared source lists and lazy thumbnails; no game or plugin code is executed.</summary>
public sealed class CharacterAssetCatalog
{
    private readonly string game, data, cache;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CharacterAsset[]? assets;
    private readonly List<string> warnings = [];
    public IReadOnlyList<string> Warnings => warnings;
    public CharacterAssetCatalog(string gamePath, string dataDirectory)
    { game = gamePath; data = dataDirectory; cache = Path.Combine(data, "UnityPreview", "definitions-v1"); }
    public async Task<IReadOnlyList<CharacterAsset>> LoadAsync(IProgress<string>? progress, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (assets != null) return assets;
            assets = await Task.Run(() =>
            {
                Directory.CreateDirectory(cache); warnings.Clear();
                var result = new List<CharacterAsset>();
                foreach (var entry in NativePreviewService.LoadIndex(Path.Combine(game, "abdata"), cache, progress, warnings, cancellation))
                    result.Add(new(entry.Category, entry.Id, entry.Get("Name"), "", "原版", entry));
                var mods = new NativeModResolver(game, data, cache, progress, cancellation);
                var installed = mods.InstalledMods().ToArray();
                for (int i = 0; i < installed.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested(); var mod = installed[i];
                    if (i % 40 == 0) progress?.Report($"读取可编辑素材 {i + 1}/{installed.Length}…");
                    try
                    {
                        foreach (var entry in mods.ReadLists(mod.Path))
                            result.Add(new(entry.Category, entry.Id, entry.Get("Name"), mod.Guid, string.IsNullOrEmpty(mod.Name) ? mod.Guid : mod.Name, entry));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.VisualBasic.FileIO.MalformedLineException)
                    { warnings.Add(Path.GetFileName(mod.Path) + "：" + ex.Message); }
                }
                return result.GroupBy(a => a.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
                    .OrderBy(a => a.IsMod).ThenBy(a => a.Category).ThenBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }, cancellation).ConfigureAwait(false);
            return assets;
        }
        finally { gate.Release(); }
    }
    public IReadOnlyList<CharacterAsset> Query(int category, string text = "", int source = 0) => (assets ?? [])
        .Where(a => a.Category == category && (source == 0 || source == 1 && !a.IsMod || source == 2 && a.IsMod) &&
            (text.Length == 0 || a.Label.Contains(text, StringComparison.CurrentCultureIgnoreCase) || a.Source.Contains(text, StringComparison.CurrentCultureIgnoreCase) || a.Id.ToString().Contains(text)))
        .ToArray();
    public CharacterAsset? Selected(CharacterDocument document, string path)
    {
        if(IsPreset(path))return null;
        var binding = CharacterAssetReferences.Describe(document, path); if (binding == null) return null;
        var reference = CharacterAssetReferences.Find(document, path);
        int id = reference?.Slot ?? Convert.ToInt32(document.GetValue(path)); string guid = reference?.Guid ?? "";
        return assets?.FirstOrDefault(a => a.Category == binding.Category && a.Id == id && a.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsPreset(string path)=>path=="face.moleLayout"||path.StartsWith("face.makeup.paintInfo[",StringComparison.Ordinal)&&path.EndsWith(".layout",StringComparison.Ordinal);
    public static int Category(CharacterDocument document, string path) => IsPreset(path)?path=="face.moleLayout"?6:7:CharacterAssetReferences.Describe(document, path)?.Category ?? -1;
    public string SelectionLabel(CharacterDocument document, string path)
    {
        if(IsPreset(path))return "选择位置预设";
        var selected=Selected(document,path);if(selected!=null)return selected.Label+" · "+selected.Source;
        var reference=CharacterAssetReferences.Find(document,path);
        return "未找到素材 · 编号 "+document.GetValue(path)+(reference==null?"（原版列表无此项）":" · MOD "+reference.Guid)+"；保留原引用";
    }
    public static CharacterDocument Select(CharacterDocument document, string path, CharacterAsset asset) => CharacterAssetReferences.Select(document, path, asset);
    public async Task<CharacterDocument> SelectAsync(CharacterDocument document, string path, CharacterAsset asset, CancellationToken cancellation)
    {
        if(IsPreset(path))
        {
            if(asset.Category!=Category(document,path))throw new ArgumentException("预设类别不匹配。");
            var values=new[]{"Scale","Scale","PosX","PosY"};var selected=document;
            for(int i=0;i<4;i++)selected=selected.WithValue(path+$"[{i}]",float.Parse(asset.Entry.Get(values[i]),System.Globalization.CultureInfo.InvariantCulture));
            return selected;
        }
        await ValidateAsync(asset, cancellation).ConfigureAwait(false);
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var next = CharacterEditRules.Select(document, path, asset);
                if (!path.EndsWith(".id", StringComparison.Ordinal) || !path.StartsWith("hair.", StringComparison.Ordinal) && !path.StartsWith("accessory.", StringComparison.Ordinal)) return next;
                var entry = asset.Entry; string name = document.Sex == 0 && entry.MaleAsset is not ("" or "0") ? entry.MaleAsset : entry.Asset;
                if (entry.Bundle is "" or "0" || name is "" or "0" or "p_dummy") return next;
                var mods = new NativeModResolver(game, data, cache, null, cancellation);
                using var resources = new UnityPreparationResources(new PreviewMetadataCache(game, data), cancellation);
                var prefab = resources.Prefab(mods.AssetBundle(entry.Bundle, name, entry.Archive).Path, name);
                return CharacterEditRules.InitializeAsset(next, path, prefab.Components);
            }, cancellation).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async Task ValidateAsync(CharacterAsset asset, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var mods = new NativeModResolver(game, data, cache, null, cancellation);
                string bundle = asset.Entry.Bundle;
                if (bundle is not ("" or "0")) _ = mods.Bundle(bundle, asset.Entry.Archive);
                foreach (string key in new[] { "MainTexAB", "TexAB" })
                    if (asset.Entry.Get(key) is { } textureBundle && textureBundle is not ("" or "0")) _ = mods.Bundle(textureBundle, asset.Entry.Archive);
            }, cancellation).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async Task<string?> ThumbnailAsync(CharacterAsset asset, CancellationToken cancellation)
    {
        string bundle = asset.Entry.Get("ThumbAB"), name = asset.Entry.Get("ThumbTex");
        if (bundle is "" or "0" || name is "" or "0") return null;
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var mods = new NativeModResolver(game, data, cache, null, cancellation);
            string stamp = asset.Entry.Archive.Length == 0 ? bundle : PreviewMetadataCache.Stamp(asset.Entry.Archive);
            string folder = Path.Combine(data, "Editor", "Thumbnails"); Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(asset.Key + "|" + stamp + "|" + bundle + "|" + name))) + ".png");
            if (File.Exists(target)) return target;
            var texture = await Task.Run(async () =>
            {
                var loose = mods.LooseTexture(bundle, name, asset.Entry.Archive);
                if (loose != null) return await ImageCodec.DecodeTextureAsync(await File.ReadAllBytesAsync(loose, cancellation), cancellation);
                using var resource = new NativeAssetBundle(mods.AssetBundle(bundle, name, asset.Entry.Archive).Path); return resource.ReadTexture(name);
            }, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            string temporary = target + ".tmp";
            try { await ImageCodec.SavePngAsync(temporary, texture.Width, texture.Height, texture.Pixels).ConfigureAwait(false); File.Move(temporary, target, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
        finally { gate.Release(); }
    }
}
