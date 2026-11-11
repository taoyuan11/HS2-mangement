using HS2Manager.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

/// <summary>The existing export PBR conversion, compiled directly for D3D11 without engine shader includes.</summary>
public sealed class NativePbrBakePass:IDisposable
{
    public static readonly string[] TextureNames="_MainTex _ColorMask _ColorMask_B _AlphaMask _AlphaMask2 _DetailMainTex _DetailMask _BumpMap _BumpMap2 _MetallicGlossMap _OcclusionMap _EmissionMap _Texture2 _Texture3 _Texture4 _MeshColorMask_B".Split(' ');
    private static readonly string[] vectors="_MainTex_ST _ColorMask_ST _ColorMask_B_ST _BumpMap_ST _BumpMap2_ST _OcclusionMap_ST _EmissionMap_ST _MeshColorMask_B_ST _DetailMainTex_ST _MetallicGlossMap_ST _DetailMask_ST _Color _Color2 _Color3 _Color4 _TopColor _UnderColor _MeshColor_01 _EmissionColor _texture2uv _texture3uv _Texture4UV _Texture3UV _eyebaseoffset".Split(' ');
    private static readonly string[] scalars="_Texture3Rotator _Texture4Rotator _BumpScale _BumpScale2 _alpha_a _alpha_b _alpha_c _alpha_d _Emission _BakeKind _MaterialKind _BakeAlpha _BaseMetallic _BaseSmoothness _IgnoreMainAlpha _OriginalHair _OriginalEye _PackedKind _TopGradation _UnderGradation _ColorMask_A0B1 _DetailMainScale _DetailScale _ShadowScale _NamidaScale _OcclusionStrength _EmissionGain _HasMainTex _HasColorMask _HasAlphaMask _HasAlphaMask2 _HasDetailMainTex _HasBumpMap _HasBumpMap2 _HasMetallicGlossMap _HasOcclusionMap _HasEmissionMap _HasTexture2 _HasTexture3 _HasTexture4 _HasMeshColorMask_B _HasColorMask_B _HasDetailMask".Split(' ');
    private static readonly HashSet<string> colors="_Color _Color2 _Color3 _Color4 _TopColor _UnderColor _MeshColor_01 _EmissionColor".Split(' ').ToHashSet(StringComparer.Ordinal);
    private readonly GraphicsDevice graphics;
    private readonly GpuMaterialComposer composer;
    private readonly ShaderBuffer constants;
    private readonly List<IDisposable> owned=[];
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader pixel;
    private readonly ID3D11Buffer buffer;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11DepthStencilState depth;
    private readonly ID3D11BlendState blend;
    public NativePbrBakePass(GraphicsDevice graphics,GpuMaterialComposer composer)
    {
        this.graphics=graphics;this.composer=composer;graphics.VerifyThread();
        var fields=vectors.Select(n=>(Name:n,Components:4)).Concat(scalars.Select(n=>(Name:n,Components:1))).ToArray();
        constants=new("Bake",0,fields.Length*16,fields.Select((p,i)=>new ShaderConstant(p.Name,i*16,p.Components,1,false,0)).ToArray());
        string declarations=$"cbuffer Bake:register(b0){{float4 parameters[{fields.Length}];}};\n"+
            string.Join("\n",fields.Select((p,i)=>$"#define {p.Name} parameters[{i}]"+(p.Components==1?".x":"")))+"\n"+
            string.Join("\n",TextureNames.Select((n,i)=>$"Texture2D {n}:register(t{i});SamplerState {n}Sampler:register(s{i});"));
        using var input=typeof(NativePbrBakePass).Assembly.GetManifestResourceStream("HS2Manager.Rendering.NativePbrBake.hlsl")??throw new InvalidDataException("Native PBR shader resource is missing.");
        string source=new StreamReader(input).ReadToEnd().Replace("__BINDINGS__",declarations,StringComparison.Ordinal);
        try
        {
            vertex=Own(graphics.Device.CreateVertexShader(Compiler.Compile(source,"VS","NativePbrBake","vs_5_0").Span));
            pixel=Own(graphics.Device.CreatePixelShader(Compiler.Compile(source,"frag","NativePbrBake","ps_5_0").Span));
            buffer=Own(graphics.Device.CreateBuffer((uint)constants.Size,BindFlags.ConstantBuffer));
            rasterizer=Own(graphics.Device.CreateRasterizerState(RasterizerDescription.CullNone));
            depth=Own(graphics.Device.CreateDepthStencilState(DepthStencilDescription.None));blend=Own(graphics.Device.CreateBlendState(BlendDescription.Opaque));
        }
        catch{Dispose();throw;}
    }
    private T Own<T>(T value)where T:IDisposable{owned.Add(value);return value;}
    public GpuTexture Draw(GpuMaterialInstance material,IReadOnlyDictionary<string,GpuTexture?> textures,IReadOnlyDictionary<string,float[]> overrides,int width,int height,bool color)
    {
        graphics.VerifyThread();var values=new Dictionary<string,float[]>(StringComparer.Ordinal);
        foreach(string vector in vectors)
        {
            // CopyPropertiesFromMaterial replaces the property sheet. Missing
            // source values read as zero, including destination shader defaults.
            // Texture transforms and explicit export defaults are set below.
            float[] fallback=[0,0,0,0];
            values[vector]=material.CopyPropertyValue(vector,4)??fallback;
            if(colors.Contains(vector)&&(material.ColorProperties.Contains(vector)||material.GammaProperties.Contains(vector)))
                for(int i=0;i<3;i++)values[vector][i]=GpuMaterialComposer.Linear(values[vector][i]);
        }
        foreach(string scalar in scalars)values[scalar]=material.CopyPropertyValue(scalar,1)??[0];
        foreach(string name in TextureNames)
        {
            var binding=material.Textures.GetValueOrDefault(name);values[name+"_ST"]=binding==null?[1,1,0,0]:[binding.Scale.X,binding.Scale.Y,binding.Offset.X,binding.Offset.Y];
            values["_Has"+name[1..]]=[textures.GetValueOrDefault(name)!=null?1:0];
        }
        foreach(var pair in overrides)values[pair.Key]=pair.Value;
        var target=new GpuTexture(graphics,width,height,true,color?Format.B8G8R8A8_UNorm_SRgb:Format.B8G8R8A8_UNorm,false);
        try
        {
            var context=graphics.Context;context.ClearState();context.OMSetRenderTargets(target.Target!);context.RSSetViewport(0,0,width,height);
            context.RSSetState(rasterizer);context.OMSetDepthStencilState(depth);context.OMSetBlendState(blend);context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.UpdateSubresource(ShaderBindings.Pack(constants,values),buffer);context.PSSetConstantBuffer(0,buffer);context.VSSetShader(vertex);context.PSSetShader(pixel);
            for(int i=0;i<TextureNames.Length;i++)
            {
                string name=TextureNames[i];var texture=textures.GetValueOrDefault(name)??composer.Default(name is "_MainTex" or "_OcclusionMap" or "_MetallicGlossMap"?"white":"opaque-black");
                context.PSSetShaderResource((uint)i,texture.View);context.PSSetSampler((uint)i,texture.Sampler);
            }
            context.Draw(3,0);context.ClearState();return target;
        }
        catch{graphics.Context.ClearState();target.Dispose();throw;}
    }
    public void Dispose(){foreach(var item in owned.AsEnumerable().Reverse())item.Dispose();owned.Clear();}
}
