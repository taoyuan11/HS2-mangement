using System.Numerics;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace HS2Manager.Rendering;

public sealed class GpuMaterialInstance
{
    public NativeShaderAsset Shader { get; private set; } = null!;
    public string Name { get; }
    private int queue = -1;
    public int Queue { get => queue >= 0 ? queue : DefaultQueue(Shader); set => queue = value; }
    public Dictionary<string, float[]> Values { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, GpuTextureBinding?> Textures { get; }
    public HashSet<string> Keywords { get; }
    public IReadOnlyDictionary<string,string> OverrideTags {get;}
    public HashSet<string> ColorProperties { get; } = new(StringComparer.Ordinal);
    public HashSet<string> GammaProperties { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> DefaultTextures { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AssetReference> ImageSelections { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, GpuTexture> TextureOverrides { get; } = new(StringComparer.Ordinal);
    private readonly HashSet<string> properties = new(StringComparer.Ordinal);
    private readonly Dictionary<string,int> propertyTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string,float[]> scalarValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string,float[]> vectorValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> assignedProperties = new(StringComparer.Ordinal);
    public GpuMaterialInstance(GpuMaterialData source, NativeShaderAsset shader)
    {
        Name = source.Name; Queue = source.Queue; Textures = source.Textures.ToDictionary(p => p.Key, p => (GpuTextureBinding?)p.Value, StringComparer.Ordinal);
        Keywords = source.Keywords.ToHashSet(StringComparer.Ordinal);OverrideTags=new Dictionary<string,string>(source.OverrideTags,StringComparer.Ordinal);
        foreach(var pair in source.Floats)scalarValues[pair.Key]=[pair.Value];
        foreach(var pair in source.Colors)vectorValues[pair.Key]=[pair.Value.X,pair.Value.Y,pair.Value.Z,pair.Value.W];
        SetShader(shader);
    }
    public bool HasProperty(string name) => properties.Contains(name)||assignedProperties.Contains(name);
    internal float[]? CopyPropertyValue(string name,int components)
    {
        // CopyPropertiesFromMaterial also carries serialized entries left by
        // an earlier shader. They remain hidden from HasProperty/GetFloat.
        if(!Values.TryGetValue(name,out var value)||value.Length!=components)
            value=(components==1?scalarValues:vectorValues).GetValueOrDefault(name);
        return value is { }&&value.Length==components?(float[])value.Clone():null;
    }
    public string Tag(string name)=>OverrideTags.TryGetValue(name,out var value)?value:
        Shader.SubShaders.FirstOrDefault()?.Get("m_Tags").Get("tags").Children.FirstOrDefault(t=>t.String("first").Equals(name,StringComparison.OrdinalIgnoreCase))?.String("second")??"";
    public void SetShader(NativeShaderAsset shader)
    {
        Shader=shader;properties.Clear();propertyTypes.Clear();Values.Clear();ColorProperties.Clear();GammaProperties.Clear();DefaultTextures.Clear();
        foreach (var property in shader.Properties.Get("m_Props").Children)
        {
            string name = property.String("m_Name"); int type = (int)property.Get("m_Type").Number;
            properties.Add(name);
            propertyTypes[name]=type;
            if (type == 4) { DefaultTextures[name] = property.Get("m_DefTexture").String("m_DefaultName"); continue; }
            float[] values = [property.Float("m_DefValue[0]"),property.Float("m_DefValue[1]"),property.Float("m_DefValue[2]"),property.Float("m_DefValue[3]")];
            // Serialized materials retain separate scalar and vector histories across shader
            // changes. A vector from an older shader must not override a current float.
            Values[name]=type is 2 or 3?scalarValues.GetValueOrDefault(name)??[values[0]]:vectorValues.GetValueOrDefault(name)??values;
            int flags=(int)property.Get("m_Flags").Number;
            // HDR colors are already linear shader values. The original material
            // upload converts ordinary colors and explicitly gamma-tagged values.
            if (type == 0&&(flags&16)==0) ColorProperties.Add(name);
            if((flags&32)!=0)GammaProperties.Add(name);
        }
        // Material property sheets also retain values absent from the current
        // ShaderLab declaration. GetFloat/CopyPropertiesFromMaterial can read
        // those values, including after MaterialEditor switches the shader.
        foreach(var name in assignedProperties.Where(n=>!properties.Contains(n)))
            if(scalarValues.TryGetValue(name,out var scalar))Values[name]=scalar;
            else if(vectorValues.TryGetValue(name,out var vector))Values[name]=vector;
    }
    public void Set(string property, params float[] values)
    {
        bool scalar=values.Length==1;
        assignedProperties.Add(property);
        (scalar?scalarValues:vectorValues)[property]=values;
        if(!propertyTypes.TryGetValue(property,out int type)||(scalar?type is 2 or 3:type is 0 or 1))Values[property]=values;
    }
    public static int DefaultQueue(NativeShaderAsset shader)
    {
        string? tag=shader.SubShaders.FirstOrDefault()?.Get("m_Tags").Get("tags").Children.FirstOrDefault(p=>p.String("first").Equals("Queue",StringComparison.OrdinalIgnoreCase))?.String("second");
        if(string.IsNullOrEmpty(tag))return 2000;
        int split=tag.IndexOfAny(['+','-']);string name=split<0?tag:tag[..split];int add=split<0?0:int.Parse(tag[split..],System.Globalization.CultureInfo.InvariantCulture);
        return checked((name switch {"Background"=>1000,"Geometry"=>2000,"AlphaTest"=>2450,"Transparent"=>3000,"Overlay"=>4000,_=>throw new NotSupportedException("Unknown material render queue: "+tag)})+add);
    }
    public void SetColor(string property, Vector4 color) => Set(property,color.X,color.Y,color.Z,color.W);
    public Vector4 Vector(string property)
    { var v=Values.GetValueOrDefault(property);return v is { Length:4 } ? new(v[0],v[1],v[2],v[3]) : Vector4.Zero; }
    public void SetTextureTransform(string property, Vector2? scale = null, Vector2? offset = null)
    {
        var previous=Textures.GetValueOrDefault(property);
        Textures[property]=new(previous?.Texture ?? default,scale ?? previous?.Scale ?? Vector2.One,offset ?? previous?.Offset ?? Vector2.Zero);
    }
    public void SetColor(string property, Parameter color, Vector4 fallback)
    {
        var value = color.Has("r") ? new Vector4(color.Float("r"),color.Float("g"),color.Float("b"),color.Float("a",1))
            : color.Children.Length >= 3 ? new Vector4((float)color.At(0).Number,(float)color.At(1).Number,(float)color.At(2).Number,color.Children.Length>3?(float)color.At(3).Number:1) : fallback;
        Set(property,value.X,value.Y,value.Z,value.W);
    }
}

/// <summary>GPU compositing of original material passes. Outputs are caller-owned and use the original resolution.</summary>
public sealed class GpuMaterialComposer : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly NativeGpuAssetStore store;
    private readonly Dictionary<GpuAssetPointer, GpuTexture> textures = [];
    private readonly Dictionary<string, GpuTexture> defaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GpuTexture> embedded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssetReference> selections;
    private readonly CharacterDefinition definition;
    private readonly int anisotropicFiltering;
    private int RenderTargetAnisotropy=>GpuTexture.EffectiveAnisotropy(1,anisotropicFiltering);
    private GpuCopyPass? copy;
    public Action<object>? Trace { get; set; }
    public HashSet<string> SourceReferenceDiagnostics {get;}=new(StringComparer.Ordinal);
    public GpuMaterialComposer(GraphicsDevice graphics, NativeGpuAssetStore store, CharacterDefinition definition)
    { this.graphics=graphics; this.store=store; this.definition=definition;selections=definition.Selections.ToDictionary(s=>s.Key,s=>s.Reference,StringComparer.Ordinal);anisotropicFiltering=definition.Environment.DhhEnabled?2:(int)definition.Environment.Quality.Float("anisotropicTextures",2); }
    public bool HasSelection(string key)=>selections.ContainsKey(key);
    public GpuMaterialInstance CreateMaterial(GpuMaterialData source)
    { var result=new GpuMaterialInstance(source,store.Shader(source.Shader));OptimizeShader(result);return result; }
    public void OptimizeShader(GpuMaterialInstance material)
    {
        var replacement=definition.Shaders.FirstOrDefault(s=>s.AutoReplace&&s.Name==material.Shader.Name);
        if(replacement!=null){int queue=material.Queue;material.SetShader(Shader(replacement.Name));material.Queue=queue;}
    }
    public NativeShaderAsset Shader(string name)
    {
        var source=definition.Shaders.FirstOrDefault(s=>s.Name==name)?.Source;
        if(source==null)return store.FindShader(name);
        // Shader definitions can point to a shader, a material using it, or a
        // prefab whose native renderer exposes it. Preserve the original loader's ABI.
        try{return store.Shader(store.Reader(source.Bundle).Find(source.Asset,48));}
        catch(InvalidDataException error)when(error.Message.StartsWith("Resource ",StringComparison.Ordinal)){}
        try{return store.Shader(store.Material(store.Reader(source.Bundle).Find(source.Asset,21)).Shader);}
        catch(InvalidDataException error)when(error.Message.StartsWith("Resource ",StringComparison.Ordinal)){}
        try
        {
            var prefab=store.Reader(source.Bundle).Prefab(source.Asset);
            foreach(var renderer in prefab.Renderers)foreach(var reference in renderer.Materials.Where(p=>p.PathId!=0))
            {var shader=store.Shader(store.Material(reference).Shader);if(shader.Name==name)return shader;}
        }
        catch(InvalidDataException error)when(error.Message.StartsWith("Resource ",StringComparison.Ordinal)){}
        return store.FindShader(name);
    }
    public GpuMaterialInstance Material(string selection)
    {
        var reference = Selection(selection);
        var source = store.Material(store.Reader(reference.Bundle).Find(reference.Asset,21));
        return CreateMaterial(source);
    }
    public GpuMaterialInstance ShaderMaterial(string selection)
    {
        var reference=Selection(selection);var shader=store.Shader(store.Reader(reference.Bundle).Find(reference.Asset,48));
        return new(new(shader.Name,default,-1,[],new Dictionary<string,float>(),new Dictionary<string,Vector4>(),new Dictionary<string,GpuTextureBinding>()),shader);
    }
    public void SelectTexture(GpuMaterialInstance material, string property, string selection)
    {
        SelectTexture(material,property,selections.GetValueOrDefault(selection));
    }
    public void SelectTexture(GpuMaterialInstance material, string property, AssetReference? reference)
    {
        material.ImageSelections.Remove(property);
        material.TextureOverrides.Remove(property);
        var prior=material.Textures.GetValueOrDefault(property);
        var scale=prior?.Scale??Vector2.One;var offset=prior?.Offset??Vector2.Zero;
        if (reference==null) { material.Textures[property]=new(default,scale,offset); return; }
        if (reference.Bundle.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        { material.Textures[property]=new(default,scale,offset); material.ImageSelections[property]=reference; return; }
        if(!store.Reader(reference.Bundle).TryFind(reference.Asset,28,out var pointer))
            SourceReferenceDiagnostics.Add($"Missing authored texture selection: {material.Name}/{property}: {reference.Bundle}/{reference.Asset}");
        material.Textures[property]=new(pointer,scale,offset);
    }
    public GpuTexture Texture(string selection)
    {
        var reference=Selection(selection);
        if (reference.Bundle.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return LooseImage(reference);
        return Texture(store.Reader(reference.Bundle).Find(reference.Asset,28));
    }
    public bool TryTexture(string selection,out GpuTexture texture)
    {
        var reference=Selection(selection);
        if(reference.Bundle.EndsWith(".png",StringComparison.OrdinalIgnoreCase)){texture=LooseImage(reference);return true;}
        if(store.Reader(reference.Bundle).TryFind(reference.Asset,28,out var pointer)){texture=Texture(pointer);return true;}
        SourceReferenceDiagnostics.Add($"Missing authored composition texture: {selection}: {reference.Bundle}/{reference.Asset}");texture=null!;return false;
    }
    private GpuTexture LooseImage(AssetReference reference) => Embedded(new EmbeddedTextureReference
    { Path = reference.Bundle, Length = checked((int)new FileInfo(reference.Bundle).Length) }, reference.Asset.Contains("clamp", StringComparison.Ordinal));
    public GpuTexture Embedded(EmbeddedTextureReference reference, bool clamp = false)
    {
        graphics.VerifyThread();
        string key = reference.Path + ":" + reference.Offset + ":" + reference.Length + ":" + clamp;
        if (embedded.TryGetValue(key, out var cached)) return cached;
        if (reference.Offset < 0 || reference.Length is < 1 or > 64 * 1024 * 1024)
            throw new InvalidDataException("Invalid embedded image range.");
        byte[] encoded = new byte[reference.Length];
        using (var file = File.OpenRead(reference.Path))
        {
            if (reference.Offset > file.Length - reference.Length) throw new InvalidDataException("Embedded image is truncated.");
            file.Position = reference.Offset; file.ReadExactly(encoded);
        }
        var decoded = ImageCodec.DecodeTextureAsync(encoded, CancellationToken.None, preserveResolution: true).GetAwaiter().GetResult();
        int stride = decoded.Width * 4;
        byte[] bottomUp = new byte[decoded.Pixels.Length];
        for (int row = 0; row < decoded.Height; row++)
            decoded.Pixels.AsSpan(row * stride, stride).CopyTo(bottomUp.AsSpan((decoded.Height - 1 - row) * stride));
        using var source = new GpuTexture(graphics, new(key, decoded.Width, decoded.Height, 1, 1, 2, 14, true, 1, 1, 0, 0, 0, 0, bottomUp));
        var result = new GpuTexture(graphics, decoded.Width, decoded.Height, clamp,anisotropy:RenderTargetAnisotropy);
        try
        {
            (copy ??= new GpuCopyPass(graphics)).Copy(source, result);
            result.GenerateMips(graphics);
            embedded.Add(key, result); return result;
        }
        catch { result.Dispose(); throw; }
    }
    public GpuTexture Overlay(GpuTexture source, OverlayDefinition overlay, int width, int height = 0)
    {
        if (overlay.Texture == null) throw new InvalidDataException("Skin overlay has no image payload.");
        return Compose(source, ShaderMaterial("overlay.composite"), width, height,
            new Dictionary<string, GpuTexture> { ["_Overlay"] = Embedded(overlay.Texture) }, preserveDestination: true);
    }
    public GpuTexture Copy(GpuTexture source, int width, int height)
    {
        var result=new GpuTexture(graphics,width,height,clamp:true,anisotropy:RenderTargetAnisotropy);
        try{(copy??=new GpuCopyPass(graphics)).Copy(source,result);result.GenerateMips(graphics);return result;}
        catch{result.Dispose();throw;}
    }
    private AssetReference Selection(string key) => selections.GetValueOrDefault(key) ?? throw new InvalidDataException("Missing appearance resource: "+key);
    public GpuTexture Texture(GpuAssetPointer pointer)
    {
        pointer=store.Resolve(pointer);
        if (!textures.TryGetValue(pointer,out var texture)) { texture=new(graphics,store.Texture(pointer),anisotropicFiltering); textures.Add(pointer,texture); }
        return texture;
    }
    public GpuTexture Compose(GpuTexture source, GpuMaterialInstance material, int width, int height = 0,
        IReadOnlyDictionary<string,GpuTexture>? overrides = null, bool preserveDestination = false)
    {
        graphics.VerifyThread(); if (height==0) height=width;
        var target=new GpuTexture(graphics,width,height,clamp:true,anisotropy:RenderTargetAnisotropy);
        try
        {
            var pass=material.Shader.Passes[0];
            var keywords=material.Keywords.ToHashSet(StringComparer.Ordinal);
            keywords.Add("DIRECTIONAL");keywords.Add("LIGHTPROBE_SH");
            var vertex=ShaderBindings.Select(material.Shader,pass,NativeShaderStage.Vertex,keywords);
            var pixel=ShaderBindings.Select(material.Shader,pass,NativeShaderStage.Pixel,keywords);
            var values=material.Values.ToDictionary(p=>p.Key,p=>(float[])p.Value.Clone(),StringComparer.Ordinal);
            foreach(string property in material.DefaultTextures.Keys.Concat(material.Textures.Keys).Distinct(StringComparer.Ordinal))
            {
                var transform=material.Textures.GetValueOrDefault(property);
                values[property+"_ST"]=transform==null?[1,1,0,0]:[transform.Scale.X,transform.Scale.Y,transform.Offset.X,transform.Offset.Y];
            }
            foreach (string color in material.ColorProperties.Union(material.GammaProperties))
                if (values.TryGetValue(color,out var channels)) for(int i=0;i<Math.Min(3,channels.Length);i++) channels[i]=Linear(channels[i]);
            foreach (string name in new[]{"unity_ObjectToWorld","unity_WorldToObject","unity_MatrixVP","unity_MatrixV","unity_MatrixInvV","glstate_matrix_projection"})
                values[name]=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
            // Preserve the asset's bottom-up texture convention through every composition pass.
            // Only the final screenshot readback changes row order.
            values["unity_MatrixVP"][5]=-1;
            values["_ScreenParams"]=[width,height,1+1f/width,1+1f/height]; values["_ProjectionParams"]=[1,.1f,1000,.001f];
            var resources=new Dictionary<string,GpuTexture>(StringComparer.Ordinal);
            foreach(var binding in vertex.Textures.Concat(pixel.Textures).DistinctBy(t=>t.Name))
            {
                var sourceBinding=material.Textures.GetValueOrDefault(binding.Name);
                var texture=overrides?.GetValueOrDefault(binding.Name) ?? (binding.Name=="_MainTex"?source:ResolveTexture(material,binding.Name));
                resources.Add(binding.Name,texture);
                values[binding.Name+"_TexelSize"]=[1f/texture.Width,1f/texture.Height,texture.Width,texture.Height];
                values[binding.Name+"_ST"]=sourceBinding==null?[1,1,0,0]:[sourceBinding.Scale.X,sourceBinding.Scale.Y,sourceBinding.Offset.X,sourceBinding.Offset.Y];
            }
            var floats=material.Values.Where(p=>p.Value.Length==1).ToDictionary(p=>p.Key,p=>p.Value[0],StringComparer.Ordinal);
            Trace?.Invoke(new { Shader=material.Shader.Name, Values=values, Textures=resources.ToDictionary(p=>p.Key,p=>new {p.Value.Width,p.Value.Height,Format=p.Value.Texture.Description.Format.ToString()}) });
            using var renderPass=new NativeMaterialPass(graphics,material.Shader,pass,keywords,floats);
            graphics.Context.OMSetRenderTargets(target.Target!); graphics.Context.RSSetViewport(0,0,width,height);
            graphics.Context.ClearRenderTargetView(target.Target!,new Color4(0,0,0,0));
            if(preserveDestination)
            {
                (copy??=new GpuCopyPass(graphics)).Copy(source,target);
                graphics.Context.OMSetRenderTargets(target.Target!);graphics.Context.RSSetViewport(0,0,width,height);
            }
            renderPass.Draw(Quad,0,values,resources);
            graphics.Context.ClearState();target.GenerateMips(graphics);
            return target;
        }
        catch { graphics.Context.ClearState(); target.Dispose(); throw; }
    }
    public GpuTexture ResolveTexture(GpuMaterialInstance material,string property)
    {
        var binding=material.Textures.GetValueOrDefault(property);
        try
        {
            if(material.TextureOverrides.TryGetValue(property,out var output))return output;
            if(material.ImageSelections.TryGetValue(property,out var loose))return LooseImage(loose);
            if(binding?.Texture.PathId is not (null or 0))
            {
                if(store.AssetClass(binding.Texture) is 28 or 89)return Texture(binding.Texture);
                // A dangling local PPtr is a null texture in the source runtime.
                // Preserve that authored state, while retaining evidence for review.
                SourceReferenceDiagnostics.Add($"Dangling authored texture: {material.Name}/{property}: {binding.Texture}");
            }
            return Default(material.DefaultTextures.GetValueOrDefault(property,"white"));
        }
        catch(Exception error) when(error is InvalidDataException or FileNotFoundException or NullReferenceException)
        {throw new InvalidDataException($"Texture binding failed: {material.Name}/{material.Shader.Name}/{property}: {binding?.Texture}",error);}
    }
    public GpuTexture? AssignedTexture(GpuMaterialInstance material,string property)
    {
        if(!material.HasProperty(property))return null;
        if(material.TextureOverrides.TryGetValue(property,out var output))return output;
        if(material.ImageSelections.TryGetValue(property,out var loose))return LooseImage(loose);
        var pointer=material.Textures.GetValueOrDefault(property)?.Texture;
        return pointer is {PathId:not 0} value&&store.AssetClass(value) is 28 or 89?Texture(value):null;
    }
    public bool TryGlobalTexture(string name,out GpuTexture texture)
    {
        // Kermo's compiled rim sampler has no ShaderLab property or assignment.
        // The reference GPU binds UnityDefault2D: 16x16 sRGB RGBA (128,128,128,128).
        if(name=="_RimEmissionMap")
        {
            const string key="unassigned-global-2d";
            if(!defaults.TryGetValue(key,out texture!))
            {texture=new(graphics,new(key,16,16,1,1,2,4,true,1,1,0,0,0,0,Enumerable.Repeat((byte)128,16*16*4).ToArray()),anisotropicFiltering);defaults.Add(key,texture);}
            return true;
        }
        texture=null!;return false;
    }
    public GpuTexture Default(string name)
    {
        if(defaults.TryGetValue(name,out var texture))return texture;
        byte[] rgba=name switch
        // Unity 2018 ShaderLab defaults have different alpha values from Texture2D.blackTexture.
        // https://docs.unity.cn/2018.4/Documentation/Manual/SL-Properties.html
        { "" or "white"=>[255,255,255,255],"black"=>[0,0,0,0],"opaque-black"=>[0,0,0,255],"gray" or "grey"=>[127,127,127,127],"red"=>[255,0,0,0],
            // D3D11's runtime normal fallback is RGBA (127,127,255,255).
            // ShaderLab's documented nominal bump colour has a different alpha;
            // using it with the installed RG-or-AG unpacking tilts the normal by 30°.
            "bump"=>[127,127,255,255],_=>throw new NotSupportedException("Unknown default texture "+name) };
        // The captured ShaderLab gray default is sRGB RGBA (127,127,127,127).
        // UnityDefault2D, used for unassigned globals above, is a separate 128 image.
        texture=new(graphics,new(name,1,1,1,1,2,4,name is "gray" or "grey",1,1,0,0,0,0,rgba)); defaults.Add(name,texture); return texture;
    }
    public byte[] ReadImage(GpuTexture texture)
    {
        byte[] pixels;
        if(texture.Texture.Description.Format is Vortice.DXGI.Format.B8G8R8A8_UNorm or Vortice.DXGI.Format.B8G8R8A8_UNorm_SRgb or Vortice.DXGI.Format.B8G8R8A8_Typeless)
            pixels=graphics.Readback(texture.Texture,texture.Width,texture.Height);
        else
        {
            using var resolved=new GpuTexture(graphics,texture.Width,texture.Height,true,mipmaps:false);
            (copy??=new GpuCopyPass(graphics)).Copy(texture,resolved);pixels=graphics.Readback(resolved.Texture,texture.Width,texture.Height);
        }
        int stride=texture.Width*4;
        byte[] topDown=new byte[pixels.Length];for(int row=0;row<texture.Height;row++)pixels.AsSpan(row*stride,stride).CopyTo(topDown.AsSpan((texture.Height-1-row)*stride));return topDown;
    }
    public void Dispose() { copy?.Dispose();foreach(var texture in textures.Values)texture.Dispose();foreach(var texture in defaults.Values)texture.Dispose();foreach(var texture in embedded.Values)texture.Dispose();textures.Clear();defaults.Clear();embedded.Clear(); }
    public static float Linear(float value) => value<=.04045f?value/12.92f:value<1?MathF.Pow((value+.055f)/1.055f,2.4f):MathF.Pow(value,2.2f);
    private static readonly GpuMeshData Quad=CreateQuad();
    private static GpuMeshData CreateQuad()
    {
        float[] vertex=[-1,-1,0,0,0,1,-1,0,1,0,-1,1,0,0,1,1,1,0,1,1];byte[] bytes=new byte[vertex.Length*4];Buffer.BlockCopy(vertex,0,bytes,0,bytes.Length);
        ushort[] indices=[0,1,2,2,1,3];byte[] index=new byte[12];Buffer.BlockCopy(indices,0,index,0,12);
        return new("composite",4,[new(0,0,0,0,3),new(4,0,12,0,2)],[0],[20],bytes,2,index,[new(0,6,0,0)],[],[]);
    }
}
