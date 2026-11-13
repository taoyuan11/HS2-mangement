#pragma once
#include "Character/CharacterAssembly.h"

namespace hs2
{
    // 管理单个重建场景的动画、弹簧骨链和 CPU 布料。
    // Update 接收绝对播放时间，相同时间的调用保持幂等；
    // 时间回退时恢复初始姿态，并按固定步长确定性重放。
    class NativeCharacterRuntime
    {
    public:
        NativeCharacterRuntime(std::shared_ptr<NativeCharacterScene> scene, std::shared_ptr<NativeGpuAssetStore> assets,
                               std::filesystem::path physicsDirectory = {});
        ~NativeCharacterRuntime();
        NativeCharacterRuntime(const NativeCharacterRuntime &) = delete;
        NativeCharacterRuntime &operator=(const NativeCharacterRuntime &) = delete;
        void Update(float time);
        // 显式步进支持固定动画姿态下的物理收敛；
        // 即使动画时间重复，物理仍按 deltaSeconds 推进。
        void AdvanceFrame(float deltaSeconds, float animationTime);
        void Visibility(CharacterPreviewVisibility value);
        void RefreshRestPose(const NativeCharacterScene &next);
        void ResetFixturePhysics();
        void Reset();
        // 切换预览动作并恢复初始姿态，避免旧动作和物理状态残留。
        void Motion(const AssetReference &clip);
        const std::vector<std::string> &Diagnostics() const;

    private:
        struct Impl;
        std::unique_ptr<Impl> impl_;
    };
}
