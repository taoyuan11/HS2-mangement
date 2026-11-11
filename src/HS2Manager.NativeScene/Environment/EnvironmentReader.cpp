#include "Environment/EnvironmentReader.h"
#include "Scene/SceneXml.h"
#include "Animation/SimulationMath.h"
#include <charconv>
#include <cstring>
#include <fstream>
#include <regex>
#include <set>
#include <sstream>

namespace hs2
{
    namespace
    {
        std::string ReadText(const fs::path &path)
        {
            auto bytes = ReadFile(path, 16 * 1024 * 1024);
            return {bytes.begin(), bytes.end()};
        }

        std::string Trim(std::string value)
        {
            auto first = value.find_first_not_of(" \t\r\n"), last = value.find_last_not_of(" \t\r\n");
            return first == value.npos ? std::string{} : value.substr(first, last - first + 1);
        }

        float Number(const std::string &text, float fallback = 0)
        {
            try
            {
                float value = std::stof(text);
                return std::isfinite(value) ? value : fallback;
            }
            catch (...)
            {
                return fallback;
            }
        }

        Parameter &Child(Parameter &p, std::string_view name)
        {
            for (auto &c : p.Children)
                if (c.Name == name)
                    return c;
            Parameter v;
            v.Name = name;
            p.Children.push_back(v);
            return p.Children.back();
        }

        GpuAssetPointer Pointer(const fs::path &file, const Parameter &p)
        {
            return {file, static_cast<int>(p.Int("m_FileID")), p.Int("m_PathID")};
        }

        const UnityAssetInfo &Single(const NativeGpuAssetReader &reader, int type)
        {
            auto found = std::find_if(reader.AssetIndex().begin(), reader.AssetIndex().end(),
                                      [&](auto &p)
                                      {
                                          return p.ClassId == type;
                                      });
            if (found == reader.AssetIndex().end())
                throw std::runtime_error("Environment asset class is missing: " + std::to_string(type));
            return *found;
        }

        std::map<std::string, std::string> Tags(const std::string &source)
        {
            std::map<std::string, std::string> values;
            std::regex pattern("\\?tag=([^&]+)&=([^&]*)&");
            for (auto it = std::sregex_iterator(source.begin(), source.end(), pattern); it != std::sregex_iterator();
                 ++it)
                values[(*it)[1].str()] = (*it)[2].str();
            return values;
        }
    }

    std::vector<fs::path> ExtractUnityBundles(const fs::path &source, const fs::path &cache,
                                              std::stop_token cancellation)
    {
        if (!fs::is_regular_file(source))
            return {};
        auto bytes = ReadFile(source, 128 * 1024 * 1024, cancellation);
        std::vector<fs::path> result;
        const uint8_t signature[]{'U', 'n', 'i', 't', 'y', 'F', 'S', 0};
        size_t cursor = 0;
        auto be = [&](size_t offset, size_t count)
        {
            uint64_t value = 0;
            if (offset > bytes.size() || count > bytes.size() - offset)
                throw std::runtime_error("Embedded bundle header is truncated.");
            for (size_t i = 0; i < count; ++i)
                value = value << 8 | bytes[offset + i];
            return value;
        };
        while (cursor + 48 < bytes.size())
        {
            CheckCancelled(cancellation);
            auto found = std::search(bytes.begin() + cursor, bytes.end(), std::begin(signature), std::end(signature));
            if (found == bytes.end())
                break;
            size_t start = found - bytes.begin();
            cursor = start + 8;
            auto version = be(cursor, 4);
            if (version < 6 || version > 8)
                continue;
            size_t position = cursor + 4;
            bool valid = true;
            for (int s = 0; s < 2; ++s)
            {
                auto end =
                    std::find(bytes.begin() + position, bytes.begin() + std::min(bytes.size(), position + 128), 0);
                if (end == bytes.begin() + std::min(bytes.size(), position + 128))
                {
                    valid = false;
                    break;
                }
                position = end - bytes.begin() + 1;
            }
            if (!valid || position + 8 > bytes.size())
                continue;
            auto length = be(position, 8);
            if (length < 48 || length > bytes.size() - start)
                continue;
            auto data = std::span(bytes).subspan(start, static_cast<size_t>(length));
            auto target = cache / Path(Sha256(data) + ".unity3d");
            if (!fs::exists(target) || HashFile(target, cancellation) != Sha256(data))
                WriteAtomic(target, data);
            result.push_back(target);
            cursor = start + static_cast<size_t>(length);
        }
        return result;
    }

    EnvironmentDefinition ReadCharacterEnvironment(const fs::path &game, const fs::path &cache,
                                                   NativeGpuAssetStore &assets, std::vector<AssetSelection> &selections,
                                                   std::vector<std::string> &diagnostics, std::stop_token cancellation,
                                                   PreviewResources resources)
    {
        using namespace sim;
        EnvironmentDefinition result;
        auto root = game / "HoneySelect2_Data";
        auto build = assets.Reader(root / "globalgamemanagers");
        result.Physics = build->Metadata(Single(*build, 55).PathId);
        auto quality = build->Metadata(Single(*build, 47).PathId);
        int selected = static_cast<int>(quality.Int("m_CurrentQuality"));
        auto settings = build->Metadata(Single(*build, 141).PathId);
        int level = -1;
        auto &scenes = settings.Get("scenes").Children;
        for (size_t i = 0; i < scenes.size(); ++i)
            if (Path(scenes[i].Text).stem() == L"CharaCustom")
            {
                level = static_cast<int>(i);
                break;
            }
        Require(level >= 0, "Character maker scene is missing.");
        auto scene = assets.Reader(root / Path("level" + std::to_string(level)));
        assets.AddPath(scene->Path);
        std::unordered_map<int64_t, int64_t> transforms;
        for (auto &info : scene->AssetIndex())
            if (info.ClassId == 4)
                transforms[scene->Metadata(info.PathId).Get("m_GameObject").Int("m_PathID")] = info.PathId;
        auto active = [&](int64_t object)
        {
            std::set<int64_t> visited;
            while (true)
            {
                Require(visited.insert(object).second, "Cyclic environment hierarchy.");
                auto &go = scene->Metadata(object);
                if (go.Int("m_IsActive") == 0)
                    return false;
                auto &transform = scene->Metadata(transforms.at(object));
                auto parent = transform.Get("m_Father").Int("m_PathID");
                if (!parent)
                    return true;
                object = scene->Metadata(parent).Get("m_GameObject").Int("m_PathID");
            }
        };
        for (auto &info : scene->AssetIndex())
            if (info.ClassId == 108)
            {
                CheckCancelled(cancellation);
                auto light = scene->Metadata(info.PathId);
                if (light.Int("m_Enabled") == 0)
                    continue;
                auto object = light.Get("m_GameObject").Int("m_PathID");
                auto &go = scene->Metadata(object);
                auto transform = scene->Metadata(transforms.at(object));
                Quaternion rotation{0, 0, 0, 1};
                bool visible = true, relative = false;
                std::set<int64_t> visited;
                while (true)
                {
                    auto id = transform.Get("m_GameObject").Int("m_PathID");
                    Require(visited.insert(id).second, "Cyclic light hierarchy.");
                    auto &parentGo = scene->Metadata(id);
                    visible = visible && parentGo.Int("m_IsActive") != 0;
                    for (auto &component : parentGo.Get("m_Component").Children)
                        if (scene->AssetClass(component.Get("component").Int("m_PathID")) == 20)
                            relative = true;
                    if (relative)
                        break;
                    auto &q = transform.Get("m_LocalRotation");
                    rotation = Mul({q.Float("x"), q.Float("y"), q.Float("z"), q.Float("w")}, rotation);
                    auto parent = transform.Get("m_Father").Int("m_PathID");
                    if (!parent)
                        break;
                    transform = scene->Metadata(parent);
                }
                if (visible)
                    result.Lights.push_back({go.String("m_Name"),
                                             relative,
                                             {rotation.x, rotation.y, rotation.z, rotation.w},
                                             std::move(light)});
            }
        result.RenderSettings = scene->Metadata(Single(*scene, 104).PathId);
        for (auto &info : scene->AssetIndex())
            if (info.ClassId == 114)
            {
                CheckCancelled(cancellation);
                auto header = scene->MetadataHeader(info.PathId);
                auto script = assets.Metadata(Pointer(scene->Path, header.Get("m_Script")));
                auto className = script.String("m_ClassName");
                if (className == "CameraControl_Ver2")
                {
                    auto object = header.Get("m_GameObject").Int("m_PathID");
                    for (auto &camera : scene->AssetIndex())
                        if (camera.ClassId == 20)
                        {
                            auto data = scene->Metadata(camera.PathId);
                            if (data.Get("m_GameObject").Int("m_PathID") == object)
                                result.Camera = std::move(data);
                        }
                }
                if (className != "PostProcessVolume")
                    continue;
                auto component = scene->Metadata(info.PathId);
                if (component.Int("m_Enabled") == 0 || component.Int("isGlobal") == 0 ||
                    !active(component.Get("m_GameObject").Int("m_PathID")))
                    continue;
                auto profilePointer = assets.Resolve(Pointer(scene->Path, component.Get("sharedProfile")));
                if (!profilePointer.PathId)
                    continue;
                auto profile = assets.Metadata(profilePointer);
                PostProfile output;
                output.Weight = component.Float("weight");
                output.Priority = component.Float("priority");
                for (auto &reference : profile.Get("settings").Children)
                {
                    auto effectPointer = assets.Resolve(Pointer(profilePointer.Bundle, reference));
                    auto effect = assets.Metadata(effectPointer);
                    effect.Name = effect.String("m_Name");
                    for (auto &property : effect.Children)
                    {
                        auto &value = property.Get("value");
                        if (!value.Has("m_FileID") || !value.Has("m_PathID") || !value.Int("m_PathID"))
                            continue;
                        auto pointer = assets.Resolve(Pointer(effectPointer.Bundle, value));
                        if (assets.AssetClass(pointer) != 28)
                            continue;
                        auto texture = assets.Metadata(pointer);
                        Require(texture.Int("m_Width") <= 8192 && texture.Int("m_Height") <= 8192,
                                "Environment texture size exceeds limit.");
                        std::string key =
                            "environment.profile." + std::to_string(info.PathId) + "." + std::to_string(pointer.PathId);
                        Child(property, "value").Text = key;
                        selections.push_back({key, {pointer.Bundle, texture.String("m_Name")}});
                        assets.AddPath(pointer.Bundle);
                    }
                    output.Effects.push_back(std::move(effect));
                }
                result.Profiles.push_back(std::move(output));
            }
        auto setup = PreviewResourceFile(game, resources, "UserData/setup.xml"),
             system = PreviewResourceFile(game, resources, "UserData/config/system.xml");
        if (fs::is_regular_file(setup))
        {
            auto xml = ReadSceneXml(ReadFile(setup, 4 * 1024 * 1024));
            auto qualityText = xml.Value("Quality");
            if (!qualityText.empty())
            {
                bool shadow = true;
                if (fs::is_regular_file(system))
                {
                    auto config = ReadSceneXml(ReadFile(system, 4 * 1024 * 1024));
                    if (auto *self = config.Descendant("SelfShadow"))
                        shadow = self->Text != "False";
                }
                selected = std::clamp(static_cast<int>(Number(qualityText)), 0, 2) * 2 + (shadow ? 0 : 1);
            }
            for (std::string axis : {"Width", "Height"})
            {
                auto size = static_cast<int>(Number(xml.Value(axis)));
                if (size >= 128 && size <= 16384)
                    Child(result.Camera, "gameScreen" + axis).Number = size;
            }
        }
        Require(selected >= 0 && static_cast<size_t>(selected) < quality.Get("m_QualitySettings").Children.size(),
                "Quality preset index is invalid.");
        result.Quality = quality.Get("m_QualitySettings").Children[selected];
        if (fs::is_regular_file(
                PreviewResourceFile(game, resources, "BepInEx/plugins/IllusionFixes/HS2_Fix_GravityAdjust.dll")))
            Child(Child(result.Physics, "m_Gravity"), "y").Number = -98.1;
        auto custom = PreviewResourceFile(game, resources, "UserData/custom/customscene.dat");
        if (fs::is_regular_file(custom))
        {
            auto bytes = ReadFile(custom, 16 * 1024 * 1024);
            size_t cursor = 0, length = 0;
            int shift = 0;
            do
            {
                Require(cursor < bytes.size() && shift <= 28, "Custom scene header is invalid.");
                auto value = bytes[cursor++];
                length |= size_t(value & 127) << shift;
                shift += 7;
                if (!(value & 128))
                    break;
            } while (true);
            Require(length <= bytes.size() - cursor && bytes.size() - cursor - length >= 12,
                    "Custom scene settings are truncated.");
            cursor += length;
            for (int i = 0; i < 3; ++i)
            {
                std::memcpy(&result.Background[i], bytes.data() + cursor, 4);
                cursor += 4;
                Require(std::isfinite(result.Background[i]), "Invalid scene background.");
            }
        }
        auto select = [&](const std::string &key, const fs::path &bundle, const std::string &name)
        {
            assets.AddPath(bundle);
            selections.push_back({key, {bundle, name}});
        };
        auto user = PreviewResourceFile(game, resources, "DHH_Data/UserSetting.ini"),
             graphic = PreviewResourceFile(game, resources, "DHH_Data/GraphicSetting.dhs");
        if (fs::is_regular_file(user) && fs::is_regular_file(graphic))
        {
            result.DhhEnabled = Tags(ReadText(user))["GraphicEnable"] == "True";
            auto values = Tags(ReadText(graphic));
            for (int i = 0; i < 50; ++i)
                result.DhhFloats[i] = Number(values["GSfloat_" + std::to_string(i)]);
            for (int i = 0; i < 15; ++i)
                result.DhhBools[i] = values["GSbool_" + std::to_string(i)] == "True";
            std::regex number("[-+]?\\d+(?:\\.\\d+)?(?:[Ee][-+]?\\d+)?");
            for (int i = 0; i < 4; ++i)
            {
                auto text = values["GSvector3_" + std::to_string(i)];
                int index = 0;
                for (auto it = std::sregex_iterator(text.begin(), text.end(), number);
                     it != std::sregex_iterator() && index < 3; ++it)
                    result.DhhColors[i * 3 + index++] = Number(it->str());
            }
            result.SkyboxId = static_cast<int>(Number(values["SkyboxID"]));
            result.LutPath = Utf8(PreviewResourceFile(
                game, resources,
                "DHH_Data/LutTextures/Lut_" + std::to_string(static_cast<int>(result.DhhFloats[43])) + ".png"));
            if (result.DhhEnabled)
            {
                auto bundle = PreviewResourceFile(game, resources, "DHH_Data/ModData/dhpost");
                Require(fs::is_regular_file(bundle), "DHH environment resources are missing.");
                assets.AddPath(bundle);
                if (result.SkyboxId > 0 && result.SkyboxId < 6)
                {
                    select("environment.skybox", bundle, "hdri" + std::to_string(result.SkyboxId));
                    select("environment.reflection", bundle, "cube" + std::to_string(result.SkyboxId));
                }
                else
                    diagnostics.push_back("DHH 当前环境贴图配置尚未完成还原：" + std::to_string(result.SkyboxId));
            }
        }
        auto aaConfig = PreviewResourceFile(game, resources, "BepInEx/config/BetterAA.cfg"),
             aaPlugin = PreviewResourceFile(game, resources, "BepInEx/plugins/HS2_BetterAA.dll");
        if (fs::is_regular_file(aaConfig) && fs::is_regular_file(aaPlugin))
        {
            std::map<std::string, std::string> config;
            std::istringstream lines(ReadText(aaConfig));
            for (std::string line; std::getline(lines, line);)
            {
                if (line.starts_with('#'))
                    continue;
                auto at = line.find('=');
                if (at != line.npos)
                    config[Trim(line.substr(0, at))] = Trim(line.substr(at + 1));
            }
            result.BetterAA = config["Enable TAA"] == "true";
            const char *keys[]{"Temporal Stability",      "Hdr Response",
                               "Adaptive Enhance",        "Temporal Jitter Scale",
                               "Micro Shimmer Reduction", "Static Stability Power"};
            for (size_t i = 0; i < 6; ++i)
                if (config.contains(keys[i]))
                    result.Temporal[i] = Number(config[keys[i]]);
            if (result.BetterAA)
                for (auto &bundle : ExtractUnityBundles(aaPlugin, cache / "shader-data", cancellation))
                {
                    assets.AddPath(bundle);
                    auto reader = assets.Reader(bundle);
                    for (std::string name : {"CTAA_PC", "CTAA_Enhance_PC"})
                        if (reader->TryFind(name, 48))
                            select("environment." + name, bundle, name);
                }
        }
        return result;
    }
}
