#pragma once
#include "AssetStore.h"
#include "CharacterDefinition.h"

namespace hs2 {
std::vector<ComponentDefinition> ReadCharacterComponents(NativeGpuAssetReader& reader,
    const GpuPrefabData& prefab,std::vector<std::string>& diagnostics);
std::shared_ptr<NativeCharacterScene> AssembleCharacter(std::shared_ptr<CharacterDefinition> definition,
    NativeGpuAssetStore& assets,std::stop_token cancellation={});
void PoseCharacterFace(NativeCharacterScene& scene,float eyeOpen,bool dynamic=false,float horizontal=0,float vertical=0,bool gaze=false);
void ApplyCharacterVisibility(NativeCharacterScene& scene);
// Applies the authored Heelz reference pose once, without advancing animation or physics.
void ApplyCharacterReferenceHeels(NativeCharacterScene& scene);
}
