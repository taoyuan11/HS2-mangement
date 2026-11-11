using HS2Manager.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

public sealed class GpuVolume : IDisposable
{
    public ID3D11Texture3D Texture {get;}
    public ID3D11ShaderResourceView View {get;}
    public ID3D11UnorderedAccessView Output {get;}
    public ID3D11SamplerState Sampler {get;}
    public GpuVolume(GraphicsDevice graphics,int width,int height,int depth,Format format=Format.R16G16B16A16_Float)
    {
        graphics.VerifyThread();var owned=new List<IDisposable>();
        try
        {
            Texture=graphics.Device.CreateTexture3D(new Texture3DDescription(format,(uint)width,(uint)height,(uint)depth,1,BindFlags.ShaderResource|BindFlags.UnorderedAccess));owned.Add(Texture);
            View=graphics.Device.CreateShaderResourceView(Texture);owned.Add(View);Output=graphics.Device.CreateUnorderedAccessView(Texture);owned.Add(Output);
            Sampler=graphics.Device.CreateSamplerState(SamplerDescription.LinearClamp);
        }
        catch{foreach(var resource in owned.AsEnumerable().Reverse())resource.Dispose();throw;}
    }
    public void Dispose(){Sampler.Dispose();Output.Dispose();View.Dispose();Texture.Dispose();}
}

public sealed class NativeComputePass : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly NativeComputeKernel kernel;
    private readonly ID3D11ComputeShader shader;
    private readonly List<(ShaderBuffer Layout,ID3D11Buffer Buffer)> buffers=[];
    private readonly List<(uint Slot,ID3D11SamplerState State)> samplers=[];
    public NativeComputePass(GraphicsDevice graphics,NativeComputeKernel kernel)
    {
        this.graphics=graphics;this.kernel=kernel;graphics.VerifyThread();shader=(ID3D11ComputeShader)graphics.CreateShader(kernel.Program);shader.DebugName=kernel.Name;
        try
        {
            if(kernel.Metadata.Get("inBuffers").Children.Length!=0)
                throw new NotSupportedException("Compute structured buffers require explicit bindings.");
            foreach(var builtin in kernel.Metadata.Get("builtinSamplers").Children)
            {
                int encoded=(int)builtin.Float("sampler");
                if(encoded<0||encoded>255)throw new NotSupportedException("Unknown built-in compute sampler flags.");
                TextureAddressMode Address(int v)=>v switch{0=>TextureAddressMode.Wrap,1=>TextureAddressMode.Clamp,2=>TextureAddressMode.Mirror,3=>TextureAddressMode.MirrorOnce,_=>throw new InvalidDataException()};
                var description=SamplerDescription.LinearClamp;
                description.Filter=(encoded&3) switch{0=>Filter.MinMagMipPoint,1=>Filter.MinMagLinearMipPoint,2=>Filter.MinMagMipLinear,_=>throw new NotSupportedException("Unknown built-in sampler filter.")};
                description.AddressU=Address((encoded>>2)&3);description.AddressV=Address((encoded>>4)&3);description.AddressW=Address((encoded>>6)&3);
                samplers.Add(((uint)builtin.Float("bindPoint"),graphics.Device.CreateSamplerState(description)));
            }
            foreach(var binding in kernel.Metadata.Get("cbs").Children)
            {
                var cb=kernel.ConstantBuffers.Children.Single(c=>c.String("name")==binding.String("name"));
                var constants=cb.Get("params").Children.Select(p=>new ShaderConstant(p.String("name"),(int)p.Float("offset"),
                    (int)p.Float(p.Float("rowCount")==1?"colCount":"rowCount"),Math.Max(1,(int)p.Float("arraySize")),p.Float("rowCount")!=1,(int)p.Float("type"))).ToArray();
                var layout=new ShaderBuffer(cb.String("name"),(int)binding.Float("bindPoint"),((int)cb.Float("byteSize")+15)&~15,constants);
                buffers.Add((layout,graphics.Device.CreateBuffer((uint)layout.Size,BindFlags.ConstantBuffer)));
            }
        }
        catch{Dispose();throw;}
    }
    public void Dispatch(uint width,uint height,uint depth,IReadOnlyDictionary<string,float[]> values,IReadOnlyDictionary<string,GpuTexture> textures,IReadOnlyDictionary<string,GpuVolume> outputs)
        =>Dispatch(width,height,depth,values,textures.ToDictionary(p=>p.Key,p=>(p.Value.View,p.Value.Sampler)),outputs.ToDictionary(p=>p.Key,p=>p.Value.Output));
    public void Dispatch(uint width,uint height,uint depth,IReadOnlyDictionary<string,float[]> values,
        IReadOnlyDictionary<string,(ID3D11ShaderResourceView View,ID3D11SamplerState Sampler)> textures,
        IReadOnlyDictionary<string,ID3D11UnorderedAccessView> outputs)
    {
        graphics.VerifyThread();var ctx=graphics.Context;ctx.ClearState();
        foreach(var buffer in buffers){ctx.UpdateSubresource(ShaderBindings.Pack(buffer.Layout,values),buffer.Buffer);ctx.CSSetConstantBuffer((uint)buffer.Layout.Slot,buffer.Buffer);}
        foreach(var binding in kernel.Metadata.Get("textures").Children)
        {
            var texture=textures[binding.String("name")];ctx.CSSetShaderResource((uint)binding.Float("bindPoint"),texture.View);
            if(binding.Float("samplerBindPoint",-1)>=0)ctx.CSSetSampler((uint)binding.Float("samplerBindPoint"),texture.Sampler);
        }
        foreach(var binding in kernel.Metadata.Get("outBuffers").Children)ctx.CSSetUnorderedAccessView((uint)binding.Float("bindPoint"),outputs[binding.String("name")]);
        foreach(var sampler in samplers)ctx.CSSetSampler(sampler.Slot,sampler.State);
        ctx.CSSetShader(shader);ctx.Dispatch((width+kernel.GroupSize[0]-1)/kernel.GroupSize[0],(height+kernel.GroupSize[1]-1)/kernel.GroupSize[1],(depth+kernel.GroupSize[2]-1)/kernel.GroupSize[2]);ctx.ClearState();
    }
    public void Dispose(){foreach(var buffer in buffers)buffer.Buffer.Dispose();buffers.Clear();foreach(var sampler in samplers)sampler.State.Dispose();samplers.Clear();shader.Dispose();}
}
