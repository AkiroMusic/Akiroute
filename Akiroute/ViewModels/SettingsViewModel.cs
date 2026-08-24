using Akiroute.Helpers;
using Akiroute.Models;
using Akiroute.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Akiroute.ViewModels;

/// <summary>
/// View model for the settings panel (plan §5.2): exposes each editable setting as
/// a two-way-bindable observable property that syncs straight into the shared
/// <see cref="AppSettings"/> singleton, plus Load/Save commands backed by
/// <see cref="SettingsService"/>. <see cref="SettingsChanged"/> lets the App layer
/// persist on every edit; <see cref="Save"/> persists explicitly and surfaces I/O
/// failures through <see cref="SaveError"/>.
///
/// The Load/Save commands target the default config file unless
/// <see cref="ConfigFilePath"/> is set (test seam pointing at a temp file).
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly Action<Action> _runOnUiThread;

    /// <summary>Suppresses the Settings round-trip and <see cref="SettingsChanged"/>
    /// while loading/initializing so a refresh never re-triggers a save.</summary>
    private bool _suppressSync;

    /// <summary>The shared settings singleton, mutated in place by the property wrappers.</summary>
    public AppSettings Settings { get; }

    /// <summary>Raised after an editable setting changes and <see cref="Settings"/> has been updated.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Alternative config file path for Load/Save; null targets the default file. Test seam.</summary>
    internal string? ConfigFilePath { get; set; }

    private string? _saveError;
    private ProxyMode _mode;
    private AppTheme _theme;
    private int _port;
    private bool _autoConnect;
    private int _subscriptionAutoUpdateMinutes;
    private int _autoPingMinutes;
    private bool _tunEnabled;

    /// <summary>Failure text from the last <see cref="Save"/>; null when the last save succeeded.</summary>
    public string? SaveError
    {
        get => _saveError;
        set => SetProperty(ref _saveError, value);
    }

    /// <summary>Global routing mode, synced to <see cref="AppSettings.Mode"/>.</summary>
    public ProxyMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value) && !_suppressSync)
            {
                Settings.Mode = value;
                NotifyChanged();
            }
        }
    }

    /// <summary>Color theme, synced to <see cref="AppSettings.Theme"/>.</summary>
    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value) && !_suppressSync)
            {
                Settings.Theme = value;
                NotifyChanged();
            }
        }
    }

    /// <summary>
    /// Local proxy port, synced to <see cref="AppSettings.Port"/>. Values outside
    /// the valid 1024-65535 range are rejected and the last valid value is kept.
    /// </summary>
    public int Port
    {
        get => _port;
        set
        {
            var clamped = value is >= 1024 and <= 65535 ? value : _port;
            if (!SetProperty(ref _port, clamped) || _suppressSync)
            {
                return;
            }

            Settings.Port = clamped;
            NotifyChanged();
        }
    }

    /// <summary>Connect to the selected node on startup, synced to <see cref="AppSettings.AutoConnect"/>.</summary>
    public bool AutoConnect
    {
        get => _autoConnect;
        set
        {
            if (SetProperty(ref _autoConnect, value) && !_suppressSync)
            {
                Settings.AutoConnect = value;
                NotifyChanged();
            }
        }
    }

    /// <summary>Subscription auto-update interval in minutes, synced to <see cref="AppSettings.SubscriptionAutoUpdateMinutes"/>.</summary>
    public int SubscriptionAutoUpdateMinutes
    {
        get => _subscriptionAutoUpdateMinutes;
        set
        {
            if (SetProperty(ref _subscriptionAutoUpdateMinutes, value) && !_suppressSync)
            {
                Settings.SubscriptionAutoUpdateMinutes = value;
                NotifyChanged();
            }
        }
    }

    /// <summary>自动测速间隔（分钟），同步至 <see cref="AppSettings.AutoPingMinutes"/>。</summary>
    public int AutoPingMinutes
    {
        get => _autoPingMinutes;
        set
        {
            if (SetProperty(ref _autoPingMinutes, value) && !_suppressSync)
            {
                Settings.AutoPingMinutes = value;
                NotifyChanged();
            }
        }
    }

    /// <summary>Route traffic through the Windows TUN adapter, synced to <see cref="AppSettings.TunEnabled"/>.</summary>
    public bool TunEnabled
    {
        get => _tunEnabled;
        set
        {
            if (SetProperty(ref _tunEnabled, value) && !_suppressSync)
            {
                Settings.TunEnabled = value;
                NotifyChanged();
            }
        }
    }

    /// <summary>
    /// 当前订阅源条目的只读视图；ItemsControl 绑定此属性以枚举
    /// <see cref="Settings.Subscriptions"/>。调用 <see cref="RefreshSubscriptionsView"/>
    /// 使绑定刷新。
    /// </summary>
    /// <summary>
    /// 订阅源条目的只读视图。每次访问返回快照副本：Native AOT 下 ItemsControl 的
    /// x:Bind 在属性变更时比较引用，原地修改同一 List 实例不会触发重绑定，
    /// 因此必须返回新实例才能让删除/新增即时反映到界面。
    /// Read-only view over <see cref="Settings.Subscriptions"/>. Returns a
    /// snapshot COPY per access: x:Bind skips rebinding when the property value
    /// reference is unchanged, so in-place mutations of the same List instance
    /// would never reach the UI without a fresh instance per read.
    /// 调用 <see cref="RefreshSubscriptionsView"/> 使绑定刷新。
    /// </summary>
    public IReadOnlyList<SubscriptionEntry> SubscriptionEntries => Settings.Subscriptions.ToList();

    /// <summary>
    /// Raises <see cref="PropertyChanged"/> for <see cref="SubscriptionEntries"/>
    /// so the ItemsControl re-enumerates the live list.
    /// </summary>
    public void RefreshSubscriptionsView() =>
        OnPropertyChanged(nameof(SubscriptionEntries));

    /// <summary>
    /// 移除指定订阅源条目并持久化；null 参数安全忽略。
    /// </summary>
    [RelayCommand]
    public void RemoveSubscription(SubscriptionEntry? entry)
    {
        if (entry is null) return;

        Settings.Subscriptions.Remove(entry);
        RefreshSubscriptionsView();
        Save();
    }

    /// <summary>
    /// Creates the view model using the real UI dispatcher as the thread marshaler.
    /// </summary>
    /// <param name="settings">The application settings singleton.</param>
    public SettingsViewModel(AppSettings settings)
        : this(settings, DispatcherHelper.RunOnUiThread)
    {
    }

    /// <summary>
    /// Creates the view model with an explicit thread marshaler. Test seam: unit
    /// tests pass an inline action because the plain test host has no dispatcher.
    /// </summary>
    /// <param name="settings">The application settings singleton.</param>
    /// <param name="runOnUiThread">Executes an action on the UI thread.</param>
    public SettingsViewModel(AppSettings settings, Action<Action> runOnUiThread)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runOnUiThread = runOnUiThread ?? throw new ArgumentNullException(nameof(runOnUiThread));

        _suppressSync = true;
        try
        {
            Mode = settings.Mode;
            Theme = settings.Theme;
            Port = settings.Port;
            AutoConnect = settings.AutoConnect;
            SubscriptionAutoUpdateMinutes = settings.SubscriptionAutoUpdateMinutes;
            AutoPingMinutes = settings.AutoPingMinutes;
            TunEnabled = settings.TunEnabled;
        }
        finally
        {
            _suppressSync = false;
        }
    }

    /// <summary>
    /// Reloads settings from disk (or <see cref="ConfigFilePath"/> when set) into the
    /// shared <see cref="Settings"/> instance and refreshes every observable wrapper.
    /// </summary>
    [RelayCommand]
    public void Load()
    {
        var loaded = string.IsNullOrEmpty(ConfigFilePath)
            ? SettingsService.Load()
            : SettingsService.Load(ConfigFilePath);

        _suppressSync = true;
        try
        {
            Settings.SelectedNodeId = loaded.SelectedNodeId;
            Settings.Nodes = loaded.Nodes;
            Settings.ProcessRules = loaded.ProcessRules;
            Settings.Subscriptions = loaded.Subscriptions;
            RefreshSubscriptionsView();
            Settings.Mode = loaded.Mode;
            Settings.Port = loaded.Port;
            Settings.AutoConnect = loaded.AutoConnect;
            Settings.SubscriptionAutoUpdateMinutes = loaded.SubscriptionAutoUpdateMinutes;
            Settings.AutoPingMinutes = loaded.AutoPingMinutes;
            Settings.TunEnabled = loaded.TunEnabled;
            Settings.Theme = loaded.Theme;

            Mode = loaded.Mode;
            Theme = loaded.Theme;
            Port = loaded.Port;
            AutoConnect = loaded.AutoConnect;
            SubscriptionAutoUpdateMinutes = loaded.SubscriptionAutoUpdateMinutes;
            AutoPingMinutes = loaded.AutoPingMinutes;
            TunEnabled = loaded.TunEnabled;
            SaveError = null;
        }
        finally
        {
            _suppressSync = false;
        }
    }

    /// <summary>
    /// Persists <see cref="Settings"/> to disk (or <see cref="ConfigFilePath"/> when
    /// set). I/O failures are captured in <see cref="SaveError"/> rather than thrown.
    /// </summary>
    [RelayCommand]
    public void Save()
    {
        SaveError = null;
        try
        {
            if (string.IsNullOrEmpty(ConfigFilePath))
            {
                SettingsService.Save(Settings);
            }
            else
            {
                SettingsService.Save(Settings, ConfigFilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = string.Format(Loc.Get("Error.SaveFailed"), ex.Message);
        }
    }

    /// <summary>Raises <see cref="SettingsChanged"/> on the UI thread.</summary>
    private void NotifyChanged() =>
        _runOnUiThread(() => SettingsChanged?.Invoke(this, EventArgs.Empty));
}
