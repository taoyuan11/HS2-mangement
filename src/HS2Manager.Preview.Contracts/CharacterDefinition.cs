using System;

namespace HS2Manager.Preview.Contracts
{
    [Serializable]
    public sealed class CharacterDefinition
    {
        public int SchemaVersion = 1;
        public string Name = "";
        public string CardHash = "";
        public string MotionState = "";
        public int Sex;
        public string[] BundlePaths = new string[0];
        public CharacterPart[] Parts = new CharacterPart[0];
        public AssetSelection[] Selections = new AssetSelection[0];
        public Parameter Face = new Parameter();
        public Parameter Body = new Parameter();
        public Parameter Hair = new Parameter();
        public Parameter Clothes = new Parameter();
        public string[] Diagnostics = new string[0];
        public bool BodyVisible = true;
        public float EyeOpenMax = 1;
        public MaterialEdit[] MaterialEdits = new MaterialEdit[0];
        [System.Runtime.Serialization.OptionalField]
        public ShaderDefinition[] Shaders = new ShaderDefinition[0];
        public OverlayDefinition[] Overlays = new OverlayDefinition[0];
        public HeelDefinition Heels = new HeelDefinition();
        public EnvironmentDefinition Environment = new EnvironmentDefinition();
        public int ParameterNodes;
        public int CountParameterNodes()
        {
            int count=Face.CountNodes()+Body.CountNodes()+Hair.CountNodes()+Clothes.CountNodes()+Environment.RenderSettings.CountNodes()+Environment.Quality.CountNodes()+Environment.Camera.CountNodes();
            foreach(var part in Parts){count+=part.List.CountNodes()+part.Parameters.CountNodes();foreach(var component in part.Components)count+=component.Data.CountNodes();}
            foreach(var light in Environment.Lights)count+=light.Parameters.CountNodes();
            foreach(var profile in Environment.Profiles)foreach(var effect in profile.Effects)count+=effect.CountNodes();
            return count;
        }
    }

    [Serializable]
    public sealed class ShaderDefinition
    {
        public string Name = "";
        public AssetReference Source;
        public int RenderQueue = -1;
        public ShaderDefault[] Defaults = new ShaderDefault[0];
    }

    [Serializable]
    public sealed class ShaderDefault
    {
        public string Property = "", Type = "";
        public float[] Values = new float[0];
        public AssetReference Texture;
    }

    [Serializable]
    public sealed class HeelDefinition
    {
        public float Height;
        public HeelBone[] Bones=new HeelBone[0];
    }
    [Serializable]
    public sealed class HeelBone
    {
        public string Name="";
        public float[] Move=new float[3],Roll=new float[3],Scale=new float[]{1,1,1};
        public bool Fixed;
    }
    [Serializable]
    public sealed class AssetReference
    {
        public string Bundle = "";
        public string Asset = "";
    }

    [Serializable]
    public sealed class AssetSelection
    {
        public string Key = "";
        public AssetReference Reference = new AssetReference();
    }

    [Serializable]
    public sealed class CharacterPart
    {
        public string Role = "";
        public int Slot;
        public AssetReference Prefab = new AssetReference();
        public string Parent = "";
        public BoneDefinition[] Bones = new BoneDefinition[0];
        public ComponentDefinition[] Components = new ComponentDefinition[0];
        public float[] Adjustments = new float[0];
        public float[] Colors = new float[0];
        public bool[] HideOptions = new bool[0];
        public Parameter List = new Parameter();
        public Parameter Parameters = new Parameter();
    }

    [Serializable]
    public sealed class EmbeddedTextureReference
    {
        public string Path = "";
        public long Offset;
        public int Length;
    }
    [Serializable]
    public sealed class MaterialEdit
    {
        public int ObjectType;
        public int Slot;
        public string Name = "";
        public string Property = "";
        public string Kind = "";
        public string Text = "";
        public float Number;
        public float[] Values = new float[0];
        public EmbeddedTextureReference Texture;
        public float[] Offset;
        public float[] Scale;
    }
    [Serializable]
    public sealed class OverlayDefinition
    {
        public int Type;
        public string Name = "";
        public bool Replace;
        public EmbeddedTextureReference Texture;
    }

    [Serializable]
    public sealed class BoneDefinition
    {
        public string Name = "";
        public string Parent = "";
        public float[] Position = new float[3];
        public float[] Rotation = new float[] { 0, 0, 0, 1 };
        public float[] Scale = new float[] { 1, 1, 1 };
    }

    [Serializable]
    public sealed class ComponentDefinition
    {
        public string Node = "";
        public string Type = "";
        public Parameter Data = new Parameter();
    }

    // Lossless named parameter tree for card data and serialized component configuration.
    // Object references are names, never objects from the game's CLR assemblies.
    [Serializable]
    public sealed class Parameter
    {
        public string Name = "";
        public string Encoding = "";
        public string Text = "";
        public double Number;
        public Parameter[] Children = new Parameter[0];

        public Parameter Get(string name)
        {
            foreach (var child in Children) if (child.Name == name) return child;
            return new Parameter { Name = name };
        }
        public bool Has(string name)
        {
            foreach (var child in Children) if (child.Name == name) return true;
            return false;
        }
        public float Float(string name, float fallback = 0) { return Has(name) ? (float)Get(name).Number : fallback; }
        public string String(string name) { return Get(name).Text; }
        public Parameter At(int index) { return index >= 0 && index < Children.Length ? Children[index] : new Parameter(); }
        public int CountNodes() { int count=1;foreach(var child in Children)count+=child.CountNodes();return count; }
    }
}
