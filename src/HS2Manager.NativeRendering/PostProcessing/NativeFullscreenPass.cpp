#include "PostProcessing/NativeFullscreenPass.h"
#include <d3dcompiler.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <stdexcept>

namespace hs2
{
    void NativeRenderCamera::IncludeBounds(Vector3 center, Vector3 extents)
    {
        auto dx = center.x - Position.x, dy = center.y - Position.y, dz = center.z - Position.z;
        auto distance = std::sqrt(dx * dx + dy * dy + dz * dz);
        auto radius = std::sqrt(extents.x * extents.x + extents.y * extents.y + extents.z * extents.z);
        Far = std::max(100.f, distance + radius * 2 + 1.f);
    }

    Matrix4x4 NativeRenderCamera::View() const
    {
        Matrix4x4 result;
        DirectX::XMStoreFloat4x4(&result,
                                 DirectX::XMMatrixLookAtRH(DirectX::XMLoadFloat3(&Position),
                                                           DirectX::XMLoadFloat3(&Target), DirectX::XMLoadFloat3(&Up)) *
                                     DirectX::XMMatrixScaling(-1, 1, 1));
        return result;
    }

    Matrix4x4 NativeRenderCamera::Projection(float aspect, bool jittered) const
    {
        if (!std::isfinite(aspect) || aspect <= 0 || !std::isfinite(FieldOfView) || FieldOfView <= 0 ||
            FieldOfView >= 180 || Near <= 0 || Far <= Near)
            throw std::invalid_argument("Invalid preview camera projection.");
        Matrix4x4 result;
        DirectX::XMStoreFloat4x4(
            &result, DirectX::XMMatrixPerspectiveFovRH(FieldOfView * (DirectX::XM_PI / 180), aspect, Near, Far));
        result._33 = -(-(Far + Near) / (Far - Near) + 1) * .5f;
        result._43 = Far * Near / (Far - Near);
        if (jittered)
        {
            result._31 += ProjectionJitter.x;
            result._32 += ProjectionJitter.y;
        }
        if (BottomOrigin)
        {
            result._22 = -result._22;
            result._32 = -result._32;
        }
        return result;
    }

    Matrix4x4 NativeRenderCamera::World() const
    {
        auto view = View();
        Matrix4x4 result;
        DirectX::XMStoreFloat4x4(&result, DirectX::XMMatrixInverse(nullptr, DirectX::XMLoadFloat4x4(&view) *
                                                                                DirectX::XMMatrixScaling(1, 1, -1)));
        return result;
    }

    std::vector<float> MatrixValues(const Matrix4x4 &matrix)
    {
        std::vector<float> result(16);
        std::memcpy(result.data(), &matrix, 64);
        return result;
    }

    namespace
    {
        const NativeShaderPass &Pass(const NativeShaderAsset &shader, int index, int subShader)
        {
            for (const auto &pass : shader.Passes)
                if (pass.SubShader == subShader && index-- == 0)
                    return pass;
            throw std::out_of_range("Shader pass index is absent.");
        }

        GpuMeshData FullscreenMesh(bool blit)
        {
            GpuMeshData mesh;
            mesh.Name = blit ? "engine blit quad" : "fullscreen triangle";
            mesh.VertexCount = blit ? 4 : 3;
            mesh.Channels = {{0, 0, 0, 0, 3}};
            if (blit)
                mesh.Channels.push_back({4, 0, 12, 0, 2});
            mesh.StreamOffsets = {0};
            mesh.StreamStrides = {blit ? 20 : 12};
            std::vector<float> data =
                blit ? std::vector<float>{0, 0, .1f, 0, 0, 0, 1, .1f, 0, 1, 1, 1, .1f, 1, 1, 1, 0, .1f, 1, 0}
                     : std::vector<float>{-1, -1, 0, -1, 3, 0, 3, -1, 0};
            mesh.VertexBytes.resize(data.size() * 4);
            std::memcpy(mesh.VertexBytes.data(), data.data(), mesh.VertexBytes.size());
            mesh.IndexSize = 2;
            mesh.IndexBytes = blit ? std::vector<std::uint8_t>{0, 0, 1, 0, 2, 0, 0, 0, 2, 0, 3, 0}
                                   : std::vector<std::uint8_t>{0, 0, 1, 0, 2, 0};
            mesh.Submeshes = {{0, blit ? 6 : 3, 0, 0}};
            return mesh;
        }

        const GpuMeshData Triangle = FullscreenMesh(false), BlitQuad = FullscreenMesh(true);
    }

    NativeFullscreenPass::NativeFullscreenPass(GraphicsDevice &g, const NativeShaderAsset &shader, int index,
                                               const std::unordered_set<std::string> &keywords, bool blit,
                                               int subShader, const std::unordered_map<std::string, float> &properties,
                                               std::optional<D3D11_DEPTH_STENCIL_DESC> depth,
                                               std::optional<UINT> stencil)
        : graphics_(g), engineBlit_(blit),
          pass_(g, shader, Pass(shader, index, subShader), keywords, properties, false,
                blit ? std::optional<D3D11_CULL_MODE>(D3D11_CULL_NONE) : std::nullopt, false, depth, stencil)
    {
    }

    void NativeFullscreenPass::Draw(GpuTexture &target, const ShaderValues &values, const ShaderTextures &textures,
                                    const ShaderResources &views, const NativeRenderCamera *camera,
                                    ID3D11DepthStencilView *depth)
    {
        graphics_.VerifyThread();
        auto *ctx = graphics_.Context();
        ctx->ClearState();
        auto *rt = target.Target.Get();
        ctx->OMSetRenderTargets(1, &rt, depth);
        D3D11_VIEWPORT viewport{0, 0, static_cast<float>(target.Width()), static_cast<float>(target.Height()), 0, 1};
        ctx->RSSetViewports(1, &viewport);
        auto constants = values;
        constants["_UVTransform"] = {1, -1, 0, 1};
        if (engineBlit_)
        {
            constants["unity_ObjectToWorld"] = MatrixValues(IdentityMatrix());
            constants["unity_MatrixVP"] = {2, 0, 0, 0, 0, -2, 0, 0, 0, 0, .00990099f, 0, -1, 1, .990099f, 1};
        }
        for (const auto &[name, t] : textures)
        {
            if (!t)
                throw std::invalid_argument("Null fullscreen texture.");
            constants[name + "_TexelSize"] = {1.f / t->Width(), 1.f / t->Height(), static_cast<float>(t->Width()),
                                              static_cast<float>(t->Height())};
        }
        std::optional<NativeDeformedVertices> rays;
        if (camera)
        {
            if (!engineBlit_)
                throw std::invalid_argument("Camera rays require an engine blit layout.");
            const auto p = camera->Projection(static_cast<float>(target.Width()) / target.Height());
            auto ray = [&](float u, float v)
            {
                return Vector3{(2 * u - 1 + p._31) / p._11, (1 - 2 * v + p._32) / p._22, 1};
            };
            rays = NativeDeformedVertices{{{0, 0, .1f}, {0, 1, .1f}, {1, 1, .1f}, {1, 0, .1f}},
                                          {ray(0, 0), ray(0, 1), ray(1, 1), ray(1, 0)},
                                          std::vector<Vector4>(4)};
        }
        pass_.Draw(engineBlit_ ? BlitQuad : Triangle, 0, constants, textures, rays ? &*rays : nullptr, views);
        ctx->ClearState();
    }

    GpuCopyPass::GpuCopyPass(GraphicsDevice &g) : graphics_(g)
    {
        g.VerifyThread();
        constexpr char source[] = R"hlsl(
Texture2D sourceTexture : register(t0);
SamplerState sourceSampler : register(s0);

struct Vertex
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

Vertex VS(uint id : SV_VertexID)
{
    Vertex o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.position = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float4 PS(Vertex i) : SV_Target
{
    return sourceTexture.Sample(sourceSampler, i.uv);
}

float4 PSFlip(Vertex i) : SV_Target
{
    return sourceTexture.Sample(sourceSampler, float2(i.uv.x, 1 - i.uv.y));
}

float4 PSFlipVelocity(Vertex i) : SV_Target
{
    float4 v = PSFlip(i);
    v.y = -v.y;
    return v;
}
)hlsl";
        auto compile = [&](const char *entry, const char *target)
        {
            ComPtr<ID3DBlob> code, error;
            auto hr = D3DCompile(source, sizeof(source) - 1, "NativeCopy", nullptr, nullptr, entry, target, 0, 0, &code,
                                 &error);
            if (FAILED(hr))
                throw std::runtime_error(error ? static_cast<const char *>(error->GetBufferPointer())
                                               : "Copy shader compilation failed.");
            return code;
        };
        auto v = compile("VS", "vs_5_0");
        CheckHResult(g.Device()->CreateVertexShader(v->GetBufferPointer(), v->GetBufferSize(), nullptr, &vertex_),
                     "Create copy vertex shader");
        auto pixel = [&](const char *entry, auto &shader)
        {
            auto p = compile(entry, "ps_5_0");
            CheckHResult(g.Device()->CreatePixelShader(p->GetBufferPointer(), p->GetBufferSize(), nullptr, &shader),
                         "Create copy pixel shader");
        };
        pixel("PS", pixel_);
        pixel("PSFlip", flipped_);
        pixel("PSFlipVelocity", velocity_);
        auto s = DefaultSampler();
        CheckHResult(g.Device()->CreateSamplerState(&s, &linear_), "Create copy sampler");
        s.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
        CheckHResult(g.Device()->CreateSamplerState(&s, &point_), "Create point copy sampler");
        D3D11_RASTERIZER_DESC r{};
        r.FillMode = D3D11_FILL_SOLID;
        r.CullMode = D3D11_CULL_NONE;
        r.DepthClipEnable = TRUE;
        CheckHResult(g.Device()->CreateRasterizerState(&r, &rasterizer_), "Create copy rasterizer");
        D3D11_DEPTH_STENCIL_DESC d{};
        d.DepthFunc = D3D11_COMPARISON_ALWAYS;
        CheckHResult(g.Device()->CreateDepthStencilState(&d, &depth_), "Create copy depth state");
        D3D11_BLEND_DESC b{};
        for (auto &t : b.RenderTarget)
        {
            t.SrcBlend = t.SrcBlendAlpha = D3D11_BLEND_ONE;
            t.DestBlend = t.DestBlendAlpha = D3D11_BLEND_ZERO;
            t.BlendOp = t.BlendOpAlpha = D3D11_BLEND_OP_ADD;
            t.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        }
        CheckHResult(g.Device()->CreateBlendState(&b, &blend_), "Create copy blend state");
    }

    void GpuCopyPass::Copy(const GpuTexture &source, GpuTexture &target, bool flip, bool velocity)
    {
        CopyView(source.View.Get(), target, flip, velocity);
    }

    void GpuCopyPass::CopyView(ID3D11ShaderResourceView *source, GpuTexture &target, bool flip, bool velocity,
                               bool point)
    {
        graphics_.VerifyThread();
        auto *ctx = graphics_.Context();
        ctx->ClearState();
        auto *rt = target.Target.Get();
        ctx->OMSetRenderTargets(1, &rt, nullptr);
        D3D11_VIEWPORT viewport{0, 0, static_cast<float>(target.Width()), static_cast<float>(target.Height()), 0, 1};
        ctx->RSSetViewports(1, &viewport);
        ctx->RSSetState(rasterizer_.Get());
        ctx->OMSetDepthStencilState(depth_.Get(), 0);
        ctx->OMSetBlendState(blend_.Get(), nullptr, ~0u);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(vertex_.Get(), nullptr, 0);
        ctx->PSSetShader(velocity ? velocity_.Get() : flip ? flipped_.Get() : pixel_.Get(), nullptr, 0);
        auto *sampler = point ? point_.Get() : linear_.Get();
        ctx->PSSetSamplers(0, 1, &sampler);
        ctx->PSSetShaderResources(0, 1, &source);
        ctx->Draw(3, 0);
        ctx->ClearState();
    }
}
