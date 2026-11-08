using System.Reflection;
using System.Security.Cryptography;
using HS2Manager.App;
using HS2Manager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

internal static class CardDeletionUiChecks
{
    internal static async Task RunAsync(string root)
    {
        string output = Path.Combine(root, "artifacts", "winui-delete-checks", Guid.NewGuid().ToString("N"));
        Console.WriteLine("WinUI deletion output: " + output);
        string game = Path.Combine(output, "测试游戏"), folder = Path.Combine(game, "UserData", "chara", "female");
        Directory.CreateDirectory(folder);
        using var model = new MainViewModel(Path.Combine(output, "Data"), new AppSettings { GamePath = game, Theme = "Light", WindowWidth = 1100, WindowHeight = 700 });
        ThemeService.Apply(model.Settings.Theme);
        var window = new MainWindow(model); window.Activate();
        try
        {
            await UiTest.WaitUntil(() => !model.IsBusy && model.Status.StartsWith("扫描完成："), "Deletion window initialization failed");
            var canvas = (FrameworkElement)window.Content;
            var list = (GridView)canvas.FindName("CardList");
            var button = (Button)canvas.FindName("DeleteCardsButton");
            var menu = (MenuFlyout)list.ContextFlyout;
            var menuItem = menu.Items.OfType<MenuFlyoutItem>().Single(i => i.Text == "永久删除…");
            var search = (TextBox)canvas.FindName("SearchBox");
            async Task<CardRecord> AddCard(string name, bool favorite = false)
            {
                var card = CardExportFixtures.Card(Path.Combine(folder, name + ".png"), name, 1);
                card.Root = folder; card.Favorite = favorite; card.Tags = ["删除检查"]; card.Notes = "删除备注";
                card.CoverFile = Path.Combine("Covers", card.Id + ".png");
                await CardParser.CopyCoverAsync(card.Path, card.PngLength, Path.Combine(model.DataDirectory, card.CoverFile), default);
                model.Repository.Save(card); return card;
            }
            var first = await AddCard("删除甲", true); var second = await AddCard("删除乙", true);
            var locked = await AddCard("占用卡"); var other = await AddCard("批量可删");
            File.Copy(first.Path, second.Path, true);
            var info = new FileInfo(second.Path); second.Size = info.Length; second.ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks;
            first.Hash = second.Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first.Path)));
            model.Repository.Save(first); model.Repository.Save(second); model.ReloadCards();
            await UiTest.WaitUntil(() => model.VisibleCards.Count == 4 && !model.HasSelection, "Deletion fixtures did not load");
            Require(!button.IsEnabled && model.DuplicateCount == 2 && model.FavoriteCount == 2, "delete is disabled without selection and fixture counts are correct");
            void Select(params CardRecord[] cards)
            {
                list.SelectedItems.Clear();
                foreach (var card in cards) list.SelectedItems.Add(model.VisibleCards.Single(c => c.Record.Id == card.Id));
                button.Focus(FocusState.Programmatic);
            }
            Task<bool> Key(bool shift) => (Task<bool>)typeof(MainWindow).GetMethod("HandleDeleteKeyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [shift])!;
            async Task<ContentDialog> Dialog(string title)
            {
                await UiTest.WaitUntil(() => UiTest.OpenDialog(canvas.XamlRoot)?.Title?.ToString() == title, "Dialog missing: " + title);
                return UiTest.OpenDialog(canvas.XamlRoot)!;
            }
            async Task CloseDialog(string text = "取消")
            {
                var dialog = UiTest.OpenDialog(canvas.XamlRoot)!;
                await UiTest.WaitUntil(() => UiTest.Descendants<Button>(dialog).Any(b => b.Content?.ToString() == text && b.IsLoaded), "Dialog button missing: " + text);
                UiTest.Invoke(UiTest.Descendants<Button>(dialog).First(b => b.Content?.ToString() == text));
                await UiTest.WaitUntil(() => !ReferenceEquals(UiTest.OpenDialog(canvas.XamlRoot), dialog) &&
                    (UiTest.OpenDialog(canvas.XamlRoot) != null || !Dialogs.IsOpen), "Dialog did not close");
            }
            async Task FinishDeletion()
            {
                await CloseDialog("永久删除");
                await Dialog("永久删除结果");
                await UiTest.WaitUntil(() => !model.IsBusy, "Deletion did not finish");
            }
            async Task MenuDelete()
            {
                menu.ShowAt(list); await UiTest.WaitUntil(() => menuItem.IsLoaded, "Delete menu did not open");
                Require(menuItem.IsEnabled, "right-click delete follows selection state");
                ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(menuItem).GetPattern(PatternInterface.Invoke)).Invoke();
                menu.Hide(); await Dialog("永久删除人物卡");
            }

            Select(first, second);
            foreach (string theme in new[] { "Light", "Dark" })
            {
                ThemeService.Apply(theme); UiTest.Invoke(button);
                var confirm = await Dialog("永久删除人物卡");
                var text = string.Join("\n", UiTest.Descendants<TextBlock>(confirm).Select(t => t.Text));
                Require(confirm.DefaultButton == ContentDialogButton.Close && confirm.PrimaryButtonText == "永久删除" && confirm.CloseButtonText == "取消" &&
                    text.Contains("2 项") && text.Contains(first.Path) && text.Contains(second.Path) && text.Contains("不经过回收站，无法恢复"), "confirmation lists captured file paths and defaults to cancel: " + theme);
                await Task.Delay(180); await UiTest.CaptureAsync(confirm, Path.Combine(output, "delete-confirm-" + theme + ".png"));
                await CloseDialog();
            }
            ThemeService.Apply("Light");
            Require(File.Exists(first.Path) && File.Exists(second.Path) && model.Files.Operations().Count == 0, "cancelling confirmation makes no file or journal changes");

            search.Focus(FocusState.Programmatic);
            Require(!await Key(true) && !await Key(false) && !Dialogs.IsOpen, "text editing suppresses both Delete shortcuts");
            button.Focus(FocusState.Programmatic);
            var unrelated = Dialogs.Confirm(window, "其他弹窗", "快捷键不得穿透。"); await Dialog("其他弹窗");
            Require(!await Key(true) && !await Key(false), "other dialogs suppress Delete shortcuts");
            await CloseDialog(); await unrelated;
            var release = new TaskCompletionSource();
            var busy = model.RunWork("删除快捷键忙碌检查", async (_, _) => { await release.Task; return "已完成"; });
            try { Require(!button.IsEnabled && !await Key(true) && !await Key(false), "busy work disables deletion"); }
            finally { release.SetResult(); await busy; }
            await UiTest.WaitUntil(() => model.SelectionCount == 2, "Busy check did not restore selection");
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsPreview))!.SetValue(model, true);
            try { Require(!await Key(true) && !await Key(false), "preview suppresses both Delete shortcuts without starting a preview process"); }
            finally { typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsPreview))!.SetValue(model, false); }

            Select(first); var shiftDelete = Key(true); await Dialog("永久删除人物卡");
            Select(second); // Programmatic selection changes while the modal is open must not change the confirmed target.
            await FinishDeletion(); await shiftDelete;
            Require(!File.Exists(first.Path) && File.Exists(second.Path) && model.Repository.FindById(first.Id) == null &&
                model.TotalCount == 3 && model.FavoriteCount == 1 && model.DuplicateCount == 0 && model.Status.Contains("完成 1 项，失败 0 项，未处理 0 项"), "Shift+Delete uses the captured selection and updates counts");
            await CloseDialog("关闭");

            Select(second); var deleteKey = Key(false); await Dialog("移入可恢复隔离区"); await CloseDialog("确定"); await deleteKey;
            Require(model.TrashCount == 1 && !File.Exists(second.Path) && model.Repository.FindById(second.Id)!.Availability == FileAvailability.Quarantined, "Delete still moves the card into recoverable quarantine");
            model.Navigate("trash"); Select(second); Require(!await Key(false), "plain Delete in quarantine does not permanently delete");
            await model.RestoreAsync(); Require(File.Exists(second.Path) && model.TrashCount == 0, "restore still returns the original card");
            model.Navigate("all"); Select(second); await model.QuarantineAsync();
            model.Navigate("trash"); Select(second); shiftDelete = Key(true); await Dialog("永久删除人物卡"); await FinishDeletion(); await shiftDelete;
            Require(model.TrashCount == 0 && model.SelectionCount == 0 && model.SelectedCard == null && model.Empty, "permanent deletion in quarantine clears selection and details");
            await CloseDialog("关闭");

            model.Navigate("all"); Select(locked, other);
            using (var stream = new FileStream(locked.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await MenuDelete(); await FinishDeletion();
                await UiTest.WaitUntil(() => model.SelectionCount == 1, "Failed item selection was not restored");
                Require(File.Exists(locked.Path) && !File.Exists(other.Path) && model.SelectedCard?.Record.Id == locked.Id &&
                    model.Status.Contains("完成 1 项，失败 1 项，未处理 0 项"), "partial failure preserves the failed selection and removes successful items");
                await Task.Delay(180);
                await UiTest.CaptureAsync(UiTest.OpenDialog(canvas.XamlRoot)!, Path.Combine(output, "delete-partial-result.png"));
                await CloseDialog("关闭");
                await Task.Delay(250); await UiTest.CaptureAsync(canvas, Path.Combine(output, "delete-library.png"));
            }
            var issue = await AddCard("异常项"); issue.Status = CardStatus.Corrupt; model.Repository.Save(issue); model.ReloadCards();
            model.Navigate("issues"); Select(issue); UiTest.Invoke(button); await Dialog("永久删除人物卡"); await FinishDeletion();
            Require(model.IssueCount == 0 && model.Empty && model.SelectedCard == null && !File.Exists(issue.Path), "the issues view supports permanent deletion and refreshes its counts");
            await CloseDialog("关闭");
            Require(!button.IsEnabled && !await Key(true), "deletion is disabled after the last visible item is removed");
            Console.WriteLine("ALL WINUI CARD DELETION CHECKS PASSED");
        }
        finally
        {
            var dialog = UiTest.OpenDialog(((FrameworkElement)window.Content).XamlRoot); dialog?.Hide();
            await UiTest.CloseAsync(window);
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception("FAIL WinUI deletion: " + message); Console.WriteLine("PASS WinUI deletion: " + message); }
}
