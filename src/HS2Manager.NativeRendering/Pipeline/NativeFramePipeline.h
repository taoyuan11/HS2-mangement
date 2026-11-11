#pragma once
#include "Pipeline/NativeForwardRenderer.h"

namespace hs2
{
    struct NativeFrameOptions
    {
        bool StaticReference = false, UseGameBackground = true;
        std::optional<Vector4> Background;
        float KeyLightMultiplier = 1;
    };
    class NativePostSettings;

    // 整个管线在 GraphicsDevice 所属线程中创建、绘制和销毁；
    // 仅将合成交换链的 COM 引用副本传给 WinUI 线程。
    class NativeFramePipeline final
    {
    public:
        NativeFramePipeline(GraphicsDevice &graphics, NativeCharacterScene &scene, NativeGpuAssetStore &assets,
                            NativeFrameOptions options = {});
        ~NativeFramePipeline();
        NativeFramePipeline(const NativeFramePipeline &) = delete;
        NativeFramePipeline &operator=(const NativeFramePipeline &) = delete;
        void ResetHistory();
        void SetInteractive(bool value);
        void SetEnvironment(const NativeFrameOptions &options);
        const GpuTexture &Draw(NativeRenderCamera camera, int width, int height, float time);
        static void CompleteVisibility(NativeCharacterScene &scene, NativeRenderCamera camera, int width, int height);
        const GpuTexture &LastFrame() const;
        GpuCharacterAppearance &Appearance();
        GpuMaterialComposer &Composer();
        NativeForwardRenderer &Renderer();
        std::span<const float> AmbientProbe() const;
        const NativePostSettings &PostSettings() const;
        const GpuVolume *GradingLut() const;
        const std::unordered_set<std::string> &SourceReferenceDiagnostics() const;

    private:
        struct State;
        std::unique_ptr<State> state_;
    };
}
