#include "Pipeline/NativeForwardRenderer.h"
#include "Pipeline/NativeDeferredRenderer.h"
#include "Lighting/NativeSkybox.h"
#include "Common/RenderMath.h"
#include <algorithm>
#include <map>
#include <tuple>
#include <stdexcept>

namespace hs2
{
    namespace
    {
        std::unordered_map<std::string, float> Scalars(const GpuMaterialInstance &m)
        {
            std::unordered_map<std::string, float> result;
            for (const auto &[k, v] : m.Values)
                if (v.size() == 1)
                    result[k] = v[0];
            return result;
        }

        void LightValues(ShaderValues &values, const NativeDirectionalLight &light)
        {
            values["_WorldSpaceLightPos0"] = Values(V4(light.Direction, 0));
            values["_LightColor0"] = Values(V4(light.Color, light.Intensity));
            values["unity_LightColor"] = Values(V4(light.Color, 1));
        }

        void Texel(ShaderValues &values, const std::string &name, const GpuTexture &texture)
        {
            values[name + "_TexelSize"] = {1.f / texture.Width(), 1.f / texture.Height(),
                                           static_cast<float>(texture.Width()), static_cast<float>(texture.Height())};
        }
    }

    struct NativeForwardRenderer::State
    {
        GraphicsDevice &Graphics;
        NativeCharacterScene &Scene;
        GpuCharacterAppearance &Appearance;
        GpuMaterialComposer &Composer;
        std::shared_ptr<GpuTexture> Reflection;
        NativeSkybox Skybox;
        NativeBuiltinTextures Builtin;
        NativeDirectionalShadows Shadows;
        GpuVolume Probe;
        NativeDeferredRenderer Deferred;
        NativeGpuMeshDeformer Deformer;
        NativeDepthNormals DepthNormals;
        std::map<std::tuple<const GpuMaterialInstance *, const NativeShaderPass *, bool, bool>,
                 std::unique_ptr<NativeMaterialPass>>
            Passes;
        std::map<std::tuple<const GpuMaterialInstance *, bool, bool>, std::unique_ptr<NativeMaterialPass>> DepthPasses;
        std::unordered_map<std::string, std::unique_ptr<GpuTexture>> Grabs;
        std::unique_ptr<GpuDepthSurface> MainDepth, CameraDepth;
        const GpuTexture *LastTarget = nullptr;
        std::optional<NativeDirectionalLight> PreviousLight;

        State(GraphicsDevice &g, NativeCharacterScene &s, GpuCharacterAppearance &a, GpuMaterialComposer &c,
              NativeGpuAssetStore &store)
            : Graphics(g), Scene(s), Appearance(a), Composer(c), Reflection(c.Texture("environment.reflection")),
              Skybox(g, c, s.Definition->Environment), Builtin(g),
              Shadows(g, store, c, s.Definition->Environment.Quality.Float("shadowNearPlaneOffset", 2),
                      s.Definition->Environment.DhhEnabled
                          ? 2
                          : static_cast<int>(s.Definition->Environment.Quality.Float("anisotropicTextures", 2))),
              Probe(g, 1, 1, 1), Deferred(g, store, c, a, Reflection, {Probe.View.Get(), Probe.Sampler.Get()}),
              Deformer(g), DepthNormals(g, store, c)
        {
            const std::uint64_t zero = 0;
            g.Context()->UpdateSubresource(Probe.Texture.Get(), 0, nullptr, &zero, 8, 8);
        }
    };

    NativeForwardRenderer::NativeForwardRenderer(GraphicsDevice &g, NativeCharacterScene &s, GpuCharacterAppearance &a,
                                                 GpuMaterialComposer &c, NativeGpuAssetStore &store)
        : state_(std::make_unique<State>(g, s, a, c, store))
    {
    }

    NativeForwardRenderer::~NativeForwardRenderer() = default;

    const GpuDepthSurface &NativeForwardRenderer::CameraDepth() const
    {
        if (!state_->CameraDepth)
            throw std::logic_error("No camera depth has been drawn.");
        return *state_->CameraDepth;
    }

    std::vector<const GpuTexture *> NativeForwardRenderer::GBuffer() const
    {
        std::vector<const GpuTexture *> result;
        for (const auto &b : state_->Deferred.Buffers)
            result.push_back(b.get());
        return result;
    }

    const GpuTexture &NativeForwardRenderer::Reflections() const
    {
        if (!state_->Deferred.ReflectionBuffer)
            throw std::logic_error("No reflection frame has been drawn.");
        return *state_->Deferred.ReflectionBuffer;
    }

    void NativeForwardRenderer::Draw(GpuTexture &target, const NativeRenderCamera &camera,
                                     std::span<const float> ambientProbe, float time)
    {
        auto &s = *state_;
        auto &graphics = s.Graphics;
        graphics.VerifyThread();
        auto *context = graphics.Context();
        auto globals =
            Globals(s.Scene.Definition->Environment, camera, target.Width(), target.Height(), ambientProbe, time);
        auto lights = DirectionalLights(s.Scene.Definition->Environment, camera, KeyLightMultiplier);
        std::stable_sort(lights.begin(), lights.end(),
                         [](const auto &a, const auto &b)
                         {
                             return LengthSquared(a.Color) > LengthSquared(b.Color);
                         });
        if (!s.PreviousLight)
            s.PreviousLight = lights.empty() ? NativeDirectionalLight{} : lights.back();
        LightValues(globals, *s.PreviousLight);
        if (!s.MainDepth || s.MainDepth->Width != target.Width() || s.MainDepth->Height != target.Height())
        {
            auto main = std::make_unique<GpuDepthSurface>(graphics, target.Width(), target.Height());
            auto cameraDepth = std::make_unique<GpuDepthSurface>(graphics, target.Width(), target.Height());
            s.MainDepth = std::move(main);
            s.CameraDepth = std::move(cameraDepth);
        }
        context->ClearState();
        SetTarget(graphics, target.Target.Get(), target.Width(), target.Height(), s.MainDepth->Depth.Get());
        context->ClearDepthStencilView(s.MainDepth->Depth.Get(), D3D11_CLEAR_DEPTH | D3D11_CLEAR_STENCIL, 0, 0);
        context->ClearDepthStencilView(s.CameraDepth->Depth.Get(), D3D11_CLEAR_DEPTH | D3D11_CLEAR_STENCIL, 0, 0);
        if (!UseGameBackground)
        {
            auto bg = Background.value_or(Vector4{s.Scene.Definition->Environment.Background.data()});
            const float color[]{GpuMaterialComposer::Linear(bg.x), GpuMaterialComposer::Linear(bg.y),
                                GpuMaterialComposer::Linear(bg.z), 1};
            context->ClearRenderTargetView(target.Target.Get(), color);
        }
        else if (s.LastTarget != &target)
        {
            const float color[]{0, 0, 0, 0};
            context->ClearRenderTargetView(target.Target.Get(), color);
        }
        s.LastTarget = &target;

        struct DrawItem
        {
            NativeSceneRenderer *Renderer;
            GpuMaterialInstance *Material;
            int Submesh;
        };

        std::vector<DrawItem> opaque, transparent;
        std::unordered_map<NativeSceneRenderer *, Vector3> centers;
        for (auto *r : s.Appearance.Order)
            if (r->Visible() && r->HasGeometry())
            {
                centers[r] = r->Bounds().Center;
                const auto &materials = s.Appearance.Materials.at(r);
                for (size_t i = 0; i < materials.size(); ++i)
                    if (materials[i])
                    {
                        auto &list = materials[i]->Queue() <= 2500 ? opaque : transparent;
                        list.push_back({r, materials[i].get(), static_cast<int>(i)});
                    }
            }
        auto shaderKey = [](const DrawItem &d)
        {
            return std::make_tuple(d.Material->Queue(), LowerAscii(RenderPathKey(d.Material->Shader->SourceBundle)),
                                   d.Material->Shader->PathId);
        };
        std::reverse(opaque.begin(), opaque.end());
        std::reverse(transparent.begin(), transparent.end());
        std::stable_sort(opaque.begin(), opaque.end(),
                         [&](const auto &a, const auto &b)
                         {
                             return shaderKey(a) < shaderKey(b);
                         });
        std::vector<std::vector<DrawItem>> batches;
        std::unordered_map<std::string, size_t> batchIds;
        for (auto d : opaque)
        {
            std::vector<std::string> keys(d.Material->Keywords.begin(), d.Material->Keywords.end());
            std::sort(keys.begin(), keys.end());
            std::string key = std::to_string(d.Material->Queue()) + "|" +
                              RenderPathKey(d.Material->Shader->SourceBundle) + "|" + d.Material->Shader->Name + "|" +
                              std::to_string(d.Material->Shader->PathId);
            for (const auto &k : keys)
                key += '|' + k;
            auto [it, inserted] = batchIds.emplace(key, batches.size());
            if (inserted)
                batches.emplace_back();
            batches[it->second].push_back(d);
        }
        opaque.clear();
        for (auto &batch : batches)
            opaque.insert(opaque.end(), batch.begin(), batch.end());
        auto distance = [&](const DrawItem &d)
        {
            return LengthSquared(Sub(camera.Position, centers.at(d.Renderer)));
        };
        std::stable_sort(
            transparent.begin(), transparent.end(),
            [&](const auto &a, const auto &b)
            {
                return std::make_tuple(a.Material->Queue(), a.Renderer->Source.Settings.Float("m_SortingOrder"),
                                       -distance(a), LowerAscii(RenderPathKey(a.Material->Shader->SourceBundle)),
                                       a.Material->Shader->PathId, a.Submesh) <
                       std::make_tuple(b.Material->Queue(), b.Renderer->Source.Settings.Float("m_SortingOrder"),
                                       -distance(b), LowerAscii(RenderPathKey(b.Material->Shader->SourceBundle)),
                                       b.Material->Shader->PathId, b.Submesh);
            });
        std::vector<DrawItem> draws = opaque;
        draws.insert(draws.end(), transparent.begin(), transparent.end());
        DeformedStreams deformed, cameraDeformed;
        const auto cameraVP = MultiplyMatrix(
            camera.View(), camera.Projection(static_cast<float>(target.Width()) / target.Height(), false));
        for (auto d : draws)
            if (!deformed.contains(d.Renderer))
            {
                auto stream = s.Deformer.Deform(*d.Renderer);
                deformed[d.Renderer] = stream;
                if (d.Renderer->Bounds().Intersects(cameraVP))
                    cameraDeformed[d.Renderer] = stream;
            }
        auto drawDepth = [&](const DrawItem &draw, const ShaderValues &frameValues, bool casting)
        {
            auto &renderer = *draw.Renderer;
            auto &material = *draw.Material;
            auto source = std::find_if(material.Shader->Passes.begin(), material.Shader->Passes.end(),
                                       [](const auto &p)
                                       {
                                           return p.SubShader == 0 && LowerAscii(Tag(p, "LightMode")) == "shadowcaster";
                                       });
            if (source == material.Shader->Passes.end())
                return;
            bool twoSided = casting && renderer.ShadowCasting == 2;
            auto &native = s.DepthPasses[{&material, twoSided, casting}];
            if (!native)
            {
                auto keys = material.Keywords;
                for (const auto *k : {"SHADOWS_DEPTH", "UNITY_PASS_SHADOWCASTER", "LIGHTMAP_OFF", "DIRLIGHTMAP_OFF",
                                      "DYNAMICLIGHTMAP_OFF"})
                    keys.insert(k);
                D3D11_DEPTH_STENCIL_DESC depth{};
                depth.DepthEnable = TRUE;
                depth.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
                depth.DepthFunc = D3D11_COMPARISON_GREATER_EQUAL;
                depth.StencilEnable = TRUE;
                depth.StencilReadMask = 255;
                depth.StencilWriteMask = 255;
                depth.FrontFace = {D3D11_STENCIL_OP_KEEP, D3D11_STENCIL_OP_KEEP, D3D11_STENCIL_OP_REPLACE,
                                   D3D11_COMPARISON_ALWAYS};
                depth.BackFace = depth.FrontFace;
                native = std::make_unique<NativeMaterialPass>(
                    graphics, *material.Shader, *source, keys, Scalars(material), true,
                    twoSided ? std::optional(D3D11_CULL_NONE) : std::nullopt, !casting,
                    casting ? std::nullopt : std::optional(depth), casting ? std::nullopt : std::optional<UINT>(0));
            }
            auto values = MaterialValues(renderer, material, frameValues);
            if (!casting)
            {
                values["unity_LightShadowBias"] = {0, 0, 0, 0};
                values["_WorldSpaceLightPos0"] = {0, 1, 0, 0};
            }
            ShaderTextures textures;
            ShaderResources views;
            std::vector<std::shared_ptr<GpuTexture>> retained;
            for (const auto &binding : native->TextureBindings())
            {
                if (auto view = s.Builtin.TryView(binding.Name, casting || camera.BottomOrigin))
                {
                    views[binding.Name] = *view;
                    continue;
                }
                auto texture = s.Composer.ResolveTexture(material, binding.Name);
                retained.push_back(texture);
                textures[binding.Name] = texture.get();
                Texel(values, binding.Name, *texture);
            }
            native->Draw(*renderer.Mesh, std::min(draw.Submesh, static_cast<int>(renderer.Mesh->Submeshes.size()) - 1),
                         values, textures, nullptr, views, nullptr, &deformed.at(&renderer), nullptr,
                         GeometryMirrored(renderer));
        };
        s.Deferred.Draw(target, camera, *s.MainDepth, *s.CameraDepth, globals, lights, s.Shadows, cameraDeformed,
                        [&](const GpuDepthSurface &shared)
                        {
                            context->ClearState();
                            SetTarget(graphics, s.Deferred.Buffers[0]->Target.Get(), target.Width(), target.Height(),
                                      shared.Depth.Get());
                            for (auto d : draws)
                                if (cameraDeformed.contains(d.Renderer) && d.Material->Queue() <= 2500 &&
                                    d.Renderer->ShadowCasting != 3 && !NativeDeferredRenderer::Supports(*d.Material))
                                    drawDepth(d, globals, false);
                            context->ClearState();
                            context->CopyResource(s.CameraDepth->Texture.Get(), shared.Texture.Get());
                            s.Shadows.Render(camera, target.Width(), target.Height(), lights, *s.CameraDepth,
                                             [&](const NativeShadowCamera &shadow)
                                             {
                                                 auto values = globals;
                                                 values["unity_MatrixVP"] =
                                                     MatrixValues(MultiplyMatrix(shadow.View, shadow.Projection));
                                                 values["unity_MatrixV"] = MatrixValues(shadow.View);
                                                 values["_ProjectionParams"] = {-1, camera.Near, camera.Far,
                                                                                1 / camera.Far};
                                                 values["unity_LightShadowBias"] = Values(shadow.Bias);
                                                 values["_WorldSpaceLightPos0"] = Values(V4(shadow.Light.Direction, 0));
                                                 for (auto d : draws)
                                                     if (d.Renderer->ShadowCasting != 0 &&
                                                         (shadow.Light.Mask & (1u << d.Renderer->Node->Layer)) != 0)
                                                         drawDepth(d, values, true);
                                             });
                        });
        SetTarget(graphics, target.Target.Get(), target.Width(), target.Height(), s.MainDepth->Depth.Get());
        bool opaqueComplete = false;
        std::unordered_set<std::string> captured;
        auto finishOpaque = [&]
        {
            opaqueComplete = true;
            context->ClearState();
            s.DepthNormals.Draw(target.Width(), target.Height(), *s.MainDepth, globals, s.Appearance, cameraDeformed);
            if (UseGameBackground)
                s.Skybox.Draw(target, camera, s.MainDepth->Depth.Get());
            if (AfterGeometry)
                AfterGeometry(camera, *s.MainDepth, *s.CameraDepth, cameraDeformed);
            if (AfterOpaque)
                AfterOpaque(*s.CameraDepth, target, camera);
            SetTarget(graphics, target.Target.Get(), target.Width(), target.Height(), s.MainDepth->Depth.Get());
        };

        struct Scheduled
        {
            DrawItem Draw;
            bool Add;
            size_t Start, End;
        };

        std::vector<Scheduled> scheduled;
        std::vector<std::vector<DrawItem>> groups;
        using GroupKey = std::tuple<int, float, float, NativeSceneRenderer *, int>;
        std::map<GroupKey, size_t> groupIds;
        for (auto d : draws)
            if (cameraDeformed.contains(d.Renderer) && d.Renderer->ShadowCasting != 3 &&
                !NativeDeferredRenderer::Supports(*d.Material))
            {
                const bool trans = d.Material->Queue() > 2500,
                           multi = trans && s.Appearance.Materials.at(d.Renderer).size() > 1;
                GroupKey key{d.Material->Queue(), trans ? d.Renderer->Source.Settings.Float("m_SortingOrder") : 0,
                             trans ? distance(d) : 0, multi ? d.Renderer : nullptr, multi ? d.Submesh : -1};
                auto [it, inserted] = groupIds.emplace(key, groups.size());
                if (inserted)
                    groups.emplace_back();
                groups[it->second].push_back(d);
            }
        for (const auto &group : groups)
        {
            std::vector<DrawItem> pending;
            auto flush = [&]
            {
                for (bool add : {false, true})
                    for (auto d : pending)
                        scheduled.push_back({d, add, 0, SIZE_MAX});
                pending.clear();
            };
            for (auto d : group)
            {
                std::vector<const NativeShaderPass *> passes;
                std::vector<size_t> baseIndices;
                bool untagged = false;
                for (const auto &p : d.Material->Shader->Passes)
                    if (p.SubShader == 0)
                    {
                        const auto mode = LowerAscii(Tag(p, "LightMode"));
                        if (mode == "forwardbase")
                            baseIndices.push_back(passes.size());
                        if (p.Metadata.Get("m_Type").Number == 0 && mode.empty())
                            untagged = true;
                        passes.push_back(&p);
                    }
                if (baseIndices.size() > 1)
                {
                    flush();
                    for (size_t i = 0; i < baseIndices.size(); ++i)
                        for (bool add : {false, true})
                            scheduled.push_back({d, add, i == 0 ? 0 : baseIndices[i],
                                                 i + 1 < baseIndices.size() ? baseIndices[i + 1] : passes.size()});
                }
                else if (untagged)
                {
                    flush();
                    for (bool add : {false, true})
                        scheduled.push_back({d, add, 0, SIZE_MAX});
                }
                else
                    pending.push_back(d);
            }
            flush();
        }
        for (const auto &item : scheduled)
        {
            auto &renderer = *item.Draw.Renderer;
            auto &material = *item.Draw.Material;
            if (!opaqueComplete && material.Queue() > 2500)
                finishOpaque();
            auto values = MaterialValues(renderer, material, globals);
            if (item.Add)
            {
                for (const auto *k :
                     {"unity_SHAr", "unity_SHAg", "unity_SHAb", "unity_SHBr", "unity_SHBg", "unity_SHBb"})
                    values[k] = {0, 0, 0, 0};
                values["unity_SHC"] = {0, 0, 0, 1};
            }
            std::vector<size_t> eligible;
            for (size_t i = 0; i < lights.size(); ++i)
                if ((lights[i].Mask & (1u << renderer.Node->Layer)) != 0)
                    eligible.push_back(i);
            size_t passIndex = 0;
            for (const auto &pass : material.Shader->Passes)
            {
                if (pass.SubShader != 0)
                    continue;
                const auto index = passIndex++;
                if (index < item.Start || index >= item.End)
                    continue;
                int type = static_cast<int>(pass.Metadata.Get("m_Type").Number);
                if (type == 2)
                {
                    if (item.Add)
                        continue;
                    auto name = pass.Metadata.String("m_TextureName");
                    const bool unnamed = name.empty();
                    if (unnamed)
                        name = "_GrabTexture";
                    if (unnamed || captured.insert(name).second)
                    {
                        auto &grab = s.Grabs[name];
                        D3D11_TEXTURE2D_DESC source{};
                        target.Texture->GetDesc(&source);
                        if (grab)
                        {
                            D3D11_TEXTURE2D_DESC previous{};
                            grab->Texture->GetDesc(&previous);
                            if (previous.Width != source.Width || previous.Height != source.Height ||
                                previous.Format != source.Format)
                                grab.reset();
                        }
                        if (!grab)
                            grab = std::make_unique<GpuTexture>(graphics, target.Width(), target.Height(), true,
                                                                source.Format, false);
                        context->ClearState();
                        context->CopyResource(grab->Texture.Get(), target.Texture.Get());
                        SetTarget(graphics, target.Target.Get(), target.Width(), target.Height(),
                                  s.MainDepth->Depth.Get());
                    }
                    continue;
                }
                if (type != 0)
                    throw std::runtime_error("Unresolved material UsePass: " + material.Shader->Name);
                auto mode = LowerAscii(Tag(pass, "LightMode"));
                bool additive = mode == "forwardadd";
                if ((!additive && mode != "forwardbase" && !mode.empty()) || item.Add != additive)
                    continue;
                std::vector<size_t> selected;
                if (additive)
                {
                    if (eligible.size() > 1)
                        selected.assign(eligible.begin() + 1, eligible.end());
                }
                else
                    selected.push_back(eligible.empty() ? SIZE_MAX : eligible.front());
                for (auto lightIndex : selected)
                {
                    auto light = lightIndex == SIZE_MAX ? NativeDirectionalLight{} : lights[lightIndex];
                    if (!mode.empty())
                        s.PreviousLight = light;
                    LightValues(values, *s.PreviousLight);
                    bool shadowed = !mode.empty() && material.Queue() <= 2500 && renderer.ReceiveShadows &&
                                    light.ShadowStrength > 0;
                    if (shadowed)
                        for (const auto *name : {"_LightShadowData", "unity_ShadowFadeCenterAndType"})
                            values[name] = s.Shadows.Uniforms(lightIndex).at(name);
                    auto &native = s.Passes[{&material, &pass, additive, shadowed}];
                    if (!native)
                    {
                        auto keys = material.Keywords;
                        for (const auto *k : {"DIRECTIONAL", "LIGHTMAP_OFF", "DIRLIGHTMAP_OFF", "DYNAMICLIGHTMAP_OFF"})
                            keys.insert(k);
                        if (!additive)
                            keys.insert("LIGHTPROBE_SH");
                        if (shadowed)
                            keys.insert("SHADOWS_SCREEN");
                        native = std::make_unique<NativeMaterialPass>(graphics, *material.Shader, pass, keys,
                                                                      Scalars(material), true);
                    }
                    ShaderTextures textures;
                    ShaderResources views;
                    std::vector<std::shared_ptr<GpuTexture>> retained;
                    for (const auto &binding : native->TextureBindings())
                    {
                        const auto &name = binding.Name;
                        if (auto view = s.Builtin.TryView(name, camera.BottomOrigin))
                        {
                            views[name] = *view;
                            continue;
                        }
                        if (name == "unity_ProbeVolumeSH")
                        {
                            views[name] = {s.Probe.View.Get(), s.Probe.Sampler.Get()};
                            continue;
                        }
                        if (name == "_ShadowMapTexture" && shadowed)
                        {
                            bool probe = std::any_of(native->TextureBindings().begin(), native->TextureBindings().end(),
                                                     [](const auto &t)
                                                     {
                                                         return t.Name == "unity_ProbeVolumeSH";
                                                     });
                            if (probe)
                                views[name] = s.Shadows.ScreenView(lightIndex);
                            else
                                textures[name] = &s.Shadows.Screen(lightIndex);
                            continue;
                        }
                        bool assigned = material.DefaultTextures.contains(name) || material.Textures.contains(name) ||
                                        material.TextureOverrides.contains(name);
                        if (!assigned)
                            if (auto texture = s.Composer.TryGlobalTexture(name))
                            {
                                retained.push_back(texture);
                                textures[name] = texture.get();
                                Texel(values, name, *texture);
                                continue;
                            }
                        const GpuTexture *texture = nullptr;
                        if (name == "unity_SpecCube0" || name == "unity_SpecCube1")
                            texture = s.Reflection.get();
                        else if (auto it = s.Grabs.find(name); it != s.Grabs.end())
                            texture = it->second.get();
                        else if (assigned)
                        {
                            retained.push_back(s.Composer.ResolveTexture(material, name));
                            texture = retained.back().get();
                        }
                        else
                            throw std::runtime_error("Unbound frame resource: " + material.Shader->Name + "/" + name);
                        textures[name] = texture;
                        Texel(values, name, *texture);
                    }
                    native->Draw(*renderer.Mesh,
                                 std::min(item.Draw.Submesh, static_cast<int>(renderer.Mesh->Submeshes.size()) - 1),
                                 values, textures, nullptr, views, nullptr, &deformed.at(&renderer), nullptr,
                                 GeometryMirrored(renderer));
                }
            }
        }
        if (!opaqueComplete)
            finishOpaque();
        context->ClearState();
    }
}
