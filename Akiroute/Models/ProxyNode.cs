using System.Text.Json.Nodes;

namespace Akiroute.Models;

/// <summary>
/// A single proxy server entry (imported from a subscription or added manually).
/// Plain POCO by design: models are not view models, so there is no change
/// notification and no source-generated properties (which would not serialize).
/// </summary>
public class ProxyNode
{
    /// <summary>Unique node identifier (32-char hex GUID, no dashes).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name shown in the node list.</summary>
    public string? Name { get; set; }

    /// <summary>Protocol type: one of "vless" | "vmess" | "ss" | "trojan" | "hysteria2" | "tuic".</summary>
    public string? Type { get; set; }

    /// <summary>Server host name or IP address.</summary>
    public string? Address { get; set; }

    /// <summary>Server TCP port.</summary>
    public int Port { get; set; }

    /// <summary>Last measured latency in milliseconds; -1 means untested.</summary>
    public int PingMs { get; set; } = -1;

    /// <summary>True when this node is the active selection.</summary>
    public bool IsSelected { get; set; }

    /// <summary>Original import link or config fragment this node came from.</summary>
    public string? RawConfig { get; set; }

    /// <summary>
    /// Protocol-specific fields consumed by the Xray config builder (uuid, alterId,
    /// security, sni, fingerprint, flow, network, headerType, host, path, password,
    /// encryption, auth, obfs, ...). <see cref="System.Text.Json.Nodes.JsonNode"/>
    /// values are serialized natively by System.Text.Json without reflection.
    /// </summary>
    public Dictionary<string, JsonNode>? ExtraParams { get; set; }

    /// <summary>
    /// 订阅来源 ID；null = 手动导入 / Id of the subscription feed this node
    /// came from; null = manual.
    /// </summary>
    public string? SourceSubscriptionId { get; set; }
}
