using System.Numerics;
using HS2Manager.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace HS2Manager.Rendering;

/// <summary>Original MRT material, reflection and directional-light passes for deferred-capable surfaces.</summary>
public sealed class NativeDeferredRenderer : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly GpuMaterialComposer composer;
    private readonly GpuCharacterAppearance appearance;
    private readonly GpuTexture reflection;
    private readonly (ID3D11ShaderResourceView,ID3D11SamplerState) probe;
    private readonly NativeShaderAsset lightShader,reflectionShader;
    private readonly Dictionary<(GpuMaterialInstance,int),NativeMaterialPass> materials=[];
    private readonly Dictionary<(int,bool),NativeFullscreenPass> lights=[];
    private readonly Dictionary<int,NativeFullscreenPass> reflections=[];
    private readonly Dictionary<(int Layer,bool Shadows),int> groups=[];
    private GpuTexture[] buffers=[];
    private GpuTexture? reflectionBuffer;
    private readonly Dictionary<int,NativeFullscreenPass> reflectionComposites=[];
    private GpuDepthSurface? stencilDepth;
    public IReadOnlyList<GpuTexture> Buffers=>buffers;
    public GpuTexture ReflectionBuffer=>reflectionBuffer??throw new InvalidOperationException("No deferred frame has been rendered.");
    public static bool Supports(GpuMaterialInstance material)=>material.Queue<=2500&&Pass(material)!=null;
    private static NativeShaderPass? Pass(GpuMaterialInstance material)=>material.Shader.Passes.FirstOrDefault(p=>p.SubShader==0&&NativeForwardRenderer.Tag(p,"LightMode").Equals("Deferred",StringComparison.OrdinalIgnoreCase));
    public NativeDeferredRenderer(GraphicsDevice graphics,NativeGpuAssetStore store,GpuMaterialComposer composer,GpuCharacterAppearance appearance,GpuTexture reflection,
        (ID3D11ShaderResourceView,ID3D11SamplerState) probe)
    {
        this.graphics=graphics;this.composer=composer;this.appearance=appearance;this.reflection=reflection;this.probe=probe;
        lightShader=store.Shader(store.GameResource("Hidden/Internal-DeferredShading",48));
        reflectionShader=store.Shader(store.GameResource("Hidden/Internal-DeferredReflections",48));
    }
    private static DepthStencilDescription Stencil(bool write)=>new()
    {
        DepthEnable=write,DepthWriteMask=write?DepthWriteMask.All:DepthWriteMask.Zero,DepthFunc=ComparisonFunction.GreaterEqual,
        StencilEnable=true,StencilReadMask=255,StencilWriteMask=write?(byte)255:(byte)0,
        FrontFace=new(){StencilFunc=write?ComparisonFunction.Always:ComparisonFunction.Equal,StencilPassOp=write?StencilOperation.Replace:StencilOperation.Keep,StencilFailOp=StencilOperation.Keep,StencilDepthFailOp=StencilOperation.Keep},
        BackFace=new(){StencilFunc=write?ComparisonFunction.Always:ComparisonFunction.Equal,StencilPassOp=write?StencilOperation.Replace:StencilOperation.Keep,StencilFailOp=StencilOperation.Keep,StencilDepthFailOp=StencilOperation.Keep}
    };
    private int Group(NativeSceneRenderer renderer)
    {
        var key=(renderer.Node.Layer,renderer.ReceiveShadows);
        if(!groups.TryGetValue(key,out int index))
        {index=groups.Count+1;if(index>255)throw new NotSupportedException("Deferred light-mask groups exceed the stencil capacity.");groups.Add(key,index);}
        return index;
    }
    private void Resize(int width,int height)
    {
        if(stencilDepth?.Width==width&&stencilDepth.Height==height)return;
        foreach(var buffer in buffers)buffer.Dispose();buffers=[];stencilDepth?.Dispose();stencilDepth=null;reflectionBuffer?.Dispose();reflectionBuffer=null;
        var pending=new List<GpuTexture>();
        try
        {
            // Both colour and specular MRT views are sRGB in the captured
            // 2018 player; normal packing remains linear R10G10B10A2.
            foreach(var format in new[]{Format.R8G8B8A8_UNorm_SRgb,Format.R8G8B8A8_UNorm_SRgb,Format.R10G10B10A2_UNorm})pending.Add(new(graphics,width,height,true,format,false));
            stencilDepth=new(graphics,width,height);reflectionBuffer=new(graphics,width,height,true,Format.R16G16B16A16_Float,false);buffers=pending.ToArray();
        }
        catch{foreach(var buffer in pending)buffer.Dispose();throw;}
    }
    public void Draw(GpuTexture target,NativeRenderCamera camera,GpuDepthSurface depth,GpuDepthSurface cameraDepth,IReadOnlyDictionary<string,float[]> globals,
        IReadOnlyList<NativeDirectionalLight> frameLights,NativeDirectionalShadows shadows,IReadOnlyDictionary<NativeSceneRenderer,NativeGpuVertexStream> deformed,
        Action<GpuDepthSurface> prepareForwardDepthAndShadows)
    {
        graphics.VerifyThread();Resize(target.Width,target.Height);var context=graphics.Context;
        context.ClearState();context.CopyResource(stencilDepth!.Texture,depth.Texture);
        context.ClearDepthStencilView(stencilDepth.Depth,DepthStencilClearFlags.Stencil,0,0);
        foreach(var buffer in buffers)context.ClearRenderTargetView(buffer.Target!,new Color4(0,0,0,0));
        context.ClearRenderTargetView(reflectionBuffer!.Target!,new Color4(0,0,0,1));
        context.OMSetRenderTargets([buffers[0].Target!,buffers[1].Target!,buffers[2].Target!,target.Target!],stencilDepth.Depth);
        context.RSSetViewport(0,0,target.Width,target.Height);
        var activeGroups=new HashSet<int>();
        // Submit deferred geometry in shader groups, with the newest material
        // instances first inside each group. Some multi-material assets put a
        // depth surface in one shader and additive emission in another. Their
        // depth surfaces must all run before the emission group is submitted.
        var draws=appearance.Materials.Where(p=>deformed.ContainsKey(p.Key)&&p.Key.ShadowCasting!=3)
            .SelectMany(pair=>pair.Value.Select((material,index)=>(Renderer:pair.Key,Material:material,Submesh:index))).Reverse()
            .Where(draw=>draw.Material!=null&&Supports(draw.Material))
            .OrderBy(draw=>draw.Material!.Queue)
            .ThenBy(draw=>draw.Material!.Shader.SourceBundle,StringComparer.OrdinalIgnoreCase).ThenBy(draw=>draw.Material!.Shader.PathId)
            .GroupBy(draw=>(draw.Material!.Queue,draw.Material.Shader.SourceBundle,draw.Material.Shader.Name,draw.Material.Shader.PathId,
                Keywords:string.Join(" ",draw.Material.Keywords.Order(StringComparer.Ordinal))))
            .SelectMany(group=>group);
        foreach(var draw in draws)
        {
            var renderer=draw.Renderer;int index=draw.Submesh;
            {
                var material=draw.Material!;
                int group=Group(renderer);activeGroups.Add(group);
                var pass=Pass(material)!;var keywords=material.Keywords.ToHashSet();keywords.UnionWith(["LIGHTPROBE_SH","UNITY_HDR_ON","LIGHTMAP_OFF","DIRLIGHTMAP_OFF","DYNAMICLIGHTMAP_OFF"]);
                if(!materials.TryGetValue((material,group),out var native))
                {
                    native=new(graphics,material.Shader,pass,keywords,material.Values.Where(p=>p.Value.Length==1).ToDictionary(p=>p.Key,p=>p.Value[0]),reversedDepth:true,depthOverride:Stencil(true),stencilOverride:(uint)group);
                    materials.Add((material,group),native);
                }
                var values=NativeForwardRenderer.MaterialValues(renderer,material,globals);
                var textures=new Dictionary<string,GpuTexture>();var views=new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>();
                foreach(var binding in native.TextureBindings)
                    {
                        if(binding.Name=="unity_ProbeVolumeSH"){views[binding.Name]=probe;continue;}
                        var texture=binding.Name is "unity_SpecCube0" or "unity_SpecCube1"?reflection:composer.ResolveTexture(material,binding.Name);
                        textures[binding.Name]=texture;values[binding.Name+"_TexelSize"]=[1f/texture.Width,1f/texture.Height,texture.Width,texture.Height];
                    }
                native.Draw(renderer.Mesh,Math.Min(index,renderer.Mesh.Submeshes.Length-1),values,textures,resourceViews:views,gpuDeformation:deformed[renderer],mirroredTransform:NativeForwardRenderer.GeometryMirrored(renderer));
            }
        }
        context.ClearState();
        prepareForwardDepthAndShadows(stencilDepth);
        context.ClearState();
        context.CopyResource(depth.Texture,stencilDepth.Texture);
        if(activeGroups.Count==0)return;
        var screenValues=new Dictionary<string,float[]>(globals)
        {
            ["_LightAsQuad"]=[1],["unity_MatrixV"]=NativeForwardRenderer.Matrix(Matrix4x4.Identity),
            ["_ProjectionParams"]=[-1,camera.Near,camera.Far,1/camera.Far],
            ["unity_CameraToWorld"]=NativeForwardRenderer.Matrix(camera.World),["unity_SpecCube1_ProbePosition"]=[0,0,0,1]
        };
        var resources=new Dictionary<string,GpuTexture>{{"_CameraGBufferTexture0",buffers[0]},{"_CameraGBufferTexture1",buffers[1]},{"_CameraGBufferTexture2",buffers[2]},{"unity_SpecCube0",reflection}};
        var depthResources=new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>{{"_CameraDepthTexture",(cameraDepth.View,cameraDepth.Sampler)}};
        var blend=new Dictionary<string,float>{{"_SrcBlend",1},{"_DstBlend",1}};
        foreach(int group in activeGroups)
        {
            if(!reflections.TryGetValue(group,out var pass))
            {pass=new(graphics,reflectionShader,0,engineBlit:true,properties:blend,depthOverride:Stencil(false),stencilOverride:(uint)group);reflections.Add(group,pass);}
            pass.Draw(reflectionBuffer!,screenValues,resources,depthResources,camera,stencilDepth.ReadOnlyDepth);
        }
        resources["_CameraReflectionsTexture"]=reflectionBuffer!;
        foreach(int group in activeGroups)
        {
            if(!reflectionComposites.TryGetValue(group,out var pass))
            {pass=new(graphics,reflectionShader,1,new HashSet<string>{"UNITY_HDR_ON"},engineBlit:true,properties:blend,depthOverride:Stencil(false),stencilOverride:(uint)group);reflectionComposites.Add(group,pass);}
            pass.Draw(target,screenValues,resources,depthResources,camera,stencilDepth.ReadOnlyDepth);
        }
        foreach(var group in groups.Where(g=>activeGroups.Contains(g.Value)))
        for(int index=0;index<frameLights.Count;index++)
        {
            var light=frameLights[index];if((light.Mask&(1u<<group.Key.Layer))==0)continue;
            bool shadowed=group.Key.Shadows&&light.ShadowStrength>0;
            if(!lights.TryGetValue((group.Value,shadowed),out var pass))
            {
                var keywords=new HashSet<string>{"DIRECTIONAL","UNITY_HDR_ON"};if(shadowed)keywords.Add("SHADOWS_SCREEN");
                pass=new(graphics,lightShader,0,keywords,true,properties:blend,depthOverride:Stencil(false),stencilOverride:(uint)group.Value);lights.Add((group.Value,shadowed),pass);
            }
            screenValues["_LightDir"]=[-light.Direction.X,-light.Direction.Y,-light.Direction.Z,0];screenValues["_LightColor"]=[light.Color.X,light.Color.Y,light.Color.Z,1];
            if(shadowed){resources["_ShadowMapTexture"]=shadows.Screen(index,false);foreach(var name in new[]{"_LightShadowData","unity_ShadowFadeCenterAndType"})screenValues[name]=shadows.Uniforms(index)[name];}
            pass.Draw(target,screenValues,resources,depthResources,camera,stencilDepth.ReadOnlyDepth);
        }
    }
    public void Dispose()
    {foreach(var pass in materials.Values)pass.Dispose();foreach(var pass in lights.Values)pass.Dispose();foreach(var pass in reflections.Values)pass.Dispose();foreach(var pass in reflectionComposites.Values)pass.Dispose();foreach(var buffer in buffers)buffer.Dispose();reflectionBuffer?.Dispose();stencilDepth?.Dispose();materials.Clear();lights.Clear();reflections.Clear();reflectionComposites.Clear();}
}
