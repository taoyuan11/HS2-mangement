#include "Images/ImageFiles.h"
#include "Device/GraphicsDevice.h"
#include "Common/Core.h"
#include <wincodec.h>
#include <DirectXPackedVector.h>
#include <algorithm>
#include <cmath>
#include <cstring>

namespace hs2
{
    namespace
    {
        void Dimensions(int width, int height, std::size_t samples)
        {
            if (width < 1 || height < 1 || width > 32767 || height > 32767 ||
                samples != static_cast<std::size_t>(width) * height * 4)
                throw std::invalid_argument("Invalid exported image dimensions.");
        }

        void Validate(int width, int height, std::span<const float> values)
        {
            Dimensions(width, height, values.size());
            if (std::any_of(values.begin(), values.end(),
                            [](auto v)
                            {
                                return !std::isfinite(v);
                            }))
                throw std::invalid_argument("Nonfinite exported image sample.");
        }

        template <class T> void Append(Bytes &output, T value)
        {
            auto first = reinterpret_cast<const std::uint8_t *>(&value);
            output.insert(output.end(), first, first + sizeof(T));
        }

        void Text(Bytes &output, const std::string &value, bool zero = false)
        {
            output.insert(output.end(), value.begin(), value.end());
            if (zero)
                output.push_back(0);
        }
    }

    void SavePng(const fs::path &path, int width, int height, std::span<const std::uint8_t> bgra)
    {
        Dimensions(width, height, bgra.size());
        auto hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

        struct Apartment
        {
            bool Owned;

            ~Apartment()
            {
                if (Owned)
                    CoUninitialize();
            }
        } apartment{SUCCEEDED(hr)};

        if (FAILED(hr) && hr != RPC_E_CHANGED_MODE)
            CheckHResult(hr, "Image COM initialization");
        try
        {
            ComPtr<IWICImagingFactory> factory;
            CheckHResult(
                CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)),
                "Create PNG encoder factory");
            ComPtr<IStream> stream;
            CheckHResult(CreateStreamOnHGlobal(nullptr, TRUE, &stream), "Create PNG memory stream");
            ComPtr<IWICBitmapEncoder> encoder;
            CheckHResult(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder), "Create PNG encoder");
            CheckHResult(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache), "Initialize PNG encoder");
            ComPtr<IWICBitmapFrameEncode> frame;
            CheckHResult(encoder->CreateNewFrame(&frame, nullptr), "Create PNG frame");
            CheckHResult(frame->Initialize(nullptr), "Initialize PNG frame");
            CheckHResult(frame->SetSize(width, height), "Set PNG dimensions");
            auto format = GUID_WICPixelFormat32bppBGRA;
            CheckHResult(frame->SetPixelFormat(&format), "Set PNG format");
            if (format != GUID_WICPixelFormat32bppBGRA)
                throw std::runtime_error("PNG encoder changed pixel format.");
            CheckHResult(
                frame->WritePixels(height, width * 4, static_cast<UINT>(bgra.size()), const_cast<BYTE *>(bgra.data())),
                "Write PNG pixels");
            CheckHResult(frame->Commit(), "Commit PNG frame");
            CheckHResult(encoder->Commit(), "Commit PNG image");
            STATSTG stat{};
            CheckHResult(stream->Stat(&stat, STATFLAG_NONAME), "Read PNG size");
            if (stat.cbSize.QuadPart > 512ull * 1024 * 1024)
                throw std::runtime_error("Encoded PNG exceeds bound.");
            HGLOBAL memory = nullptr;
            CheckHResult(GetHGlobalFromStream(stream.Get(), &memory), "Read PNG stream");
            auto *bytes = static_cast<const std::uint8_t *>(GlobalLock(memory));
            if (!bytes)
                throw std::runtime_error("PNG stream lock failed.");
            try
            {
                WriteAtomic(path, std::span(bytes, static_cast<std::size_t>(stat.cbSize.QuadPart)));
            }
            catch (...)
            {
                GlobalUnlock(memory);
                throw;
            }
            GlobalUnlock(memory);
        }
        catch (...)
        {
            throw;
        }
    }

    void WriteRadiance(const fs::path &path, int width, int height, std::span<const float> pixels)
    {
        Validate(width, height, pixels);
        if (width < 8)
            throw std::invalid_argument("Radiance image width must be at least 8.");
        Bytes output;
        Text(output, "#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y " + std::to_string(height) + " +X " +
                         std::to_string(width) + "\n");
        Bytes row(static_cast<std::size_t>(width) * 4);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                auto i = (static_cast<std::size_t>(y) * width + x) * 4;
                float maximum = (std::max)({pixels[i], pixels[i + 1], pixels[i + 2]});
                int exponent =
                    maximum < 1e-32f ? 0 : static_cast<int>(std::floor(std::log2(static_cast<double>(maximum)))) + 1;
                if (maximum >= 1e-32f && (exponent + 128 < 0 || exponent + 128 > 255))
                    throw std::runtime_error("Radiance exponent exceeds format range.");
                double scale = maximum < 1e-32f ? 0 : 256 / std::pow(2., exponent);
                for (int c = 0; c < 3; c++)
                    row[static_cast<std::size_t>(c) * width + x] =
                        static_cast<std::uint8_t>(std::clamp(pixels[i + c] * scale, 0., 255.));
                row[static_cast<std::size_t>(3) * width + x] =
                    maximum < 1e-32f ? 0 : static_cast<std::uint8_t>(exponent + 128);
            }
            output.insert(output.end(),
                          {2, 2, static_cast<std::uint8_t>(width >> 8), static_cast<std::uint8_t>(width)});
            for (int c = 0; c < 4; c++)
                for (int x = 0; x < width; x += 128)
                {
                    int length = (std::min)(128, width - x);
                    output.push_back(static_cast<std::uint8_t>(length));
                    output.insert(output.end(), row.begin() + c * width + x, row.begin() + c * width + x + length);
                }
        }
        WriteAtomic(path, output);
    }

    void WriteOpenExr(const fs::path &path, int width, int height, std::span<const float> pixels)
    {
        Validate(width, height, pixels);
        if (std::any_of(pixels.begin(), pixels.end(),
                        [](float value)
                        {
                            return std::abs(value) > 65504;
                        }))
            throw std::runtime_error("HDR sample exceeds HALF range.");
        Bytes output;
        Append(output, std::uint32_t(20000630));
        Append(output, std::uint32_t(2));
        auto attribute = [&](const std::string &name, const std::string &type, const Bytes &value)
        {
            Text(output, name, true);
            Text(output, type, true);
            Append(output, static_cast<std::int32_t>(value.size()));
            output.insert(output.end(), value.begin(), value.end());
        };
        Bytes channels;
        for (auto name : {"A", "B", "G", "R"})
        {
            Text(channels, name, true);
            Append(channels, std::int32_t(1));
            Append(channels, std::int32_t(0));
            Append(channels, std::int32_t(1));
            Append(channels, std::int32_t(1));
        }
        channels.push_back(0);
        attribute("channels", "chlist", channels);
        attribute("compression", "compression", {0});
        Bytes window;
        for (int value : {0, 0, width - 1, height - 1})
            Append(window, static_cast<std::int32_t>(value));
        attribute("dataWindow", "box2i", window);
        attribute("displayWindow", "box2i", window);
        attribute("lineOrder", "lineOrder", {0});
        Bytes one;
        Append(one, 1.f);
        attribute("pixelAspectRatio", "float", one);
        Bytes center;
        Append(center, 0.f);
        Append(center, 0.f);
        attribute("screenWindowCenter", "v2f", center);
        attribute("screenWindowWidth", "float", one);
        output.push_back(0);
        auto first = output.size() + static_cast<std::uint64_t>(height) * 8;
        auto rowBytes = static_cast<std::int32_t>(width * 8);
        for (int y = 0; y < height; y++)
            Append(output, first + static_cast<std::uint64_t>(y) * (rowBytes + 8));
        for (int y = 0; y < height; y++)
        {
            Append(output, static_cast<std::int32_t>(y));
            Append(output, rowBytes);
            for (int component : {3, 2, 1, 0})
                for (int x = 0; x < width; x++)
                    Append(output, DirectX::PackedVector::XMConvertFloatToHalf(
                                       pixels[(static_cast<std::size_t>(y) * width + x) * 4 + component]));
        }
        WriteAtomic(path, output);
    }
}
