using HS2Manager.Core;
using HS2Manager.Rendering;
using Vortice.DXGI;

internal static class InvalidPixelChecks
{
    public static void Run(GraphicsDevice graphics,NativeGpuAssetStore assets)
    {
        using var texture=new GpuTexture(graphics,4,3,true,Format.R32G32B32A32_Float,false);
        float[] source=Enumerable.Range(0,48).Select(i=>i%4==3?1f:(i+1)*.125f).ToArray();
        source[4]=float.NaN;source[19]=float.NaN;
        graphics.Context.UpdateSubresource(source,texture.Texture,0,64,192);
        using var filter=new NativeInvalidPixelFilter(graphics,assets);filter.Draw(texture);
        var result=graphics.ReadbackFloat(texture.Texture);
        for(int p=0;p<12;p++)for(int c=0;c<4;c++)
        {
            float expected=p is 1 or 4?0:source[p*4+c];
            if(result[p*4+c]!=expected)throw new Exception("Installed NaN filter changed a finite HDR sample or retained an invalid pixel.");
        }
        Console.WriteLine("PASS original invalid-pixel filter preserves HDR values and clears invalid RGBA pixels");
    }
}
