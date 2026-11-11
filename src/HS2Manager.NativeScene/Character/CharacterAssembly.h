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
    // 仅应用一次 Heelz 预设参考姿态，不推进动画或物理模拟。
    void ApplyCharacterReferenceHeels(NativeCharacterScene &scene);
}
