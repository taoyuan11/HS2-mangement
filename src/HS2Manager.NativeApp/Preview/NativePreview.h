#pragma once
#include "Preview/NativePreviewWorker.h"
#include "Application/NativeWindow.h"

namespace hs2::ui
{
    class NativePreview : public std::enable_shared_from_this<NativePreview>
    {
    public:
        static std::shared_ptr<NativePreview> Create(std::shared_ptr<NativeWindow> const &owner);
        ~NativePreview();
        void Load(CardRecord card, xaml::FrameworkElement view, bool editor,
                  std::optional<CharacterDocument> draft = {}, CharacterPreviewVisibility visibility = {},
                  bool preserveView = false);
        void Update(CharacterDocument draft, CharacterPreviewVisibility visibility);
        void SyncRail(bool reveal = false);
        void Stop();
        bool Action(std::wstring_view name, Object sender);
        void Environment();

        void Visible(bool value)
        {
            if (worker_)
                worker_->Visible(value);
        }

        ModelExportView ExportView() const;

        CardRecord const &Card() const
        {
            return card_;
        }

        void SavedCard(CardRecord card)
        {
            card_ = std::move(card);
        }

    private:
        explicit NativePreview(std::shared_ptr<NativeWindow> const &owner);
        void Initialize();
        void Attach(xaml::FrameworkElement view, bool editor);
        void Resize();
        void Camera();
        void Frame(bool face = false);
        winrt::fire_and_forget Screenshot();
        std::weak_ptr<NativeWindow> owner_;
        std::unique_ptr<NativePreviewWorker> worker_;
        xaml::FrameworkElement view_{nullptr}, viewport_{nullptr};
        controls::SwapChainPanel panel_{nullptr};
        std::vector<std::function<void()>> unbind_;
        CardRecord card_;
        uint64_t generation_ = 0, epoch_ = 0;
        std::optional<CharacterDocument> draft_;
        CharacterPreviewVisibility visibility_;
        NativeRendererBounds body_, face_;
        NativeRenderCamera camera_;
        Vector3 center_{};
        double yaw_ = 0, pitch_ = 5, distance_ = 5, aspect_ = 1, width_ = 640;
        bool editor_ = false, playing_ = true, frame_ = true, first_ = true, dragging_ = false, pan_ = false;
        winrt::Windows::Foundation::Point pointer_{};
    };
} // 命名空间 hs2::ui
