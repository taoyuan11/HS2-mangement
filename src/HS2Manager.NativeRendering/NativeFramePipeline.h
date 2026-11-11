#pragma once
#include "NativeForwardRenderer.h"

namespace hs2 {
struct NativeFrameOptions {bool StaticReference=false,UseGameBackground=true;std::optional<Vector4> Background;float KeyLightMultiplier=1;};
class NativePostSettings;
// Construct, draw and destroy the entire pipeline on its GraphicsDevice owner thread.
// Only a copied composition swap-chain COM reference crosses to the WinUI thread.
class NativeFramePipeline final {
public:
    NativeFramePipeline(GraphicsDevice& graphics,NativeCharacterScene& scene,NativeGpuAssetStore& assets,NativeFrameOptions options={});
    ~NativeFramePipeline();
    NativeFramePipeline(const NativeFramePipeline&)=delete;
    NativeFramePipeline& operator=(const NativeFramePipeline&)=delete;
    void ResetHistory();
    void SetInteractive(bool value);
    void SetEnvironment(const NativeFrameOptions& options);
    const GpuTexture& Draw(NativeRenderCamera camera,int width,int height,float time);
    static void CompleteVisibility(NativeCharacterScene& scene,NativeRenderCamera camera,int width,int height);
    const GpuTexture& LastFrame()const;
    GpuCharacterAppearance& Appearance();
    GpuMaterialComposer& Composer();
    NativeForwardRenderer& Renderer();
    std::span<const float> AmbientProbe()const;
    const NativePostSettings& PostSettings()const;
    const GpuVolume* GradingLut()const;
    const std::unordered_set<std::string>& SourceReferenceDiagnostics()const;
private:
    struct State;
    std::unique_ptr<State> state_;
};
}
