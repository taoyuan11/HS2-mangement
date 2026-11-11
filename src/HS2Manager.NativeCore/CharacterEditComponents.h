#pragma once
#include "Core.h"
#include "../HS2Manager.NativeScene/CharacterDefinition.h"
namespace hs2 {
class CharacterFieldCapabilities {
public:
    static bool Visible(const CharacterField& field,const CharacterDefinition* definition);
};
}
