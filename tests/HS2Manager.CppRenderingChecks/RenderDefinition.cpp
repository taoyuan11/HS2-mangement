#include "FrozenDefinition.h"
#include "Pipeline/NativeFramePipeline.h"
#include "Character/CharacterAssembly.h"
#include "Runtime/CharacterRuntime.h"
#include <wincodec.h>
#include <fstream>
#include <iostream>
#include <chrono>

using namespace hs2;
using fixture::Json;

namespace
{
    void Png(const std::filesystem::path &file, int width, int height, std::span<const std::uint8_t> pixels)
    {
        ComPtr<IWICImagingFactory> factory;
        CheckHResult(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)),
                     "Create WIC factory");
        ComPtr<IWICStream> stream;
        CheckHResult(factory->CreateStream(&stream), "Create image stream");
        CheckHResult(stream->InitializeFromFilename(file.c_str(), GENERIC_WRITE), "Open PNG");
        ComPtr<IWICBitmapEncoder> encoder;
        CheckHResult(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder), "Create PNG encoder");
        CheckHResult(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache), "Initialize PNG");
        ComPtr<IWICBitmapFrameEncode> frame;
        CheckHResult(encoder->CreateNewFrame(&frame, nullptr), "Create PNG frame");
        CheckHResult(frame->Initialize(nullptr), "Initialize frame");
        CheckHResult(frame->SetSize(width, height), "PNG size");
        auto format = GUID_WICPixelFormat32bppBGRA;
        CheckHResult(frame->SetPixelFormat(&format), "PNG format");
        if (format != GUID_WICPixelFormat32bppBGRA)
            throw std::runtime_error("PNG encoder changed pixel format.");
        CheckHResult(
            frame->WritePixels(height, width * 4, static_cast<UINT>(pixels.size()), const_cast<BYTE *>(pixels.data())),
            "Write PNG");
        CheckHResult(frame->Commit(), "Commit frame");
        CheckHResult(encoder->Commit(), "Commit PNG");
    }

    std::string Path(const NativeSceneNode &node, const NativeSceneNode &root)
    {
        auto path = node.Name;
        for (auto *p = node.Parent(); p && p != &root; p = p->Parent())
            path = p->Name + "/" + path;
        return path;
    }

    std::string Utf8(const std::filesystem::path &path)
    {
        auto text = path.u8string();
        return {reinterpret_cast<const char *>(text.data()), text.size()};
    }

    Json Vec(Vector3 v)
    {
        return Json::array({v.x, v.y, v.z});
    }

    void State(const NativeCharacterScene &scene, NativeFramePipeline &pipeline, const std::filesystem::path &file)
    {
        Json renderers = Json::array();
        for (const auto &r : scene.Renderers)
        {
            const auto bounds = r->Bounds();
            Json materials = Json::array();
            for (const auto &m : pipeline.Appearance().Materials.at(r.get()))
            {
                if (!m)
                {
                    materials.push_back(nullptr);
                    continue;
                }
                Json textures = Json::object();
                for (const auto &[key, binding] : m->Textures)
                    textures[key] = {{"Bundle", Utf8(binding.Texture.Bundle)},
                                     {"FileId", binding.Texture.FileId},
                                     {"PathId", binding.Texture.PathId},
                                     {"Scale", {binding.Scale.x, binding.Scale.y}},
                                     {"Offset", {binding.Offset.x, binding.Offset.y}}};
                materials.push_back({{"Name", m->Name},
                                     {"Shader", m->Shader->Name},
                                     {"Source", Utf8(m->Shader->SourceBundle)},
                                     {"ShaderId", m->Shader->PathId},
                                     {"Queue", m->Queue()},
                                     {"Values", m->Values},
                                     {"Colors", m->ColorProperties},
                                     {"Keywords", m->Keywords},
                                     {"Textures", textures}});
            }
            renderers.push_back({{"Path", Path(*r->Node, *scene.Root)},
                                 {"Visible", r->Visible()},
                                 {"LastRenderedVisibility", r->LastRenderedVisibility.value_or(false)},
                                 {"Layer", r->Node->Layer},
                                 {"Casting", r->ShadowCasting},
                                 {"ReceiveShadows", r->ReceiveShadows},
                                 {"BoundsCenter", Vec(bounds.Center)},
                                 {"BoundsExtents", Vec(bounds.Extents)},
                                 {"World", MatrixValues(r->RenderWorldMatrix())},
                                 {"Materials", materials}});
        }
        std::ofstream(file)
            << Json{{"Renderers", renderers}, {"Diagnostics", pipeline.SourceReferenceDiagnostics()}}.dump(2);
    }

    void Pose(const NativeCharacterScene &scene, const std::filesystem::path &file)
    {
        Json bones = Json::array(), faces = Json::array();
        for (auto *node : scene.Root->Traverse())
        {
            const auto q = node->LocalRotation();
            bones.push_back({{"Path", Path(*node, *scene.Root)},
                             {"Position", Vec(node->LocalPosition())},
                             {"Rotation", {q.x, q.y, q.z, q.w}},
                             {"Scale", Vec(node->LocalScale())}});
        }
        for (const auto &renderer : scene.Renderers)
            if (!renderer->BlendWeights.empty())
                faces.push_back({{"Name", renderer->Node->Name}, {"BlendWeights", renderer->BlendWeights}});
        std::ofstream(file) << Json{{"Bones", bones}, {"Faces", faces}}.dump();
    }
}

int wmain(int argc, wchar_t **argv)
{
    try
    {
        if (argc < 6 || argc > 9)
        {
            std::cerr << "Usage: hs2_render_definition_checks game classdata.tpk definition.json fixture-directory "
                         "output-directory [view|all] [frames] [runtime-physics-directory]\n";
            return 2;
        }
        const int count = argc >= 8 ? std::stoi(argv[7]) : 48;
        if (count < 1 || count > 4096)
            throw std::invalid_argument("Frame count must be between 1 and 4096.");
        const bool simulate = argc == 9;
        CheckHResult(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "Initialize COM");

        struct Apartment
        {
            ~Apartment()
            {
                CoUninitialize();
            }
        } apartment;

        auto started = std::chrono::steady_clock::now();
        auto definition = fixture::Definition(argv[3]);
        std::cout << "Frozen definition=" << definition->Name << " parts=" << definition->Parts.size() << std::endl;
        auto database = std::make_shared<UnityClassDatabase>(argv[2]);
        auto assets = std::make_shared<NativeGpuAssetStore>(definition->BundlePaths, argv[1], database);
        auto scene = AssembleCharacter(definition, *assets);
        std::cout << "Assembly renderers=" << scene->Renderers.size() << " nodes=" << scene->Nodes.size() << std::endl;
        std::filesystem::path fixturePath = argv[4], output = argv[5];
        std::filesystem::create_directories(output);
        std::ifstream fixtureInput(fixturePath / "fixture.json");
        auto captures = Json::parse(fixtureInput);
        std::string selected = argc >= 7 ? std::filesystem::path(argv[6]).string() : std::string{};
        int views = 0;
        NativeRenderCamera warmup;
        warmup.Position = {0, 15.9255455f, 5.721683f};
        warmup.Target = {0, 15.9255455f, -.2183166f};
        warmup.FieldOfView = 35;
        warmup.Near = definition->Environment.Camera.Float("near clip plane", .08f);
        warmup.Far = definition->Environment.Camera.Float("far clip plane", 1000);
        std::unique_ptr<NativeCharacterRuntime> runtime;
        if (simulate)
        {
            for (const auto &[name, value] :
                 std::initializer_list<std::pair<const char *, int>>{{"Seed", 1979},
                                                                     {"Rate", 30},
                                                                     {"InitialFrames", 180},
                                                                     {"TemporalPhaseVersion", 2},
                                                                     {"PhysicsResetVersion", 3}})
                if (captures.at(name) != value)
                    throw std::runtime_error(std::string("Unsupported fixture setting: ") + name);
            runtime = std::make_unique<NativeCharacterRuntime>(scene, assets, argv[8]);
            runtime->Update(0);
            PoseCharacterFace(*scene, 1);
            for (int i = 0; i < 4; ++i)
            {
                runtime->AdvanceFrame(1.f / 30, 0);
                PoseCharacterFace(*scene, 1);
                NativeFramePipeline::CompleteVisibility(*scene, warmup, 1280, 720);
            }
            runtime->ResetFixturePhysics();
            for (int i = 0; i < 180; ++i)
            {
                runtime->AdvanceFrame(1.f / 30, 0);
                PoseCharacterFace(*scene, 1);
                NativeFramePipeline::CompleteVisibility(*scene, warmup, 1280, 720);
            }
            std::cout << "Runtime warmup 184 steps complete\n" << std::flush;
        }
        else
            fixture::Pose(*scene, fixturePath / "front-pose.json");
        GraphicsDevice graphics;
        NativeFramePipeline pipeline(graphics, *scene, *assets);
        std::cout << "GPU pipeline initialized\n" << std::flush;
        pipeline.Draw(warmup, 1280, 720, 0);
        for (const auto &view : captures.at("Views"))
        {
            const auto name = view.at("Name").get<std::string>();
            const bool capture = selected.empty() || selected == "all" || name == selected;
            if (!capture && !simulate)
                continue;
            if (capture)
                ++views;
            if (!simulate)
                fixture::Pose(*scene, fixturePath / (name + "-pose.json"));
            pipeline.ResetHistory();
            NativeRenderCamera camera;
            camera.Position = fixture::Vector3(view.at("Position"));
            camera.Target = fixture::Vector3(view.at("Target"));
            camera.FieldOfView = view.at("Fov");
            camera.Near = definition->Environment.Camera.Float("near clip plane", .08f);
            camera.Far = definition->Environment.Camera.Float("far clip plane", 1000);
            for (int i = 0; i < count; ++i)
            {
                if (runtime)
                {
                    runtime->AdvanceFrame(1.f / 30, 0);
                    PoseCharacterFace(*scene, 1);
                }
                const auto &frame = pipeline.Draw(camera, 1280, 720, 0);
                if (capture && i + 1 == count)
                {
                    auto pixels = graphics.Readback(frame.Texture.Get());
                    for (size_t alpha = 3; alpha < pixels.size(); alpha += 4)
                        pixels[alpha] = 255;
                    Png(output / (name + ".png"), 1280, 720, pixels);
                    State(*scene, pipeline, output / (name + "-state.json"));
                    Pose(*scene, output / (name + "-pose.json"));
                }
                if ((i + 1) % 12 == 0 || i == 0)
                    std::cout << name << " frame " << i + 1 << "/" << count << std::endl;
            }
            std::vector<NativeSceneRenderer *> enabled;
            for (const auto &renderer : scene->Renderers)
                if (renderer->Enabled)
                {
                    enabled.push_back(renderer.get());
                    renderer->Enabled = false;
                }
            for (int i = 0; i < count; ++i)
            {
                if (runtime)
                {
                    runtime->AdvanceFrame(1.f / 30, 0);
                    PoseCharacterFace(*scene, 1);
                }
                pipeline.Draw(camera, 1280, 720, 0);
            }
            if (capture)
            {
                auto background = graphics.Readback(pipeline.LastFrame().Texture.Get());
                for (size_t alpha = 3; alpha < background.size(); alpha += 4)
                    background[alpha] = 255;
                Png(output / (name + "-background.png"), 1280, 720, background);
            }
            for (auto *renderer : enabled)
                renderer->Enabled = true;
        }
        if (views == 0)
            throw std::runtime_error("Requested fixture view was not found.");
        std::ofstream(output / "scope.json")
            << Json{{"Scope", simulate ? "C++ full render pipeline and runtime with frozen definition"
                                       : "C++ full render pipeline with frozen definition and reference pose"},
                    {"ProductionPreparationValidated", false},
                    {"RuntimeSimulationExercised", simulate},
                    {"RuntimeSimulationValidated", false},
                    {"Renderers", scene->Renderers.size()},
                    {"Views", views},
                    {"FramesPerView", count},
                    {"Seconds", std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count()}}
                   .dump(2);
        std::cout << "PASS: full real-character render completed\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
