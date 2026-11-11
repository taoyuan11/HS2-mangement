#pragma once
#include "GpuMaterialComposer.h"

namespace hs2 {
// System.Random's seeded subtractive generator: preserve the original noise phase.
class NativeSeededRandom {
public: explicit NativeSeededRandom(int seed=1979);double NextDouble();
private:std::array<int,56> values_{};int next_=0,nextp_=21;
};
class NativeInvalidPixelFilter {
public:NativeInvalidPixelFilter(GraphicsDevice& g,NativeGpuAssetStore& assets,GpuMaterialComposer& composer);void Draw(GpuTexture& target);
private:GraphicsDevice& graphics_;NativeFullscreenPass pass_;std::unique_ptr<GpuTexture> temporary_;
};
class NativeSpatialAntialiasing {
public:
    NativeSpatialAntialiasing(GraphicsDevice& g,NativeGpuAssetStore& store,GpuMaterialComposer& composer,const EnvironmentDefinition& environment,bool forceSpatial=false);
    void ResetHistory(int phase=0);void Draw(const GpuTexture& source,GpuTexture& target,bool forceSpatial=false);
private:
    GraphicsDevice& graphics_;NativeGpuAssetStore& store_;GpuMaterialComposer& composer_;
    std::unique_ptr<NativeFullscreenPass> edges_,weights_,neighbourhood_,final_,spatialFinal_;
    bool normallySpatial_;std::shared_ptr<GpuTexture> area_,search_;
    std::unique_ptr<GpuTexture> edgeTarget_,weightTarget_,spatialTarget_;int frame_=0;NativeSeededRandom random_;
};
class NativeTemporalAntialiasing {
public:
    NativeTemporalAntialiasing(GraphicsDevice& g,GpuMaterialComposer& composer,const EnvironmentDefinition& environment);
    NativeRenderCamera Jitter(NativeRenderCamera camera,int width,int height);
    void ResetHistory(int phase=0){initialized_=false;sample_=((phase%16)+16)%16;}
    void Draw(const GpuTexture& source,const GpuTexture& motionVectors,GpuTexture& target,bool bottomOrigin=false);
private:
    GraphicsDevice& graphics_;NativeFullscreenPass resolve_,enhance_;GpuCopyPass copy_;std::array<float,6> settings_;Vector2 referenceResolution_;int anisotropy_;
    std::unique_ptr<GpuTexture> previous_,current_,sharpened_,bottomSource_,bottomVelocity_;bool initialized_=false;int sample_=0;
};
}
