#pragma once
#include "Scene/SceneTypes.h"
#include "Environment/EnvironmentDefinition.h"

namespace hs2
{
    struct CharacterPreviewVisibility
    {
        int Clothing = 0;
        bool Accessories = true;

        bool operator==(CharacterPreviewVisibility const &) const = default;

        bool ClothesVisible(int slot) const
        {
            return Clothing == 0 || (Clothing == 1 && slot >= 2 && slot <= 6);
        }
    };

    struct AssetReference
    {
        std::filesystem::path Bundle;
        std::string Asset;
        bool operator==(const AssetReference &) const = default;
    };

    struct AssetSelection
    {
        std::string Key;
        AssetReference Reference;
        bool operator==(const AssetSelection &) const = default;
    };

    struct ComponentDefinition
    {
        std::string Node, Type;
        Parameter Data;
    };

    struct CharacterPart
    {
        std::string Role;
        int Slot = 0;
        AssetReference Prefab;
        std::string Parent, RuntimeName;
        std::vector<BoneDefinition> Bones, UnmodifiedBones;
        std::vector<ComponentDefinition> Components;
        std::vector<float> Adjustments, Colors;
        std::vector<bool> HideOptions;
        Parameter List, Parameters;
    };

    struct EmbeddedTextureReference
    {
        std::filesystem::path Path;
        std::uint64_t Offset = 0;
        int Length = 0;
        bool operator==(const EmbeddedTextureReference &) const = default;
    };

    struct MaterialEdit
    {
        int ObjectType = 0, Slot = 0;
        std::string Name, Property, Kind, Text;
        float Number = 0;
        std::vector<float> Values, Offset, Scale;
        std::optional<EmbeddedTextureReference> Texture;
        bool operator==(const MaterialEdit &) const = default;
    };

    struct OverlayDefinition
    {
        int Type = 0;
        std::string Name;
        bool Replace = false;
        std::optional<EmbeddedTextureReference> Texture;
        bool operator==(const OverlayDefinition &) const = default;
    };

    struct HeelBone
    {
        std::string Name;
        Vector3 Move{}, Roll{}, Scale{1, 1, 1};
        bool Fixed = false;
    };

    struct HeelDefinition
    {
        float Height = 0;
        std::vector<HeelBone> Bones;
    };

    // 首次准备时缓存三种着装状态的塑形数据，预览切换不再读取资源或重建网格。
    struct CharacterClothingState
    {
        Parameter Body;
        std::vector<BoneDefinition> Bones, UnmodifiedBones;
        HeelDefinition Heels;
        std::vector<int> HiddenAccessories;
    };

    struct ColliderSupportDefinition
    {
        bool Breast = false, Floor = false;
    };

    struct ShaderDefault
    {
        std::string Property, Type;
        std::vector<float> Values;
        std::optional<AssetReference> Texture;
    };

    struct ShaderDefinition
    {
        std::string Name;
        bool AutoReplace = false;
        std::optional<AssetReference> Source;
        int RenderQueue = -1;
        std::vector<ShaderDefault> Defaults;
    };

    struct CharacterDefinition
    {
        int SchemaVersion = 1;
        std::int64_t EditRevision = 0;
        CharacterPreviewVisibility Visibility;
        std::vector<CharacterClothingState> ClothingStates;
        std::string Name, CardHash, MotionState;
        int Sex = 0;
        std::vector<std::filesystem::path> BundlePaths;
        std::vector<std::shared_ptr<CharacterPart>> Parts;
        std::vector<AssetSelection> Selections;
        Parameter Face, Body, Hair, Clothes;
        std::vector<std::string> Diagnostics;
        bool BodyVisible = true;
        float EyeOpenMax = 1;
        std::vector<MaterialEdit> MaterialEdits;
        std::vector<BoneAdjustment> BoneModifiers;
        std::vector<ShaderDefinition> Shaders;
        std::vector<OverlayDefinition> Overlays;
        HeelDefinition Heels;
        std::optional<ColliderSupportDefinition> ColliderSupport;
        EnvironmentDefinition Environment;
    };

    struct NativeScenePart
    {
        std::shared_ptr<CharacterPart> Definition;
        NativeSceneNode *Root = nullptr;
        std::vector<NativeSceneNode *> Nodes;
        std::unordered_map<std::string, NativeSceneNode *> ByName;
        std::vector<GpuComponentData> Components;
        std::vector<GpuNodeData> SourceNodes;
    };
}
