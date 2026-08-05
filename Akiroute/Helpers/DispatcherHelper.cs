using Microsoft.UI.Dispatching;

namespace Akiroute.Helpers;

/// <summary>
/// DispatcherQueue bridge: marshals background-thread work onto the UI thread.
/// All services that mutate bound UI state MUST go through <see cref="RunOnUiThread"/>.
/// </summary>
public static class DispatcherHelper
{
    private static DispatcherQueue? _queue;

    /// <summary>Thread-safe single reference to the app's UI dispatcher queue.</summary>
    public static DispatcherQueue? Queue => _queue;

    /// <summary>
    /// Binds the app's main DispatcherQueue. Call once from App/Window construction.
    /// </summary>
    public static void Initialize(DispatcherQueue queue) => _queue = queue;

    /// <summary>
    /// Executes <paramref name="action"/> on the UI thread.
    /// If no queue is bound (e.g. unit-test host) or the current thread already owns
    /// the queue, the action runs inline — services stay callable without a UI.
    /// </summary>
    public static void RunOnUiThread(Action action)
    {
        var queue = _queue;
        if (queue is null)
        {
            action();
            return;
        }

        if (queue.HasThreadAccess)
        {
            action();
            return;
        }

        queue.TryEnqueue(() => action());
    }
}
