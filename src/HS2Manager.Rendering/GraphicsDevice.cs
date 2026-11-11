using HS2Manager.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace HS2Manager.Rendering;

/// <summary>Single-thread-owned D3D11 device. No engine or external rendering process.</summary>
public sealed class GraphicsDevice : IDisposable
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public string AdapterName { get; }
    private bool disposed;

    public GraphicsDevice()
    {
        D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out var device, out _, out var context).CheckError();
        Device = device!; Context = context!;
        using var dxgi = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        AdapterName = adapter.Description.Description;
    }

    public void VerifyThread()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The graphics device belongs to the rendering thread.");
    }

    public ID3D11DeviceChild CreateShader(NativeShaderProgram program)
    {
        VerifyThread();
        if (NativeShaderReader.ValidateBytecode(program.Bytecode) != program.Stage) throw new InvalidDataException("Shader stage mismatch.");
        return program.Stage switch
        {
            NativeShaderStage.Vertex => Device.CreateVertexShader(program.Bytecode),
            NativeShaderStage.Pixel => Device.CreatePixelShader(program.Bytecode),
            NativeShaderStage.Geometry => Device.CreateGeometryShader(program.Bytecode),
            NativeShaderStage.Hull => Device.CreateHullShader(program.Bytecode),
            NativeShaderStage.Domain => Device.CreateDomainShader(program.Bytecode),
            NativeShaderStage.Compute => Device.CreateComputeShader(program.Bytecode),
            _ => throw new NotSupportedException("Unsupported shader stage.")
        };
    }

    public byte[] ClearAndReadback(int width, int height, Color4 color)
    {
        VerifyThread();
        if (width is < 1 or > 8192 || height is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(width));
        using var target = Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
            (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget));
        using var view = Device.CreateRenderTargetView(target);
        Context.ClearRenderTargetView(view, color);
        return Readback(target, width, height);
    }

    public unsafe byte[] Readback(ID3D11Texture2D source, int width, int height)
    {
        VerifyThread();
        var description = source.Description;
        if (description.Width != width || description.Height != height || description.SampleDescription.Count != 1 ||
            description.Format is not (Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb or Format.B8G8R8A8_Typeless)) throw new ArgumentException("Readback requires an exact-size BGRA8 resolved texture.");
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        using var staging = Device.CreateTexture2D(description);
        Context.CopyResource(staging, source);
        var mapped = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pixels = new byte[checked(width * height * 4)];
            for (int row = 0; row < height; row++)
                new ReadOnlySpan<byte>((byte*)mapped.DataPointer + row * mapped.RowPitch, width * 4).CopyTo(pixels.AsSpan(row * width * 4));
            return pixels;
        }
        finally { Context.Unmap(staging, 0); }
    }

    public unsafe float[] ReadbackFloat(ID3D11Texture2D source)
    {
        VerifyThread();var description=source.Description;
        if(description.Format is not (Format.R32G32B32A32_Float or Format.R16G16B16A16_Float or Format.R16G16_Float)||description.SampleDescription.Count!=1||description.ArraySize!=1)
            throw new ArgumentException("Float readback requires a resolved RG16F, RGBA16F or RGBA32F texture.");
        description.Usage=ResourceUsage.Staging;description.BindFlags=BindFlags.None;description.CPUAccessFlags=CpuAccessFlags.Read;description.MiscFlags=ResourceOptionFlags.None;
        using var staging=Device.CreateTexture2D(description);Context.CopyResource(staging,source);
        var mapped=Context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
        try
        {
            var values=new float[checked((int)(description.Width*description.Height*4))];
            for(int row=0;row<description.Height;row++)
            {
                int count=(int)description.Width*4,offset=row*count;
                if(description.Format==Format.R32G32B32A32_Float)new ReadOnlySpan<float>((byte*)mapped.DataPointer+row*mapped.RowPitch,count).CopyTo(values.AsSpan(offset));
                else
                {
                    int components=description.Format==Format.R16G16_Float?2:4;
                    var half=new ReadOnlySpan<Half>((byte*)mapped.DataPointer+row*mapped.RowPitch,(int)description.Width*components);
                    for(int i=0;i<description.Width;i++)for(int c=0;c<components;c++)values[offset+i*4+c]=(float)half[i*components+c];
                }
            }
            return values;
        }
        finally{Context.Unmap(staging,0);}
    }

    public void Dispose()
    {
        if (disposed) return;
        VerifyThread();
        Context.ClearState(); Context.Flush();
        Context.Dispose(); Device.Dispose(); disposed = true;
    }
}
