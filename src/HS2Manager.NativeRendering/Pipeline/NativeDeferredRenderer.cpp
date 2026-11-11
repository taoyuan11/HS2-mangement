#include "Pipeline/NativeDeferredRenderer.h"
#include "Common/RenderMath.h"
#include <algorithm>
#include <set>
#include <stdexcept>

namespace hs2
{
    const NativeShaderPass *NativeDeferredRenderer::Pass(const GpuMaterialInstance &material)
    {
        for (const auto &pass : material.Shader->Passes)
            if (pass.SubShader == 0 && LowerAscii(NativeForwardRenderer::Tag(pass, "LightMode")) == "deferred")
                return &pass;
        return nullptr;
    }

    bool NativeDeferredRenderer::Supports(const GpuMaterialInstance &material)
    {
        return material.Queue() <= 2500 && Pass(material);
    }

    NativeDeferredRenderer::NativeDeferredRenderer(GraphicsDevice &g, NativeGpuAssetStore &assets,
                                                   GpuMaterialComposer &composer, GpuCharacterAppearance &appearance,
                                                   std::shared_ptr<GpuTexture> reflection, ShaderResource probe)
        : graphics_(g), composer_(composer), appearance_(appearance), reflection_(std::move(reflection)), probe_(probe),
          lightShader_(composer.Shader(assets.GameResource("Hidden/Internal-DeferredShading", 48))),
          reflectionShader_(composer.Shader(assets.GameResource("Hidden/Internal-DeferredReflections", 48)))
    {
    }

    D3D11_DEPTH_STENCIL_DESC NativeDeferredRenderer::Stencil(bool write)
    {
        D3D11_DEPTH_STENCIL_DESC d{};
        d.DepthEnable = write;
        d.DepthWriteMask = write ? D3D11_DEPTH_WRITE_MASK_ALL : D3D11_DEPTH_WRITE_MASK_ZERO;
        d.DepthFunc = D3D11_COMPARISON_GREATER_EQUAL;
        d.StencilEnable = TRUE;
        d.StencilReadMask = 255;
        d.StencilWriteMask = write ? 255 : 0;
        d.FrontFace = {D3D11_STENCIL_OP_KEEP, D3D11_STENCIL_OP_KEEP,
                       write ? D3D11_STENCIL_OP_REPLACE : D3D11_STENCIL_OP_KEEP,
                       write ? D3D11_COMPARISON_ALWAYS : D3D11_COMPARISON_EQUAL};
        d.BackFace = d.FrontFace;
        return d;
    }

    int NativeDeferredRenderer::Group(const NativeSceneRenderer &renderer)
    {
        const auto key = std::make_pair(renderer.Node->Layer, renderer.ReceiveShadows);
        auto found = groups_.find(key);
        if (found != groups_.end())
            return found->second;
        const auto index = static_cast<int>(groups_.size() + 1);
        if (index > 255)
            throw std::runtime_error("Deferred light-mask groups exceed stencil capacity.");
        groups_[key] = index;
        return index;
    }

    void NativeDeferredRenderer::Resize(int width, int height)
    {
        if (stencilDepth_ && stencilDepth_->Width == width && stencilDepth_->Height == height)
            return;
        std::array<std::unique_ptr<GpuTexture>, 3> pending;
        constexpr DXGI_FORMAT formats[]{DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,
                                        DXGI_FORMAT_R10G10B10A2_UNORM};
        for (size_t i = 0; i < 3; ++i)
            pending[i] = std::make_unique<GpuTexture>(graphics_, width, height, true, formats[i], false);
        auto depth = std::make_unique<GpuDepthSurface>(graphics_, width, height);
        auto reflection =
            std::make_unique<GpuTexture>(graphics_, width, height, true, DXGI_FORMAT_R16G16B16A16_FLOAT, false);
        Buffers = std::move(pending);
        stencilDepth_ = std::move(depth);
        ReflectionBuffer = std::move(reflection);
    }

    void NativeDeferredRenderer::Draw(GpuTexture &target, const NativeRenderCamera &camera,
                                      const GpuDepthSurface &depth, const GpuDepthSurface &cameraDepth,
                                      const ShaderValues &globals, std::span<const NativeDirectionalLight> frameLights,
                                      NativeDirectionalShadows &shadows, const DeformedStreams &deformed,
                                      const std::function<void(const GpuDepthSurface &)> &prepare)
    {
        graphics_.VerifyThread();
        Resize(target.Width(), target.Height());
        auto *ctx = graphics_.Context();
        ctx->ClearState();
        ctx->CopyResource(stencilDepth_->Texture.Get(), depth.Texture.Get());
        ctx->ClearDepthStencilView(stencilDepth_->Depth.Get(), D3D11_CLEAR_STENCIL, 0, 0);
        const float clear[]{0, 0, 0, 0}, reflectionClear[]{0, 0, 0, 1};
        for (const auto &buffer : Buffers)
            ctx->ClearRenderTargetView(buffer->Target.Get(), clear);
        ctx->ClearRenderTargetView(ReflectionBuffer->Target.Get(), reflectionClear);
        ID3D11RenderTargetView *targets[]{Buffers[0]->Target.Get(), Buffers[1]->Target.Get(), Buffers[2]->Target.Get(),
                                          target.Target.Get()};
        ctx->OMSetRenderTargets(4, targets, stencilDepth_->Depth.Get());
        D3D11_VIEWPORT viewport{0, 0, static_cast<float>(target.Width()), static_cast<float>(target.Height()), 0, 1};
        ctx->RSSetViewports(1, &viewport);

        struct DrawItem
        {
            NativeSceneRenderer *Renderer;
            GpuMaterialInstance *Material;
            int Submesh;
        };

        std::vector<DrawItem> draws;
        for (auto *renderer : appearance_.Order)
            if (deformed.contains(renderer) && renderer->ShadowCasting != 3)
            {
                const auto &materials = appearance_.Materials.at(renderer);
                for (size_t i = 0; i < materials.size(); ++i)
                    if (materials[i] && Supports(*materials[i]))
                        draws.push_back({renderer, materials[i].get(), static_cast<int>(i)});
            }
        std::reverse(draws.begin(), draws.end());
        std::stable_sort(
            draws.begin(), draws.end(),
            [](const auto &a, const auto &b)
            {
                return std::make_tuple(a.Material->Queue(), LowerAscii(RenderPathKey(a.Material->Shader->SourceBundle)),
                                       a.Material->Shader->PathId) <
                       std::make_tuple(b.Material->Queue(), LowerAscii(RenderPathKey(b.Material->Shader->SourceBundle)),
                                       b.Material->Shader->PathId);
            });
        // 将相同变体分组，并保留各组首次出现的顺序。
        auto groupKey = [](const DrawItem &item)
        {
            std::vector<std::string> keywords(item.Material->Keywords.begin(), item.Material->Keywords.end());
            std::sort(keywords.begin(), keywords.end());
            std::string key = std::to_string(item.Material->Queue()) + "|" +
                              RenderPathKey(item.Material->Shader->SourceBundle) + "|" + item.Material->Shader->Name +
                              "|" + std::to_string(item.Material->Shader->PathId);
            for (const auto &keyword : keywords)
                key += '|' + keyword;
            return key;
        };
        std::vector<std::vector<DrawItem>> batches;
        std::unordered_map<std::string, size_t> batchIds;
        for (const auto &draw : draws)
        {
            const auto key = groupKey(draw);
            auto [it, inserted] = batchIds.emplace(key, batches.size());
            if (inserted)
                batches.emplace_back();
            batches[it->second].push_back(draw);
        }
        std::set<int> active;
        for (const auto &batch : batches)
            for (const auto &draw : batch)
            {
                auto &renderer = *draw.Renderer;
                auto &material = *draw.Material;
                const int group = Group(renderer);
                active.insert(group);
                auto key = std::make_pair(&material, group);
                auto &native = materials_[key];
                if (!native)
                {
                    auto keywords = material.Keywords;
                    for (const auto *keyword :
                         {"LIGHTPROBE_SH", "UNITY_HDR_ON", "LIGHTMAP_OFF", "DIRLIGHTMAP_OFF", "DYNAMICLIGHTMAP_OFF"})
                        keywords.insert(keyword);
                    std::unordered_map<std::string, float> floats;
                    for (const auto &[name, v] : material.Values)
                        if (v.size() == 1)
                            floats[name] = v[0];
                    native = std::make_unique<NativeMaterialPass>(graphics_, *material.Shader, *Pass(material),
                                                                  keywords, floats, true, std::nullopt, false,
                                                                  Stencil(true), static_cast<UINT>(group));
                }
                auto values = NativeForwardRenderer::MaterialValues(renderer, material, globals);
                ShaderTextures textures;
                ShaderResources views;
                std::vector<std::shared_ptr<GpuTexture>> retained;
                for (const auto &binding : native->TextureBindings())
                {
                    if (binding.Name == "unity_ProbeVolumeSH")
                    {
                        views[binding.Name] = probe_;
                        continue;
                    }
                    auto texture = (binding.Name == "unity_SpecCube0" || binding.Name == "unity_SpecCube1")
                                       ? reflection_
                                       : composer_.ResolveTexture(material, binding.Name);
                    retained.push_back(texture);
                    textures[binding.Name] = texture.get();
                    values[binding.Name + "_TexelSize"] = {1.f / texture->Width(), 1.f / texture->Height(),
                                                           static_cast<float>(texture->Width()),
                                                           static_cast<float>(texture->Height())};
                }
                native->Draw(*renderer.Mesh,
                             std::min(draw.Submesh, static_cast<int>(renderer.Mesh->Submeshes.size()) - 1), values,
                             textures, nullptr, views, nullptr, &deformed.at(&renderer), nullptr,
                             NativeForwardRenderer::GeometryMirrored(renderer));
            }
        ctx->ClearState();
        prepare(*stencilDepth_);
        ctx->ClearState();
        ctx->CopyResource(depth.Texture.Get(), stencilDepth_->Texture.Get());
        if (active.empty())
            return;
        auto screen = globals;
        screen["_LightAsQuad"] = {1};
        screen["unity_MatrixV"] = MatrixValues(IdentityMatrix());
        screen["_ProjectionParams"] = {-1, camera.Near, camera.Far, 1 / camera.Far};
        screen["unity_CameraToWorld"] = MatrixValues(camera.World());
        screen["unity_SpecCube1_ProbePosition"] = {0, 0, 0, 1};
        ShaderTextures resources{{"_CameraGBufferTexture0", Buffers[0].get()},
                                 {"_CameraGBufferTexture1", Buffers[1].get()},
                                 {"_CameraGBufferTexture2", Buffers[2].get()},
                                 {"unity_SpecCube0", reflection_.get()}};
        ShaderResources depthResources{{"_CameraDepthTexture", {cameraDepth.View.Get(), cameraDepth.Sampler.Get()}}};
        const std::unordered_map<std::string, float> blend{{"_SrcBlend", 1.f}, {"_DstBlend", 1.f}};
        for (int group : active)
        {
            auto &pass = reflections_[group];
            if (!pass)
                pass = std::make_unique<NativeFullscreenPass>(graphics_, *reflectionShader_, 0,
                                                              std::unordered_set<std::string>{}, true, 0, blend,
                                                              Stencil(false), static_cast<UINT>(group));
            pass->Draw(*ReflectionBuffer, screen, resources, depthResources, &camera,
                       stencilDepth_->ReadOnlyDepth.Get());
        }
        resources["_CameraReflectionsTexture"] = ReflectionBuffer.get();
        for (int group : active)
        {
            auto &pass = composites_[group];
            if (!pass)
                pass = std::make_unique<NativeFullscreenPass>(graphics_, *reflectionShader_, 1,
                                                              std::unordered_set<std::string>{"UNITY_HDR_ON"}, true, 0,
                                                              blend, Stencil(false), static_cast<UINT>(group));
            pass->Draw(target, screen, resources, depthResources, &camera, stencilDepth_->ReadOnlyDepth.Get());
        }
        for (const auto &[key, group] : groups_)
            if (active.contains(group))
                for (size_t index = 0; index < frameLights.size(); ++index)
                {
                    const auto &light = frameLights[index];
                    if ((light.Mask & (1u << key.first)) == 0)
                        continue;
                    const bool shadowed = key.second && light.ShadowStrength > 0;
                    auto &pass = lights_[{group, shadowed}];
                    if (!pass)
                    {
                        std::unordered_set<std::string> keywords{"DIRECTIONAL", "UNITY_HDR_ON"};
                        if (shadowed)
                            keywords.insert("SHADOWS_SCREEN");
                        pass = std::make_unique<NativeFullscreenPass>(graphics_, *lightShader_, 0, keywords, true, 0,
                                                                      blend, Stencil(false), static_cast<UINT>(group));
                    }
                    screen["_LightDir"] = {-light.Direction.x, -light.Direction.y, -light.Direction.z, 0};
                    screen["_LightColor"] = Values(V4(light.Color, 1));
                    if (shadowed)
                    {
                        resources["_ShadowMapTexture"] = &shadows.Screen(index, false);
                        for (const auto *name : {"_LightShadowData", "unity_ShadowFadeCenterAndType"})
                            screen[name] = shadows.Uniforms(index).at(name);
                    }
                    pass->Draw(target, screen, resources, depthResources, &camera, stencilDepth_->ReadOnlyDepth.Get());
                }
    }

    NativeDepthNormals::NativeDepthNormals(GraphicsDevice &g, NativeGpuAssetStore &assets,
                                           GpuMaterialComposer &composer)
        : graphics_(g), composer_(composer),
          shader_(composer.Shader(assets.GameResource("Hidden/Internal-DepthNormalsTexture", 48)))
    {
        GpuMaterialData source;
        source.Name = "camera depth normals";
        defaults_ = std::make_unique<GpuMaterialInstance>(source, shader_);
        defaults_->Values["_TreeInstanceScale"] = {0, 0, 0, 0};
        defaults_->Values["_SquashPlaneNormal"] = {0, 0, 0, 0};
        defaults_->Values["_SquashAmount"] = {0};
        defaults_->Values["_TerrainEngineBendTree"] = MatrixValues(IdentityMatrix());
        for (size_t sub = 0; sub < shader_->SubShaders.size(); ++sub)
        {
            std::string tag;
            for (const auto &t : shader_->SubShaders[sub].Get("m_Tags").Get("tags").Children)
                if (LowerAscii(t.String("first")) == "rendertype")
                    tag = t.String("second");
            if (!tag.empty())
            {
                auto &passes = replacements_[tag];
                for (const auto &pass : shader_->Passes)
                    if (pass.SubShader == sub)
                        passes.push_back(&pass);
            }
        }
    }

    const GpuTexture &NativeDepthNormals::Texture() const
    {
        if (!normals_)
            throw std::logic_error("No depth-normal frame has been drawn.");
        return *normals_;
    }

    void NativeDepthNormals::Draw(int width, int height, const GpuDepthSurface &depth, const ShaderValues &globals,
                                  const GpuCharacterAppearance &appearance, const DeformedStreams &deformed)
    {
        if (!normals_ || normals_->Width() != width || normals_->Height() != height)
            normals_ = std::make_unique<GpuTexture>(graphics_, width, height, true, DXGI_FORMAT_R8G8B8A8_UNORM, false);
        auto *context = graphics_.Context();
        context->ClearState();
        SetTarget(graphics_, normals_->Target.Get(), width, height, depth.Depth.Get());
        const float color[]{.5f, .5f, 1, 1};
        context->ClearRenderTargetView(normals_->Target.Get(), color);
        for (auto *renderer : appearance.Order)
            if (deformed.contains(renderer) && renderer->ShadowCasting != 3)
            {
                const auto &materials = appearance.Materials.at(renderer);
                for (size_t index = 0; index < materials.size(); ++index)
                {
                    const auto &material = materials[index];
                    if (!material || material->Queue() > 2500)
                        continue;
                    auto replacement = replacements_.find(material->Tag("RenderType"));
                    if (replacement == replacements_.end())
                        continue;
                    auto values = NativeForwardRenderer::MaterialValues(*renderer, *material, globals);
                    for (const auto &[name, v] : defaults_->Values)
                        values.try_emplace(name, v);
                    for (auto *source : replacement->second)
                    {
                        auto &pass = passes_[source];
                        if (!pass)
                            pass = std::make_unique<NativeMaterialPass>(graphics_, *shader_, *source,
                                                                        std::unordered_set<std::string>{},
                                                                        std::unordered_map<std::string, float>{}, true);
                        ShaderTextures textures;
                        std::vector<std::shared_ptr<GpuTexture>> retained;
                        for (const auto &binding : pass->TextureBindings())
                        {
                            auto texture = composer_.ResolveTexture(*material, binding.Name);
                            retained.push_back(texture);
                            textures[binding.Name] = texture.get();
                            values[binding.Name + "_TexelSize"] = {1.f / texture->Width(), 1.f / texture->Height(),
                                                                   static_cast<float>(texture->Width()),
                                                                   static_cast<float>(texture->Height())};
                            values.try_emplace(binding.Name + "_ST", std::vector<float>{1, 1, 0, 0});
                        }
                        pass->Draw(
                            *renderer->Mesh,
                            std::min(static_cast<int>(index), static_cast<int>(renderer->Mesh->Submeshes.size()) - 1),
                            values, textures, nullptr, {}, nullptr, &deformed.at(renderer), nullptr,
                            NativeForwardRenderer::GeometryMirrored(*renderer));
                    }
                }
            }
        context->ClearState();
    }
}
