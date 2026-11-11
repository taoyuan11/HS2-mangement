#include "PostProcessing/NativeScreenEffects.h"
#include "Common/RenderMath.h"
#include <algorithm>
#include <cmath>
#include <numeric>
#include <stdexcept>

namespace hs2
{
    namespace
    {
        ShaderResource View(const GpuComputeImage &image)
        {
            return {image.View.Get(), image.Sampler.Get()};
        }
    }

    NativeAmbientOcclusion::NativeAmbientOcclusion(GraphicsDevice &g, NativeGpuAssetStore &assets,
                                                   GpuMaterialComposer &composer, const NativePostSettings &settings)
        : graphics_(g), settings_(settings),
          down1_(g, ComputeKernel(composer, assets, "MultiScaleVODownsample1", "MultiScaleVODownsample1")),
          down2_(g, ComputeKernel(composer, assets, "MultiScaleVODownsample2", "MultiScaleVODownsample2")),
          obscurance_(g, ComputeKernel(composer, assets, "MultiScaleVORender", "MultiScaleVORender_interleaved")),
          upsample_(g, ComputeKernel(composer, assets, "MultiScaleVOUpsample", "MultiScaleVOUpSample")),
          combine_(g, ComputeKernel(composer, assets, "MultiScaleVOUpsample", "MultiScaleVOUpSample_blendout")),
          composite_(g, *composer.Shader(assets.GameResource("Hidden/PostProcessing/MultiScaleVO", 48)), 2), copy_(g)
    {
        if (settings.Scalar("AmbientOcclusion", "mode") != 1)
            throw std::runtime_error("The selected scalable AO mode needs its own original shader path.");
    }

    void NativeAmbientOcclusion::Resize(int width, int height)
    {
        if (linear_ && linear_->Width == width && linear_->Height == height)
            return;
        bottomDepth_ = std::make_unique<GpuTexture>(graphics_, width, height, true, DXGI_FORMAT_R32_FLOAT, false);
        uprightOcclusion_ = std::make_unique<GpuTexture>(graphics_, width, height, true, DXGI_FORMAT_R8_UNORM, false);
        linear_ = std::make_unique<GpuComputeImage>(graphics_, width, height, DXGI_FORMAT_R16_FLOAT);
        output_ = std::make_unique<GpuComputeImage>(graphics_, width, height, DXGI_FORMAT_R8_UNORM);
        for (int i = 0; i < 4; ++i)
        {
            int divisor = 2 << i, w = (width + divisor - 1) / divisor, h = (height + divisor - 1) / divisor;
            low_[i] = std::make_unique<GpuComputeImage>(graphics_, w, h, DXGI_FORMAT_R32_FLOAT);
            ao_[i] = std::make_unique<GpuComputeImage>(graphics_, w, h, DXGI_FORMAT_R8_UNORM);
            atlas_[i] =
                std::make_unique<GpuComputeImage>(graphics_, (w + 3) / 4, (h + 3) / 4, DXGI_FORMAT_R16_FLOAT, 16);
            if (i < 3)
                merged_[i] = std::make_unique<GpuComputeImage>(graphics_, w, h, DXGI_FORMAT_R8_UNORM);
        }
    }

    void NativeAmbientOcclusion::Draw(const GpuDepthSurface &depth, GpuTexture &target,
                                      const NativeRenderCamera &camera)
    {
        graphics_.VerifyThread();
        Resize(target.Width(), target.Height());
        copy_.CopyView(depth.View.Get(), *bottomDepth_, !camera.BottomOrigin, false, true);
        down1_.Dispatch(atlas_[1]->Width * 8, atlas_[1]->Height * 8, 1,
                        {{"ZBufferParams", {camera.Far / camera.Near - 1, 1, 0, 0}}},
                        {{"Depth", {bottomDepth_->View.Get(), depth.Sampler.Get()}}},
                        {{"LinearZ", linear_->Output.Get()},
                         {"DS2x", low_[0]->Output.Get()},
                         {"DS2xAtlas", atlas_[0]->Output.Get()},
                         {"DS4x", low_[1]->Output.Get()},
                         {"DS4xAtlas", atlas_[1]->Output.Get()}});
        down2_.Dispatch(atlas_[3]->Width * 8, atlas_[3]->Height * 8, 1, {}, {{"DS4x", View(*low_[1])}},
                        {{"DS8x", low_[2]->Output.Get()},
                         {"DS8xAtlas", atlas_[2]->Output.Get()},
                         {"DS16x", low_[3]->Output.Get()},
                         {"DS16xAtlas", atlas_[3]->Output.Get()}});
        constexpr int offsets[12][2]{{1, 0}, {2, 0}, {3, 0}, {4, 0}, {1, 1}, {1, 2},
                                     {1, 3}, {1, 4}, {2, 2}, {2, 3}, {2, 4}, {3, 3}};
        std::vector<float> thickness(12), weights(12);
        for (int i = 0; i < 12; ++i)
        {
            auto x = offsets[i][0], y = offsets[i][1];
            thickness[i] = std::sqrt(1 - (x * x + y * y) / 25.f);
            weights[i] =
                (i == 0 || i == 2 || i == 5 || i == 7 || i == 9) ? 0 : thickness[i] * (y == 0 || x == y ? 4 : 8);
        }
        float sum = std::accumulate(weights.begin(), weights.end(), 0.f);
        for (auto &w : weights)
            w /= sum;
        float tanHalf = 1 / camera.Projection(static_cast<float>(target.Width()) / target.Height())._11;
        for (int i = 0; i < 4; ++i)
        {
            auto &image = *atlas_[i];
            float diameter = 20 * tanHalf / image.Width;
            auto inverse = thickness;
            for (auto &t : inverse)
                t = 1 / (diameter * t);
            obscurance_.Dispatch(image.Width, image.Height, 16,
                                 {{"gInvThicknessTable", inverse},
                                  {"gSampleWeightTable", weights},
                                  {"gInvSliceDimension", {1.f / image.Width, 1.f / image.Height, 0, 0}},
                                  {"AdditionalParams",
                                   {-1 / settings_.Scalar("AmbientOcclusion", "thicknessModifier", 1),
                                    settings_.Scalar("AmbientOcclusion", "intensity")}}},
                                 {{"DepthTex", View(image)}}, {{"Occlusion", ao_[i]->Output.Get()}});
        }
        auto *current = ao_[3].get();
        for (int i = 3; i >= 0; --i)
        {
            auto &high = i == 0 ? *linear_ : *low_[i - 1];
            auto &destination = i == 0 ? *output_ : *merged_[i - 1];
            float step = 1920.f / low_[i]->Width,
                  tolerance = 1 - std::pow(10.f, settings_.Scalar("AmbientOcclusion", "blurTolerance", -4.6f)) * step,
                  up = std::pow(10.f, settings_.Scalar("AmbientOcclusion", "upsampleTolerance", -12));
            ShaderValues values{
                {"InvLowResolution", {1.f / low_[i]->Width, 1.f / low_[i]->Height, 0, 0}},
                {"InvHighResolution", {1.f / high.Width, 1.f / high.Height, 0, 0}},
                {"AdditionalParams",
                 {1 / (std::pow(10.f, settings_.Scalar("AmbientOcclusion", "noiseFilterTolerance")) + up), step,
                  tolerance * tolerance, up}}};
            ShaderResources textures{
                {"LoResDB", View(*low_[i])}, {"HiResDB", View(high)}, {"LoResAO1", View(*current)}};
            if (i > 0)
                textures["HiResAO"] = View(*ao_[i - 1]);
            (i == 0 ? upsample_ : combine_)
                .Dispatch((high.Width + 17) / 16 * 8, (high.Height + 17) / 16 * 8, 1, values, textures,
                          {{"AoResult", destination.Output.Get()}});
            current = &destination;
        }
        auto color = settings_.Value("AmbientOcclusion", "color", {0, 0, 0, 1});
        for (auto &c : color)
            c = 1 - c;
        copy_.CopyView(output_->View.Get(), *uprightOcclusion_, !camera.BottomOrigin, false, true);
        composite_.Draw(target, {{"_RenderViewportScaleFactor", {1}}, {"_AOColor", color}}, {},
                        {{"_MSVOcclusionTexture", {uprightOcclusion_->View.Get(), uprightOcclusion_->Sampler.Get()}}});
    }

    NativeScreenSpaceReflections::NativeScreenSpaceReflections(GraphicsDevice &g, NativeGpuAssetStore &assets,
                                                               GpuMaterialComposer &composer,
                                                               const NativePostSettings &settings)
        : graphics_(g), settings_(settings),
          gaussian_(g, ComputeKernel(composer, assets, "GaussianDownsample", "KMain")), copy_(g),
          noise_(composer.Texture(assets.GameResource("LDR_LLL1_0", 28)))
    {
        auto shader = composer.Shader(assets.GameResource("Hidden/PostProcessing/ScreenSpaceReflections", 48));
        for (int i = 0; i < 4; ++i)
            passes_[i] = std::make_unique<NativeFullscreenPass>(g, *shader, i);
    }

    std::tuple<int, float, int> NativeScreenSpaceReflections::Quality() const
    {
        switch (static_cast<int>(settings_.Scalar("ScreenSpaceReflections", "preset", 1)))
        {
        case 0:
            return {10, 32.f, 0};
        case 1:
            return {16, 32.f, 0};
        case 2:
            return {32, 16.f, 0};
        case 3:
            return {48, 8.f, 0};
        case 4:
            return {16, 32.f, 1};
        case 5:
            return {48, 16.f, 1};
        case 6:
            return {128, 12.f, 2};
        case 7:
            return {static_cast<int>(settings_.Scalar("ScreenSpaceReflections", "maximumIterationCount", 16)),
                    settings_.Scalar("ScreenSpaceReflections", "thickness", 8),
                    static_cast<int>(settings_.Scalar("ScreenSpaceReflections", "resolution"))};
        default:
            throw std::runtime_error("Unknown screen-space reflection quality.");
        }
    }

    void NativeScreenSpaceReflections::Resize(int width, int height, int resolution)
    {
        int dimension = std::min(width, height), power = 1;
        while (power < dimension)
            power *= 2;
        if (dimension - power / 2 < power - dimension)
            power /= 2;
        int next = resolution == 0 ? power / 2 : resolution == 1 ? power : resolution == 2 ? power * 2 : 0;
        if (next == 0)
            throw std::runtime_error("Unknown reflection resolution.");
        if (next < 16)
            throw std::runtime_error("Reflection targets require at least a 16-pixel square.");
        if (size_ == next && sourceCopy_ && sourceCopy_->Width() == width && sourceCopy_->Height() == height)
            return;
        size_ = next;
        lods_ = std::min(12, static_cast<int>(std::log2(static_cast<float>(size_))) - 3);
        initialized_ = false;
        pyramid_.clear();
        test_ = std::make_unique<GpuTexture>(graphics_, size_, size_, true, DXGI_FORMAT_R16G16B16A16_FLOAT, false,
                                             D3D11_FILTER_MIN_MAG_MIP_POINT);
        resolved_ = std::make_unique<GpuTexture>(graphics_, size_, size_, true, DXGI_FORMAT_R16G16B16A16_FLOAT, true,
                                                 D3D11_FILTER_MIN_MAG_MIP_LINEAR);
        temporal_ = std::make_unique<GpuTexture>(graphics_, size_, size_, true, DXGI_FORMAT_R16G16B16A16_FLOAT, false);
        history_ = std::make_unique<GpuTexture>(graphics_, size_, size_, true, DXGI_FORMAT_R16G16B16A16_FLOAT, false);
        auto allocate = [&](DXGI_FORMAT format)
        {
            return std::make_unique<GpuTexture>(graphics_, width, height, true, format, false);
        };
        sourceCopy_ = allocate(DXGI_FORMAT_R16G16B16A16_FLOAT);
        bottomDepth_ = allocate(DXGI_FORMAT_R32_FLOAT);
        bottomVelocity_ = allocate(DXGI_FORMAT_R16G16_FLOAT);
        bottomReflections_ = allocate(DXGI_FORMAT_R16G16B16A16_FLOAT);
        bottomOutput_ = allocate(DXGI_FORMAT_R16G16B16A16_FLOAT);
        constexpr DXGI_FORMAT formats[]{DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,
                                        DXGI_FORMAT_R10G10B10A2_UNORM};
        for (int i = 0; i < 3; ++i)
            bottomGBuffer_[i] = allocate(formats[i]);
        for (int i = 1; i <= lods_; ++i)
            pyramid_.push_back(
                std::make_unique<GpuComputeImage>(graphics_, size_ >> i, size_ >> i, DXGI_FORMAT_R16G16B16A16_FLOAT));
    }

    void NativeScreenSpaceReflections::Draw(GpuTexture &target, const GpuDepthSurface &depth, NativeRenderCamera camera,
                                            std::span<const GpuTexture *const> gbuffer, const GpuTexture &reflections,
                                            const GpuTexture &velocity)
    {
        graphics_.VerifyThread();
        if (gbuffer.size() != 3)
            throw std::invalid_argument("Reflection composition requires three GBuffer surfaces.");
        auto [iterations, thickness, resolution] = Quality();
        Resize(target.Width(), target.Height(), resolution);
        copy_.Copy(target, *sourceCopy_, !camera.BottomOrigin);
        copy_.CopyView(depth.View.Get(), *bottomDepth_, !camera.BottomOrigin, false, true);
        copy_.Copy(velocity, *bottomVelocity_, !camera.BottomOrigin, !camera.BottomOrigin);
        copy_.Copy(reflections, *bottomReflections_, !camera.BottomOrigin);
        for (int i = 0; i < 3; ++i)
            copy_.Copy(*gbuffer[i], *bottomGBuffer_[i], !camera.BottomOrigin);
        bool bottom = camera.BottomOrigin;
        camera.BottomOrigin = false;
        auto projection = camera.Projection(static_cast<float>(target.Width()) / target.Height(), false);
        camera.BottomOrigin = bottom;
        Matrix4x4 screenTransform{size_ * .5f, 0, 0, 0, 0,           size_ * .5f, 0, 0,
                                  0,           0, 1, 0, size_ * .5f, size_ * .5f, 0, 1};
        auto screen = MultiplyMatrix(projection, screenTransform);
        ShaderValues values{
            {"_ViewMatrix", MatrixValues(camera.View())},
            {"_InverseViewMatrix", MatrixValues(InverseMatrix(camera.View()))},
            {"_InverseProjectionMatrix", MatrixValues(InverseMatrix(projection))},
            {"_ScreenSpaceProjectionMatrix", MatrixValues(screen)},
            {"_Params",
             {settings_.Scalar("ScreenSpaceReflections", "vignette", .5f),
              settings_.Scalar("ScreenSpaceReflections", "distanceFade", .5f),
              std::max(0.f, settings_.Scalar("ScreenSpaceReflections", "maximumMarchDistance", 100)),
              static_cast<float>(lods_)}},
            {"_Params2",
             {static_cast<float>(target.Width()) / target.Height(), static_cast<float>(size_) / noise_->Width(),
              thickness, static_cast<float>(iterations)}},
            {"_Test_TexelSize", {1.f / size_, 1.f / size_, static_cast<float>(size_), static_cast<float>(size_)}},
            {"_WorldSpaceCameraPos", Values(camera.Position)},
            {"_ProjectionParams", {-1, camera.Near, camera.Far, 1 / camera.Far}},
            {"_ZBufferParams",
             {camera.Far / camera.Near - 1, 1, (camera.Far / camera.Near - 1) / camera.Far, 1 / camera.Far}}};
        ShaderTextures textures{{"_MainTex", sourceCopy_.get()},
                                {"_Noise", noise_.get()},
                                {"_CameraGBufferTexture0", bottomGBuffer_[0].get()},
                                {"_CameraGBufferTexture1", bottomGBuffer_[1].get()},
                                {"_CameraGBufferTexture2", bottomGBuffer_[2].get()},
                                {"_CameraReflectionsTexture", bottomReflections_.get()},
                                {"_CameraMotionVectorsTexture", bottomVelocity_.get()},
                                {"_Test", test_.get()}};
        ShaderResources views{{"_CameraDepthTexture", {bottomDepth_->View.Get(), depth.Sampler.Get()}}};
        passes_[0]->Draw(*test_, values, textures, views);
        if (!initialized_)
        {
            copy_.Copy(*sourceCopy_, *history_);
            initialized_ = true;
        }
        passes_[1]->Draw(*temporal_, values, textures);
        textures["_MainTex"] = temporal_.get();
        textures["_History"] = history_.get();
        passes_[2]->Draw(*resolved_, values, textures);
        graphics_.Context()->CopySubresourceRegion(history_->Texture.Get(), 0, 0, 0, 0, resolved_->Texture.Get(), 0,
                                                   nullptr);
        ShaderResource last{resolved_->View.Get(), resolved_->Sampler.Get()};
        for (size_t i = 0; i < pyramid_.size(); ++i)
        {
            auto &output = *pyramid_[i];
            gaussian_.Dispatch(output.Width, output.Height, 1,
                               {{"_Size",
                                 {static_cast<float>(output.Width), static_cast<float>(output.Height),
                                  1.f / output.Width, 1.f / output.Height}}},
                               {{"_Source", last}}, {{"_Result", output.Output.Get()}});
            graphics_.Context()->CopySubresourceRegion(resolved_->Texture.Get(), static_cast<UINT>(i + 1), 0, 0, 0,
                                                       output.Texture.Get(), 0, nullptr);
            last = View(output);
        }
        textures["_MainTex"] = sourceCopy_.get();
        textures["_Resolve"] = resolved_.get();
        passes_[3]->Draw(*bottomOutput_, values, textures, views);
        copy_.Copy(*bottomOutput_, target, !camera.BottomOrigin);
    }
}
