using System.Windows.Threading;

namespace LocalAIModelManager.App.Infrastructure;

/// <summary>
/// Access to the WPF dispatcher. Background work (process output, gateway events,
/// resource polling) must never touch UI objects directly.
/// </summary>
public static class UiDispatcher
{
    private static Dispatcher? _dispatcher;

    public static void Initialize(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public static bool IsOnUiThread =>
        _dispatcher is null || _dispatcher.CheckAccess();

    public static void Invoke(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    /// <summary>
    /// Always runs <paramref name="action"/> on a later dispatcher turn, even when the
    /// caller is already on the UI thread. Used when the work would otherwise tear down
    /// the very UI that is still executing the current command.
    /// </summary>
    public static void Post(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    public static async Task InvokeAsync(Func<Task> action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
        }

        await dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task.Unwrap().ConfigureAwait(true);
    }
}
