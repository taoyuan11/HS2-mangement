#pragma once
#include "Character/CharacterCatalog.h"
#include "Character/CharacterExtensions.h"

namespace hs2
{
    struct BodyCatalogEntry
    {
        std::string Kind, Guid, Name;
        int Sex = -1;
        bool Random = false;
        fs::path Archive;
        std::string Bundle, Asset;
    };

    std::vector<BodyCatalogEntry> ResolveCharacterBody(const fs::path &game, const fs::path &data,
                                                       const CharacterDocument &document, const Parameter &uncensor,
                                                       std::stop_token cancellation = {},
                                                       PreviewResources resources = {});
    HeelDefinition ReadCharacterHeels(const NativeAssetEntry *shoe, std::vector<std::string> &diagnostics);
    std::optional<ColliderSupportDefinition> ReadCharacterColliders(const fs::path &game,
                                                                    PreviewResources resources = {});
}
