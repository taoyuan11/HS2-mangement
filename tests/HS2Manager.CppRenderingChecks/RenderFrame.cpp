#include "Pipeline/NativeFramePipeline.h"
#include "Character/CharacterPreparation.h"
#include "Runtime/CharacterRuntime.h"
#include "Preview/TemporaryPreviewSession.h"
#include <wincodec.h>
#include <fstream>
#include <iostream>
#include <set>
#include <cmath>
#include <algorithm>
#include <chrono>

using namespace hs2;

namespace
{
    void Png(const std::filesystem::path &file, int width, int height, std::span<const std::uint8_t> pixels)
    {
        ComPtr<IWICImagingFactory> factory;
        CheckHResult(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)),
                     "Create image factory");
        ComPtr<IWICStream> stream;
        CheckHResult(factory->CreateStream(&stream), "Create PNG stream");
        CheckHResult(stream->InitializeFromFilename(file.c_str(), GENERIC_WRITE), "Open PNG output");
        ComPtr<IWICBitmapEncoder> encoder;
        CheckHResult(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder), "Create PNG encoder");
        CheckHResult(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache), "Initialize PNG encoder");
        ComPtr<IWICBitmapFrameEncode> frame;
        CheckHResult(encoder->CreateNewFrame(&frame, nullptr), "Create PNG frame");
        CheckHResult(frame->Initialize(nullptr), "Initialize PNG frame");
        CheckHResult(frame->SetSize(width, height), "Set PNG dimensions");
        auto format = GUID_WICPixelFormat32bppBGRA;
        CheckHResult(frame->SetPixelFormat(&format), "Set PNG pixel format");
        if (format != GUID_WICPixelFormat32bppBGRA)
            throw std::runtime_error("PNG encoder changed pixel format.");
        CheckHResult(
            frame->WritePixels(height, width * 4, static_cast<UINT>(pixels.size()), const_cast<BYTE *>(pixels.data())),
            "Write PNG pixels");
        CheckHResult(frame->Commit(), "Commit PNG frame");
        CheckHResult(encoder->Commit(), "Commit PNG");
    }
}

int wmain(int argc, wchar_t **argv)
{
    try
    {
        fs::path decoder;
        if (argc >= 8 && std::wstring_view(argv[argc - 2]) == L"--temporary")
        {
            decoder = argv[argc - 1];
            argc -= 2;
        }
        if (argc < 6 || argc > 8)
        {
            std::cerr << "Usage: hs2_render_frame_checks game data classdata.tpk card.png output-directory "
                         "[physics-directory [comparison-card.png]] [--temporary 7z.dll]\n";
            return 2;
        }
        CheckHResult(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "Initialize COM");

        struct Apartment
        {
            ~Apartment()
            {
                CoUninitialize();
            }
        } apartment;

        std::shared_ptr<TemporaryPreviewSession> session;
        PreviewResources resources;
        if (!decoder.empty())
        {
            std::optional<ModIndex> previous;
            {
                CardRepository repository(argv[2]);
                if (auto saved = repository.LoadMetadata("mods-v1"))
                    previous = saved->get<ModIndex>();
            }
            session = TemporaryPreviewSession::Open(argv[4], argv[1], argv[2], decoder, previous, {},
                                                    [](auto &p)
                                                    {
                                                        std::cout << p.Message << " " << p.Completed << "/" << p.Total
                                                                  << std::endl;
                                                    });
            resources = session->Cards.front().Resources;
            std::cout << "temporary cards=" << session->Cards.size() << " mods=" << resources->Index.Files.size()
                      << std::endl;
        }
        auto card = session ? session->Cards.front().Card : CardParser().Parse(argv[4]);
        if (card.Status != CardStatus::Valid)
            throw std::runtime_error(card.Error);
        auto started = std::chrono::steady_clock::now();
        auto elapsed = [&]
        {
            return std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
        };
        NativeCharacterService service(argv[1], argv[2], argv[3], resources);
        auto original = card;
        for (int run = 0; run < (argc == 8 ? 3 : 1); ++run)
        {
            if (run > 0)
            {
                card = run == 1 ? CardParser().Parse(argv[7]) : original;
                if (card.Status != CardStatus::Valid)
                    throw std::runtime_error(card.Error);
                started = std::chrono::steady_clock::now();
            }
            auto prepared = service.Prepare(card, nullptr, {},
                                            [](const auto &p)
                                            {
                                                std::cout << p.Message << " " << p.Completed << "/" << p.Total
                                                          << std::endl;
                                            });
            auto prepareSeconds = elapsed();
            std::cout << "prepared parts=" << prepared.Scene->Parts.size()
                      << " renderers=" << prepared.Scene->Renderers.size()
                      << " selections=" << prepared.Scene->Definition->Selections.size() << std::endl;
            GraphicsDevice graphics;
            NativeCharacterRuntime runtime(prepared.Scene, prepared.Assets,
                                           argc >= 7 ? std::filesystem::path(argv[6]) : std::filesystem::path{});
            auto runtimeSeconds = elapsed() - prepareSeconds;
            NativeFramePipeline pipeline(graphics, *prepared.Scene, *prepared.Assets);
            auto pipelineSeconds = elapsed() - prepareSeconds - runtimeSeconds;
            runtime.Update(0);
            Vector3 lo{INFINITY, INFINITY, INFINITY}, hi{-INFINITY, -INFINITY, -INFINITY};
            for (const auto &renderer : prepared.Scene->Renderers)
                if (renderer->Visible() && renderer->HasGeometry())
                {
                    auto bounds = renderer->Bounds();
                    lo.x = std::min(lo.x, bounds.Center.x - bounds.Extents.x);
                    lo.y = std::min(lo.y, bounds.Center.y - bounds.Extents.y);
                    lo.z = std::min(lo.z, bounds.Center.z - bounds.Extents.z);
                    hi.x = std::max(hi.x, bounds.Center.x + bounds.Extents.x);
                    hi.y = std::max(hi.y, bounds.Center.y + bounds.Extents.y);
                    hi.z = std::max(hi.z, bounds.Center.z + bounds.Extents.z);
                }
            NativeRenderCamera camera;
            camera.Target = {(lo.x + hi.x) * .5f, (lo.y + hi.y) * .5f, (lo.z + hi.z) * .5f};
            camera.FieldOfView = 35;
            float distance =
                std::max(hi.y - lo.y, (hi.x - lo.x) * 640 / 512) / (2 * std::tan(35 * DirectX::XM_PI / 360)) * 1.2f +
                (hi.z - lo.z) * .5f;
            camera.Position = {camera.Target.x, camera.Target.y, camera.Target.z + distance};
            camera.Near = .08f;
            camera.Far = 1000;
            std::filesystem::path output = argv[5];
            if (argc == 8)
                output /= "sequence-" + std::to_string(run);
            std::filesystem::create_directories(output);
            std::set<std::uint32_t> unique;
            for (int i = 0; i < 8; ++i)
            {
                float time = i / 30.f;
                runtime.Update(time);
                const auto &frame = pipeline.Draw(camera, 512, 640, time);
                auto pixels = graphics.Readback(frame.Texture.Get());
                if (i == 0)
                {
                    auto stats = prepared.Assets->ReaderStats();
                    auto blocks = prepared.Assets->BlockStats();
                    WriteJson(output / "loading-performance.json", {{"PrepareSeconds", prepareSeconds},
                                                                    {"RuntimeSeconds", runtimeSeconds},
                                                                    {"PipelineSeconds", pipelineSeconds},
                                                                    {"FirstFrameSeconds", elapsed()},
                                                                    {"ReaderLoads", stats.Loads},
                                                                    {"ReaderHits", stats.Hits},
                                                                    {"ReaderEvictions", stats.Evictions},
                                                                    {"ReaderLoadSeconds", stats.LoadSeconds},
                                                                    {"ReaderResources", stats.Resources},
                                                                    {"BlockMemoryHits", blocks.MemoryHits},
                                                                    {"BlockDiskHits", blocks.DiskHits},
                                                                    {"BlockDecodes", blocks.Decodes},
                                                                    {"BlockResidentBytes", blocks.ResidentBytes}});
                }
                if (pixels.size() != 512 * 640 * 4)
                    throw std::runtime_error("Unexpected output frame size.");
                if (i == 0 || i == 7)
                {
                    for (size_t p = 0; p < pixels.size(); p += 4)
                    {
                        pixels[p + 3] = 255;
                        unique.insert((std::uint32_t(pixels[p]) << 16) | (std::uint32_t(pixels[p + 1]) << 8) |
                                      pixels[p + 2]);
                    }
                    Png(output / (i == 0 ? L"frame-000.png" : L"frame-007.png"), 512, 640, pixels);
                }
                std::cout << "frame " << i << " complete\n";
            }
            if (unique.size() < 128)
                throw std::runtime_error("Rendered frame has insufficient colour variation.");
            pipeline.SetInteractive(true);
            pipeline.Draw(camera, 321, 385, .3f);
            pipeline.SetInteractive(false);
            pipeline.ResetHistory();
            pipeline.Draw(camera, 512, 640, .3f);
            std::cout << "PASS: real card, HDR/deferred/forward frame pipeline, frame histories, resize, interaction "
                         "reset; colours="
                      << unique.size() << "\n";
        }
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
