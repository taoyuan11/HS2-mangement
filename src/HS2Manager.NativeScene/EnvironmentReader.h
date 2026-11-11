#pragma once
#include "CharacterDefinition.h"
#include "AssetStore.h"
#include "../HS2Manager.NativeCore/Core.h"
namespace hs2 {
std::vector<fs::path> ExtractUnityBundles(const fs::path& source,const fs::path& cache,std::stop_token cancellation={});
EnvironmentDefinition ReadCharacterEnvironment(const fs::path& game,const fs::path& cache,NativeGpuAssetStore& assets,
    std::vector<AssetSelection>& selections,std::vector<std::string>& diagnostics,std::stop_token cancellation={});
}
