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

    /// <summary>Serializes access to <see cref="_allNodes"/>.</summary>
    private readonly object _nodesGate = new();

    /// <summary>Complete, unfiltered node repository; <see cref="Nodes"/> is a projection of this.</summary>
    private readonly List<ProxyNode> _allNodes = new();

    private ProxyNode? _selectedNode;
    private string? _searchText;
    private bool _isPinging;
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
    /// Raised after a link or subscription import appended at least one node. The
    /// App layer hooks this to persist <see cref="AppSettings.Nodes"/>.
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
            ImportError = "订阅地址无效";
            return;
        }

        string content;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            content = await client.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            ImportError = $"订阅获取失败: {ex.Message}";
            return;
        }

        var imported = NodeLinkParser.Parse(content);
        if (imported.Count == 0)
        {
            ImportError = "未解析到任何节点";
            return;
        }

        AddImported(imported);
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

        return $"已导入 {added} 个节点";
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
