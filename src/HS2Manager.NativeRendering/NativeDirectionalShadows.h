#pragma once
#include "GpuMaterialComposer.h"
#include <functional>

namespace hs2 {
struct NativeDirectionalLight {Vector3 Direction{0,1,0},Color{};UINT Mask=~0u;float Intensity=0,ShadowStrength=0,ShadowBias=.05f,NormalBias=.4f,NearPlane=.2f;std::optional<Quaternion> Rotation;};
struct NativeShadowCamera {Matrix4x4 View,Projection;Vector4 Bias;NativeDirectionalLight Light;};
class NativeDirectionalShadows final {
public:
    NativeDirectionalShadows(GraphicsDevice& graphics,NativeGpuAssetStore& store,GpuMaterialComposer& composer,float nearPlaneOffset=2,int anisotropicFiltering=2);
    ~NativeDirectionalShadows();
    const GpuTexture& Screen(size_t light,bool materialCoordinates=true)const;
    ShaderResource ScreenView(size_t light,bool materialCoordinates=true)const;
    const ShaderValues& Uniforms(size_t light)const{return uniforms_.at(light);}
    static std::vector<NativeShadowCamera> CullingCameras(const NativeRenderCamera& camera,int width,int height,std::span<const NativeDirectionalLight> lights,float nearPlaneOffset);
    void Render(const NativeRenderCamera& camera,int width,int height,std::span<const NativeDirectionalLight> lights,const GpuDepthSurface& cameraDepth,const std::function<void(const NativeShadowCamera&)>& drawCasters);
    static float CascadeFar(const NativeRenderCamera& camera,float aspect,float distance);
private:
    static constexpr int AtlasSize=4096,TileSize=AtlasSize/2;
    struct LightMap;
    GraphicsDevice& graphics_;
    NativeFullscreenPass resolve_;
    GpuCopyPass copy_;
    float nearPlaneOffset_;
    int anisotropy_;
    std::vector<std::unique_ptr<LightMap>> maps_;
    std::vector<ShaderValues> uniforms_;
    static Matrix4x4 LightView(const NativeDirectionalLight& light,Vector3 centre);
};
}
