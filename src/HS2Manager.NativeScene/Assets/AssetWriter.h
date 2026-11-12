#pragma once
#include "Assets/AssetReader.h"

namespace hs2
{
    // Symmetric with the lossless Parameter reader. Integer object identifiers never pass through double.
    std::vector<std::uint8_t> WriteUnityValue(std::span<const UnityTypeNode> schema, const Parameter &value,
                                              bool bigEndian = false);
    Parameter DefaultUnityValue(std::span<const UnityTypeNode> schema);
    Parameter DefaultUnityElement(std::span<const UnityTypeNode> schema, std::span<const std::string_view> path);
    void VisitUnityPointers(Parameter &value, const std::function<void(std::int32_t &, std::int64_t &)> &visitor);
    std::vector<std::uint8_t> WriteUnitySerializedFile(const UnitySerializedDocument &document);

    struct UnityBundleOutput
    {
        std::string Name;
        std::vector<std::uint8_t> Data;
        std::uint32_t Flags = 4;
    };

    std::vector<std::uint8_t> WriteUnityBundle(std::string_view unityVersion,
                                               std::span<const UnityBundleOutput> entries,
                                               std::stop_token cancellation = {});
}
