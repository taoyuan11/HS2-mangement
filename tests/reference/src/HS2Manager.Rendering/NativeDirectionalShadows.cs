using System.Numerics;
using HS2Manager.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

public sealed record NativeDirectionalLight(Vector3 Direction,Vector3 Color,uint Mask,float Intensity,
    float ShadowStrength=0,float ShadowBias=.05f,float NormalBias=.4f,float NearPlane=.2f,Quaternion? Rotation=null);
public sealed record NativeShadowCamera(Matrix4x4 View,Matrix4x4 Projection,Vector4 Bias,NativeDirectionalLight Light);

/// <summary>Session-owned cascaded directional shadow maps and the installed screen-space resolve.</summary>
public sealed class NativeDirectionalShadows : IDisposable
{
    private const int AtlasSize=4096,TileSize=AtlasSize/2;
    private readonly GraphicsDevice graphics;
    private readonly NativeFullscreenPass resolve;
    private readonly GpuCopyPass copy;
    private readonly float nearPlaneOffset;
    private readonly int renderTargetAnisotropy;
    private readonly List<LightMap> maps=[];
    private readonly List<Dictionary<string,float[]>> uniforms=[];
    private readonly float[] splits=[.0667f,.167f,.45f,1];
    public NativeDirectionalShadows(GraphicsDevice graphics,NativeGpuAssetStore store,float nearPlaneOffset=2,int anisotropicFiltering=2)
    {
        this.graphics=graphics;this.nearPlaneOffset=nearPlaneOffset;
        renderTargetAnisotropy=GpuTexture.EffectiveAnisotropy(1,anisotropicFiltering);
        resolve=new(graphics,store.Shader(store.GameResource("Hidden/Internal-ScreenSpaceShadows",48)),0,new HashSet<string>{"SHADOWS_SPLIT_SPHERES"},engineBlit:true,subShader:3);
        try{copy=new(graphics);}catch{resolve.Dispose();throw;}
    }
    public GpuTexture Screen(int light,bool materialCoordinates=true)=>materialCoordinates?maps[light].MaterialScreen!:maps[light].Screen!;
    internal (ID3D11ShaderResourceView View,ID3D11SamplerState Sampler) ScreenView(int light,bool materialCoordinates=true)
    {var screen=Screen(light,materialCoordinates);return(screen.View,maps[light].ScreenSampler);}
    public IReadOnlyDictionary<string,float[]> Uniforms(int light)=>uniforms[light];
    internal static IEnumerable<NativeShadowCamera> CullingCameras(NativeRenderCamera camera,int width,int height,IEnumerable<NativeDirectionalLight> lights,float nearPlaneOffset)
    {
        var forward=Vector3.Normalize(camera.Target-camera.Position);float distance=Math.Min(camera.Far,120);
        float tangent=MathF.Tan(camera.FieldOfView*(MathF.PI/180)*.5f),aspect=(float)width/height,k2=tangent*tangent*(1+aspect*aspect);
        float far=CascadeFar(camera,aspect,distance);
        foreach(var light in lights.Where(l=>l.ShadowStrength>0))
        {
            float near=camera.Near;
            foreach(float split in new[]{.0667f,.167f,.45f,1})
            {
                float end=camera.Near+(far-camera.Near)*split,centre=Math.Min(end,(near+end)*(1+k2)/2);
                float radius=MathF.Sqrt((end-centre)*(end-centre)+end*end*k2),extent=radius*(TileSize+10f)/TileSize;
                var centreWorld=camera.Position+forward*centre;
                var view=LightView(light,centreWorld);
                float texel=2*extent/TileSize;view.M41=MathF.Truncate(view.M41/texel)*texel;view.M42=MathF.Truncate(view.M42/texel)*texel;
                float minZ=-1-radius*1.1f,maxZ=-1+radius*1.1f+nearPlaneOffset*10,range=Math.Max(.01f,maxZ-minZ);
                var projection=Matrix4x4.CreateScale(1/extent,-1/extent,1/range);projection.M43=-minZ/range;
                yield return new(view,projection,Vector4.Zero,light);near=end;
            }
        }
    }
    public void Render(NativeRenderCamera camera,int width,int height,IReadOnlyList<NativeDirectionalLight> lights,GpuDepthSurface cameraDepth,
        Action<NativeShadowCamera> drawCasters)
    {
        graphics.VerifyThread();
        while(maps.Count<lights.Count){maps.Add(new(graphics,renderTargetAnisotropy));uniforms.Add(new());}
        var forward=Vector3.Normalize(camera.Target-camera.Position);float distance=Math.Min(camera.Far,120);
        float tangent=MathF.Tan(camera.FieldOfView*(MathF.PI/180)*.5f),aspect=(float)width/height;
        float k2=tangent*tangent*(1+aspect*aspect);
        // Stable-fit shadows use a spherical fade volume. Intersect its front
        // with the frustum corner ray before dividing the range into cascades.
        float fadeOffset=distance*FadeFraction(camera),fadeRadius=distance-fadeOffset;
        float far=CascadeFar(camera,aspect,distance);
        var cameraProjection=Matrix4x4.CreatePerspectiveFieldOfView(camera.FieldOfView*(MathF.PI/180),aspect,camera.Near,camera.Far);
        cameraProjection.M33=-(camera.Far+camera.Near)/(camera.Far-camera.Near);cameraProjection.M43=-2*camera.Far*camera.Near/(camera.Far-camera.Near);
        cameraProjection.M31=camera.ProjectionJitter.X;cameraProjection.M32=camera.ProjectionJitter.Y;
        var cameraInverseProjection=NativeSceneNode.InverseMatrix(cameraProjection);
        for(int lightIndex=0;lightIndex<lights.Count;lightIndex++)
        {
            var light=lights[lightIndex];var map=maps[lightIndex];map.Resize(width,height);var values=uniforms[lightIndex];values.Clear();
            var worldToShadow=new List<float>();var spheres=new List<Vector4>();float near=camera.Near;
            graphics.Context.ClearState();graphics.Context.ClearDepthStencilView(map.Depth,DepthStencilClearFlags.Depth,0,0);
            for(int cascade=0;cascade<4;cascade++)
            {
                float end=camera.Near+(far-camera.Near)*splits[cascade];
                // A rotation-invariant sphere encloses the frustum slice. Snapping in
                // light space keeps cascades stable during camera translation.
                float centre=Math.Min(end,(near+end)*(1+k2)/2);
                float radius=MathF.Sqrt((end-centre)*(end-centre)+end*end*k2);
                var centreWorld=camera.Position+forward*centre;spheres.Add(new(centreWorld,radius*radius));
                float extent=radius*(TileSize+10f)/TileSize;
                var lightView=LightView(light,centreWorld);
                float texel=2*extent/TileSize;
                lightView.M41=MathF.Truncate(lightView.M41/texel)*texel;lightView.M42=MathF.Truncate(lightView.M42/texel)*texel;
                // Keep the cascade receiver sphere plus the legacy caster guard
                // toward the light; visible mesh bounds must not shrink this range.
                float minZ=-1-radius*1.1f,maxZ=-1+radius*1.1f+nearPlaneOffset*10;
                float range=Math.Max(.01f,maxZ-minZ);
                var projection=Matrix4x4.CreateScale(1/extent,-1/extent,1/range);projection.M43=-minZ/range;
                var tile=new Matrix4x4(.25f,0,0,0,0,-.25f,0,0,0,0,1,0,.25f+(cascade%2)*.5f,.25f+(cascade/2)*.5f,0,1);
                worldToShadow.AddRange(NativeForwardRenderer.Matrix(lightView*projection*tile));
                graphics.Context.ClearState();graphics.Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(),map.Depth);
                graphics.Context.RSSetViewport((cascade%2)*TileSize,(cascade/2)*TileSize,TileSize,TileSize);
                drawCasters(new(lightView,projection,new(-light.ShadowBias/range,1,light.NormalBias*texel*3.65f,0),light));
                near=end;
            }
            for(int c=0;c<4;c++)values["unity_ShadowSplitSpheres"+c]=[spheres[c].X,spheres[c].Y,spheres[c].Z,spheres[c].W];
            values["unity_ShadowSplitSpheres"]=spheres.SelectMany(s=>new[]{s.X,s.Y,s.Z,s.W}).ToArray();
            values["unity_ShadowSplitSqRadii"]=spheres.Select(s=>s.W).ToArray();
            values["unity_WorldToShadow"]=worldToShadow.ToArray();values["_LightSplitsNear"]=[0,0,0,0];values["_LightSplitsFar"]=[0,0,0,0];
            values["_ShadowMapTexture_TexelSize"]=[1f/AtlasSize,1f/AtlasSize,AtlasSize,AtlasSize];
            // Reconstruct an upright camera ray from this renderer's top-origin
            // depth surface. The original offscreen pipeline stored it inverted.
            values["_ProjectionParams"]=[camera.BottomOrigin?-1:1,camera.Near,camera.Far,1/camera.Far];
            values["unity_CameraInvProjection"]=NativeForwardRenderer.Matrix(cameraInverseProjection);
            values["unity_CameraToWorld"]=NativeForwardRenderer.Matrix(camera.World);
            values["unity_OrthoParams"]=[0,0,0,0];values["_ZBufferParams"]=[camera.Far/camera.Near-1,1,(camera.Far/camera.Near-1)/camera.Far,1/camera.Far];
            values["_LightShadowData"]=[1-light.ShadowStrength,1/(distance*.001f),1/(distance*.2f),-(fadeRadius-distance*.2f)/(distance*.2f)];
            var fadeCentre=camera.Position+forward*fadeOffset;
            values["unity_ShadowFadeCenterAndType"]=[fadeCentre.X,fadeCentre.Y,fadeCentre.Z,1];
            resolve.Draw(map.Screen!,values,new Dictionary<string,GpuTexture>(),new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>
                {{"_CameraDepthTexture",(cameraDepth.View,cameraDepth.Sampler)},{"_ShadowMapTexture",(map.View,map.Sampler)}});
            copy.Copy(map.Screen!,map.MaterialScreen!,flipVertical:!camera.BottomOrigin);
        }
        graphics.Context.ClearState();
    }
    private static float FadeFraction(NativeRenderCamera camera)=>Math.Clamp(1-camera.FieldOfView/180,0,1)*.4f;
    internal static float CascadeFar(NativeRenderCamera camera,float aspect,float distance)
    {
        // Culling uses the unprojected camera frustum, including the rounding
        // of its far plane. Replacing it with an analytic ray changes the
        // cascade's texel grid and therefore stochastic hair shadow coverage.
        var projection=Matrix4x4.CreatePerspectiveFieldOfView(camera.FieldOfView*(MathF.PI/180),aspect,camera.Near,camera.Far);
        projection.M33=-(camera.Far+camera.Near)/(camera.Far-camera.Near);
        projection.M43=-2*camera.Far*camera.Near/(camera.Far-camera.Near);
        var inverse=NativeSceneNode.InverseMatrix(projection);
        float cornerW=((inverse.M14+inverse.M24)+inverse.M34)+inverse.M44,reciprocal=1/cornerW;
        var corner=new Vector3((((inverse.M11+inverse.M21)+inverse.M31)+inverse.M41)*reciprocal,
            (((inverse.M12+inverse.M22)+inverse.M32)+inverse.M42)*reciprocal,
            (((inverse.M13+inverse.M23)+inverse.M33)+inverse.M43)*reciprocal);
        float ratio=MathF.Sqrt((corner.X*corner.X+corner.Y*corner.Y)+corner.Z*corner.Z)/camera.Far,q=ratio*ratio;
        float fade=FadeFraction(camera),remaining=1-fade;
        float fraction=(MathF.Sqrt((q*remaining*remaining-q*fade*fade)+fade*fade)+fade)/q;
        float t=(fraction*distance-camera.Near)/(camera.Far-camera.Near);
        float nearW=((-inverse.M14-inverse.M24)-inverse.M34)+inverse.M44;
        float nearZ=(((-inverse.M13-inverse.M23)-inverse.M33)+inverse.M43)*(1/nearW);
        return -(corner.Z*t+nearZ*(1-t));
    }
    private static Matrix4x4 LightView(NativeDirectionalLight light,Vector3 centre)
    {
        // Construct the directional basis before translation. Subtracting a
        // large sphere centre from centre+direction loses rotation precision.
        var view=light.Rotation is {} rotation
            ?NativeSceneNode.InverseRigidMatrix(Vector3.Zero,rotation)*Matrix4x4.CreateScale(1,1,-1)
            :Matrix4x4.CreateLookAt(Vector3.Zero,-light.Direction,Math.Abs(Vector3.Dot(light.Direction,Vector3.UnitY))>.98f?Vector3.UnitZ:Vector3.UnitY)*Matrix4x4.CreateScale(-1,1,1);
        view.M41=-((view.M11*centre.X+view.M21*centre.Y)+view.M31*centre.Z);
        view.M42=-((view.M12*centre.X+view.M22*centre.Y)+view.M32*centre.Z);
        view.M43=-((view.M13*centre.X+view.M23*centre.Y)+view.M33*centre.Z)-1;
        return view;
    }
    public void Dispose(){foreach(var map in maps)map.Dispose();maps.Clear();copy.Dispose();resolve.Dispose();}
    private sealed class LightMap:IDisposable
    {
        private readonly GraphicsDevice graphics;
        private readonly int anisotropy;
        public ID3D11Texture2D Texture{get;}
        public ID3D11DepthStencilView Depth{get;}
        public ID3D11ShaderResourceView View{get;}
        public ID3D11SamplerState Sampler{get;}
        public ID3D11SamplerState ScreenSampler{get;}
        public GpuTexture? Screen{get;private set;}
        public GpuTexture? MaterialScreen{get;private set;}
        public LightMap(GraphicsDevice graphics,int anisotropy)
        {
            this.graphics=graphics;this.anisotropy=anisotropy;var owned=new List<IDisposable>();
            try
            {
                Texture=graphics.Device.CreateTexture2D(new Texture2DDescription(Format.R16_Typeless,AtlasSize,AtlasSize,1,1,BindFlags.ShaderResource|BindFlags.DepthStencil));owned.Add(Texture);
                Depth=graphics.Device.CreateDepthStencilView(Texture,new DepthStencilViewDescription{Format=Format.D16_UNorm,ViewDimension=DepthStencilViewDimension.Texture2D});owned.Add(Depth);
                View=graphics.Device.CreateShaderResourceView(Texture,new ShaderResourceViewDescription{Format=Format.R16_UNorm,ViewDimension=ShaderResourceViewDimension.Texture2D,Texture2D=new(){MipLevels=1}});owned.Add(View);
                var sampler=SamplerDescription.LinearClamp;sampler.Filter=Filter.ComparisonMinMagLinearMipPoint;sampler.ComparisonFunc=ComparisonFunction.GreaterEqual;
                Sampler=graphics.Device.CreateSamplerState(sampler);
                owned.Add(Sampler);
                var screenSampler=SamplerDescription.LinearClamp;screenSampler.Filter=Filter.MinMagLinearMipPoint;screenSampler.MinLOD=-float.MaxValue;screenSampler.MaxLOD=float.MaxValue;
                ScreenSampler=graphics.Device.CreateSamplerState(screenSampler);
            }
            catch{foreach(var item in owned.AsEnumerable().Reverse())item.Dispose();throw;}
        }
        public void Resize(int width,int height)
        {if(Screen?.Width==width&&Screen.Height==height)return;Screen?.Dispose();MaterialScreen?.Dispose();Screen=new(graphics,width,height,false,Format.R8_UNorm,false,anisotropy:anisotropy);MaterialScreen=new(graphics,width,height,false,Format.R8_UNorm,false,anisotropy:anisotropy);}
        public void Dispose(){MaterialScreen?.Dispose();Screen?.Dispose();ScreenSampler.Dispose();Sampler.Dispose();View.Dispose();Depth.Dispose();Texture.Dispose();}
    }
}
