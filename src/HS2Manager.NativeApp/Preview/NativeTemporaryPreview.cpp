#include "Application/NativeWindow.h"
#include "Preview/NativePreview.h"
#include "Archive/PackageArchive.h"
#include <Shobjidl.h>
#include <algorithm>

using namespace winrt;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;
using namespace winrt::Windows::Foundation;

namespace hs2::ui
{
    namespace
    {
        struct Password
        {
            std::optional<std::wstring> Value;

            void Clear()
            {
                if (Value && !Value->empty())
                    SecureZeroMemory(Value->data(), Value->size() * sizeof(wchar_t));
                Value.reset();
            }

            ~Password()
            {
                Clear();
            }
        };
    }

    void NativeWindow::CancelTemporaryPreview()
    {
        temporaryCancellation_.request_stop();
        if (temporaryPasswordDialog_)
            temporaryPasswordDialog_.Hide();
    }

    void NativeWindow::ClosePreview()
    {
        CancelTemporaryPreview();
        ++temporaryGeneration_;
        // 先解除交换链并等待工作线程结束，再释放目录租约。
        preview_.reset();
        updating_ = true;
        model_->Set(L"PreviewCards", model_->Get(L"VisibleCards"), false);
        updating_ = false;
        temporaryPreview_.reset();
        model_->Put(L"IsTemporaryPreview", false);
        model_->Put(L"IsPreview", false);
    }

    void NativeWindow::LoadTemporaryCard(const std::string &id)
    {
        if (!temporaryPreview_ || temporaryOpening_ || closing_)
            return;
        auto found = std::find_if(temporaryPreview_->Cards.begin(), temporaryPreview_->Cards.end(),
                                  [&](auto &entry)
                                  {
                                      return entry.Card.Id == id;
                                  });
        if (found == temporaryPreview_->Cards.end() || (preview_ && preview_->Card().Id == id))
            return;
        auto first = !preview_;
        if (first)
            preview_ = NativePreview::Create(shared_from_this());
        preview_->Load(found->Card, root_, false, {}, {}, false, found->Resources);
        if (first)
            model_->Put(L"ShowPreviewRail", temporaryPreview_->Cards.size() > 1);
        model_->Text(L"PreviewFileName", Utf8(found->Relative));
        model_->Text(L"PreviewFilePath", Utf8(temporaryPreview_->Source) + " · " + Utf8(found->Relative));
        preview_->SyncRail(true);
    }

    fire_and_forget NativeWindow::OpenTemporaryPreview(fs::path source)
    {
        auto lifetime = shared_from_this();
        if (busy_ || editor_ || closing_ || dialogOpen_)
            co_return;
        uint64_t generation = 0;
        bool started = false;
        Password password;
        try
        {
            if (source.empty())
            {
                winrt::Windows::Storage::Pickers::FileOpenPicker picker;
                picker.ViewMode(winrt::Windows::Storage::Pickers::PickerViewMode::Thumbnail);
                for (auto type : {L".png", L".zip", L".7z", L".rar"})
                    picker.FileTypeFilter().Append(type);
                picker.CommitButtonText(L"临时预览");
                check_hresult(picker.as<IInitializeWithWindow>()->Initialize(Handle()));
                dialogOpen_ = true;
                auto file = co_await picker.PickSingleFileAsync();
                dialogOpen_ = false;
                if (!file || closing_)
                    co_return;
                source = fs::path(file.Path().c_str());
            }
            if (busy_ || editor_ || closing_)
                co_return;
            ClosePreview();
            generation = ++temporaryGeneration_;
            temporaryCancellation_ = std::stop_source{};
            auto token = temporaryCancellation_.get_token();
            temporaryOpening_ = true;
            started = true;
            SetBusy(true);
            model_->Put(L"IsTemporaryPreview", true);
            model_->Put(L"IsPreview", true);
            model_->Put(L"ShowPreviewRail", false);
            model_->Put(L"PreviewBusy", true);
            model_->Put(L"CanControlPreview", false);
            model_->Put(L"HasPreviewError", false);
            model_->Put(L"HasPreviewWarnings", false);
            model_->Text(L"PreviewName", Utf8(source.stem()));
            model_->Text(L"PreviewFileName", Utf8(source.filename()));
            model_->Text(L"PreviewFilePath", Utf8(source));
            model_->Text(L"TemporaryPreviewSummary", "临时预览 · " + Utf8(source.filename()));
            model_->Put(L"PreviewStatus", L"准备临时预览…");
            model_->Put(L"WorkLabel", L"准备临时预览");
            model_->Put(L"ProgressIndeterminate", true);
            std::optional<ModIndex> previous;
            try
            {
                if (auto saved = repository_->LoadMetadata("mods-v1"))
                    previous = saved->get<ModIndex>();
            }
            catch (std::exception const &)
            {
            }
            apartment_context ui;
            auto queue = dispatcher_;
            auto data = dataDirectory_;
            auto game = Path(settings_.GamePath);
            auto decoder = ExecutableDirectory() / L"native/win-x64/7z.dll";
            std::weak_ptr<NativeWindow> weak = lifetime;
            Progress progress = [weak, queue, generation](WorkProgress const &value)
            {
                queue.TryEnqueue(
                    [weak, generation, value]
                    {
                        if (auto self = weak.lock())
                            if (self->temporaryOpening_ && self->temporaryGeneration_ == generation)
                            {
                                self->model_->Text(L"PreviewStatus", value.Message);
                                self->model_->Text(L"WorkLabel", value.Message);
                                self->model_->Put(L"ProgressIndeterminate", value.Total <= 0);
                                self->model_->Put(L"ProgressValue",
                                                  value.Total > 0 ? 100.0 * value.Completed / value.Total : 0);
                            }
                    });
            };
            std::shared_ptr<TemporaryPreviewSession> session;
            std::string error;
            while (!token.stop_requested())
            {
                bool needPassword = false;
                co_await resume_background();
                try
                {
                    session = TemporaryPreviewSession::Open(source, game, data, decoder, previous, password.Value,
                                                            progress, token);
                }
                catch (ArchivePasswordRequired const &e)
                {
                    needPassword = true;
                    error = e.what();
                }
                catch (std::exception const &e)
                {
                    error = e.what();
                }
                co_await ui;
                password.Clear();
                if (token.stop_requested() || generation != temporaryGeneration_ || closing_ || !needPassword)
                    break;
                PasswordBox input;
                input.PlaceholderText(L"输入压缩包密码");
                input.Name(L"TemporaryArchivePassword");
                StackPanel content;
                content.Spacing(12);
                TextBlock description;
                description.Text(to_hstring(error));
                description.TextWrapping(TextWrapping::Wrap);
                content.Children().Append(description);
                content.Children().Append(input);
                ContentDialog dialog;
                dialog.XamlRoot(root_.XamlRoot());
                dialog.RequestedTheme(root_.RequestedTheme());
                dialog.Title(box_value(L"压缩包需要密码"));
                dialog.Content(content);
                dialog.PrimaryButtonText(L"继续");
                dialog.CloseButtonText(L"取消");
                dialog.DefaultButton(ContentDialogButton::Primary);
                dialog.Opened(
                    [input](auto &&, auto &&)
                    {
                        input.Focus(FocusState::Programmatic);
                    });
                temporaryPasswordDialog_ = dialog;
                dialogOpen_ = true;
                model_->Put(L"PreviewStatus", L"等待输入压缩包密码");
                auto result = co_await dialog.ShowAsync();
                dialogOpen_ = false;
                temporaryPasswordDialog_ = nullptr;
                if (result == ContentDialogResult::Primary && !token.stop_requested())
                    password.Value = std::wstring(input.Password());
                input.Password(L"");
                if (!password.Value)
                {
                    temporaryCancellation_.request_stop();
                    break;
                }
                error.clear();
            }
            password.Clear();
            temporaryOpening_ = false;
            SetBusy(false);
            if (token.stop_requested() || generation != temporaryGeneration_ || closing_)
            {
                session.reset();
                if (generation == temporaryGeneration_)
                    ClosePreview();
                Report("临时预览已取消，临时内容已清理。");
            }
            else if (!session)
            {
                ClosePreview();
                Report("临时预览未打开：" + error);
                ShowReport(L"临时预览未打开", to_hstring(error));
            }
            else
            {
                temporaryPreview_ = std::move(session);
                auto items = single_threaded_observable_vector<Object>();
                for (auto &entry : temporaryPreview_->Cards)
                {
                    auto item = make_self<Observable>();
                    item->Text(L"Id", entry.Card.Id);
                    item->Text(L"Name", entry.Card.Name);
                    item->Text(L"DetailLine", Utf8(entry.Relative));
                    auto record = make_self<Observable>();
                    record->Text(L"Path", Utf8(source) + " · " + Utf8(entry.Relative));
                    item->Set(L"Record", record.as<Object>(), false);
                    items.Append(item.as<Object>());
                }
                updating_ = true;
                model_->Set(L"PreviewCards", items, false);
                updating_ = false;
                model_->Text(L"TemporaryPreviewSummary", "临时预览 · " + Utf8(source.filename()) + " · " +
                                                             std::to_string(temporaryPreview_->Cards.size()) +
                                                             " 个角色");
                LoadTemporaryCard(temporaryPreview_->Cards.front().Card.Id);
                Report("临时角色及资源已就绪，退出预览后自动清理。");
            }
            if (closing_)
                window_.Close();
        }
        catch (hresult_error const &e)
        {
            dialogOpen_ = false;
            temporaryPasswordDialog_ = nullptr;
            if (started)
            {
                temporaryOpening_ = false;
                SetBusy(false);
                ClosePreview();
            }
            Report("临时预览未打开：" + to_string(e.message()));
            if (closing_)
                window_.Close();
        }
        catch (std::exception const &e)
        {
            dialogOpen_ = false;
            temporaryPasswordDialog_ = nullptr;
            if (started)
            {
                temporaryOpening_ = false;
                SetBusy(false);
                ClosePreview();
            }
            Report("临时预览未打开：" + std::string(e.what()));
            if (closing_)
                window_.Close();
        }
    }

    void NativeWindow::BindTemporaryPreviewDrop(FrameworkElement const &target)
    {
        std::weak_ptr<NativeWindow> weak = shared_from_this();
        target.AllowDrop(true);
        target.DragOver(
            [weak](auto &&, DragEventArgs const &args)
            {
                using namespace winrt::Windows::ApplicationModel::DataTransfer;
                args.Handled(true);
                if (auto self = weak.lock())
                {
                    bool accept = !self->busy_ && !self->editor_ && !self->closing_ && !self->dialogOpen_ &&
                                  args.DataView().Contains(StandardDataFormats::StorageItems());
                    args.AcceptedOperation(accept ? DataPackageOperation::Copy : DataPackageOperation::None);
                    args.DragUIOverride().Caption(L"临时预览角色卡或压缩包");
                    args.DragUIOverride().IsCaptionVisible(true);
                }
            });
        target.Drop(
            [weak](auto &&, DragEventArgs const &args) -> fire_and_forget
            {
                using namespace winrt::Windows::ApplicationModel::DataTransfer;
                // 必须在首次挂起前截断冒泡，避免根窗口同时执行导入。
                args.Handled(true);
                auto deferral = args.GetDeferral();
                if (auto self = weak.lock())
                {
                    try
                    {
                        if (!self->busy_ && !self->editor_ && !self->closing_ && !self->dialogOpen_ &&
                            args.DataView().Contains(StandardDataFormats::StorageItems()))
                        {
                            auto items = co_await args.DataView().GetStorageItemsAsync();
                            if (items.Size() != 1 ||
                                !items.GetAt(0).IsOfType(winrt::Windows::Storage::StorageItemTypes::File))
                                self->Report("临时预览每次接受一个 PNG 角色卡或 ZIP／7Z／RAR 压缩包。");
                            else
                                self->OpenTemporaryPreview(fs::path(items.GetAt(0).Path().c_str()));
                        }
                    }
                    catch (hresult_error const &e)
                    {
                        self->Report(to_string(e.message()));
                    }
                    catch (std::exception const &e)
                    {
                        self->Report(e.what());
                    }
                }
                deferral.Complete();
            });
    }
}
