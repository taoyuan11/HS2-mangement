#pragma once
#include "Common/Core.h"
#include "Character/CharacterDefinition.h"

namespace hs2
{
    class CharacterFieldCapabilities
    {
    public:
        static bool Visible(const CharacterField &field, const CharacterDefinition *definition);
    };
}
