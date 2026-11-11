#pragma once
#include "Device/GraphicsDevice.h"
#include "Scene/SceneTypes.h"

namespace hs2
{
    struct TextureLevel
    {
        int Mip = 0, Width = 0, Height = 0, RowPitch = 0;
        std::vector<std::uint8_t> Bytes;
        int Slice = 0;
    };

    struct TextureUpload
    {
        DXGI_FORMAT Format = DXGI_FORMAT_UNKNOWN;
        std::vector<TextureLevel> Levels;
    };

    TextureUpload TextureLayout(const GpuTextureData &source);
    int EffectiveAnisotropy(int textureLevel, int qualityMode);
    D3D11_TEXTURE_ADDRESS_MODE TextureAddress(int mode);
    D3D11_SAMPLER_DESC DefaultSampler(bool clamp = true, D3D11_FILTER filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR);

    class GpuTexture
    {
    public:
        GpuTexture(GraphicsDevice &graphics, int width, int height, bool clamp = false,
                   DXGI_FORMAT format = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB, bool mipmaps = true,
                   D3D11_FILTER filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT, int anisotropy = 0);
        GpuTexture(GraphicsDevice &graphics, const GpuTextureData &source, int anisotropicFiltering = 1);

        int Width() const noexcept
        {
            return width_;
        }

        int Height() const noexcept
        {
            return height_;
        }

        void GenerateMips(GraphicsDevice &graphics);
        ComPtr<ID3D11Texture2D> Texture;
        ComPtr<ID3D11ShaderResourceView> View;
        ComPtr<ID3D11SamplerState> Sampler;
        ComPtr<ID3D11RenderTargetView> Target;

    private:
        int width_ = 0, height_ = 0;
        ComPtr<ID3D11ShaderResourceView> mipView_;
    };

    class GpuDepthSurface
    {
    public:
        GpuDepthSurface(GraphicsDevice &graphics, int width, int height);
        ComPtr<ID3D11Texture2D> Texture;
        ComPtr<ID3D11DepthStencilView> Depth, ReadOnlyDepth;
        ComPtr<ID3D11ShaderResourceView> View;
        ComPtr<ID3D11SamplerState> Sampler;
        int Width = 0, Height = 0;
    };

    class GpuComputeImage
    {
    public:
        GpuComputeImage(GraphicsDevice &graphics, int width, int height, DXGI_FORMAT format, int slices = 1);
        ComPtr<ID3D11Texture2D> Texture;
        ComPtr<ID3D11ShaderResourceView> View;
        ComPtr<ID3D11UnorderedAccessView> Output;
        ComPtr<ID3D11SamplerState> Sampler;
        int Width = 0, Height = 0, Slices = 0;
    };

    class GpuVolume
    {
    public:
        GpuVolume(GraphicsDevice &graphics, int width, int height, int depth,
                  DXGI_FORMAT format = DXGI_FORMAT_R16G16B16A16_FLOAT);
        ComPtr<ID3D11Texture3D> Texture;
        ComPtr<ID3D11ShaderResourceView> View;
        ComPtr<ID3D11UnorderedAccessView> Output;
        ComPtr<ID3D11SamplerState> Sampler;
    };

    struct PreviewViewportSize
    {
        int Width = 1, Height = 1;
        float RasterizationScale = 1;
    };

    class NativeSwapChainSurface
    {
    public:
        NativeSwapChainSurface(GraphicsDevice &graphics, PreviewViewportSize size);
        void Resize(PreviewViewportSize size);
        void Present(const GpuTexture &frame);
        // 界面绑定交换链期间，此副本始终持有对应的 COM 引用。
        ComPtr<IDXGISwapChain2> Acquire() const;

    private:
        GraphicsDevice &graphics_;
        ComPtr<IDXGISwapChain2> swapChain_;
        ComPtr<ID3D11Texture2D> buffer_;
        PreviewViewportSize size_{0, 0, 1};
    };
}
