#include "Agent.h"
#include <windows.h>
#include <wincrypt.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <cmath>

namespace hs2::agent
{
    Json ReadJson(const fs::path &file)
    {
        auto bytes = ReadFile(file, 16 * 1024 * 1024);
        return Json::parse(bytes);
    }

    std::string Base64(std::span<const std::uint8_t> bytes)
    {
        DWORD count = 0;
        auto flags = CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF;
        if (!CryptBinaryToStringA(bytes.data(), static_cast<DWORD>(bytes.size()), flags, nullptr, &count))
            throw std::runtime_error("Base64 编码失败");
        std::string output(count, '\0');
        if (!CryptBinaryToStringA(bytes.data(), static_cast<DWORD>(bytes.size()), flags, output.data(), &count))
            throw std::runtime_error("Base64 编码失败");
        output.resize(count);
        return output;
    }

    Bytes Unbase64(std::string_view value)
    {
        if (value.size() > 44 * 1024 * 1024)
            throw std::invalid_argument("图片超过 32 MiB");
        DWORD count = 0;
        auto flags = CRYPT_STRING_BASE64 | CRYPT_STRING_STRICT;
        if (!CryptStringToBinaryA(value.data(), static_cast<DWORD>(value.size()), flags, nullptr, &count, nullptr,
                                  nullptr))
            throw std::invalid_argument("Base64 无效");
        Bytes output(count);
        if (!CryptStringToBinaryA(value.data(), static_cast<DWORD>(value.size()), flags, output.data(), &count, nullptr,
                                  nullptr))
            throw std::invalid_argument("Base64 无效");
        return output;
    }

    void ValidateImage(const fs::path &file)
    {
        using Microsoft::WRL::ComPtr;
        ComPtr<IWICImagingFactory> factory;
        ComPtr<IWICBitmapDecoder> decoder;
        ComPtr<IWICBitmapFrameDecode> frame;
        auto check = [](HRESULT hr)
        {
            if (FAILED(hr))
                throw std::invalid_argument("图片无法完整解码");
        };
        check(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)));
        check(factory->CreateDecoderFromFilename(file.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad,
                                                 &decoder));
        GUID container{};
        check(decoder->GetContainerFormat(&container));
        if (container != GUID_ContainerFormatPng && container != GUID_ContainerFormatJpeg)
            throw std::invalid_argument("只支持 PNG/JPEG");
        check(decoder->GetFrame(0, &frame));
        UINT width = 0, height = 0;
        check(frame->GetSize(&width, &height));
        if (!width || !height || width > 8192 || height > 8192 || std::uint64_t(width) * height > 32 * 1024 * 1024)
            throw std::invalid_argument("图片尺寸越界：最大 8192 边长／3200 万像素");
        ComPtr<IWICFormatConverter> converter;
        check(factory->CreateFormatConverter(&converter));
        check(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0,
                                    WICBitmapPaletteTypeCustom));
        Bytes pixels(static_cast<size_t>(width) * height * 4);
        check(converter->CopyPixels(nullptr, width * 4, static_cast<UINT>(pixels.size()), pixels.data()));
    }

    MessagePack::Value Scalar(const Json &value)
    {
        if (value.is_boolean())
            return value.get<bool>();
        if (value.is_number_integer())
            return value.get<std::int64_t>();
        if (value.is_number_float() && std::isfinite(value.get<double>()))
            return value.get<double>();
        if (value.is_string())
            return value.get<std::string>();
        throw std::invalid_argument("参数必须是有限数值、布尔值或字符串");
    }

    Json ScalarJson(const MessagePack::Value &value)
    {
        return std::visit(
            [](const auto &v) -> Json
            {
                if constexpr (std::is_same_v<std::decay_t<decltype(v)>, std::monostate>)
                    return nullptr;
                else
                    return v;
            },
            value);
    }
}
