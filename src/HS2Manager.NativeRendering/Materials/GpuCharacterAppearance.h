#pragma once
#include "Materials/GpuMaterialComposer.h"

namespace hs2
{
    class GpuSkinComposer
    {
    public:
        explicit GpuSkinComposer(GpuMaterialComposer &composer) : composer_(composer)
        {
        }

        std::pair<std::shared_ptr<GpuTexture>, std::shared_ptr<GpuTexture>> Compose(
            const CharacterDefinition &definition, bool face);

    private:
        GpuMaterialComposer &composer_;
    };

    class GpuCharacterAppearance
    {
    public:
        using MaterialMap =
            std::unordered_map<NativeSceneRenderer *, std::vector<std::shared_ptr<GpuMaterialInstance>>>;
        GpuCharacterAppearance(NativeCharacterScene &scene, GpuMaterialComposer &composer);
        void UpdateVisibility();
        bool Update(const CharacterDefinition &previous);
        MaterialMap Materials;
        std::vector<NativeSceneRenderer *> Order;

    private:
        NativeCharacterScene &scene_;
        const CharacterDefinition &definition_;
        GpuMaterialComposer &composer_;
        std::unordered_map<bool, std::pair<std::shared_ptr<GpuTexture>, std::shared_ptr<GpuTexture>>> skin_;
        bool Build(const CharacterDefinition *previous = nullptr);
        std::vector<NativeSceneRenderer *> PartRenderers(const CharacterPart &part) const;
        void Skin(GpuMaterialInstance &material, bool face);
        void Eye(GpuMaterialInstance &material, int index);
        void HairShader(GpuMaterialInstance &material, const std::string &key);
        void Hair(GpuMaterialInstance &material, const CharacterPart &part, NativeSceneRenderer &renderer);
        void Accessory(GpuMaterialInstance &material, const CharacterPart &part, NativeSceneRenderer &renderer);
        void Clothes(const CharacterPart &part);
        void ClothesOverlay(const CharacterPart &part, const Parameter &config);
        void ClothingMasks(bool selectTextures = true);
        void Extensions(const CharacterPart &part, bool visibilityOnly = false);
        void DryMaker();
    };
}
