using System.Diagnostics;
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

        // Capture the enqueue result; when the dispatcher queue is shutting down
        // TryEnqueue returns false. The enqueued actions are usually lightweight
        // UI property updates (SetProperty on ObservableObject), so running them
        // inline is preferred over silently dropping state transitions during
        // shutdown. Inline execution can still touch bound collections from a
        // non-UI thread during teardown; the log warning marks that window.
        if (!queue.TryEnqueue(() => action()))
        {
            AppLogger.Warn("[DispatcherHelper] TryEnqueue failed (queue shutting down); executing action inline.");
            action();
        }
    }
}
