using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

/// <summary>Request-scoped bundle readers, backed by immutable serialized metadata snapshots.</summary>
internal sealed class UnityPreparationResources(PreviewMetadataCache cache, CancellationToken cancellation) : IDisposable
{
    internal sealed record PrefabData(NativePrefab Rig, ComponentDefinition[] Components, string[] Diagnostics);
    private readonly Dictionary<string, NativeAssetBundle> readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> recent = new();
    internal NativeAssetBundle Reader(string path)
    {
        cancellation.ThrowIfCancellationRequested();
        if (readers.TryGetValue(path, out var reader)) { recent.Remove(path); recent.AddFirst(path); return reader; }
        // Limit the number of decompressed bundles kept during one preparation.
        while (readers.Count >= 3)
        { string last = recent.Last!.Value; readers[last].Dispose(); readers.Remove(last); recent.RemoveLast(); }
        reader = new NativeAssetBundle(path); readers.Add(path, reader); recent.AddFirst(path); return reader;
    }
    internal byte[] Text(string path, string name) => cache.Get("text", PreviewMetadataCache.Stamp(path) + "|" + name,
        () => Reader(path).ReadText(name), cancellation);
    internal PrefabData Prefab(string path, string name)
    {
        using var timing = PreviewPerformance.Measure("structure");
        return cache.Get("prefab-structure", PreviewMetadataCache.Stamp(path) + "|" + name, () =>
        {
            var reader = Reader(path);
            var warnings = new List<string>();
            var rig = reader.ReadPrefab(name, cancellation, includeGeometry: false);
            var components = reader.ReadComponents(name, warnings, cancellation);
            if (reader.HasExport("additional_bones"))
            {
                var line = System.Text.Encoding.UTF8.GetString(reader.ReadText("additional_bones")).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Split('\t')).FirstOrDefault(columns => columns[0] == name);
                if (line != null) components = [..components, new ComponentDefinition { Node = name, Type = "BonesFramework", Data = UnityCharacterService.ParameterOf(line.Skip(1).Where(n => n.Length > 0).Cast<object?>().ToList()) }];
            }
            return new PrefabData(rig, components, warnings.ToArray());
        }, cancellation);
    }
    public void Dispose() { foreach (var reader in readers.Values) reader.Dispose(); readers.Clear(); recent.Clear(); }
}
