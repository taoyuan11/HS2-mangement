#include "Preview/NativePreviewWorker.h"
#include "Runtime/CharacterRuntime.h"
#include <algorithm>
#include <chrono>
#include <limits>
#include <wincodec.h>

namespace hs2::ui
{
    namespace
    {
        NativeRendererBounds Bounds(NativeCharacterScene const &scene, bool face)
        {
            Vector3 low{FLT_MAX, FLT_MAX, FLT_MAX}, high{-FLT_MAX, -FLT_MAX, -FLT_MAX};
            bool found = false;
            for (auto const &renderer : scene.Renderers)
            {
                if (face ? renderer->Node->Name != "o_head" || !renderer->Node->ActiveInHierarchy()
                         : !renderer->Visible())
                    continue;
                auto b = renderer->Bounds();
                if (!std::isfinite(b.Center.x) || !std::isfinite(b.Center.y) || !std::isfinite(b.Center.z) ||
                    !std::isfinite(b.Extents.x) || !std::isfinite(b.Extents.y) || !std::isfinite(b.Extents.z))
                    continue;
                low = {std::min(low.x, b.Center.x - b.Extents.x), std::min(low.y, b.Center.y - b.Extents.y),
                       std::min(low.z, b.Center.z - b.Extents.z)};
                high = {std::max(high.x, b.Center.x + b.Extents.x), std::max(high.y, b.Center.y + b.Extents.y),
                        std::max(high.z, b.Center.z + b.Extents.z)};
                found = true;
                if (face)
                    break;
            }
            if (!found)
            {
                if (face)
                    return Bounds(scene, false);
                throw std::runtime_error("人物没有可显示的几何结构。");
            }
            return {{(low.x + high.x) / 2, (low.y + high.y) / 2, (low.z + high.z) / 2},
                    {(high.x - low.x) / 2, (high.y - low.y) / 2, (high.z - low.z) / 2}};
        }

        void WritePng(ScreenshotTarget const &destination, std::span<const uint8_t> pixels, int width, int height)
        {
            auto const &path = destination.Path;
            if (width <= 0 || height <= 0 || pixels.size() != static_cast<size_t>(width) * height * 4)
                throw std::runtime_error("无效截图尺寸");
            if (fs::exists(path) && (!destination.ExpectedHash || HashFile(path) != *destination.ExpectedHash))
                throw std::runtime_error("截图目标已被修改，请重新选择文件名。");
            fs::create_directories(path.parent_path());
            auto temporary = fs::path(path.wstring() + L"." + Path(NewId()).wstring() + L".tmp");
            try
            {
                ComPtr<IWICImagingFactory> factory;
                CheckHResult(
                    CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)),
                    "WIC factory");
                ComPtr<IWICStream> stream;
                CheckHResult(factory->CreateStream(&stream), "WIC stream");
                CheckHResult(stream->InitializeFromFilename(temporary.c_str(), GENERIC_WRITE), "WIC output");
                ComPtr<IWICBitmapEncoder> encoder;
                CheckHResult(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder), "PNG encoder");
                CheckHResult(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache), "PNG initialize");
                ComPtr<IWICBitmapFrameEncode> frame;
                ComPtr<IPropertyBag2> properties;
                CheckHResult(encoder->CreateNewFrame(&frame, &properties), "PNG frame");
                CheckHResult(frame->Initialize(properties.Get()), "PNG frame initialize");
                CheckHResult(frame->SetSize(width, height), "PNG size");
                WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
                CheckHResult(frame->SetPixelFormat(&format), "PNG format");
                if (format != GUID_WICPixelFormat32bppBGRA)
                    throw std::runtime_error("WIC 无法保存 BGRA 截图。");
                CheckHResult(frame->WritePixels(height, width * 4, static_cast<UINT>(pixels.size()),
                                                const_cast<BYTE *>(pixels.data())),
                             "PNG pixels");
                CheckHResult(frame->Commit(), "PNG frame commit");
                CheckHResult(encoder->Commit(), "PNG commit");
                frame.Reset();
                encoder.Reset();
                stream.Reset();
                if (destination.ExpectedHash)
                {
                    if (!fs::exists(path) || HashFile(path) != *destination.ExpectedHash)
                        throw std::runtime_error("截图目标在保存期间发生变化。");
                    auto backup = fs::path(temporary.wstring() + L".previous");
                    auto writtenHash = HashFile(temporary);
                    CheckHResult(ReplaceFileW(path.c_str(), temporary.c_str(), backup.c_str(),
                                              REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr)
                                     ? S_OK
                                     : HRESULT_FROM_WIN32(GetLastError()),
                                 "提交截图");
                    if (HashFile(backup) != *destination.ExpectedHash)
                    {
                        if (HashFile(path) == writtenHash)
                            CheckHResult(ReplaceFileW(path.c_str(), backup.c_str(), temporary.c_str(),
                                                      REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr)
                                             ? S_OK
                                             : HRESULT_FROM_WIN32(GetLastError()),
                                         "恢复并发替换截图");
                        throw std::runtime_error("截图目标在提交时被替换，已保留外部文件。");
                    }
                    fs::remove(backup);
                }
                else
                    fs::rename(temporary, path);
            }
            catch (...)
            {
                std::error_code ignored;
                fs::remove(temporary, ignored);
                throw;
            }
        }
    } // 命名空间

    NativePreviewWorker::NativePreviewWorker(fs::path game, fs::path data, fs::path tpk, PreviewCallbacks callbacks)
        : game_(std::move(game)), data_(std::move(data)), tpk_(std::move(tpk)), callbacks_(std::move(callbacks)),
          worker_(
              [this](auto token)
              {
                  Run(token);
              })
    {
    }

    NativePreviewWorker::~NativePreviewWorker()
    {
        worker_.request_stop();
        {
            std::lock_guard lock(mutex_);
            loading_.request_stop();
            if (pending_)
                pending_->Cancellation.request_stop();
        }
        wake_.notify_all();
        if (worker_.joinable())
            worker_.join();
    }

    uint64_t NativePreviewWorker::Load(CardRecord card, std::optional<CharacterDocument> draft,
                                       CharacterPreviewVisibility visibility, bool preserveTime,
                                       PreviewResources resources)
    {
        std::lock_guard lock(mutex_);
        loading_.request_stop();
        visibility_ = visibility;
        pendingDraft_.reset();
        pending_ = Request{++generation_, std::move(card), std::move(draft),    visibility, {},
                           preserveTime,  false,           std::move(resources)};
        unload_ = false;
        quiesce_ = false;
        // 只有渲染线程离开暂停点时才能撤销确认。若界面在它醒来前再次请求暂停，
        // 提前清零会让双方都等待对方，导致关闭窗口后进程无法退出。
        wake_.notify_all();
        return generation_;
    }

    void NativePreviewWorker::Unload()
    {
        std::lock_guard lock(mutex_);
        ++generation_;
        loading_.request_stop();
        pending_.reset();
        pendingDraft_.reset();
        unload_ = true;
        wake_.notify_all();
    }

    void NativePreviewWorker::Quiesce()
    {
        std::unique_lock lock(mutex_);
        quiesce_ = true;
        loading_.request_stop();
        if (pending_)
            pending_->Cancellation.request_stop();
        pending_.reset();
        pendingDraft_.reset();
        wake_.notify_all();
        wake_.wait(lock,
                   [&]
                   {
                       return quiescent_ || finished_;
                   });
    }

    void NativePreviewWorker::Resize(PreviewViewportSize value)
    {
        std::lock_guard lock(mutex_);
        size_ = value;
        dirty_ = true;
        wake_.notify_all();
    }

    void NativePreviewWorker::View(NativeRenderCamera value, bool /*交互标记*/)
    {
        std::lock_guard lock(mutex_);
        camera_ = value;
        // 预设镜头和滚轮操作需要与拖动相同的时序历史稳定处理。
        interactionUntil_ = std::chrono::steady_clock::now() + std::chrono::milliseconds(150);
        dirty_ = true;
        wake_.notify_all();
    }

    void NativePreviewWorker::Environment(NativeFrameOptions value)
    {
        std::lock_guard lock(mutex_);
        environment_ = value;
        environmentDirty_ = true;
        wake_.notify_all();
    }

    void NativePreviewWorker::Paused(bool value)
    {
        std::lock_guard lock(mutex_);
        paused_ = value;
        resetClock_ = true;
        dirty_ = true;
        wake_.notify_all();
    }

    void NativePreviewWorker::Visible(bool value)
    {
        std::lock_guard lock(mutex_);
        visible_ = value;
        resetClock_ = true;
        dirty_ = true;
        wake_.notify_all();
    }

    void NativePreviewWorker::Screenshot(ScreenshotTarget target)
    {
        std::lock_guard lock(mutex_);
        screenshot_ = std::move(target);
        wake_.notify_all();
    }

    void NativePreviewWorker::Visibility(CharacterPreviewVisibility value)
    {
        std::lock_guard lock(mutex_);
        if (visibility_ == value)
            return;
        visibility_ = value;
        dirty_ = true;
        wake_.notify_all();
    }

    void NativePreviewWorker::Update(CharacterDocument draft, CharacterPreviewVisibility visibility)
    {
        std::lock_guard lock(mutex_);
        pendingDraft_ = std::move(draft);
        visibility_ = visibility;
        dirty_ = true;
        wake_.notify_all();
    }

    void NativePreviewWorker::Run(std::stop_token stop)
    {
        auto com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        try
        {
            std::unique_ptr<NativeCharacterService> service;
            PreviewResources serviceResources;
            std::unique_ptr<GraphicsDevice> graphics;
            PreparedCharacter prepared;
            std::unique_ptr<NativeCharacterRuntime> runtime;
            std::unique_ptr<NativeFramePipeline> pipeline;
            std::unique_ptr<NativeSwapChainSurface> surface;
            std::optional<Request> active;
            uint64_t loaded = 0;
            float time = 0;
            bool recovered = false;
            bool needsDraw = true, interactiveApplied = false;
            auto last = std::chrono::steady_clock::now();
            auto nextFrame = last;
            auto applyVisibility = [&](CharacterPreviewVisibility visibility)
            {
                if (prepared.Scene->Definition->Visibility == visibility)
                    return;
                runtime->Visibility(visibility);
                pipeline->Appearance().UpdateVisibility();
                pipeline->ResetHistory();
                if (active)
                    active->Visibility = visibility;
                needsDraw = true;
            };
            while (!stop.stop_requested())
            {
                std::optional<Request> request;
                std::optional<CharacterDocument> draft;
                std::optional<ScreenshotTarget> screenshot;
                PreviewViewportSize size;
                NativeRenderCamera camera;
                NativeFrameOptions environment;
                CharacterPreviewVisibility visibility;
                bool paused, visible, interactive, unload, environmentDirty, resetClock;
                {
                    std::unique_lock lock(mutex_);
                    auto command = [&]
                    {
                        return quiesce_ || pending_.has_value() || pendingDraft_.has_value() || unload_ ||
                               screenshot_.has_value() || dirty_ || environmentDirty_;
                    };
                    if (visible_ && loaded && (!paused_ || needsDraw))
                        wake_.wait_until(lock, stop, nextFrame, command);
                    else if (visible_ && loaded && interactiveApplied)
                        wake_.wait_until(lock, stop, interactionUntil_, command);
                    else
                        wake_.wait(lock, stop, command);
                    if (stop.stop_requested())
                        break;
                    if (quiesce_)
                    {
                        // 包括 Present 在内的即时上下文操作均已返回；
                        // 界面解除交换链绑定之前仍需保留 GPU 资源。
                        quiescent_ = true;
                        wake_.notify_all();
                        wake_.wait(lock, stop,
                                   [&]
                                   {
                                       return !quiesce_;
                                   });
                        if (stop.stop_requested())
                            break;
                        quiescent_ = false;
                    }
                    request = std::move(pending_);
                    pending_.reset();
                    draft = std::move(pendingDraft_);
                    pendingDraft_.reset();
                    screenshot = std::move(screenshot_);
                    screenshot_.reset();
                    size = size_;
                    camera = camera_;
                    environment = environment_;
                    paused = paused_;
                    visible = visible_;
                    interactive = std::chrono::steady_clock::now() < interactionUntil_;
                    needsDraw = needsDraw || dirty_;
                    dirty_ = false;
                    resetClock = resetClock_;
                    resetClock_ = false;
                    unload = unload_;
                    unload_ = false;
                    environmentDirty = environmentDirty_;
                    environmentDirty_ = false;
                    if (request)
                        loading_ = request->Cancellation;
                }
                if (draft && !loaded && active && !request && !unload)
                {
                    request = active;
                    request->Cancellation = std::stop_source{};
                    request->PreserveTime = true;
                    std::lock_guard lock(mutex_);
                    loading_ = request->Cancellation;
                }
                if (request && draft)
                {
                    request->Draft = std::move(draft);
                    draft.reset();
                }
                if (unload)
                {
                    surface.reset();
                    pipeline.reset();
                    runtime.reset();
                    prepared = {};
                    active.reset();
                    service.reset();
                    serviceResources.reset();
                    loaded = 0;
                }
                if (request)
                {
                    active = request;
                    loaded = 0;
                    recovered = request->Recovery;
                    surface.reset();
                    pipeline.reset();
                    runtime.reset();
                    prepared = {};
                    if (!request->PreserveTime)
                        time = 0;
                    try
                    {
                        auto progress = [&](WorkProgress const &value)
                        {
                            if (callbacks_.Progress)
                                callbacks_.Progress(request->Generation, value.Message);
                        };
                        auto token = request->Cancellation.get_token();
                        if (!service || serviceResources != request->Resources)
                        {
                            service.reset();
                            serviceResources = request->Resources;
                            service = std::make_unique<NativeCharacterService>(game_, data_, tpk_, serviceResources);
                        }
                        prepared = service->Prepare(request->Card, request->Draft ? &*request->Draft : nullptr,
                                                    request->Visibility, progress, token, true);
                        CheckCancelled(token);
                        if (!graphics)
                            graphics = std::make_unique<GraphicsDevice>();
                        runtime = std::make_unique<NativeCharacterRuntime>(prepared.Scene, prepared.Assets,
                                                                           tpk_.parent_path() / L"native" / L"win-x64");
                        runtime->Update(0);
                        PoseCharacterFace(*prepared.Scene, 1);
                        pipeline = std::make_unique<NativeFramePipeline>(*graphics, *prepared.Scene, *prepared.Assets,
                                                                         environment);
                        CheckCancelled(token);
                        size.Width = std::max(1, size.Width);
                        size.Height = std::max(1, size.Height);
                        surface = std::make_unique<NativeSwapChainSurface>(*graphics, size);
                        loaded = request->Generation;
                        last = std::chrono::steady_clock::now();
                        nextFrame = last;
                        needsDraw = true;
                        interactiveApplied = false;
                        {
                            std::lock_guard lock(mutex_);
                            visibility = visibility_;
                        }
                        // 加载期间的导航也必须生效，不能被旧请求的显示状态覆盖。
                        applyVisibility(visibility);
                        if (callbacks_.Ready)
                        {
                            auto diagnostics = prepared.Scene->Diagnostics;
                            for (auto const &message : runtime->Diagnostics())
                                if (std::find(diagnostics.begin(), diagnostics.end(), message) == diagnostics.end())
                                    diagnostics.push_back(message);
                            for (auto const &message : pipeline->SourceReferenceDiagnostics())
                                if (std::find(diagnostics.begin(), diagnostics.end(), message) == diagnostics.end())
                                    diagnostics.push_back(message);
                            callbacks_.Ready({loaded, surface->Acquire(), Bounds(*prepared.Scene, false),
                                              Bounds(*prepared.Scene, true), std::move(diagnostics),
                                              std::make_shared<CharacterDefinition>(*prepared.Scene->Definition),
                                              prepared.Assets->ReaderStats().Loads});
                        }
                    }
                    catch (Cancelled const &)
                    {
                    }
                    catch (std::exception const &error)
                    {
                        surface.reset();
                        pipeline.reset();
                        runtime.reset();
                        prepared = {};
                        if (!request->Cancellation.stop_requested() && callbacks_.Failure)
                            callbacks_.Failure(request->Generation, error.what());
                    }
                }
                if (draft && pipeline && surface && loaded && active &&
                    (!prepared.Document || !prepared.Document->ContentEquals(*draft)))
                {
                    try
                    {
                        auto token = active->Cancellation.get_token();
                        {
                            std::lock_guard lock(mutex_);
                            visibility = visibility_;
                        }
                        auto next = service->Prepare(active->Card, &*draft, visibility, {}, token, true, &prepared);
                        CheckCancelled(token);
                        auto previous = *prepared.Scene->Definition;
                        // 材质比较只需参数快照，不能让共享的部件定义被原地更新覆盖。
                        previous.Parts.clear();
                        for (auto const &part : prepared.Scene->Definition->Parts)
                        {
                            auto snapshot = std::make_shared<CharacterPart>();
                            snapshot->Role = part->Role;
                            snapshot->Slot = part->Slot;
                            snapshot->Prefab = part->Prefab;
                            snapshot->Parameters = part->Parameters;
                            previous.Parts.push_back(std::move(snapshot));
                        }
                        bool reshape = CharacterSceneRequiresRuntimeReset(*prepared.Scene, *next.Scene);
                        if (reshape)
                        {
                            if (CharacterSceneTopologyMatches(*prepared.Scene, *next.Scene))
                                runtime->RefreshRestPose(*next.Scene);
                            else
                                runtime.reset();
                        }
                        UpdateCharacterScene(*prepared.Scene, *next.Scene);
                        bool created = !runtime;
                        if (created)
                            runtime = std::make_unique<NativeCharacterRuntime>(
                                prepared.Scene, prepared.Assets, tpk_.parent_path() / L"native" / L"win-x64");
                        else if (reshape)
                            runtime->Reset();
                        if (created || reshape)
                            runtime->AdvanceFrame(0, time);
                        prepared.Document = std::move(next.Document);
                        active->Draft = prepared.Document;
                        ApplyCharacterVisibility(*prepared.Scene);
                        pipeline->UpdateCharacter(previous);
                        {
                            std::lock_guard lock(mutex_);
                            visibility = visibility_;
                        }
                        applyVisibility(visibility);
                        needsDraw = true;
                        resetClock = true;
                        if (callbacks_.Ready)
                            callbacks_.Ready({loaded, surface->Acquire(), Bounds(*prepared.Scene, false),
                                              Bounds(*prepared.Scene, true), prepared.Scene->Diagnostics,
                                              std::make_shared<CharacterDefinition>(*prepared.Scene->Definition),
                                              prepared.Assets->ReaderStats().Loads});
                    }
                    catch (Cancelled const &)
                    {
                    }
                    catch (std::exception const &error)
                    {
                        if (callbacks_.Failure)
                            callbacks_.Failure(loaded, error.what());
                        // 更新提交过程中失败时不再使用可能已失效的骨骼或材质引用。
                        loaded = 0;
                        runtime.reset();
                        pipeline.reset();
                        prepared = {};
                    }
                }
                if (!pipeline || !surface || !loaded)
                {
                    if (screenshot && callbacks_.Screenshot)
                        callbacks_.Screenshot(screenshot->Path, "预览尚未就绪。");
                    continue;
                }
                {
                    std::lock_guard lock(mutex_);
                    if (loaded != generation_)
                        continue;
                    visibility = visibility_;
                }
                try
                {
                    applyVisibility(visibility);
                    auto now = std::chrono::steady_clock::now();
                    auto delta =
                        resetClock ? 0.f : std::clamp(std::chrono::duration<float>(now - last).count(), 0.f, .1f);
                    last = now;
                    if (environmentDirty)
                    {
                        pipeline->SetEnvironment(environment);
                        needsDraw = true;
                    }
                    if (!visible || size.Width < 1 || size.Height < 1)
                    {
                        if (screenshot && callbacks_.Screenshot)
                            callbacks_.Screenshot(screenshot->Path, "请先显示预览窗口再保存截图。");
                        continue;
                    }
                    if (interactive != interactiveApplied)
                        needsDraw = true;
                    if (paused && !needsDraw && !screenshot)
                        continue;
                    surface->Resize(size);
                    pipeline->SetInteractive(interactive);
                    interactiveApplied = interactive;
                    if (!paused)
                    {
                        time += delta;
                        // 交互播放沿用原宿主的做法，每次仅推进有界的一帧；
                        // 每次编辑创建新运行状态后，不重放此前累计的全部时间。
                        runtime->AdvanceFrame(delta, time);
                    }
                    auto const &frame = pipeline->Draw(camera, size.Width, size.Height, time);
                    surface->Present(frame);
                    needsDraw = false;
                    nextFrame = now + std::chrono::microseconds(16667);
                    if (screenshot)
                    {
                        try
                        {
                            auto pixels = graphics->Readback(frame.Texture.Get());
                            // 显示表面忽略透明度，保存时也应输出相同的不透明画面，避免 PNG 查看器显示后处理中间透明度。
                            for (size_t i = 3; i < pixels.size(); i += 4)
                                pixels[i] = 255;
                            WritePng(*screenshot, pixels, frame.Width(), frame.Height());
                            if (callbacks_.Screenshot)
                                callbacks_.Screenshot(screenshot->Path, {});
                        }
                        catch (std::exception const &e)
                        {
                            if (callbacks_.Screenshot)
                                callbacks_.Screenshot(screenshot->Path, e.what());
                        }
                    }
                }
                catch (std::exception const &error)
                {
                    auto removed = graphics->Device()->GetDeviceRemovedReason();
                    surface.reset();
                    pipeline.reset();
                    runtime.reset();
                    prepared = {};
                    graphics.reset();
                    if (FAILED(removed) && active && !recovered)
                    {
                        recovered = true;
                        std::lock_guard lock(mutex_);
                        if (active->Generation == generation_ && !pending_)
                        {
                            active->Cancellation = std::stop_source{};
                            active->Recovery = true;
                            pending_ = active;
                            if (callbacks_.Progress)
                                callbacks_.Progress(loaded, "正在恢复显卡设备…");
                        }
                    }
                    else if (callbacks_.Failure)
                        callbacks_.Failure(loaded, error.what());
                    loaded = 0;
                }
            }
        }
        catch (std::exception const &e)
        {
            uint64_t generation;
            {
                std::lock_guard lock(mutex_);
                generation = generation_;
            }
            if (callbacks_.Failure)
                callbacks_.Failure(generation, e.what());
        }
        if (SUCCEEDED(com))
            CoUninitialize();
        {
            std::lock_guard lock(mutex_);
            finished_ = true;
        }
        wake_.notify_all();
    }
} // 命名空间 hs2::ui
