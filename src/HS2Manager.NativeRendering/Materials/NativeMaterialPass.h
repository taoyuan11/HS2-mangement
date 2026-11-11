#pragma once
#include "Device/GpuResources.h"
#include "Shaders/ShaderMetadata.h"

namespace hs2
{
    struct ShaderResource
    {
        ID3D11ShaderResourceView *View = nullptr;
        ID3D11SamplerState *Sampler = nullptr;
    };

    using ShaderResources = std::unordered_map<std::string, ShaderResource>;
    using ShaderTextures = std::unordered_map<std::string, const GpuTexture *>;

    struct NativeGpuVertexStream
    {
        static constexpr UINT Stride = 48;
        ID3D11Buffer *Buffer = nullptr;
        int VertexCount = 0;
        ID3D11Buffer *PreviousBuffer = nullptr;
    };

    class NativeMaterialPass
    {
    public:
        NativeMaterialPass(GraphicsDevice &graphics, const NativeShaderAsset &shader, const NativeShaderPass &pass,
                           const std::unordered_set<std::string> &keywords,
                           const std::unordered_map<std::string, float> &properties, bool reversedDepth = false,
                           std::optional<D3D11_CULL_MODE> culling = {}, bool disableDepthOffset = false,
                           std::optional<D3D11_DEPTH_STENCIL_DESC> depthOverride = {},
                           std::optional<UINT> stencilOverride = {});
        void Draw(const GpuMeshData &mesh, int submesh, const ShaderValues &values, const ShaderTextures &textures,
                  const NativeDeformedVertices *deformation = nullptr, const ShaderResources &resources = {},
                  const NativeDeformedVertices *previousDeformation = nullptr,
                  const NativeGpuVertexStream *gpuDeformation = nullptr, ID3D11Buffer *previousGpuBuffer = nullptr,
                  std::optional<bool> mirroredTransform = {});

        const std::vector<ShaderTextureBinding> &TextureBindings() const noexcept
        {
            return textureBindings_;
        }

    private:
        struct Signature
        {
            std::string Name;
            UINT Index = 0, Components = 0;
            int Semantic = 0;
        };

        struct Constant
        {
            bool Vertex = false;
            ShaderBuffer Layout;
            ComPtr<ID3D11Buffer> Buffer;
        };

        struct MeshBuffers
        {
            std::vector<float> Base, Data;
            ComPtr<ID3D11Buffer> Vertices, Indices;
        };

        GraphicsDevice &graphics_;
        ShaderVariant vertex_, pixel_;
        std::vector<ShaderTextureBinding> textureBindings_;
        std::vector<Signature> signature_;
        std::vector<Constant> constants_;
        std::unordered_map<const GpuMeshData *, MeshBuffers> meshes_;
        ComPtr<ID3D11VertexShader> vertexShader_;
        ComPtr<ID3D11PixelShader> pixelShader_;
        ComPtr<ID3D11InputLayout> layout_, gpuLayout_, gpuPreviousLayout_;
        ComPtr<ID3D11RasterizerState> rasterizer_, invertedRasterizer_;
        ComPtr<ID3D11BlendState> blend_;
        ComPtr<ID3D11DepthStencilState> depth_;
        UINT stencilReference_ = 0;
    };

    class NativeComputePass
    {
    public:
        NativeComputePass(GraphicsDevice &graphics, NativeComputeKernel kernel);
        void Dispatch(UINT width, UINT height, UINT depth, const ShaderValues &values, const ShaderResources &textures,
                      const std::unordered_map<std::string, ID3D11UnorderedAccessView *> &outputs);

    private:
        GraphicsDevice &graphics_;
        NativeComputeKernel kernel_;
        ComPtr<ID3D11ComputeShader> shader_;

        struct Buffer
        {
            ShaderBuffer Layout;
            ComPtr<ID3D11Buffer> Object;
        };

        std::vector<Buffer> buffers_;
        std::vector<std::pair<UINT, ComPtr<ID3D11SamplerState>>> samplers_;
    };
}
