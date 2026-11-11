#include "GraphicsDevice.h"
#include <DirectXPackedVector.h>
#include <cstring>
#include <iomanip>
#include <sstream>
#include <stdexcept>

namespace hs2 {
void CheckHResult(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        std::ostringstream message;
        message << operation << " failed (HRESULT 0x" << std::hex << static_cast<unsigned long>(result) << ')';
        throw std::runtime_error(message.str());
    }
}
GraphicsDevice::GraphicsDevice(bool software) : owner_(std::this_thread::get_id()) {
    constexpr D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    auto result = D3D11CreateDevice(nullptr, software ? D3D_DRIVER_TYPE_WARP : D3D_DRIVER_TYPE_HARDWARE,
        nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION,
        &device_, nullptr, &context_);
    if (result == E_INVALIDARG) {
        result = D3D11CreateDevice(nullptr, software ? D3D_DRIVER_TYPE_WARP : D3D_DRIVER_TYPE_HARDWARE,
            nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels + 1, 1, D3D11_SDK_VERSION,
            &device_, nullptr, &context_);
    }
    CheckHResult(result, "D3D11CreateDevice");
    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    CheckHResult(device_.As(&dxgi), "Query IDXGIDevice");
    CheckHResult(dxgi->GetAdapter(&adapter), "GetAdapter");
    DXGI_ADAPTER_DESC description{};
    CheckHResult(adapter->GetDesc(&description), "Get adapter description");
    adapterName_ = description.Description;
}
GraphicsDevice::~GraphicsDevice() {
    // A destructor must not throw during device-removal or window teardown.
    if (context_ && owner_ == std::this_thread::get_id()) { context_->ClearState(); context_->Flush(); }
}
void GraphicsDevice::VerifyThread() const {
    if (owner_ != std::this_thread::get_id()) throw std::logic_error("Graphics device belongs to the rendering thread.");
}
ComPtr<ID3D11Buffer> GraphicsDevice::CreateBuffer(UINT size, UINT bindings, const void* data) const {
    VerifyThread();
    if (!size || ((bindings & D3D11_BIND_CONSTANT_BUFFER) && (size > 65536 || size % 16)))
        throw std::invalid_argument("Invalid GPU buffer size.");
    D3D11_BUFFER_DESC description{};
    description.ByteWidth = size; description.BindFlags = bindings;
    D3D11_SUBRESOURCE_DATA initial{}; initial.pSysMem = data;
    ComPtr<ID3D11Buffer> buffer;
    CheckHResult(device_->CreateBuffer(&description, data ? &initial : nullptr, &buffer), "CreateBuffer");
    return buffer;
}
namespace {
struct ReadMapping {
    ID3D11DeviceContext* context;
    ID3D11Resource* resource;
    D3D11_MAPPED_SUBRESOURCE value{};
    ReadMapping(ID3D11DeviceContext* ctx, ID3D11Resource* object) : context(ctx), resource(object) {
        CheckHResult(context->Map(resource, 0, D3D11_MAP_READ, 0, &value), "Map readback texture");
    }
    ~ReadMapping() { context->Unmap(resource, 0); }
};
ComPtr<ID3D11Texture2D> Stage(const GraphicsDevice& graphics, ID3D11Texture2D* source, D3D11_TEXTURE2D_DESC description) {
    description.Usage = D3D11_USAGE_STAGING; description.BindFlags = 0;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ; description.MiscFlags = 0; description.MipLevels = 1;
    ComPtr<ID3D11Texture2D> staging;
    CheckHResult(graphics.Device()->CreateTexture2D(&description, nullptr, &staging), "Create staging texture");
    graphics.Context()->CopySubresourceRegion(staging.Get(), 0, 0, 0, 0, source, 0, nullptr);
    return staging;
}
}
std::vector<std::uint8_t> GraphicsDevice::Readback(ID3D11Texture2D* source) const {
    VerifyThread(); if (!source) throw std::invalid_argument("Null readback texture.");
    D3D11_TEXTURE2D_DESC description{}; source->GetDesc(&description);
    if (description.SampleDesc.Count != 1 || description.ArraySize != 1 ||
        (description.Format != DXGI_FORMAT_B8G8R8A8_UNORM && description.Format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB && description.Format != DXGI_FORMAT_B8G8R8A8_TYPELESS))
        throw std::invalid_argument("Readback requires a resolved BGRA8 texture with one image.");
    auto staging = Stage(*this, source, description); ReadMapping mapped(context_.Get(), staging.Get());
    std::vector<std::uint8_t> pixels(static_cast<size_t>(description.Width) * description.Height * 4);
    for (UINT row = 0; row < description.Height; ++row)
        std::memcpy(pixels.data() + static_cast<size_t>(row) * description.Width * 4,
            static_cast<const std::uint8_t*>(mapped.value.pData) + static_cast<size_t>(row) * mapped.value.RowPitch, static_cast<size_t>(description.Width) * 4);
    return pixels;
}
std::vector<float> GraphicsDevice::ReadbackFloat(ID3D11Texture2D* source) const {
    VerifyThread(); if (!source) throw std::invalid_argument("Null readback texture.");
    D3D11_TEXTURE2D_DESC description{}; source->GetDesc(&description);
    if (description.SampleDesc.Count != 1 || description.ArraySize != 1 ||
        (description.Format != DXGI_FORMAT_R32G32B32A32_FLOAT && description.Format != DXGI_FORMAT_R16G16B16A16_FLOAT && description.Format != DXGI_FORMAT_R16G16_FLOAT))
        throw std::invalid_argument("Float readback requires a resolved RG16F, RGBA16F or RGBA32F texture.");
    auto staging = Stage(*this, source, description); ReadMapping mapped(context_.Get(), staging.Get());
    std::vector<float> pixels(static_cast<size_t>(description.Width) * description.Height * 4);
    for (UINT row = 0; row < description.Height; ++row) {
        auto* input = static_cast<const std::uint8_t*>(mapped.value.pData) + static_cast<size_t>(row) * mapped.value.RowPitch;
        auto* output = pixels.data() + static_cast<size_t>(row) * description.Width * 4;
        if (description.Format == DXGI_FORMAT_R32G32B32A32_FLOAT) std::memcpy(output, input, static_cast<size_t>(description.Width) * 16);
        else {
            const unsigned components = description.Format == DXGI_FORMAT_R16G16_FLOAT ? 2 : 4;
            for (UINT x = 0; x < description.Width; ++x) for (unsigned c = 0; c < components; ++c) {
                DirectX::PackedVector::HALF value;
                std::memcpy(&value, input + (static_cast<size_t>(x) * components + c) * 2, 2);
                output[x * 4 + c] = DirectX::PackedVector::XMConvertHalfToFloat(value);
            }
        }
    }
    return pixels;
}
std::vector<std::uint8_t> GraphicsDevice::ClearAndReadback(int width, int height, const std::array<float, 4>& color) const {
    VerifyThread();
    if (width < 1 || width > 8192 || height < 1 || height > 8192) throw std::out_of_range("Invalid render dimensions.");
    D3D11_TEXTURE2D_DESC description{};
    description.Width = width; description.Height = height; description.MipLevels = 1; description.ArraySize = 1;
    description.Format = DXGI_FORMAT_B8G8R8A8_UNORM; description.SampleDesc.Count = 1; description.BindFlags = D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> texture; ComPtr<ID3D11RenderTargetView> target;
    CheckHResult(device_->CreateTexture2D(&description, nullptr, &texture), "Create readback target");
    CheckHResult(device_->CreateRenderTargetView(texture.Get(), nullptr, &target), "Create readback view");
    context_->ClearRenderTargetView(target.Get(), color.data());
    return Readback(texture.Get());
}
}
