#include "Materials/GpuCharacterAppearance.h"
#include <algorithm>
#include <cmath>
#include <stdexcept>

namespace hs2
{
    namespace
    {
        const Parameter &At(const Parameter &value, size_t index)
        {
            static const Parameter empty;
            return index < value.Children.size() ? value.Children[index] : empty;
        }

        const ComponentDefinition *Component(const CharacterPart &part, const char *type)
        {
            auto found = std::find_if(part.Components.begin(), part.Components.end(),
                                      [&](const auto &c)
                                      {
                                          return c.Type == type;
                                      });
            return found == part.Components.end() ? nullptr : &*found;
        }

        bool Named(const Parameter &list, const std::string &name)
        {
            return std::any_of(list.Children.begin(), list.Children.end(),
                               [&](const auto &p)
                               {
                                   return p.Text == name;
                               });
        }

        float Lerp(float a, float b, float value)
        {
            return a + (b - a) * std::clamp(value, 0.f, 1.f);
        }

        constexpr Vector4 White{1, 1, 1, 1}, Black{0, 0, 0, 1}, Zero{0, 0, 0, 0};

        bool SameExcept(const Parameter &a, const Parameter &b, std::initializer_list<std::string_view> ignored)
        {
            auto included = [&](auto const &field)
            {
                return std::find(ignored.begin(), ignored.end(), field.Name) == ignored.end();
            };
            return std::count_if(a.Children.begin(), a.Children.end(), included) ==
                       std::count_if(b.Children.begin(), b.Children.end(), included) &&
                   std::all_of(a.Children.begin(), a.Children.end(),
                               [&](auto const &field)
                               {
                                   return !included(field) || field == b.Get(field.Name);
                               });
        }

        bool SameSelections(const CharacterDefinition &a, const CharacterDefinition &b, std::string_view prefix)
        {
            auto included = [&](auto const &selection)
            {
                return selection.Key.starts_with(prefix);
            };
            return std::count_if(a.Selections.begin(), a.Selections.end(), included) ==
                       std::count_if(b.Selections.begin(), b.Selections.end(), included) &&
                   std::all_of(a.Selections.begin(), a.Selections.end(),
                               [&](auto const &selection)
                               {
                                   return !included(selection) || std::find(b.Selections.begin(), b.Selections.end(),
                                                                            selection) != b.Selections.end();
                               });
        }

        bool SamePassState(const GpuMaterialInstance &a, const GpuMaterialInstance &b)
        {
            if (a.Shader != b.Shader || a.Keywords != b.Keywords)
                return false;
            for (auto key :
                 {"_Cull", "_CullMode", "_ZWrite", "_ZTest", "_SrcBlend", "_DstBlend", "_BlendOp", "_ColorMask",
                  "_Stencil", "_StencilComp", "_StencilOp", "_StencilReadMask", "_StencilWriteMask"})
            {
                auto first = a.Values.find(key), second = b.Values.find(key);
                if ((first == a.Values.end()) != (second == b.Values.end()) ||
                    (first != a.Values.end() && first->second != second->second))
                    return false;
            }
            return true;
        }
    }

    std::pair<std::shared_ptr<GpuTexture>, std::shared_ptr<GpuTexture>> GpuSkinComposer::Compose(
        const CharacterDefinition &definition, bool face)
    {
        const int before = face ? 4 : 3, after = face ? 2 : 1, size = face ? 2048 : 4096;
        const std::string name = face ? "face" : "body";
        auto color = composer_.Material("template.create_skin_" + name),
             detail = composer_.Material("template.create_skin detail_" + name);
        color->SetColor("_Color", definition.Body.Get("skinColor"), White);
        if (face)
        {
            const auto &makeup = definition.Face.Get("makeup");
            for (const auto &[layer, number] :
                 std::vector<std::pair<std::string, int>>{{"eyeshadow", 11}, {"cheek", 10}, {"lip", 9}})
            {
                composer_.SelectTexture(*color, "_Texture" + std::to_string(number), "face." + layer + ".AddTex");
                color->SetColor("_Color" + std::to_string(number), makeup.Get(layer + "Color"), Zero);
                composer_.SelectTexture(*detail, "_Texture" + std::to_string(number), "face." + layer + ".GlossTex");
                detail->Set("_Gloss" + std::to_string(number), makeup.Float(layer + "Gloss"));
            }
            composer_.SelectTexture(*color, "_Texture12", std::string("face.mole.AddTex"));
            color->SetColor("_Color12", definition.Face.Get("moleColor"), Zero);
            const auto &mole = definition.Face.Get("moleLayout");
            color->Set("_Texture12UV",
                       {Lerp(5, 1, ParameterComponent(mole, 0)), Lerp(5, 1, ParameterComponent(mole, 1)),
                        Lerp(.3f, -.3f, ParameterComponent(mole, 2)), Lerp(.3f, -.3f, ParameterComponent(mole, 3))});
        }
        else
        {
            composer_.SelectTexture(*color, "_Texture7", std::string("body.sunburn.AddTex"));
            color->SetColor("_Color7", definition.Body.Get("sunburnColor"), Zero);
        }
        const auto &paints = (face ? definition.Face.Get("makeup") : definition.Body).Get("paintInfo");
        for (int i = 0; i < 2; ++i)
        {
            const auto &paint = At(paints, i);
            const auto number = std::to_string(i + 5), key = name + ".paint." + std::to_string(i);
            composer_.SelectTexture(*color, "_Texture" + number, key + ".AddTex");
            composer_.SelectTexture(*detail, "_Texture" + number, key + ".GlossTex");
            color->SetColor("_Color" + number, paint.Get("color"), Zero);
            detail->Set("_Gloss" + number, paint.Float("glossPower"));
            detail->Set("_Metallic" + number, paint.Float("metallicPower"));
            const auto &layout = paint.Get("layout");
            for (auto &material : {color, detail})
                if (face)
                {
                    material->Set("_Texture" + number + "UV", {Lerp(10, 1, ParameterComponent(layout, 0)),
                                                               Lerp(10, 1, ParameterComponent(layout, 1)),
                                                               Lerp(.28f, -.3f, ParameterComponent(layout, 2)),
                                                               Lerp(.28f, -.3f, ParameterComponent(layout, 3))});
                    material->Set("_Texture" + number + "Rotator", Lerp(1, -1, paint.Float("rotation")));
                }
                else if (paint.Has("resolvedLayout"))
                {
                    const auto &c = paint.Get("resolvedLayout");
                    material->Set("_Texture" + number + "UV",
                                  {Lerp(c.Float("CenterScale") + c.Float("AddScale"),
                                        c.Float("CenterScale") - c.Float("AddScale"), ParameterComponent(layout, 0)),
                                   Lerp(c.Float("CenterScale") + c.Float("AddScale"),
                                        c.Float("CenterScale") - c.Float("AddScale"), ParameterComponent(layout, 1)),
                                   Lerp(c.Float("CenterX") + c.Float("MoveX"), c.Float("CenterX") - c.Float("MoveX"),
                                        ParameterComponent(layout, 2)),
                                   Lerp(c.Float("CenterY") + c.Float("MoveY"), c.Float("CenterY") - c.Float("MoveY"),
                                        ParameterComponent(layout, 3))});
                    material->Set("_Texture" + number + "Rotator", Lerp(1, -1, paint.Float("rotation")));
                }
        }
        auto layers = [&](std::shared_ptr<GpuTexture> source, const GpuMaterialInstance &material)
        {
            for (const auto &overlay : definition.Overlays)
                if (overlay.Type == before)
                    source = composer_.Overlay(*source, overlay, size);
            source = composer_.Compose(*source, material, size);
            for (const auto &overlay : definition.Overlays)
                if (overlay.Type == after)
                    source = composer_.Overlay(*source, overlay, size);
            return source;
        };
        return {layers(composer_.Texture(name + ".skin.MainTex"), *color),
                layers(composer_.Texture("template.black" + std::to_string(size)), *detail)};
    }

    GpuCharacterAppearance::GpuCharacterAppearance(NativeCharacterScene &scene, GpuMaterialComposer &composer)
        : scene_(scene), definition_(*scene.Definition), composer_(composer)
    {
        Build();
    }

    bool GpuCharacterAppearance::Update(const CharacterDefinition &previous)
    {
        return Build(&previous);
    }

    bool GpuCharacterAppearance::Build(const CharacterDefinition *previous)
    {
        bool extensions = !previous || previous->MaterialEdits != definition_.MaterialEdits ||
                          previous->Overlays != definition_.Overlays;
        bool masks = extensions || !SameSelections(*previous, definition_, "clothes.");
        bool body = masks ||
                    !SameExcept(previous->Body, definition_.Body,
                                {"shapeValueBody", "bustSoftness", "bustWeight", "effectiveBustSoftness"}) ||
                    !SameSelections(*previous, definition_, "body.");
        bool face =
            extensions || body ||
            !SameExcept(previous->Face, definition_.Face, {"shapeValueFace", "disableBlinking"}) ||
            !SameSelections(*previous, definition_, "face.") || !SameSelections(*previous, definition_, "eye.") ||
            !SameSelections(*previous, definition_, "pupil.") || !SameSelections(*previous, definition_, "highlight.");
        bool hair = extensions || previous->Hair.Get("shaderType") != definition_.Hair.Get("shaderType");
        if (extensions || previous->Body.Get("skinColor") != definition_.Body.Get("skinColor") ||
            previous->Face.Get("makeup") != definition_.Face.Get("makeup") ||
            previous->Face.Get("moleColor") != definition_.Face.Get("moleColor") ||
            previous->Face.Get("moleLayout") != definition_.Face.Get("moleLayout") ||
            !SameSelections(*previous, definition_, "face.skin.") ||
            !SameSelections(*previous, definition_, "face.mole."))
            skin_.erase(true);
        if (extensions || previous->Body.Get("skinColor") != definition_.Body.Get("skinColor") ||
            previous->Body.Get("sunburnColor") != definition_.Body.Get("sunburnColor") ||
            previous->Body.Get("paintInfo") != definition_.Body.Get("paintInfo") ||
            !SameSelections(*previous, definition_, "body.skin.") ||
            !SameSelections(*previous, definition_, "body.paint.") ||
            !SameSelections(*previous, definition_, "body.sunburn."))
            skin_.erase(false);
        auto old = std::move(Materials);
        Materials.clear();
        Order.clear();
        std::unordered_set<const CharacterPart *> changed;
        for (auto const &part : scene_.Parts)
        {
            bool refresh = extensions;
            if (part->Definition->Role == "body")
                refresh |= body;
            else if (part->Definition->Role == "head")
                refresh |= face;
            else if (part->Definition->Role == "hair" || part->Definition->Role == "accessory")
                refresh |= hair;
            else if (part->Definition->Role == "clothes")
                refresh |= masks;
            if (previous)
            {
                auto found = std::find_if(previous->Parts.begin(), previous->Parts.end(),
                                          [&](auto const &candidate)
                                          {
                                              return candidate->Role == part->Definition->Role &&
                                                     candidate->Slot == part->Definition->Slot;
                                          });
                refresh |= found == previous->Parts.end() || (*found)->Prefab != part->Definition->Prefab ||
                           !SameExcept((*found)->Parameters, part->Definition->Parameters,
                                       {"dictBundle", "addMove", "noShake", "partsOfHead", "parentKey", "hideOpt"}) ||
                           !SameSelections(*previous, definition_,
                                           part->Definition->Role + "." + std::to_string(part->Definition->Slot) + ".");
            }
            if (refresh)
                changed.insert(part->Definition.get());
        }
        for (const auto &renderer : scene_.Renderers)
        {
            Order.push_back(renderer.get());
            auto found = old.find(renderer.get());
            if (found != old.end() && !changed.contains(renderer->Part.get()))
            {
                Materials.emplace(renderer.get(), found->second);
                continue;
            }
            changed.insert(renderer->Part.get());
            auto &materials = Materials[renderer.get()];
            for (const auto &m : renderer->Materials)
                materials.push_back(m ? composer_.CreateMaterial(*m) : nullptr);
        }
        for (const auto &p : scene_.Parts)
        {
            const auto &part = *p->Definition;
            if (!changed.contains(&part))
            {
                if (part.Role == "accessory")
                    if (auto component = Component(part, "CmpAccessory"))
                        for (auto renderer : PartRenderers(part))
                            if (Named(component->Data.Get("rendAlpha"), renderer->Node->Name))
                            {
                                auto const &value = At(part.Parameters.Get("colorInfo"), 3);
                                renderer->Node->Active = value.Has("color")
                                                             ? ParameterColor(value.Get("color"), White).w != 0
                                                             : part.Colors.size() < 16 || part.Colors[15] != 0;
                            }
                continue;
            }
            if (part.Role == "clothes")
                Clothes(part);
            for (auto *renderer : PartRenderers(part))
                for (const auto &pointer : Materials.at(renderer))
                {
                    if (!pointer)
                        continue;
                    auto &material = *pointer;
                    const auto &role = part.Role;
                    const auto &name = renderer->Node->Name;
                    if ((role == "body" && material.HasProperty("_MainTex")) || (role == "head" && name == "o_head"))
                        Skin(material, role == "head");
                    else if (role == "head" && name.starts_with("o_eyebase"))
                        Eye(material, name.ends_with("_L") ? 0 : 1);
                    else if (role == "head" && name == "o_eyelashes")
                    {
                        composer_.SelectTexture(material, "_MainTex", std::string("face.eyelashes.AddTex"));
                        material.SetColor("_Color", definition_.Face.Get("eyelashesColor"), Black);
                    }
                    else if (role == "head" && name == "o_eyeshadow")
                        material.Set("_ShadowScale", Lerp(.1f, .9f, definition_.Face.Float("whiteShadowScale")));
                    else if (role == "hair")
                        Hair(material, part, *renderer);
                    else if (role == "accessory")
                        Accessory(material, part, *renderer);
                    if (role == "body" || (role == "clothes" && part.Slot == 2 && name == "o_bra_a"))
                        for (const auto *property : {"_alpha_a", "_alpha_b"})
                            if (material.HasProperty(property))
                                material.Set(property, 1.f);
                }
        }
        ClothingMasks(!changed.empty());
        for (const auto &part : scene_.Parts)
            // 遮罩绑定可能覆盖材质编辑器的贴图；按原顺序重新应用覆盖值。
            Extensions(*part->Definition, changed.empty());
        DryMaker();
        bool reset = old.size() != Materials.size();
        for (auto &[renderer, materials] : Materials)
        {
            auto found = old.find(renderer);
            if (found == old.end())
            {
                reset = true;
                continue;
            }
            reset |= materials.size() != found->second.size();
            for (size_t i = 0; i < std::min(materials.size(), found->second.size()); ++i)
                if (materials[i] != found->second[i])
                {
                    if (!materials[i] || !found->second[i])
                        reset = true;
                    else
                    {
                        reset |= !SamePassState(*found->second[i], *materials[i]);
                        *found->second[i] = std::move(*materials[i]);
                        materials[i] = found->second[i];
                    }
                }
        }
        return reset;
    }

    std::vector<NativeSceneRenderer *> GpuCharacterAppearance::PartRenderers(const CharacterPart &part) const
    {
        std::vector<NativeSceneRenderer *> result;
        for (const auto &renderer : scene_.Renderers)
            if (renderer->Part.get() == &part)
                result.push_back(renderer.get());
        return result;
    }

    void GpuCharacterAppearance::Skin(GpuMaterialInstance &material, bool face)
    {
        const std::string name = face ? "face" : "body";
        auto found = skin_.find(face);
        if (found == skin_.end())
            found = skin_.emplace(face, GpuSkinComposer(composer_).Compose(definition_, face)).first;
        material.TextureOverrides["_MainTex"] = found->second.first;
        material.TextureOverrides["_DetailMainTex"] = found->second.second;
        auto select = [&](const std::string &property, const std::string &key)
        {
            composer_.SelectTexture(material, property, key);
        };
        if (face)
        {
            select("_BumpMap", "face.skin.NormalMapTex");
            select("_Texture3", "face.eyebrow.AddTex");
            material.SetColor("_Color3", definition_.Face.Get("eyebrowColor"), Black);
            const auto &brow = definition_.Face.Get("eyebrowLayout");
            material.Set("_Texture3UV",
                         {Lerp(-.2f, .2f, ParameterComponent(brow, 0)), Lerp(.16f, 0, ParameterComponent(brow, 1)),
                          Lerp(2, .5f, ParameterComponent(brow, 2)), Lerp(2, .5f, ParameterComponent(brow, 3))});
            material.Set("_Texture3Rotator", Lerp(-.15f, .15f, definition_.Face.Float("eyebrowTilt")));
        }
        else
        {
            select("_Texture2", "body.nip.AddTex");
            material.SetColor("_Color2", definition_.Body.Get("nipColor"), White);
            material.Set("_NipScale", definition_.Body.Float("areolaSize", .5f));
            material.Set("_NipGloss", definition_.Body.Float("nipGlossPower"));
            select("_Texture3", "body.underhair.AddTex");
            material.SetColor("_Color3", definition_.Body.Get("underhairColor"), Black);
            material.SetColor("_Color13", definition_.Body.Get("nailColor"), White);
            material.Set("_NailGloss", definition_.Body.Float("nailGlossPower"));
        }
        select("_OcclusionMap", name + (face ? ".skin" : ".detail") + ".OcclusionMapTex");
        select("_BumpMap2", name + ".detail." + (face ? "AddTex" : "NormalMapTex"));
        material.Set("_BumpScale2", (face ? definition_.Face : definition_.Body).Float("detailPower"));
        material.Set("_Gloss", Lerp(0, .8f, definition_.Body.Float("skinGlossPower")));
        material.Set("_Metallic", definition_.Body.Float("skinMetallicPower"));
        material.Set("_Texture4Scale", 0.f);
    }

    void GpuCharacterAppearance::Eye(GpuMaterialInstance &material, int index)
    {
        const auto &pupil = At(definition_.Face.Get("pupil"), index);
        composer_.SelectTexture(material, "_Texture2", "eye." + std::to_string(index) + ".AddTex");
        composer_.SelectTexture(material, "_Texture3", "pupil." + std::to_string(index) + ".AddTex");
        composer_.SelectTexture(material, "_Texture4", std::string("highlight.AddTex"));
        material.SetColor("_Color", pupil.Get("whiteColor"), White);
        material.SetColor("_Color2", pupil.Get("pupilColor"), White);
        material.SetColor("_Color3", pupil.Get("blackColor"), Black);
        material.SetColor("_Color4", definition_.Face.Get("hlColor"), White);
        material.Set("_Emission", pupil.Float("pupilEmission"));
        material.Set("_Smoothness", 1.f);
        auto layout = material.Vector("_texture2uv");
        layout.x = Lerp(2, .5f, pupil.Float("pupilW", .5f));
        layout.y = Lerp(2, .5f, pupil.Float("pupilH", .5f));
        layout.w = Lerp(.5f, -.5f, definition_.Face.Float("pupilY"));
        material.SetColor("_texture2uv", layout);
        layout = material.Vector("_texture3uv");
        layout.x = Lerp(4, .4f, pupil.Float("blackW", .5f));
        layout.y = Lerp(4, .4f, pupil.Float("blackH", .5f));
        material.SetColor("_texture3uv", layout);
        const auto &hl = definition_.Face.Get("hlLayout");
        material.Set("_Texture4UV",
                     {Lerp(1.8f, .2f, ParameterComponent(hl, 0)), Lerp(1.8f, .2f, ParameterComponent(hl, 1)),
                      Lerp(-.3f, .3f, ParameterComponent(hl, 2)), Lerp(-.3f, .3f, ParameterComponent(hl, 3))});
        material.Set("_Texture4Rotator", Lerp(-1, 1, definition_.Face.Float("hlTilt")));
    }

    void GpuCharacterAppearance::HairShader(GpuMaterialInstance &material, const std::string &key)
    {
        int queue = material.Queue();
        if (composer_.HasSelection("template.hair"))
            material.SetShader(composer_.Material("template.hair")->Shader);
        composer_.OptimizeShader(material);
        composer_.SelectTexture(material, "_MainTex",
                                key + (definition_.Hair.Float("shaderType") == 0 ? ".TexD" : ".TexC"));
        material.Queue(queue);
    }

    void GpuCharacterAppearance::Hair(GpuMaterialInstance &material, const CharacterPart &part,
                                      NativeSceneRenderer &renderer)
    {
        const auto *component = Component(part, "CmpHair");
        const auto &values = At(definition_.Hair.Get("parts"), part.Slot);
        if (component && !Named(component->Data.Get("rendHair"), renderer.Node->Name))
        {
            if (Named(component->Data.Get("rendAccessory"), renderer.Node->Name))
                for (size_t i = 0; i < std::min<size_t>(3, component->Data.Get("acsDefColor").Children.size()); ++i)
                    if (component->Data.Float("useAcsColor0" + std::to_string(i + 1)) != 0)
                    {
                        auto c = ParameterColor(At(values.Get("acsColorInfo"), i).Get("color"), White);
                        c.w = std::max(1.f, c.w);
                        material.SetColor(i == 0 ? "_Color" : "_Color" + std::to_string(i + 1), c);
                    }
            return;
        }
        HairShader(material, "hair." + std::to_string(part.Slot));
        if (material.HasProperty("_Ringoff"))
            material.Set("_Ringoff", part.List.String("RingOff") == "1" ? 1.f : 0.f);
        auto opaque = [&](const char *key)
        {
            auto c = ParameterColor(values.Get(key), White);
            c.w = std::max(1.f, c.w);
            return c;
        };
        material.SetColor("_Color", opaque("baseColor"));
        if (!component || component->Data.Float("useTopColor") != 0)
            material.SetColor("_TopColor", opaque("topColor"));
        if (!component || component->Data.Float("useUnderColor") != 0)
            material.SetColor("_UnderColor", opaque("underColor"));
        material.SetColor("_Specular", values.Get("specular"), White);
        material.Set("_Metallic", values.Float("metallic"));
        material.Set("_Smoothness", values.Float("smoothness"));
        if (component && component->Data.Float("useMesh") != 0)
        {
            composer_.SelectTexture(material, "_MeshColorMask_B",
                                    "hair." + std::to_string(part.Slot) + ".mesh.MainTex");
            auto c = ParameterColor(values.Get("meshColor"), White);
            c.w = 1;
            material.SetColor("_MeshColor_01", c);
            const auto &layout = values.Get("meshLayout");
            material.SetTextureTransform(
                "_MeshColorMask_B",
                Vector2{Lerp(.5f, 1, ParameterComponent(layout, 0)), Lerp(.5f, 1, ParameterComponent(layout, 1))},
                Vector2{Lerp(.5f, 1.5f, ParameterComponent(layout, 2)),
                        Lerp(.5f, 1.5f, ParameterComponent(layout, 3))});
        }
    }

    void GpuCharacterAppearance::Accessory(GpuMaterialInstance &material, const CharacterPart &part,
                                           NativeSceneRenderer &renderer)
    {
        const auto *component = Component(part, "CmpAccessory");
        if (!component || Materials.at(&renderer).empty() || Materials.at(&renderer)[0].get() != &material)
            return;
        const auto &data = component->Data;
        const bool alpha = Named(data.Get("rendAlpha"), renderer.Node->Name),
                   normal = Named(data.Get("rendNormal"), renderer.Node->Name);
        const auto &values = part.Parameters.Get("colorInfo");
        if (data.Float("typeHair") != 0 && normal)
        {
            HairShader(material, "accessory." + std::to_string(part.Slot));
            constexpr const char *properties[]{"_Color", "_TopColor", "_UnderColor"};
            for (int i = 0; i < 3; ++i)
                if (data.Float("useColor0" + std::to_string(i + 1)) != 0)
                    material.SetColor(properties[i], At(values, i).Get("color"), White);
            material.SetColor("_Specular", At(values, 3).Get("color"), White);
            material.Set("_Smoothness", At(values, 0).Float("smoothnessPower"));
            material.Set("_Metallic", At(values, 0).Float("metallicPower"));
            return;
        }
        for (int i = 0; i < 4; ++i)
        {
            if (i == 3 ? !alpha : !normal || data.Float("useColor0" + std::to_string(i + 1)) == 0)
                continue;
            const auto &value = At(values, i);
            const auto color = value.Has("color") ? ParameterColor(value.Get("color"), White)
                               : part.Colors.size() > static_cast<size_t>(i * 4 + 3)
                                   ? Vector4{part.Colors[i * 4], part.Colors[i * 4 + 1], part.Colors[i * 4 + 2],
                                             part.Colors[i * 4 + 3]}
                                   : White;
            material.SetColor(i == 0 || alpha ? "_Color" : "_Color" + std::to_string(i + 1), color);
            material.Set(alpha    ? "_Glossiness4"
                         : i == 0 ? "_Glossiness"
                                  : "_Glossiness" + std::to_string(i + 1),
                         value.Float("glossPower"));
            material.Set(alpha    ? "_Metallic4"
                         : i == 0 ? "_Metallic"
                                  : "_Metallic" + std::to_string(i + 1),
                         value.Float("metallicPower"));
            if (alpha)
                renderer.Node->Active = color.w != 0;
        }
    }

    void GpuCharacterAppearance::Clothes(const CharacterPart &part)
    {
        const auto *component = Component(part, "CmpClothes");
        if (!component)
            return;
        const auto &config = component->Data;
        const auto &values = At(definition_.Clothes.Get("parts"), part.Slot);
        const auto prefix = "clothes." + std::to_string(part.Slot) + ".";
        if (config.Float("useBreak") != 0)
            for (auto *renderer : PartRenderers(part))
                if (Named(config.Get("rendNormal01"), renderer->Node->Name) ||
                    Named(config.Get("rendNormal02"), renderer->Node->Name) ||
                    Named(config.Get("rendNormal03"), renderer->Node->Name))
                {
                    auto &list = Materials.at(renderer);
                    if (!list.empty() && list[0] && list[0]->HasProperty("_AlphaEx"))
                        list[0]->Set("_AlphaEx", 1 - values.Float("breakRate"));
                }
        if (!composer_.HasSelection(prefix + "MainTex") || !composer_.HasSelection(prefix + "ColorMaskTex") ||
            !composer_.TryTexture(prefix + "MainTex") || !composer_.TryTexture(prefix + "ColorMaskTex"))
            return;
        for (int group = 0; group < 3; ++group)
        {
            const auto &names = config.Get("rendNormal0" + std::to_string(group + 1));
            if (names.Children.empty())
                continue;
            const auto key = prefix + (group == 0 ? "MainTex" : "MainTex0" + std::to_string(group + 1));
            if (!composer_.HasSelection(key))
                continue;
            auto main = composer_.TryTexture(key);
            if (!main)
                continue;
            auto creator = composer_.Material("template.create_clothes"),
                 detail = composer_.Material("template.create_clothes detail");
            const auto mask = prefix + (group == 0 ? "ColorMaskTex" : "ColorMask0" + std::to_string(group + 1) + "Tex");
            composer_.SelectTexture(*creator, "_ColorMask", mask);
            composer_.SelectTexture(*detail, "_ColorMask", mask);
            const auto &scale = config.Get("uvScalePattern");
            creator->Set("_UVScalePattern", {ParameterComponent(scale, 0), ParameterComponent(scale, 1),
                                             ParameterComponent(scale, 2), ParameterComponent(scale, 3)});
            for (int i = 0; i < 3; ++i)
            {
                const auto &value = At(values.Get("colorInfo"), i);
                auto color = ParameterColor(value.Get("baseColor"), White);
                if (config.Float("useColorA0" + std::to_string(i + 1)) == 0)
                    color.w = 1;
                creator->SetColor(i == 0 ? "_Color" : "_Color" + std::to_string(i + 1), color);
                creator->SetColor("_Color" + std::to_string(i + 1) + "_2", value.Get("patternColor"), White);
                const auto &layout = value.Get("layout");
                creator->Set("_patternuv" + std::to_string(i + 1),
                             {Lerp(20, 1, ParameterComponent(layout, 0)), Lerp(20, 1, ParameterComponent(layout, 1)),
                              Lerp(-1, 1, ParameterComponent(layout, 2)), Lerp(-1, 1, ParameterComponent(layout, 3))});
                creator->Set("_patternuv" + std::to_string(i + 1) + "Rotator", Lerp(-1, 1, value.Float("rotation")));
                composer_.SelectTexture(*creator, "_PatternMask" + std::to_string(i + 1),
                                        prefix + "pattern." + std::to_string(i) + ".MainTex");
                detail->Set(i == 0 ? "_Glossiness" : "_Glossiness" + std::to_string(i + 1), value.Float("glossPower"));
                detail->Set(i == 0 ? "_Metallic" : "_Metallic" + std::to_string(i + 1), value.Float("metallicPower"));
            }
            creator->SetColor("_Color4", config.Get("defMainColor04"), White);
            detail->Set("_Glossiness4", config.Float("defGloss04"));
            detail->Set("_Metallic4", config.Float("defMetallic04"));
            auto diffuse = composer_.Compose(*main, *creator, main->Width(), main->Height());
            auto properties =
                composer_.Compose(*composer_.Default("opaque-black"), *detail, main->Width(), main->Height());
            for (auto *renderer : PartRenderers(part))
                if (Named(names, renderer->Node->Name))
                {
                    auto &list = Materials.at(renderer);
                    if (!list.empty() && list[0])
                    {
                        list[0]->TextureOverrides["_MainTex"] = diffuse;
                        list[0]->TextureOverrides["_DetailMainTex"] = properties;
                    }
                }
        }
        ClothesOverlay(part, config);
    }

    void GpuCharacterAppearance::ClothesOverlay(const CharacterPart &part, const Parameter &config)
    {
        constexpr const char *names[]{"ct_clothesTop", "ct_clothesBot", "ct_bra",   "ct_shorts",
                                      "ct_gloves",     "ct_panst",      "ct_socks", "ct_shoes"};
        if (part.Slot < 0 || part.Slot >= 8)
            return;
        auto overlay = std::find_if(definition_.Overlays.begin(), definition_.Overlays.end(),
                                    [&](const auto &o)
                                    {
                                        return o.Name == names[part.Slot];
                                    });
        if (overlay == definition_.Overlays.end())
            return;
        for (int group = 1; group <= 3; group += 2)
        {
            if (config.Get("rendNormal0" + std::to_string(group)).Children.empty())
                continue;
            for (int index = group; index <= std::min(3, group + 1); ++index)
            {
                const auto name = At(config.Get("rendNormal0" + std::to_string(index)), 0).Text;
                for (auto *renderer : PartRenderers(part))
                    if (renderer->Node->Name == name)
                    {
                        auto &list = Materials.at(renderer);
                        if (list.empty() || !list[0])
                            break;
                        auto found = list[0]->TextureOverrides.find("_MainTex");
                        if (found == list[0]->TextureOverrides.end())
                            break;
                        auto target = found->second, source = overlay->Replace ? composer_.Default("black") : target;
                        auto output = overlay->Texture
                                          ? composer_.Overlay(*source, *overlay, target->Width(), target->Height())
                                          : composer_.Copy(*source, target->Width(), target->Height());
                        for (auto &[_, materials] : Materials)
                            for (auto &material : materials)
                                if (material)
                                {
                                    auto texture = material->TextureOverrides.find("_MainTex");
                                    if (texture != material->TextureOverrides.end() && texture->second == target)
                                        texture->second = output;
                                }
                        break;
                    }
            }
            break;
        }
    }

    void GpuCharacterAppearance::UpdateVisibility()
    {
        // 贴图已在初始化时绑定；切换时只修改遮罩开关，保留材质编辑器的覆盖值。
        ClothingMasks(false);
        for (auto const &part : scene_.Parts)
            if (part->Definition->Role == "body" || part->Definition->Role == "clothes" ||
                part->Definition->Role == "accessory")
                Extensions(*part->Definition, true);
    }

    void GpuCharacterAppearance::ClothingMasks(bool selectTextures)
    {
        const auto &visibility = definition_.Visibility;
        const CharacterPart *lower = nullptr;
        for (const auto &part : scene_.Parts)
            if (part->Definition->Role == "clothes" && (part->Definition->Slot == 0 || part->Definition->Slot == 1) &&
                composer_.HasSelection("clothes." + std::to_string(part->Definition->Slot) + ".OverBodyBMask"))
                lower = part->Definition.get();
        bool enabled = visibility.ClothesVisible(1) && lower &&
                       (lower->List.String("BreakDisableMask") != "1" ||
                        At(definition_.Clothes.Get("parts"), lower->Slot).Float("breakRate") == 0);
        for (const auto &renderer : scene_.Renderers)
        {
            if (!renderer->Part)
                continue;
            const auto &part = *renderer->Part;
            std::string mask = part.Role == "body"                        ? "OverBodyMask"
                               : part.Role == "clothes" && part.Slot == 2 ? "OverBraMask"
                                                                          : "";
            std::string bottom = part.Role == "body"      ? "OverBodyBMask"
                                 : part.Role == "clothes" ? (part.Slot == 2   ? "OverInnerTBMask"
                                                             : part.Slot == 3 ? "OverInnerBMask"
                                                             : part.Slot == 5 ? "OverPanstMask"
                                                                              : "")
                                                          : "";
            for (const auto &material : Materials.at(renderer.get()))
            {
                if (!material)
                    continue;
                if (!mask.empty() && (part.Role == "body" || renderer->Node->Name == "o_bra_a"))
                {
                    for (const auto *property : {"_alpha_a", "_alpha_b"})
                        if (material->HasProperty(property))
                            material->Set(property, visibility.ClothesVisible(0) ? 1.f : 0.f);
                    if (selectTextures && material->HasProperty("_AlphaMask") &&
                        composer_.HasSelection("clothes.0." + mask))
                        composer_.SelectTexture(*material, "_AlphaMask", "clothes.0." + mask);
                    if (material->HasProperty("_alpha_c"))
                        material->Set("_alpha_c",
                                      At(definition_.Clothes.Get("parts"), 0).Float("breakRate") == 0 ? 0.f : 1.f);
                }
                const auto maskRenderer = part.Role == "body" ? renderer->Node->Name
                                          : part.Slot == 5    ? "o_panst_a"
                                                              : "o_shorts_a";
                if (!bottom.empty() && renderer->Node->Name == maskRenderer)
                {
                    if (selectTextures && lower && material->HasProperty("_AlphaMask2"))
                        composer_.SelectTexture(*material, "_AlphaMask2",
                                                "clothes." + std::to_string(lower->Slot) + "." + bottom);
                    if (material->HasProperty("_alpha_d"))
                        material->Set("_alpha_d", enabled ? 1.f : 0.f);
                }
            }
        }
    }

    void GpuCharacterAppearance::Extensions(const CharacterPart &part, bool visibilityOnly)
    {
        if (part.Role == "rig" || part.Role == "headRig")
            return;
        int type = part.Role == "clothes" ? 1 : part.Role == "accessory" ? 2 : part.Role == "hair" ? 3 : 4;
        std::vector<const MaterialEdit *> edits;
        for (const auto &edit : definition_.MaterialEdits)
            if (edit.ObjectType == type && (type == 4 || edit.Slot == part.Slot))
                edits.push_back(&edit);
        std::stable_sort(edits.begin(), edits.end(),
                         [](const auto *a, const auto *b)
                         {
                             return (a->Kind == "shader" ? 0 : 1) < (b->Kind == "shader" ? 0 : 1);
                         });
        for (auto *renderer : PartRenderers(part))
        {
            for (const auto *edit : edits)
                if (edit->Kind == "renderer" && edit->Name == renderer->Node->Name)
                {
                    if (edit->Property == "shadowCasting")
                        renderer->ShadowCasting = static_cast<int>(edit->Number);
                    else if (edit->Property == "receiveShadows")
                        renderer->ReceiveShadows = edit->Number != 0;
                    else
                        renderer->Enabled = edit->Number != 0;
                }
            for (const auto &material : Materials.at(renderer))
            {
                if (!material)
                    continue;
                auto name = material->Name;
                for (size_t pos; (pos = name.find("(Instance)")) != std::string::npos;)
                    name.erase(pos, 10);
                auto begin = name.find_first_not_of(" \t\r\n"), end = name.find_last_not_of(" \t\r\n");
                name = begin == std::string::npos ? "" : name.substr(begin, end - begin + 1);
                for (const auto *edit : edits)
                {
                    if (edit->Kind == "renderer" || edit->Name != name)
                        continue;
                    if (visibilityOnly &&
                        (edit->Kind != "float" || (edit->Property != "_alpha_a" && edit->Property != "_alpha_b" &&
                                                   edit->Property != "_alpha_c" && edit->Property != "_alpha_d")))
                        continue;
                    if (edit->Kind == "shader")
                    {
                        material->SetShader(composer_.Shader(edit->Text));
                        material->Queue(-1);
                        auto source = std::find_if(definition_.Shaders.begin(), definition_.Shaders.end(),
                                                   [&](const auto &s)
                                                   {
                                                       return s.Name == edit->Text;
                                                   });
                        if (source != definition_.Shaders.end())
                        {
                            if (source->RenderQueue >= 0)
                                material->Queue(source->RenderQueue);
                            for (const auto &value : source->Defaults)
                                if (value.Type == "Texture" && value.Texture)
                                    composer_.SelectTexture(*material, value.Property, value.Texture);
                                else if (value.Type == "Float" || value.Type == "Color")
                                    material->Set(value.Property, value.Values);
                        }
                        continue;
                    }
                    if (edit->Kind == "queue")
                    {
                        material->Queue(static_cast<int>(edit->Number));
                        continue;
                    }
                    if (!material->HasProperty(edit->Property))
                        continue;
                    if (edit->Kind == "float")
                        material->Set(edit->Property, edit->Number);
                    else if (edit->Kind == "color")
                        material->Set(edit->Property, edit->Values);
                    else if (edit->Kind == "texture")
                    {
                        if (edit->Texture)
                            material->TextureOverrides[edit->Property] = composer_.Embedded(*edit->Texture);
                        material->SetTextureTransform(
                            edit->Property,
                            edit->Scale.size() == 2 ? std::optional<Vector2>{{edit->Scale[0], edit->Scale[1]}}
                                                    : std::nullopt,
                            edit->Offset.size() == 2 ? std::optional<Vector2>{{edit->Offset[0], edit->Offset[1]}}
                                                     : std::nullopt);
                    }
                }
            }
        }
    }

    void GpuCharacterAppearance::DryMaker()
    {
        for (const auto &p : scene_.Parts)
        {
            const auto &part = *p->Definition;
            const auto *component = part.Role == "hair"      ? Component(part, "CmpHair")
                                    : part.Role == "clothes" ? Component(part, "CmpClothes")
                                                             : nullptr;
            for (auto *renderer : PartRenderers(part))
            {
                bool named = false;
                if (component)
                    for (const auto *key :
                         {"rendHair", "rendAccessory", "rendNormal01", "rendNormal02", "rendNormal03"})
                        named |= Named(component->Data.Get(key), renderer->Node->Name);
                if (part.Role == "body" || (part.Role == "head" && renderer->Node->Name == "o_head") || named)
                    for (const auto &material : Materials.at(renderer))
                        if (material && material->HasProperty("_ExGloss"))
                            material->Set("_ExGloss", 0.f);
            }
        }
    }
}
