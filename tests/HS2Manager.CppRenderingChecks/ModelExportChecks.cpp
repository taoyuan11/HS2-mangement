#include "Model/ModelExportInternal.h"
#include "Baking/NativePbrBake.h"
#include "Images/ImageFiles.h"
#include <wincodec.h>
#include <cmath>
#include <cstring>
#include <iostream>
using namespace hs2;

namespace
{
    int checks = 0;

    void Check(bool value, const char *message)
    {
        if (!value)
            throw std::runtime_error(message);
        ++checks;
    }

    template <class F> void Reject(F action, const char *message)
    {
        bool failed = false;
        try
        {
            action();
        }
        catch (const std::exception &)
        {
            failed = true;
        }
        Check(failed, message);
    }

    Bytes DecodePng(const fs::path &path, UINT width, UINT height)
    {
        ComPtr<IWICImagingFactory> factory;
        CheckHResult(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)),
                     "Test PNG factory");
        ComPtr<IWICBitmapDecoder> decoder;
        CheckHResult(factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ,
                                                        WICDecodeMetadataCacheOnLoad, &decoder),
                     "Test PNG decoder");
        ComPtr<IWICBitmapFrameDecode> frame;
        CheckHResult(decoder->GetFrame(0, &frame), "Test PNG frame");
        UINT w = 0, h = 0;
        CheckHResult(frame->GetSize(&w, &h), "Test PNG size");
        Check(w == width && h == height, "PNG dimensions changed");
        ComPtr<IWICFormatConverter> converter;
        CheckHResult(factory->CreateFormatConverter(&converter), "Test PNG converter");
        CheckHResult(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr,
                                           0, WICBitmapPaletteTypeCustom),
                     "Test PNG conversion");
        Bytes pixels(static_cast<std::size_t>(w) * h * 4);
        CheckHResult(converter->CopyPixels(nullptr, w * 4, static_cast<UINT>(pixels.size()), pixels.data()),
                     "Test PNG readback");
        return pixels;
    }

    void Images(const fs::path &root)
    {
        Bytes pixels{0, 0, 255, 255, 0, 255, 0, 128, 255, 0, 0, 64, 255, 255, 255, 0};
        SavePng(root / "checker.png", 2, 2, pixels);
        Check(DecodePng(root / "checker.png", 2, 2) == pixels, "PNG BGRA or alpha roundtrip changed");
        std::vector<float> hdr(8 * 2 * 4);
        for (std::size_t i = 0; i < hdr.size(); i += 4)
        {
            hdr[i] = 2;
            hdr[i + 1] = 1;
            hdr[i + 2] = .5f;
            hdr[i + 3] = 1;
        }
        WriteRadiance(root / "hdr.hdr", 8, 2, hdr);
        auto radiance = ReadFile(root / "hdr.hdr");
        std::string header(radiance.begin(), radiance.end());
        auto start = header.find("-Y 2 +X 8\n");
        Check(start != std::string::npos, "Radiance orientation/dimensions");
        auto offset = start + std::strlen("-Y 2 +X 8\n");
        Check(radiance.size() == offset + 2 * (4 + 4 * 9), "Radiance scanline sizes");
        Check(radiance[offset] == 2 && radiance[offset + 2] == 0 && radiance[offset + 3] == 8 &&
                  radiance[offset + 4] == 8,
              "Radiance RLE row header");
        Check(radiance[offset + 5] == 128 && radiance[offset + 14] == 64 && radiance[offset + 23] == 32 &&
                  radiance[offset + 32] == 130,
              "Radiance independent RGBE values");
        WriteOpenExr(root / "hdr.exr", 8, 2, hdr);
        auto exr = ReadFile(root / "hdr.exr");
        std::uint32_t magic = 0;
        std::memcpy(&magic, exr.data(), 4);
        Check(magic == 20000630, "EXR file signature");
        std::size_t p = 8;
        while (exr.at(p))
        {
            while (exr.at(p++))
            {
            }
            while (exr.at(p++))
            {
            }
            std::uint32_t count = 0;
            std::memcpy(&count, exr.data() + p, 4);
            p += 4 + count;
        }
        ++p;
        std::uint64_t line = 0;
        std::memcpy(&line, exr.data() + p, 8);
        Check(line == p + 16, "EXR scanline offset table");
        std::uint16_t alpha = 0, blue = 0, green = 0, red = 0;
        std::memcpy(&alpha, exr.data() + line + 8, 2);
        std::memcpy(&blue, exr.data() + line + 8 + 16, 2);
        std::memcpy(&green, exr.data() + line + 8 + 32, 2);
        std::memcpy(&red, exr.data() + line + 8 + 48, 2);
        Check(alpha == 0x3c00 && blue == 0x3800 && green == 0x3c00 && red == 0x4000, "EXR HALF channels and values");
        hdr[0] = NAN;
        Reject(
            [&]
            {
                WriteRadiance(root / "invalid.hdr", 8, 2, hdr);
            },
            "Nonfinite HDR accepted");
    }

    void Pbr(const fs::path &root)
    {
        GraphicsDevice graphics(true);
        NativeGpuAssetStore assets({}, fs::path{}, {});
        CharacterDefinition definition;
        GpuMaterialComposer composer(graphics, assets, definition);
        auto shader = std::make_shared<NativeShaderAsset>();
        shader->Name = "Standard";
        GpuMaterialData source;
        source.Name = "Known constant";
        GpuMaterialInstance material(source, shader);
        material.Set("_Color", {.25f, .5f, 1, 1});
        material.Set("_Metallic", .25f);
        material.Set("_Glossiness", .75f);
        NativePbrMaterialExporter exporter(graphics, composer, root);
        auto record = exporter.Get(material, "clothes", "test", "slot");
        Check(record.at("Kind") == "Surface" && record.at("AlphaMode") == "Opaque", "Standard material mode");
        auto base = DecodePng(root / Path(record.at("BaseColor").get<std::string>()), 1, 1);
        Check(base[0] == 255 && std::abs(static_cast<int>(base[1]) - 188) <= 1 &&
                  std::abs(static_cast<int>(base[2]) - 137) <= 1 && base[3] == 255,
              "PBR linear base color to sRGB");
        auto normal = DecodePng(root / Path(record.at("Normal").get<std::string>()), 1, 1);
        Check(normal[0] == 255 && std::abs(static_cast<int>(normal[1]) - 128) <= 1 &&
                  std::abs(static_cast<int>(normal[2]) - 128) <= 1,
              "PBR default OpenGL normal");
        for (auto key : {"Metallic", "Roughness"})
        {
            auto data = DecodePng(root / Path(record.at(key).get<std::string>()), 1, 1);
            Check(std::abs(static_cast<int>(data[0]) - 64) <= 1 && data[0] == data[1] && data[1] == data[2],
                  "PBR scalar map erroneously gamma encoded");
        }
        auto ao = DecodePng(root / Path(record.at("Occlusion").get<std::string>()), 1, 1);
        Check(ao[0] == 255 && ao[1] == 255 && ao[2] == 255, "PBR default occlusion");
        Check(exporter.Get(material, "clothes", "test", "slot").at("Id") == record.at("Id") &&
                  exporter.Records().size() == 1,
              "PBR stable slot cache");
        exporter.Save();
        auto manifest = Json::parse(ReadFile(root / "materials.json"));
        Check(manifest.at("SchemaVersion") == 3 && manifest.at("Materials").size() == 1, "PBR material contract");
        std::stop_source cancel;
        cancel.request_stop();
        Reject(
            [&]
            {
                exporter.Get(material, "clothes", "test", "cancel", cancel.get_token());
            },
            "PBR cancellation ignored");
    }

    void Options()
    {
        ModelExportView view;
        ModelExportService::ValidateView(view);
        view.Width = 0;
        Reject(
            [&]
            {
                ModelExportService::ValidateView(view);
            },
            "Zero view width accepted");
        view.Width = 64;
        view.HasCamera = true;
        Reject(
            [&]
            {
                ModelExportService::ValidateView(view);
            },
            "Coincident camera accepted");
        view.Position = {0, 1, 0};
        Reject(
            [&]
            {
                ModelExportService::ValidateView(view);
            },
            "Parallel camera up accepted");
        view.Position = {0, 0, 1};
        ModelExportService::ValidateView(view);
        Check(Json(view).at("Position") == Json({0, 0, 1}), "Camera JSON coordinate schema");
        ModelExportReport report;
        report.Requested = 2;
        report.Items.push_back({{}, {}, ModelExportStatus::Complete, {}});
        Check(report.Summary().find("未处理 1") != std::string::npos, "Model summary unprocessed count");
    }
}

int wmain(int argc, wchar_t **argv)
{
    fs::path root;
    auto com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    try
    {
        if (argc >= 7 && std::wstring(argv[1]) == L"--real")
        {
            if (SUCCEEDED(com))
            {
                CoUninitialize();
                com = E_FAIL;
            }
            auto runtime = fs::path(argv[2]), game = fs::path(argv[3]), data = fs::path(argv[4]),
                 card = fs::path(argv[5]), output = fs::path(argv[6]);
            ModelExportOptions options;
            options.View.Width = 256;
            options.View.Height = 256;
            if (argc > 7 && std::wstring(argv[7]) == L"Blender")
                options.Target = ModelExportTarget::Blender;
            if (argc > 7 && std::wstring(argv[7]) == L"Unreal")
                options.Target = ModelExportTarget::Unreal;
            auto report =
                ModelExportService(runtime).Export({{CardParser().Parse(card), {}, 0}}, game, data, output, options,
                                                   [](const WorkProgress &p)
                                                   {
                                                       std::cout << p.Message << std::endl;
                                                   });
            for (auto &item : report.Items)
                std::cout << item.Message << "\n" << item.Directory << "\n";
            Check(report.Items.size() == 1 && (report.Items[0].Status == ModelExportStatus::Complete ||
                                               report.Items[0].Status == ModelExportStatus::Incomplete),
                  "Real model export failed");
            std::cout << report.Summary() << std::endl;
        }
        else
        {
            wchar_t temporary[MAX_PATH];
            GetTempPathW(MAX_PATH, temporary);
            root = fs::path(temporary) / Path("HS2Manager-ModelExportChecks-" + NewId());
            fs::create_directory(root);
            Images(root);
            Pbr(root);
            Options();
            fs::remove_all(root);
        }
        if (SUCCEEDED(com))
            CoUninitialize();
        std::cout << "PASS " << checks << " native model export checks\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << "FAIL " << checks << ": " << error.what() << "\n" << Utf8(root) << "\n";
        if (!root.empty())
        {
            std::error_code ignored;
            fs::remove_all(root, ignored);
        }
        if (SUCCEEDED(com))
            CoUninitialize();
        return 1;
    }
}
