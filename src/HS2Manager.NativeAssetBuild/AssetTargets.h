#pragma once
#include "AssetBundleBuild.h"

namespace hs2::asset
{
    // 制作、Schema 和验收共用同一映射，避免新增编辑字段只能选择旧素材。
    Json CreationTarget(const CharacterDocument &document, const std::string &path);
    Json CreationTargets(const CharacterDocument &document);
    bool TargetEnabled(const CharacterDocument &document, const std::string &path);
    Json FaceHeadBinding(const CharacterDocument &document);
    NativeAssetEntry CatalogTemplate(CharacterCatalog &catalog, const CharacterDocument &document, const Json &target);
    Json BuildCatalogAsset(const NativeAssetEntry &source, const Json &spec, const fs::path &output,
                           std::stop_token token = {});
    void WriteCatalogList(const NativeAssetEntry &source, const Json &spec, const Json &built, const fs::path &output);
    Json HeadReference(const MaterialTemplate &source, std::stop_token token = {});
    Json BuildHeadBundle(const MaterialTemplate &source, const Json &spec, const fs::path &game, const fs::path &output,
                         std::stop_token token = {});
    Json BuildTextureBundle(CharacterCatalog &catalog, const Json &spec, const fs::path &game, const fs::path &output,
                            std::stop_token token = {});
}
