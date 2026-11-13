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
        std::size_t ResourceLoads = 0;
        std::vector<std::string> Motions;
        int MotionIndex = 0;
    };

    struct PreviewCallbacks
    {
        // 先在界面线程按人物尺寸定位，再用 ConfirmView 允许绘制首帧。
        std::function<void(uint64_t, NativeRendererBounds, NativeRendererBounds)> PrepareView;
        // 交换链至少已提交一帧，收到后才能绑定面板并结束加载提示。
        std::function<void(PreviewReady)> Ready;
        std::function<void(uint64_t, std::string)> Progress, Failure;
        std::function<void(std::filesystem::path, std::string)> Screenshot;
        std::function<void(uint64_t, int, int, NativeRendererBounds, NativeRendererBounds, std::string)> MotionChanged;
    };

    // 界面队列只持有信箱，关闭时可以立即释放尚未投递的交换链引用。
    class PreviewReadyDelivery final
    {
    public:
        bool Publish(PreviewReady value)
        {
            std::lock_guard lock(mutex_);
            if (closed_)
                return false;
            ready_ = std::move(value);
            return true;
        }

        std::optional<PreviewReady> Take()
        {
            std::lock_guard lock(mutex_);
            auto result = std::move(ready_);
            ready_.reset();
            return result;
        }

        void Close()
        {
            std::lock_guard lock(mutex_);
            closed_ = true;
            ready_.reset();
        }

    private:
        std::mutex mutex_;
        std::optional<PreviewReady> ready_;
        bool closed_ = false;
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
                      CharacterPreviewVisibility visibility = {}, bool preserveTime = false,
                      PreviewResources resources = {});
        void Update(CharacterDocument draft, CharacterPreviewVisibility visibility);
        void Unload();
        // 等待渲染器退出 Present 后，界面再解除面板绑定。
        void Quiesce();
        void Resize(PreviewViewportSize size);
        void View(NativeRenderCamera camera, bool interactive);
        bool ConfirmView(uint64_t generation, NativeRenderCamera camera);
        void Environment(NativeFrameOptions options);
        void Paused(bool value);
        void Motion(uint64_t generation, int index);
        void Visible(bool value);
        void Visibility(CharacterPreviewVisibility value);
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
            PreviewResources Resources;
        };

        void Run(std::stop_token stop);
        fs::path game_, data_, tpk_;
        PreviewCallbacks callbacks_;
        std::mutex mutex_;
        std::condition_variable_any wake_;
        std::optional<Request> pending_;
        std::optional<CharacterDocument> pendingDraft_;
        std::optional<int> pendingMotion_;
        std::stop_source loading_;
        uint64_t generation_ = 0, viewGeneration_ = 0;
        PreviewViewportSize size_{640, 640, 1};
        NativeRenderCamera camera_;
        NativeFrameOptions environment_;
        CharacterPreviewVisibility visibility_;
        std::optional<ScreenshotTarget> screenshot_;
        std::chrono::steady_clock::time_point interactionUntil_{};
        bool paused_ = false, visible_ = true, unload_ = false, environmentDirty_ = true;
        bool dirty_ = true, resetClock_ = true;
        bool quiesce_ = false, quiescent_ = false, finished_ = false;
        std::jthread worker_;
    };
} // 命名空间 hs2::ui
