#include "PaintDocument.h"
#include "Device/GraphicsDevice.h"
#include "Images/ImageFiles.h"
#include <wincodec.h>
#include <algorithm>
#include <queue>

namespace hs2::creation
{
    namespace
    {
        void Require(bool value, const char *message)
        {
            if (!value)
                throw std::invalid_argument(message);
        }

        std::uint8_t Byte(float value)
        {
            return static_cast<std::uint8_t>(std::clamp(std::lround(value * 255), 0l, 255l));
        }

        void Blend(std::uint8_t *pixel, Vector4 color, float coverage, bool erase)
        {
            float alpha = std::clamp(color.w * coverage, 0.f, 1.f), old = pixel[3] / 255.f;
            if (erase)
            {
                pixel[3] = Byte(old * (1 - coverage));
                return;
            }
            float result = alpha + old * (1 - alpha);
            if (result <= 0)
                return;
            pixel[0] = Byte((color.z * alpha + pixel[0] / 255.f * old * (1 - alpha)) / result);
            pixel[1] = Byte((color.y * alpha + pixel[1] / 255.f * old * (1 - alpha)) / result);
            pixel[2] = Byte((color.x * alpha + pixel[2] / 255.f * old * (1 - alpha)) / result);
            pixel[3] = Byte(result);
        }
    }

    bool PaintDocument::SamePixels(const PaintDocument &other) const
    {
        if (Width != other.Width || Height != other.Height || Srgb != other.Srgb ||
            Layers.size() != other.Layers.size())
            return false;
        for (std::size_t i = 0; i < Layers.size(); ++i)
            if (Layers[i].Pixels != other.Layers[i].Pixels || Layers[i].Visible != other.Layers[i].Visible ||
                Layers[i].Opacity != other.Layers[i].Opacity)
                return false;
        return true;
    }

    PaintDocument PaintDocument::Blank(int width, int height)
    {
        PaintDocument result;
        result.Width = width;
        result.Height = height;
        Require(width > 0 && height > 0 && width <= 8192 && height <= 8192 &&
                    std::uint64_t(width) * height <= 32 * 1024 * 1024,
                "贴图尺寸超出限制");
        result.AddLayer("底层");
        return result;
    }

    PaintDocument PaintDocument::Import(const fs::path &path)
    {
        Require(fs::file_size(fs::path(NativeFilePath(path))) <= 32 * 1024 * 1024, "贴图文件超过 32 MiB");
        ComPtr<IWICImagingFactory> factory;
        ComPtr<IWICBitmapDecoder> decoder;
        ComPtr<IWICBitmapFrameDecode> frame;
        CheckHResult(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)),
                     "创建图像解码器");
        CheckHResult(factory->CreateDecoderFromFilename(NativeFilePath(path).c_str(), nullptr, GENERIC_READ,
                                                        WICDecodeMetadataCacheOnLoad, &decoder),
                     "读取贴图");
        GUID format{};
        CheckHResult(decoder->GetContainerFormat(&format), "读取贴图格式");
        Require(format == GUID_ContainerFormatPng || format == GUID_ContainerFormatJpeg, "贴图只支持 PNG/JPEG");
        CheckHResult(decoder->GetFrame(0, &frame), "读取贴图帧");
        UINT width = 0, height = 0;
        CheckHResult(frame->GetSize(&width, &height), "读取贴图尺寸");
        auto result = Blank(static_cast<int>(width), static_cast<int>(height));
        ComPtr<IWICFormatConverter> converter;
        CheckHResult(factory->CreateFormatConverter(&converter), "创建颜色转换器");
        CheckHResult(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr,
                                           0, WICBitmapPaletteTypeCustom),
                     "转换贴图颜色");
        auto pixels = std::make_shared<Bytes>(static_cast<std::size_t>(width) * height * 4);
        CheckHResult(converter->CopyPixels(nullptr, width * 4, static_cast<UINT>(pixels->size()), pixels->data()),
                     "解码完整贴图");
        result.Layers[0].Pixels = std::move(pixels);
        result.Layers[0].Name = Utf8(path.filename());
        return result;
    }

    void PaintDocument::Validate() const
    {
        Require(Width > 0 && Height > 0 && Width <= 8192 && Height <= 8192 &&
                    std::uint64_t(Width) * Height <= 32 * 1024 * 1024,
                "贴图尺寸超出限制");
        Require(!Layers.empty() && Layers.size() <= 64, "图层数量必须为 1 至 64");
        Require(std::uint64_t(Width) * Height * Layers.size() * 4 <= 1024ull * 1024 * 1024, "贴图图层总内存超过 1 GiB");
        std::set<std::string> ids;
        for (const auto &layer : Layers)
            Require(!layer.Id.empty() && ids.insert(layer.Id).second && layer.Pixels &&
                        layer.Pixels->size() == static_cast<std::size_t>(Width) * Height * 4 &&
                        std::isfinite(layer.Opacity) && layer.Opacity >= 0 && layer.Opacity <= 1,
                    "贴图图层数据损坏");
    }

    void PaintDocument::AddLayer(std::string name)
    {
        Require(Layers.size() < 64 && std::uint64_t(Width) * Height * (Layers.size() + 1) * 4 <= 1024ull * 1024 * 1024,
                "图层达到容量限制");
        PaintLayer layer;
        layer.Name = std::move(name);
        layer.Pixels = std::make_shared<Bytes>(static_cast<std::size_t>(Width) * Height * 4, 0);
        Layers.push_back(std::move(layer));
    }

    void PaintDocument::ImportLayer(const fs::path &path)
    {
        auto source = Import(path);
        AddLayer(Utf8(path.filename()));
        auto pixels = std::make_shared<Bytes>(static_cast<std::size_t>(Width) * Height * 4);
        for (int y = 0; y < Height; ++y)
            for (int x = 0; x < Width; ++x)
            {
                auto from = (static_cast<std::size_t>(y) * source.Height / Height * source.Width +
                             static_cast<std::size_t>(x) * source.Width / Width) *
                            4;
                auto to = (static_cast<std::size_t>(y) * Width + x) * 4;
                std::copy_n(source.Layers[0].Pixels->data() + from, 4, pixels->data() + to);
            }
        Layers.back().Pixels = std::move(pixels);
    }

    Json PaintDocument::ToJson() const
    {
        Json layers = Json::array();
        for (const auto &layer : Layers)
            layers.push_back({{"id", layer.Id},
                              {"name", layer.Name},
                              {"opacity", layer.Opacity},
                              {"visible", layer.Visible},
                              {"locked", layer.Locked},
                              {"pixels", Json::binary(*layer.Pixels)}});
        return {{"width", Width}, {"height", Height}, {"srgb", Srgb}, {"layers", layers}};
    }

    PaintDocument PaintDocument::FromJson(const Json &json)
    {
        auto result = Blank(json.at("width"), json.at("height"));
        result.Srgb = json.value("srgb", true);
        result.Layers.clear();
        for (const auto &j : json.at("layers"))
        {
            Require(j.at("pixels").is_binary(), "图层数据不是二进制像素");
            const auto &binary = j.at("pixels").get_binary();
            PaintLayer layer;
            layer.Id = j.at("id");
            layer.Name = j.at("name");
            layer.Opacity = j.at("opacity");
            layer.Visible = j.at("visible");
            layer.Locked = j.at("locked");
            layer.Pixels = std::make_shared<Bytes>(binary.begin(), binary.end());
            result.Layers.push_back(std::move(layer));
        }
        result.Validate();
        return result;
    }

    Bytes PaintDocument::Composite(bool checker) const
    {
        Validate();
        Bytes result(static_cast<std::size_t>(Width) * Height * 4, 0);
        if (checker)
            for (int y = 0; y < Height; ++y)
                for (int x = 0; x < Width; ++x)
                {
                    auto at = (static_cast<std::size_t>(y) * Width + x) * 4;
                    auto value = ((x / 32 + y / 32) % 2) ? 72 : 112;
                    result[at] = result[at + 1] = result[at + 2] = static_cast<std::uint8_t>(value);
                    result[at + 3] = 255;
                }
        for (const auto &layer : Layers)
            if (layer.Visible && layer.Opacity > 0)
                for (std::size_t i = 0; i < result.size(); i += 4)
                {
                    const auto *p = layer.Pixels->data() + i;
                    Blend(result.data() + i, {p[2] / 255.f, p[1] / 255.f, p[0] / 255.f, p[3] / 255.f}, layer.Opacity,
                          false);
                }
        return result;
    }

    void PaintDocument::Stroke(std::size_t index, std::span<const Vector2> points, Vector4 color, float radius,
                               float strength, float hardness, bool erase)
    {
        Validate();
        auto &layer = Layers.at(index);
        Require(!layer.Locked, "当前图层已锁定");
        Require(radius > 0 && radius <= 8192 && std::isfinite(radius) && strength >= 0 && strength <= 1 &&
                    hardness >= 0 && hardness <= 1 && Finite({color.x, color.y, color.z}) && std::isfinite(color.w),
                "贴图笔刷参数无效");
        auto pixels = std::make_shared<Bytes>(*layer.Pixels);
        auto dab = [&](Vector2 p)
        {
            Require(std::isfinite(p.x) && std::isfinite(p.y), "笔刷坐标无效");
            int x0 = std::max(0, static_cast<int>(std::floor(p.x - radius))),
                y0 = std::max(0, static_cast<int>(std::floor(p.y - radius)));
            int x1 = std::min(Width - 1, static_cast<int>(std::ceil(p.x + radius))),
                y1 = std::min(Height - 1, static_cast<int>(std::ceil(p.y + radius)));
            for (int y = y0; y <= y1; ++y)
                for (int x = x0; x <= x1; ++x)
                {
                    float d = std::hypot(x + .5f - p.x, y + .5f - p.y) / radius;
                    if (d > 1)
                        continue;
                    float coverage = d <= hardness ? 1 : (1 - d) / std::max(.0001f, 1 - hardness);
                    Blend(pixels->data() + (static_cast<std::size_t>(y) * Width + x) * 4, color, coverage * strength,
                          erase);
                }
        };
        for (std::size_t i = 0; i < points.size(); ++i)
        {
            if (i == 0)
            {
                dab(points[i]);
                continue;
            }
            auto a = points[i - 1], b = points[i];
            int steps = std::max(
                1, static_cast<int>(std::ceil(std::hypot(b.x - a.x, b.y - a.y) / std::max(1.f, radius * .2f))));
            Require(steps <= 65536, "笔画长度超过限制");
            for (int step = 1; step <= steps; ++step)
            {
                float t = static_cast<float>(step) / steps;
                dab({a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t});
            }
        }
        layer.Pixels = std::move(pixels);
    }

    void PaintDocument::Fill(std::size_t index, Vector2 point, Vector4 color, float tolerance)
    {
        Validate();
        Require(tolerance >= 0 && tolerance <= 1, "填充容差无效");
        auto &layer = Layers.at(index);
        Require(!layer.Locked, "当前图层已锁定");
        int x = static_cast<int>(point.x), y = static_cast<int>(point.y);
        Require(x >= 0 && x < Width && y >= 0 && y < Height, "填充位置超出贴图");
        auto pixels = std::make_shared<Bytes>(*layer.Pixels);
        std::array<std::uint8_t, 4> target{};
        std::copy_n(pixels->data() + (static_cast<std::size_t>(y) * Width + x) * 4, 4, target.begin());
        std::vector<bool> seen(static_cast<std::size_t>(Width) * Height);
        std::vector<std::pair<int, int>> pending{{x, y}};
        seen[static_cast<std::size_t>(y) * Width + x] = true;
        for (std::size_t i = 0; i < pending.size(); ++i)
        {
            auto [px, py] = pending[i];
            auto at = (static_cast<std::size_t>(py) * Width + px) * 4;
            bool matches = true;
            for (int c = 0; c < 4; ++c)
                matches &= (c < 3 && (*layer.Pixels)[at + 3] == 0 && target[3] == 0) ||
                           std::abs(int((*layer.Pixels)[at + c]) - int(target[c])) <= tolerance * 255;
            if (!matches)
                continue;
            (*pixels)[at] = Byte(color.z);
            (*pixels)[at + 1] = Byte(color.y);
            (*pixels)[at + 2] = Byte(color.x);
            (*pixels)[at + 3] = Byte(color.w);
            for (auto [dx, dy] : {std::pair{-1, 0}, {1, 0}, {0, -1}, {0, 1}})
            {
                auto nx = px + dx, ny = py + dy;
                if (nx < 0 || ny < 0 || nx >= Width || ny >= Height)
                    continue;
                auto next = static_cast<std::size_t>(ny) * Width + nx;
                if (!seen[next])
                {
                    seen[next] = true;
                    pending.push_back({nx, ny});
                }
            }
        }
        layer.Pixels = std::move(pixels);
    }

    Vector4 PaintDocument::Sample(Vector2 point) const
    {
        int x = std::clamp(static_cast<int>(point.x), 0, Width - 1),
            y = std::clamp(static_cast<int>(point.y), 0, Height - 1);
        auto bytes = Composite();
        auto at = (static_cast<std::size_t>(y) * Width + x) * 4;
        return {bytes[at + 2] / 255.f, bytes[at + 1] / 255.f, bytes[at] / 255.f, bytes[at + 3] / 255.f};
    }

    void PaintDocument::Save(const fs::path &path) const
    {
        SavePng(path, Width, Height, Composite());
    }
}
