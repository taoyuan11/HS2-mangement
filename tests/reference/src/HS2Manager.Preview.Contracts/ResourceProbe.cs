using System;

namespace HS2Manager.Preview.Contracts
{
    // Plain fields deliberately support both Unity 2018 JsonUtility and System.Text.Json.
    // This protocol verifies resource compatibility, not character/visual parity.
    [Serializable]
    public sealed class ResourceProbeManifest
    {
        public int SchemaVersion = 1;
        public string EngineVersion = "2018.4.11f1";
        public string[] BundlePaths = new string[0];
        public ResourceProbeCase[] Cases = new ResourceProbeCase[0];
    }

    [Serializable]
    public sealed class ResourceProbeCase
    {
        public string Category = "";
        public string BundlePath = "";
        public string AssetName = "";
        public string AssetKind = "Material";
        public string ExpectedShaderName = "";
        public string[] RequiredTextureProperties = new string[0];
    }

    [Serializable]
    public sealed class ResourceProbeReport
    {
        public int SchemaVersion = 1;
        public string EngineVersion = "";
        public string GraphicsApi = "";
        public bool ResourceCompatibilityPassed;
        public bool VisualParityVerified; // Always false in this probe; needs a separate game comparison.
        public ResourceProbeResult[] Results = new ResourceProbeResult[0];
        public string[] Errors = new string[0];
        public string[] LoadedAssemblies = new string[0];
    }

    [Serializable]
    public sealed class ResourceProbeResult
    {
        public string Category = "";
        public string BundlePath = "";
        public string AssetName = "";
        public string MaterialName = "";
        public string ShaderName = "";
        public string[] Keywords = new string[0];
        public int RenderQueue;
        public int PassCount;
        public string[] TextureProperties = new string[0];
        public string Screenshot = "";
        public bool Passed;
        public string Error = "";
    }
}
