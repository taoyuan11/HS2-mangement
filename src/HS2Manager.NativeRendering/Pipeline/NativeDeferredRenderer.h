#pragma once
#include "Pipeline/NativeForwardRenderer.h"
#include <map>

namespace hs2
{
    class NativeDeferredRenderer final
    {
    public:
        NativeDeferredRenderer(GraphicsDevice &graphics, NativeGpuAssetStore &assets, GpuMaterialComposer &composer,
                               GpuCharacterAppearance &appearance, std::shared_ptr<GpuTexture> reflection,
                               ShaderResource probe);
        static bool Supports(const GpuMaterialInstance &material);

        void ResetMaterials()
        {
            materials_.clear();
        }

        std::array<std::unique_ptr<GpuTexture>, 3> Buffers;
        std::unique_ptr<GpuTexture> ReflectionBuffer;
        void Draw(GpuTexture &target, const NativeRenderCamera &camera, const GpuDepthSurface &depth,
                  const GpuDepthSurface &cameraDepth, const ShaderValues &globals,
                  std::span<const NativeDirectionalLight> lights, NativeDirectionalShadows &shadows,
                  const DeformedStreams &deformed, const std::function<void(const GpuDepthSurface &)> &prepareForward);

    private:
        GraphicsDevice &graphics_;
        GpuMaterialComposer &composer_;
        GpuCharacterAppearance &appearance_;
        std::shared_ptr<GpuTexture> reflection_;
        ShaderResource probe_;
        std::shared_ptr<NativeShaderAsset> lightShader_, reflectionShader_;
        std::map<std::pair<const GpuMaterialInstance *, int>, std::unique_ptr<NativeMaterialPass>> materials_;
        std::map<std::pair<int, bool>, std::unique_ptr<NativeFullscreenPass>> lights_;
        std::map<int, std::unique_ptr<NativeFullscreenPass>> reflections_, composites_;
        std::map<std::pair<int, bool>, int> groups_;
        std::unique_ptr<GpuDepthSurface> stencilDepth_;
        static const NativeShaderPass *Pass(const GpuMaterialInstance &material);
        static D3D11_DEPTH_STENCIL_DESC Stencil(bool write);
        int Group(const NativeSceneRenderer &renderer);
        void Resize(int width, int height);
    };

    class NativeDepthNormals final
    {
    public:
        NativeDepthNormals(GraphicsDevice &graphics, NativeGpuAssetStore &assets, GpuMaterialComposer &composer);
        void Draw(int width, int height, const GpuDepthSurface &depth, const ShaderValues &globals,
                  const GpuCharacterAppearance &appearance, const DeformedStreams &deformed);
        const GpuTexture &Texture() const;

    private:
        GraphicsDevice &graphics_;
        GpuMaterialComposer &composer_;
        std::shared_ptr<NativeShaderAsset> shader_;
        std::unique_ptr<GpuMaterialInstance> defaults_;
        std::unordered_map<std::string, std::vector<const NativeShaderPass *>> replacements_;
        std::unordered_map<const NativeShaderPass *, std::unique_ptr<NativeMaterialPass>> passes_;
        std::unique_ptr<GpuTexture> normals_;
    };
}
