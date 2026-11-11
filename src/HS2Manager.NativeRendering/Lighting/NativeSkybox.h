#pragma once
#include "Materials/GpuMaterialComposer.h"

namespace hs2
{
    class NativeSkybox
    {
    public:
        NativeSkybox(GraphicsDevice &graphics, GpuMaterialComposer &composer, const EnvironmentDefinition &environment);
        void Draw(GpuTexture &target, NativeRenderCamera camera, ID3D11DepthStencilView *depth = nullptr);
        void Draw(ID3D11RenderTargetView *target, int width, int height, NativeRenderCamera camera,
                  ID3D11DepthStencilView *depth = nullptr);
        std::array<float, 27> GenerateAmbientProbe(int size = 64);

    private:
        GraphicsDevice &graphics_;
        std::shared_ptr<GpuMaterialInstance> material_;
        std::unique_ptr<NativeMaterialPass> pass_;
        std::unordered_map<std::string, std::shared_ptr<GpuTexture>> textures_;
        ShaderValues values_;
    };

    class NativeBuiltinTextures
    {
    public:
        explicit NativeBuiltinTextures(GraphicsDevice &graphics) : graphics_(graphics)
        {
        }

        std::optional<ShaderResource> TryView(const std::string &name, bool bottomOrigin = false);
        static std::vector<std::uint8_t> DitherMask();
        static std::vector<std::uint8_t> RoughnessLookup();

    private:
        GraphicsDevice &graphics_;
        ComPtr<ID3D11Texture3D> dither_, originalDither_;
        ComPtr<ID3D11ShaderResourceView> ditherView_, originalDitherView_;
        ComPtr<ID3D11SamplerState> ditherSampler_;
        std::unique_ptr<GpuTexture> roughness_;
    };
}
