using HS2Manager.App;
using HS2Manager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

internal static class PreviewRailUiChecks
{
    public static async Task RunAsync(string root)
    {
        string output = Path.Combine(root, "artifacts", "preview-rail-checks", Guid.NewGuid().ToString("N"));
        using var model = new MainViewModel(Path.Combine(output, "Data"), new AppSettings { GamePath = Path.Combine(output, "NoGame") });
        // Exercise navigation with isolated, unavailable resource fixtures.
        var window = new MainWindow(model); window.Activate();
        try
        {
            await UiTest.WaitUntil(() => !model.IsBusy && model.Status.StartsWith("扫描完成："), "Window initialization failed");
            for (int i = 0; i < 80; i++) model.Repository.Save(new CardRecord
            {
                Id = "rail" + i, Name = "同名角色", Status = CardStatus.Valid,
                Path = Path.Combine(output, "cards", $"角色{i:000}.png"), Root = Path.Combine(output, "cards")
            });
            model.ReloadCards();
            var canvas = (FrameworkElement)window.Content;
            var library = (GridView)canvas.FindName("CardList");
            var rail = (ListView)canvas.FindName("PreviewRail");
            int loads = 0;
            model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.PreviewFilePath)) loads++; };

            var target = model.VisibleCards[70];
            library.SelectedItem = target;
            canvas.UpdateLayout();
            await UiTest.WaitUntil(() => UiTest.Descendants<Button>(canvas).Any(b => AutomationProperties.GetName(b) == "打开 3D 预览" && b.IsEnabled),
                "Details preview button was not ready");
            UiTest.Invoke(UiTest.Descendants<Button>(canvas).Single(b => AutomationProperties.GetName(b) == "打开 3D 预览"));
            await CheckTarget(target, 1, "details button");

            await model.ClosePreviewAsync();
            target = model.VisibleCards[2];
            library.SelectedItem = target;
            canvas.UpdateLayout(); library.ScrollIntoView(target);
            await UiTest.WaitUntil(() => library.ContainerFromItem(target) is GridViewItem, "Library card was not realized");
            await (Task)typeof(MainWindow).GetMethod("PreviewDoubleTappedCardAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [library.ContainerFromItem(target)])!;
            await CheckTarget(target, 2, "reopen by double tap");

            var scroll = UiTest.Descendants<ScrollViewer>(rail).Single();
            double offset = scroll.VerticalOffset;
            target = model.VisibleCards[4];
            if (!IsVisible(target)) throw new Exception("Rail switch fixture must already be visible below the top item");
            rail.SelectedItem = target;
            await CheckTarget(target, 3, "switch from rail");
            await CheckScrollPosition(offset, "switch from rail");

            await model.RetryPreviewAsync();
            await CheckTarget(target, 4, "retry current preview");
            await CheckScrollPosition(offset, "retry current preview");

            model.ShowPreviewRail = false;
            canvas.UpdateLayout();
            await Task.Delay(100);
            model.ShowPreviewRail = true;
            await CheckTarget(target, 4, "show hidden rail");
            await CheckScrollPosition(offset, "show hidden rail");

            string targetId = target.Record.Id;
            model.ReloadCards();
            target = model.VisibleCards.Single(c => c.Record.Id == targetId);
            await CheckTarget(target, 4, "replace list items", reveal: false);

            var first = model.OpenPreviewAsync(model.VisibleCards[10].Record);
            target = model.VisibleCards[60];
            var latest = model.OpenPreviewAsync(target.Record);
            await Task.WhenAll(first, latest);
            await CheckTarget(target, 6, "rapid preview requests", reveal: false);
            Console.WriteLine("PASS preview rail reveals cards on library entry only; rail switches, retry and hide/show preserve scroll position; selection stays synchronized without duplicate loads");

            async Task CheckTarget(CardItemViewModel card, int expectedLoads, string scenario, bool reveal = true)
            {
                await UiTest.WaitUntil(() => model.IsPreview && model.PreviewFilePath == card.Record.Path && !model.PreviewBusy,
                    scenario + ": preview did not finish");
                await UiTest.WaitUntil(() => ReferenceEquals(rail.SelectedItem, card) && (!reveal || IsVisible(card)),
                    scenario + ": preview rail did not select the current card or reveal it when requested");
                if (loads != expectedLoads) throw new Exception($"{scenario}: expected {expectedLoads} loads, got {loads}");
            }
            async Task CheckScrollPosition(double expected, string scenario)
            {
                // Allow queued layout/scroll requests to run before checking the user's position.
                await Task.Delay(150);
                if (Math.Abs(scroll.VerticalOffset - expected) > 1)
                    throw new Exception($"{scenario}: rail scrolled from {expected} to {scroll.VerticalOffset}");
            }
            bool IsVisible(CardItemViewModel card)
            {
                if (rail.ContainerFromItem(card) is not ListViewItem item || item.ActualHeight <= 0) return false;
                var bounds = item.TransformToVisual(rail).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
                return bounds.Top >= -1 && bounds.Bottom <= rail.ActualHeight + 1;
            }
        }
        finally { await UiTest.CloseAsync(window); }
    }
}
