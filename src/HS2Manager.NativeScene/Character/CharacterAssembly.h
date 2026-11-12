#pragma once
#include "Assets/AssetStore.h"
#include "Character/CharacterDefinition.h"

namespace hs2
{
    std::vector<ComponentDefinition> ReadCharacterComponents(NativeGpuAssetReader &reader, const GpuPrefabData &prefab,
                                                             std::vector<std::string> &diagnostics);
    std::shared_ptr<NativeCharacterScene> AssembleCharacter(std::shared_ptr<CharacterDefinition> definition,
                                                            NativeGpuAssetStore &assets,
                                                            std::stop_token cancellation = {});
    void PoseCharacterFace(NativeCharacterScene &scene, float eyeOpen, bool dynamic = false, float horizontal = 0,
                           float vertical = 0, bool gaze = false);
    void ApplyCharacterVisibility(NativeCharacterScene &scene);
    // 保留相同网格的渲染器；返回值表示骨架或部件结构是否需要刷新运行姿态。
    bool CharacterSceneRequiresRuntimeReset(const NativeCharacterScene &scene, const NativeCharacterScene &next);
    bool CharacterSceneTopologyMatches(const NativeCharacterScene &scene, const NativeCharacterScene &next);
    bool UpdateCharacterScene(NativeCharacterScene &scene, NativeCharacterScene &next);
    // 仅应用一次 Heelz 预设参考姿态，不推进动画或物理模拟。
    void ApplyCharacterReferenceHeels(NativeCharacterScene &scene);
}
