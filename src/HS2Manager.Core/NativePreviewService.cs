using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HS2Manager.Core;

public sealed record NativePreviewScene(IReadOnlyList<NativeMesh> Meshes, IReadOnlyList<string> Warnings, double LoadSeconds, int CacheHits);
public sealed record NativeAssetEntry(int Category, int Id, string Bundle, string Asset, string MaleAsset);

/// <summary>Standalone, on-demand data decoding with a local cache; never starts a game process.</summary>
public sealed class NativePreviewService
{
    public const string Limitations = "第一阶段：基础网格与纯色材质。未应用捏脸、体型、服装、配饰、皮肤贴图及 MOD；不代表人物最终外观。";
    private const string CacheVersion = "native-v1";
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<NativePreviewScene> LoadAsync(string gamePath, string dataPath, CardRecord card, IProgress<string>? progress, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { return await Task.Run(() => Load(gamePath, dataPath, card, progress, cancellation), cancellation).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private NativePreviewScene Load(string gamePath, string dataPath, CardRecord card, IProgress<string>? progress, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew(); int cacheHits = 0;
        string assetsRoot = Path.GetFullPath(Path.Combine(gamePath, "abdata"));
        string cache = Path.Combine(dataPath, "NativePreview", CacheVersion); Directory.CreateDirectory(cache);
        progress?.Report("正在读取人物外观参数…");
        var appearance = CardAppearanceReader.Read(card, cancellation);
        var warnings = new List<string>(); var meshes = new List<NativeMesh>();
        var index = LoadIndex(assetsRoot, cache, progress, warnings, cancellation);
        var body = Prefab("chara/oo_base.unity3d", appearance.Sex == 0 ? "p_cm_body_00" : "p_cf_body_00");
        Add(body, appearance.Skin, "身体", Matrix4x4.Identity);
        var headTransform = body.Anchors.TryGetValue("cf_J_Head", out var anchor) ? Matrix(anchor) :
            throw new InvalidDataException("基础骨架缺少头部连接点，无法正确组合模型。");
        bool headLoaded = false;
        foreach (var part in appearance.Parts)
        {
            cancellation.ThrowIfCancellationRequested();
            if (part.Category >= 300 && !headLoaded) { warnings.Add(part.Label + "未显示：头部资源未能加载。"); continue; }
            if (part.IsMod) { warnings.Add($"{part.Label} #{part.Id} 引用 MOD，本阶段未加载。"); continue; }
            var entry = index.LastOrDefault(e => e.Category == part.Category && e.Id == part.Id);
            if (entry == null) { warnings.Add($"未找到{part.Label} #{part.Id} 的基础资源。"); continue; }
            string asset = appearance.Sex == 0 && entry.MaleAsset is not ("" or "0") ? entry.MaleAsset : entry.Asset;
            if (asset is "" or "0" or "p_dummy") continue;
            progress?.Report($"正在读取{part.Label}…");
            try
            {
                var prefab = Prefab(entry.Bundle, asset);
                Add(prefab, part.Color, part.Label, headTransform);
                if (part.Category < 300) headLoaded = prefab.Meshes.Any(m => m.Name.StartsWith("o_head/", StringComparison.Ordinal));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            { warnings.Add(part.Label + "：" + ex.Message); }
        }
        if (meshes.Count == 0) throw new InvalidDataException("没有可独立预览的网格。" + string.Join("；", warnings));
        if (card.PluginDataIds.Count > 0) warnings.Add("人物卡扩展插件效果未执行。");
        return new(meshes, warnings.Distinct().ToArray(), clock.Elapsed.TotalSeconds, cacheHits);

        NativePrefab Prefab(string relative, string asset)
        {
            cancellation.ThrowIfCancellationRequested();
            string source = ResolveBundlePath(assetsRoot, relative);
            var stamp = Fingerprint(source);
            string target = Path.Combine(cache, Hash(stamp + "|" + asset) + ".mesh.br");
            if (TryRead<NativePrefab>(target, out var saved) && ValidPrefab(saved!)) { cacheHits++; return saved!; }
            using var bundle = new NativeAssetBundle(source);
            var result = bundle.ReadPrefab(asset, cancellation);
            if (!ValidPrefab(result)) throw new InvalidDataException("解析得到的模型包含无效数据。");
            cancellation.ThrowIfCancellationRequested(); Write(target, result); return result;
        }
        void Add(NativePrefab prefab, PreviewColor color, string label, Matrix4x4 placement)
        {
            warnings.AddRange(prefab.Warnings.Select(w => label + "：" + w));
            foreach (var mesh in prefab.Meshes)
            {
                if (label == "身体" && !mesh.Name.StartsWith("o_body_", StringComparison.Ordinal)) continue;
                // These overlays need game shaders / alpha masks, so an opaque substitute would hide the face.
                if (mesh.Name.Contains("namida", StringComparison.OrdinalIgnoreCase) || mesh.Name.Contains("eyeshadow", StringComparison.OrdinalIgnoreCase) || mesh.Name.Contains("eyelashes", StringComparison.OrdinalIgnoreCase)) continue;
                mesh.Color = color;
                if (mesh.Name.Contains("eyebase", StringComparison.OrdinalIgnoreCase) || mesh.Name.Contains("tooth", StringComparison.OrdinalIgnoreCase)) mesh.Color = new(.85f, .85f, .85f);
                if (mesh.Name.Contains("hitomi", StringComparison.OrdinalIgnoreCase)) mesh.Color = new(.16f, .18f, .2f);
                for (int i = 0; i < mesh.Positions.Length; i += 3)
                {
                    var p = Vector3.Transform(new(mesh.Positions[i], mesh.Positions[i + 1], -mesh.Positions[i + 2]), placement);
                    mesh.Positions[i] = p.X; mesh.Positions[i + 1] = p.Y; mesh.Positions[i + 2] = -p.Z;
                    var n = Vector3.TransformNormal(new(mesh.Normals[i], mesh.Normals[i + 1], -mesh.Normals[i + 2]), placement);
                    if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n);
                    mesh.Normals[i] = n.X; mesh.Normals[i + 1] = n.Y; mesh.Normals[i + 2] = -n.Z;
                }
                meshes.Add(mesh);
            }
        }
    }
    private static Matrix4x4 Matrix(float[] m) => m.Length == 16 ? new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]) : throw new InvalidDataException("模型缓存的连接点损坏。");
    private static bool ValidPrefab(NativePrefab prefab) => prefab.Meshes != null && prefab.Anchors != null && prefab.Warnings != null
        && prefab.Anchors.Values.All(m => m != null && m.Length == 16 && m.All(float.IsFinite))
        && prefab.Meshes.All(m => m != null && m.Name != null && m.Positions != null && m.Normals != null && m.UV != null && m.Indices != null
            && m.Positions.Length > 0 && m.Positions.Length % 3 == 0 && m.Positions.Length <= 1500000
            && m.Normals.Length == m.Positions.Length && m.UV.Length == m.Positions.Length / 3 * 2
            && m.Indices.Length % 3 == 0 && m.Positions.All(float.IsFinite) && m.Normals.All(float.IsFinite)
            && m.Indices.All(i => i >= 0 && i < m.Positions.Length / 3));
    private static List<NativeAssetEntry> LoadIndex(string root, string cache, IProgress<string>? progress, List<string> warnings, CancellationToken cancellation)
    {
        string directory = Path.Combine(root, "list", "characustom");
        var files = Directory.GetFiles(directory, "*.unity3d", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string fingerprint = Hash(string.Join("\n", files.Select(Fingerprint)));
        string target = Path.Combine(cache, "index-" + fingerprint + ".br");
        if (TryRead<List<NativeAssetEntry>>(target, out var cached) && cached!.All(e => e != null && e.Bundle != null && e.Asset != null && e.MaleAsset != null)) return cached!;
        var entries = new List<NativeAssetEntry>(); bool complete = true;
        for (int i = 0; i < files.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested(); progress?.Report($"首次建立模型资源索引 {i + 1}/{files.Length}…");
            try
            {
                using var bundle = new NativeAssetBundle(files[i]);
                foreach (var list in bundle.ReadLists(cancellation))
                {
                    int category = checked((int)CardAppearanceReader.Integer(list, "categoryNo"));
                    if (category is not (110 or 210 or 300 or 301 or 302 or 303)) continue;
                    var keys = CardAppearanceReader.List(list, "lstKey").Select(x => x?.ToString() ?? "").ToList();
                    foreach (var row in CardAppearanceReader.Map(list.GetValueOrDefault("dictList")))
                    {
                        if (!int.TryParse(row.Key, out int id) || row.Value is not List<object?> values) continue;
                        string Value(string key) { int index = keys.IndexOf(key); return index >= 0 && index < values.Count ? values[index]?.ToString() ?? "" : ""; }
                        string path = Value("MainAB"); if (path.Length > 0) entries.Add(new(category, id, path, Value("MainData"), Value("MainData02")));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            { complete = false; warnings.Add(Path.GetFileName(files[i]) + " 资源清单未读取：" + ex.Message); }
        }
        if (complete) { cancellation.ThrowIfCancellationRequested(); Write(target, entries); }
        return entries;
    }
    internal static string ResolveBundlePath(string root, string relative)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("资源引用超出游戏 abdata 目录。");
        return path;
    }
    private static string Fingerprint(string path)
    { var file = new FileInfo(path); return Path.GetFullPath(path) + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks; }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool TryRead<T>(string path, out T? value)
    {
        value = default;
        if (!File.Exists(path)) return false;
        try
        {
            using var file = File.OpenRead(path); using var compressed = new BrotliStream(file, CompressionMode.Decompress);
            value = JsonSerializer.Deserialize<T>(compressed); return value != null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { return false; }
    }
    private static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temporary))
            using (var compressed = new BrotliStream(output, CompressionLevel.Fastest)) JsonSerializer.Serialize(compressed, value);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
