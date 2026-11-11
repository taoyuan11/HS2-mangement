using HS2Manager.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace HS2Manager.Rendering;

/// <summary>The installed replacement shader also updates the camera's shared depth attachment.</summary>
public sealed class NativeDepthNormals:IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly GpuMaterialComposer composer;
    private readonly NativeShaderAsset shader;
    private readonly GpuMaterialInstance defaults;
    private readonly Dictionary<string,NativeShaderPass[]> replacements=new(StringComparer.Ordinal);
    private readonly Dictionary<NativeShaderPass,NativeMaterialPass> passes=[];
    private GpuTexture? normals;
    public GpuTexture Texture=>normals??throw new InvalidOperationException("No depth-normal frame has been drawn.");
    public NativeDepthNormals(GraphicsDevice graphics,NativeGpuAssetStore assets,GpuMaterialComposer composer)
    {
        this.graphics=graphics;this.composer=composer;
        shader=assets.Shader(assets.GameResource("Hidden/Internal-DepthNormalsTexture",48));
        defaults=new(new("camera depth normals",default,-1,[],new Dictionary<string,float>(),new Dictionary<string,System.Numerics.Vector4>(),new Dictionary<string,GpuTextureBinding>()),shader);
        // Mesh renderers tagged as tree cutouts still use the replacement tree
        // program. The standalone scene has no Terrain/Tree instance uniforms.
        defaults.Values["_TreeInstanceScale"]=[0,0,0,0];
        defaults.Values["_SquashPlaneNormal"]=[0,0,0,0];defaults.Values["_SquashAmount"]=[0];
        defaults.Values["_TerrainEngineBendTree"]=NativeForwardRenderer.Matrix(System.Numerics.Matrix4x4.Identity);
        for(int subshader=0;subshader<shader.SubShaders.Length;subshader++)
        {
            string tag=Tag(shader,subshader,"RenderType");
            if(tag.Length>0)replacements.Add(tag,shader.Passes.Where(p=>p.SubShader==subshader).ToArray());
        }
    }
    internal static string Tag(NativeShaderAsset shader,int subshader,string name)
        =>shader.SubShaders[subshader].Get("m_Tags").Get("tags").Children.FirstOrDefault(t=>t.String("first").Equals(name,StringComparison.OrdinalIgnoreCase))?.String("second")??"";
    public void Draw(int width,int height,GpuDepthSurface depth,IReadOnlyDictionary<string,float[]> globals,
        IReadOnlyDictionary<NativeSceneRenderer,GpuMaterialInstance?[]> materials,IReadOnlyDictionary<NativeSceneRenderer,NativeGpuVertexStream> deformed)
    {
        if(normals?.Width!=width||normals.Height!=height){normals?.Dispose();normals=new(graphics,width,height,true,Format.R8G8B8A8_UNorm,false);}
        var context=graphics.Context;context.ClearState();context.OMSetRenderTargets(normals.Target!,depth.Depth);context.RSSetViewport(0,0,width,height);
        context.ClearRenderTargetView(normals.Target!,new Color4(.5f,.5f,1,1));
        foreach(var pair in materials.Where(p=>deformed.ContainsKey(p.Key)&&p.Key.ShadowCasting!=3))
        for(int index=0;index<pair.Value.Length;index++)
        {
            var material=pair.Value[index];
            if(material==null||material.Queue>2500||!replacements.TryGetValue(material.Tag("RenderType"),out var replacement))continue;
            var values=NativeForwardRenderer.MaterialValues(pair.Key,material,globals);
            foreach(var value in defaults.Values)values.TryAdd(value.Key,value.Value);
            foreach(var sourcePass in replacement)
            {
                if(!passes.TryGetValue(sourcePass,out var pass))
                {pass=new(graphics,shader,sourcePass,new HashSet<string>(),new Dictionary<string,float>(),reversedDepth:true);passes.Add(sourcePass,pass);}
                var textures=new Dictionary<string,GpuTexture>(StringComparer.Ordinal);
                foreach(var binding in pass.TextureBindings)
                {
                    var texture=composer.ResolveTexture(material,binding.Name);textures.Add(binding.Name,texture);
                    values[binding.Name+"_TexelSize"]=[1f/texture.Width,1f/texture.Height,texture.Width,texture.Height];
                    values.TryAdd(binding.Name+"_ST",[1,1,0,0]);
                }
                pass.Draw(pair.Key.Mesh,Math.Min(index,pair.Key.Mesh.Submeshes.Length-1),values,textures,gpuDeformation:deformed[pair.Key],mirroredTransform:NativeForwardRenderer.GeometryMirrored(pair.Key));
            }
        }
        context.ClearState();
    }
    public void Dispose(){foreach(var pass in passes.Values)pass.Dispose();passes.Clear();normals?.Dispose();normals=null;}
}
