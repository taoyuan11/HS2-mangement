#include "Device/GpuResources.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <stdexcept>

namespace hs2
{
    namespace
    {
        void Dimensions(int width, int height, int max = 16384)
        {
            if (width < 1 || height < 1 || width > max || height > max)
                throw std::invalid_argument("Invalid texture dimensions.");
        }

        bool TrailingMatches(const GpuTextureData &source, size_t offset, bool blocks, int mipCount)
        {
            if (offset == source.Bytes.size())
                return true;
            if (offset > source.Bytes.size())
                return false;
            if (blocks &&
                (source.Bytes.size() == ((offset + 31) & ~size_t(31)) || source.Bytes.size() - offset == 32) &&
                std::all_of(source.Bytes.begin() + offset, source.Bytes.end(),
                            [](std::uint8_t b)
                            {
                                return b == 0;
                            }))
                return true;
            if (source.ImageCount != 1 || mipCount != 1 || (source.Format != 3 && source.Format != 5) ||
                source.Bytes.size() - offset != 26)
                return false;
            std::array<std::uint8_t, 26> footer{};
            constexpr char signature[] = "TRUEVISION-XFILE.";
            std::memcpy(footer.data() + 8, signature, sizeof(signature));
            const size_t stride = source.Format == 3 ? 3 : 4;
            for (size_t i = 0; i + stride <= footer.size(); i += stride)
                std::reverse(footer.begin() + i, footer.begin() + i + stride);
            return std::equal(footer.begin(), footer.end(), source.Bytes.begin() + offset);
        }

        D3D11_TEXTURE2D_DESC ImageDescription(int width, int height, DXGI_FORMAT format, UINT bindings, UINT slices = 1,
                                              UINT levels = 1)
        {
            Dimensions(width, height);
            D3D11_TEXTURE2D_DESC d{};
            d.Width = width;
            d.Height = height;
            d.MipLevels = levels;
            d.ArraySize = slices;
            d.Format = format;
            d.SampleDesc.Count = 1;
            d.BindFlags = bindings;
            return d;
        }
    }

    int EffectiveAnisotropy(int textureLevel, int qualityMode)
    {
        switch (qualityMode)
        {
        case 0:
            return 0;
        case 1:
            return std::clamp(textureLevel, 0, 16);
        case 2:
            return textureLevel > 0 ? std::clamp(textureLevel, 9, 16) : 0;
        default:
            throw std::invalid_argument("Invalid anisotropic filtering mode.");
        }
    }

    D3D11_TEXTURE_ADDRESS_MODE TextureAddress(int mode)
    {
        switch (mode)
        {
        case 0:
            return D3D11_TEXTURE_ADDRESS_WRAP;
        case 1:
            return D3D11_TEXTURE_ADDRESS_CLAMP;
        case 2:
            return D3D11_TEXTURE_ADDRESS_MIRROR;
        case 3:
            return D3D11_TEXTURE_ADDRESS_MIRROR_ONCE;
        default:
            throw std::invalid_argument("Unknown texture address mode.");
        }
    }

    D3D11_SAMPLER_DESC DefaultSampler(bool clamp, D3D11_FILTER filter)
    {
        D3D11_SAMPLER_DESC result{};
        result.Filter = filter;
        result.AddressU = result.AddressV = result.AddressW =
            clamp ? D3D11_TEXTURE_ADDRESS_CLAMP : D3D11_TEXTURE_ADDRESS_WRAP;
        result.MaxAnisotropy = 1;
        result.ComparisonFunc = D3D11_COMPARISON_NEVER;
        result.MinLOD = -std::numeric_limits<float>::max();
        result.MaxLOD = std::numeric_limits<float>::max();
        return result;
    }

    TextureUpload TextureLayout(const GpuTextureData &source)
    {
        Dimensions(source.Width, source.Height);
        const int maxMips = 1 + static_cast<int>(std::floor(std::log2(std::max(source.Width, source.Height))));
        if (source.MipCount < 0 || source.MipCount > maxMips || (source.ImageCount != 1 && source.ImageCount != 6) ||
            (source.ImageCount == 6 && (source.Dimension != 4 || source.Width != source.Height)))
            throw std::runtime_error("Invalid texture level count or image dimensions.");
        DXGI_FORMAT format;
        int unit;
        bool blocks = false;
        switch (source.Format)
        {
        case 1:
            format = DXGI_FORMAT_A8_UNORM;
            unit = 1;
            break;
        case 3:
        case 4:
            format = DXGI_FORMAT_R8G8B8A8_UNORM;
            unit = 4;
            break;
        case 5:
        case 14:
            format = DXGI_FORMAT_B8G8R8A8_UNORM;
            unit = 4;
            break;
        case 7:
            format = DXGI_FORMAT_B5G6R5_UNORM;
            unit = 2;
            break;
        case 9:
            format = DXGI_FORMAT_R16_UNORM;
            unit = 2;
            break;
        case 10:
            format = DXGI_FORMAT_BC1_UNORM;
            unit = 8;
            blocks = true;
            break;
        case 12:
            format = DXGI_FORMAT_BC3_UNORM;
            unit = 16;
            blocks = true;
            break;
        case 15:
            format = DXGI_FORMAT_R16_FLOAT;
            unit = 2;
            break;
        case 16:
            format = DXGI_FORMAT_R16G16_FLOAT;
            unit = 4;
            break;
        case 17:
            format = DXGI_FORMAT_R16G16B16A16_FLOAT;
            unit = 8;
            break;
        case 18:
            format = DXGI_FORMAT_R32_FLOAT;
            unit = 4;
            break;
        case 19:
            format = DXGI_FORMAT_R32G32_FLOAT;
            unit = 8;
            break;
        case 20:
            format = DXGI_FORMAT_R32G32B32A32_FLOAT;
            unit = 16;
            break;
        case 22:
            format = DXGI_FORMAT_R9G9B9E5_SHAREDEXP;
            unit = 4;
            break;
        case 24:
            format = DXGI_FORMAT_BC6H_UF16;
            unit = 16;
            blocks = true;
            break;
        case 25:
            format = DXGI_FORMAT_BC7_UNORM;
            unit = 16;
            blocks = true;
            break;
        case 26:
            format = DXGI_FORMAT_BC4_UNORM;
            unit = 8;
            blocks = true;
            break;
        case 27:
            format = DXGI_FORMAT_BC5_UNORM;
            unit = 16;
            blocks = true;
            break;
        default:
            throw std::runtime_error("Texture requires a lossless decoder before upload: " +
                                     std::to_string(source.Format));
        }
        int mipCount = source.MipCount;
        if (!mipCount)
        {
            size_t length = 0;
            for (int mip = 0, w = source.Width, h = source.Height; mip < maxMips;
                 ++mip, w = std::max(1, w / 2), h = std::max(1, h / 2))
            {
                length += (blocks ? size_t(std::max(1, (w + 3) / 4)) * std::max(1, (h + 3) / 4) * unit
                                  : size_t(w) * h * (source.Format == 3 ? 3 : unit)) *
                          source.ImageCount;
                if (TrailingMatches(source, length, blocks, mip + 1))
                {
                    mipCount = mip + 1;
                    break;
                }
            }
            if (!mipCount)
                throw std::runtime_error("Implicit mip count does not match texture payload.");
        }
        if (source.Srgb)
            switch (format)
            {
            case DXGI_FORMAT_R8G8B8A8_UNORM:
                format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
                break;
            case DXGI_FORMAT_B8G8R8A8_UNORM:
                format = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
                break;
            case DXGI_FORMAT_BC1_UNORM:
                format = DXGI_FORMAT_BC1_UNORM_SRGB;
                break;
            case DXGI_FORMAT_BC3_UNORM:
                format = DXGI_FORMAT_BC3_UNORM_SRGB;
                break;
            case DXGI_FORMAT_BC7_UNORM:
                format = DXGI_FORMAT_BC7_UNORM_SRGB;
                break;
            default:
                break;
            }
        TextureUpload upload{format, {}};
        size_t offset = 0;
        for (int slice = 0; slice < source.ImageCount; ++slice)
            for (int mip = 0, w = source.Width, h = source.Height; mip < mipCount;
                 ++mip, w = std::max(1, w / 2), h = std::max(1, h / 2))
            {
                const int pitch = (blocks ? std::max(1, (w + 3) / 4) : w) * unit;
                const size_t length = size_t(pitch) * (blocks ? std::max(1, (h + 3) / 4) : h);
                const size_t inputLength = source.Format == 3 ? size_t(w) * h * 3 : length;
                if (offset > source.Bytes.size() || inputLength > source.Bytes.size() - offset)
                    throw std::runtime_error("Texture mip data is truncated.");
                TextureLevel level{mip, w, h, pitch, std::vector<std::uint8_t>(length), slice};
                if (source.Format == 3)
                    for (size_t pixel = 0; pixel < size_t(w) * h; ++pixel)
                    {
                        std::memcpy(level.Bytes.data() + pixel * 4, source.Bytes.data() + offset + pixel * 3, 3);
                        level.Bytes[pixel * 4 + 3] = 255;
                    }
                else if (source.Format == 5)
                    for (size_t pixel = 0; pixel < size_t(w) * h; ++pixel)
                        for (size_t c = 0; c < 4; ++c)
                            level.Bytes[pixel * 4 + c] = source.Bytes[offset + pixel * 4 + 3 - c];
                else
                    std::memcpy(level.Bytes.data(), source.Bytes.data() + offset, length);
                offset += inputLength;
                upload.Levels.push_back(std::move(level));
            }
        if (!TrailingMatches(source, offset, blocks, mipCount))
            throw std::runtime_error("Texture payload contains unaccounted image data.");
        return upload;
    }

    GpuTexture::GpuTexture(GraphicsDevice &g, int width, int height, bool clamp, DXGI_FORMAT format, bool mipmaps,
                           D3D11_FILTER filter, int anisotropy)
        : width_(width), height_(height)
    {
        g.VerifyThread();
        auto mipFormat = format;
        if (format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)
            mipFormat = DXGI_FORMAT_B8G8R8A8_UNORM;
        else if (format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB)
            mipFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
        auto resourceFormat = format;
        if (mipmaps && mipFormat != format)
            resourceFormat = format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB ? DXGI_FORMAT_B8G8R8A8_TYPELESS
                                                                       : DXGI_FORMAT_R8G8B8A8_TYPELESS;
        auto description = ImageDescription(width, height, resourceFormat,
                                            D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE, 1, mipmaps ? 0 : 1);
        description.MiscFlags = mipmaps ? D3D11_RESOURCE_MISC_GENERATE_MIPS : 0;
        CheckHResult(g.Device()->CreateTexture2D(&description, nullptr, &Texture), "Create color texture");
        Texture->GetDesc(&description);
        D3D11_SHADER_RESOURCE_VIEW_DESC view{};
        view.Format = format;
        view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        view.Texture2D.MipLevels = description.MipLevels;
        CheckHResult(g.Device()->CreateShaderResourceView(Texture.Get(), &view, &View), "Create texture view");
        D3D11_RENDER_TARGET_VIEW_DESC target{};
        target.Format = format;
        target.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        CheckHResult(g.Device()->CreateRenderTargetView(Texture.Get(), &target, &Target), "Create render target");
        if (resourceFormat != format)
        {
            view.Format = mipFormat;
            CheckHResult(g.Device()->CreateShaderResourceView(Texture.Get(), &view, &mipView_), "Create mip view");
        }
        auto sampler = DefaultSampler(clamp, filter);
        if (anisotropy > 1)
        {
            sampler.Filter = D3D11_FILTER_ANISOTROPIC;
            sampler.MaxAnisotropy = std::clamp(anisotropy, 1, 16);
        }
        CheckHResult(g.Device()->CreateSamplerState(&sampler, &Sampler), "Create texture sampler");
    }

    GpuTexture::GpuTexture(GraphicsDevice &g, const GpuTextureData &source, int quality)
        : width_(source.Width), height_(source.Height)
    {
        g.VerifyThread();
        if (source.Dimension == 4 ? (source.ImageCount != 6 || source.Width != source.Height)
                                  : (source.Dimension != 2 || source.ImageCount != 1))
            throw std::invalid_argument("Texture upload requires a 2D image or six square cube faces.");
        if ((source.Format == 10 || source.Format == 12 || (source.Format >= 24 && source.Format <= 27)) &&
            (source.Width % 4 || source.Height % 4))
            throw std::invalid_argument("Compressed texture dimensions must be divisible by four before upload.");
        auto upload = TextureLayout(source);
        const auto mips = static_cast<UINT>(upload.Levels.size() / source.ImageCount);
        auto description = ImageDescription(source.Width, source.Height, upload.Format, D3D11_BIND_SHADER_RESOURCE,
                                            source.ImageCount, mips);
        if (source.Dimension == 4)
            description.MiscFlags = D3D11_RESOURCE_MISC_TEXTURECUBE;
        CheckHResult(g.Device()->CreateTexture2D(&description, nullptr, &Texture), "Create source texture");
        for (const auto &level : upload.Levels)
            g.Context()->UpdateSubresource(Texture.Get(), level.Mip + level.Slice * mips, nullptr, level.Bytes.data(),
                                           level.RowPitch, static_cast<UINT>(level.Bytes.size()));
        CheckHResult(g.Device()->CreateShaderResourceView(Texture.Get(), nullptr, &View), "Create source texture view");
        const int anisotropy = EffectiveAnisotropy(source.Anisotropy, quality);
        auto sampler = DefaultSampler(false, anisotropy > 1       ? D3D11_FILTER_ANISOTROPIC
                                             : source.Filter == 0 ? D3D11_FILTER_MIN_MAG_MIP_POINT
                                             : source.Filter == 1 ? D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT
                                                                  : D3D11_FILTER_MIN_MAG_MIP_LINEAR);
        sampler.AddressU = TextureAddress(source.WrapU);
        sampler.AddressV = TextureAddress(source.WrapV);
        sampler.AddressW = TextureAddress(source.WrapW);
        sampler.MipLODBias = source.MipBias;
        sampler.MaxAnisotropy = std::clamp(anisotropy, 1, 16);
        CheckHResult(g.Device()->CreateSamplerState(&sampler, &Sampler), "Create source texture sampler");
    }

    void GpuTexture::GenerateMips(GraphicsDevice &graphics)
    {
        graphics.VerifyThread();
        graphics.Context()->GenerateMips(mipView_ ? mipView_.Get() : View.Get());
    }

    GpuDepthSurface::GpuDepthSurface(GraphicsDevice &g, int width, int height) : Width(width), Height(height)
    {
        g.VerifyThread();
        auto d = ImageDescription(width, height, DXGI_FORMAT_R32G8X24_TYPELESS,
                                  D3D11_BIND_DEPTH_STENCIL | D3D11_BIND_SHADER_RESOURCE);
        CheckHResult(g.Device()->CreateTexture2D(&d, nullptr, &Texture), "Create depth texture");
        D3D11_DEPTH_STENCIL_VIEW_DESC depth{};
        depth.Format = DXGI_FORMAT_D32_FLOAT_S8X24_UINT;
        depth.ViewDimension = D3D11_DSV_DIMENSION_TEXTURE2D;
        CheckHResult(g.Device()->CreateDepthStencilView(Texture.Get(), &depth, &Depth), "Create depth view");
        depth.Flags = D3D11_DSV_READ_ONLY_DEPTH | D3D11_DSV_READ_ONLY_STENCIL;
        CheckHResult(g.Device()->CreateDepthStencilView(Texture.Get(), &depth, &ReadOnlyDepth),
                     "Create readonly depth view");
        D3D11_SHADER_RESOURCE_VIEW_DESC view{};
        view.Format = DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
        view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        view.Texture2D.MipLevels = 1;
        CheckHResult(g.Device()->CreateShaderResourceView(Texture.Get(), &view, &View), "Create depth resource view");
        auto sampler = DefaultSampler(true, D3D11_FILTER_MIN_MAG_MIP_POINT);
        CheckHResult(g.Device()->CreateSamplerState(&sampler, &Sampler), "Create depth sampler");
    }

    GpuComputeImage::GpuComputeImage(GraphicsDevice &g, int width, int height, DXGI_FORMAT format, int slices)
        : Width(width), Height(height), Slices(slices)
    {
        g.VerifyThread();
        if (slices < 1 || slices > 2048)
            throw std::invalid_argument("Invalid image array size.");
        auto d =
            ImageDescription(width, height, format, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS, slices);
        CheckHResult(g.Device()->CreateTexture2D(&d, nullptr, &Texture), "Create compute image");
        CheckHResult(g.Device()->CreateShaderResourceView(Texture.Get(), nullptr, &View), "Create compute image view");
        CheckHResult(g.Device()->CreateUnorderedAccessView(Texture.Get(), nullptr, &Output),
                     "Create compute image output");
        auto sampler = DefaultSampler(true, D3D11_FILTER_MIN_MAG_MIP_POINT);
        CheckHResult(g.Device()->CreateSamplerState(&sampler, &Sampler), "Create compute image sampler");
    }

    GpuVolume::GpuVolume(GraphicsDevice &g, int width, int height, int depth, DXGI_FORMAT format)
    {
        g.VerifyThread();
        Dimensions(width, height, 2048);
        if (depth < 1 || depth > 2048)
            throw std::invalid_argument("Invalid volume depth.");
        D3D11_TEXTURE3D_DESC d{};
        d.Width = width;
        d.Height = height;
        d.Depth = depth;
        d.MipLevels = 1;
        d.Format = format;
        d.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
        CheckHResult(g.Device()->CreateTexture3D(&d, nullptr, &Texture), "Create compute volume");
        CheckHResult(g.Device()->CreateShaderResourceView(Texture.Get(), nullptr, &View), "Create volume view");
        CheckHResult(g.Device()->CreateUnorderedAccessView(Texture.Get(), nullptr, &Output), "Create volume output");
        auto sampler = DefaultSampler();
        CheckHResult(g.Device()->CreateSamplerState(&sampler, &Sampler), "Create volume sampler");
    }

    NativeSwapChainSurface::NativeSwapChainSurface(GraphicsDevice &g, PreviewViewportSize size) : graphics_(g)
    {
        g.VerifyThread();
        Dimensions(size.Width, size.Height, 8192);
        ComPtr<IDXGIDevice> device;
        ComPtr<IDXGIAdapter> adapter;
        ComPtr<IDXGIFactory2> factory;
        ComPtr<IDXGISwapChain1> created;
        CheckHResult(g.Device()->QueryInterface(IID_PPV_ARGS(&device)), "Query DXGI device");
        CheckHResult(device->GetAdapter(&adapter), "Get DXGI adapter");
        CheckHResult(adapter->GetParent(IID_PPV_ARGS(&factory)), "Get DXGI factory");
        DXGI_SWAP_CHAIN_DESC1 d{};
        d.Width = size.Width;
        d.Height = size.Height;
        d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        d.SampleDesc.Count = 1;
        d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        d.BufferCount = 2;
        d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        d.Scaling = DXGI_SCALING_STRETCH;
        d.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        CheckHResult(factory->CreateSwapChainForComposition(g.Device(), &d, nullptr, &created),
                     "Create composition swap chain");
        CheckHResult(created.As(&swapChain_), "Query swap chain 2");
        Resize(size);
    }

    ComPtr<IDXGISwapChain2> NativeSwapChainSurface::Acquire() const
    {
        graphics_.VerifyThread();
        return swapChain_;
    }

    void NativeSwapChainSurface::Resize(PreviewViewportSize size)
    {
        graphics_.VerifyThread();
        Dimensions(size.Width, size.Height, 8192);
        if (!std::isfinite(size.RasterizationScale) || size.RasterizationScale <= 0)
            throw std::invalid_argument("Invalid rasterization scale.");
        if (size.Width != size_.Width || size.Height != size_.Height)
        {
            graphics_.Context()->ClearState();
            buffer_.Reset();
            CheckHResult(swapChain_->ResizeBuffers(2, size.Width, size.Height, DXGI_FORMAT_B8G8R8A8_UNORM, 0),
                         "Resize composition buffers");
            CheckHResult(swapChain_->GetBuffer(0, IID_PPV_ARGS(&buffer_)), "Get composition buffer");
        }
        DXGI_MATRIX_3X2_F matrix{1 / size.RasterizationScale, 0, 0, 1 / size.RasterizationScale, 0, 0};
        CheckHResult(swapChain_->SetMatrixTransform(&matrix), "Set composition scale");
        size_ = size;
    }

    void NativeSwapChainSurface::Present(const GpuTexture &frame)
    {
        graphics_.VerifyThread();
        if (frame.Width() != size_.Width || frame.Height() != size_.Height)
            throw std::invalid_argument("Composition frame dimensions differ from the viewport.");
        D3D11_TEXTURE2D_DESC d{};
        frame.Texture->GetDesc(&d);
        // CopyResource 在 BGRA8 格式族内保留编码字节。
        // 最终 sRGB 渲染目标已将线性场景值编码为 UNORM 合成链所需数据；
        // 再次转换会使画面变亮，并造成截图与视口不一致。
        const bool bgra = d.Format == DXGI_FORMAT_B8G8R8A8_UNORM || d.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB ||
                          d.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS;
        if (!bgra || d.MipLevels != 1 || d.ArraySize != 1 || d.SampleDesc.Count != 1)
            throw std::invalid_argument("Composition requires a single BGRA8 output surface.");
        graphics_.Context()->ClearState();
        graphics_.Context()->CopyResource(buffer_.Get(), frame.Texture.Get());
        CheckHResult(swapChain_->Present(0, 0), "Present composition surface");
    }
}
