#pragma once
#include <windows.h>
#undef GetCurrentTime
#include "Model/ModelExport.h"
#include "Common/Core.h"
#include "Mods/ModIndex.h"
#include "Preview/TemporaryPreviewSession.h"
#include "Infrastructure/Observable.h"
#include "Update.h"
#include <set>
#include <thread>
#include <unordered_map>
#include <winrt/Microsoft.UI.Dispatching.h>
#include <winrt/Microsoft.UI.Windowing.h>
#include <winrt/Microsoft.UI.Xaml.Controls.Primitives.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.UI.Xaml.Input.h>
#include <winrt/Microsoft.UI.Xaml.Markup.h>
#include <winrt/Microsoft.UI.Xaml.Media.Imaging.h>
#include <winrt/Microsoft.UI.Xaml.Media.h>
#include <winrt/Windows.ApplicationModel.DataTransfer.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.Storage.Pickers.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.UI.ViewManagement.h>

namespace hs2::ui
{
    namespace xaml = winrt::Microsoft::UI::Xaml;
    namespace controls = xaml::Controls;
    class NativeEditor;
    class NativeCreation;
    class NativeProjects;
    class NativePreview;

    class NativeWindow : public std::enable_shared_from_this<NativeWindow>
    {
    public:
        static std::shared_ptr<NativeWindow> Create(fs::path dataDirectory, std::unique_ptr<CardRepository> repository);
        ~NativeWindow();
        void Shutdown();

        xaml::Window Window() const
        {
            return window_;
        }

    private:
        friend class NativeEditor;
        friend class NativeCreation;
        friend class NativeProjects;
        friend class NativePreview;
        NativeWindow(fs::path dataDirectory, std::unique_ptr<CardRepository> repository);
        void Initialize();
        void DiscoverGame();
        winrt::fire_and_forget CompleteGameSetup(std::filesystem::path detected);
        void StartLibraryScan(bool gameAvailable);
        void BindEvents(xaml::FrameworkElement const &root, std::wstring_view view);
        xaml::FrameworkElement LoadView(std::wstring_view view);
        void ApplyTheme();
        void Reload();
        void Filter();
        void Select();
        void Refresh();
        void Navigate(std::string mode);
        void Work(std::string label, std::function<std::string(std::stop_token, Progress)> operation,
                  std::function<void()> completed = {});
        void SetBusy(bool value);
        void Report(std::string message);
        void ClosePreview();
        void CancelTemporaryPreview();
        void BindTemporaryPreviewDrop(xaml::FrameworkElement const &target);
        void LoadTemporaryCard(const std::string &id);
        winrt::fire_and_forget OpenTemporaryPreview(fs::path source = {});
        winrt::fire_and_forget Action(std::wstring name, Object sender);
        winrt::Windows::Foundation::IAsyncOperation<bool> Confirm(winrt::hstring title, winrt::hstring message,
                                                                  bool destructive = false);
        winrt::Windows::Foundation::IAsyncOperation<winrt::hstring> Prompt(winrt::hstring title, winrt::hstring message,
                                                                           winrt::hstring initial = L"");
        winrt::Windows::Foundation::IAsyncOperation<winrt::hstring> PickFolder(winrt::hstring title);
        winrt::fire_and_forget Settings();
        winrt::fire_and_forget ExportCards(std::vector<CardRecord> cards);
        winrt::fire_and_forget ExportModels(std::vector<ModelExportSource> sources, int clothing = 0,
                                            bool accessories = true);
        winrt::fire_and_forget ShowReport(winrt::hstring title, winrt::hstring message);
        winrt::fire_and_forget OpenEditor(CardRecord card);
        winrt::fire_and_forget OpenCreation(std::string mode, fs::path source = {});
        void ShowProjects(std::string kind);
        void HideProjects();
        winrt::Windows::Foundation::IAsyncOperation<bool> CloseEditor();
        winrt::fire_and_forget RequestClose();
        winrt::fire_and_forget LoadCover(winrt::com_ptr<Observable> item, CardRecord card,
                                         std::shared_ptr<TemporaryPreviewStorage> storage = {});
        std::vector<CardRecord> Selected() const;
        std::vector<std::string> SelectedIds() const;

        template <class T> T Control(std::wstring_view name) const
        {
            return root_.FindName(winrt::hstring(name)).as<T>();
        }

        void InitializeUpdates();
        void BindUpdates(xaml::FrameworkElement const &view, controls::ContentDialog const &dialog);
        void RefreshUpdateView();
        void CheckUpdates(bool automatic = false);
        void DownloadUpdate();
        void LaunchPendingUpdate();
        std::jthread updateWork_;
        std::optional<update::Release> updateRelease_;
        fs::path updatePackage_, updateRequest_;
        std::string updateStatus_ = "尚未检查更新";
        double updateProgress_ = 0;
        bool updateBusy_ = false, installRequested_ = false;
        winrt::weak_ref<xaml::FrameworkElement> updateView_;
        HWND Handle() const;
        fs::path dataDirectory_;
        AppSettings settings_;
        std::unique_ptr<CardRepository> repository_;
        std::unique_ptr<CardLibraryService> library_;
        std::unique_ptr<CardFileService> files_;
        std::unique_ptr<ModDependencyService> mods_;
        std::vector<CardRecord> cards_, visible_;
        std::unordered_map<std::string, std::size_t> visibleIndex_;
        winrt::com_ptr<Observable> model_;
        xaml::Window window_{nullptr};
        xaml::FrameworkElement root_{nullptr};
        winrt::Microsoft::UI::Dispatching::DispatcherQueue dispatcher_{nullptr};
        winrt::Microsoft::UI::Dispatching::DispatcherQueueTimer searchTimer_{nullptr};
        std::string mode_ = "all", folder_, collection_, selectedCardId_;
        std::set<std::string> duplicates_;
        std::jthread work_;
        std::shared_ptr<NativeEditor> editor_;
        std::shared_ptr<NativeCreation> creation_;
        std::shared_ptr<NativeProjects> projects_;
        std::string creationKind_;
        std::shared_ptr<NativePreview> preview_;
        std::shared_ptr<TemporaryPreviewSession> temporaryPreview_;
        std::stop_source temporaryCancellation_;
        uint64_t temporaryGeneration_ = 0;
        bool temporaryOpening_ = false;
        bool startupStarted_ = false, gameSetupPending_ = false;
        controls::ContentDialog temporaryPasswordDialog_{nullptr};
        bool closePrompt_ = false;
        bool closeReady_ = false;
        bool resourcesReleased_ = false;
        bool busy_ = false, closing_ = false, dialogOpen_ = false, updating_ = false;
        winrt::Windows::UI::ViewManagement::UISettings preferences_;
        winrt::event_token preferenceToken_{};
    };

    fs::path ExecutableDirectory();
    void InitializeResources();
} // 命名空间 hs2::ui
