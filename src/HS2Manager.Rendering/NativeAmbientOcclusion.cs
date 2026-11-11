using HS2Manager.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

/// <summary>Dispatches the installed multiscale obscurance kernels with their original typed resources.</summary>
public sealed class NativeAmbientOcclusion : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly NativePostSettings settings;
    private readonly NativeComputePass down1,down2,obscurance,upsample,combine;
    private readonly NativeFullscreenPass composite;
    private readonly GpuCopyPass copy;
    private GpuTexture? bottomDepth,uprightOcclusion;
    private readonly List<IDisposable> owned=[];
    private readonly List<GpuComputeImage> images=[];
    private GpuComputeImage? linear,output;
    private readonly GpuComputeImage[] low=new GpuComputeImage[4],atlas=new GpuComputeImage[4],ao=new GpuComputeImage[4],merged=new GpuComputeImage[3];
    public GpuComputeImage? Occlusion=>output;
    public NativeAmbientOcclusion(GraphicsDevice graphics,NativeGpuAssetStore store,NativePostSettings settings)
    {
        this.graphics=graphics;this.settings=settings;
        if(settings.Scalar("AmbientOcclusion","mode")!=1)throw new NotSupportedException("The selected scalable AO mode needs its own original shader path.");
        try
        {
            NativeComputePass Kernel(string asset,string name)
            {var p=store.GameResource(asset,72);return Own(new NativeComputePass(graphics,store.Reader(p.Bundle).Compute(p.PathId).Kernels.Single(k=>k.Name==name)));}
            down1=Kernel("MultiScaleVODownsample1","MultiScaleVODownsample1");down2=Kernel("MultiScaleVODownsample2","MultiScaleVODownsample2");
            obscurance=Kernel("MultiScaleVORender","MultiScaleVORender_interleaved");
            upsample=Kernel("MultiScaleVOUpsample","MultiScaleVOUpSample");combine=Kernel("MultiScaleVOUpsample","MultiScaleVOUpSample_blendout");
            composite=Own(new NativeFullscreenPass(graphics,store.Shader(store.GameResource("Hidden/PostProcessing/MultiScaleVO",48)),2));
            copy=Own(new GpuCopyPass(graphics));
        }
        catch{Dispose();throw;}
    }
    private T Own<T>(T value)where T:IDisposable{owned.Add(value);return value;}
    private GpuComputeImage Image(int w,int h,Format format,int slices=1){var t=new GpuComputeImage(graphics,w,h,format,slices);images.Add(t);return t;}
    private void Resize(int width,int height)
    {
        if(linear?.Width==width&&linear.Height==height)return;
        foreach(var image in images)image.Dispose();images.Clear();
        bottomDepth?.Dispose();uprightOcclusion?.Dispose();
        bottomDepth=new(graphics,width,height,true,Format.R32_Float,false);
        uprightOcclusion=new(graphics,width,height,true,Format.R8_UNorm,false);
        linear=Image(width,height,Format.R16_Float);output=Image(width,height,Format.R8_UNorm);
        for(int i=0;i<4;i++)
        {
            int divisor=2<<i,w=(width+divisor-1)/divisor,h=(height+divisor-1)/divisor;
            low[i]=Image(w,h,Format.R32_Float);ao[i]=Image(w,h,Format.R8_UNorm);
            atlas[i]=Image((w+3)/4,(h+3)/4,Format.R16_Float,16);
            if(i<3)merged[i]=Image(w,h,Format.R8_UNorm);
        }
    }
    private static (ID3D11ShaderResourceView,ID3D11SamplerState) View(GpuComputeImage image)=>(image.View,image.Sampler);
    public void Draw(GpuDepthSurface depth,GpuTexture target,NativeRenderCamera camera)
    {
        graphics.VerifyThread();Resize(target.Width,target.Height);
        // The interleaved downsample offsets and bilateral reconstruction are
        // directional. Run the original kernels in their bottom-origin space.
        copy.CopyView(depth.View,bottomDepth!,flipVertical:!camera.BottomOrigin,point:true);
        var empty=new Dictionary<string,float[]>();
        down1.Dispatch((uint)atlas[1].Width*8,(uint)atlas[1].Height*8,1,
            new Dictionary<string,float[]>{{"ZBufferParams",[camera.Far/camera.Near-1,1,0,0]}},
            new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>{{"Depth",(bottomDepth!.View,depth.Sampler)}},
            new Dictionary<string,ID3D11UnorderedAccessView>{{"LinearZ",linear!.Output},{"DS2x",low[0].Output},{"DS2xAtlas",atlas[0].Output},{"DS4x",low[1].Output},{"DS4xAtlas",atlas[1].Output}});
        down2.Dispatch((uint)atlas[3].Width*8,(uint)atlas[3].Height*8,1,empty,
            new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>{{"DS4x",View(low[1])}},
            new Dictionary<string,ID3D11UnorderedAccessView>{{"DS8x",low[2].Output},{"DS8xAtlas",atlas[2].Output},{"DS16x",low[3].Output},{"DS16xAtlas",atlas[3].Output}});
        // Project a radius-five sampling sphere into each interleaved slice. The twelve
        // symmetric offsets and their multiplicities define the installed kernel ABI.
        (int X,int Y)[] offsets=[(1,0),(2,0),(3,0),(4,0),(1,1),(1,2),(1,3),(1,4),(2,2),(2,3),(2,4),(3,3)];
        var thickness=offsets.Select(p=>MathF.Sqrt(1-(p.X*p.X+p.Y*p.Y)/25f)).ToArray();
        var weights=offsets.Select((p,i)=>i is 0 or 2 or 5 or 7 or 9?0:thickness[i]*(p.Y==0||p.X==p.Y?4:8)).ToArray();
        float sum=weights.Sum();for(int i=0;i<weights.Length;i++)weights[i]/=sum;
        float tanHalf=1/camera.Projection((float)target.Width/target.Height).M11;
        for(int i=0;i<4;i++)
        {
            var image=atlas[i];float diameter=20*tanHalf/image.Width;
            obscurance.Dispatch((uint)image.Width,(uint)image.Height,16,new Dictionary<string,float[]>
            {
                ["gInvThicknessTable"]=thickness.Select(t=>1/(diameter*t)).ToArray(),["gSampleWeightTable"]=weights,
                ["gInvSliceDimension"]=[1f/image.Width,1f/image.Height,0,0],
                ["AdditionalParams"]=[-1/settings.Scalar("AmbientOcclusion","thicknessModifier",1),settings.Scalar("AmbientOcclusion","intensity")]
            },new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>{{"DepthTex",View(image)}},new Dictionary<string,ID3D11UnorderedAccessView>{{"Occlusion",ao[i].Output}});
        }
        var current=ao[3];
        for(int i=3;i>=0;i--)
        {
            var high=i==0?linear!:low[i-1];var destination=i==0?output!:merged[i-1];
            float step=1920f/low[i].Width,tolerance=1-MathF.Pow(10,settings.Scalar("AmbientOcclusion","blurTolerance",-4.6f))*step;
            float up=MathF.Pow(10,settings.Scalar("AmbientOcclusion","upsampleTolerance",-12));
            var values=new Dictionary<string,float[]>
            {
                ["InvLowResolution"]=[1f/low[i].Width,1f/low[i].Height,0,0],["InvHighResolution"]=[1f/high.Width,1f/high.Height,0,0],
                ["AdditionalParams"]=[1/(MathF.Pow(10,settings.Scalar("AmbientOcclusion","noiseFilterTolerance"))+up),step,tolerance*tolerance,up]
            };
            var textures=new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>{{"LoResDB",View(low[i])},{"HiResDB",View(high)},{"LoResAO1",View(current)}};
            if(i>0)textures["HiResAO"]=View(ao[i-1]);
            // One 8x8 workgroup reconstructs a 16x16 destination tile, plus a border.
            (i==0?upsample:combine).Dispatch((uint)((high.Width+17)/16*8),(uint)((high.Height+17)/16*8),1,values,textures,new Dictionary<string,ID3D11UnorderedAccessView>{{"AoResult",destination.Output}});
            current=destination;
        }
        var color=settings.Value("AmbientOcclusion","color",0,0,0,1);
        copy.CopyView(output!.View,uprightOcclusion!,flipVertical:!camera.BottomOrigin,point:true);
        composite.Draw(target,new Dictionary<string,float[]>{{"_RenderViewportScaleFactor",[1]},{"_AOColor",color.Select(c=>1-c).ToArray()}},
            new Dictionary<string,GpuTexture>(),new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>{{"_MSVOcclusionTexture",(uprightOcclusion!.View,uprightOcclusion.Sampler)}});
    }
    public void Dispose(){bottomDepth?.Dispose();uprightOcclusion?.Dispose();foreach(var image in images)image.Dispose();images.Clear();for(int i=owned.Count-1;i>=0;i--)owned[i].Dispose();owned.Clear();}
}
