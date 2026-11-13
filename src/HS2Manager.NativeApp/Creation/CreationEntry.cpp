#include "Creation/NativeCreation.h"
#include "Editor/NativeEditor.h"
#include "Preview/NativePreview.h"
#include <Shobjidl.h>
using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    fire_and_forget NativeWindow::OpenCreation(std::string mode, fs::path source)
    {
        auto lifetime = shared_from_this();
        if (busy_ || editor_ || closing_ || dialogOpen_)
            co_return;
        std::shared_ptr<creation::CreationProject> project;
        std::shared_ptr<creation::CreationService> service;
        std::string error;
        apartment_context ui;
        try
        {
            service = std::make_shared<creation::CreationService>(Path(settings_.GamePath), dataDirectory_,
                                                                  ExecutableDirectory());
            dialogOpen_ = true;
            ContentDialog dialog;
            dialog.XamlRoot(root_.XamlRoot());
            dialog.RequestedTheme(root_.ActualTheme());
            dialog.PrimaryButtonText(L"创建");
            dialog.CloseButtonText(L"取消");
            dialog.DefaultButton(ContentDialogButton::Primary);
            StackPanel content;
            content.Spacing(10);
            TextBox name;
            name.Header(box_value(L"工程名称"));
            name.Text(mode == "mod" ? L"新 Mod" : L"新角色");
            ComboBox choice;
            dialog.Title(box_value(mode == "mod" ? L"新建mod工程" : L"新建角色工程"));
            content.Children().Append(name);
            if (source.empty())
            {
                choice.Header(box_value(L"起始角色"));
                for (auto label : {L"默认女性", L"默认男性", L"选择已有角色卡"})
                    choice.Items().Append(box_value(label));
                choice.SelectedIndex(0);
                content.Children().Append(choice);
            }
            else
            {
                TextBlock text;
                text.Text(to_hstring("复制来源：" + Utf8(source.filename())));
                text.TextWrapping(TextWrapping::Wrap);
                content.Children().Append(text);
            }
            TextBlock hint;
            hint.Text(L"默认角色读取本机游戏原版预设。资源缺失时，可选择已有卡继续创建。");
            hint.TextWrapping(TextWrapping::Wrap);
            content.Children().Append(hint);
            dialog.Content(content);
            auto result = co_await dialog.ShowAsync();
            dialogOpen_ = false;
            if (result != ContentDialogResult::Primary || closing_)
                co_return;
            if (to_string(name.Text()).find_first_not_of(" \t\r\n") == std::string::npos)
                throw std::runtime_error("工程名称不能为空");
            int gender = 1;
            if (source.empty())
            {
                gender = choice.SelectedIndex() == 1 ? 0 : 1;
                if (choice.SelectedIndex() == 2)
                {
                    winrt::Windows::Storage::Pickers::FileOpenPicker picker;
                    picker.as<IInitializeWithWindow>()->Initialize(Handle());
                    picker.FileTypeFilter().Append(L".png");
                    dialogOpen_ = true;
                    auto file = co_await picker.PickSingleFileAsync();
                    dialogOpen_ = false;
                    if (!file || closing_)
                        co_return;
                    source = fs::path(file.Path().c_str());
                }
            }
            SetBusy(true);
            model_->Put(L"WorkLabel", L"正在创建工程…");
            model_->Put(L"ProgressIndeterminate", true);
            auto projectName = to_string(name.Text());
            co_await resume_background();
            try
            {
                service->RecoverInstallations();
                project = service->Create(projectName, mode == "mod" ? "mod" : "character", gender,
                                          source.empty() ? std::nullopt : std::optional<fs::path>(source));
            }
            catch (const std::exception &e)
            {
                error = e.what();
            }
            co_await ui;
            if (project && !closing_)
            {
                auto previous = std::exchange(preview_, nullptr);
                if (previous)
                    co_await previous->StopAsync();
                if (!closing_)
                    creation_ = NativeCreation::Open(lifetime, project, service);
            }
        }
        catch (const std::exception &e)
        {
            error = e.what();
        }
        catch (const hresult_error &e)
        {
            error = to_string(e.message());
        }
        dialogOpen_ = false;
        SetBusy(false);
        if (closing_)
            RequestClose();
        else if (!error.empty())
            ShowReport(L"创建工程失败", to_hstring(error));
    }
}
