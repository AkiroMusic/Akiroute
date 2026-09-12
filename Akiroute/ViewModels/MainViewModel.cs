using System.ComponentModel;
using Akiroute.Helpers;
using Akiroute.Models;
using Akiroute.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Akiroute.ViewModels;

/// <summary>
/// Top-level coordinator view model (plan §5): composes the node list, settings,
/// proxy status, and process-list child view models and exposes the global commands
/// the tray and main-window header bind to. It stays thin — all real work is
/// delegated to the child view models, which own their own thread marshaling.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action<Action> _runOnUiThread;

    /// <summary>Alternative settings file path for <see cref="SaveSettings"/>; null targets the default. Test seam.</summary>
    internal string? ConfigFilePath { get; set; }

    /// <summary>The node-list child view model.</summary>
    public NodeListViewModel Nodes { get; }

    /// <summary>The settings child view model.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The proxy-status child view model.</summary>
    public ProxyStatusViewModel Status { get; }

    /// <summary>The per-process routing child view model.</summary>
    public ProcessListViewModel Processes { get; }

    /// <summary>The logs viewer child view model.</summary>
    public LogsViewModel Logs { get; }

    private bool _isProxyRunning;

    /// <summary>Mirror of <see cref="ProxyStatusViewModel.IsRunning"/> for tray/menu bindings.</summary>
    public bool IsProxyRunning
    {
        get => _isProxyRunning;
        set => SetProperty(ref _isProxyRunning, value);
    }

    /// <summary>
    /// Creates the coordinator using the real UI dispatcher as the thread marshaler
    /// for all child view models.
    /// </summary>
    /// <param name="settings">The application settings singleton.</param>
    /// <param name="xray">The xray process manager.</param>
    /// <param name="ping">The latency probe service.</param>
    /// <param name="monitor">The process monitor backing the process list.</param>
    public MainViewModel(AppSettings settings, XrayService xray, PingService ping, ProcessMonitorService monitor)
        : this(settings, xray, ping, monitor, DispatcherHelper.RunOnUiThread)
    {
    }

    /// <summary>
    /// Creates the coordinator with an explicit thread marshaler passed to every
    /// child view model. Test seam: unit tests pass an inline action because the
    /// plain test host has no dispatcher.
    /// </summary>
    /// <param name="settings">The application settings singleton.</param>
    /// <param name="xray">The xray process manager.</param>
    /// <param name="ping">The latency probe service.</param>
    /// <param name="monitor">The process monitor backing the process list.</param>
    /// <param name="runOnUiThread">Executes an action on the UI thread.</param>
    public MainViewModel(
        AppSettings settings,
        XrayService xray,
        PingService ping,
        ProcessMonitorService monitor,
        Action<Action> runOnUiThread)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runOnUiThread = runOnUiThread ?? throw new ArgumentNullException(nameof(runOnUiThread));

        Nodes = new NodeListViewModel(settings, ping, _runOnUiThread);
        Settings = new SettingsViewModel(settings, _runOnUiThread);
        Status = new ProxyStatusViewModel(xray, settings, _runOnUiThread);
        Processes = new ProcessListViewModel(monitor, settings, _runOnUiThread);
        Logs = new LogsViewModel(n => AppLogger.ReadTail(n), () => xray.RecentLogs);

        IsProxyRunning = Status.IsRunning;
        Status.PropertyChanged += OnStatusPropertyChanged;
    }

    /// <summary>Keeps <see cref="IsProxyRunning"/> in lockstep with the proxy status child.</summary>
    private void OnStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProxyStatusViewModel.IsRunning))
        {
            _runOnUiThread(() => IsProxyRunning = Status.IsRunning);
        }
    }

    /// <summary>Starts or stops the proxy, delegating to <see cref="ProxyStatusViewModel.ToggleProxyAsync"/>.</summary>
    /// <param name="cancellationToken">Cancels the pending start/stop.</param>
    [RelayCommand]
    public Task ToggleProxyAsync(CancellationToken cancellationToken = default) =>
        Status.ToggleProxyAsync(cancellationToken);

    /// <summary>Pings every known node, delegating to <see cref="NodeListViewModel.PingAllAsync"/>.</summary>
    /// <param name="cancellationToken">Cancels the pending ping sweep.</param>
    [RelayCommand]
    public Task PingAllAsync(CancellationToken cancellationToken = default) =>
        Nodes.PingAllAsync(cancellationToken);

    /// <summary>
    /// Persists the shared settings (to <see cref="ConfigFilePath"/> when set).
    /// I/O failures are logged and surfaced through
    /// <see cref="SettingsViewModel.SaveError"/> instead of escaping — most
    /// callers sit on async-void UI event handlers where an uncaught exception
    /// would crash the process.
    /// </summary>
    [RelayCommand]
    public void SaveSettings()
    {
        try
        {
            if (string.IsNullOrEmpty(ConfigFilePath))
            {
                SettingsService.Save(_settings);
            }
            else
            {
                SettingsService.Save(_settings, ConfigFilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Error($"[MainViewModel] SaveSettings failed: {ex.Message}");
            Settings.SaveError = string.Format(Loc.Get("Error.SaveFailed"), ex.Message);
        }
    }
}
