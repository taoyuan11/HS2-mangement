#include "Application/NativeWindow.h"
#include "AppVersion.h"
#include <chrono>
#include <shellapi.h>

using namespace winrt;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    void NativeWindow::InitializeUpdates()
    {
        auto weak = weak_from_this();
        Control<Button>(L"UpdateNoticeButton")
            .Click(
                [weak](auto &&, auto &&)
                {
                    if (auto self = weak.lock())
                        self->Settings();
                });
        try
        {
            auto error = update::CacheRoot(ExecutableDirectory()) / L"last-error.txt";
            if (fs::exists(error))
                updateStatus_ = update::ReadText(error);
            auto now = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
            if (settings_.AutoCheckUpdates &&
                (now < settings_.LastUpdateCheck || now - settings_.LastUpdateCheck >= 86400))
                CheckUpdates(true);
        }
        catch (std::exception const &e)
        {
            updateStatus_ = e.what();
        }
    }

    void NativeWindow::RefreshUpdateView()
    {
        auto view = updateView_.get();
        if (!view)
            return;
        view.FindName(L"UpdateStatus").as<TextBlock>().Text(to_hstring(updateStatus_));
        view.FindName(L"CurrentVersion").as<TextBlock>().Text(L"当前版本：" + to_hstring(HS2_VERSION));
        view.FindName(L"LatestVersion")
            .as<TextBlock>()
            .Text(updateRelease_ ? L"最新版本：" + to_hstring(updateRelease_->Version) : L"");
        view.FindName(L"ReleaseNotes").as<TextBlock>().Text(updateRelease_ ? to_hstring(updateRelease_->Notes) : L"");
        view.FindName(L"CheckUpdate").as<Button>().IsEnabled(!updateBusy_);
        view.FindName(L"DownloadUpdate")
            .as<Button>()
            .IsEnabled(!updateBusy_ && updateRelease_.has_value() && updatePackage_.empty() &&
                       update::IsPortable(ExecutableDirectory()));
        view.FindName(L"CancelUpdate").as<Button>().IsEnabled(updateBusy_);
        view.FindName(L"InstallUpdate").as<Button>().IsEnabled(!updateBusy_ && !updatePackage_.empty());
        auto progress = view.FindName(L"UpdateProgress").as<ProgressBar>();
        progress.IsIndeterminate(updateBusy_ && updateProgress_ == 0);
        progress.Value(updateProgress_);
        progress.Visibility(updateBusy_ ? Visibility::Visible : Visibility::Collapsed);
    }

    void NativeWindow::BindUpdates(FrameworkElement const &view, ContentDialog const &dialog)
    {
        updateView_ = make_weak(view);
        view.FindName(L"AutoUpdateBox").as<CheckBox>().IsChecked(settings_.AutoCheckUpdates);
        if (!update::IsPortable(ExecutableDirectory()))
            view.FindName(L"UpdateInstallHint")
                .as<TextBlock>()
                .Text(L"当前为开发构建，只能检查版本。请从发布页下载便携包后使用自动安装。");
        auto weak = weak_from_this();
        view.FindName(L"CheckUpdate")
            .as<Button>()
            .Click(
                [weak](auto &&, auto &&)
                {
                    if (auto self = weak.lock())
                        self->CheckUpdates();
                });
        view.FindName(L"DownloadUpdate")
            .as<Button>()
            .Click(
                [weak](auto &&, auto &&)
                {
                    if (auto self = weak.lock())
                        self->DownloadUpdate();
                });
        view.FindName(L"CancelUpdate")
            .as<Button>()
            .Click(
                [weak](auto &&, auto &&)
                {
                    if (auto self = weak.lock())
                        self->updateWork_.request_stop();
                });
        view.FindName(L"OpenRelease")
            .as<Button>()
            .Click(
                [weak](auto &&, auto &&)
                {
                    if (auto self = weak.lock())
                    {
                        auto url = self->updateRelease_ ? update::Path(self->updateRelease_->PageUrl).wstring()
                                                        : std::wstring(update::ReleasesPage);
                        ShellExecuteW(self->Handle(), L"open", url.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
                    }
                });
        auto weakDialog = make_weak(dialog);
        view.FindName(L"InstallUpdate")
            .as<Button>()
            .Click(
                [weak, weakDialog](auto &&, auto &&)
                {
                    if (auto self = weak.lock())
                    {
                        if (self->busy_ || self->closePrompt_ || self->updateBusy_)
                        {
                            self->updateStatus_ = "请先完成当前操作，再重启安装。";
                            self->RefreshUpdateView();
                            return;
                        }
                        if (self->updateRequest_.empty())
                            return;
                        self->installRequested_ = true;
                        if (auto dialog = weakDialog.get())
                            dialog.Hide();
                    }
                });
        RefreshUpdateView();
    }

    void NativeWindow::CheckUpdates(bool automatic)
    {
        if (updateBusy_ || closing_)
            return;
        try
        {
            settings_.LastUpdateCheck = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
            settings_.Save(dataDirectory_);
        }
        catch (std::exception const &e)
        {
            updateStatus_ = e.what();
            RefreshUpdateView();
            return;
        }
        updateBusy_ = true;
        updateProgress_ = 0;
        updateStatus_ = "正在检查 GitHub 正式版本…";
        RefreshUpdateView();
        auto weak = weak_from_this();
        auto dispatcher = dispatcher_;
        updateWork_ = std::jthread(
            [weak, dispatcher, automatic](std::stop_token stop)
            {
                std::optional<update::Release> release;
                std::string error;
                try
                {
                    release = update::Check(HS2_VERSION, stop);
                }
                catch (std::exception const &e)
                {
                    error = e.what();
                }
                dispatcher.TryEnqueue(
                    [weak, release = std::move(release), error, automatic]() mutable
                    {
                        auto self = weak.lock();
                        if (!self || self->closing_)
                            return;
                        self->updateBusy_ = false;
                        if (!error.empty())
                            self->updateStatus_ = error;
                        else
                        {
                            if (!release || (self->updateRelease_ && self->updateRelease_->Version != release->Version))
                            {
                                self->updatePackage_.clear();
                                self->updateRequest_.clear();
                            }
                            self->updateRelease_ = std::move(release);
                            self->updateStatus_ = self->updateRelease_
                                                      ? "发现新版本，可点击下载。安装前会再次等待你的确认。"
                                                      : "当前没有可用的更新。";
                            if (self->updateRelease_)
                            {
                                auto bar = self->Control<InfoBar>(L"UpdateNotice");
                                bar.Message(L"新版本 " + to_hstring(self->updateRelease_->Version) +
                                            L" 已发布，可在设置中下载与安装。");
                                bar.IsOpen(true);
                            }
                        }
                        self->RefreshUpdateView();
                        if (!automatic && !self->updateView_.get())
                            self->Report(self->updateStatus_);
                    });
            });
    }

    void NativeWindow::DownloadUpdate()
    {
        if (updateBusy_ || !updateRelease_ || closing_)
            return;
        updateBusy_ = true;
        updateProgress_ = 0;
        updateStatus_ = "正在下载更新…";
        RefreshUpdateView();
        auto weak = weak_from_this();
        auto dispatcher = dispatcher_;
        auto release = *updateRelease_;
        auto installation = ExecutableDirectory(), data = dataDirectory_;
        updateWork_ = std::jthread(
            [weak, dispatcher, release, installation, data](std::stop_token stop)
            {
                fs::path session, package, request;
                std::string error;
                try
                {
                    session = update::CacheRoot(installation) / update::Path("download-" + update::Id());
                    package = session / L"package";
                    auto archive = session / update::Path(release.ArchiveName);
                    int last = -1;
                    update::Download(release, archive, stop,
                                     [&](uint64_t done, uint64_t total)
                                     {
                                         auto percent = int(done * 100 / total);
                                         if (last == percent)
                                             return;
                                         last = percent;
                                         dispatcher.TryEnqueue(
                                             [weak, percent]
                                             {
                                                 if (auto self = weak.lock())
                                                 {
                                                     if (self->closing_)
                                                         return;
                                                     self->updateProgress_ = percent;
                                                     self->updateStatus_ =
                                                         percent == 100
                                                             ? "下载完成，正在校验并准备安装…"
                                                             : "正在下载更新：" + std::to_string(percent) + "%";
                                                     self->RefreshUpdateView();
                                                 }
                                             });
                                     });
                    update::Extract(release, archive, package, stop);
                    update::CheckStop(stop);
                    request = update::PrepareLaunch(installation, package, data);
                    update::CheckStop(stop);
                }
                catch (std::exception const &e)
                {
                    error = e.what();
                    if (!session.empty())
                    {
                        std::error_code ec;
                        fs::remove_all(session, ec);
                    }
                    package.clear();
                    request.clear();
                }
                dispatcher.TryEnqueue(
                    [weak, package, request, error]
                    {
                        auto self = weak.lock();
                        if (!self || self->closing_)
                            return;
                        self->updateBusy_ = false;
                        self->updatePackage_ = package;
                        self->updateRequest_ = request;
                        self->updateStatus_ =
                            error.empty() ? "校验完成。点击“重启并安装”后更新程序，管理数据与用户文件保留。" : error;
                        self->RefreshUpdateView();
                    });
            });
    }

    void NativeWindow::LaunchPendingUpdate()
    {
        if (!installRequested_)
            return;
        if (updateRequest_.empty())
            throw std::runtime_error("更新包尚未准备完成。");
        auto process = update::LaunchUpdater(updateRequest_);
        installRequested_ = false;
    }
}
