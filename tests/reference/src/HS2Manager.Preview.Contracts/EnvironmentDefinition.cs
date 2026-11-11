using System;

namespace HS2Manager.Preview.Contracts
{
    [Serializable]
    public sealed class EnvironmentDefinition
    {
        public Parameter RenderSettings = new Parameter();
        public Parameter Quality = new Parameter();
        public Parameter Camera = new Parameter();
        [System.Runtime.Serialization.OptionalField]
        public Parameter Physics = new Parameter();
        public SceneLight[] Lights = new SceneLight[0];
        public PostProfile[] Profiles = new PostProfile[0];
        public float[] Background = new float[] { .5f, .5f, .5f, 1 };
        public bool DhhEnabled;
        public float[] DhhFloats = new float[50];
        public bool[] DhhBools = new bool[15];
        public float[] DhhColors = new float[12];
        public int SkyboxId = 6;
        public float Brightness;
        public string LutPath = "";
        public bool BetterAA;
        public float[] Temporal = new float[] { 3, .001f, .32f, .25f, 3, .5f };
    }
    [Serializable]
    public sealed class PostProfile
    {
        public float Weight=1,Priority;
        public Parameter[] Effects=new Parameter[0];
    }
    [Serializable]
    public sealed class SceneLight
    {
        public string Name = "";
        public bool CameraRelative;
        public float[] Rotation = new float[] { 0, 0, 0, 1 };
        public Parameter Parameters = new Parameter();
    }
}
