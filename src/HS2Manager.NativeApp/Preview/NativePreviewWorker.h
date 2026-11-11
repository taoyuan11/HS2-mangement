#pragma once
#include "Character/CharacterPreparation.h"
#include "Pipeline/NativeFramePipeline.h"
#include <chrono>
#include <condition_variable>
#include <mutex>
#include <thread>

namespace hs2::ui
{
    struct PreviewReady
    {
        uint64_t Generation = 0;
        ComPtr<IDXGISwapChain2> Chain;
        NativeRendererBounds Body, Face;
        std::vector<std::string> Diagnostics;
        std::shared_ptr<const CharacterDefinition> Definition;
    };

    struct PreviewCallbacks
    {
        std::function<void(PreviewReady)> Ready;
        std::function<void(uint64_t, std::string)> Progress, Failure;
        std::function<void(std::filesystem::path, std::string)> Screenshot;
    };

    struct ScreenshotTarget
    {
        fs::path Path;
        std::optional<std::string> ExpectedHash;
    };

    class NativePreviewWorker final
    {
    public:
        NativePreviewWorker(fs::path game, fs::path data, fs::path tpk, PreviewCallbacks callbacks);
        ~NativePreviewWorker();
        uint64_t Load(CardRecord card, std::optional<CharacterDocument> draft = {},
                      CharacterPreviewVisibility visibility = {}, bool preserveTime = false);
        void Unload();
        // 等待渲染器退出 Present 后，界面再解除面板绑定。
        void Quiesce();
        void Resize(PreviewViewportSize size);
        void View(NativeRenderCamera camera, bool interactive);
        void Environment(NativeFrameOptions options);
        void Paused(bool value);
        void Visible(bool value);
        void Screenshot(ScreenshotTarget target);

    private:
        struct Request
        {
            uint64_t Generation;
            CardRecord Card;
            std::optional<CharacterDocument> Draft;
            CharacterPreviewVisibility Visibility;
            std::stop_source Cancellation;
            bool PreserveTime = false;
            bool Recovery = false;
        };

        void Run(std::stop_token stop);
        fs::path game_, data_, tpk_;
        PreviewCallbacks callbacks_;
        std::mutex mutex_;
        std::condition_variable_any wake_;
        std::optional<Request> pending_;
        std::stop_source loading_;
        uint64_t generation_ = 0;
        PreviewViewportSize size_{640, 640, 1};
        NativeRenderCamera camera_;
        NativeFrameOptions environment_;
        std::optional<ScreenshotTarget> screenshot_;
        std::chrono::steady_clock::time_point interactionUntil_{};
        bool paused_ = false, visible_ = true, unload_ = false, environmentDirty_ = true;
        bool dirty_ = true, resetClock_ = true;
        bool quiesce_ = false, quiescent_ = false, finished_ = false;
        std::jthread worker_;
    };
} // 命名空间 hs2::ui
