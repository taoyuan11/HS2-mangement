using System.Buffers.Binary;
using System.Text;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace HS2Manager.Rendering;

/// <summary>A material pass uses its actual DXBC, declared constant layout and serialized render state.</summary>
public sealed class NativeMaterialPass : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly List<IDisposable> owned = [];
    private readonly ShaderVariant vertex, pixel;
    public IReadOnlyList<ShaderTextureBinding> TextureBindings {get;}
    private readonly ID3D11VertexShader vertexShader;
    private readonly ID3D11PixelShader pixelShader;
    private readonly ID3D11InputLayout layout;
    private readonly ID3D11InputLayout gpuLayout,gpuPreviousLayout;
    private readonly ID3D11RasterizerState rasterizer,invertedRasterizer;
    private readonly ID3D11BlendState blend;
    private readonly ID3D11DepthStencilState depth;
    private readonly List<(NativeShaderStage Stage, ShaderBuffer Layout, ID3D11Buffer Buffer)> constants = [];
    private readonly Signature[] signature;
    private readonly int[] semantics;
    private readonly uint stencilReference;
    private sealed class MeshBuffers(float[] data,ID3D11Buffer vertices,ID3D11Buffer indices)
    {
        internal readonly float[] Data=data;
        internal readonly ID3D11Buffer Vertices=vertices,Indices=indices;
        internal NativeDeformedVertices? LastDeformation,LastPrevious;
    }
    private readonly Dictionary<GpuMeshData,MeshBuffers> meshes = new(ReferenceEqualityComparer.Instance);
    public NativeMaterialPass(GraphicsDevice graphics, NativeShaderAsset shader, NativeShaderPass pass, ISet<string> keywords, IReadOnlyDictionary<string, float> properties, bool reversedDepth = false, CullMode? culling = null, bool disableDepthOffset=false,
        DepthStencilDescription? depthOverride=null,uint? stencilOverride=null)
    {
        this.graphics = graphics; graphics.VerifyThread();
        try
        {
            vertex = ShaderBindings.Select(shader, pass, NativeShaderStage.Vertex, keywords);
            pixel = ShaderBindings.Select(shader, pass, NativeShaderStage.Pixel, keywords);
            TextureBindings=vertex.Textures.Concat(pixel.Textures).DistinctBy(t=>t.Name).ToArray();
            foreach (string stage in new[] { "progGeometry", "progHull", "progDomain" })
                if (pass.Metadata.Get(stage).Get("m_SubPrograms").Children.Length != 0)
                    throw new NotSupportedException("Material pass requires additional GPU stages: " + stage);
            vertexShader = Own((ID3D11VertexShader)graphics.CreateShader(vertex.Program));
            pixelShader = Own((ID3D11PixelShader)graphics.CreateShader(pixel.Program));
            vertexShader.DebugName=shader.Name;pixelShader.DebugName=shader.Name;
            signature = InputSignature(vertex.Program.Bytecode);
            semantics=signature.Select(Semantic).ToArray();
            var elements = signature.Select((s, i) => new InputElementDescription(s.Name, (uint)s.Index,
                InputFormat(semantics[i] is 0 or 1?3:s.Components), (uint)(i * 16), 0)).ToArray();
            layout = Own(graphics.Device.CreateInputLayout(elements, vertex.Program.Bytecode));
            var gpuElements=signature.Select((s,i)=>semantics[i] is >=0 and <=2
                ?new InputElementDescription(s.Name,(uint)s.Index,InputFormat(semantics[i] is 0 or 1?3:4),(uint)(semantics[i]*16),1)
                :elements[i]).ToArray();
            gpuLayout=Own(graphics.Device.CreateInputLayout(gpuElements,vertex.Program.Bytecode));
            gpuPreviousLayout=Own(graphics.Device.CreateInputLayout(gpuElements.Select((element,i)=>semantics[i]==8
                ?new InputElementDescription(signature[i].Name,(uint)signature[i].Index,Format.R32G32B32_Float,0,2):element).ToArray(),vertex.Program.Bytecode));
            foreach (var pair in new[] { (NativeShaderStage.Vertex, vertex), (NativeShaderStage.Pixel, pixel) })
                foreach (var buffer in pair.Item2.Buffers)
                    constants.Add((pair.Item1, buffer, Own(graphics.Device.CreateBuffer((uint)buffer.Size, BindFlags.ConstantBuffer))));
            var state = pass.Metadata.Get("m_State");
            int V(string name, int fallback = 0) => State(state.Get(name), fallback);
            int State(Parameter p, int fallback = 0)
            {
                string name = p.String("name");
                if (!string.IsNullOrEmpty(name) && name != "<noninit>")
                    return properties.TryGetValue(name, out float value) ? (int)value : throw new InvalidDataException("Render-state property is missing: " + name);
                return p.Has("val") ? (int)p.Float("val") : fallback;
            }
            var rasterDescription=new RasterizerDescription
            {
                FillMode = FillMode.Solid, CullMode = culling??(V("culling", 2) switch
                { 0 => CullMode.None, 1 => CullMode.Front, 2 => CullMode.Back, _ => throw new InvalidDataException("Unknown culling mode.") }),
                DepthClipEnable = V("zClip", 1) != 0, DepthBias = disableDepthOffset?0:V("offsetUnits") * (reversedDepth ? -1 : 1), SlopeScaledDepthBias = disableDepthOffset?0:V("offsetFactor") * (reversedDepth ? -1 : 1),
                // This renderer uses a positive-Y projection; offscreen captures with
                // a negative-Y projection invert this rasterizer winding convention.
                FrontCounterClockwise = false, MultisampleEnable = true
            };
            rasterizer=Own(graphics.Device.CreateRasterizerState(rasterDescription));
            rasterDescription.FrontCounterClockwise=true;invertedRasterizer=Own(graphics.Device.CreateRasterizerState(rasterDescription));
            var blendDescription = BlendDescription.Opaque;
            blendDescription.AlphaToCoverageEnable = V("alphaToMask") != 0;
            blendDescription.IndependentBlendEnable = state.Get("rtSeparateBlend").Number != 0;
            for (int i = 0; i < 8; i++)
            {
                var b = state.Get("rtBlend" + i);
                var target = new RenderTargetBlendDescription
                {
                    SourceBlend = BlendFactor(State(b.Get("srcBlend"), 1)), DestinationBlend = BlendFactor(State(b.Get("destBlend"))),
                    SourceBlendAlpha = AlphaFactor(State(b.Get("srcBlendAlpha"), 1)), DestinationBlendAlpha = AlphaFactor(State(b.Get("destBlendAlpha"))),
                    BlendOperation = BlendOperationOf(State(b.Get("blendOp"))), BlendOperationAlpha = BlendOperationOf(State(b.Get("blendOpAlpha"))),
                    RenderTargetWriteMask = ColorMask(State(b.Get("colMask"), 15))
                };
                target.BlendEnable = target.SourceBlend != Blend.One || target.DestinationBlend != Blend.Zero ||
                    target.SourceBlendAlpha != Blend.One || target.DestinationBlendAlpha != Blend.Zero ||
                    target.BlendOperation != BlendOperation.Add || target.BlendOperationAlpha != BlendOperation.Add;
                blendDescription.RenderTarget[i] = target;
            }
            blend = Own(graphics.Device.CreateBlendState(blendDescription));
            var stencil = state.Get("stencilOp");
            var front = Stencil(state.Get("stencilOpFront"));
            var back = Stencil(state.Get("stencilOpBack"));
            if (State(stencil.Get("comp"), 8) != 8 || State(stencil.Get("pass")) != 0 || State(stencil.Get("fail")) != 0 || State(stencil.Get("zFail")) != 0)
                front = back = Stencil(stencil);
            var depthDescription = new DepthStencilDescription
            {
                DepthEnable = V("zTest", 4) != 0 || V("zWrite", 1) != 0, DepthWriteMask = V("zWrite", 1) != 0 ? DepthWriteMask.All : DepthWriteMask.Zero,
                DepthFunc = Compare(reversedDepth ? ReverseCompare(V("zTest", 4)) : V("zTest", 4)), StencilEnable = front.StencilFunc != ComparisonFunction.Always || back.StencilFunc != ComparisonFunction.Always ||
                    front.StencilPassOp != StencilOperation.Keep || back.StencilPassOp != StencilOperation.Keep,
                StencilReadMask = (byte)V("stencilReadMask", 255), StencilWriteMask = (byte)V("stencilWriteMask", 255), FrontFace = front, BackFace = back
            };
            stencilReference = stencilOverride??(uint)V("stencilRef");
            depth = Own(graphics.Device.CreateDepthStencilState(depthOverride??depthDescription));
            DepthStencilOperationDescription Stencil(Parameter s) => new()
            {
                StencilFailOp = StencilOp(State(s.Get("fail"))), StencilDepthFailOp = StencilOp(State(s.Get("zFail"))),
                StencilPassOp = StencilOp(State(s.Get("pass"))), StencilFunc = Compare(State(s.Get("comp"), 8))
            };
        }
        catch(InvalidDataException error){Dispose();throw new InvalidDataException($"Shader pass {shader.Name}/{pass.Metadata.Get("m_State").String("m_Name")}: {error.Message}",error);}
        catch { Dispose(); throw; }
    }
    private T Own<T>(T value) where T : IDisposable { owned.Add(value); return value; }
    private static ColorWriteEnable ColorMask(int mask) => (ColorWriteEnable)(((mask&8)>>3)|((mask&4)>>1)|((mask&2)<<1)|((mask&1)<<3));

    public void Draw(GpuMeshData mesh, int submesh, IReadOnlyDictionary<string, float[]> values, IReadOnlyDictionary<string, GpuTexture> textures,
        NativeDeformedVertices? deformation = null, IReadOnlyDictionary<string, (ID3D11ShaderResourceView View, ID3D11SamplerState Sampler)>? resourceViews = null,
        NativeDeformedVertices? previousDeformation = null,NativeGpuVertexStream? gpuDeformation=null,ID3D11Buffer? previousGpuBuffer=null,bool? mirroredTransform=null)
    {
        graphics.VerifyThread();
        var ctx = graphics.Context;
        foreach (var constant in constants)
        {
            ctx.UpdateSubresource(ShaderBindings.Pack(constant.Layout, values), constant.Buffer);
            if (constant.Stage == NativeShaderStage.Vertex) ctx.VSSetConstantBuffer((uint)constant.Layout.Slot, constant.Buffer);
            else ctx.PSSetConstantBuffer((uint)constant.Layout.Slot, constant.Buffer);
        }
        BindTextures(vertex, true); BindTextures(pixel, false);
        if (!meshes.TryGetValue(mesh, out var buffers))
        {
            var data = new float[checked(mesh.VertexCount * signature.Length * 4)];
            for (int v = 0; v < mesh.VertexCount; v++)
            for (int s = 0; s < signature.Length; s++)
            {
                int semantic = semantics[s];
                // Missing UV streams inherit the last available UV channel in
                // the source mesh binding. Imported hair shaders commonly read
                // TEXCOORD1 even when their mesh stores only TEXCOORD0.
                if(semantic is >=4 and <=11&&!mesh.Channels.Any(c=>c.Semantic==semantic))
                    semantic=mesh.Channels.Where(c=>c.Semantic is >=4 and <=11&&c.Semantic<semantic).Select(c=>c.Semantic).DefaultIfEmpty(semantic).Max();
                for (int c = 0; c < 4; c++)
                    data[(v * signature.Length + s) * 4 + c] = (semantic == 0 && c == 3) ||
                        (semantic == 3 && !mesh.Channels.Any(ch => ch.Semantic == 3)) ? 1 : mesh.Read(semantic, v, c);
            }
            buffers = new(data, Own(graphics.Device.CreateBuffer(data, BindFlags.VertexBuffer)),
                Own(graphics.Device.CreateBuffer(mesh.IndexBytes, BindFlags.IndexBuffer, ResourceUsage.Immutable)));
            meshes.Add(mesh, buffers);
        }
        if (deformation != null&&(!ReferenceEquals(deformation,buffers.LastDeformation)||!ReferenceEquals(previousDeformation,buffers.LastPrevious)))
        {
            if (deformation.Positions.Length != mesh.VertexCount) throw new ArgumentException("Deformed mesh size differs from source.");
            for (int v = 0; v < mesh.VertexCount; v++)
                for (int s = 0; s < signature.Length; s++)
                {
                    int semantic = semantics[s];
                    if (semantic is < 0 or > 2 && !(semantic==8&&previousDeformation!=null)) continue;
                    var value = semantic switch { 8 => new System.Numerics.Vector4(previousDeformation!.Positions[v],1), 0 => new System.Numerics.Vector4(deformation.Positions[v], 1),
                        1 => new System.Numerics.Vector4(deformation.Normals[v], 0), _ => deformation.Tangents[v] };
                    int offset = (v * signature.Length + s) * 4;
                    buffers.Data[offset] = value.X; buffers.Data[offset+1] = value.Y; buffers.Data[offset+2] = value.Z; buffers.Data[offset+3] = value.W;
                }
            ctx.UpdateSubresource(buffers.Data, buffers.Vertices);
            buffers.LastDeformation=deformation;buffers.LastPrevious=previousDeformation;
        }
        ctx.IASetInputLayout(gpuDeformation==null?layout:previousGpuBuffer==null?gpuLayout:gpuPreviousLayout);
        ctx.IASetVertexBuffer(0, buffers.Vertices, (uint)(signature.Length * 16));
        if(gpuDeformation!=null)
        {
            if(gpuDeformation.VertexCount!=mesh.VertexCount)throw new ArgumentException("GPU deformed mesh size differs from source.");
            ctx.IASetVertexBuffer(1,gpuDeformation.Buffer,NativeGpuVertexStream.Stride);
            if(previousGpuBuffer!=null)ctx.IASetVertexBuffer(2,previousGpuBuffer,NativeGpuVertexStream.Stride);
        }
        ctx.IASetIndexBuffer(buffers.Indices, mesh.IndexSize == 2 ? Format.R16_UInt : Format.R32_UInt, 0);
        var part = mesh.Submeshes[submesh];
        ctx.IASetPrimitiveTopology(part.Topology switch
        { 0 => PrimitiveTopology.TriangleList, 3 => PrimitiveTopology.LineList, 4 => PrimitiveTopology.LineStrip, 5 => PrimitiveTopology.PointList, _ => throw new NotSupportedException("Unsupported mesh topology.") });
        ctx.VSSetShader(vertexShader); ctx.PSSetShader(pixelShader);
        bool counterClockwise=values.TryGetValue("_ProjectionParams",out var projectionSign)&&projectionSign[0]<0;
        // Skinning bakes reflection into the vertex stream while its submitted
        // rigid object matrix has positive determinant. Retain the original
        // renderer's reflection when selecting culling and SV_IsFrontFace.
        if(mirroredTransform is {} mirrored)counterClockwise^=mirrored;
        ctx.RSSetState(counterClockwise?invertedRasterizer:rasterizer); ctx.OMSetBlendState(blend); ctx.OMSetDepthStencilState(depth, stencilReference);
        ctx.DrawIndexed((uint)part.IndexCount, (uint)part.FirstIndex, part.BaseVertex);
        void BindTextures(ShaderVariant variant, bool isVertex)
        {
            foreach (var binding in variant.Textures)
            {
                if (resourceViews != null && resourceViews.TryGetValue(binding.Name, out var resource))
                {
                    if (isVertex) ctx.VSSetShaderResource((uint)binding.Slot, resource.View); else ctx.PSSetShaderResource((uint)binding.Slot, resource.View);
                    if (binding.SamplerSlot >= 0)
                    {if(isVertex)ctx.VSSetSampler((uint)binding.SamplerSlot,resource.Sampler);else ctx.PSSetSampler((uint)binding.SamplerSlot,resource.Sampler);}
                    continue;
                }
                if (!textures.TryGetValue(binding.Name, out var texture)) throw new InvalidDataException("Shader texture is missing: " + binding.Name);
                if (isVertex) ctx.VSSetShaderResource((uint)binding.Slot, texture.View); else ctx.PSSetShaderResource((uint)binding.Slot, texture.View);
                if (binding.SamplerSlot >= 0)
                { if (isVertex) ctx.VSSetSampler((uint)binding.SamplerSlot, texture.Sampler); else ctx.PSSetSampler((uint)binding.SamplerSlot, texture.Sampler); }
            }
        }
    }
    public void Dispose() { for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); owned.Clear(); meshes.Clear(); }
    private static ComparisonFunction Compare(int value) => value switch
    { 0 or 8 => ComparisonFunction.Always, 1 => ComparisonFunction.Never, 2 => ComparisonFunction.Less, 3 => ComparisonFunction.Equal,
        4 => ComparisonFunction.LessEqual, 5 => ComparisonFunction.Greater, 6 => ComparisonFunction.NotEqual, 7 => ComparisonFunction.GreaterEqual,
        _ => throw new InvalidDataException("Unknown depth comparison.") };
    private static int ReverseCompare(int value) => value switch { 2 => 5, 4 => 7, 5 => 2, 7 => 4, _ => value };
    private static StencilOperation StencilOp(int value) => value switch
    { 0 => StencilOperation.Keep, 1 => StencilOperation.Zero, 2 => StencilOperation.Replace, 3 => StencilOperation.IncrementSaturate,
        4 => StencilOperation.DecrementSaturate, 5 => StencilOperation.Invert, 6 => StencilOperation.Increment, 7 => StencilOperation.Decrement,
        _ => throw new InvalidDataException("Unknown stencil operation.") };
    private static Blend AlphaFactor(int value) => BlendFactor(value switch { 2 => 7, 3 => 5, 4 => 8, 6 => 10, 9 => 1, _ => value });
    private static Blend BlendFactor(int value) => value switch
    { 0 => Blend.Zero, 1 => Blend.One, 2 => Blend.DestinationColor, 3 => Blend.SourceColor, 4 => Blend.InverseDestinationColor,
        5 => Blend.SourceAlpha, 6 => Blend.InverseSourceColor, 7 => Blend.DestinationAlpha, 8 => Blend.InverseDestinationAlpha,
        // Imported Iridescent materials serialize 11 for destination alpha
        // blending; the reference D3D11 pipeline resolves it to InvSrcAlpha.
        9 => Blend.SourceAlphaSaturate, 10 or 11 => Blend.InverseSourceAlpha, _ => throw new InvalidDataException("Unknown blend factor: "+value) };
    private static BlendOperation BlendOperationOf(int value) => value switch
    { 0 => BlendOperation.Add, 1 => BlendOperation.Subtract, 2 => BlendOperation.ReverseSubtract, 3 => BlendOperation.Min,
        4 => BlendOperation.Max, _ => throw new NotSupportedException("Logical blend operations require a D3D11.1 pass.") };
    private static Format InputFormat(int components)=>components switch
    {1=>Format.R32_Float,2=>Format.R32G32_Float,3=>Format.R32G32B32_Float,4=>Format.R32G32B32A32_Float,_=>throw new InvalidDataException("Invalid vertex input component count.")};
    private sealed record Signature(string Name, int Index,int Components);
    private static int Semantic(Signature s) => s.Name.ToUpperInvariant() switch
    { "POSITION" => 0, "NORMAL" => 1, "TANGENT" => 2, "COLOR" => 3, "TEXCOORD" when s.Index is >= 0 and < 8 => 4 + s.Index,
        "BLENDWEIGHT" => 12, "BLENDINDICES" => 13, _ => throw new NotSupportedException("Unsupported vertex semantic " + s.Name) };
    private static Signature[] InputSignature(byte[] bytecode)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytecode.AsSpan(28, 4));
        for (int i = 0; i < count; i++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(bytecode.AsSpan(32 + i * 4, 4));
            if (!bytecode.AsSpan(offset, 4).SequenceEqual("ISGN"u8)) continue;
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytecode.AsSpan(offset + 4, 4));
            var data = bytecode.AsSpan(offset + 8, length);
            if (data.Length < 8) throw new InvalidDataException("Invalid input signature.");
            int inputs = BinaryPrimitives.ReadInt32LittleEndian(data);
            if (inputs < 0 || inputs > 32 || 8 + inputs * 24 > data.Length) throw new InvalidDataException("Invalid input signature table.");
            var result = new List<Signature>();
            for (int input = 0; input < inputs; input++)
            {
                int p = 8 + input * 24;
                int system = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(p + 8, 4));
                if (system != 0) continue;
                int name = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(p, 4));
                if (name < 0 || name >= data.Length || data[name..].IndexOf((byte)0) < 0) throw new InvalidDataException("Invalid semantic name.");
                int mask=data[p+20]&15;int components=mask==0?4:System.Numerics.BitOperations.Log2((uint)mask)+1;
                result.Add(new(Encoding.ASCII.GetString(data.Slice(name, data[name..].IndexOf((byte)0))), BinaryPrimitives.ReadInt32LittleEndian(data.Slice(p + 4, 4)),components));
            }
            return result.ToArray();
        }
        throw new InvalidDataException("No vertex input signature.");
    }
}
