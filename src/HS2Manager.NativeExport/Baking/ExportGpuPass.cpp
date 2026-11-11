#include "Baking/ExportGpuPass.h"
#include <d3dcompiler.h>
#include <stdexcept>

namespace hs2
{
    ExportGpuPass::ExportGpuPass(GraphicsDevice &g, const std::string &source, const char *entry, int constantBytes)
        : graphics_(g), constantBytes_(constantBytes)
    {
        g.VerifyThread();
        if (constantBytes < 16 || constantBytes % 16)
            throw std::invalid_argument("Unaligned export shader constants.");
        auto compile = [&](const char *function, const char *profile)
        {
            ComPtr<ID3DBlob> code, error;
            auto hr = D3DCompile(source.data(), source.size(), "NativeExport", nullptr, nullptr, function, profile, 0,
                                 0, &code, &error);
            if (FAILED(hr))
                throw std::runtime_error(
                    error ? std::string(static_cast<const char *>(error->GetBufferPointer()), error->GetBufferSize())
                          : "Export shader compilation failed.");
            return code;
        };
        auto v = compile("VS", "vs_5_0"), p = compile(entry, "ps_5_0");
        CheckHResult(g.Device()->CreateVertexShader(v->GetBufferPointer(), v->GetBufferSize(), nullptr, &vertex_),
                     "Export vertex shader");
        CheckHResult(g.Device()->CreatePixelShader(p->GetBufferPointer(), p->GetBufferSize(), nullptr, &pixel_),
                     "Export pixel shader");
        constants_ = g.CreateBuffer(static_cast<UINT>(constantBytes), D3D11_BIND_CONSTANT_BUFFER);
        D3D11_RASTERIZER_DESC rasterizer{};
        rasterizer.FillMode = D3D11_FILL_SOLID;
        rasterizer.CullMode = D3D11_CULL_NONE;
        rasterizer.DepthClipEnable = TRUE;
        CheckHResult(g.Device()->CreateRasterizerState(&rasterizer, &rasterizer_), "Export rasterizer");
        D3D11_DEPTH_STENCIL_DESC depth{};
        depth.DepthFunc = D3D11_COMPARISON_ALWAYS;
        CheckHResult(g.Device()->CreateDepthStencilState(&depth, &depth_), "Export depth state");
        D3D11_BLEND_DESC blend{};
        for (auto &target : blend.RenderTarget)
        {
            target.SrcBlend = target.SrcBlendAlpha = D3D11_BLEND_ONE;
            target.DestBlend = target.DestBlendAlpha = D3D11_BLEND_ZERO;
            target.BlendOp = target.BlendOpAlpha = D3D11_BLEND_OP_ADD;
            target.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        }
        CheckHResult(g.Device()->CreateBlendState(&blend, &blend_), "Export blend state");
    }

    void ExportGpuPass::Draw(GpuTexture &target, std::span<const std::uint8_t> constants,
                             std::span<ID3D11ShaderResourceView *const> views,
                             std::span<ID3D11SamplerState *const> samplers)
    {
        graphics_.VerifyThread();
        if (constants.size() != static_cast<std::size_t>(constantBytes_) ||
            views.size() > D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT ||
            samplers.size() > D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT)
            throw std::invalid_argument("Export shader binding count is invalid.");
        auto *context = graphics_.Context();
        context->ClearState();
        auto *renderTarget = target.Target.Get();
        context->OMSetRenderTargets(1, &renderTarget, nullptr);
        D3D11_VIEWPORT viewport{0, 0, static_cast<float>(target.Width()), static_cast<float>(target.Height()), 0, 1};
        context->RSSetViewports(1, &viewport);
        context->RSSetState(rasterizer_.Get());
        context->OMSetDepthStencilState(depth_.Get(), 0);
        context->OMSetBlendState(blend_.Get(), nullptr, ~0u);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex_.Get(), nullptr, 0);
        context->PSSetShader(pixel_.Get(), nullptr, 0);
        context->UpdateSubresource(constants_.Get(), 0, nullptr, constants.data(), 0, 0);
        auto *buffer = constants_.Get();
        context->PSSetConstantBuffers(0, 1, &buffer);
        if (!views.empty())
            context->PSSetShaderResources(0, static_cast<UINT>(views.size()), views.data());
        if (!samplers.empty())
            context->PSSetSamplers(0, static_cast<UINT>(samplers.size()), samplers.data());
        context->Draw(3, 0);
        context->ClearState();
    }
}
