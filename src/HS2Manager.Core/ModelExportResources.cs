using System.Security.Cryptography;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

// Export has its own bundle copies: enabling CPU reads must never affect preview or source assets.
internal static class ModelExportResources
{
    internal static void Prepare(CharacterDefinition definition, string directory, CancellationToken cancellation)
    {
        Directory.CreateDirectory(directory);
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in definition.BundlePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            string target = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source))) + ".unity3d");
            var manager = new AssetsManager();
            try
            {
                var bundle = manager.LoadBundleFile(source, true);
                bool changed = false;
                for (int i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                {
                    var entry = bundle.file.BlockAndDirInfo.DirectoryInfos[i];
                    if (!entry.Name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) && !entry.Name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                    {
                        var file = manager.LoadAssetsFileFromBundle(bundle, i, false);
                        if (file == null) continue;
                        if (!file.file.Metadata.TypeTreeEnabled)
                        {
                            using var schema = typeof(ModelExportResources).Assembly.GetManifestResourceStream("HS2Manager.Core.Resources.classdata.tpk")!;
                            manager.LoadClassPackage(schema);
                            manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
                        }
                        bool fileChanged = false;
                        foreach (var info in file.file.GetAssetsOfType(AssetClassID.Mesh))
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var field = manager.GetBaseField(file, info);
                            if (!field["m_IsReadable"].IsDummy && field["m_IsReadable"].AsBool) continue;
                            if (!field["m_IsReadable"].IsDummy) field["m_IsReadable"].AsBool = true;
                            if (!field["m_KeepVertices"].IsDummy) field["m_KeepVertices"].AsBool = true;
                            if (!field["m_KeepIndices"].IsDummy) field["m_KeepIndices"].AsBool = true;
                            info.SetNewData(field);
                            changed = fileChanged = true;
                        }
                        if (fileChanged) entry.SetNewData(file.file);
                    }
                }
                if (changed)
                {
                    using var writer = new AssetsFileWriter(target);
                    bundle.file.Write(writer);
                }
                replacements.Add(source, changed ? target : source);
            }
            finally { manager.UnloadAll(); }
        }
        foreach (var reference in definition.Parts.Select(p => p.Prefab).Concat(definition.Selections.Select(s => s.Reference))
            .Concat(definition.Shaders.Select(s => s.Source)).Concat(definition.Shaders.SelectMany(s => s.Defaults.Select(d => d.Texture))))
            if (reference != null && replacements.TryGetValue(reference.Bundle, out string? target)) reference.Bundle = target;
        definition.BundlePaths = definition.BundlePaths.Select(p => replacements[p]).ToArray();
    }
}
