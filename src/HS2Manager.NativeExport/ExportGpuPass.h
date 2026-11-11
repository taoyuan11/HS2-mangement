#pragma once
#include "../HS2Manager.NativeRendering/GpuResources.h"
namespace hs2 {
class ExportGpuPass final {
public:
    ExportGpuPass(GraphicsDevice& graphics,const std::string& source,const char* pixelEntry,int constantBytes);
    void Draw(GpuTexture& target,std::span<const std::uint8_t> constants,std::span<ID3D11ShaderResourceView* const> views,std::span<ID3D11SamplerState* const> samplers);
private:
    GraphicsDevice& graphics_;
    int constantBytes_;
    ComPtr<ID3D11VertexShader> vertex_;
    ComPtr<ID3D11PixelShader> pixel_;
    ComPtr<ID3D11Buffer> constants_;
    ComPtr<ID3D11RasterizerState> rasterizer_;
    ComPtr<ID3D11DepthStencilState> depth_;
    ComPtr<ID3D11BlendState> blend_;
};
}
