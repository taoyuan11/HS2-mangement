using System;

namespace HS2Manager.Preview.Contracts
{
    [Serializable]
    public sealed class ModelExportRequest
    {
        public string DefinitionPath = "", OutputDirectory = "";
        public long EditRevision;
        public bool IncludeScene;
        public ModelExportView View = new ModelExportView();
    }
    [Serializable]
    public sealed class ModelExportResult
    {
        public int SchemaVersion = 2;
        public long EditRevision;
        public string Target = "GenericFbx", ToolVersion = "", EntryPoint = "character.fbx";
        public int Meshes, Vertices, Triangles, Bones, BlendShapes, Materials;
        public ModelExportMesh[] Geometry = new ModelExportMesh[0];
        public string[] Warnings = new string[0];
        public string[] Issues = new string[0];
    }
    [Serializable]
    public sealed class ModelExportMesh
    {
        public string Node = "";
        public int Vertices;
        public float RebaseError;
        public int SkinInfluences;
        public int[] SampleIndices = new int[0];
        public float[] ReferencePositions = new float[0];
        public string[] BlendShapeNames = new string[0];
        public float[] BlendShapeMaximumDisplacements = new float[0]; // metres, across all frames/vertices
        public float[] DefaultWeights = new float[0];
        public float[] SourceDefaultWeights = new float[0];
        public string NeutralResetShape = "";
    }

    // Coordinates remain in Unity's character space (one source unit = 0.1 m).
    // Converters apply the same axis/unit transform as their FBX importer.
    [Serializable]
    public sealed class ModelExportView
    {
        public bool HasCamera;
        public float[] Position = new float[3], Target = new float[3], Up = new float[] { 0, 1, 0 };
        public float FieldOfView = 35;
        public int Width = 1280, Height = 960;
        public bool UseGameBackground = true;
        public float[] Background = new float[] { .16f, .17f, .19f, 1 };
        public float KeyLightMultiplier = 1;
    }

    [Serializable]
    public sealed class ModelExportScene
    {
        public int SchemaVersion = 1;
        public ModelExportView View = new ModelExportView();
        public float NearClip, FarClip;
        public float[] SourceRootPosition = new float[3];
        public bool SkyBackground;
        public float[] Background = new float[4];
        public float[] AmbientSky = new float[4], AmbientEquator = new float[4], AmbientGround = new float[4];
        public float AmbientIntensity, ReflectionIntensity;
        public int AmbientMode;
        public string EnvironmentTexture = "", ReflectionTexture = "", LutTexture = "";
        public string LutEncoding = "None";
        public int LutSize;
        public ModelExportLight[] Lights = new ModelExportLight[0];
        public ModelExportPostProcessing Post = new ModelExportPostProcessing();
        public string[] Warnings = new string[0];
    }

    [Serializable]
    public sealed class ModelExportLight
    {
        public string Name = "";
        public int Type;
        public float[] Position = new float[3], Direction = new float[3], Color = new float[4];
        public float Intensity, Range, SpotAngle, ShadowStrength;
        public bool Shadows;
    }

    [Serializable]
    public sealed class ModelExportPostProcessing
    {
        public bool ColorGrading, Bloom, AmbientOcclusion, Vignette;
        public string Tonemapper = "None";
        public float Exposure, Temperature, Tint, HueShift, Saturation, Contrast;
        public float[] ColorFilter = new float[] { 1, 1, 1, 1 };
        public float[] Lift = new float[] { 1, 1, 1, 0 }, Gamma = new float[] { 1, 1, 1, 0 }, Gain = new float[] { 1, 1, 1, 0 };
        public float LutContribution, BloomIntensity, BloomThreshold, AoIntensity, AoRadius, VignetteIntensity;
    }
}
