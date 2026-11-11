using HS2Manager.Core;

namespace HS2Manager.Rendering;

/// <summary>The installed post stack's invalid-pixel copy pass, before history and spatial filtering.</summary>
public sealed class NativeInvalidPixelFilter : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly NativeFullscreenPass pass;
    private GpuTexture? temporary;
    public NativeInvalidPixelFilter(GraphicsDevice graphics,NativeGpuAssetStore assets)
    {
        this.graphics=graphics;
        pass=new(graphics,assets.Shader(assets.GameResource("Hidden/PostProcessing/Copy",48)),1);
    }
    public void Draw(GpuTexture target)
    {
        graphics.VerifyThread();
        if(temporary?.Width!=target.Width||temporary.Height!=target.Height||temporary.Texture.Description.Format!=target.Texture.Description.Format)
        {temporary?.Dispose();temporary=new(graphics,target.Width,target.Height,true,target.Texture.Description.Format,false);}
        pass.Draw(temporary,new Dictionary<string,float[]>{{"_RenderViewportScaleFactor",[1]}},new Dictionary<string,GpuTexture>{{"_MainTex",target}});
        graphics.Context.CopyResource(target.Texture,temporary.Texture);
    }
    public void Dispose(){temporary?.Dispose();pass.Dispose();}
}
