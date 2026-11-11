#include "PostProcessing/NativeMotion.h"
#include "Common/RenderMath.h"
#include <algorithm>
#include <stdexcept>

namespace hs2
{
    NativeMotionVectors::NativeMotionVectors(GraphicsDevice &g, NativeGpuAssetStore &assets,
                                             GpuMaterialComposer &composer, GpuCharacterAppearance &appearance)
        : graphics_(g), appearance_(appearance),
          cameraPass_(g, *composer.Shader(assets.GameResource("Hidden/Internal-MotionVectors", 48)), 1, {}, true),
          geometryPass_(g, *composer.Shader(assets.GameResource("Hidden/Internal-MotionVectors", 48)),
                        composer.Shader(assets.GameResource("Hidden/Internal-MotionVectors", 48))->Passes.at(0), {}, {},
                        true)
    {
    }

    const GpuTexture &NativeMotionVectors::Texture() const
    {
        if (!target_)
            throw std::logic_error("No motion-vector frame has been drawn.");
        return *target_;
    }

    void NativeMotionVectors::Draw(const NativeRenderCamera &camera, const GpuDepthSurface &depth,
                                   const GpuDepthSurface &cameraDepth, const DeformedStreams &deformed)
    {
        graphics_.VerifyThread();
        if (!target_ || target_->Width() != depth.Width || target_->Height() != depth.Height)
        {
            target_ = std::make_unique<GpuTexture>(graphics_, depth.Width, depth.Height, true, DXGI_FORMAT_R16G16_FLOAT,
                                                   false);
            ResetHistory();
        }
        float aspect = static_cast<float>(depth.Width) / depth.Height;
        auto vp = MultiplyMatrix(camera.View(), camera.Projection(aspect, false));
        if (!initialized_)
            previousVP_ = vp;
        ShaderValues values{
            {"_ProjectionParams", {camera.BottomOrigin ? -1.f : 1.f, camera.Near, camera.Far, 1 / camera.Far}},
            {"_ZBufferParams",
             {camera.Far / camera.Near - 1, 1, (camera.Far / camera.Near - 1) / camera.Far, 1 / camera.Far}},
            {"unity_CameraToWorld", MatrixValues(camera.World())},
            {"_NonJitteredVP", MatrixValues(vp)},
            {"_PreviousVP", MatrixValues(previousVP_)}};
        cameraPass_.Draw(*target_, values, {},
                         {{"_CameraDepthTexture", {cameraDepth.View.Get(), cameraDepth.Sampler.Get()}}}, &camera,
                         depth.ReadOnlyDepth.Get());
        auto *context = graphics_.Context();
        context->ClearState();
        SetTarget(graphics_, target_->Target.Get(), depth.Width, depth.Height, depth.Depth.Get());
        values["unity_MatrixVP"] = MatrixValues(MultiplyMatrix(camera.View(), camera.Projection(aspect)));
        values["_MotionVectorDepthBias"] = {-.001f};

        struct DrawItem
        {
            NativeSceneRenderer *Renderer;
            GpuMaterialInstance *Material;
            int Submesh;
        };

        std::vector<DrawItem> draws;
        for (auto *renderer : appearance_.Order)
            if (deformed.contains(renderer))
            {
                const auto &materials = appearance_.Materials.at(renderer);
                for (size_t i = 0; i < materials.size(); ++i)
                    if (materials[i] && materials[i]->Queue() <= 2500)
                        draws.push_back({renderer, materials[i].get(), static_cast<int>(i)});
            }
        std::stable_sort(draws.begin(), draws.end(),
                         [&](const auto &a, const auto &b)
                         {
                             return std::make_pair(a.Material->Queue(),
                                                   LengthSquared(Sub(camera.Position, a.Renderer->Bounds().Center))) <
                                    std::make_pair(b.Material->Queue(),
                                                   LengthSquared(Sub(camera.Position, b.Renderer->Bounds().Center)));
                         });
        for (auto d : draws)
        {
            auto &renderer = *d.Renderer;
            if (!renderer.Visible() || renderer.ShadowCasting == 3)
                continue;
            int mode = static_cast<int>(renderer.Source.Settings.Float("m_MotionVectors", 1));
            if (mode == 0)
                continue;
            if (mode < 0 || mode > 2)
                throw std::runtime_error("Unknown renderer motion-vector mode.");
            bool hasPrevious = initialized_ && previous_.contains(&renderer),
                 skinned = hasPrevious && renderer.Source.Skinned &&
                           renderer.Source.Settings.Float("m_SkinnedMotionVectors", 1) != 0;
            const auto &stream = deformed.at(&renderer);
            auto world = renderer.RenderWorldMatrix();
            values["_HasLastPositionData"] = {skinned ? 1.f : 0.f};
            values["_ForceNoMotion"] = {hasPrevious && mode != 2 ? 0.f : 1.f};
            values["unity_ObjectToWorld"] = MatrixValues(world);
            values["_PreviousM"] = MatrixValues(hasPrevious ? previous_.at(&renderer).Matrix : world);
            geometryPass_.Draw(*renderer.Mesh,
                               std::min(d.Submesh, static_cast<int>(renderer.Mesh->Submeshes.size()) - 1), values, {},
                               nullptr, {}, nullptr, &stream, skinned ? previous_.at(&renderer).Buffer.Get() : nullptr,
                               NativeForwardRenderer::GeometryMirrored(renderer));
        }
        context->ClearState();
        for (const auto &[renderer, stream] : deformed)
            previous_[renderer] = {ComPtr<ID3D11Buffer>(stream.Buffer), renderer->RenderWorldMatrix()};
        previousVP_ = vp;
        initialized_ = true;
    }

    NativeMotionBlur::NativeMotionBlur(GraphicsDevice &g, NativeGpuAssetStore &assets, GpuMaterialComposer &composer,
                                       const NativePostSettings &settings)
        : graphics_(g), settings_(settings), copy_(g)
    {
        auto shader = composer.Shader(assets.GameResource("Hidden/PostProcessing/MotionBlur", 48));
        for (size_t i = 0; i < passes_.size(); ++i)
            passes_[i] = std::make_unique<NativeFullscreenPass>(g, *shader, static_cast<int>(i));
    }

    void NativeMotionBlur::Resize(int width, int height)
    {
        if (width_ == width && height_ == height)
            return;
        targets_.clear();
        width_ = width;
        height_ = height;
        auto allocate = [&](DXGI_FORMAT format)
        {
            return std::make_unique<GpuTexture>(graphics_, width, height, true, format, false);
        };
        bottomSource_ = allocate(DXGI_FORMAT_R16G16B16A16_FLOAT);
        bottomVelocity_ = allocate(DXGI_FORMAT_R16G16_FLOAT);
        bottomDepth_ = allocate(DXGI_FORMAT_R32_FLOAT);
        bottomResult_ = allocate(DXGI_FORMAT_R16G16B16A16_FLOAT);
        radius_ = std::max(1, height * 5 / 100);
        tilePixels_ = ((radius_ + 7) / 8) * 8;
        targets_.push_back(std::make_unique<GpuTexture>(graphics_, width, height, true, DXGI_FORMAT_R10G10B10A2_UNORM,
                                                        false, D3D11_FILTER_MIN_MAG_MIP_POINT));
        for (int divisor : {2, 4, 8, tilePixels_, tilePixels_})
            targets_.push_back(
                std::make_unique<GpuTexture>(graphics_, std::max(1, width / divisor), std::max(1, height / divisor),
                                             true, DXGI_FORMAT_R16G16_FLOAT, false, D3D11_FILTER_MIN_MAG_MIP_POINT));
        first_ = true;
    }

    void NativeMotionBlur::Draw(const GpuTexture &source, GpuTexture &target, const GpuTexture &velocity,
                                const GpuDepthSurface &depth, const NativeRenderCamera &camera)
    {
        graphics_.VerifyThread();
        Resize(source.Width(), source.Height());
        if (first_)
        {
            copy_.Copy(source, target);
            first_ = false;
            return;
        }
        copy_.Copy(source, *bottomSource_, !camera.BottomOrigin);
        copy_.Copy(velocity, *bottomVelocity_, !camera.BottomOrigin, !camera.BottomOrigin);
        copy_.CopyView(depth.View.Get(), *bottomDepth_, !camera.BottomOrigin, false, true);
        float blocks = tilePixels_ / 8.f;
        ShaderValues values{
            {"_RenderViewportScaleFactor", {1}},
            {"unity_OrthoParams", {0, 0, 0, 0}},
            {"_ZBufferParams",
             {camera.Far / camera.Near - 1, 1, (camera.Far / camera.Near - 1) / camera.Far, 1 / camera.Far}},
            {"_ScreenParams",
             {static_cast<float>(width_), static_cast<float>(height_), 1 + 1.f / width_, 1 + 1.f / height_}},
            {"_VelocityScale", {settings_.Scalar("MotionBlur", "shutterAngle", 270) / 360}},
            {"_MaxBlurRadius", {static_cast<float>(radius_)}},
            {"_RcpMaxBlurRadius", {1.f / radius_}},
            {"_TileMaxLoop", {static_cast<float>(static_cast<int>(blocks))}},
            {"_TileMaxOffs", {(1 - blocks) / 2, (1 - blocks) / 2}},
            {"_LoopCount",
             {static_cast<float>(
                 std::clamp(static_cast<int>(settings_.Scalar("MotionBlur", "sampleCount", 10)) / 2, 1, 64))}}};
        passes_[0]->Draw(*targets_[0], values, {{"_CameraMotionVectorsTexture", bottomVelocity_.get()}},
                         {{"_CameraDepthTexture", {bottomDepth_->View.Get(), depth.Sampler.Get()}}});
        constexpr int stages[]{1, 2, 2, 3, 4};
        for (size_t i = 1; i < targets_.size(); ++i)
            passes_[stages[i - 1]]->Draw(*targets_[i], values, {{"_MainTex", targets_[i - 1].get()}});
        passes_[5]->Draw(*bottomResult_, values,
                         {{"_MainTex", bottomSource_.get()},
                          {"_VelocityTex", targets_[0].get()},
                          {"_NeighborMaxTex", targets_.back().get()}});
        copy_.Copy(*bottomResult_, target, !camera.BottomOrigin);
    }
}
