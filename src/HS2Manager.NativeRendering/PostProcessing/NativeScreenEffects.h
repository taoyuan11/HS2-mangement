#pragma once
#include "PostProcessing/NativePostProcessor.h"

namespace hs2
{
    class NativeAmbientOcclusion
    {
    public:
        NativeAmbientOcclusion(GraphicsDevice &graphics, NativeGpuAssetStore &assets, GpuMaterialComposer &composer,
                               const NativePostSettings &settings);
        void Draw(const GpuDepthSurface &depth, GpuTexture &target, const NativeRenderCamera &camera);

    private:
        GraphicsDevice &graphics_;
        const NativePostSettings &settings_;
        NativeComputePass down1_, down2_, obscurance_, upsample_, combine_;
        NativeFullscreenPass composite_;
        GpuCopyPass copy_;
        std::unique_ptr<GpuTexture> bottomDepth_, uprightOcclusion_;
        std::unique_ptr<GpuComputeImage> linear_, output_;
        std::array<std::unique_ptr<GpuComputeImage>, 4> low_, atlas_, ao_;
        std::array<std::unique_ptr<GpuComputeImage>, 3> merged_;
        void Resize(int width, int height);
    };

    class NativeScreenSpaceReflections
    {
    public:
        NativeScreenSpaceReflections(GraphicsDevice &graphics, NativeGpuAssetStore &assets,
                                     GpuMaterialComposer &composer, const NativePostSettings &settings);

        void ResetHistory()
        {
            initialized_ = false;
        }

        void Draw(GpuTexture &target, const GpuDepthSurface &depth, NativeRenderCamera camera,
                  std::span<const GpuTexture *const> gbuffer, const GpuTexture &reflections,
                  const GpuTexture &velocity);

    private:
        GraphicsDevice &graphics_;
        const NativePostSettings &settings_;
        std::array<std::unique_ptr<NativeFullscreenPass>, 4> passes_;
        NativeComputePass gaussian_;
        GpuCopyPass copy_;
        std::shared_ptr<GpuTexture> noise_;
        std::vector<std::unique_ptr<GpuComputeImage>> pyramid_;
        std::unique_ptr<GpuTexture> test_, resolved_, history_, temporal_, sourceCopy_, bottomDepth_, bottomVelocity_,
            bottomReflections_, bottomOutput_;
        std::array<std::unique_ptr<GpuTexture>, 3> bottomGBuffer_;
        bool initialized_ = false;
        int size_ = 0, lods_ = 0;
        std::tuple<int, float, int> Quality() const;
        void Resize(int width, int height, int resolution);
    };
}
