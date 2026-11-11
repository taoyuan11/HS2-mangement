#pragma once
#include "NativeForwardRenderer.h"
#include "NativePostSettings.h"

namespace hs2 {
class NativeMotionVectors {
public:
    NativeMotionVectors(GraphicsDevice& graphics,NativeGpuAssetStore& assets,GpuMaterialComposer& composer,GpuCharacterAppearance& appearance);
    void ResetHistory(){initialized_=false;previous_.clear();}
    void Draw(const NativeRenderCamera& camera,const GpuDepthSurface& depth,const GpuDepthSurface& cameraDepth,const DeformedStreams& deformed);
    const GpuTexture& Texture()const;
private:
    GraphicsDevice& graphics_;GpuCharacterAppearance& appearance_;NativeFullscreenPass cameraPass_;NativeMaterialPass geometryPass_;
    struct History{ComPtr<ID3D11Buffer> Buffer;Matrix4x4 Matrix;};
    std::unordered_map<NativeSceneRenderer*,History> previous_;Matrix4x4 previousVP_{};bool initialized_=false;std::unique_ptr<GpuTexture> target_;
};
class NativeMotionBlur {
public:
    NativeMotionBlur(GraphicsDevice& graphics,NativeGpuAssetStore& assets,GpuMaterialComposer& composer,const NativePostSettings& settings);
    void ResetHistory(){first_=true;}
    void Draw(const GpuTexture& source,GpuTexture& target,const GpuTexture& velocity,const GpuDepthSurface& depth,const NativeRenderCamera& camera);
private:
    GraphicsDevice& graphics_;const NativePostSettings& settings_;std::array<std::unique_ptr<NativeFullscreenPass>,6> passes_;GpuCopyPass copy_;
    std::vector<std::unique_ptr<GpuTexture>> targets_;std::unique_ptr<GpuTexture> bottomSource_,bottomVelocity_,bottomDepth_,bottomResult_;bool first_=true;int width_=0,height_=0,tilePixels_=0,radius_=0;
    void Resize(int width,int height);
};
}
