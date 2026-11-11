#include "CardExport.h"
#include "NativeWindow.h"
using namespace winrt;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Controls;
namespace hs2::ui
{
fire_and_forget NativeWindow::ExportCards(std::vector<CardRecord> cards)
{
    auto lifetime = shared_from_this();
    if (dialogOpen_ || busy_)
        co_return;
    try
    {
        StackPanel panel;
        panel.Spacing(12);
        panel.Width(420);
        TextBlock info;
        info.Text(to_hstring("导出 " + std::to_string(cards.size()) + " 张人物卡"));
        info.TextWrapping(TextWrapping::Wrap);
        panel.Children().Append(info);
        ComboBox level;
        level.HorizontalAlignment(HorizontalAlignment::Stretch);
        for (int i = 1; i <= 4; ++i)
            level.Items().Append(box_value(to_hstring(ExportLevelLabel(static_cast<CardExportLevel>(i)))));
        level.SelectedIndex(static_cast<int>(settings_.LastCardExportLevel) - 1);
        panel.Children().Append(level);
        TextBlock scope;
        scope.TextWrapping(TextWrapping::Wrap);
        scope.Text(to_hstring(ExportLevelContents(settings_.LastCardExportLevel) + "\n" +
                              ExportLevelScope(settings_.LastCardExportLevel)));
        panel.Children().Append(scope);
        auto weakScope = make_weak(scope);
        level.SelectionChanged([weakScope](auto const &sender, auto &&) {
            auto choice = sender.template as<ComboBox>().SelectedIndex() + 1;
            if (choice < 1 || choice > 4)
                return;
            if (auto text = weakScope.get())
                text.Text(to_hstring(ExportLevelContents(static_cast<CardExportLevel>(choice)) + "\n" +
                                     ExportLevelScope(static_cast<CardExportLevel>(choice))));
        });
        ComboBox mode;
        mode.HorizontalAlignment(HorizontalAlignment::Stretch);
        mode.Items().Append(box_value(L"每张人物卡独立目录"));
        mode.Items().Append(box_value(L"合并为一个目录，依赖去重"));
        mode.SelectedIndex(0);
        panel.Children().Append(mode);
        ContentDialog dialog;
        dialog.XamlRoot(root_.XamlRoot());
        dialog.RequestedTheme(root_.RequestedTheme());
        dialog.Title(box_value(L"导出人物卡与依赖"));
        dialog.Content(panel);
        dialog.PrimaryButtonText(L"选择导出目录");
        dialog.CloseButtonText(L"取消");
        dialogOpen_ = true;
        auto result = co_await dialog.ShowAsync();
        dialogOpen_ = false;
        if (result != ContentDialogResult::Primary)
            co_return;
        auto folder = co_await PickFolder(L"选择导出目录");
        if (folder.empty())
            co_return;
        CardExportOptions options;
        options.Mode = mode.SelectedIndex() ? CardExportMode::Merged : CardExportMode::PerCard;
        options.Level = static_cast<CardExportLevel>(level.SelectedIndex() + 1);
        auto game = Path(settings_.GamePath), output = fs::path(folder.c_str());
        CardExportService::ValidateOutput(game, output);
        settings_.LastCardExportLevel = options.Level;
        settings_.Save(dataDirectory_);
        Work("导出人物卡与依赖", [this, cards = std::move(cards), game, output, options](auto token, auto progress) {
            CardExportService service(*repository_, ExecutableDirectory() / L"classdata.tpk");
            auto plan = service.BuildPlan(cards, game, output, options, progress, token);
            auto report = service.Export(plan, progress, token);
            return report.Summary() + (report.ReportPath.empty() ? "" : "\n报告：" + report.ReportPath);
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
} // namespace hs2::ui
