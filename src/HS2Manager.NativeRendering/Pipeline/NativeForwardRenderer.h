#pragma once
#include "Materials/GpuCharacterAppearance.h"
#include "Lighting/NativeDirectionalShadows.h"
#include "Geometry/NativeGpuMeshDeformer.h"

namespace hs2
{
    using DeformedStreams = std::unordered_map<NativeSceneRenderer *, NativeGpuVertexStream>;

    class NativeForwardRenderer final
    {
    public:
        NativeForwardRenderer(GraphicsDevice &graphics, NativeCharacterScene &scene, GpuCharacterAppearance &appearance,
                              GpuMaterialComposer &composer, NativeGpuAssetStore &assets);
        ~NativeForwardRenderer();
        void UpdateCharacter(bool resetMaterials);
        void Draw(GpuTexture &target, const NativeRenderCamera &camera, std::span<const float> ambientProbe,
                  float time = 0);
        bool UseGameBackground = true;
        std::optional<Vector4> Background;
        float KeyLightMultiplier = 1;
        const GpuDepthSurface &CameraDepth() const;
        std::vector<const GpuTexture *> GBuffer() const;
        const GpuTexture &Reflections() const;
        std::function<void(const NativeRenderCamera &, const GpuDepthSurface &, const GpuDepthSurface &,
                           const DeformedStreams &)>
            AfterGeometry;
        std::function<void(const GpuDepthSurface &, GpuTexture &, const NativeRenderCamera &)> AfterOpaque;
        static ShaderValues MaterialValues(const NativeSceneRenderer &renderer, const GpuMaterialInstance &material,
                                           const ShaderValues &globals);
        static bool GeometryMirrored(const NativeSceneRenderer &renderer);
        static std::string Tag(const NativeShaderPass &pass, const std::string &key);
        static std::vector<NativeDirectionalLight> DirectionalLights(const EnvironmentDefinition &environment,
                                                                     const NativeRenderCamera &camera,
                                                                     float multiplier = 1);
        static ShaderValues Globals(const EnvironmentDefinition &environment, const NativeRenderCamera &camera,
                                    int width, int height, std::span<const float> sh, float time);

    private:
        struct State;
        std::unique_ptr<State> state_;
    };
}
