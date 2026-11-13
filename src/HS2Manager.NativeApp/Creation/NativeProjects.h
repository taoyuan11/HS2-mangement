#pragma once
#include "Application/NativeWindow.h"
#include "CreationProject.h"

namespace hs2::ui
{
    // 两类工程共用列表，独立于人物卡筛选、选择和快捷键状态。
    class NativeProjects : public std::enable_shared_from_this<NativeProjects>
    {
    public:
        static std::shared_ptr<NativeProjects> Create(const std::shared_ptr<NativeWindow> &owner);
        void Show(std::string kind);
        void Hide();
        void SyncControls();
        winrt::fire_and_forget Refresh();
        winrt::fire_and_forget Action(std::string action);

    private:
        template <class T> T Get(std::wstring_view name) const
        {
            return root_.FindName(winrt::hstring(name)).as<T>();
        }

        const creation::ProjectSummary *Selected() const;
        std::weak_ptr<NativeWindow> owner_;
        xaml::FrameworkElement root_{nullptr};
        winrt::com_ptr<Observable> model_;
        std::vector<creation::ProjectSummary> summaries_;
        std::string kind_;
        uint64_t generation_ = 0;
        bool active_ = false, loading_ = false;
    };
}
