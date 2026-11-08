using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

internal static class UnityResourceProbeChecks
{
    // Produces a reproducible, data-only feasibility manifest. It is not a visual-parity test.
    public static void Prepare(string game, string output, string? modBundle)
    {
        game = Path.GetFullPath(game);
        string cache = Path.Combine(output, "unity-resource-probe-cache-v1");
        Directory.CreateDirectory(cache);
        var mods = new NativeModResolver(game, output, cache, null, default);
        var resources = new NativeResourceDependencies(game, mods, default);
        var bundlePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cases = new List<ResourceProbeCase>();
        Add("skin", "chara/mm_base.unity3d", false);
        Add("hair", "chara/hair_shader_mat.unity3d", false);
        Add("head-and-eyes", "chara/00/fo_head_00.unity3d", true);
        var mod = Path.GetFullPath(modBundle ?? Path.Combine(game, "abdata", "wen", "wenwujianxian.unity3d"));
        var relative = Path.GetRelativePath(Path.Combine(game, "abdata"), mod).Replace('\\', '/');
        // Reuse the resolver's path confinement before inspecting a caller-selected sample.
        _ = NativePreviewService.ResolveBundlePath(Path.Combine(game, "abdata"), relative);
        Add("mod", relative, true);
        string dhh = Path.Combine(game, "DHH_Data", "ModData", "dhpost");
        RequireFile(dhh);
        bundlePaths.Add(dhh);
        AddCases(dhh, "dhh-resources", false);
        if (cases.Select(c => c.Category).Distinct().Count() != 5)
            throw new InvalidDataException("All five resource categories must be represented; missing cases cannot pass.");
        var manifest = new ResourceProbeManifest { BundlePaths = bundlePaths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), Cases = cases.ToArray() };
        string target = Path.Combine(output, "unity-resource-probe.json");
        var options = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        File.WriteAllText(target, JsonSerializer.Serialize(manifest, options));
        var roundtrip = JsonSerializer.Deserialize<ResourceProbeManifest>(File.ReadAllText(target), options)!;
        if (roundtrip.SchemaVersion != 1 || roundtrip.Cases.Length != cases.Count || roundtrip.BundlePaths.Length != bundlePaths.Count)
            throw new InvalidDataException("Probe manifest round trip failed.");
        Console.WriteLine($"Prepared {cases.Count} cases, {bundlePaths.Count} original bundles: {target}");
        Console.WriteLine("NOT VERIFIED: Unity shader execution, character rendering, DHH/BetterAA post effects, motion, and visual parity require the activated editor and runtime tests.");

        void Add(string category, string bundle, bool prefab)
        {
            string path = NativePreviewService.ResolveBundlePath(Path.Combine(game, "abdata"), bundle);
            RequireFile(path);
            foreach (var dependency in resources.Paths(bundle, "")) { RequireFile(dependency); bundlePaths.Add(Path.GetFullPath(dependency)); }
            bundlePaths.Add(path);
            AddCases(path, category, prefab);
        }
        void AddCases(string path, string category, bool prefab)
        {
            var manager = new AssetsManager();
            try
            {
                var bundle = manager.LoadBundleFile(path, true);
                var file = manager.LoadAssetsFileFromBundle(bundle, 0, false);
                if (!file.file.Metadata.UnityVersion.StartsWith("2018.", StringComparison.Ordinal))
                    throw new InvalidDataException("Probe source uses a different Unity resource format: " + path);
                var containerInfo = file.file.GetAssetsOfType(AssetClassID.AssetBundle).Single();
                var container = manager.GetBaseField(file, containerInfo)["m_Container"]["Array"].Children;
                int count = 0;
                // The selected clothing prefab has serialized materials. Hair accessories can
                // intentionally leave these empty until the game assigns its hair shader/material.
                foreach (var item in container.OrderBy(c => c["first"].AsString.Equals("shoes", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(c => c["first"].AsString, StringComparer.Ordinal))
                {
                    var pointer = item["second"]["asset"];
                    if (pointer["m_FileID"].AsInt != 0) continue;
                    var info = file.file.GetAssetInfo(pointer["m_PathID"].AsLong);
                    if (info == null || info.TypeId != (int)(prefab ? AssetClassID.GameObject : AssetClassID.Material)) continue;
                    string[] textures = [];
                    string shaderName = "";
                    if (!prefab)
                    {
                        var field = manager.GetBaseField(file, info);
                        var shaderPointer = field["m_Shader"];
                        var shaderFile = file;
                        if (shaderPointer["m_FileID"].AsInt != 0)
                        {
                            int externalIndex = shaderPointer["m_FileID"].AsInt - 1;
                            if (externalIndex < 0 || externalIndex >= file.file.Metadata.Externals.Count)
                                throw new InvalidDataException("Shader file reference is out of range: " + path);
                            string cab = Path.GetFileName(file.file.Metadata.Externals[externalIndex].PathName);
                            string dependency = bundlePaths.FirstOrDefault(p => NativeAssetBundle.CabName(p).Equals(cab, StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidDataException("Original shader dependency is unresolved: " + cab);
                            shaderFile = manager.LoadAssetsFileFromBundle(manager.LoadBundleFile(dependency, true), 0, false);
                        }
                        var shaderInfo = shaderFile.file.GetAssetInfo(shaderPointer["m_PathID"].AsLong);
                        if (shaderInfo == null || shaderInfo.TypeId != (int)AssetClassID.Shader)
                            throw new InvalidDataException("Original shader metadata is missing: " + path);
                        var shader = manager.GetBaseField(shaderFile, shaderInfo);
                        shaderName = shader["m_ParsedForm"]["m_Name"].AsString;
                        if (string.IsNullOrEmpty(shaderName)) throw new InvalidDataException("Original shader name is empty: " + path);
                        textures = field["m_SavedProperties"]["m_TexEnvs"]["Array"].Children
                            .Where(t => t["second"]["m_Texture"]["m_PathID"].AsLong != 0)
                            .Select(t => t["first"].AsString).ToArray();
                    }
                    cases.Add(new ResourceProbeCase
                    {
                        Category = category, BundlePath = path, AssetName = item["first"].AsString,
                        AssetKind = prefab ? "GameObject" : "Material", ExpectedShaderName = shaderName, RequiredTextureProperties = textures
                    });
                    // Two original materials per family, one complete prefab (all its materials) per model case.
                    if (++count == (prefab ? 1 : 2)) break;
                }
                if (count == 0) throw new InvalidDataException("No exported probe assets in " + category + ": " + path);
            }
            finally { manager.UnloadAll(); }
        }
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Required original probe resource is missing; specify --mod-bundle for a different installed MOD sample.", path);
    }
}
