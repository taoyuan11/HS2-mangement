using System.Buffers.Binary;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

/// <summary>Small generated engine tables, owned by the graphics pipeline.</summary>
public sealed class NativeBuiltinTextures:IDisposable
{
    private readonly GraphicsDevice graphics;
    private ID3D11Texture3D? dither;
    private ID3D11Texture3D? originalDither;
    private ID3D11ShaderResourceView? ditherView;
    private ID3D11ShaderResourceView? originalDitherView;
    private ID3D11SamplerState? ditherSampler;
    private GpuTexture? roughness;
    public NativeBuiltinTextures(GraphicsDevice graphics)=>this.graphics=graphics;
    public static byte[] DitherMask()
    {
        // The first half adds distributed cells; the second half is its
        // complement in reverse order. Alpha slices straddle half coverage.
        int[] order=[0,10,8,2,5,15,13];var values=new byte[4*4*16];
        for(int slice=0;slice<8;slice++)
            for(int cell=0;cell<16;cell++)
            {
                byte value=(byte)(order.Take(slice).Contains(cell)?255:0);
                values[slice*16+cell]=value;values[(15-slice)*16+cell]=(byte)(255-value);
            }
        return values;
    }
    public bool TryView(string name,out (ID3D11ShaderResourceView View,ID3D11SamplerState Sampler) result,bool bottomOrigin=false)
    {
        graphics.VerifyThread();
        if(name=="unity_NHxRoughness")
        {
            roughness??=new(graphics,new("NHx roughness",256,64,1,1,2,9,false,1,0,0,1,1,1,RoughnessLookup()));
            result=(roughness.View,roughness.Sampler);return true;
        }
        if(name!="_DitherMaskLOD"){result=default;return false;}
        if(dither==null)
        {
            dither=graphics.Device.CreateTexture3D(new Texture3DDescription(Format.A8_UNorm,4,4,16,1,BindFlags.ShaderResource));
            var mask=DitherMask();
            // Casters use a positive-Y projection here. Match the original
            // negative-Y atlas's VPOS-based dither by reversing each volume row.
            for(int slice=0;slice<16;slice++)for(int y=0;y<2;y++)for(int x=0;x<4;x++)
                (mask[slice*16+y*4+x],mask[slice*16+(3-y)*4+x])=(mask[slice*16+(3-y)*4+x],mask[slice*16+y*4+x]);
            graphics.Context.UpdateSubresource(mask,dither,0,4,16);
            ditherView=graphics.Device.CreateShaderResourceView(dither);
            var description=SamplerDescription.PointWrap;description.AddressW=TextureAddressMode.Clamp;
            ditherSampler=graphics.Device.CreateSamplerState(description);
        }
        if(bottomOrigin&&originalDither==null)
        {
            originalDither=graphics.Device.CreateTexture3D(new Texture3DDescription(Format.A8_UNorm,4,4,16,1,BindFlags.ShaderResource));
            graphics.Context.UpdateSubresource(DitherMask(),originalDither,0,4,16);originalDitherView=graphics.Device.CreateShaderResourceView(originalDither);
        }
        result=(bottomOrigin?originalDitherView!:ditherView!,ditherSampler!);return true;
    }
    public static byte[] RoughnessLookup()
    {
        // Legacy normalized specular distribution, warped along R.L^4 and
        // perceptual roughness. Generate the numeric table independently; no
        // captured engine texture is shipped or read by the renderer.
        var bytes=new byte[256*64*2];float range=MathF.Pow(16,2.2f);
        for(int y=0;y<64;y++)
        {
            float roughness=y/63.5f;
            float logarithm=MathF.Log((1-roughness)*.968f+.03f)/MathF.Log(2);
            float power=10/logarithm;power=power*power*.25f;
            for(int x=0;x<256;x++)
            {
                float value=MathF.Pow(MathF.Pow(x/255f,.25f),power)*(power+2)/(2*MathF.PI)/range;
                value=Math.Clamp(value,0,1);
                value=value<=.0031308f?value*12.92f:value<1?1.055f*MathF.Pow(value,.4166666865348816f)-.055f:value;
                if(roughness<=.05f&&x==255)value*=roughness/.05f;
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((y*256+x)*2),checked((ushort)(value*65535+.5f)));
            }
        }
        return bytes;
    }
    public void Dispose(){roughness?.Dispose();roughness=null;ditherSampler?.Dispose();ditherView?.Dispose();dither?.Dispose();originalDitherView?.Dispose();originalDither?.Dispose();ditherSampler=null;ditherView=null;dither=null;originalDitherView=null;originalDither=null;}
}
