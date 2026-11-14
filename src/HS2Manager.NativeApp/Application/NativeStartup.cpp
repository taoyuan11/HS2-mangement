#include "Application/NativeWindow.h"
#include "Infrastructure/GameDiscovery.h"

using namespace winrt;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    void NativeWindow::DiscoverGame()
    {
        if (startupStarted_ || closing_)
            return;
        startupStarted_ = true;
        gameSetupPending_ = true;
        auto detected = std::make_shared<fs::path>();
        auto configured = Path(settings_.GamePath);
        auto application = ExecutableDirectory();
        auto weak = weak_from_this();
        Work(
            "正在自动检测 HS2 游戏目录…",
            [detected, configured, application](auto stop, auto)
            {
                if (!stop.stop_requested())
                    *detected = IsGameDirectory(configured) ? configured
                                                            : FindGameDirectory(GameSearchLocations(application), stop);
                return detected->empty() ? "未检测到 HS2 游戏，请手动选择游戏根目录。"
                                         : "已检测到 HS2 游戏：" + Utf8(*detected);
            },
            [weak, detected]
            {
                if (auto self = weak.lock())
                    self->CompleteGameSetup(*detected);
            });
    }

    fire_and_forget NativeWindow::CompleteGameSetup(fs::path detected)
    {
        auto lifetime = shared_from_this();
        bool available = false;
        try
        {
            std::wstring message = L"未在常见位置检测到 HS2 游戏。请选择包含 HoneySelect2.exe、"
                                   L"HoneySelect2_Data 和 abdata 的游戏根目录。\n\n"
                                   L"也可稍后在左下角“设置与连接”中配置。";
            while (!closing_ && !resourcesReleased_)
            {
                if (!detected.empty())
                {
                    if (IsGameDirectory(detected))
                    {
                        auto next = settings_;
                        next.GamePath = Utf8(detected.lexically_normal());
                        next.Save(dataDirectory_);
                        settings_ = std::move(next);
                        available = true;
                        break;
                    }
                    message = L"所选文件夹不是有效的 HS2 游戏根目录。请确认其中包含 HoneySelect2.exe、"
                              L"HoneySelect2_Data 和 abdata，然后重新选择。";
                }
                ContentDialog dialog;
                dialog.XamlRoot(root_.XamlRoot());
                dialog.RequestedTheme(root_.RequestedTheme());
                dialog.Title(box_value(L"选择 HS2 游戏目录"));
                TextBlock description;
                description.Text(message);
                description.TextWrapping(TextWrapping::Wrap);
                dialog.Content(description);
                dialog.PrimaryButtonText(L"手动选择目录");
                dialog.CloseButtonText(L"稍后设置");
                dialog.DefaultButton(ContentDialogButton::Primary);
                dialogOpen_ = true;
                auto result = co_await dialog.ShowAsync();
                dialogOpen_ = false;
                if (closing_ || resourcesReleased_ || result != ContentDialogResult::Primary)
                    break;
                dialogOpen_ = true;
                auto selected = co_await PickFolder(L"选择 HS2 游戏根目录");
                dialogOpen_ = false;
                detected = fs::path(selected.c_str());
                // 取消系统选择器后回到说明，可重新选择或稍后设置。
            }
            dialogOpen_ = false;
            gameSetupPending_ = false;
            if (closing_ || resourcesReleased_)
                co_return;
            Reload();
            StartLibraryScan(available);
        }
        catch (hresult_error const &error)
        {
            Report("游戏目录设置未完成：" + to_string(error.message()));
        }
        catch (std::exception const &error)
        {
            Report(std::string("游戏目录设置未完成：") + error.what());
        }
        dialogOpen_ = false;
        gameSetupPending_ = false;
    }

    void NativeWindow::StartLibraryScan(bool gameAvailable)
    {
        auto roots = gameAvailable ? settings_.ScanRoots() : std::vector<fs::path>{};
        if (!gameAvailable)
            for (auto const &root : settings_.ExtraRoots)
                roots.push_back(Path(root));
        Work("检查未完成操作",
             [this, roots = std::move(roots), gameAvailable](auto stop, auto progress)
             {
                 TemporaryPreviewStorage::CleanAbandoned(dataDirectory_);
                 auto recovered = files_->Recover(stop);
                 std::string result;
                 if (!roots.empty())
                 {
                     auto scan = library_->Scan(roots, progress, stop, settings_.RecursiveCardSearch);
                     result = "扫描完成：" + std::to_string(scan.Valid) + " 张人物卡，" +
                              std::to_string(scan.Warnings.size()) + " 项目录/文件警告";
                 }
                 if (!gameAvailable)
                     result += (result.empty() ? "" : "；") +
                               std::string("游戏目录尚未连接，请在“设置与连接”中选择游戏根目录。");
                 for (auto const &message : recovered)
                     result += "；" + message;
                 return result;
             });
    }
}
