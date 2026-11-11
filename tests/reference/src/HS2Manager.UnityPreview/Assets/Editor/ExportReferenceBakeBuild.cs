using System;
using System.IO;
using UnityEditor;

namespace HS2Manager.UnityPreview.Editor
{
    public static class ExportReferenceBakeBuild
    {
        public static void Build()
        {
            var output=Environment.GetEnvironmentVariable("HS2_REFERENCE_BAKE_OUTPUT");
            if(string.IsNullOrEmpty(output))throw new InvalidOperationException("Missing offline bake output directory.");
            Directory.CreateDirectory(output);
            var build=new AssetBundleBuild{assetBundleName="export-reference-bakes",assetNames=new[]{"Assets/Resources/ModelExportBake.shader","Assets/Resources/ModelSceneBake.shader"}};
            if(BuildPipeline.BuildAssetBundles(output,new[]{build},BuildAssetBundleOptions.ForceRebuildAssetBundle|BuildAssetBundleOptions.ChunkBasedCompression,BuildTarget.StandaloneWindows64)==null)
                throw new IOException("Offline bake shader compilation failed.");
        }
    }
}
