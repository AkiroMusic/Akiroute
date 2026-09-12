using System.Globalization;
using System.Text.Json.Nodes;
using Akiroute.Models;
using YamlDotNet.RepresentationModel;

namespace Akiroute.Services;

/// <summary>
/// Maps a single Clash proxy entry (a <see cref="YamlMappingNode"/> from the
/// <c>proxies:</c> sequence) to a <see cref="ProxyNode"/>: core fields, the
/// protocol-specific <see cref="ProxyNode.ExtraParams"/>, and a compact JSON
/// snapshot of the raw mapping for <see cref="ProxyNode.RawConfig"/>.
/// </summary>
internal static class ClashNodeMapper
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "ss", "vmess", "vless", "trojan", "hysteria2", "tuic",
    };

    /// <summary>
    /// Builds a <see cref="ProxyNode"/> from one <c>proxies:</c> entry, or null
    /// when the entry uses an unsupported protocol or lacks <c>name</c>/<c>server</c>.
    /// </summary>
    public static ProxyNode? MapProxyNode(YamlMappingNode map)
    {
        var type = YamlMapReader.GetString(map, "type");
        if (type is null || !SupportedTypes.Contains(type))
        {
            return null;
        }

        var name = YamlMapReader.GetString(map, "name");
        var server = YamlMapReader.GetString(map, "server");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(server))
        {
            return null;
        }

        // A missing or non-numeric port maps to 0; such a node can never build
        // a working config, so reject it at import time.
        var port = YamlMapReader.GetInt(map, "port");
        if (port is < 1 or > 65535)
        {
            return null;
        }

        var extra = new Dictionary<string, JsonNode>();
        PopulateExtraParams(map, type, extra);

        return new ProxyNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Type = type,
            Address = server,
            Port = port,
            RawConfig = BuildRawConfig(map),
            ExtraParams = extra,
        };
    }

    private static void PopulateExtraParams(YamlMappingNode map, string type, Dictionary<string, JsonNode> extra)
    {
        MapCommonFields(map, type, extra);

        switch (type)
        {
            case "ss": MapSs(map, extra); break;
            case "vmess": MapVmess(map, extra); break;
            case "vless": MapVless(map, extra); break;
            case "trojan": MapTrojan(map, extra); break;
            case "hysteria2": MapHysteria2(map, extra); break;
            case "tuic": MapTuic(map, extra); break;
        }
    }

    /// <summary>
    /// Fields shared by every protocol. The presence of <c>tls</c> is recorded
    /// as a bool and, for protocols with no cipher-derived security (vless),
    /// implies the default <c>security</c> of "tls".
    /// </summary>
    private static void MapCommonFields(YamlMappingNode map, string type, Dictionary<string, JsonNode> extra)
    {
        if (YamlMapReader.TryGetValue(map, "udp", out _))
        {
            extra["udp"] = JsonValue.Create(YamlMapReader.GetBool(map, "udp"));
        }

        SetString(extra, "sni", YamlMapReader.GetString(map, "sni"));
        SetString(extra, "fingerprint", YamlMapReader.GetString(map, "client-fingerprint"));
        SetString(extra, "flow", YamlMapReader.GetString(map, "flow"));

        var alpn = YamlMapReader.GetJoinedSequence(map, "alpn");
        if (alpn is not null)
        {
            extra["alpn"] = JsonValue.Create(alpn);
        }

        if (YamlMapReader.TryGetValue(map, "tls", out _))
        {
            extra["tls"] = JsonValue.Create(YamlMapReader.GetBool(map, "tls"));
            if (type == "vless" && !extra.ContainsKey("security"))
            {
                extra["security"] = JsonValue.Create("tls");
            }
        }

        if (type != "hysteria2" && YamlMapReader.TryGetValue(map, "skip-cert-verify", out _))
        {
            extra["allowInsecure"] = JsonValue.Create(YamlMapReader.GetBool(map, "skip-cert-verify"));
        }
    }

    private static void MapSs(YamlMappingNode map, Dictionary<string, JsonNode> extra)
    {
        SetString(extra, "method", YamlMapReader.GetString(map, "cipher"));
        SetString(extra, "password", YamlMapReader.GetString(map, "password"));
    }

    private static void MapVmess(YamlMappingNode map, Dictionary<string, JsonNode> extra)
    {
        SetString(extra, "uuid", YamlMapReader.GetString(map, "uuid"));

        if (YamlMapReader.TryGetValue(map, "alterId", out _))
        {
            extra["alterId"] = JsonValue.Create(YamlMapReader.GetInt(map, "alterId"));
        }

        // Clash "cipher" is the vmess encryption/security method (e.g. "auto").
        SetString(extra, "security", YamlMapReader.GetString(map, "cipher"));
        ApplyTransport(map, extra);
    }

    private static void MapVless(YamlMappingNode map, Dictionary<string, JsonNode> extra)
    {
        SetString(extra, "uuid", YamlMapReader.GetString(map, "uuid"));
        SetString(extra, "sni", YamlMapReader.GetString(map, "servername"));

        var reality = YamlMapReader.GetMapping(map, "reality-opts");
        if (reality is not null)
        {
            extra["security"] = JsonValue.Create("reality");
            SetString(extra, "publicKey", YamlMapReader.GetString(reality, "public-key"));
            SetString(extra, "shortId", YamlMapReader.GetString(reality, "short-id"));
            // server-name is the authoritative SNI for a reality node.
            SetString(extra, "sni", YamlMapReader.GetString(reality, "server-name"));
        }

        ApplyTransport(map, extra);
    }

    private static void MapTrojan(YamlMappingNode map, Dictionary<string, JsonNode> extra)
    {
        SetString(extra, "password", YamlMapReader.GetString(map, "password"));
        ApplyTransport(map, extra);
    }

    private static void MapHysteria2(YamlMappingNode map, Dictionary<string, JsonNode> extra)
    {
        SetString(extra, "password", YamlMapReader.GetString(map, "password"));
        SetString(extra, "obfs", YamlMapReader.GetString(map, "obfs"));
        SetString(extra, "obfsPassword", YamlMapReader.GetString(map, "obfs-password"));

        if (YamlMapReader.TryGetValue(map, "skip-cert-verify", out _))
        {
            extra["insecure"] = JsonValue.Create(YamlMapReader.GetBool(map, "skip-cert-verify"));
        }
    }

    private static void MapTuic(YamlMappingNode map, Dictionary<string, JsonNode> extra)
    {
        SetString(extra, "uuid", YamlMapReader.GetString(map, "uuid"));
        SetString(extra, "password", YamlMapReader.GetString(map, "password"));
        SetString(extra, "congestionControl", YamlMapReader.GetString(map, "congestion-controller"));
        SetString(extra, "udpRelayMode", YamlMapReader.GetString(map, "udp-relay-mode"));

        if (YamlMapReader.TryGetValue(map, "disable-sni", out var disable) &&
            disable is YamlScalarNode node &&
            YamlMapReader.ParseBool(node.Value))
        {
            extra["sni"] = JsonValue.Create("");
        }
    }

    /// <summary>
    /// Resolves the transport for vmess/vless/trojan: the explicit <c>network</c>
    /// key, overridden by <c>ws-opts</c> (network "ws", plus path and
    /// headers.Host) and then by <c>grpc-opts</c> (network "grpc", plus
    /// grpc-service-name).
    /// </summary>
    private static void ApplyTransport(YamlMappingNode map, Dictionary<string, JsonNode> extra)
    {
        var network = YamlMapReader.GetString(map, "network");

        var wsOpts = YamlMapReader.GetMapping(map, "ws-opts");
        if (wsOpts is not null)
        {
            network = "ws";
            SetString(extra, "path", YamlMapReader.GetString(wsOpts, "path"));
            var headers = YamlMapReader.GetMapping(wsOpts, "headers");
            var host = headers is null
                ? null
                : YamlMapReader.GetString(headers, "Host") ?? YamlMapReader.GetString(headers, "host");
            SetString(extra, "host", host);
        }

        var grpcOpts = YamlMapReader.GetMapping(map, "grpc-opts");
        if (grpcOpts is not null)
        {
            network = "grpc";
            SetString(extra, "serviceName", YamlMapReader.GetString(grpcOpts, "grpc-service-name"));
        }

        if (network is not null)
        {
            extra["network"] = JsonValue.Create(network);
        }
    }

    /// <summary>Adds a string parameter, skipping null values.</summary>
    private static void SetString(Dictionary<string, JsonNode> extra, string key, string? value)
    {
        if (value is not null)
        {
            extra[key] = JsonValue.Create(value);
        }
    }

    /// <summary>
    /// Re-serializes the raw proxy mapping as a compact JSON string using
    /// <see cref="JsonObject"/>/<see cref="JsonValue"/> (no reflection). Scalars
    /// are typed best-effort (bool/long/double), everything else stays a string.
    /// </summary>
    private static string BuildRawConfig(YamlMappingNode map)
    {
        var root = new JsonObject();
        foreach (var pair in map.Children)
        {
            if (pair.Key is not YamlScalarNode key || key.Value is null)
            {
                continue;
            }

            var value = NodeToJsonNode(pair.Value);
            if (value is not null)
            {
                root[key.Value] = value;
            }
        }

        return root.ToJsonString();
    }

    private static JsonNode? NodeToJsonNode(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                return ScalarToJsonNode(scalar.Value);
            case YamlMappingNode mapping:
                var obj = new JsonObject();
                foreach (var pair in mapping.Children)
                {
                    if (pair.Key is not YamlScalarNode key || key.Value is null)
                    {
                        continue;
                    }

                    var value = NodeToJsonNode(pair.Value);
                    if (value is not null)
                    {
                        obj[key.Value] = value;
                    }
                }

                return obj;
            case YamlSequenceNode sequence:
                var array = new JsonArray();
                foreach (var item in sequence.Children)
                {
                    var value = NodeToJsonNode(item);
                    if (value is not null)
                    {
                        array.Add(value);
                    }
                }

                return array;
            default:
                // Aliases and other node kinds have no JSON equivalent here.
                return null;
        }
    }

    /// <summary>Typed JSON representation of a YAML scalar; null scalars are skipped.</summary>
    private static JsonNode? ScalarToJsonNode(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is "true")
        {
            return JsonValue.Create(true);
        }

        if (value is "false")
        {
            return JsonValue.Create(false);
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return JsonValue.Create(longValue);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return JsonValue.Create(doubleValue);
        }

        return JsonValue.Create(value);
    }
}
