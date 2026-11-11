#pragma once
#include "Materials/NativeMaterialPass.h"
#include "Pipeline/NativeRenderCamera.h"

namespace hs2
{
    class NativeFullscreenPass final
    {
    public:
        NativeFullscreenPass(GraphicsDevice &graphics, const NativeShaderAsset &shader, int passIndex,
                             const std::unordered_set<std::string> &keywords = {}, bool engineBlit = false,
                             int subShader = 0, const std::unordered_map<std::string, float> &properties = {},
                             std::optional<D3D11_DEPTH_STENCIL_DESC> depthOverride = {},
                             std::optional<UINT> stencilOverride = {});
        void Draw(GpuTexture &target, const ShaderValues &values, const ShaderTextures &textures,
                  const ShaderResources &views = {}, const NativeRenderCamera *rayCamera = nullptr,
                  ID3D11DepthStencilView *depth = nullptr);

    private:
        GraphicsDevice &graphics_;
        bool engineBlit_;
        NativeMaterialPass pass_;
    };

    class GpuCopyPass final
    {
    public:
        explicit GpuCopyPass(GraphicsDevice &graphics);
        void Copy(const GpuTexture &source, GpuTexture &target, bool flipVertical = false, bool velocity = false);
        void CopyView(ID3D11ShaderResourceView *source, GpuTexture &target, bool flipVertical = false,
                      bool velocity = false, bool point = false);

    private:
        GraphicsDevice &graphics_;
        ComPtr<ID3D11VertexShader> vertex_;
        ComPtr<ID3D11PixelShader> pixel_, flipped_, velocity_;
        ComPtr<ID3D11SamplerState> linear_, point_;
        ComPtr<ID3D11RasterizerState> rasterizer_;
        ComPtr<ID3D11DepthStencilState> depth_;
        ComPtr<ID3D11BlendState> blend_;
    };
}
