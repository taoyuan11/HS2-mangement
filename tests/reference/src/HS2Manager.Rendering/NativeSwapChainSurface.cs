using System.Numerics;
using System.Runtime.InteropServices;
using HS2Manager.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

/// <summary>A COM reference that remains valid while the UI binds the composition surface.</summary>
public sealed class NativeSwapChainLease:IDisposable
{
    private nint pointer;
    public nint Pointer=>pointer!=0?pointer:throw new ObjectDisposedException(nameof(NativeSwapChainLease));
    internal NativeSwapChainLease(nint pointer){Marshal.AddRef(pointer);this.pointer=pointer;}
    public void Dispose(){nint value=Interlocked.Exchange(ref pointer,0);if(value!=0)Marshal.Release(value);}
}

internal sealed class NativeSwapChainSurface:IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly IDXGISwapChain2 swapChain;
    private ID3D11Texture2D? buffer;
    private PreviewViewportSize? size;
    internal NativeSwapChainSurface(GraphicsDevice graphics,PreviewViewportSize size)
    {
        this.graphics=graphics;graphics.VerifyThread();
        using var device=graphics.Device.QueryInterface<IDXGIDevice>();
        using var adapter=device.GetAdapter();using var factory=adapter.GetParent<IDXGIFactory2>();
        var description=new SwapChainDescription1
        {
            Width=(uint)size.Width,Height=(uint)size.Height,Format=Format.B8G8R8A8_UNorm,
            SampleDescription=new(1,0),BufferUsage=Usage.RenderTargetOutput,BufferCount=2,
            SwapEffect=SwapEffect.FlipSequential,Scaling=Scaling.Stretch,AlphaMode=AlphaMode.Ignore
        };
        using var created=factory.CreateSwapChainForComposition(graphics.Device,description);
        swapChain=created.QueryInterface<IDXGISwapChain2>();
        try{Resize(size);}catch{swapChain.Dispose();throw;}
    }
    internal NativeSwapChainLease Acquire(){graphics.VerifyThread();return new(swapChain.NativePointer);}
    internal void Resize(PreviewViewportSize next)
    {
        graphics.VerifyThread();
        if(next.Width is <1 or >8192||next.Height is <1 or >8192||!float.IsFinite(next.RasterizationScale)||next.RasterizationScale<=0)throw new ArgumentOutOfRangeException(nameof(next));
        if(size?.Width!=next.Width||size.Height!=next.Height)
        {
            graphics.Context.ClearState();buffer?.Dispose();buffer=null;
            swapChain.ResizeBuffers(2,(uint)next.Width,(uint)next.Height,Format.B8G8R8A8_UNorm,SwapChainFlags.None).CheckError();
            buffer=swapChain.GetBuffer<ID3D11Texture2D>(0);
        }
        swapChain.MatrixTransform=Matrix3x2.CreateScale(1/next.RasterizationScale);size=next;
    }
    internal void Present(GpuTexture frame)
    {
        graphics.VerifyThread();
        if(size==null||frame.Width!=size.Width||frame.Height!=size.Height)throw new InvalidOperationException("Composition frame and viewport dimensions differ.");
        graphics.Context.ClearState();graphics.Context.CopyResource(buffer!,frame.Texture);swapChain.Present(0,PresentFlags.None).CheckError();
    }
    public void Dispose(){graphics.VerifyThread();buffer?.Dispose();buffer=null;swapChain.Dispose();}
}
