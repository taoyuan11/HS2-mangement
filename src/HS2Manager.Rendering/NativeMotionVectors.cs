using System.Numerics;
using HS2Manager.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

/// <summary>Camera and skinned-object velocity using the installed motion-vector programs.</summary>
public sealed class NativeMotionVectors : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly GpuCharacterAppearance appearance;
    private readonly NativeFullscreenPass cameraPass;
    private readonly NativeMaterialPass geometryPass;
    private readonly Dictionary<NativeSceneRenderer,(ID3D11Buffer Buffer,Matrix4x4 Matrix)> previous=[];
    private Matrix4x4 previousVP;
    private bool initialized;
    private GpuTexture? target;
    public GpuTexture Texture=>target??throw new InvalidOperationException("No motion-vector frame has been drawn.");
    public NativeMotionVectors(GraphicsDevice graphics,NativeGpuAssetStore store,GpuCharacterAppearance appearance)
    {
        this.graphics=graphics;this.appearance=appearance;
        var shader=store.Shader(store.GameResource("Hidden/Internal-MotionVectors",48));
        cameraPass=new(graphics,shader,1,engineBlit:true);
        try{geometryPass=new(graphics,shader,shader.Passes[0],new HashSet<string>(),new Dictionary<string,float>(),reversedDepth:true);}
        catch{cameraPass.Dispose();throw;}
    }
    public void ResetHistory(){initialized=false;previous.Clear();}
    public void Draw(NativeRenderCamera camera,GpuDepthSurface depth,GpuDepthSurface cameraDepth,IReadOnlyDictionary<NativeSceneRenderer,NativeGpuVertexStream> deformed)
    {
        graphics.VerifyThread();
        if(target?.Width!=depth.Width||target.Height!=depth.Height)
        {target?.Dispose();target=new(graphics,depth.Width,depth.Height,true,Format.R16G16_Float,false);ResetHistory();}
        float aspect=(float)depth.Width/depth.Height;
        var vp=camera.View*camera.Projection(aspect,false);
        if(!initialized)previousVP=vp;
        var values=new Dictionary<string,float[]>
        {
            ["_ProjectionParams"]=[camera.BottomOrigin?-1:1,camera.Near,camera.Far,1/camera.Far],
            ["_ZBufferParams"]=[camera.Far/camera.Near-1,1,(camera.Far/camera.Near-1)/camera.Far,1/camera.Far],
            ["unity_CameraToWorld"]=NativeForwardRenderer.Matrix(camera.World),
            ["_NonJitteredVP"]=NativeForwardRenderer.Matrix(vp),["_PreviousVP"]=NativeForwardRenderer.Matrix(previousVP)
        };
        // Camera reconstruction reads the depth texture, while object velocity
        // tests against the completed opaque depth attachment. Forward cutout
        // passes can update the latter after the depth texture was produced.
        cameraPass.Draw(target,values,new Dictionary<string,GpuTexture>(),new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>{{"_CameraDepthTexture",(cameraDepth.View,cameraDepth.Sampler)}},camera,depth.ReadOnlyDepth);
        var context=graphics.Context;
        context.ClearState();context.OMSetRenderTargets(target.Target!,depth.Depth);context.RSSetViewport(0,0,depth.Width,depth.Height);
        values["unity_MatrixVP"]=NativeForwardRenderer.Matrix(camera.View*camera.Projection(aspect));
        values["_MotionVectorDepthBias"]=[-.001f];
        // Velocity replacement passes retain each material's queue and the
        // front-to-back renderer order. A renderer can have submeshes in
        // different queues; submitting all of them together lets a static
        // backing surface erase a nearer moving submesh's velocity.
        var draws=appearance.Materials.Where(pair=>deformed.ContainsKey(pair.Key))
            .SelectMany(pair=>pair.Value.Select((material,submesh)=>(pair.Key,Value:deformed[pair.Key],Material:material,Submesh:submesh)))
            .Where(draw=>draw.Material is {Queue:<=2500})
            .OrderBy(draw=>draw.Material!.Queue).ThenBy(draw=>Vector3.DistanceSquared(camera.Position,draw.Key.Bounds.Center));
        foreach(var draw in draws)
        {
            var renderer=draw.Key;if(!renderer.Visible||renderer.ShadowCasting==3)continue;
            int mode=(int)renderer.Source.Settings.Float("m_MotionVectors",1);
            if(mode==0)continue;
            if(mode is <0 or >2)throw new InvalidDataException("Unknown renderer motion-vector mode.");
            bool hasPrevious=initialized&&previous.TryGetValue(renderer,out _);
            var last=hasPrevious?previous[renderer]:(draw.Value.Buffer,renderer.RenderWorldMatrix);
            bool skinned=hasPrevious&&renderer.Source.Skinned&&renderer.Source.Settings.Float("m_SkinnedMotionVectors",1)!=0;
            values["_HasLastPositionData"]=[skinned?1:0];values["_ForceNoMotion"]=[hasPrevious&&mode!=2?0:1];
            values["unity_ObjectToWorld"]=NativeForwardRenderer.Matrix(renderer.RenderWorldMatrix);
            values["_PreviousM"]=NativeForwardRenderer.Matrix(last.Item2);
            geometryPass.Draw(renderer.Mesh,Math.Min(draw.Submesh,renderer.Mesh.Submeshes.Length-1),values,new Dictionary<string,GpuTexture>(),gpuDeformation:draw.Value,previousGpuBuffer:skinned?last.Item1:null,mirroredTransform:NativeForwardRenderer.GeometryMirrored(renderer));
        }
        // Hidden renderers retain their previous submitted geometry. Clearing
        // them loses camera velocity when they become visible after a view change.
        context.ClearState();
        foreach(var pair in deformed)previous[pair.Key]=(pair.Value.Buffer,pair.Key.RenderWorldMatrix);
        previousVP=vp;initialized=true;
    }
    public void Dispose(){previous.Clear();target?.Dispose();geometryPass.Dispose();cameraPass.Dispose();}
}
