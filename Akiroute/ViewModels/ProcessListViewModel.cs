using System.Collections.ObjectModel;
using Akiroute.Helpers;
using Akiroute.Models;
using Akiroute.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Akiroute.ViewModels;

/// <summary>
/// View model for the per-process routing panel (plan §4.2): consumes the
/// <see cref="ProcessMonitorService"/> to populate the bound process list, owns the
/// user's per-process routing action, and produces the <see cref="ProcessRule"/> list
/// handed to <see cref="XrayConfigBuilder"/> when the proxy starts.
///
/// Threading model: <see cref="Items"/> is mutated only on the UI thread — refreshes
/// marshal through the injected <c>runOnUiThread</c> seam (defaulting to
/// <see cref="DispatcherHelper.RunOnUiThread"/>) — while <see cref="BuildProcessRules"/>
/// may run from any thread and snapshots the collection under a lock.
/// </summary>
public partial class ProcessListViewModel : ObservableObject
{
    private readonly ProcessMonitorService _monitor;
    private readonly Action<Action> _runOnUiThread;

    /// <summary>Serializes access to <see cref="Items"/> between the UI-thread
    /// refresh path and background <see cref="BuildProcessRules"/> callers.</summary>
    private readonly object _itemsGate = new();

    private bool _isRefreshing;
    private bool _isEmpty = true;

    /// <summary>
    /// Bound by the ListView; repopulated in place on each refresh so the selected
    /// item and scroll position survive rescanning.
    /// </summary>
    public ObservableCollection<ProcessInfoItem> Items { get; } = new();

    /// <summary>True while a refresh enumeration is in flight (drives a ProgressRing).</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetProperty(ref _isRefreshing, value);
    }

    /// <summary>True when no processes are listed (drives the empty-state hint).</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetProperty(ref _isEmpty, value);
    }

    /// <summary>The shared settings singleton; <see cref="SetAction"/> persists rules into it.</summary>
    public AppSettings Settings { get; }

    /// <summary>
    /// Raised after a routing action changes and <see cref="AppSettings.ProcessRules"/>
    /// has been updated. The View/App layer hooks this to trigger a settings save.
    /// </summary>
    public event EventHandler? RulesChanged;

    /// <summary>
    /// Creates the view model using the real UI dispatcher as the thread marshaler.
    /// </summary>
    /// <param name="monitor">The process monitor backing the list.</param>
    /// <param name="settings">The application settings singleton.</param>
    public ProcessListViewModel(ProcessMonitorService monitor, AppSettings settings)
        : this(monitor, settings, DispatcherHelper.RunOnUiThread)
    {
    }

    /// <summary>
    /// Creates the view model with an explicit thread marshaler. Test seam: unit
    /// tests pass an inline action because the plain test host has no dispatcher.
    /// </summary>
    /// <param name="monitor">The process monitor backing the list.</param>
    /// <param name="settings">The application settings singleton.</param>
    /// <param name="runOnUiThread">Executes an action on the UI thread.</param>
    public ProcessListViewModel(ProcessMonitorService monitor, AppSettings settings, Action<Action> runOnUiThread)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runOnUiThread = runOnUiThread ?? throw new ArgumentNullException(nameof(runOnUiThread));

        // The list starts empty; the partial property cannot carry an initializer.
        IsEmpty = true;
    }

    /// <summary>
    /// Enumerates the current processes off the UI thread and repopulates
    /// <see cref="Items"/>. Previously seen processes keep their cached
    /// <see cref="ProcessInfoItem"/> (the monitor preserves <see cref="ProcessAction"/>
    /// across scans), so the user's routing choices survive refreshes.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending refresh. A cancelled
    /// refresh leaves <see cref="Items"/> untouched and propagates
    /// <see cref="OperationCanceledException"/>.</param>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsRefreshing = true;
        try
        {
            // Enumerate off the UI thread; the monitor's cache is concurrent-safe.
            var snapshot = await Task.Run(() => _monitor.GetCurrentProcesses(), cancellationToken).ConfigureAwait(true);

            // A token cancelled while the scan ran propagates here, before any
            // marshaling, so the UI-thread callback never throws.
            cancellationToken.ThrowIfCancellationRequested();

            // Marshal back to the UI thread before touching the bound collection.
            _runOnUiThread(() =>
            {
                // Inherit any persisted routing rules so freshly materialized
                // items (first scan of a session) reflect saved choices instead of
                // defaulting to Proxy. Idempotent: in-session changes via SetAction
                // stay in sync with Settings.ProcessRules, so re-applying is a no-op.
                ApplyPersistedRules(snapshot);

                lock (_itemsGate)
                {
                    Items.Clear();
                    foreach (var item in snapshot)
                    {
                        Items.Add(item);
                    }

                    IsEmpty = Items.Count == 0;
                }
            });
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// Applies a routing action to an item, persists the resulting rule set into
    /// <see cref="AppSettings.ProcessRules"/>, and raises <see cref="RulesChanged"/>.
    /// The action itself lives on the item (and the monitor's cache), so it survives
    /// subsequent refreshes.
    /// </summary>
    /// <param name="item">The process item being re-routed — a live <see cref="Items"/>
    /// entry, as passed back by the per-item toggle.</param>
    /// <param name="action">The routing action to apply.</param>
    public void SetAction(ProcessInfoItem item, ProcessAction action)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Action == action)
        {
            return;
        }

        item.Action = action;

        Settings.ProcessRules.Clear();
        Settings.ProcessRules.AddRange(BuildProcessRules());

        RulesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Copies persisted routing rules from <see cref="AppSettings.ProcessRules"/> onto
    /// freshly materialized <see cref="ProcessInfoItem"/>s so the UI reflects saved
    /// choices after a restart. Only items still in the default
    /// <see cref="ProcessAction.Proxy"/> state are touched: a user's in-session
    /// selection (which SetAction keeps in sync with <see cref="AppSettings.ProcessRules"/>)
    /// is never overwritten, and an explicit Proxy choice removes the matching rule.
    /// </summary>
    private void ApplyPersistedRules(IEnumerable<ProcessInfoItem> snapshot)
    {
        foreach (var item in snapshot)
        {
            if (item is null || item.Action != ProcessAction.Proxy || string.IsNullOrWhiteSpace(item.ProcessName))
            {
                continue;
            }

            foreach (var rule in Settings.ProcessRules)
            {
                if (string.Equals(rule.ProcessName, item.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    item.Action = rule.Action;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Builds the <see cref="ProcessRule"/> list consumed by
    /// <see cref="XrayConfigBuilder.Build"/>. Only processes with an explicit
    /// non-<see cref="ProcessAction.Proxy"/> action produce rules (Proxy is the
    /// default "no rule" state); blank process names are skipped, duplicates are
    /// deduped keeping the last, and the result is sorted by process name so the
    /// generated xray config is stable across refreshes. Safe to call from any thread.
    /// </summary>
    public IReadOnlyList<ProcessRule> BuildProcessRules()
    {
        ProcessInfoItem[] items;
        lock (_itemsGate)
        {
            items = Items.ToArray();
        }

        var byName = new Dictionary<string, ProcessRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is null || item.Action == ProcessAction.Proxy || string.IsNullOrWhiteSpace(item.ProcessName))
            {
                continue;
            }

            byName[item.ProcessName] = new ProcessRule { ProcessName = item.ProcessName, Action = item.Action };
        }

        var rules = byName.Values.ToList();
        rules.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.ProcessName, b.ProcessName));
        return rules;
    }
}
