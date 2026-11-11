#include "Baking/NativePbrBake.h"
#include "Images/ImageFiles.h"
#include "PbrShader.h"
#include <algorithm>
#include <sstream>

namespace hs2
{
    namespace
    {
        std::vector<std::string> Split(const char *text)
        {
            std::vector<std::string> result;
            std::istringstream input(text);
            for (std::string word; input >> word;)
                result.push_back(word);
            return result;
        }

        const auto Vectors =
            Split("_MainTex_ST _ColorMask_ST _ColorMask_B_ST _BumpMap_ST _BumpMap2_ST _OcclusionMap_ST _EmissionMap_ST "
                  "_MeshColorMask_B_ST _DetailMainTex_ST _MetallicGlossMap_ST _DetailMask_ST _Color _Color2 _Color3 "
                  "_Color4 _TopColor _UnderColor _MeshColor_01 _EmissionColor _texture2uv _texture3uv _Texture4UV "
                  "_Texture3UV _eyebaseoffset");
        const auto Scalars =
            Split("_Texture3Rotator _Texture4Rotator _BumpScale _BumpScale2 _alpha_a _alpha_b _alpha_c _alpha_d "
                  "_Emission _BakeKind _MaterialKind _BakeAlpha _BaseMetallic _BaseSmoothness _IgnoreMainAlpha "
                  "_OriginalHair _OriginalEye _PackedKind _TopGradation _UnderGradation _ColorMask_A0B1 "
                  "_DetailMainScale _DetailScale _ShadowScale _NamidaScale _OcclusionStrength _EmissionGain "
                  "_HasMainTex _HasColorMask _HasAlphaMask _HasAlphaMask2 _HasDetailMainTex _HasBumpMap _HasBumpMap2 "
                  "_HasMetallicGlossMap _HasOcclusionMap _HasEmissionMap _HasTexture2 _HasTexture3 _HasTexture4 "
                  "_HasMeshColorMask_B _HasColorMask_B _HasDetailMask");
        const std::set<std::string> Colors{"_Color",    "_Color2",     "_Color3",       "_Color4",
                                           "_TopColor", "_UnderColor", "_MeshColor_01", "_EmissionColor"};

        std::string Lower(std::string text)
        {
            for (auto &c : text)
                if (c >= 'A' && c <= 'Z')
                    c = char(c - 'A' + 'a');
            return text;
        }

        std::vector<float> Linear(std::vector<float> values)
        {
            for (std::size_t i = 0; i < (std::min)(values.size(), std::size_t(3)); i++)
                values[i] = GpuMaterialComposer::Linear(values[i]);
            return values;
        }
    }

    const std::vector<std::string> &NativePbrBakePass::TextureNames()
    {
        static const auto names = Split(
            "_MainTex _ColorMask _ColorMask_B _AlphaMask _AlphaMask2 _DetailMainTex _DetailMask _BumpMap _BumpMap2 "
            "_MetallicGlossMap _OcclusionMap _EmissionMap _Texture2 _Texture3 _Texture4 _MeshColorMask_B");
        return names;
    }

    NativePbrBakePass::NativePbrBakePass(GraphicsDevice &graphics, GpuMaterialComposer &composer)
        : graphics_(graphics), composer_(composer)
    {
        constants_.Name = "Bake";
        constants_.Size = static_cast<int>((Vectors.size() + Scalars.size()) * 16);
        int index = 0;
        for (auto &name : Vectors)
            constants_.Constants.push_back({name, index++ * 16, 4, 1, false, 0});
        for (auto &name : Scalars)
            constants_.Constants.push_back({name, index++ * 16, 1, 1, false, 0});
        std::string declarations = "cbuffer Bake:register(b0){float4 parameters[" + std::to_string(index) + "];};\n";
        index = 0;
        for (auto &field : constants_.Constants)
            declarations += "#define " + field.Name + " parameters[" + std::to_string(index++) + "]" +
                            (field.Components == 1 ? ".x" : "") + "\n";
        index = 0;
        for (auto &name : TextureNames())
        {
            declarations += "Texture2D " + name + ":register(t" + std::to_string(index) + ");SamplerState " + name +
                            "Sampler:register(s" + std::to_string(index) + ");\n";
            index++;
        }
        std::string shader = PbrShaderSource;
        auto binding = shader.find("__BINDINGS__");
        if (binding == shader.npos)
            throw std::runtime_error("Native PBR shader bindings missing.");
        shader.replace(binding, 12, declarations);
        pass_ = std::make_unique<ExportGpuPass>(graphics_, shader, "frag", constants_.Size);
    }

    std::unique_ptr<GpuTexture> NativePbrBakePass::Draw(
        const GpuMaterialInstance &material,
        const std::unordered_map<std::string, std::shared_ptr<GpuTexture>> &textures, const ShaderValues &overrides,
        int width, int height, bool color)
    {
        graphics_.VerifyThread();
        ShaderValues values;
        for (auto &vector : Vectors)
        {
            values[vector] = material.CopyPropertyValue(vector, 4).value_or(std::vector<float>(4));
            if (Colors.contains(vector) &&
                (material.ColorProperties.contains(vector) || material.GammaProperties.contains(vector)))
                values[vector] = Linear(values[vector]);
        }
        for (auto &scalar : Scalars)
            values[scalar] = material.CopyPropertyValue(scalar, 1).value_or(std::vector<float>{0});
        for (auto &name : TextureNames())
        {
            auto binding = material.Textures.find(name);
            values[name + "_ST"] = binding == material.Textures.end()
                                       ? std::vector<float>{1, 1, 0, 0}
                                       : std::vector<float>{binding->second.Scale.x, binding->second.Scale.y,
                                                            binding->second.Offset.x, binding->second.Offset.y};
            auto texture = textures.find(name);
            values["_Has" + name.substr(1)] = {texture != textures.end() && texture->second ? 1.f : 0.f};
        }
        for (auto &[key, value] : overrides)
            values[key] = value;
        auto target =
            std::make_unique<GpuTexture>(graphics_, width, height, true,
                                         color ? DXGI_FORMAT_B8G8R8A8_UNORM_SRGB : DXGI_FORMAT_B8G8R8A8_UNORM, false);
        std::vector<std::shared_ptr<GpuTexture>> owned;
        std::vector<ID3D11ShaderResourceView *> views;
        std::vector<ID3D11SamplerState *> samplers;
        for (auto &name : TextureNames())
        {
            auto found = textures.find(name);
            auto texture = found != textures.end() ? found->second : nullptr;
            if (!texture)
                texture = composer_.Default(name == "_MainTex" || name == "_OcclusionMap" || name == "_MetallicGlossMap"
                                                ? "white"
                                                : "opaque-black");
            owned.push_back(texture);
            views.push_back(texture->View.Get());
            samplers.push_back(texture->Sampler.Get());
        }
        auto packed = PackShaderBuffer(constants_, values);
        pass_->Draw(*target, packed, views, samplers);
        return target;
    }

    NativePbrMaterialExporter::NativePbrMaterialExporter(GraphicsDevice &graphics, GpuMaterialComposer &composer,
                                                         fs::path directory)
        : composer_(composer), baker_(graphics, composer), directory_(fs::absolute(directory))
    {
        fs::create_directories(directory_ / L"Textures");
    }

    Json NativePbrMaterialExporter::Get(const GpuMaterialInstance &source, const std::string &role,
                                        const std::string &renderer, const std::string &slot, std::stop_token token)
    {
        if (auto found = slots_.find(slot); found != slots_.end())
            return records_[found->second];
        CheckCancelled(token);
        auto shader = source.Shader->Name;
        auto number = [&](float fallback, std::initializer_list<const char *> names)
        {
            for (auto name : names)
                if (source.HasProperty(name))
                {
                    auto found = source.Values.find(name);
                    if (found != source.Values.end() && !found->second.empty())
                        return found->second.front();
                }
            return fallback;
        };
        auto color = [&](std::vector<float> fallback, std::initializer_list<const char *> names)
        {
            for (auto name : names)
                if (source.HasProperty(name))
                {
                    auto found = source.Values.find(name);
                    if (found != source.Values.end() && found->second.size() == 4)
                        return found->second;
                }
            return fallback;
        };
        auto shaderColor = [&](std::vector<float> fallback, std::initializer_list<const char *> names)
        {
            for (auto name : names)
                if (source.HasProperty(name))
                {
                    auto found = source.Values.find(name);
                    if (found != source.Values.end() && found->second.size() == 4)
                        return source.ColorProperties.contains(name) || source.GammaProperties.contains(name)
                                   ? Linear(found->second)
                                   : found->second;
                }
            return fallback;
        };
        auto index = std::to_string(records_.size());
        auto id = "mat_" + std::string(index.size() < 3 ? 3 - index.size() : 0, '0') + index;
        bool skin = role == "body" || (role == "head" && renderer == "o_head"), eye = renderer.starts_with("o_eyebase"),
             hair = Lower(shader).find("hair") != shader.npos ||
                    (source.HasProperty("_TopColor") && source.HasProperty("_UnderColor")),
             originalHair = shader == "Shader Forge/hair_07" || shader == "Shader Forge/hair_08",
             lash = renderer == "o_eyelashes", eyeShadow = shader == "AIT/main eyeshadow lambert",
             tear = shader == "AIT/main namida", packedClothes = shader.starts_with("AIT/Clothes");
        bool known = skin && shader.starts_with("AIT/Skin") || eye && shader == "AIT/Eye Translucency" ||
                     originalHair || lash && shader == "AIT/eyelashes" || eyeShadow || tear || packedClothes ||
                     shader == "Standard";
        if (!known && unknown_.insert(shader).second)
            Warnings.push_back("材质近似：" + shader + " 使用通用 PBR 转换，未还原专用 Shader 效果。");
        bool transparent = source.Queue() >= 3000 || source.Keywords.contains("_ALPHABLEND_ON") ||
                           source.Keywords.contains("_ALPHAPREMULTIPLY_ON"),
             masked = hair || lash || skin || source.Queue() >= 2450 || source.Keywords.contains("_ALPHATEST_ON") ||
                      source.Tag("RenderType") == "TransparentCutout";
        std::string alpha = transparent && !hair && !lash && !skin ? "Blend" : masked ? "Mask" : "Opaque";
        if (originalHair || eyeShadow || tear || shader == "AIT/Clothes Alpha")
            alpha = "Blend";
        Json record{{"Id", id},
                    {"Name", source.Name},
                    {"SourceShader", shader},
                    {"Role", role},
                    {"Kind", skin        ? "Skin"
                             : eye       ? "Eye"
                             : hair      ? "Hair"
                             : lash      ? "Eyelash"
                             : eyeShadow ? "EyeShadow"
                             : tear      ? "Tearline"
                                         : "Surface"},
                    {"BaseColor", ""},
                    {"Normal", ""},
                    {"Metallic", ""},
                    {"Roughness", ""},
                    {"Occlusion", ""},
                    {"Emission", ""},
                    {"HairStreakMask", ""},
                    {"BaseColorSpace", "sRGB"},
                    {"EmissionColorSpace", "sRGB"},
                    {"DataColorSpace", "Linear"},
                    {"NormalConvention", "OpenGL +Y"},
                    {"AlphaMode", alpha},
                    {"AlphaCutoff", number(.5f, {"_Cutoff"})},
                    {"HairExtraGloss", originalHair ? std::clamp(number(0, {"_ExGloss"}), 0.f, 1.f) : 0.f},
                    {"HairStreakStrength", 0},
                    {"HairStreakColor", std::vector<float>{1, 1, 1, 1}},
                    {"HairStreakUv", 1},
                    {"DoubleSided", hair || lash || number(2, {"_Cull", "_CullMode"}) == 0}};
        std::unordered_map<std::string, std::shared_ptr<GpuTexture>> textures;
        for (auto &name : NativePbrBakePass::TextureNames())
            textures[name] = composer_.AssignedTexture(source, name);
        if (originalHair)
            if (auto ao = composer_.AssignedTexture(source, "_Occlusion"))
                textures["_OcclusionMap"] = ao;
        ShaderValues values{{"_Color", shaderColor({1, 1, 1, 1}, {"_Color", "_BaseColor"})},
                            {"_TopColor", shaderColor({1, 1, 1, 1}, {"_TopColor"})},
                            {"_UnderColor", shaderColor({1, 1, 1, 1}, {"_UnderColor"})},
                            {"_EmissionColor", shaderColor({0, 0, 0, 1}, {"_EmissionColor"})}};
        auto set = [&](std::string name, float value)
        {
            values[std::move(name)] = {value};
        };
        set("_MaterialKind", skin ? 3.f : eye ? 2.f : hair ? 1.f : lash ? 4.f : eyeShadow ? 5.f : tear ? 6.f : 0.f);
        set("_OriginalHair", originalHair ? 1.f : 0.f);
        set("_PackedKind", skin ? 1.f : packedClothes ? 2.f : 0.f);
        set("_OriginalEye", shader == "AIT/Eye Translucency" ? 1.f : 0.f);
        set("_TopGradation", number(1, {"_TopGradation"}));
        set("_UnderGradation", number(1, {"_UnderGradation"}));
        set("_ColorMask_A0B1", number(0, {"_ColorMask_A0B1"}));
        set("_DetailMainScale", number(1, {"_DetailMainScale"}));
        set("_DetailScale", number(0, {"_DetailScale"}));
        set("_ShadowScale", number(.5f, {"_ShadowScale"}));
        set("_NamidaScale", number(0, {"_NamidaScale"}));
        set("_OcclusionStrength", number(1, {"_OcclusionStrength", "_OcculusionStrength"}));
        set("_IgnoreMainAlpha", skin || Lower(shader).find("clothes true") != shader.npos ? 1.f : 0.f);
        set("_BakeAlpha", alpha == "Opaque" ? 0.f : 1.f);
        set("_BaseMetallic", std::clamp(number(0, {"_Metallic"}), 0.f, 1.f));
        set("_BaseSmoothness", std::clamp(number(.5f, {"_Smoothness", "_Glossiness", "_Gloss"}), 0.f, 1.f));
        set("_BumpScale", number(1, {"_BumpScale"}));
        set("_BumpScale2", number(1, {"_BumpScale2"}));
        set("_Emission", number(0, {"_Emission"}));
        set("_EmissionGain", number(1, {"_EmissionStrength"}));
        for (auto name : {"_alpha_a", "_alpha_b", "_alpha_c", "_alpha_d"})
            set(name, number(0, {name}));
        if (originalHair)
            if (auto binding = source.Textures.find("_Occlusion"); binding != source.Textures.end())
                values["_OcclusionMap_ST"] = {binding->second.Scale.x, binding->second.Scale.y,
                                              binding->second.Offset.x, binding->second.Offset.y};
        auto size = [&](std::initializer_list<const char *> names)
        {
            std::pair<int, int> dimensions{1, 1};
            for (auto name : names)
                if (auto texture = textures[name])
                {
                    dimensions.first = (std::max)(dimensions.first, texture->Width());
                    dimensions.second = (std::max)(dimensions.second, texture->Height());
                }
            return dimensions;
        };
        auto bake = [&](const std::string &suffix, int kind, std::pair<int, int> dimensions, bool isColor)
        {
            CheckCancelled(token);
            set("_BakeKind", static_cast<float>(kind));
            auto target = baker_.Draw(source, textures, values, dimensions.first, dimensions.second, isColor);
            auto relative = "Textures/" + id + "_" + suffix + ".png";
            SavePng(directory_ / Path(relative), dimensions.first, dimensions.second, composer_.ReadImage(*target));
            CheckCancelled(token);
            return relative;
        };
        auto baseSize = size({"_MainTex", "_Texture2", "_Texture3", "_Texture4", "_ColorMask", "_ColorMask_B",
                              "_AlphaMask", "_AlphaMask2"});
        if (originalHair && number(0, {"_Mesh_onoff"}) > 0 && textures["_MeshColorMask_B"])
        {
            auto streak = textures["_MeshColorMask_B"];
            record["HairStreakMask"] = bake("hair_streak", 6, {streak->Width(), streak->Height()}, false);
            record["HairStreakColor"] = Linear(color({1, 1, 1, 1}, {"_MeshColor_01"}));
            record["HairStreakStrength"] = std::clamp(number(0, {"_Mesh_onoff"}), 0.f, 1.f);
            Warnings.push_back(renderer + "：挑染保留独立 UV1 遮罩，原生导出自动接线；通用 FBX 需按材质清单配置。");
        }
        record["BaseColor"] = bake("basecolor", 0, baseSize, true);
        auto normalSize =
            originalHair ? size({"_BumpMap", "_BumpMap2", "_DetailMask"}) : size({"_BumpMap", "_BumpMap2"});
        if (originalHair && textures["_DetailMask"])
        {
            normalSize.first = (std::max)(normalSize.first, baseSize.first);
            normalSize.second = (std::max)(normalSize.second, baseSize.second);
        }
        record["Normal"] = bake("normal", 1, normalSize, false);
        auto dataSize = size({"_DetailMainTex", "_MetallicGlossMap"});
        record["Metallic"] = bake("metallic", 2, dataSize, false);
        record["Roughness"] = bake("roughness", 3, dataSize, false);
        record["Occlusion"] = bake("occlusion", 4, size({"_OcclusionMap"}), false);
        auto emission = color({0, 0, 0, 1}, {"_EmissionColor"});
        if (number(1, {"_EmissionStrength"}) > 0 &&
                (textures["_EmissionMap"] || (std::max)({emission[0], emission[1], emission[2]}) > 0) ||
            eye && number(0, {"_Emission"}) > 0)
            record["Emission"] =
                bake("emission", 5, textures["_EmissionMap"] ? size({"_EmissionMap"}) : baseSize, true);
        slots_[slot] = records_.size();
        records_.push_back(record);
        return record;
    }

    void NativePbrMaterialExporter::Save()
    {
        WriteJson(directory_ / L"materials.json", Json{{"SchemaVersion", 3}, {"Materials", records_}});
    }
}
