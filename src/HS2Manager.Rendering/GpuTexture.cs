using HS2Manager.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

/// <summary>Uploads original texture levels; no decode/resize/recompression on BC formats.</summary>
public sealed class GpuTexture : IDisposable
{
    public ID3D11Texture2D Texture { get; }
    public ID3D11ShaderResourceView View { get; }
    public ID3D11SamplerState Sampler { get; }
    public ID3D11RenderTargetView? Target { get; }
    private readonly ID3D11ShaderResourceView? mipGenerationView;
    public int Width => (int)Texture.Description.Width;
    public int Height => (int)Texture.Description.Height;
    public GpuTexture(GraphicsDevice graphics, int width, int height, bool clamp = false, Format format = Format.B8G8R8A8_UNorm_SRgb, bool mipmaps = true,Filter? filter=null,int anisotropy=0)
    {
        graphics.VerifyThread();
        if (width is < 1 or > 16384 || height is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(width));
        var mipFormat=format switch
        {Format.B8G8R8A8_UNorm_SRgb=>Format.B8G8R8A8_UNorm,Format.R8G8B8A8_UNorm_SRgb=>Format.R8G8B8A8_UNorm,_=>format};
        var resourceFormat=mipmaps&&mipFormat!=format?format switch
        {Format.B8G8R8A8_UNorm_SRgb=>Format.B8G8R8A8_Typeless,Format.R8G8B8A8_UNorm_SRgb=>Format.R8G8B8A8_Typeless,_=>format}:format;
        Texture = graphics.Device.CreateTexture2D(new Texture2DDescription(resourceFormat,
            (uint)width, (uint)height, 1, mipmaps ? 0u : 1u, BindFlags.RenderTarget | BindFlags.ShaderResource,
            ResourceUsage.Default, CpuAccessFlags.None, 1, 0, mipmaps ? ResourceOptionFlags.GenerateMips : ResourceOptionFlags.None));
        ID3D11ShaderResourceView? view = null,mipView=null; ID3D11RenderTargetView? target = null;
        try
        {
            view = graphics.Device.CreateShaderResourceView(Texture,new ShaderResourceViewDescription
                {Format=format,ViewDimension=Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,Texture2D=new(){MipLevels=Texture.Description.MipLevels}});
            target = graphics.Device.CreateRenderTargetView(Texture,new RenderTargetViewDescription
                {Format=format,ViewDimension=RenderTargetViewDimension.Texture2D});
            if(resourceFormat!=format)mipView=graphics.Device.CreateShaderResourceView(Texture,new ShaderResourceViewDescription
                {Format=mipFormat,ViewDimension=Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,Texture2D=new(){MipLevels=Texture.Description.MipLevels}});
            var sampler = clamp ? SamplerDescription.LinearClamp : SamplerDescription.LinearWrap;
            sampler.Filter = filter??Filter.MinMagLinearMipPoint;
            if(anisotropy>1){sampler.Filter=Filter.Anisotropic;sampler.MaxAnisotropy=(uint)Math.Clamp(anisotropy,1,16);}
            Sampler = graphics.Device.CreateSamplerState(sampler);
            View = view; Target = target;mipGenerationView=mipView;
        }
        catch { mipView?.Dispose();target?.Dispose(); view?.Dispose(); Texture.Dispose(); throw; }
    }
    public GpuTexture(GraphicsDevice graphics, GpuTextureData source,int anisotropicFiltering=1)
    {
        graphics.VerifyThread();
        if (source.Dimension == 4 ? source.ImageCount != 6 || source.Width != source.Height : source.Dimension != 2 || source.ImageCount != 1)
            throw new NotSupportedException("Texture upload requires a 2D image or six square cubemap faces.");
        if (source.Format is 10 or 12 or >= 24 and <= 27 && (source.Width % 4 != 0 || source.Height % 4 != 0))
            throw new NotSupportedException("D3D11 compressed textures require top-level dimensions divisible by four; decode this texture before uploading.");
        var layout = TextureUpload.Layout(source);
        int mipCount=layout.Levels.Count/source.ImageCount;
        Texture = graphics.Device.CreateTexture2D(new Texture2DDescription(layout.Format, (uint)source.Width, (uint)source.Height,
            (uint)source.ImageCount, (uint)mipCount, BindFlags.ShaderResource,
            ResourceUsage.Default, CpuAccessFlags.None, 1, 0, source.Dimension == 4 ? ResourceOptionFlags.TextureCube : ResourceOptionFlags.None));
        ID3D11ShaderResourceView? view = null;
        try
        {
            foreach (var level in layout.Levels)
                graphics.Context.UpdateSubresource(level.Bytes, Texture, (uint)(level.Mip + level.Slice * mipCount), (uint)level.RowPitch, (uint)level.Bytes.Length);
            view = graphics.Device.CreateShaderResourceView(Texture);
            int anisotropy=EffectiveAnisotropy(source.Anisotropy,anisotropicFiltering);
            Sampler = graphics.Device.CreateSamplerState(new SamplerDescription
            {
                Filter = anisotropy > 1 ? Filter.Anisotropic : source.Filter switch
                { 0 => Filter.MinMagMipPoint, 1 => Filter.MinMagLinearMipPoint, _ => Filter.MinMagMipLinear },
                AddressU = Address(source.WrapU), AddressV = Address(source.WrapV), AddressW = Address(source.WrapW),
                MipLODBias = source.MipBias, MaxAnisotropy = (uint)Math.Clamp(anisotropy, 1, 16),
                ComparisonFunc = ComparisonFunction.Never, MinLOD = -float.MaxValue, MaxLOD = float.MaxValue
            });
            View = view;
        }
        catch { view?.Dispose(); Texture.Dispose(); throw; }
    }
    public static int EffectiveAnisotropy(int textureLevel,int qualityMode)=>qualityMode switch
    {0=>0,1=>Math.Clamp(textureLevel,0,16),2=>textureLevel>0?Math.Clamp(textureLevel,9,16):0,_=>throw new ArgumentOutOfRangeException(nameof(qualityMode))};
    private static TextureAddressMode Address(int mode) => mode switch
    { 0 => TextureAddressMode.Wrap, 1 => TextureAddressMode.Clamp, 2 => TextureAddressMode.Mirror, 3 => TextureAddressMode.MirrorOnce, _ => throw new NotSupportedException("Unknown texture address mode.") };
    internal void GenerateMips(GraphicsDevice graphics)
    {
        graphics.VerifyThread();
        // The reference generates composed-image levels from encoded colour
        // bytes. Sampling and rendering still use the sRGB views; only mip
        // reduction uses the corresponding UNorm view of the same resource.
        graphics.Context.GenerateMips(mipGenerationView??View);
    }
    public void Dispose() { Target?.Dispose(); Sampler.Dispose();mipGenerationView?.Dispose(); View.Dispose(); Texture.Dispose(); }
}

public static class TextureUpload
{
    public sealed record Level(int Mip, int Width, int Height, int RowPitch, byte[] Bytes, int Slice = 0);
    public sealed record Upload(Format Format, IReadOnlyList<Level> Levels);
    public static Upload Layout(GpuTextureData source)
    {
        if (source.Width is < 1 or > 16384 || source.Height is < 1 or > 16384 || source.MipCount < 0 ||
            source.ImageCount is not (1 or 6) || source.ImageCount == 6 && (source.Dimension != 4 || source.Width != source.Height) ||
            source.MipCount > 1 + (int)Math.Floor(Math.Log2(Math.Max(source.Width, source.Height))))
            throw new InvalidDataException($"Invalid texture level count or dimensions: {source.Name}, {source.Width}x{source.Height}, {source.MipCount} levels, {source.ImageCount} images, dimension {source.Dimension}.");
        (Format format, int unit, bool blocks) = source.Format switch
        {
            1 => (Format.A8_UNorm, 1, false), 3 or 4 => (Format.R8G8B8A8_UNorm, 4, false),
            5 or 14 => (Format.B8G8R8A8_UNorm, 4, false), 7 => (Format.B5G6R5_UNorm, 2, false),
            9 => (Format.R16_UNorm, 2, false), 10 => (Format.BC1_UNorm, 8, true), 12 => (Format.BC3_UNorm, 16, true),
            15 => (Format.R16_Float, 2, false), 16 => (Format.R16G16_Float, 4, false), 17 => (Format.R16G16B16A16_Float, 8, false),
            18 => (Format.R32_Float, 4, false), 19 => (Format.R32G32_Float, 8, false), 20 => (Format.R32G32B32A32_Float, 16, false),
            22 => ((Format)67, 4, false), 24 => (Format.BC6H_Uf16, 16, true),
            25 => (Format.BC7_UNorm, 16, true), 26 => (Format.BC4_UNorm, 8, true), 27 => (Format.BC5_UNorm, 16, true),
            _ => throw new NotSupportedException($"Texture format {source.Format} requires a lossless native decoder before upload.")
        };
        int mipCount=source.MipCount;
        if(mipCount==0)
        {
            // Legacy MODs use zero as an implicit level count. Recover only a
            // uniquely sized contiguous chain; never truncate unexplained bytes.
            int length=0,max=1+(int)Math.Floor(Math.Log2(Math.Max(source.Width,source.Height)));
            for(int mip=0,w=source.Width,h=source.Height;mip<max;mip++,w=Math.Max(1,w/2),h=Math.Max(1,h/2))
            {
                length=checked(length+(blocks?Math.Max(1,(w+3)/4)*Math.Max(1,(h+3)/4)*unit:w*h*(source.Format==3?3:unit))*source.ImageCount);
                if(TrailingMatches(source with{MipCount=mip+1},length,blocks)){mipCount=mip+1;break;}
            }
            if(mipCount==0)throw new InvalidDataException($"Implicit mip count does not match the texture payload: {source.Name}.");
        }
        if (source.Srgb) format = format switch
        {
            Format.R8G8B8A8_UNorm => Format.R8G8B8A8_UNorm_SRgb, Format.B8G8R8A8_UNorm => Format.B8G8R8A8_UNorm_SRgb,
            Format.BC1_UNorm => Format.BC1_UNorm_SRgb, Format.BC3_UNorm => Format.BC3_UNorm_SRgb,
            Format.BC7_UNorm => Format.BC7_UNorm_SRgb, _ => format
        };
        var levels = new List<Level>(); int offset = 0;
        for (int slice = 0; slice < source.ImageCount; slice++)
        for (int mip = 0, width = source.Width, height = source.Height; mip < mipCount; mip++, width = Math.Max(1, width / 2), height = Math.Max(1, height / 2))
        {
            int pitch = checked((blocks ? Math.Max(1, (width + 3) / 4) : width) * unit);
            int length = checked(pitch * (blocks ? Math.Max(1, (height + 3) / 4) : height));
            int inputLength = source.Format == 3 ? checked(width * height * 3) : length;
            if ((long)offset + inputLength > source.Bytes.Length) throw new InvalidDataException("Texture mip data is truncated.");
            var bytes = new byte[length];
            if (source.Format == 3)
                for (int pixel = 0; pixel < width * height; pixel++)
                { source.Bytes.AsSpan(offset + pixel * 3, 3).CopyTo(bytes.AsSpan(pixel * 4)); bytes[pixel * 4 + 3] = 255; }
            else if (source.Format == 5)
                for (int pixel = 0; pixel < width * height; pixel++)
                {
                    int p = offset + pixel * 4, q = pixel * 4;
                    bytes[q] = source.Bytes[p + 3]; bytes[q + 1] = source.Bytes[p + 2]; bytes[q + 2] = source.Bytes[p + 1]; bytes[q + 3] = source.Bytes[p];
                }
            else source.Bytes.AsSpan(offset, length).CopyTo(bytes);
            levels.Add(new(mip, width, height, pitch, bytes, slice)); offset += inputLength;
        }
        // Some 2018 MOD writers pad the complete BC image payload to 32 bytes.
        // Padding is not another mip; reject nonzero trailing data or any other size.
        if (!TrailingMatches(source with{MipCount=mipCount},offset,blocks)) throw new InvalidDataException($"Texture payload contains unaccounted image data: {source.Name} {source.Width}x{source.Height}, format {source.Format}, {mipCount} levels, consumed {offset} of {source.Bytes.Length} bytes.");
        return new(format, levels);
    }
    private static bool TrailingMatches(GpuTextureData source,int offset,bool blocks)
        =>offset==source.Bytes.Length||offset<source.Bytes.Length&&(blocks&&(source.Bytes.Length==((offset+31)&~31)||source.Bytes.Length-offset==32)&&source.Bytes.AsSpan(offset).IndexOfAnyExcept((byte)0)<0||HasTgaFooter(source,offset));
    private static bool HasTgaFooter(GpuTextureData source,int offset)
    {
        // A few MOD writers copied the TGA 2.0 footer into image data and then
        // swizzled the entire buffer as pixels. Recognize only that exact footer,
        // including zero extension/developer offsets; arbitrary extra bytes fail.
        if(source.ImageCount!=1||source.MipCount!=1||source.Format is not (3 or 5)||source.Bytes.Length-offset!=26)return false;
        Span<byte> footer=stackalloc byte[26];footer.Clear();"TRUEVISION-XFILE.\0"u8.CopyTo(footer[8..]);
        int stride=source.Format==3?3:4;
        for(int i=0;i+stride<=footer.Length;i+=stride)footer.Slice(i,stride).Reverse();
        return source.Bytes.AsSpan(offset).SequenceEqual(footer);
    }
}
