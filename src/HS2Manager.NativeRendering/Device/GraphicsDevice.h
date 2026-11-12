#pragma once
#include <d3d11.h>
#include <dxgi1_3.h>
#include <wrl/client.h>
#include <array>
#include <cstdint>
#include <span>
#include <string>
#include <thread>
#include <vector>

namespace hs2
{
    template <class T> using ComPtr = Microsoft::WRL::ComPtr<T>;
    void CheckHResult(HRESULT result, const char *operation);

    // 即时上下文与资源仅在拥有设备的线程内使用。
    class GraphicsDevice final
    {
    public:
        explicit GraphicsDevice(bool software = false);
        ~GraphicsDevice();
        GraphicsDevice(const GraphicsDevice &) = delete;
        GraphicsDevice &operator=(const GraphicsDevice &) = delete;
        void VerifyThread() const;

        ID3D11Device *Device() const noexcept
        {
            return device_.Get();
        }

        ID3D11DeviceContext *Context() const noexcept
        {
            return context_.Get();
        }

        std::vector<std::uint8_t> Readback(ID3D11Texture2D *source) const;
        std::vector<float> ReadbackFloat(ID3D11Texture2D *source) const;
        std::vector<std::uint8_t> ClearAndReadback(int width, int height, const std::array<float, 4> &color) const;
        ComPtr<ID3D11Buffer> CreateBuffer(UINT byteSize, UINT bindings, const void *initialData = nullptr) const;

    private:
        std::thread::id owner_;
        ComPtr<ID3D11Device> device_;
        ComPtr<ID3D11DeviceContext> context_;
    };
}
