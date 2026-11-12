#include "Creation/CreationViewport.h"
#include "Animation/SimulationMath.h"
#include <d3dcompiler.h>
#include <objbase.h>
#include <map>
#include <cmath>

namespace hs2::ui
{
    using namespace sim;
    using namespace creation;

    Vector3 CreationCamera::Right() const
    {
        return {std::cos(Yaw), 0, -std::sin(Yaw)};
    }

    Vector3 CreationCamera::Forward() const
    {
        return {std::sin(Yaw) * std::cos(Pitch), -std::sin(Pitch), std::cos(Yaw) * std::cos(Pitch)};
    }

    Vector3 CreationCamera::Up() const
    {
        return Unit(Cross(Forward(), Right()));
    }

    Vector2 CreationCamera::Project(Vector3 p, float w, float h) const
    {
        auto d = p - Center;
        return {w * .5f + Dot(d, Right()) * h / (2 * Scale), h * .5f - Dot(d, Up()) * h / (2 * Scale)};
    }

    std::pair<Vector3, Vector3> CreationCamera::Ray(Vector2 p, float w, float h) const
    {
        auto origin = Center + Right() * ((p.x - w * .5f) * 2 * Scale / h) + Up() * ((h * .5f - p.y) * 2 * Scale / h) -
                      Forward() * 50;
        return {origin, Forward()};
    }

    CreationViewport::CreationViewport(std::function<void(ComPtr<IDXGISwapChain2>)> ready,
                                       std::function<void(std::string)> error)
        : ready_(std::move(ready)), error_(std::move(error)), worker_(
                                                                  [this](std::stop_token t)
                                                                  {
                                                                      Run(t);
                                                                  })
    {
    }

    CreationViewport::~CreationViewport()
    {
        Stop();
    }

    void CreationViewport::Stop()
    {
        worker_.request_stop();
        wake_.notify_all();
        if (worker_.joinable())
            worker_.join();
    }

    void CreationViewport::Update(CreationView v, PreviewViewportSize s)
    {
        {
            std::lock_guard lock(mutex_);
            view_ = std::move(v);
            size_ = s;
            dirty_ = true;
        }
        wake_.notify_all();
    }

    void CreationViewport::Visible(bool value)
    {
        {
            std::lock_guard lock(mutex_);
            visible_ = value;
            dirty_ = true;
        }
        wake_.notify_all();
    }

    void CreationViewport::Run(std::stop_token token)
    {
        auto com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        try
        {
            GraphicsDevice graphics;
            auto device = graphics.Device();
            auto context = graphics.Context();
            const char *shader = R"(
cbuffer Camera : register(b0) { float4 right; float4 up; float4 forward; float4 center; };
cbuffer Surface : register(b1) { float4 tint; float4 flags; };
Texture2D paint : register(t0); Texture2D overlayPaint : register(t1); SamplerState linearSampler : register(s0);
struct V { float3 p:POSITION; float3 n:NORMAL; float4 c:COLOR; float2 uv:TEXCOORD; };
struct O { float4 p:SV_POSITION; float3 n:NORMAL; float4 c:COLOR; float2 uv:TEXCOORD; };
O vs(V i) { O o; float3 d=i.p-center.xyz; o.p=float4(dot(d,right.xyz)*right.w,dot(d,up.xyz)*up.w,(dot(d,forward.xyz)+50)/100,1); o.n=i.n; o.c=i.c; o.uv=i.uv; return o; }
float4 ps(O i):SV_TARGET {
    float l=length(i.n)<.01?1:.35+.65*abs(dot(normalize(i.n),normalize(float3(.4,.7,-.6))));
    float4 color=i.c*tint;
    if(flags.x>0) { float4 sample=paint.Sample(linearSampler,float2(i.uv.x,1-i.uv.y));
        if(flags.y>0) color.rgb=lerp(color.rgb,sample.rgb,sample.a);
        else { color*=sample; clip(color.a-.01); } }
    if(flags.z>0) { float4 sample=overlayPaint.Sample(linearSampler,float2(i.uv.x,1-i.uv.y)); color.rgb=lerp(color.rgb,sample.rgb,sample.a); }
    float checker=center.w>0 && length(i.n)>.01 ? .55+.45*fmod(floor(i.uv.x*16)+floor(i.uv.y*16)+2000,2):1;
    return float4(color.rgb*l*checker,1);
}
)";
            ComPtr<ID3DBlob> vs, ps, errors;
            CheckHResult(
                D3DCompile(shader, strlen(shader), nullptr, nullptr, nullptr, "vs", "vs_5_0", 0, 0, &vs, &errors),
                "创作顶点着色器");
            CheckHResult(
                D3DCompile(shader, strlen(shader), nullptr, nullptr, nullptr, "ps", "ps_5_0", 0, 0, &ps, &errors),
                "创作像素着色器");
            ComPtr<ID3D11VertexShader> vertex;
            ComPtr<ID3D11PixelShader> pixel;
            ComPtr<ID3D11InputLayout> layout;
            CheckHResult(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &vertex),
                         "创作顶点程序");
            CheckHResult(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &pixel),
                         "创作像素程序");
            D3D11_INPUT_ELEMENT_DESC elements[]{
                {"POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0},
                {"NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D11_INPUT_PER_VERTEX_DATA, 0},
                {"COLOR", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 24, D3D11_INPUT_PER_VERTEX_DATA, 0},
                {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 40, D3D11_INPUT_PER_VERTEX_DATA, 0}};
            CheckHResult(device->CreateInputLayout(elements, 4, vs->GetBufferPointer(), vs->GetBufferSize(), &layout),
                         "创作顶点格式");
            auto constants = graphics.CreateBuffer(64, D3D11_BIND_CONSTANT_BUFFER);
            auto surfaceConstants = graphics.CreateBuffer(32, D3D11_BIND_CONSTANT_BUFFER);
            D3D11_SAMPLER_DESC sampling{};
            sampling.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
            sampling.AddressU = sampling.AddressV = sampling.AddressW = D3D11_TEXTURE_ADDRESS_WRAP;
            sampling.MaxLOD = D3D11_FLOAT32_MAX;
            ComPtr<ID3D11SamplerState> sampler;
            CheckHResult(device->CreateSamplerState(&sampling, &sampler), "创作贴图采样器");
            D3D11_RASTERIZER_DESC raster{};
            raster.FillMode = D3D11_FILL_SOLID;
            raster.CullMode = D3D11_CULL_NONE;
            raster.DepthClipEnable = true;
            ComPtr<ID3D11RasterizerState> solid;
            CheckHResult(device->CreateRasterizerState(&raster, &solid), "创作光栅状态");
            D3D11_DEPTH_STENCIL_DESC ds{};
            ds.DepthEnable = true;
            ds.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
            ds.DepthFunc = D3D11_COMPARISON_LESS_EQUAL;
            ComPtr<ID3D11DepthStencilState> depthState;
            CheckHResult(device->CreateDepthStencilState(&ds, &depthState), "创作深度状态");
            ds.DepthEnable = false;
            ComPtr<ID3D11DepthStencilState> overlayState;
            device->CreateDepthStencilState(&ds, &overlayState);

            struct V
            {
                Vector3 P, N;
                Vector4 C;
                Vector2 U;
            };

            struct Buffer
            {
                std::string Hash;
                ComPtr<ID3D11Buffer> Data;
                UINT Count = 0;
            };

            std::map<std::string, Buffer> buffers;

            struct TextureBuffer
            {
                std::optional<PaintDocument> Snapshot;
                ComPtr<ID3D11Texture2D> Data;
                ComPtr<ID3D11ShaderResourceView> View;
            };

            std::map<std::string, TextureBuffer> textures;
            auto textureView = [&](const CreationTexture *texture) -> ID3D11ShaderResourceView *
            {
                ID3D11ShaderResourceView *resource = nullptr;
                if (texture)
                {
                    auto &cached = textures[texture->Id];
                    if (!cached.Snapshot || !texture->Image.SamePixels(*cached.Snapshot))
                    {
                        auto pixels = texture->Image.Composite();
                        if (!cached.Snapshot || cached.Snapshot->Width != texture->Image.Width ||
                            cached.Snapshot->Height != texture->Image.Height ||
                            cached.Snapshot->Srgb != texture->Image.Srgb)
                        {
                            cached.View.Reset();
                            cached.Data.Reset();
                            D3D11_TEXTURE2D_DESC desc{};
                            desc.Width = texture->Image.Width;
                            desc.Height = texture->Image.Height;
                            desc.MipLevels = desc.ArraySize = 1;
                            desc.Format =
                                texture->Image.Srgb ? DXGI_FORMAT_B8G8R8A8_UNORM_SRGB : DXGI_FORMAT_B8G8R8A8_UNORM;
                            desc.SampleDesc.Count = 1;
                            desc.Usage = D3D11_USAGE_DEFAULT;
                            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                            D3D11_SUBRESOURCE_DATA initial{pixels.data(), UINT(desc.Width * 4), 0};
                            CheckHResult(device->CreateTexture2D(&desc, &initial, &cached.Data), "创作贴图上传");
                            CheckHResult(device->CreateShaderResourceView(cached.Data.Get(), nullptr, &cached.View),
                                         "创作贴图视图");
                        }
                        else
                            context->UpdateSubresource(cached.Data.Get(), 0, nullptr, pixels.data(),
                                                       texture->Image.Width * 4, 0);
                        cached.Snapshot = texture->Image;
                    }
                    resource = cached.View.Get();
                }
                return resource;
            };
            auto material = [&](const CreationTexture *texture, Vector4 tint = Vector4{1, 1, 1, 1},
                                bool overlay = false, const CreationTexture *overlayTexture = nullptr)
            {
                ID3D11ShaderResourceView *resources[]{textureView(texture), textureView(overlayTexture)};
                float values[]{tint.x,
                               tint.y,
                               tint.z,
                               tint.w,
                               texture ? 1.f : 0.f,
                               overlay ? 1.f : 0.f,
                               overlayTexture ? 1.f : 0.f,
                               0};
                context->UpdateSubresource(surfaceConstants.Get(), 0, nullptr, values, 0, 0);
                auto cb = surfaceConstants.Get();
                context->PSSetConstantBuffers(1, 1, &cb);
                context->PSSetShaderResources(0, 2, resources);
            };
            std::shared_ptr<const CreationState> cachedState;
            std::map<std::string, std::string> meshHashes;
            std::unique_ptr<NativeSwapChainSurface> surface;
            std::unique_ptr<GpuTexture> frame;
            std::unique_ptr<GpuDepthSurface> depth;
            auto draw = [&](const std::string &id, const std::string &hash, const std::function<std::vector<V>()> &make,
                            D3D11_PRIMITIVE_TOPOLOGY topology)
            {
                auto &b = buffers[id];
                if (b.Hash != hash)
                {
                    auto data = make();
                    b.Data.Reset();
                    b.Count = static_cast<UINT>(data.size());
                    if (!data.empty())
                        b.Data = graphics.CreateBuffer(static_cast<UINT>(data.size() * sizeof(V)),
                                                       D3D11_BIND_VERTEX_BUFFER, data.data());
                    b.Hash = hash;
                }
                if (!b.Count)
                    return;
                UINT stride = sizeof(V), offset = 0;
                auto data = b.Data.Get();
                context->IASetVertexBuffers(0, 1, &data, &stride, &offset);
                context->IASetPrimitiveTopology(topology);
                context->Draw(b.Count, 0);
            };
            while (!token.stop_requested())
            {
                CreationView view;
                PreviewViewportSize size;
                {
                    std::unique_lock lock(mutex_);
                    wake_.wait(lock, token,
                               [&]
                               {
                                   return dirty_ && visible_;
                               });
                    if (token.stop_requested())
                        break;
                    view = view_;
                    size = size_;
                    dirty_ = false;
                }
                if (size.Width < 1 || size.Height < 1)
                    continue;
                if (cachedState != view.State)
                {
                    meshHashes.clear();
                    std::set<std::string> liveTextures;
                    if (view.State)
                    {
                        for (auto &a : view.State->Assets)
                        {
                            for (auto &o : a.Objects)
                            {
                                auto json = o.Mesh.ToJson().dump();
                                meshHashes[o.Id] = Sha256(Bytes(json.begin(), json.end()));
                            }
                            for (auto &t : a.Textures)
                                liveTextures.insert(t.Id);
                        }
                        for (auto &t : view.State->Overlays)
                            liveTextures.insert(t.Id);
                    }
                    std::erase_if(textures,
                                  [&](auto &entry)
                                  {
                                      return !liveTextures.contains(entry.first);
                                  });
                    std::erase_if(buffers,
                                  [&](auto &entry)
                                  {
                                      return entry.first.starts_with("object:") &&
                                             !meshHashes.contains(entry.first.substr(7, 32));
                                  });
                    cachedState = view.State;
                }
                if (!surface)
                {
                    surface = std::make_unique<NativeSwapChainSurface>(graphics, size);
                    ready_(surface->Acquire());
                }
                surface->Resize(size);
                if (!frame || frame->Width() != size.Width || frame->Height() != size.Height)
                {
                    frame = std::make_unique<GpuTexture>(graphics, size.Width, size.Height, true,
                                                         DXGI_FORMAT_B8G8R8A8_UNORM, false);
                    depth = std::make_unique<GpuDepthSurface>(graphics, size.Width, size.Height);
                }
                auto r = view.Camera.Right(), u = view.Camera.Up(), f = view.Camera.Forward(), c = view.Camera.Center;
                float data[]{r.x, r.y, r.z, float(size.Height) / size.Width / view.Camera.Scale,
                             u.x, u.y, u.z, 1 / view.Camera.Scale,
                             f.x, f.y, f.z, 0,
                             c.x, c.y, c.z, view.Checker ? 1.f : 0.f};
                context->UpdateSubresource(constants.Get(), 0, nullptr, data, 0, 0);
                auto cb = constants.Get();
                context->VSSetConstantBuffers(0, 1, &cb);
                context->PSSetConstantBuffers(0, 1, &cb);
                context->VSSetShader(vertex.Get(), nullptr, 0);
                context->PSSetShader(pixel.Get(), nullptr, 0);
                context->IASetInputLayout(layout.Get());
                auto samplingState = sampler.Get();
                context->PSSetSamplers(0, 1, &samplingState);
                material(nullptr);
                context->RSSetState(solid.Get());
                context->OMSetDepthStencilState(depthState.Get(), 0);
                D3D11_VIEWPORT vp{0, 0, float(size.Width), float(size.Height), 0, 1};
                context->RSSetViewports(1, &vp);
                auto target = frame->Target.Get();
                context->OMSetRenderTargets(1, &target, depth->Depth.Get());
                float clear[]{.075f, .085f, .1f, 1};
                context->ClearRenderTargetView(target, clear);
                context->ClearDepthStencilView(depth->Depth.Get(), D3D11_CLEAR_DEPTH, 1, 0);
                draw(
                    "grid", "grid",
                    [&]
                    {
                        std::vector<V> v;
                        for (int i = -20; i <= 20; ++i)
                        {
                            float p = float(i);
                            Vector4 col = i == 0 ? Vector4{.5f, .5f, .5f, 1} : Vector4{.18f, .2f, .23f, 1};
                            v.insert(v.end(), {{{-20, 0, p}, {}, col, {}},
                                               {{20, 0, p}, {}, col, {}},
                                               {{p, 0, -20}, {}, col, {}},
                                               {{p, 0, 20}, {}, col, {}}});
                        }
                        return v;
                    },
                    D3D11_PRIMITIVE_TOPOLOGY_LINELIST);
                if (view.Reference && view.ShowReference)
                    for (size_t i = 0; i < view.Reference->Meshes.size(); ++i)
                    {
                        auto &m = view.Reference->Meshes[i];
                        if (view.Clothing == 2 && m.Role == "clothes")
                            continue;
                        if (m.Role == "clothes" && view.State &&
                            std::any_of(view.State->Assets.begin(), view.State->Assets.end(),
                                        [&](const auto &a)
                                        {
                                            return a.Kind == "clothes" &&
                                                   a.TargetPath ==
                                                       "clothes.parts[" + std::to_string(m.ReferenceSlot) + "].id";
                                        }))
                            continue;
                        const CreationTexture *overlay = nullptr;
                        if (view.State)
                            for (auto &t : view.State->Overlays)
                            {
                                bool matches = OverlayMatches(m, t);
                                if (matches)
                                    overlay = &t;
                            }
                        if (m.Role == "clothes" && !overlay)
                            continue;
                        material(overlay, {1, 1, 1, 1}, true);
                        auto hash = std::to_string(reinterpret_cast<uintptr_t>(view.Reference.get()));
                        draw(
                            "ref" + std::to_string(i), hash,
                            [&]
                            {
                                std::vector<V> v;
                                for (auto n : m.Indices)
                                {
                                    auto &a = m.Vertices[n];
                                    v.push_back({a.Position, a.Normal, {.65f, .67f, .7f, 1}, a.Uv});
                                }
                                return v;
                            },
                            D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                    }
                if (view.State)
                    for (const auto &a : view.State->Assets)
                        for (const auto &o : a.Objects)
                        {
                            if (o.Hidden || (a.Kind == "clothes" && view.Clothing == 2) ||
                                o.HalfState != (a.Kind == "clothes" && a.IndependentHalfState && view.HalfState))
                                continue;
                            auto hash = meshHashes.at(o.Id) + view.Bone + std::to_string(view.Weights);
                            std::set<int> slots;
                            for (auto &[id, face] : o.Mesh.Faces)
                            {
                                slots.insert(face.Material);
                                (void)id;
                            }
                            for (int slot : slots)
                            {
                                const CreationTexture *texture = nullptr;
                                Vector4 tint{1, 1, 1, 1};
                                for (auto &t : a.Textures)
                                    if (t.MaterialSlot == slot && t.Kind == "material" &&
                                        (t.Property == "_MainTex" || t.Id == view.Texture))
                                    {
                                        if (!texture || t.Id == view.Texture)
                                            texture = &t;
                                    }
                                for (auto &value : a.MaterialValues)
                                    if (value.value("slot", 0) == slot &&
                                        (value.value("property", "") == "_Color" ||
                                         value.value("property", "") == "_Color1") &&
                                        value.at("value").is_array())
                                        tint = asset::Vector4Of(value.at("value"));
                                const CreationTexture *clothingOverlay = nullptr;
                                if (a.Kind == "clothes")
                                    for (auto &t : view.State->Overlays)
                                        if (t.Target == "clothes" &&
                                            a.TargetPath == "clothes.parts[" +
                                                                std::to_string(ClothingOverlaySlot(t.ClothingName)) +
                                                                "].id")
                                            clothingOverlay = &t;
                                material(view.Weights ? nullptr : texture, view.Weights ? Vector4{1, 1, 1, 1} : tint,
                                         false, view.Weights ? nullptr : clothingOverlay);
                                draw(
                                    "object:" + o.Id + ":" + std::to_string(slot), hash,
                                    [&]
                                    {
                                        auto display = o.Mesh;
                                        for (auto &[id, v] : display.Vertices)
                                        {
                                            float shade = 1 - v.Mask * .75f;
                                            v.Color = {v.Color.x * shade, v.Color.y * shade, v.Color.z * shade,
                                                       v.Color.w};
                                            (void)id;
                                        }
                                        auto mesh = display.Triangulate();
                                        std::vector<V> v;
                                        for (size_t i = 0; i < mesh.Indices.size(); ++i)
                                        {
                                            if (mesh.Materials[i / 3] != slot)
                                                continue;
                                            auto &p = mesh.Vertices[mesh.Indices[i]];
                                            Vector4 col = p.Color;
                                            if (view.Weights)
                                            {
                                                float w = 0;
                                                for (auto &weight : p.Weights)
                                                    if (weight.Bone == view.Bone)
                                                        w = weight.Weight;
                                                col = {w, .2f, 1 - w, 1};
                                            }
                                            v.push_back({p.Position, p.Normal, col, p.Uv});
                                        }
                                        return v;
                                    },
                                    D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                            }
                            if (o.Id == view.Object ||
                                (view.Selection.Mode == SelectionMode::Object && view.Objects.contains(o.Id)))
                            {
                                context->OMSetDepthStencilState(overlayState.Get(), 0);
                                material(nullptr);
                                auto selectionHash =
                                    hash + Json(view.Selection.Ids).dump() + std::to_string(int(view.Selection.Mode)) +
                                    std::to_string(view.Camera.Scale) + asset::VectorJson(view.Pivot).dump();
                                draw(
                                    "selection:" + o.Id, selectionHash,
                                    [&]
                                    {
                                        std::vector<V> v;
                                        auto ids = o.Mesh.SelectedVertices(view.Selection);
                                        for (auto &[key, e] : o.Mesh.Edges)
                                        {
                                            Vector4 col = ids.contains(e.A) && ids.contains(e.B)
                                                              ? Vector4{1, .55f, .12f, 1}
                                                              : Vector4{.2f, .3f, .35f, 1};
                                            v.push_back({o.Mesh.Vertices.at(e.A).Position, {}, col, {}});
                                            v.push_back({o.Mesh.Vertices.at(e.B).Position, {}, col, {}});
                                            (void)key;
                                        }
                                        auto center = view.Selection.Mode == SelectionMode::Object
                                                          ? view.Pivot
                                                          : o.Mesh.Center(view.Selection);
                                        float s = view.Camera.Scale * .2f;
                                        if (o.Id == view.Object)
                                            for (auto [axis, col] :
                                                 {std::pair{Vector3{s, 0, 0}, Vector4{1, .2f, .2f, 1}},
                                                  {{0, s, 0}, {.2f, 1, .2f, 1}},
                                                  {{0, 0, s}, {.2f, .5f, 1, 1}}})
                                            {
                                                v.push_back({center, {}, col, {}});
                                                v.push_back({center + axis, {}, col, {}});
                                            }
                                        return v;
                                    },
                                    D3D11_PRIMITIVE_TOPOLOGY_LINELIST);
                                context->OMSetDepthStencilState(depthState.Get(), 0);
                            }
                        }
                surface->Present(*frame);
            }
        }
        catch (const std::exception &e)
        {
            if (error_)
                error_(e.what());
        }
        if (SUCCEEDED(com))
            CoUninitialize();
    }
}
