#include "Preview/NativePreview.h"
#include "Application/NativeWindow.h"
#include <Shobjidl.h>
#include <fstream>

using namespace winrt;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace hs2::ui
{
    namespace
    {
        TextBlock ExportText(std::string const &text)
        {
            TextBlock control;
            control.Text(to_hstring(text));
            control.TextWrapping(TextWrapping::Wrap);
            return control;
        }

        struct ExportDialogState
        {
            ModelExportOptions Options;
            ComboBox Target, Clothing;
            CheckBox Accessories;
            TextBox ToolPath;
            StackPanel ToolPanel;
            TextBlock Description;
            InfoBar Error;
            bool Changing = false;

            void Refresh()
            {
                Changing = true;
                Options.Target = static_cast<ModelExportTarget>(Target.SelectedIndex());
                auto target = Options.Target;
                ToolPanel.Visibility(target == ModelExportTarget::GenericFbx ? Visibility::Collapsed
                                                                             : Visibility::Visible);
                ToolPath.Header(
                    box_value(target == ModelExportTarget::Blender ? L"Blender 程序" : L"Unreal Editor 程序"));
                auto path = target == ModelExportTarget::Blender ? Options.BlenderPath : Options.UnrealEditorPath;
                if (path.empty() && target != ModelExportTarget::GenericFbx)
                    path = ModelExportTools::Detect(target);
                ToolPath.Text(to_hstring(path));
                Description.Text(target == ModelExportTarget::Blender
                                     ? L"生成可直接打开的 .blend，包含可编辑材质、打包贴图、灯光和相机。使用参考姿态。"
                                 : target == ModelExportTarget::Unreal
                                     ? L"生成独立 Unreal 项目，包含骨骼模型、材质和默认预览关卡。使用参考姿态。"
                                     : L"生成通用 FBX、PBR 贴图和骨骼映射。材质可在目标软件中配置。");
                Error.IsOpen(false);
                Changing = false;
            }

            ModelExportOptions Capture() const
            {
                auto options = Options;
                options.Clothing = Clothing.SelectedIndex();
                options.Accessories = unbox_value_or<bool>(Accessories.IsChecked(), false);
                if (options.Target == ModelExportTarget::Blender)
                    options.BlenderPath = to_string(ToolPath.Text());
                else if (options.Target == ModelExportTarget::Unreal)
                    options.UnrealEditorPath = to_string(ToolPath.Text());
                return options;
            }
        };
    } // 命名空间

    fire_and_forget NativeWindow::ExportModels(std::vector<ModelExportSource> sources, int clothing, bool accessories)
    {
        auto lifetime = shared_from_this();
        if (dialogOpen_ || busy_ || sources.empty())
            co_return;
        try
        {
            auto form = std::make_shared<ExportDialogState>();
            auto &options = form->Options;
            options.Clothing = clothing;
            options.Accessories = accessories;
            options.BlenderPath = settings_.BlenderPath;
            options.UnrealEditorPath = settings_.UnrealEditorPath;
            if (sources.size() == 1 && preview_)
                options.View = preview_->ExportView();
            auto weak = std::weak_ptr<ExportDialogState>(form);
            auto &target = form->Target;
            target.Header(box_value(L"导出类型"));
            target.HorizontalAlignment(HorizontalAlignment::Stretch);
            for (auto type : {ModelExportTarget::GenericFbx, ModelExportTarget::Blender, ModelExportTarget::Unreal})
                target.Items().Append(box_value(to_hstring(ModelExportTools::Name(type))));
            target.SelectedIndex(0);
            auto &clothes = form->Clothing;
            clothes.Header(box_value(L"导出服装"));
            clothes.HorizontalAlignment(HorizontalAlignment::Stretch);
            for (auto label : {L"完整着装", L"内着", L"不穿衣物"})
                clothes.Items().Append(box_value(label));
            clothes.SelectedIndex(std::clamp(clothing, 0, 2));
            form->Accessories.Content(box_value(L"包含配饰"));
            form->Accessories.IsChecked(accessories);
            form->Description.TextWrapping(TextWrapping::Wrap);
            form->ToolPath.PlaceholderText(L"自动检测，或选择已安装的软件");
            form->ToolPanel.Spacing(8);
            form->ToolPanel.Children().Append(form->ToolPath);
            Button browse;
            browse.Content(box_value(L"选择程序…"));
            form->ToolPanel.Children().Append(browse);
            form->ToolPath.TextChanged(
                [weak](auto &&, auto &&)
                {
                    if (auto f = weak.lock())
                        if (!f->Changing)
                        {
                            if (f->Options.Target == ModelExportTarget::Blender)
                                f->Options.BlenderPath = to_string(f->ToolPath.Text());
                            else if (f->Options.Target == ModelExportTarget::Unreal)
                                f->Options.UnrealEditorPath = to_string(f->ToolPath.Text());
                        }
                });
            target.SelectionChanged(
                [weak](auto &&, auto &&)
                {
                    if (auto f = weak.lock())
                        f->Refresh();
                });
            browse.Click(
                [weak, owner = weak_from_this()](auto &&, auto &&) -> fire_and_forget
                {
                    auto self = owner.lock();
                    auto f = weak.lock();
                    if (!self || !f)
                        co_return;
                    try
                    {
                        winrt::Windows::Storage::Pickers::FileOpenPicker picker;
                        picker.FileTypeFilter().Append(L".exe");
                        check_hresult(picker.as<IInitializeWithWindow>()->Initialize(self->Handle()));
                        auto file = co_await picker.PickSingleFileAsync();
                        if (file)
                            f->ToolPath.Text(file.Path());
                    }
                    catch (hresult_error const &e)
                    {
                        f->Error.Message(e.message());
                        f->Error.IsOpen(true);
                    }
                });
            form->Error.Severity(InfoBarSeverity::Error);
            form->Error.IsOpen(false);
            form->Refresh();
            StackPanel panel;
            panel.Spacing(14);
            panel.MinWidth(340);
            panel.MaxWidth(460);
            panel.Children().Append(
                ExportText("导出 " + std::to_string(sources.size()) + " 个角色，保留骨架、蒙皮和表情。"));
            panel.Children().Append(target);
            panel.Children().Append(form->Description);
            panel.Children().Append(form->ToolPanel);
            if (std::any_of(sources.begin(), sources.end(),
                            [](auto const &source)
                            {
                                return source.Draft.has_value();
                            }))
                panel.Children().Append(ExportText("包含点击导出时的未保存修改。"));
            panel.Children().Append(clothes);
            panel.Children().Append(form->Accessories);
            panel.Children().Append(ExportText("每个角色独立成包。跨软件材质和后处理存在近似，差异与缺失会列入报告。"));
            panel.Children().Append(form->Error);
            ScrollViewer scroll;
            scroll.Content(panel);
            scroll.MaxHeight(std::max(250.0, root_.XamlRoot().Size().Height - 240.0));
            scroll.VerticalScrollBarVisibility(ScrollBarVisibility::Auto);
            scroll.HorizontalScrollBarVisibility(ScrollBarVisibility::Disabled);
            ContentDialog dialog;
            dialog.XamlRoot(root_.XamlRoot());
            dialog.RequestedTheme(root_.RequestedTheme());
            dialog.Title(box_value(L"导出 3D 模型"));
            dialog.Content(scroll);
            dialog.PrimaryButtonText(L"选择导出位置");
            dialog.CloseButtonText(L"取消");
            dialog.PrimaryButtonClick(
                [weak](auto &&, ContentDialogButtonClickEventArgs const &args)
                {
                    if (auto f = weak.lock())
                        try
                        {
                            ModelExportTools::Resolve(f->Capture());
                        }
                        catch (std::exception const &e)
                        {
                            args.Cancel(true);
                            f->Error.Message(to_hstring(e.what()));
                            f->Error.IsOpen(true);
                        }
                });
            dialogOpen_ = true;
            auto result = co_await dialog.ShowAsync();
            dialogOpen_ = false;
            if (result != ContentDialogResult::Primary)
                co_return;
            auto captured = form->Capture();
            auto folder = co_await PickFolder(L"选择 3D 模型导出位置（游戏目录之外）");
            if (folder.empty() || closing_)
                co_return;
            settings_.BlenderPath = captured.BlenderPath;
            settings_.UnrealEditorPath = captured.UnrealEditorPath;
            settings_.Save(dataDirectory_);
            auto game = Path(settings_.GamePath), data = dataDirectory_, output = fs::path(folder.c_str());
            auto queue = dispatcher_;
            auto owner = weak_from_this();
            Work("导出 " + ModelExportTools::Name(captured.Target),
                 [sources = std::move(sources), game, data, output, captured, queue, owner](auto token, auto progress)
                 {
                     ModelExportService service(ExecutableDirectory());
                     auto report = service.Export(sources, game, data, output, captured, progress, token);
                     std::string message = report.Summary() + "\n\n报告：" + report.ReportPath;
                     for (auto const &item : report.Items)
                         message += "\n\n" + item.Source + "\n" + item.Directory + "\n" + item.Message;
                     std::error_code error;
                     fs::create_directories(data / L"Logs", error);
                     std::ofstream(data / L"Logs" / L"native-model-export.log", std::ios::app) << message << '\n';
                     queue.TryEnqueue(
                         [owner, message]
                         {
                             if (auto self = owner.lock())
                                 if (!self->closing_)
                                     self->ShowReport(L"3D 模型导出", to_hstring(message));
                         });
                     return report.Summary();
                 });
        }
        catch (hresult_error const &e)
        {
            dialogOpen_ = false;
            Report(to_string(e.message()));
        }
        catch (std::exception const &e)
        {
            dialogOpen_ = false;
            Report(e.what());
        }
    }

    fire_and_forget NativeWindow::ShowReport(hstring title, hstring message)
    {
        auto lifetime = shared_from_this();
        if (dialogOpen_ || closing_)
        {
            Report(to_string(message));
            co_return;
        }
        try
        {
            TextBox text;
            text.Text(message);
            text.AcceptsReturn(true);
            text.IsReadOnly(true);
            text.TextWrapping(TextWrapping::Wrap);
            text.MaxHeight(460);
            ScrollViewer::SetVerticalScrollBarVisibility(text, ScrollBarVisibility::Auto);
            ContentDialog dialog;
            dialog.XamlRoot(root_.XamlRoot());
            dialog.RequestedTheme(root_.RequestedTheme());
            dialog.Title(box_value(title));
            dialog.Content(text);
            dialog.CloseButtonText(L"关闭");
            dialogOpen_ = true;
            co_await dialog.ShowAsync();
            dialogOpen_ = false;
        }
        catch (hresult_error const &e)
        {
            dialogOpen_ = false;
            Report(to_string(e.message()));
        }
    }
} // 命名空间 hs2::ui
