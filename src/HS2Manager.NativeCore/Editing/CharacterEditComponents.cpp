#include "Editing/CharacterEditComponents.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <regex>

namespace hs2
{
    namespace
    {
        const Parameter &At(const Parameter &value, std::size_t index)
        {
            static const Parameter empty;
            auto items = value.Array();
            return index < items.size() ? items[index] : empty;
        }

        const Parameter *Component(const std::vector<ComponentDefinition> &components, std::string_view type)
        {
            for (auto &component : components)
                if (component.Type == type)
                    return &component.Data;
            return nullptr;
        }

        CharacterDocument SetComponentColor(CharacterDocument document, const std::string &path,
                                            const Parameter &parameter)
        {
            auto node = document.Node(path);
            std::array<float, 4> values{parameter.Float("r"), parameter.Float("g"), parameter.Float("b"),
                                        parameter.Float("a", 1)};
            for (auto value : values)
                if (!std::isfinite(value) || value < 0 || value > 1)
                    throw std::runtime_error("素材默认颜色超出有效范围");
            for (std::size_t i = 0; i < (std::min)(node.Count(), values.size()); i++)
                node = node.Set(static_cast<std::int64_t>(i),
                                node.Require(static_cast<std::int64_t>(i)).WithScalar(values[i]));
            return document.WithNode(path, node);
        }
    }

    CharacterDocument CharacterEditRules::InitializeAsset(CharacterDocument document, const std::string &path,
                                                          const std::vector<ComponentDefinition> &components)
    {
        if (!path.ends_with(".id"))
            return document;
        auto root = path.substr(0, path.size() - 3);
        if (auto hair = Component(components, "CmpHair"); path.starts_with("hair.") && hair)
        {
            std::vector<std::pair<MessagePack::Value, MessagePack>> groups;
            auto bones = hair->Get("boneInfo").Array();
            for (std::size_t i = 0; i < bones.size(); i++)
            {
                auto rate = [&](const std::string &property)
                {
                    std::vector<MessagePack> values;
                    for (auto axis : {"x", "y", "z"})
                    {
                        float minimum = bones[i].Get(property + "Min").Float(axis),
                              maximum = bones[i].Get(property + "Max").Float(axis),
                              value = bones[i].Get(property == "pos" ? "basePos" : "baseRot").Float(axis);
                        values.push_back(MessagePack::Scalar(
                            minimum == maximum ? 0.f : std::clamp((value - minimum) / (maximum - minimum), 0.f, 1.f)));
                    }
                    return MessagePack::Array(std::move(values));
                };
                groups.emplace_back(static_cast<std::int64_t>(i),
                                    MessagePack::Map({{std::string("moveRate"), rate("pos")},
                                                      {std::string("rotRate"), rate("rot")},
                                                      {std::string("noShake"), MessagePack::Scalar(false)}}));
            }
            document = document.WithNode(root + ".dictBundle", MessagePack::Map(std::move(groups)));
            auto colors = hair->Get("acsDefColor").Array();
            for (std::size_t i = 0; i < colors.size(); i++)
            {
                auto color = root + ".acsColorInfo[" + std::to_string(i) + "].color";
                if (document.Contains(color))
                    document = SetComponentColor(document, color, colors[i]);
            }
        }
        if (auto accessory = Component(components, "CmpAccessory");
            path.starts_with("accessory.") && accessory && accessory->Float("typeHair") == 0)
            for (int i = 0; i < 4; i++)
            {
                if (i == 3 ? accessory->Get("rendAlpha").Array().empty()
                           : accessory->Float("useColor0" + std::to_string(i + 1)) == 0)
                    continue;
                auto color = root + ".colorInfo[" + std::to_string(i) + "]", suffix = "0" + std::to_string(i + 1);
                document = SetComponentColor(document, color + ".color", accessory->Get("defColor" + suffix))
                               .WithValue(color + ".glossPower", accessory->Float("defGlossPower" + suffix))
                               .WithValue(color + ".metallicPower", accessory->Float("defMetallicPower" + suffix));
            }
        return document;
    }

    bool CharacterFieldCapabilities::Visible(const CharacterField &field, const CharacterDefinition *definition)
    {
        if (!definition)
            return true;
        std::smatch match;
        if (!std::regex_match(field.Path, match, std::regex(R"(^(hair|clothes|accessory)\.parts\[(\d+)\]\.(.+)$)")))
            return true;
        std::string role = match[1], path = match[3];
        auto slot = std::stoi(match[2]);
        if (path == "id" || path == "type")
            return true;
        const CharacterPart *part = nullptr;
        for (auto &candidate : definition->Parts)
            if (candidate->Role == role && candidate->Slot == slot)
            {
                part = candidate.get();
                break;
            }
        if (!part)
            return false;
        auto data = Component(part->Components, role == "hair"      ? "CmpHair"
                                                : role == "clothes" ? "CmpClothes"
                                                                    : "CmpAccessory");
        if (!data)
            return true;
        auto flag = [&](const std::string &key)
        {
            return data->Float(key) != 0;
        };
        auto references = [&](const std::string &key)
        {
            auto values = data->Get(key).Array();
            return std::any_of(values.begin(), values.end(),
                               [](auto &p)
                               {
                                   return !p.Text.empty();
                               });
        };
        if (role == "hair")
        {
            if (path == "topColor")
                return flag("useTopColor");
            if (path == "underColor")
                return flag("useUnderColor");
            if (path.starts_with("mesh"))
                return flag("useMesh");
            if (std::regex_search(path, match, std::regex(R"(^acsColorInfo\[(\d+)\])")))
                return flag("useAcsColor0" + std::to_string(std::stoi(match[1]) + 1));
            if (std::regex_search(path, match, std::regex(R"(^dictBundle\{(\d+)\})")))
                return !At(data->Get("boneInfo"), std::stoul(match[1])).String("trfCorrect").empty();
        }
        else if (role == "clothes")
        {
            if (path == "breakRate")
                return flag("useBreak");
            if (std::regex_search(path, match, std::regex(R"(^hideOpt\[(\d+)\])")))
                return references("objOpt0" + std::to_string(std::stoi(match[1]) + 1));
            if (std::regex_search(path, match, std::regex(R"(^colorInfo\[(\d+)\])")))
            {
                auto suffix = "0" + std::to_string(std::stoi(match[1]) + 1);
                return flag("useColorN" + suffix) || flag("useColorA" + suffix);
            }
        }
        else
        {
            if (std::regex_match(path, match, std::regex(R"(^colorInfo\[(\d+)\]\.(.+)$)")))
            {
                int channel = std::stoi(match[1]);
                if (flag("typeHair"))
                    return match[2] == "color"
                               ? (channel == 0 || channel == 3 || flag("useColor0" + std::to_string(channel + 1)))
                               : channel == 0;
                return channel == 3 ? references("rendAlpha") : flag("useColor0" + std::to_string(channel + 1));
            }
            if (std::regex_search(path, match, std::regex(R"(^addMove\[2\]\[(\d+)\])")))
                return !data->String("trfMove0" + std::to_string(std::stoi(match[1]) / 3 + 1)).empty();
        }
        return true;
    }
}
