#pragma once
#include "Character/CharacterDefinition.h"

namespace hs2
{
    class NativeClothRuntime
    {
    public:
        NativeClothRuntime(NativeCharacterScene &scene, std::filesystem::path directory);
        ~NativeClothRuntime();
        void Reset();
        void Step(float seconds);
        bool HasCloth() const;

    private:
        struct Impl;
        std::unique_ptr<Impl> impl_;
    };
}
