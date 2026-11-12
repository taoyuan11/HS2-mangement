#pragma once
#include "Character/CharacterAssembly.h"
#include "Character/CharacterCatalog.h"
#include "Character/CharacterBodyCatalog.h"

namespace hs2
{
    class NativeShapeRig;

    struct PreparedCharacter
    {
        std::shared_ptr<NativeCharacterScene> Scene;
        std::shared_ptr<NativeGpuAssetStore> Assets;
        std::optional<CharacterDocument> Document;
    };

    class NativeCharacterService
    {
    public:
        NativeCharacterService(std::filesystem::path game, std::filesystem::path data, std::filesystem::path tpk,
                               PreviewResources resources = {});
        PreparedCharacter Prepare(const CardRecord &card, const CharacterDocument *draft = nullptr,
                                  CharacterPreviewVisibility visibility = {}, Progress progress = {},
                                  std::stop_token cancellation = {}, bool cacheClothingStates = false,
                                  const PreparedCharacter *reuse = nullptr);

        CharacterCatalog &Catalog()
        {
            return catalog_;
        }

    private:
        std::filesystem::path game_, data_;
        PreviewResources resources_;
        CharacterCatalog catalog_;
        std::shared_ptr<UnityBlockCache> blockCache_;
        std::shared_ptr<NativeShapeRig> shapes_;
        bool unlocked_ = false;

        struct PartTemplate
        {
            CharacterPart Part;
            std::vector<std::string> Diagnostics;
        };

        std::unordered_map<std::string, PartTemplate> partTemplates_;
        std::unordered_map<std::string, Bytes> texts_;
        std::unordered_map<std::string, AssetReference> references_;
        std::vector<AssetSelection> environmentSelections_;
        std::shared_ptr<const CharacterExtensionState> extensions_;
        std::shared_ptr<const CharacterDefinition> extensionDefinition_;
        Bytes extensionBytes_, bodyParameterBytes_;
        Parameter bodySelection_;
        std::vector<BodyCatalogEntry> bodyParts_;
        bool bodySelectionCached_ = false, overlayLoaded_ = false;
        std::vector<AssetSelection> overlaySelections_;
        std::string shoeKey_;
        HeelDefinition heels_;
        std::vector<std::string> heelDiagnostics_;
    };
}
