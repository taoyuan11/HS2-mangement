using Vortice.Direct3D11;
using Vortice.Direct3D;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

/// <summary>Sampleable camera depth, shared by original material, shadow and postprocessing passes.</summary>
public sealed class GpuDepthSurface : IDisposable
{
    public ID3D11Texture2D Texture {get;}
    public ID3D11DepthStencilView Depth {get;}
    public ID3D11DepthStencilView ReadOnlyDepth {get;}
    public ID3D11ShaderResourceView View {get;}
    public ID3D11SamplerState Sampler {get;}
    public int Width=>(int)Texture.Description.Width;
    public int Height=>(int)Texture.Description.Height;
    public GpuDepthSurface(GraphicsDevice graphics,int width,int height)
    {
        graphics.VerifyThread();var owned=new List<IDisposable>();
        try
        {
            Texture=graphics.Device.CreateTexture2D(new Texture2DDescription(Format.R32G8X24_Typeless,(uint)width,(uint)height,1,1,BindFlags.DepthStencil|BindFlags.ShaderResource));owned.Add(Texture);
            Depth=graphics.Device.CreateDepthStencilView(Texture,new DepthStencilViewDescription{Format=Format.D32_Float_S8X24_UInt,ViewDimension=DepthStencilViewDimension.Texture2D});owned.Add(Depth);
            ReadOnlyDepth=graphics.Device.CreateDepthStencilView(Texture,new DepthStencilViewDescription{Format=Format.D32_Float_S8X24_UInt,ViewDimension=DepthStencilViewDimension.Texture2D,Flags=DepthStencilViewFlags.ReadOnlyDepth|DepthStencilViewFlags.ReadOnlyStencil});owned.Add(ReadOnlyDepth);
            View=graphics.Device.CreateShaderResourceView(Texture,new ShaderResourceViewDescription{Format=Format.R32_Float_X8X24_Typeless,ViewDimension=ShaderResourceViewDimension.Texture2D,Texture2D=new(){MipLevels=1}});owned.Add(View);
            var sampler=SamplerDescription.PointClamp;Sampler=graphics.Device.CreateSamplerState(sampler);
        }
        catch{foreach(var resource in owned.AsEnumerable().Reverse())resource.Dispose();throw;}
    }
    public void Dispose(){Sampler.Dispose();View.Dispose();ReadOnlyDepth.Dispose();Depth.Dispose();Texture.Dispose();}
}

/// <summary>A typed compute image, including interleaved array slices used by the original AO kernels.</summary>
public sealed class GpuComputeImage : IDisposable
{
    public ID3D11Texture2D Texture {get;}
    public ID3D11ShaderResourceView View {get;}
    public ID3D11UnorderedAccessView Output {get;}
    public ID3D11SamplerState Sampler {get;}
    public int Width=>(int)Texture.Description.Width;
    public int Height=>(int)Texture.Description.Height;
    public int Slices=>(int)Texture.Description.ArraySize;
    public GpuComputeImage(GraphicsDevice graphics,int width,int height,Format format,int slices=1)
    {
        graphics.VerifyThread();var owned=new List<IDisposable>();
        try
        {
            Texture=graphics.Device.CreateTexture2D(new Texture2DDescription(format,(uint)width,(uint)height,(uint)slices,1,BindFlags.UnorderedAccess|BindFlags.ShaderResource));owned.Add(Texture);
            View=graphics.Device.CreateShaderResourceView(Texture);owned.Add(View);
            Output=graphics.Device.CreateUnorderedAccessView(Texture);owned.Add(Output);
            Sampler=graphics.Device.CreateSamplerState(SamplerDescription.PointClamp);
        }
        catch{foreach(var resource in owned.AsEnumerable().Reverse())resource.Dispose();throw;}
    }
    public void Dispose(){Sampler.Dispose();Output.Dispose();View.Dispose();Texture.Dispose();}
}
