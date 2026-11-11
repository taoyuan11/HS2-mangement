#pragma once
#include "CharacterCatalog.h"
namespace hs2 {
void ReadCharacterShaders(const fs::path& game,const fs::path& data,CharacterCatalog& catalog,NativeGpuAssetStore& assets,CharacterDefinition& definition,std::stop_token cancellation={});
}
