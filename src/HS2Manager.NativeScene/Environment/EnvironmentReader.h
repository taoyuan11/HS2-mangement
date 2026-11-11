#pragma once
#include "Character/CharacterDefinition.h"
#include "Assets/AssetStore.h"
#include "Common/Core.h"

namespace hs2
{
    std::vector<fs::path> ExtractUnityBundles(const fs::path &source, const fs::path &cache,
                                              std::stop_token cancellation = {});
    EnvironmentDefinition ReadCharacterEnvironment(const fs::path &game, const fs::path &cache,
                                                   NativeGpuAssetStore &assets, std::vector<AssetSelection> &selections,
                                                   std::vector<std::string> &diagnostics,
                                                   std::stop_token cancellation = {});
}
