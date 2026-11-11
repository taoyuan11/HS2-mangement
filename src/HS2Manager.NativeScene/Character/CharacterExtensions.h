#pragma once
#include "Character/CharacterDefinition.h"
#include "Common/Core.h"
#include "Preview/PreviewResourceContext.h"
#include <map>

namespace hs2
{
    struct ExtraCharacterAccessory
    {
        int Slot = 20, Category = 350, Id = 0;
        Parameter Parameters;
        std::vector<float> Adjustments, Colors;
        std::string Parent;
        std::optional<ModReference> Reference;
    };

    struct CharacterAccessoryRule
    {
        int Type = 0, Modifier = 0;
    };

    struct CharacterAccessoryControl
    {
        std::string Name, Parent;
        bool MatchHair = false;
        std::vector<CharacterAccessoryRule> Rules;
    };

    struct CharacterExtensionState
    {
        Parameter Uncensor, PushUp;
        std::vector<ExtraCharacterAccessory> Accessories;
        std::map<int, CharacterAccessoryControl> AccessoryControls;
    };

    CharacterExtensionState ReadCharacterExtensions(const CharacterDocument &document, CharacterDefinition &definition,
                                                    const fs::path &dataCache, const fs::path &game,
                                                    std::stop_token cancellation = {}, PreviewResources resources = {});
    void ApplyCharacterPushUp(CharacterDefinition &definition, const CharacterExtensionState &state,
                              std::span<const std::uint8_t> bodyAnimation, bool braWorn, bool topWorn);
}
