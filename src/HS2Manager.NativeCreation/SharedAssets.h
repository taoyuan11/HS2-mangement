#pragma once
#include "AssetBundleBuild.h"

namespace hs2::creation
{
    CharacterDocument ApplyOverlay(CharacterDocument document, const Json &spec, Bytes png);
    void WriteAssetZip(const fs::path &root, const fs::path &output, std::stop_token token = {});
    std::string XmlText(std::string value);
    Json RenderCharacter(const fs::path &game, const fs::path &data, const fs::path &runtime,
                         const CharacterDocument &document, const Json &args, const fs::path &output,
                         PreviewResources resources = {}, std::stop_token token = {});
}
