using System;

namespace HS2Manager.Preview.Contracts
{
    [Serializable]
    public sealed class ModelExportRequest
    {
        public string DefinitionPath = "", OutputDirectory = "";
        public long EditRevision;
    }
    [Serializable]
    public sealed class ModelExportResult
    {
        public int SchemaVersion = 1;
        public long EditRevision;
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
        public float[] DefaultWeights = new float[0];
        public float[] SourceDefaultWeights = new float[0];
        public string NeutralResetShape = "";
    }
}
