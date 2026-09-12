using System.Text.Json.Serialization;

namespace Akiroute.Models;

/// <summary>Global proxy routing mode.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProxyMode>))]
public enum ProxyMode
{
    /// <summary>Route all traffic through the selected node.</summary>
    Global,

    /// <summary>Route traffic according to the routing rules.</summary>
    Rule,

    /// <summary>Bypass the proxy entirely; direct connections only.</summary>
    DirectOnly,

    /// <summary>Apply per-process rules only.</summary>
    ProcessOnly,
}

/// <summary>Application color theme.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    /// <summary>Follow the Windows system theme.</summary>
    System,

    /// <summary>Always dark.</summary>
    Dark,

    /// <summary>Always light.</summary>
    Light,
}

/// <summary>
/// Root settings document, persisted as JSON by <c>Services.SettingsService</c>.
/// Plain POCO by design: models are not view models, so there is no change
/// notification and no source-generated properties (which would not serialize).
/// </summary>
public class AppSettings
{
    /// <summary>Id of the currently selected node ("" = none).</summary>
    public string SelectedNodeId { get; set; } = "";

    /// <summary>All known proxy nodes.</summary>
    public List<ProxyNode> Nodes { get; set; } = new();

    /// <summary>Per-process routing rules.</summary>
    public List<ProcessRule> ProcessRules { get; set; } = new();

    /// <summary>Subscription feeds that supply nodes.</summary>
    public List<SubscriptionEntry> Subscriptions { get; set; } = new();

    /// <summary>Global routing mode.</summary>
    public ProxyMode Mode { get; set; } = ProxyMode.Rule;

    /// <summary>Local proxy listening port.</summary>
    public int Port { get; set; } = 3333;

    /// <summary>Connect to the selected node on startup.</summary>
    public bool AutoConnect { get; set; }

    /// <summary>Minutes between automatic subscription updates; 0 = off.</summary>
    public int SubscriptionAutoUpdateMinutes { get; set; }

    /// <summary>自动测速间隔（分钟），0 表示关闭自动测速。</summary>
    public int AutoPingMinutes { get; set; }

    /// <summary>
    /// Route traffic through the Windows TUN adapter. RESERVED, currently NOT
    /// implemented by <see cref="Services.XrayConfigBuilder"/> — no UI exposes
    /// it and it must stay false until TUN support lands.
    /// </summary>
    public bool TunEnabled { get; set; }

    /// <summary>启动时最小化到托盘（Start minimized to tray）。</summary>
    public bool StartMinimized { get; set; }

    /// <summary>开机自动启动（Launch on Windows login via HKCU Run key）。</summary>
    public bool LaunchOnStartup { get; set; }

    /// <summary>Application theme preference.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Saved window X position in physical pixels; null = never saved.</summary>
    public int? WindowX { get; set; }

    /// <summary>Saved window Y position in physical pixels; null = never saved.</summary>
    public int? WindowY { get; set; }

    /// <summary>Saved window width in physical pixels; null = never saved.</summary>
    public int? WindowWidth { get; set; }

    /// <summary>Saved window height in physical pixels; null = never saved.</summary>
    public int? WindowHeight { get; set; }

    /// <summary>
    /// Copies every persisted value from <paramref name="other"/> into this
    /// instance. Collection-valued properties are mutated in place (clear +
    /// refill) so view models and services holding references to THIS instance
    /// observe the new state without any reference swap — the contract the
    /// config-restore flow depends on.
    /// </summary>
    public void CopyFrom(AppSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);

        SelectedNodeId = other.SelectedNodeId;
        Nodes.Clear();
        Nodes.AddRange(other.Nodes);
        ProcessRules.Clear();
        ProcessRules.AddRange(other.ProcessRules);
        Subscriptions.Clear();
        Subscriptions.AddRange(other.Subscriptions);
        Mode = other.Mode;
        Port = other.Port;
        AutoConnect = other.AutoConnect;
        SubscriptionAutoUpdateMinutes = other.SubscriptionAutoUpdateMinutes;
        AutoPingMinutes = other.AutoPingMinutes;
        TunEnabled = other.TunEnabled;
        StartMinimized = other.StartMinimized;
        LaunchOnStartup = other.LaunchOnStartup;
        Theme = other.Theme;
        WindowX = other.WindowX;
        WindowY = other.WindowY;
        WindowWidth = other.WindowWidth;
        WindowHeight = other.WindowHeight;
    }
}
