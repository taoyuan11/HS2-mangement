using System.Numerics;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace HS2Manager.Rendering;

public sealed record NativeRenderCamera(Vector3 Position, Vector3 Target, float FieldOfView, float Near, float Far, Vector3? Up = null)
{
    public Vector2 ProjectionJitter {get;init;}
    public bool BottomOrigin {get;init;}
    // Serialized scenes use left-handed world coordinates; camera space looks along -Z.
    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Up??Vector3.UnitY) * Matrix4x4.CreateScale(-1,1,1);
    public Matrix4x4 Projection(float aspect,bool jittered=true)
    {
        var projection=Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView*(MathF.PI/180),aspect,Near,Far);
        // Match the source camera's OpenGL projection followed by D3D's reversed
        // depth conversion, including its intermediate single-precision rounding.
        projection.M33=-(-(Far+Near)/(Far-Near)+1)*.5f;projection.M43=Far*Near/(Far-Near);
        if(jittered){projection.M31+=ProjectionJitter.X;projection.M32+=ProjectionJitter.Y;}
        if(BottomOrigin){projection.M22=-projection.M22;projection.M32=-projection.M32;}
        return projection;
    }
    public Matrix4x4 World
    {
        get { Matrix4x4.Invert(View * Matrix4x4.CreateScale(1,1,-1),out var world); return world; }
    }
}

/// <summary>Original forward material passes with explicit environment inputs. The caller owns the scene and appearance.</summary>
public sealed class NativeForwardRenderer : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly NativeCharacterScene scene;
    private readonly GpuCharacterAppearance appearance;
    private readonly GpuMaterialComposer composer;
    private readonly GpuTexture reflection;
    private readonly NativeSkybox skybox;
    private readonly NativeBuiltinTextures builtin;
    private readonly Dictionary<(GpuMaterialInstance,NativeShaderPass,bool,bool),NativeMaterialPass> passes = [];
    private readonly Dictionary<(GpuMaterialInstance,bool,bool),NativeMaterialPass> depthPasses=[];
    private readonly NativeDirectionalShadows shadows;
    private readonly NativeDeferredRenderer deferred;
    private readonly NativeGpuMeshDeformer deformer;
    private readonly NativeDepthNormals depthNormals;
    private GpuTexture? lastColorTarget;
    private readonly Dictionary<string,GpuTexture> grabs = new(StringComparer.Ordinal);
    private readonly List<IDisposable> owned = [];
    private readonly ID3D11Texture3D emptyProbe;
    private readonly ID3D11ShaderResourceView emptyProbeView;
    private readonly ID3D11SamplerState probeSampler;
    private NativeDirectionalLight? previousForwardLight;
    public bool UseGameBackground {get;set;}=true;
    public Vector4? Background {get;set;}
    public float KeyLightMultiplier {get;set;}=1;
    public Action<string,GpuTexture>? DrawTrace {get;set;}
    public Action<string,IReadOnlyDictionary<string,float[]>>? BindingTrace {get;set;}
    private GpuDepthSurface? mainDepth;
    public GpuDepthSurface? CameraDepth {get;private set;}
    public IReadOnlyList<GpuTexture> GBuffer=>deferred.Buffers;
    public GpuTexture Reflections=>deferred.ReflectionBuffer;
    public Action<NativeRenderCamera,GpuDepthSurface,GpuDepthSurface,IReadOnlyDictionary<NativeSceneRenderer,NativeGpuVertexStream>>? AfterGeometry {get;set;}
    public Action<GpuDepthSurface,GpuTexture,NativeRenderCamera>? AfterOpaque {get;set;}
    public NativeForwardRenderer(GraphicsDevice graphics,NativeCharacterScene scene,GpuCharacterAppearance appearance,GpuMaterialComposer composer,NativeGpuAssetStore store)
    {
        this.graphics=graphics;this.scene=scene;this.appearance=appearance;this.composer=composer;
        try
        {
            var selection=scene.Definition.Selections.Single(s=>s.Key=="environment.reflection").Reference;
            reflection=composer.Texture(store.Reader(selection.Bundle).Find(selection.Asset,89));
            skybox=Own(new NativeSkybox(graphics,composer,scene.Definition.Environment));
            builtin=Own(new NativeBuiltinTextures(graphics));
            shadows=Own(new NativeDirectionalShadows(graphics,store,scene.Definition.Environment.Quality.Float("shadowNearPlaneOffset",2),
                scene.Definition.Environment.DhhEnabled?2:(int)scene.Definition.Environment.Quality.Float("anisotropicTextures",2)));
            emptyProbe=Own(graphics.Device.CreateTexture3D(new Texture3DDescription(Format.R16G16B16A16_Float,1,1,1,1,BindFlags.ShaderResource)));
            graphics.Context.UpdateSubresource(new byte[8],emptyProbe,0,8,8);
            emptyProbeView=Own(graphics.Device.CreateShaderResourceView(emptyProbe));
            probeSampler=Own(graphics.Device.CreateSamplerState(SamplerDescription.LinearClamp));
            deferred=Own(new NativeDeferredRenderer(graphics,store,composer,appearance,reflection,(emptyProbeView,probeSampler)));
            deformer=Own(new NativeGpuMeshDeformer(graphics));
            depthNormals=Own(new NativeDepthNormals(graphics,store,composer));
        }
        catch {Dispose();throw;}
    }
    private T Own<T>(T value) where T:IDisposable {owned.Add(value);return value;}

    public void Draw(GpuTexture target,NativeRenderCamera camera,ReadOnlySpan<float> ambientProbe,float time=0)
    {
        graphics.VerifyThread();
        if(ambientProbe.Length!=27)throw new ArgumentException("An evaluated 27-coefficient ambient probe is required.");
        var globals=Globals(camera,target.Width,target.Height,ambientProbe,time);
        var lights=Lights(camera).OrderByDescending(l=>l.Color.LengthSquared()).ToArray();
        previousForwardLight??=lights.LastOrDefault()??new NativeDirectionalLight(Vector3.UnitY,Vector3.Zero,uint.MaxValue,0);
        globals["_WorldSpaceLightPos0"]=[previousForwardLight.Direction.X,previousForwardLight.Direction.Y,previousForwardLight.Direction.Z,0];
        globals["_LightColor0"]=[previousForwardLight.Color.X,previousForwardLight.Color.Y,previousForwardLight.Color.Z,previousForwardLight.Intensity];
        globals["unity_LightColor"]=[previousForwardLight.Color.X,previousForwardLight.Color.Y,previousForwardLight.Color.Z,1];
        if(mainDepth==null||mainDepth.Width!=target.Width||mainDepth.Height!=target.Height)
        {
            mainDepth?.Dispose();CameraDepth?.Dispose();mainDepth=new(graphics,target.Width,target.Height);
            try{CameraDepth=new(graphics,target.Width,target.Height);}
            catch{mainDepth.Dispose();mainDepth=null;CameraDepth=null;throw;}
        }
        var depthView=mainDepth.Depth;
        var context=graphics.Context;
        context.ClearState();context.OMSetRenderTargets(target.Target!,depthView);context.RSSetViewport(0,0,target.Width,target.Height);
        context.ClearDepthStencilView(depthView,DepthStencilClearFlags.Depth|DepthStencilClearFlags.Stencil,0,0);
        context.ClearDepthStencilView(CameraDepth!.Depth,DepthStencilClearFlags.Depth|DepthStencilClearFlags.Stencil,0,0);
        var background=UseGameBackground||Background==null?scene.Definition.Environment.Background:[Background.Value.X,Background.Value.Y,Background.Value.Z,Background.Value.W];
        if(!UseGameBackground)context.ClearRenderTargetView(target.Target!,new Color4(GpuMaterialComposer.Linear(background[0]),GpuMaterialComposer.Linear(background[1]),GpuMaterialComposer.Linear(background[2]),1));
        else if(!ReferenceEquals(lastColorTarget,target))context.ClearRenderTargetView(target.Target!,new Color4(0,0,0,0));
        lastColorTarget=target;
        context.OMSetRenderTargets(target.Target!,depthView);context.RSSetViewport(0,0,target.Width,target.Height);
        var rendererCenters=appearance.Materials.Where(p=>p.Key.Visible&&p.Key.HasGeometry).ToDictionary(p=>p.Key,p=>p.Key.Bounds.Center);
        var unsorted=appearance.Materials.Where(p=>p.Key.Visible&&p.Key.HasGeometry).SelectMany(p=>p.Value.Select((material,index)=>(Renderer:p.Key,Material:material,Submesh:index)))
            .Where(p=>p.Material!=null).ToArray();
        // Opaque queues batch matching programs before depth sorting. Material
        // instances are submitted newest first within those batches, including
        // shaders that blend while retaining an opaque custom queue.
        var opaque=unsorted.Reverse().Where(p=>p.Material!.Queue<=2500).OrderBy(p=>p.Material!.Queue)
            .ThenBy(p=>p.Material!.Shader.SourceBundle,StringComparer.OrdinalIgnoreCase).ThenBy(p=>p.Material!.Shader.PathId)
            .GroupBy(p=>(p.Material!.Queue,p.Material.Shader.SourceBundle,p.Material.Shader.Name,p.Material.Shader.PathId,
                Keywords:string.Join(" ",p.Material.Keywords.Order(StringComparer.Ordinal)))).SelectMany(group=>group);
        var transparent=unsorted.Reverse().Where(p=>p.Material!.Queue>2500).OrderBy(p=>p.Material!.Queue)
            .ThenBy(p=>p.Material!.Queue>2500?p.Renderer.Source.Settings.Float("m_SortingOrder"):0)
            .ThenBy(p=>-Vector3.DistanceSquared(camera.Position,rendererCenters[p.Renderer]))
            // Bundle loading establishes distinct program instances even when
            // copies share the same shader name and serialized path id. Keep
            // that program order at coincident transparent renderer centres.
            .ThenBy(p=>p.Material!.Shader.SourceBundle,StringComparer.OrdinalIgnoreCase)
            .ThenBy(p=>p.Material!.Shader.PathId)
            .ThenBy(p=>p.Submesh);
        var draws=opaque.Concat(transparent).ToArray();
        var deformed=new Dictionary<NativeSceneRenderer,NativeGpuVertexStream>();
        foreach(var renderer in draws.Select(d=>d.Renderer).Distinct())
        {
            deformed.Add(renderer,deformer.Deform(renderer));
        }
        // Camera culling uses renderer bounds before shader vertex expansion.
        // Keep off-camera meshes available for shadow casting.
        var cameraVP=camera.View*camera.Projection((float)target.Width/target.Height,false);
        var cameraDeformed=deformed.Where(p=>p.Key.Bounds.Intersects(cameraVP)).ToDictionary(p=>p.Key,p=>p.Value);
        deferred.Draw(target,camera,mainDepth,CameraDepth,globals,lights,shadows,cameraDeformed,sharedDepth=>
        {
        // Deferred GBuffer geometry executes first. Forward-only surfaces then
        // populate depth, clear the deferred stencil and write their original
        // zero-colour shadow pass into GBuffer0. Its alpha prevents SSR from
        // reusing an occluded material's reflection on forward-only surfaces.
        // Pre-filling depth before the GBuffer suppresses additive deferred
        // contributions that the reference has already accumulated underneath.
        context.ClearState();context.OMSetRenderTargets(deferred.Buffers[0].Target!,sharedDepth.Depth);
        context.RSSetViewport(0,0,target.Width,target.Height);
        foreach(var draw in draws.Where(d=>cameraDeformed.ContainsKey(d.Renderer)&&d.Material!.Queue<=2500&&d.Renderer.ShadowCasting!=3&&!NativeDeferredRenderer.Supports(d.Material!)))
            DrawDepth(draw.Renderer,draw.Material!,draw.Submesh,globals,null,false);
        context.ClearState();context.CopyResource(CameraDepth.Texture,sharedDepth.Texture);
        shadows.Render(camera,target.Width,target.Height,lights,CameraDepth,shadowCamera=>
        {
            var shadowGlobals=new Dictionary<string,float[]>(globals)
            {
                ["unity_MatrixVP"]=Matrix(shadowCamera.View*shadowCamera.Projection),["unity_MatrixV"]=Matrix(shadowCamera.View),
                ["_ProjectionParams"]=[-1,camera.Near,camera.Far,1/camera.Far],
                ["unity_LightShadowBias"]=[shadowCamera.Bias.X,shadowCamera.Bias.Y,shadowCamera.Bias.Z,shadowCamera.Bias.W],
                ["_WorldSpaceLightPos0"]=[shadowCamera.Light.Direction.X,shadowCamera.Light.Direction.Y,shadowCamera.Light.Direction.Z,0]
            };
            foreach(var draw in draws.Where(d=>d.Renderer.ShadowCasting!=0&&(shadowCamera.Light.Mask&(1u<<d.Renderer.Node.Layer))!=0))
                DrawDepth(draw.Renderer,draw.Material!,draw.Submesh,shadowGlobals,shadowCamera.Light,true);
        });
        });
        context.OMSetRenderTargets(target.Target!,depthView);context.RSSetViewport(0,0,target.Width,target.Height);
        var captured=new HashSet<string>(StringComparer.Ordinal);
        bool opaqueComplete=false;
        // Forward queues submit their base surfaces before additional
        // lights. Interleaving every renderer's add passes changes coverage at
        // coincident cutout surfaces and overwrites contributions from neighbours.
        var scheduled=draws.Where(d=>cameraDeformed.ContainsKey(d.Renderer)&&d.Renderer.ShadowCasting!=3&&!NativeDeferredRenderer.Supports(d.Material!))
            .GroupBy(d=>(d.Material!.Queue,Sorting:d.Material.Queue>2500?d.Renderer.Source.Settings.Float("m_SortingOrder"):0,
                Distance:d.Material.Queue>2500?Vector3.DistanceSquared(camera.Position,rendererCenters[d.Renderer]):0,
                Surface:d.Material.Queue>2500&&appearance.Materials[d.Renderer].Length>1?d.Renderer:null,
                Slot:d.Material.Queue>2500&&appearance.Materials[d.Renderer].Length>1?d.Submesh:-1))
            .SelectMany(ScheduleGroup);
        static IEnumerable<((NativeSceneRenderer Renderer,GpuMaterialInstance? Material,int Submesh) Draw,bool Add,int PassStart,int PassEnd)> ScheduleGroup(
            IEnumerable<(NativeSceneRenderer Renderer,GpuMaterialInstance? Material,int Submesh)> group)
        {
            var pending=new List<(NativeSceneRenderer Renderer,GpuMaterialInstance? Material,int Submesh)>();
            foreach(var draw in group)
            {
                var material=draw.Material!;
                var materialPasses=material.Shader.Passes.Where(p=>p.SubShader==0).ToArray();
                var baseIndices=materialPasses.Select((pass,index)=>(pass,index))
                    .Where(p=>Tag(p.pass,"LightMode").Equals("ForwardBase",StringComparison.OrdinalIgnoreCase)).Select(p=>p.index).ToArray();
                // A multipass surface finishes each base/add pair before its
                // next base pass. Grouping every base ahead of every add pass
                // changes alpha blending even for a single renderer.
                if(baseIndices.Length>1)
                {
                    foreach(var item in pending)yield return(item,false,0,int.MaxValue);
                    foreach(var item in pending)yield return(item,true,0,int.MaxValue);
                    pending.Clear();
                    for(int i=0;i<baseIndices.Length;i++)
                    {
                        int start=i==0?0:baseIndices[i],end=i+1<baseIndices.Length?baseIndices[i+1]:materialPasses.Length;
                        yield return(draw,false,start,end);yield return(draw,true,start,end);
                    }
                    continue;
                }
                bool untagged=material.Shader.Passes.Any(p=>p.SubShader==0&&(int)p.Metadata.Get("m_Type").Number==0&&Tag(p,"LightMode").Length==0);
                if(!untagged){pending.Add(draw);continue;}
                // Equal-distance surfaces share the base/add batch. A change
                // in transparent distance starts another scheduled group.
                // Untagged programs consume the preceding lit batch's globals.
                foreach(var item in pending)yield return(item,false,0,int.MaxValue);
                foreach(var item in pending)yield return(item,true,0,int.MaxValue);
                pending.Clear();yield return(draw,false,0,int.MaxValue);yield return(draw,true,0,int.MaxValue);
            }
            foreach(var item in pending)yield return(item,false,0,int.MaxValue);
            foreach(var item in pending)yield return(item,true,0,int.MaxValue);
        }
        foreach(var item in scheduled)
        {
            var draw=item.Draw;
            var renderer=draw.Renderer;var material=draw.Material!;
            if(NativeDeferredRenderer.Supports(material))continue;
            if(!opaqueComplete&&material.Queue>2500)FinishOpaque();
            if(!deformed.TryGetValue(renderer,out var vertices)){vertices=deformer.Deform(renderer);deformed.Add(renderer,vertices);}
            var values=MaterialValues(renderer,material,globals);
            if(item.Add)
            {
                // Additional lights receive no ambient probe. Some custom
                // forward-add programs still evaluate SH without LIGHTPROBE_SH;
                // the reference clears these coefficients for that pass.
                foreach(string name in new[]{"unity_SHAr","unity_SHAg","unity_SHAb","unity_SHBr","unity_SHBg","unity_SHBb"})
                    values[name]=[0,0,0,0];
                values["unity_SHC"]=[0,0,0,1];
            }
            var eligible=lights.Where(l=>(l.Mask&(1u<<renderer.Node.Layer))!=0).ToArray();
            var main=eligible.FirstOrDefault()??new NativeDirectionalLight(Vector3.UnitY,Vector3.Zero,uint.MaxValue,0);
            foreach(var pass in material.Shader.Passes.Where(p=>p.SubShader==0).Skip(item.PassStart).Take(item.PassEnd==int.MaxValue?int.MaxValue:item.PassEnd-item.PassStart))
            {
                int type=(int)pass.Metadata.Get("m_Type").Number;
                if(type==2)
                {
                    if(item.Add)continue;
                    string name=pass.Metadata.String("m_TextureName");bool unnamed=string.IsNullOrEmpty(name);if(unnamed)name="_GrabTexture";
                    if(unnamed||captured.Add(name))
                    {
                        if(grabs.TryGetValue(name,out var previous)&&(previous.Width!=target.Width||previous.Height!=target.Height||previous.Texture.Description.Format!=target.Texture.Description.Format))
                        {previous.Dispose();grabs.Remove(name);}
                        if(!grabs.TryGetValue(name,out var grab)){grab=new(graphics,target.Width,target.Height,true,target.Texture.Description.Format,false);grabs.Add(name,grab);}
                        // GrabScreenPos already applies the D3D texture-origin
                        // transform independently of ProjectionParams.
                        context.ClearState();context.CopyResource(grab.Texture,target.Texture);
                        context.OMSetRenderTargets(target.Target!,depthView);context.RSSetViewport(0,0,target.Width,target.Height);
                    }
                    continue;
                }
                if(type!=0)throw new NotSupportedException("Unresolved material UsePass: "+material.Shader.Name);
                string mode=Tag(pass,"LightMode");
                bool additive=mode.Equals("ForwardAdd",StringComparison.OrdinalIgnoreCase);
                if(!additive&&!mode.Equals("ForwardBase",StringComparison.OrdinalIgnoreCase)&&mode.Length!=0)continue;
                if(item.Add!=additive)continue;
                foreach(var light in additive?eligible.Skip(1):[main])
                {
                    // Untagged passes do not perform light setup. Custom unlit
                    // programs can still read the previous lit pass's globals.
                    if(mode.Length!=0)previousForwardLight=light;
                    var passLight=previousForwardLight;
                    values["_WorldSpaceLightPos0"]=[passLight.Direction.X,passLight.Direction.Y,passLight.Direction.Z,0];
                    values["_LightColor0"]=[passLight.Color.X,passLight.Color.Y,passLight.Color.Z,passLight.Intensity];
                    values["unity_LightColor"]=[passLight.Color.X,passLight.Color.Y,passLight.Color.Z,1];
                    // Screen-space shadow masks describe the opaque depth buffer.
                    // Transparent queues use the unshadowed forward variants, as
                    // sampling that opaque receiver mask shadows unrelated strands.
                    bool shadowed=mode.Length!=0&&material.Queue<=2500&&renderer.ReceiveShadows&&light.ShadowStrength>0;int lightIndex=Array.IndexOf(lights,light);
                    if(shadowed)
                    {
                        values["_LightShadowData"]=shadows.Uniforms(lightIndex)["_LightShadowData"];
                        values["unity_ShadowFadeCenterAndType"]=shadows.Uniforms(lightIndex)["unity_ShadowFadeCenterAndType"];
                    }
                    var keywords=material.Keywords.ToHashSet(StringComparer.Ordinal);
                    keywords.UnionWith(["DIRECTIONAL","LIGHTMAP_OFF","DIRLIGHTMAP_OFF","DYNAMICLIGHTMAP_OFF"]);
                    if(!additive)keywords.Add("LIGHTPROBE_SH");
                    if(shadowed)keywords.Add("SHADOWS_SCREEN");
                    if(!passes.TryGetValue((material,pass,additive,shadowed),out var nativePass))
                    {
                        nativePass=new(graphics,material.Shader,pass,keywords,material.Values.Where(p=>p.Value.Length==1).ToDictionary(p=>p.Key,p=>p.Value[0]),reversedDepth:true);
                        passes.Add((material,pass,additive,shadowed),nativePass);
                    }
                    var resources=new Dictionary<string,GpuTexture>(StringComparer.Ordinal);
                    var views=new Dictionary<string,(ID3D11ShaderResourceView View, ID3D11SamplerState Sampler)>(StringComparer.Ordinal);
                    foreach(var binding in nativePass.TextureBindings)
                    {
                        if(builtin.TryView(binding.Name,out var builtinView,camera.BottomOrigin)){views.Add(binding.Name,builtinView);continue;}
                        if(binding.Name=="unity_ProbeVolumeSH"){views.Add(binding.Name,(emptyProbeView,probeSampler));continue;}
                        if(binding.Name=="_ShadowMapTexture"&&shadowed)
                        {
                            // LPPV-era programs reserve the preceding sampler for
                            // the probe volume; their screen shadow uses the
                            // built-in linear clamp sampler observed at that slot.
                            if(nativePass.TextureBindings.Any(t=>t.Name=="unity_ProbeVolumeSH"))views.Add(binding.Name,shadows.ScreenView(lightIndex));
                            else resources.Add(binding.Name,shadows.Screen(lightIndex));
                            continue;
                        }
                        if(!material.DefaultTextures.ContainsKey(binding.Name)&&!material.Textures.ContainsKey(binding.Name)&&!material.TextureOverrides.ContainsKey(binding.Name)&&composer.TryGlobalTexture(binding.Name,out var globalTexture))
                        {resources.Add(binding.Name,globalTexture);values[binding.Name+"_TexelSize"]=[1f/globalTexture.Width,1f/globalTexture.Height,globalTexture.Width,globalTexture.Height];continue;}
                        if(binding.Name is not ("unity_SpecCube0" or "unity_SpecCube1")&&!grabs.ContainsKey(binding.Name)&&
                            !material.DefaultTextures.ContainsKey(binding.Name)&&!material.Textures.ContainsKey(binding.Name)&&!material.TextureOverrides.ContainsKey(binding.Name))
                            throw new NotSupportedException("Unbound frame resource: "+material.Shader.Name+"/"+binding.Name);
                        var texture=binding.Name is "unity_SpecCube0" or "unity_SpecCube1"?reflection:grabs.GetValueOrDefault(binding.Name)??composer.ResolveTexture(material,binding.Name);
                        resources.Add(binding.Name,texture);
                        values[binding.Name+"_TexelSize"]=[1f/texture.Width,1f/texture.Height,texture.Width,texture.Height];
                    }
                    BindingTrace?.Invoke(renderer.Node.Name+"/"+mode,values);
                    try{nativePass.Draw(renderer.Mesh,Math.Min(draw.Submesh,renderer.Mesh.Submeshes.Length-1),values,resources,resourceViews:views,gpuDeformation:vertices,mirroredTransform:GeometryMirrored(renderer));}
                    catch(Exception error){throw new InvalidDataException($"Draw failed: {renderer.Node.Name}/{material.Shader.Name}/{mode}",error);}
                    DrawTrace?.Invoke(renderer.Node.Name+"/"+mode,target);
                }
            }
        }
        if(!opaqueComplete)FinishOpaque();
        context.ClearState();
        void FinishOpaque()
        {
            opaqueComplete=true;context.ClearState();
            depthNormals.Draw(target.Width,target.Height,mainDepth,globals,appearance.Materials,cameraDeformed);
            if(UseGameBackground)skybox.Draw(target,camera,depthView);
            AfterGeometry?.Invoke(camera,mainDepth,CameraDepth,cameraDeformed);AfterOpaque?.Invoke(CameraDepth,target,camera);
            context.OMSetRenderTargets(target.Target!,depthView);context.RSSetViewport(0,0,target.Width,target.Height);
        }
        void DrawDepth(NativeSceneRenderer renderer,GpuMaterialInstance material,int submesh,IReadOnlyDictionary<string,float[]> frameValues,NativeDirectionalLight? light,bool casting)
        {
            var pass=material.Shader.Passes.FirstOrDefault(p=>p.SubShader==0&&Tag(p,"LightMode").Equals("ShadowCaster",StringComparison.OrdinalIgnoreCase));
            if(pass==null)return;
            bool twoSided=casting&&renderer.ShadowCasting==2;
            var keywords=material.Keywords.ToHashSet(StringComparer.Ordinal);keywords.UnionWith(["SHADOWS_DEPTH","UNITY_PASS_SHADOWCASTER","LIGHTMAP_OFF","DIRLIGHTMAP_OFF","DYNAMICLIGHTMAP_OFF"]);
            if(!depthPasses.TryGetValue((material,twoSided,casting),out var nativePass))
            {
                var forwardDepth=new DepthStencilDescription
                {
                    DepthEnable=true,DepthWriteMask=DepthWriteMask.All,DepthFunc=ComparisonFunction.GreaterEqual,
                    StencilEnable=true,StencilReadMask=255,StencilWriteMask=255,
                    FrontFace=new(){StencilFunc=ComparisonFunction.Always,StencilPassOp=StencilOperation.Replace,StencilFailOp=StencilOperation.Keep,StencilDepthFailOp=StencilOperation.Keep},
                    BackFace=new(){StencilFunc=ComparisonFunction.Always,StencilPassOp=StencilOperation.Replace,StencilFailOp=StencilOperation.Keep,StencilDepthFailOp=StencilOperation.Keep}
                };
                nativePass=new(graphics,material.Shader,pass,keywords,material.Values.Where(p=>p.Value.Length==1).ToDictionary(p=>p.Key,p=>p.Value[0]),reversedDepth:true,culling:twoSided?CullMode.None:null,disableDepthOffset:!casting,
                    depthOverride:casting?null:forwardDepth,stencilOverride:casting?null:0);
                depthPasses.Add((material,twoSided,casting),nativePass);
            }
            var values=MaterialValues(renderer,material,frameValues);if(!casting){values["unity_LightShadowBias"]=[0,0,0,0];values["_WorldSpaceLightPos0"]=[0,1,0,0];}
            var textures=new Dictionary<string,GpuTexture>();
            var views=new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>();
            foreach(var binding in nativePass.TextureBindings)
                {if(builtin.TryView(binding.Name,out var builtinView,casting||camera.BottomOrigin)){views.Add(binding.Name,builtinView);continue;}var texture=composer.ResolveTexture(material,binding.Name);textures[binding.Name]=texture;values[binding.Name+"_TexelSize"]=[1f/texture.Width,1f/texture.Height,texture.Width,texture.Height];}
            nativePass.Draw(renderer.Mesh,Math.Min(submesh,renderer.Mesh.Submeshes.Length-1),values,textures,resourceViews:views,gpuDeformation:deformed[renderer],mirroredTransform:GeometryMirrored(renderer));
        }
    }
    internal static Dictionary<string,float[]> MaterialValues(NativeSceneRenderer renderer,GpuMaterialInstance material,IReadOnlyDictionary<string,float[]> globals)
    {
        var values=new Dictionary<string,float[]>(globals,StringComparer.Ordinal);
        foreach(var pair in material.Values)values[pair.Key]=(float[])pair.Value.Clone();
        foreach(var color in material.ColorProperties.Union(material.GammaProperties))if(values.TryGetValue(color,out var channels))for(int c=0;c<Math.Min(3,channels.Length);c++)channels[c]=GpuMaterialComposer.Linear(channels[c]);
        foreach(string property in material.DefaultTextures.Keys.Concat(material.Textures.Keys).Distinct(StringComparer.Ordinal))
        {var t=material.Textures.GetValueOrDefault(property);values[property+"_ST"]=t==null?[1,1,0,0]:[t.Scale.X,t.Scale.Y,t.Offset.X,t.Offset.Y];}
        var world=renderer.RenderWorldMatrix;
        if(!Matrix4x4.Invert(world,out var inverse))throw new InvalidDataException("Renderer world matrix is singular.");
        values["unity_ObjectToWorld"]=Matrix(world);values["unity_WorldToObject"]=Matrix(inverse);
        values["unity_WorldTransformParams"]=[0,0,0,world.GetDeterminant()<0?-1:1];return values;
    }
    internal static bool GeometryMirrored(NativeSceneRenderer renderer)=>renderer.Node.WorldMatrix.GetDeterminant()<0;
    public static string Tag(NativeShaderPass pass,string key)=>pass.Metadata.Get("m_State").Get("m_Tags").Get("tags").Children
        .FirstOrDefault(t=>t.String("first").Equals(key,StringComparison.OrdinalIgnoreCase))?.String("second")??"";
    private IEnumerable<NativeDirectionalLight> Lights(NativeRenderCamera camera)=>DirectionalLights(scene.Definition.Environment,camera,KeyLightMultiplier);
    internal static IEnumerable<NativeDirectionalLight> DirectionalLights(EnvironmentDefinition environment,NativeRenderCamera camera,float keyLightMultiplier=1)
    {
        var cameraNode=new NativeSceneNode("camera-light-frame",""){LocalRotation=NativeSceneNode.LookRotation(camera.Target-camera.Position,camera.Up??Vector3.UnitY)};
        // ForwardBase uses the brightest eligible directional light; others use additive passes.
        foreach(var light in environment.Lights)
        {
            if((int)light.Parameters.Float("m_Type")!=1)throw new NotSupportedException("Native forward local-light attenuation is not implemented.");
            var r=light.Rotation;var lightNode=new NativeSceneNode("directional-light",""){LocalRotation=new Quaternion(r[0],r[1],r[2],r[3])};
            if(light.CameraRelative)lightNode.SetParent(cameraNode);
            var direction=lightNode.TransformDirection(-Vector3.UnitZ);
            var color=light.Parameters.Get("m_Color");float intensity=light.Parameters.Float("m_Intensity",1);
            if(environment.DhhEnabled&&light.Name.Contains("Back",StringComparison.Ordinal))intensity=environment.DhhFloats[34];
            if(light.Name.Contains("Key",StringComparison.Ordinal))intensity*=keyLightMultiplier;
            // The frozen preview project has m_LightsUseLinearIntensity: 0. Apply intensity
            // in gamma space before conversion, preserving its legacy lighting convention.
            var shadow=light.Parameters.Get("m_Shadows");float strength=shadow.Float("m_Type")==0?0:shadow.Float("m_Strength",1);
            if(environment.DhhEnabled&&light.Name.Contains("Key",StringComparison.Ordinal))strength=environment.DhhFloats[36];
            yield return new(direction,new Vector3(GpuMaterialComposer.Linear(color.Float("r")*intensity),GpuMaterialComposer.Linear(color.Float("g")*intensity),GpuMaterialComposer.Linear(color.Float("b")*intensity)),
                unchecked((uint)light.Parameters.Get("m_CullingMask").Get("m_Bits").Number),intensity,strength,shadow.Float("m_Bias"),shadow.Float("m_NormalBias"),shadow.Float("m_NearPlane",.2f),lightNode.Rotation);
        }
        if(environment.DhhEnabled&&environment.DhhBools[8])
        {
            var colors=environment.DhhColors;
            var extra=new NativeSceneNode("additional-directional-light",""){LocalRotation=NativeCharacterScene.Euler(new(colors[6],colors[7],0))};extra.SetParent(cameraNode);
            yield return new(extra.TransformDirection(-Vector3.UnitZ),
                new Vector3(GpuMaterialComposer.Linear(colors[9]/255*environment.DhhFloats[38]),GpuMaterialComposer.Linear(colors[10]/255*environment.DhhFloats[38]),GpuMaterialComposer.Linear(colors[11]/255*environment.DhhFloats[38])),uint.MaxValue,environment.DhhFloats[38],environment.DhhFloats[39],Rotation:extra.Rotation);
        }
    }
    private Dictionary<string,float[]> Globals(NativeRenderCamera camera,int width,int height,ReadOnlySpan<float> sh,float time)
    {
        var projection=camera.Projection((float)width/height);var view=camera.View;
        Matrix4x4.Invert(view,out var inverseView);Matrix4x4.Invert(projection,out var inverseProjection);
        float reflectionIntensity=scene.Definition.Environment.DhhEnabled?scene.Definition.Environment.DhhFloats[33]:scene.Definition.Environment.RenderSettings.Float("m_ReflectionIntensity",1);
        reflectionIntensity=GpuMaterialComposer.Linear(reflectionIntensity);
        float ambientIntensity=scene.Definition.Environment.DhhEnabled?scene.Definition.Environment.DhhFloats[32]:scene.Definition.Environment.RenderSettings.Float("m_AmbientIntensity",1);
        float ambientScale=GpuMaterialComposer.Linear(ambientIntensity);
        var ambient=scene.Definition.Environment.RenderSettings.Get("m_AmbientSkyColor");
        var result=new Dictionary<string,float[]>(StringComparer.Ordinal)
        {
            ["unity_MatrixV"]=Matrix(view),["unity_MatrixInvV"]=Matrix(inverseView),["unity_MatrixVP"]=Matrix(view*projection),
            ["glstate_matrix_projection"]=Matrix(projection),["unity_CameraProjection"]=Matrix(projection),["unity_CameraInvProjection"]=Matrix(inverseProjection),
            ["_WorldSpaceCameraPos"]=[camera.Position.X,camera.Position.Y,camera.Position.Z],
            // Materials retain bottom-origin ComputeScreenPos coordinates. This is
            // also observable in their alpha-dither patterns, so screen resources
            // are exposed in that orientation instead of changing the shader ABI.
            ["_ProjectionParams"]=[camera.BottomOrigin?-1:1,camera.Near,camera.Far,1/camera.Far],
            ["_ScreenParams"]=[width,height,1+1f/width,1+1f/height],
            ["_ZBufferParams"]=[camera.Far/camera.Near-1,1,(camera.Far/camera.Near-1)/camera.Far,1/camera.Far],
            ["_Time"]=[time/20,time,time*2,time*3],["_SinTime"]=[MathF.Sin(time/8),MathF.Sin(time/4),MathF.Sin(time/2),MathF.Sin(time)],
            ["_CosTime"]=[MathF.Cos(time/8),MathF.Cos(time/4),MathF.Cos(time/2),MathF.Cos(time)],
            ["unity_DeltaTime"]=[1f/30,30,1f/30,30],["unity_OrthoParams"]=[0,0,0,0],
            // Legacy lighting macros double this stored value. The original
            // GPU buffer retains half the linear ambient sky color, including alpha.
            ["glstate_lightmodel_ambient"]=[GpuMaterialComposer.Linear(ambient.Float("r"))*.5f,GpuMaterialComposer.Linear(ambient.Float("g"))*.5f,GpuMaterialComposer.Linear(ambient.Float("b"))*.5f,ambient.Float("a",1)*.5f],
            // Alloy declares this as a global with no property initializer.
            // The frozen standalone runtime leaves its constant at zero.
            ["_TransShadowWeight"]=[0],
            ["unity_OcclusionMaskSelector"]=[1,0,0,0],["unity_ProbesOcclusion"]=[1,1,1,1],
            ["unity_ProbeVolumeParams"]=[0,1,1,0],["unity_ProbeVolumeSizeInv"]=[1,1,1],["unity_ProbeVolumeMin"]=[0,0,0],
            ["unity_ProbeVolumeWorldToObject"]=Matrix(Matrix4x4.Identity),["unity_LightmapST"]=[1,1,0,0],["unity_DynamicLightmapST"]=[1,1,0,0],
            ["unity_LightShadowBias"]=[0,0,0,0],["_LightShadowData"]=[1,0,0,0],
            ["unity_SpecCube0_HDR"]=[reflectionIntensity,1,0,0],["unity_SpecCube1_HDR"]=[reflectionIntensity,1,0,0],
            ["unity_SpecCube0_BoxMin"]=[-float.MaxValue,-float.MaxValue,-float.MaxValue,0],["unity_SpecCube0_BoxMax"]=[float.MaxValue,float.MaxValue,float.MaxValue,1],["unity_SpecCube0_ProbePosition"]=[0,0,0,0],
            ["unity_SpecCube1_BoxMin"]=[-float.MaxValue,-float.MaxValue,-float.MaxValue,1],["unity_SpecCube1_BoxMax"]=[float.MaxValue,float.MaxValue,float.MaxValue,1],["unity_SpecCube1_ProbePosition"]=[0,0,0,0]
        };
        for(int color=0;color<3;color++)
        {
            var c=sh.Slice(color*9,9);string suffix=new[]{"r","g","b"}[color];
            result["unity_SHA"+suffix]=[c[3],c[1],c[2],c[0]-c[6]];
            result["unity_SHB"+suffix]=[c[4],c[5],c[6]*3,c[7]];
        }
        result["unity_SHC"]=[sh[8],sh[17],sh[26],1];
        foreach(string name in new[]{"unity_SHAr","unity_SHAg","unity_SHAb","unity_SHBr","unity_SHBg","unity_SHBb","unity_SHC"})
            for(int c=0;c<(name=="unity_SHC"?3:4);c++)result[name][c]*=ambientScale;
        return result;
    }
    public static float[] Matrix(Matrix4x4 m)=>[m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
    public void Dispose(){mainDepth?.Dispose();mainDepth=null;CameraDepth?.Dispose();CameraDepth=null;foreach(var grab in grabs.Values)grab.Dispose();grabs.Clear();foreach(var pass in passes.Values)pass.Dispose();passes.Clear();foreach(var pass in depthPasses.Values)pass.Dispose();depthPasses.Clear();for(int i=owned.Count-1;i>=0;i--)owned[i].Dispose();owned.Clear();}
}
