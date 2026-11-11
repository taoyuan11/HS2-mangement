#pragma once
#include "PostProcessing/NativePostSettings.h"
#include "PostProcessing/NativeAntialiasing.h"

namespace hs2
{
    class NativePostProcessor
    {
    public:
        NativePostProcessor(GraphicsDevice &graphics, NativeGpuAssetStore &assets, GpuMaterialComposer &composer,
                            const EnvironmentDefinition &environment, bool staticReference = false);

        void ResetHistory()
        {
            spatial_->ResetHistory();
        }

        void Draw(GpuTexture &source, GpuTexture &target, bool sourceBottomOrigin = false,
                  bool outputBottomOrigin = false, GpuTexture *retainedCameraColor = nullptr,
                  bool forceSpatial = false);

        const NativePostSettings &Settings() const
        {
            return settings_;
        }

        const GpuVolume *GradingLut() const
        {
            return lut_.get();
        }

    private:
        GraphicsDevice &graphics_;
        GpuMaterialComposer &composer_;
        NativePostSettings settings_;
        std::unique_ptr<NativeFullscreenPass> uber_;
        std::vector<std::unique_ptr<NativeFullscreenPass>> bloom_;
        std::unique_ptr<GpuVolume> lut_;
        std::shared_ptr<GpuTexture> spectrum_;
        std::unique_ptr<NativeSpatialAntialiasing> spatial_;
        GpuCopyPass copy_;
        NativeInvalidPixelFilter invalid_;
        std::vector<std::unique_ptr<GpuTexture>> temporary_;

        float CG(const std::string &name, float fallback = 0) const
        {
            return settings_.Scalar("ColorGrading", name, fallback);
        }

        void GenerateLut(NativeGpuAssetStore &assets);
        std::pair<GpuTexture *, float> Bloom(const GpuTexture &source);
        GpuTexture &Temp(int width, int height);
    };

    NativeComputeKernel ComputeKernel(GpuMaterialComposer &composer, NativeGpuAssetStore &assets,
                                      const std::string &asset, const std::string &name);
}
