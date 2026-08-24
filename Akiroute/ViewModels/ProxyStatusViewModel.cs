using System.Collections.ObjectModel;
using System.Threading;
using Akiroute.Helpers;
using Akiroute.Models;
using Akiroute.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Akiroute.ViewModels;

/// <summary>
/// View model for the proxy status header and traffic chart (plan §7.1/§7.5):
/// mirrors the <see cref="XrayService"/> lifecycle onto bound properties, drives
/// start/stop, surfaces the crash tail through <see cref="LastError"/>, and estimates
/// real-time throughput by sampling the xray access log's file size.
///
/// Threading model: every bound property is mutated through the injected
/// <c>runOnUiThread</c> seam (default <see cref="DispatcherHelper.RunOnUiThread"/>)
/// because <see cref="XrayService"/> events and the traffic loop fire off-thread.
/// </summary>
public partial class ProxyStatusViewModel : ObservableObject
{
    /// <summary>Number of throughput samples retained in <see cref="TrafficSeries"/>.</summary>
    public const int TrafficSeriesCapacity = 60;

    /// <summary>Default sampling interval of the throughput loop.</summary>
    private const int DefaultSampleIntervalMs = 1000;

    private readonly XrayService _xray;
    private readonly AppSettings _settings;
    private readonly Action<Action> _runOnUiThread;
    private readonly Func<long> _fileSizeProvider;
    private readonly object _trafficGate = new();

    private CancellationTokenSource? _trafficCts;
    private long _lastFileSize = -1;

    /// <summary>Bound by the traffic chart control; holds the last <see cref="TrafficSeriesCapacity"/> samples.</summary>
    public ObservableCollection<double> TrafficSeries { get; } = new();

    /// <summary>The background sampling task; completed once the loop exits after a stop.</summary>
    internal Task? TrafficLoopTask { get; private set; }

    /// <summary>Sampling interval of the traffic loop in milliseconds. Internal test seam.</summary>
    internal int SampleIntervalMs { get; set; } = DefaultSampleIntervalMs;

    private bool _isRunning;
    private bool _isStarting;
    private string? _statusMessage;
    private string? _lastError;
    private IReadOnlyList<string> _recentLogs = Array.Empty<string>();
    private double _throughputBytesPerSecond;
    private int _localPort;

    /// <summary>True while the xray process is running.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    /// <summary>True while a start is in flight (disables the toggle).</summary>
    public bool IsStarting
    {
        get => _isStarting;
        set => SetProperty(ref _isStarting, value);
    }

    /// <summary>Current lifecycle status line for the header.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Last failure summary (crash tail or start error); null when healthy.</summary>
    public string? LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value);
    }

    /// <summary>Most recent xray log lines (tail 20), in arrival order.</summary>
    public IReadOnlyList<string> RecentLogs
    {
        get => _recentLogs;
        set => SetProperty(ref _recentLogs, value);
    }

    /// <summary>Rolling throughput estimate in bytes per second.</summary>
    public double ThroughputBytesPerSecond
    {
        get => _throughputBytesPerSecond;
        set => SetProperty(ref _throughputBytesPerSecond, value);
    }

    /// <summary>The loopback SOCKS port xray bound after the last successful start.</summary>
    public int LocalPort
    {
        get => _localPort;
        set => SetProperty(ref _localPort, value);
    }

    /// <summary>
    /// Creates the view model using the real UI dispatcher as the thread marshaler.
    /// </summary>
    /// <param name="xray">The xray process manager backing the state.</param>
    /// <param name="settings">The application settings singleton.</param>
    public ProxyStatusViewModel(XrayService xray, AppSettings settings)
        : this(xray, settings, DispatcherHelper.RunOnUiThread, DefaultFileSizeProvider)
    {
    }

    /// <summary>
    /// Creates the view model with an explicit thread marshaler and file-size
    /// provider. Test seam: unit tests pass an inline action (no dispatcher in the
    /// plain test host) and a scripted <paramref name="fileSizeProvider"/>.
    /// </summary>
    /// <param name="xray">The xray process manager backing the state.</param>
    /// <param name="settings">The application settings singleton.</param>
    /// <param name="runOnUiThread">Executes an action on the UI thread.</param>
    /// <param name="fileSizeProvider">Returns the current access-log size in bytes;
    /// defaults to reading the real log (0 when absent).</param>
    public ProxyStatusViewModel(
        XrayService xray,
        AppSettings settings,
        Action<Action> runOnUiThread,
        Func<long>? fileSizeProvider = null)
    {
        _xray = xray ?? throw new ArgumentNullException(nameof(xray));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runOnUiThread = runOnUiThread ?? throw new ArgumentNullException(nameof(runOnUiThread));
        _fileSizeProvider = fileSizeProvider ?? DefaultFileSizeProvider;

        IsRunning = xray.IsRunning;
        LocalPort = xray.LocalPort;
        RecentLogs = xray.RecentLogs;
        ApplyState(xray.IsRunning ? XrayServiceState.Running : XrayServiceState.Stopped);

        xray.StateChanged += OnXrayStateChanged;
        xray.LogReceived += OnXrayLogReceived;
    }

    /// <summary>
    /// Debounce window (ms) that swallows overlapping/rapid toggle requests.
    /// Covers the real races: a tray-menu toggle landing while a header-button
    /// toggle is still in flight, and double-clicks firing two toggles within
    /// one event-loop turn. Distinct user clicks spaced further apart than the
    /// window always pass through, so normal start→stop usage is unaffected.
    /// </summary>
    private const long ToggleDebounceMs = 300;

    /// <summary>Timestamp (<see cref="Environment.TickCount64"/>) of the last accepted toggle.</summary>
    private long _lastToggleTick;

    /// <summary>Starts the proxy when stopped; stops it when running.</summary>
    /// <param name="cancellationToken">Cancels the pending start/stop.</param>
    [RelayCommand]
    public async Task ToggleProxyAsync(CancellationToken cancellationToken = default)
    {
        // Single atomic read-and-claim: two concurrent callers (tray + header
        // button) can never both pass this check. A rejected call still refreshes
        // the timestamp, which only marginally extends the window under deliberate
        // spam — irrelevant for human click cadence.
        var now = Environment.TickCount64;
        if (now - Interlocked.Exchange(ref _lastToggleTick, now) < ToggleDebounceMs)
        {
            // A toggle is in flight or just completed — ignore this request
            // instead of racing it (which would show a false "连接失败" over a
            // successful start or sample traffic twice).
            return;
        }

        if (IsRunning)
        {
            await StopProxyAsync(cancellationToken).ConfigureAwait(true);
        }
        else
        {
            await StartProxyAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Starts xray for the currently selected node (see <see cref="AppSettings.SelectedNodeId"/>).
    /// A missing selection or a failed start is reported through <see cref="LastError"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending start.</param>
    [RelayCommand]
    public async Task StartProxyAsync(CancellationToken cancellationToken = default)
    {
        IsStarting = true;
        LastError = null;
        try
        {
            var node = ResolveSelectedNode();
            if (node is null)
            {
                StatusMessage = Loc.Get("Status.StartFailed");
                LastError = Loc.Get("Status.NoNodeSelected");
                return;
            }

            var started = await _xray
                .StartAsync(node, _settings.ProcessRules, _settings.Mode, _settings.Port, cancellationToken)
                .ConfigureAwait(true);

            if (started)
            {
                StartTrafficLoop();
            }
            else
            {
                StatusMessage = Loc.Get("Status.ConnectFailed");
                LastError = _xray.LastCrashSummary ?? Loc.Get("Status.StartFailed");
            }
        }
        finally
        {
            IsStarting = false;
        }
    }

    /// <summary>Stops the xray process and the throughput sampling loop.</summary>
    /// <param name="cancellationToken">Cancels the pending stop.</param>
    [RelayCommand]
    public async Task StopProxyAsync(CancellationToken cancellationToken = default)
    {
        StopTrafficLoop();
        await _xray.StopAsync(cancellationToken).ConfigureAwait(true);
        ApplyState(XrayServiceState.Stopped);
    }

    /// <summary>Marshals an xray lifecycle transition onto the UI thread.</summary>
    private void OnXrayStateChanged(object? sender, XrayServiceState state)
    {
        _runOnUiThread(() => ApplyState(state));
    }

    /// <summary>Marshals the xray log tail snapshot onto the UI thread.</summary>
    private void OnXrayLogReceived(object? sender, string line)
    {
        _runOnUiThread(() => RecentLogs = _xray.RecentLogs);
    }

    /// <summary>Maps an xray lifecycle state onto the bound properties.</summary>
    private void ApplyState(XrayServiceState state)
    {
        switch (state)
        {
            case XrayServiceState.Starting:
                IsStarting = true;
                StatusMessage = Loc.Get("Status.Connecting");
                break;

            case XrayServiceState.Running:
                IsStarting = false;
                IsRunning = true;
                LocalPort = _xray.LocalPort;
                LastError = null;
                StatusMessage = LocalPort > 0 ? string.Format(Loc.Get("Status.ConnectedPort"), LocalPort) : Loc.Get("Status.Connected");
                break;

            case XrayServiceState.Failed:
                IsStarting = false;
                IsRunning = false;
                StatusMessage = Loc.Get("Status.ConnectFailed");
                LastError = _xray.LastCrashSummary ?? Loc.Get("Status.StartFailed");
                break;

            default:
                IsStarting = false;
                IsRunning = false;
                StatusMessage = Loc.Get("Status.Disconnected");
                break;
        }
    }

    /// <summary>Returns the node referenced by <see cref="AppSettings.SelectedNodeId"/>, or null.</summary>
    private ProxyNode? ResolveSelectedNode()
    {
        var selectedId = _settings.SelectedNodeId;
        if (string.IsNullOrEmpty(selectedId))
        {
            return null;
        }

        foreach (var node in _settings.Nodes)
        {
            if (node is not null && node.Id == selectedId)
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// (Re)starts the throughput sampling loop. Internal test seam: unit tests drive
    /// the loop directly with a scripted <see cref="_fileSizeProvider"/> so the suite
    /// never waits on the real 1 s timer.
    /// </summary>
    internal void StartTrafficLoop()
    {
        StopTrafficLoop();

        _lastFileSize = _fileSizeProvider();
        lock (_trafficGate)
        {
            TrafficSeries.Clear();
        }

        var cts = new CancellationTokenSource();
        _trafficCts = cts;
        TrafficLoopTask = RunTrafficLoopAsync(cts.Token);
    }

    /// <summary>Cancels the throughput sampling loop if one is running.</summary>
    internal void StopTrafficLoop()
    {
        _trafficCts?.Cancel();
        _trafficCts?.Dispose();
        _trafficCts = null;
    }

    /// <summary>
    /// Samples the access log every <see cref="SampleIntervalMs"/> until cancelled.
    /// A cancelled delay unwinds silently; the loop never throws.
    /// </summary>
    private async Task RunTrafficLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(SampleIntervalMs, cancellationToken).ConfigureAwait(false);
                SampleTrafficOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // The loop was cancelled on stop; nothing more to do.
        }
    }

    /// <summary>
    /// Computes a single throughput sample from <see cref="_fileSizeProvider"/> and
    /// pushes it onto <see cref="TrafficSeries"/>. The delta between consecutive
    /// samples approximates bytes per second (the loop samples once per second).
    /// Negative deltas (log rotation) clamp to zero.
    /// </summary>
    internal void SampleTrafficOnce()
    {
        var current = _fileSizeProvider();
        long delta = _lastFileSize < 0 ? 0 : current - _lastFileSize;
        _lastFileSize = current;

        var bytesPerSecond = delta > 0 ? (double)delta : 0.0;
        _runOnUiThread(() =>
        {
            lock (_trafficGate)
            {
                TrafficSeries.Add(bytesPerSecond);
                while (TrafficSeries.Count > TrafficSeriesCapacity)
                {
                    TrafficSeries.RemoveAt(0);
                }
            }

            ThroughputBytesPerSecond = bytesPerSecond;
        });
    }

    /// <summary>Default throughput source: the byte length of the xray access log, 0 when absent.</summary>
    private static long DefaultFileSizeProvider()
    {
        try
        {
            var path = Path.Combine(AppPaths.LogsDir, "akiroute-xray-access.log");
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
