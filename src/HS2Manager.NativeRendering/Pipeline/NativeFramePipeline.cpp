#include "Pipeline/NativeFramePipeline.h"
#include "PostProcessing/NativePostProcessor.h"
#include "PostProcessing/NativeMotion.h"
#include "PostProcessing/NativeScreenEffects.h"
#include "Lighting/NativeSkybox.h"
#include <algorithm>
#include <stdexcept>

namespace hs2
{
    struct NativeFramePipeline::State
    {
        GraphicsDevice &Graphics;
        NativeCharacterScene &Scene;
        GpuMaterialComposer Composer;
        GpuCharacterAppearance Appearance;
        NativeForwardRenderer Renderer;
        NativePostProcessor Post;
        std::unique_ptr<NativeTemporalAntialiasing> Temporal;
        std::unique_ptr<NativeMotionVectors> Vectors;
        std::unique_ptr<NativeMotionBlur> Blur;
        std::unique_ptr<NativeScreenSpaceReflections> Reflections;
        std::unique_ptr<NativeAmbientOcclusion> Occlusion;
        NativeInvalidPixelFilter Invalid;
        std::array<float, 27> Probe{};
        std::unique_ptr<GpuTexture> Hdr, PostTarget, BlurTarget, Output;
        bool Interactive = false;

        State(GraphicsDevice &g, NativeCharacterScene &scene, NativeGpuAssetStore &assets,
              const NativeFrameOptions &options)
            : Graphics(g), Scene(scene), Composer(g, assets, *scene.Definition), Appearance(scene, Composer),
              Renderer(g, scene, Appearance, Composer, assets),
              Post(g, assets, Composer, scene.Definition->Environment, options.StaticReference),
              Invalid(g, assets, Composer)
        {
            Renderer.UseGameBackground = options.UseGameBackground;
            Renderer.Background = options.Background;
            Renderer.KeyLightMultiplier = options.KeyLightMultiplier;
            if (scene.Definition->Environment.BetterAA && !options.StaticReference)
                Temporal = std::make_unique<NativeTemporalAntialiasing>(g, Composer, scene.Definition->Environment);
            if (Temporal || Post.Settings().Enabled("MotionBlur") || Post.Settings().Enabled("ScreenSpaceReflections"))
                Vectors = std::make_unique<NativeMotionVectors>(g, assets, Composer, Appearance);
            if (Post.Settings().Enabled("MotionBlur"))
                Blur = std::make_unique<NativeMotionBlur>(g, assets, Composer, Post.Settings());
            if (Post.Settings().Enabled("ScreenSpaceReflections"))
                Reflections = std::make_unique<NativeScreenSpaceReflections>(g, assets, Composer, Post.Settings());
            if (Post.Settings().Enabled("AmbientOcclusion"))
                Occlusion = std::make_unique<NativeAmbientOcclusion>(g, assets, Composer, Post.Settings());
            if (Vectors)
                Renderer.AfterGeometry = [this](const NativeRenderCamera &camera, const GpuDepthSurface &depth,
                                                const GpuDepthSurface &cameraDepth, const DeformedStreams &streams)
                {
                    Vectors->Draw(camera, depth, cameraDepth, streams);
                };
            Renderer.AfterOpaque =
                [this](const GpuDepthSurface &depth, GpuTexture &color, const NativeRenderCamera &camera)
            {
                if (Occlusion)
                    Occlusion->Draw(depth, color, camera);
                Invalid.Draw(color);
                if (Reflections)
                {
                    auto buffers = Renderer.GBuffer();
                    Reflections->Draw(color, depth, camera, buffers, Renderer.Reflections(), Vectors->Texture());
                }
            };
            NativeSkybox sky(g, Composer, scene.Definition->Environment);
            Probe = sky.GenerateAmbientProbe(64);
        }
    };

    NativeFramePipeline::NativeFramePipeline(GraphicsDevice &g, NativeCharacterScene &scene,
                                             NativeGpuAssetStore &assets, NativeFrameOptions options)
        : state_(std::make_unique<State>(g, scene, assets, options))
    {
    }

    NativeFramePipeline::~NativeFramePipeline() = default;

    void NativeFramePipeline::ResetHistory()
    {
        auto &s = *state_;
        s.Graphics.VerifyThread();
        s.Post.ResetHistory();
        if (s.Temporal)
            s.Temporal->ResetHistory();
        if (s.Blur)
            s.Blur->ResetHistory();
        if (s.Reflections)
            s.Reflections->ResetHistory();
    }

    void NativeFramePipeline::SetInteractive(bool value)
    {
        auto &s = *state_;
        s.Graphics.VerifyThread();
        if (value || s.Interactive != value)
            ResetHistory();
        s.Interactive = value;
    }

    void NativeFramePipeline::SetEnvironment(const NativeFrameOptions &options)
    {
        auto &s = *state_;
        s.Graphics.VerifyThread();
        s.Renderer.UseGameBackground = options.UseGameBackground;
        s.Renderer.Background = options.Background;
        s.Renderer.KeyLightMultiplier = options.KeyLightMultiplier;
        ResetHistory();
    }

    const GpuTexture &NativeFramePipeline::Draw(NativeRenderCamera camera, int width, int height, float time)
    {
        auto &s = *state_;
        s.Graphics.VerifyThread();
        if (width < 1 || height < 1 || width > 8192 || height > 8192)
            throw std::invalid_argument("Frame dimensions must be between 1 and 8192.");
        if (!s.Output || s.Output->Width() != width || s.Output->Height() != height)
        {
            auto output =
                std::make_unique<GpuTexture>(s.Graphics, width, height, true, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB, false);
            auto hdr =
                std::make_unique<GpuTexture>(s.Graphics, width, height, true, DXGI_FORMAT_R16G16B16A16_FLOAT, false);
            auto post =
                std::make_unique<GpuTexture>(s.Graphics, width, height, true, DXGI_FORMAT_R16G16B16A16_FLOAT, false);
            auto blur =
                std::make_unique<GpuTexture>(s.Graphics, width, height, true, DXGI_FORMAT_R16G16B16A16_FLOAT, false);
            s.Output = std::move(output);
            s.Hdr = std::move(hdr);
            s.PostTarget = std::move(post);
            s.BlurTarget = std::move(blur);
            ResetHistory();
        }
        camera.BottomOrigin = true;
        bool temporal = s.Temporal && !s.Interactive, blur = s.Blur && !s.Interactive;
        auto renderCamera = temporal ? s.Temporal->Jitter(camera, width, height) : camera;
        s.Renderer.Draw(*s.Hdr, renderCamera, s.Probe, time);
        if (blur)
            s.Blur->Draw(*s.Hdr, *s.BlurTarget, s.Vectors->Texture(), s.Renderer.CameraDepth(), renderCamera);
        s.Post.Draw(blur ? *s.BlurTarget : *s.Hdr, temporal ? *s.PostTarget : *s.Output, renderCamera.BottomOrigin,
                    temporal && renderCamera.BottomOrigin, s.Hdr.get(), s.Interactive);
        if (temporal)
            s.Temporal->Draw(*s.PostTarget, s.Vectors->Texture(), *s.Output, renderCamera.BottomOrigin);
        CompleteVisibility(s.Scene, renderCamera, width, height);
        return *s.Output;
    }

    void NativeFramePipeline::CompleteVisibility(NativeCharacterScene &scene, NativeRenderCamera camera, int width,
                                                 int height)
    {
        if (width < 1 || height < 1)
            throw std::invalid_argument("Invalid visibility dimensions.");
        const auto vp = MultiplyMatrix(camera.View(), camera.Projection(static_cast<float>(width) / height, false));
        auto lights = NativeForwardRenderer::DirectionalLights(scene.Definition->Environment, camera);
        auto shadowCameras = NativeDirectionalShadows::CullingCameras(
            camera, width, height, lights, scene.Definition->Environment.Quality.Float("shadowNearPlaneOffset", 2));
        for (auto &renderer : scene.Renderers)
        {
            if (!renderer->Visible() || renderer->Mesh->VertexCount == 0)
            {
                renderer->LastRenderedVisibility = false;
                continue;
            }
            const auto bounds = renderer->Bounds();
            renderer->LastRenderedVisibility =
                bounds.Intersects(vp) ||
                (renderer->ShadowCasting != 0 &&
                 std::any_of(shadowCameras.begin(), shadowCameras.end(),
                             [&](const auto &shadow)
                             {
                                 return (shadow.Light.Mask & (1u << renderer->Node->Layer)) != 0 &&
                                        bounds.Intersects(MultiplyMatrix(shadow.View, shadow.Projection));
                             }));
        }
    }

    const GpuTexture &NativeFramePipeline::LastFrame() const
    {
        if (!state_->Output)
            throw std::logic_error("No native preview frame has been drawn.");
        return *state_->Output;
    }

    GpuCharacterAppearance &NativeFramePipeline::Appearance()
    {
        return state_->Appearance;
    }

    GpuMaterialComposer &NativeFramePipeline::Composer()
    {
        return state_->Composer;
    }

    NativeForwardRenderer &NativeFramePipeline::Renderer()
    {
        return state_->Renderer;
    }

    std::span<const float> NativeFramePipeline::AmbientProbe() const
    {
        return state_->Probe;
    }

    const NativePostSettings &NativeFramePipeline::PostSettings() const
    {
        return state_->Post.Settings();
    }

    const GpuVolume *NativeFramePipeline::GradingLut() const
    {
        return state_->Post.GradingLut();
    }

    const std::unordered_set<std::string> &NativeFramePipeline::SourceReferenceDiagnostics() const
    {
        return state_->Composer.SourceReferenceDiagnostics;
    }
}
