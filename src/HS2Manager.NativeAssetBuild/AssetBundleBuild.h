#pragma once
#include "AssetGeometry.h"
#include "Assets/AssetWriter.h"

namespace hs2::asset
{
    struct MaterialTemplate
    {
        NativeAssetEntry Entry;
        AssetReference Prefab;
        std::shared_ptr<NativeGpuAssetStore> Assets;
        std::shared_ptr<const UnityClassDatabase> Database;
        std::shared_ptr<const GpuPrefabData> Data;
        GpuAssetPointer Component;
        std::string ComponentName;
    };

    std::string AssetId(const NativeAssetEntry &entry);
    Json TemplateChoices(CharacterCatalog &catalog, int category, int limit = 12);
    MaterialTemplate ResolveTemplate(CharacterCatalog &catalog, const CharacterDocument &document, const Json &spec,
                                     const fs::path &game, std::stop_token token = {});
    Json BuildGameBundle(const Geometry &geometry, const MaterialTemplate &source, const Json &spec,
                         const fs::path &game, const fs::path &output, std::stop_token token = {});
    void WriteSideloaderList(const MaterialTemplate &source, const Json &spec, const Json &built,
                             const fs::path &output);
}
