#pragma once
#include "CharacterAssembly.h"
#include "CharacterCatalog.h"

namespace hs2 {
struct PreparedCharacter {
    std::shared_ptr<NativeCharacterScene> Scene;
    std::shared_ptr<NativeGpuAssetStore> Assets;
};
class NativeCharacterService {
public:
    NativeCharacterService(std::filesystem::path game,std::filesystem::path data,std::filesystem::path tpk);
    PreparedCharacter Prepare(const CardRecord& card,const CharacterDocument* draft=nullptr,
        CharacterPreviewVisibility visibility={},Progress progress={},std::stop_token cancellation={});
    CharacterCatalog& Catalog(){return catalog_;}
private:
    std::filesystem::path game_,data_;
    CharacterCatalog catalog_;
};
}
