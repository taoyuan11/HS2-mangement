#pragma once
#include <map>
#include <span>
#include <string>
#include <vector>
#include <cstdint>

namespace hs2
{
    struct SceneXmlNode
    {
        std::string Name, Text;
        std::map<std::string, std::string> Attributes;
        std::vector<SceneXmlNode> Children;
        const SceneXmlNode *Child(std::string_view name) const;
        const SceneXmlNode *Descendant(std::string_view name) const;
        std::string Value(std::string_view name, std::string fallback = {}) const;
        std::string Attribute(std::string_view name, std::string fallback = {}) const;
    };

    SceneXmlNode ReadSceneXml(std::span<const std::uint8_t> bytes);
}
