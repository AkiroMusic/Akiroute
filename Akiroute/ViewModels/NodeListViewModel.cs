using System.Collections.ObjectModel;
using Akiroute.Helpers;
using Akiroute.Models;
using Akiroute.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Akiroute.ViewModels;

/// <summary>
/// View model for the node-list panel (plan §4.1): exposes the filtered node
/// collection, live search filtering, latency pinging via <see cref="PingService"/>,
/// node selection persisted to <see cref="AppSettings.SelectedNodeId"/>, and link /
/// subscription imports backed by <see cref="NodeLinkParser"/> and
/// <see cref="ClashConfigParser"/>.
///
/// Threading model: <see cref="Nodes"/> is mutated only on the UI thread — every
/// mutation is marshaled through the injected <c>runOnUiThread</c> seam (defaulting
/// to <see cref="DispatcherHelper.RunOnUiThread"/>) — while the private node
/// repository is guarded by a lock so background operations (ping completions) can
/// read it safely.
/// </summary>
public partial class NodeListViewModel : ObservableObject
{
    private readonly PingService _ping;
    private readonly Action<Action> _runOnUiThread;

    /// <summary>Shared <see cref="HttpClient"/> for subscription fetches — avoids
    /// socket exhaustion from per-request disposal (D6).</summary>
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Test seam: when non-null, subscription fetches use this delegate instead of
    /// <see cref="SharedHttpClient"/>. Set to <c>null</c> in test teardown.
    /// </summary>
    internal Func<Uri, CancellationToken, Task<string>>? SubscriptionFetchOverrideForTests { get; set; }

    /// <summary>Serializes access to <see cref="_allNodes"/>.</summary>
    private readonly object _nodesGate = new();

    /// <summary>Complete, unfiltered node repository; <see cref="Nodes"/> is a projection of this.</summary>
    private readonly List<ProxyNode> _allNodes = new();

    private ProxyNode? _selectedNode;
    private string? _searchText;
    private bool _isPinging;
    private DateTimeOffset? _lastAutoPingAt;
    private string? _importError;

    /// <summary>Bound by the node ListView; refreshed by <see cref="ApplyFilter"/>.</summary>
    public ObservableCollection<ProxyNode> Nodes { get; } = new();

    /// <summary>The shared settings singleton; <see cref="SelectNode"/> persists the choice into it.</summary>
    public AppSettings Settings { get; }

    /// <summary>The node currently selected in the list; also persisted via <see cref="AppSettings.SelectedNodeId"/>.</summary>
    public ProxyNode? SelectedNode
    {
        get => _selectedNode;
        set => SetProperty(ref _selectedNode, value);
    }

    /// <summary>Live search filter applied to <see cref="Nodes"/>; null or blank shows every node.</summary>
    public string? SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _runOnUiThread(ApplyFilter);
            }
        }
    }

    /// <summary>True while a latency ping sweep is in flight (drives a ProgressRing).</summary>
    public bool IsPinging
    {
        get => _isPinging;
        set => SetProperty(ref _isPinging, value);
    }

    /// <summary>Human-readable failure text from the last import attempt; null when the last import succeeded.</summary>
    public string? ImportError
    {
        get => _importError;
        set => SetProperty(ref _importError, value);
    }

    /// <summary>
    /// Raised after any node-list mutation that should be persisted — import
    /// (link / subscription / Clash) AND deletion. The App layer hooks this to
    /// persist <see cref="AppSettings.Nodes"/>.
    /// </summary>
    public event EventHandler? NodesImported;

    /// <summary>
    /// Creates the view model using the real UI dispatcher as the thread marshaler.
    /// </summary>
    /// <param name="settings">The application settings singleton.</param>
    /// <param name="ping">The latency probe service.</param>
    public NodeListViewModel(AppSettings settings, PingService ping)
        : this(settings, ping, DispatcherHelper.RunOnUiThread)
    {
    }

    /// <summary>
    /// Creates the view model with an explicit thread marshaler. Test seam: unit
    /// tests pass an inline action because the plain test host has no dispatcher.
    /// </summary>
    /// <param name="settings">The application settings singleton.</param>
    /// <param name="ping">The latency probe service.</param>
    /// <param name="runOnUiThread">Executes an action on the UI thread.</param>
    public NodeListViewModel(AppSettings settings, PingService ping, Action<Action> runOnUiThread)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ping = ping ?? throw new ArgumentNullException(nameof(ping));
        _runOnUiThread = runOnUiThread ?? throw new ArgumentNullException(nameof(runOnUiThread));

        // Seed the repository from the persisted node list and restore the selection.
        lock (_nodesGate)
        {
            foreach (var node in settings.Nodes)
            {
                if (node is not null)
                {
                    _allNodes.Add(node);
                }
            }
        }

        foreach (var node in _allNodes)
        {
            Nodes.Add(node);
            if (node.Id == settings.SelectedNodeId)
            {
                node.IsSelected = true;
                SelectedNode = node;
            }
        }
    }

    /// <summary>
    /// Adds a single node to the repository and refreshes the filtered projection.
    /// No import event is raised; use the import commands when the node arrives
    /// through the paste/URL flows that should trigger a settings save.
    /// </summary>
    /// <param name="node">The node to add.</param>
    public void AddNode(ProxyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        lock (_nodesGate)
        {
            _allNodes.Add(node);
            Settings.Nodes.Add(node);
        }

        _runOnUiThread(ApplyFilter);
    }

    /// <summary>
    /// Removes a node from the repository and persists the change. If the removed
    /// node was the active selection, the selection moves to the next available node
    /// (or clears when the list is empty). Uses reference equality to locate the
    /// exact instance.
    /// </summary>
    /// <param name="node">The node to remove.</param>
    [RelayCommand]
    private void DeleteNode(ProxyNode node)
    {
        if (node is null)
        {
            return;
        }

        lock (_nodesGate)
        {
            // Capture the position BEFORE removal so the selection can fall to
            // the adjacent (same-index) node afterwards.
            var removedIndex = _allNodes.IndexOf(node);
            var removed = removedIndex >= 0 && _allNodes.Remove(node);
            if (!removed)
            {
                return;
            }

            Settings.Nodes.Remove(node);

            // Update selection if the deleted node was selected: prefer the
            // adjacent (same-index) node so focus "stays in place" instead of
            // jumping to the top of the list.
            if (ReferenceEquals(SelectedNode, node))
            {
                ProxyNode? next = null;
                if (_allNodes.Count > 0)
                {
                    var idx = Math.Clamp(removedIndex, 0, _allNodes.Count - 1);
                    next = _allNodes[idx];
                }

                foreach (var candidate in _allNodes)
                {
                    candidate.IsSelected = false;
                }

                if (next is not null)
                {
                    next.IsSelected = true;
                }

                SelectedNode = next;
                Settings.SelectedNodeId = next?.Id ?? "";
            }
        }

        _runOnUiThread(() =>
        {
            ApplyFilter();
            NodesImported?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// Pings every known node concurrently through the injected
    /// <see cref="PingService"/> and writes each measured latency back onto the
    /// node. Never throws: the service swallows cancellation and probe failures.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending ping sweep.</param>
    [RelayCommand]
    public async Task PingAllAsync(CancellationToken cancellationToken = default)
    {
        IsPinging = true;
        try
        {
            ProxyNode[] snapshot;
            lock (_nodesGate)
            {
                snapshot = _allNodes.ToArray();
            }

            await _ping.PingAllAsync(snapshot, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsPinging = false;
        }
    }

    /// <summary>
    /// Marks <paramref name="node"/> as the active selection, clears any prior
    /// selection, and persists the choice to <see cref="AppSettings.SelectedNodeId"/>.
    /// Operates on the full repository so a node hidden by the search filter can
    /// still be selected.
    /// </summary>
    /// <param name="node">The node to select, or null to clear the selection.</param>
    [RelayCommand]
    public void SelectNode(ProxyNode? node)
    {
        _runOnUiThread(() =>
        {
            lock (_nodesGate)
            {
                foreach (var candidate in _allNodes)
                {
                    candidate.IsSelected = ReferenceEquals(candidate, node);
                }

                SelectedNode = node;
                Settings.SelectedNodeId = node?.Id ?? "";
            }
        });
    }

    /// <summary>
    /// Imports nodes from pasted text — either link tokens (parsed via
    /// <see cref="NodeLinkParser"/>) or a Clash YAML document (parsed via
    /// <see cref="ClashConfigParser"/> as a fallback). New nodes are appended
    /// (deduped by type/address/port) and <see cref="NodesImported"/> is raised when
    /// at least one node was added.
    /// </summary>
    /// <param name="text">Raw link text or Clash YAML to import.</param>
    /// <returns>A human-readable import summary, or null when nothing was added.</returns>
    [RelayCommand]
    public Task<string?> ImportAsync(string? text)
    {
        return Task.FromResult(ImportCore(text));
    }

    /// <summary>
    /// Fetches a subscription URL (http/https only, 15 s timeout) and imports every
    /// node parsed from the response body. Failures are surfaced through
    /// <see cref="ImportError"/> instead of being thrown, and
    /// <see cref="NodesImported"/> is only raised when at least one node was added.
    /// </summary>
    /// <param name="url">The subscription feed URL.</param>
    /// <param name="cancellationToken">Cancels the in-flight fetch.</param>
    [RelayCommand]
    public async Task ImportSubscriptionAsync(string? url, CancellationToken cancellationToken = default)
    {
        ImportError = null;

        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ImportError = Loc.Get("Error.SubUrlInvalid");
            return;
        }

        string content;
        try
        {
            content = SubscriptionFetchOverrideForTests is not null
                ? await SubscriptionFetchOverrideForTests(uri, cancellationToken).ConfigureAwait(false)
                : await SharedHttpClient.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            ImportError = string.Format(Loc.Get("Error.SubFetchFailed"), ex.Message);
            return;
        }

        var imported = NodeLinkParser.Parse(content);
        if (imported.Count == 0)
        {
            imported.AddRange(ClashConfigParser.Parse(content));
        }

        if (imported.Count == 0)
        {
            ImportError = Loc.Get("Error.SubNoNodesParsed");
            return;
        }

        // Upsert subscription entry: match by trimmed URL (case-insensitive).
        var trimmedUrl = url.Trim();
        SubscriptionEntry entry;
        lock (_nodesGate)
        {
            entry = Settings.Subscriptions.Find(
                           s => string.Equals(s.Url?.Trim(), trimmedUrl, StringComparison.OrdinalIgnoreCase))
                       ?? new SubscriptionEntry
                       {
                           Id = Guid.NewGuid().ToString("N"),
                           Name = uri.Host,
                           Url = trimmedUrl,
                       };

            if (!Settings.Subscriptions.Contains(entry))
            {
                Settings.Subscriptions.Add(entry);
            }

            entry.LastUpdated = DateTimeOffset.Now;
        }

        // Tag every parsed node with the subscription id before dedup/add.
        foreach (var node in imported)
        {
            node.SourceSubscriptionId = entry.Id;
        }

        AddImported(imported);
    }

    /// <summary>
    /// Re-fetches the subscription feed for <paramref name="entry"/> and
    /// replaces the stale nodes (those tagged with
    /// <see cref="ProxyNode.SourceSubscriptionId"/> matching
    /// <see cref="SubscriptionEntry.Id"/>) with the fresh parse result.
    /// Nodes from other sources (manual imports, other subscriptions) are
    /// never touched.
    /// </summary>
    /// <param name="entry">The subscription to update.</param>
    /// <param name="cancellationToken">Cancels the in-flight fetch.</param>
    /// <returns>
    /// Human-readable summary on success (e.g. "订阅已更新：新增 2，移除 1")
    /// or a failure description. On failure the node list and
    /// <see cref="SubscriptionEntry.LastUpdated"/> are left untouched.
    /// </returns>
    public async Task<string> UpdateSubscriptionAsync(SubscriptionEntry entry, CancellationToken cancellationToken = default)
    {
        // ── Validate ──────────────────────────────────────────────────
        if (entry is null
            || string.IsNullOrWhiteSpace(entry.Url)
            || !Uri.TryCreate(entry.Url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            AppLogger.Warn("subscription update failed — invalid url");
            return Loc.Get("Error.SubUrlInvalid");
        }

        // ── Fetch ─────────────────────────────────────────────────────
        string content;
        try
        {
            content = SubscriptionFetchOverrideForTests is not null
                ? await SubscriptionFetchOverrideForTests(uri, cancellationToken).ConfigureAwait(false)
                : await SharedHttpClient.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            AppLogger.Warn($"subscription update failed url={entry.Url} error={ex.Message}");
            return string.Format(Loc.Get("Error.SubFetchFailed"), ex.Message);
        }

        // ── Parse ─────────────────────────────────────────────────────
        var fresh = NodeLinkParser.Parse(content);
        if (fresh.Count == 0)
        {
            fresh.AddRange(ClashConfigParser.Parse(content));
        }

        if (fresh.Count == 0)
        {
            AppLogger.Warn($"subscription update parsed 0 nodes url={entry.Url}");
            return Loc.Get("Error.SubNoNodesParsedShort");
        }

        // ── Replace-on-update under _nodesGate ────────────────────────
        //
        // Threading rationale: _allNodes and Settings.Nodes are plain
        // List<ProxyNode> — safe to mutate under _nodesGate from any
        // thread. Observable mutations on Nodes (the ObservableCollection
        // bound to the UI) must happen on the UI thread. We follow the
        // same split used by AddImported: perform all List mutations
        // under the lock, collect results (added/removed counts,
        // selection-lost flag), then marshal ApplyFilter + property
        // writes + event raise through _runOnUiThread. This avoids
        // blocking the caller thread on UI dispatch and keeps the lock
        // critical section short.
        int added;
        int removed;
        bool selectionLost;

        lock (_nodesGate)
        {
            var entryId = entry.Id;

            // Tag fresh nodes with this subscription's id.
            foreach (var node in fresh)
            {
                node.SourceSubscriptionId = entryId;
            }

            // Keys of nodes that should survive after the update.
            var freshKeys = new HashSet<string>(
                fresh.Select(static n => NodeKey(n)),
                StringComparer.OrdinalIgnoreCase);

            // Snapshot existing nodes owned by this subscription.
            var toRemove = _allNodes
                .Where(n => string.Equals(n.SourceSubscriptionId, entryId, StringComparison.Ordinal)
                            && !freshKeys.Contains(NodeKey(n)))
                .ToList();

            // Snapshot fresh nodes not already present (by dedup key).
            var existingKeys = new HashSet<string>(
                _allNodes.Select(static n => NodeKey(n)),
                StringComparer.OrdinalIgnoreCase);
            var toAdd = fresh.Where(n => !existingKeys.Contains(NodeKey(n))).ToList();

            // Check if the currently selected node is about to be removed.
            selectionLost = toRemove.Any(n => ReferenceEquals(SelectedNode, n));

            // Apply removals to both lists.
            removed = 0;
            foreach (var node in toRemove)
            {
                if (_allNodes.Remove(node))
                {
                    Settings.Nodes.Remove(node);
                    removed++;
                }
            }

            // Apply additions to both lists.
            added = 0;
            foreach (var node in toAdd)
            {
                _allNodes.Add(node);
                Settings.Nodes.Add(node);
                added++;
            }

            entry.LastUpdated = DateTimeOffset.Now;
        }

        // ── UI-thread mutations ───────────────────────────────────────
        if (added + removed > 0)
        {
            _runOnUiThread(() =>
            {
                ApplyFilter();

                // Clear selection on UI thread if the selected node was removed.
                if (selectionLost)
                {
                    SelectedNode = null;
                    Settings.SelectedNodeId = "";
                }

                NodesImported?.Invoke(this, EventArgs.Empty);
            });
        }

        AppLogger.Info($"subscription updated url={entry.Url} added={added} removed={removed}");
        return string.Format(Loc.Get("Info.SubUpdated"), added, removed);
    }

    /// <summary>
    /// Determines whether an automatic update should run based on the current
    /// time, the last successful update, and the effective interval.
    /// </summary>
    /// <param name="now">Current timestamp (injected for determinism).</param>
    /// <param name="lastUpdated">Time of the last successful update; null = never.</param>
    /// <param name="effectiveIntervalMinutes">
    /// Minutes between updates; ≤ 0 means auto-update is disabled.
    /// </param>
    /// <returns><c>true</c> when an update is due.</returns>
    internal static bool ShouldRunUpdate(DateTimeOffset now, DateTimeOffset? lastUpdated, int effectiveIntervalMinutes)
    {
        if (effectiveIntervalMinutes <= 0)
        {
            return false;
        }

        return lastUpdated is null || (now - lastUpdated.Value).TotalMinutes >= effectiveIntervalMinutes;
    }

    /// <summary>
    /// Returns the effective auto-update interval for a subscription: the
    /// per-subscription value when positive, otherwise the global setting.
    /// </summary>
    /// <param name="entry">The subscription entry to query.</param>
    /// <returns>Minutes between updates; ≤ 0 means disabled.</returns>
    public int GetEffectiveIntervalMinutes(SubscriptionEntry entry) =>
        entry.AutoUpdateMinutes > 0 ? entry.AutoUpdateMinutes : Settings.SubscriptionAutoUpdateMinutes;

    /// <summary>
    /// Determines whether an automatic ping should run based on the current
    /// time, the last successful ping, and the effective interval.
    /// </summary>
    /// <param name="now">Current timestamp (injected for determinism).</param>
    /// <param name="lastPingAt">Time of the last successful ping; null = never.</param>
    /// <param name="intervalMinutes">
    /// Minutes between pings; ≤ 0 means auto-ping is disabled.
    /// </param>
    /// <returns><c>true</c> when a ping is due.</returns>
    internal static bool ShouldRunPing(DateTimeOffset now, DateTimeOffset? lastPingAt, int intervalMinutes)
    {
        if (intervalMinutes <= 0)
        {
            return false;
        }

        return lastPingAt is null || (now - lastPingAt.Value).TotalMinutes >= intervalMinutes;
    }

    /// <summary>
    /// Triggers a ping sweep when the auto-ping interval has elapsed since the
    /// last run. The decision is purely time-based (no logging — badges are the
    /// feedback surface).
    /// </summary>
    /// <param name="now">Current timestamp (injected for determinism).</param>
    public async Task RunAutoPingIfDueAsync(DateTimeOffset now)
    {
        var interval = Settings.AutoPingMinutes;
        if (!ShouldRunPing(now, _lastAutoPingAt, interval) || IsPinging)
        {
            return;
        }

        _lastAutoPingAt = now;
        await PingAllAsync().ConfigureAwait(true);
    }

    /// <summary>Parses pasted text into nodes; returns null when nothing was added.</summary>
    private string? ImportCore(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var imported = NodeLinkParser.Parse(text);
        if (imported.Count == 0)
        {
            imported.AddRange(ClashConfigParser.Parse(text));
        }

        return AddImported(imported);
    }

    /// <summary>
    /// Appends the not-yet-known nodes from <paramref name="imported"/> to the
    /// repository, refreshes the filtered projection, and raises
    /// <see cref="NodesImported"/> when anything was actually added.
    /// </summary>
    private string? AddImported(IEnumerable<ProxyNode> imported)
    {
        var added = 0;
        lock (_nodesGate)
        {
            var known = new HashSet<string>(
                _allNodes.Select(static node => NodeKey(node)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var node in imported)
            {
                if (node is null || !known.Add(NodeKey(node)))
                {
                    continue;
                }

                _allNodes.Add(node);
                Settings.Nodes.Add(node);
                added++;
            }
        }

        if (added == 0)
        {
            return null;
        }

        _runOnUiThread(() =>
        {
            ApplyFilter();
            NodesImported?.Invoke(this, EventArgs.Empty);
        });

        return string.Format(Loc.Get("Info.ImportedNodes"), added);
    }

    /// <summary>Replaces <see cref="Nodes"/> with the search-matching subset of the repository.</summary>
    private void ApplyFilter()
    {
        lock (_nodesGate)
        {
            var query = SearchText?.Trim();
            Nodes.Clear();
            foreach (var node in _allNodes)
            {
                if (string.IsNullOrEmpty(query) || Matches(node, query))
                {
                    Nodes.Add(node);
                }
            }
        }
    }

    /// <summary>True when a node's name or address contains the query (case-insensitive).</summary>
    private static bool Matches(ProxyNode node, string query) =>
        (node.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (node.Address?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Deduplication key for a node: its type, address, and port.</summary>
    private static string NodeKey(ProxyNode node) =>
        $"{node.Type}|{node.Address}|{node.Port}";
}
