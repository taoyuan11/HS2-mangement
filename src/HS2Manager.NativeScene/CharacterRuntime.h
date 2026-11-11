#pragma once
#include "CharacterAssembly.h"

namespace hs2 {
// Owns animation, spring chains and CPU cloth for a single reconstructed scene.
// Update receives absolute playback time. Equal times are idempotent; moving
// backwards restores the initial pose and deterministically replays fixed steps.
class NativeCharacterRuntime {
public:
    NativeCharacterRuntime(std::shared_ptr<NativeCharacterScene> scene,
        std::shared_ptr<NativeGpuAssetStore> assets,std::filesystem::path physicsDirectory={});
    ~NativeCharacterRuntime();
    NativeCharacterRuntime(const NativeCharacterRuntime&)=delete;
    NativeCharacterRuntime& operator=(const NativeCharacterRuntime&)=delete;
    void Update(float time);
    // Explicit stepping also supports fixed animation poses while physics settles.
    // Repeated animation times still advance physics by deltaSeconds.
    void AdvanceFrame(float deltaSeconds,float animationTime);
    void ResetFixturePhysics();
    void Reset();
    const std::vector<std::string>& Diagnostics()const;
private:
    struct Impl;std::unique_ptr<Impl> impl_;
};
}
