using Microsoft.UI.Dispatching;

namespace HS2Manager.App;

public static class UiDispatcher
{
    private static DispatcherQueue? queue;
    public static void Initialize() => queue = DispatcherQueue.GetForCurrentThread();
    public static void Post(Action action)
    {
        if (queue is null || !queue.TryEnqueue(() => action()))
            throw new InvalidOperationException("UI dispatcher is unavailable.");
    }
    public static Task InvokeAsync(Action action)
    {
        if (queue?.HasThreadAccess == true) { action(); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        return completion.Task;
    }
}
