#include "VisualReview.h"
#include "Agent.h"
#include "Images/ImageFiles.h"
#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <set>

namespace hs2::agent
{
    namespace
    {
        using Microsoft::WRL::ComPtr;

        void CheckImage(HRESULT result)
        {
            if (FAILED(result))
                throw std::runtime_error("参考图片处理失败");
        }

        struct Picture
        {
            ComPtr<IWICImagingFactory> Factory;
            ComPtr<IWICBitmapDecoder> Decoder;
            ComPtr<IWICBitmapFrameDecode> Frame;
            UINT Width = 0, Height = 0;

            explicit Picture(const fs::path &file)
            {
                ValidateImage(file);
                CheckImage(
                    CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&Factory)));
                CheckImage(Factory->CreateDecoderFromFilename(file.c_str(), nullptr, GENERIC_READ,
                                                              WICDecodeMetadataCacheOnLoad, &Decoder));
                CheckImage(Decoder->GetFrame(0, &Frame));
                CheckImage(Frame->GetSize(&Width, &Height));
            }

            Bytes Pixels(WICRect rect, int width, int height)
            {
                ComPtr<IWICBitmapClipper> clip;
                ComPtr<IWICBitmapScaler> scale;
                ComPtr<IWICFormatConverter> convert;
                CheckImage(Factory->CreateBitmapClipper(&clip));
                CheckImage(clip->Initialize(Frame.Get(), &rect));
                CheckImage(Factory->CreateBitmapScaler(&scale));
                CheckImage(scale->Initialize(clip.Get(), width, height, WICBitmapInterpolationModeFant));
                CheckImage(Factory->CreateFormatConverter(&convert));
                CheckImage(convert->Initialize(scale.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone,
                                               nullptr, 0, WICBitmapPaletteTypeCustom));
                Bytes pixels(static_cast<size_t>(width) * height * 4);
                CheckImage(convert->CopyPixels(nullptr, width * 4, static_cast<UINT>(pixels.size()), pixels.data()));
                return pixels;
            }
        };

        void Fit(Picture &picture, Bytes &canvas, int canvasWidth, int panelX, int panelY, int width, int height)
        {
            double factor =
                (std::min)(static_cast<double>(width) / picture.Width, static_cast<double>(height) / picture.Height);
            int w = (std::max)(1, static_cast<int>(picture.Width * factor));
            int h = (std::max)(1, static_cast<int>(picture.Height * factor));
            auto pixels =
                picture.Pixels({0, 0, static_cast<int>(picture.Width), static_cast<int>(picture.Height)}, w, h);
            int x = panelX + (width - w) / 2, y = panelY + (height - h) / 2;
            for (int row = 0; row < h; ++row)
                for (int col = 0; col < w; ++col)
                {
                    auto source = static_cast<size_t>(row * w + col) * 4;
                    auto target = static_cast<size_t>((row + y) * canvasWidth + col + x) * 4;
                    for (int channel = 0; channel < 3; ++channel)
                        canvas[target + channel] = static_cast<std::uint8_t>(
                            (pixels[source + channel] * pixels[source + 3] + 224 * (255 - pixels[source + 3])) / 255);
                }
        }
    }

    Json ReferenceRegions(const fs::path &reference, const Json &regions, const fs::path &output)
    {
        if (!regions.is_array() || regions.empty() || regions.size() > 6)
            throw std::invalid_argument("regions 需要 1 到 6 个可见视图");
        Picture picture(reference);
        Json result = Json::array();
        std::set<std::string> views;
        // 先校验整个批次，避免错误的后续区域留下半批结果。
        for (const auto &region : regions)
        {
            auto view = region.at("view").get<std::string>();
            if (!ReviewView(view) || !views.insert(view).second)
                throw std::invalid_argument("参考视图无效或重复");
            const auto &rect = region.at("rect");
            if (!rect.is_array() || rect.size() != 4)
                throw std::invalid_argument("rect 使用归一化 [x,y,width,height]");
            for (const auto &value : rect)
                if (!value.is_number() || !std::isfinite(value.get<double>()))
                    throw std::invalid_argument("参考区域必须为有限数值");
            double x = rect[0], y = rect[1], w = rect[2], h = rect[3];
            if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > 1.0000001 || y + h > 1.0000001)
                throw std::invalid_argument("参考区域超出原图");
            int left = static_cast<int>(std::floor(x * picture.Width));
            int top = static_cast<int>(std::floor(y * picture.Height));
            int right =
                (std::min)(static_cast<int>(picture.Width), static_cast<int>(std::ceil((x + w) * picture.Width)));
            int bottom =
                (std::min)(static_cast<int>(picture.Height), static_cast<int>(std::ceil((y + h) * picture.Height)));
            if (right - left < 2 || bottom - top < 2)
                throw std::invalid_argument("参考区域至少包含 2×2 原始像素");
            result.push_back(
                {{"view", view}, {"rect", rect}, {"source_pixels", {left, top, right - left, bottom - top}}});
        }
        fs::create_directories(output);
        for (auto &region : result)
        {
            auto r = region["source_pixels"].get<std::vector<int>>();
            double scale = (std::min)(1.0, 2048.0 / (std::max)(r[2], r[3]));
            int width = (std::max)(1, static_cast<int>(r[2] * scale));
            int height = (std::max)(1, static_cast<int>(r[3] * scale));
            auto file = output / Path(region["view"].get<std::string>() + ".png");
            SavePng(file, width, height, picture.Pixels({r[0], r[1], r[2], r[3]}, width, height));
            region["path"] = Utf8(file);
            region["mime_type"] = "image/png";
            region["detail_limited"] = r[2] < 128 || r[3] < 128;
        }
        return result;
    }

    Json ComparisonImages(const Json &references, const Json &images, const fs::path &output)
    {
        Json result = Json::array();
        for (const auto &image : images)
            for (const auto &reference : references)
                if (reference.at("view") == image.at("view"))
                {
                    Picture left(Path(reference.at("path"))), right(Path(image.at("path")));
                    int width = 640, height = 960;
                    Bytes pixels(static_cast<size_t>(width) * height * 8, 224);
                    for (size_t i = 3; i < pixels.size(); i += 4)
                        pixels[i] = 255;
                    Fit(left, pixels, width * 2, 0, 0, width, height);
                    Fit(right, pixels, width * 2, width, 0, width, height);
                    fs::create_directories(output);
                    auto file = output / Path(image.at("view").get<std::string>() + "-comparison.png");
                    SavePng(file, width * 2, height, pixels);
                    result.push_back({{"path", Utf8(file)},
                                      {"mime_type", "image/png"},
                                      {"view", image.at("view")},
                                      {"layout", "left_reference_right_render"},
                                      {"detail_limited", reference.value("detail_limited", false)}});
                }
        return result;
    }

    Json ProbeImages(const Json &probes, const fs::path &output)
    {
        Json images = Json::array();
        fs::create_directories(output);
        for (size_t index = 0; index + 2 < probes.size(); index += 3)
        {
            const auto &first = probes.at(index);
            int columns = static_cast<int>(first.at("render").at("images").size());
            int width = 384 * columns, height = 512 * 3;
            Bytes pixels(static_cast<size_t>(width) * height * 4, 224);
            for (size_t i = 3; i < pixels.size(); i += 4)
                pixels[i] = 255;
            Json rows = Json::array(), views = Json::array();
            for (int row = 0; row < 3; ++row)
            {
                const auto &probe = probes.at(index + row);
                rows.push_back({{"sign", probe.at("sign")}, {"value", probe.at("value")}});
                for (int column = 0; column < columns; ++column)
                {
                    const auto &image = probe.at("render").at("images").at(column);
                    Picture picture(Path(image.at("path")));
                    Fit(picture, pixels, width, column * 384, row * 512, 384, 512);
                    if (row == 0)
                        views.push_back(image.at("view"));
                }
            }
            auto file = output / Path("probe-" + std::to_string(index / 3) + ".png");
            SavePng(file, width, height, pixels);
            images.push_back({{"path", Utf8(file)},
                              {"mime_type", "image/png"},
                              {"field", first.at("path")},
                              {"rows", rows},
                              {"columns", views},
                              {"layout", "rows_negative_current_positive"}});
        }
        return images;
    }
}
