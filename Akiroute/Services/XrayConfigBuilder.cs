using System.Globalization;
using System.Text.Json.Nodes;
using Akiroute.Helpers;
using Akiroute.Models;

namespace Akiroute.Services;

/// <summary>
/// Builds a complete xray core configuration document (log, inbounds, outbounds,
/// routing) from a <see cref="ProxyNode"/> plus the per-process routing rules.
/// The document is assembled exclusively from <see cref="JsonObject"/> /
/// <see cref="JsonArray"/> / <see cref="JsonValue"/> nodes so it can be handed
/// straight to <c>JsonSerializer.Serialize</c> without reflection — this is the
/// AOT-safe mechanism; no <c>JsonSerializerContext</c> is involved for the
/// dynamic output. The result targets the bundled xray 26.x (see
/// <c>AppPaths.XrayExe</c>) and follows the xray core config v1 schema.
/// </summary>
public static class XrayConfigBuilder
{
    /// <summary>
    /// Builds the full xray configuration for a single proxy node.
    /// </summary>
    /// <param name="node">The selected proxy node. Must have a non-empty
    /// <c>Address</c> and a port in the range 1-65535; protocols that require a
    /// uuid (vless, vmess, tuic) must carry it in <c>ExtraParams["uuid"]</c>.</param>
    /// <param name="processRules">Per-process routing rules. Rules with a null or
    /// blank <c>ProcessName</c> are skipped.</param>
    /// <param name="localPort">Loopback port for the SOCKS inbound; the HTTP
    /// inbound uses <c>localPort + 1</c>.</param>
    /// <param name="mode">Routing mode deciding the catch-all outbound tag.</param>
    /// <returns>A <see cref="JsonObject"/> directly serializable to the config file.</returns>
    /// <exception cref="ArgumentException">The node is null, missing its address,
    /// has an out-of-range port, or is missing a protocol-required uuid.</exception>
    public static JsonObject Build(ProxyNode node, IReadOnlyList<ProcessRule> processRules, int localPort, ProxyMode mode)
    {
        ArgumentNullException.ThrowIfNull(processRules);
        if (node is null)
        {
            throw new ArgumentException("The proxy node must not be null.", nameof(node));
        }
        if (string.IsNullOrEmpty(node.Address))
        {
            throw new ArgumentException("The proxy node must have a non-empty address.", nameof(node));
        }
        if (node.Port is < 1 or > 65535)
        {
            throw new ArgumentException("The proxy node port must be in the range 1-65535.", nameof(node));
        }
        if (localPort is < 1 or > 65534)
        {
            throw new ArgumentException("The local port must be in the range 1-65534.", nameof(localPort));
        }

        AppPaths.EnsureDirectories();

        var config = new JsonObject
        {
            ["log"] = BuildLogSection(),
            ["inbounds"] = new JsonArray
            {
                BuildSocksInbound(localPort),
                BuildHttpInbound(localPort + 1),
            },
            ["outbounds"] = new JsonArray
            {
                BuildProxyOutbound(node),
                new JsonObject { ["protocol"] = "freedom", ["tag"] = "direct" },
                new JsonObject { ["protocol"] = "blackhole", ["tag"] = "block" },
            },
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = BuildRoutingRules(processRules, mode),
            },
        };

        return config;
    }

    /// <summary>Builds the top-level log section pointing at the app log directory.</summary>
    private static JsonObject BuildLogSection() => new()
    {
        ["loglevel"] = "warning",
        ["error"] = Path.Combine(AppPaths.LogsDir, "akiroute-xray-error.log"),
        ["access"] = Path.Combine(AppPaths.LogsDir, "akiroute-xray-access.log"),
    };

    /// <summary>Builds the SOCKS inbound listening on 127.0.0.1 with UDP relay enabled.</summary>
    private static JsonObject BuildSocksInbound(int port) => new()
    {
        ["tag"] = "socks-in",
        ["listen"] = "127.0.0.1",
        ["port"] = JsonValue.Create(port),
        ["protocol"] = "socks",
        ["settings"] = new JsonObject { ["udp"] = true },
    };

    /// <summary>Builds the HTTP inbound listening on 127.0.0.1.</summary>
    private static JsonObject BuildHttpInbound(int port) => new()
    {
        ["tag"] = "http-in",
        ["listen"] = "127.0.0.1",
        ["port"] = JsonValue.Create(port),
        ["protocol"] = "http",
    };

    /// <summary>Dispatches the proxy outbound (tag "proxy") to the node's protocol builder.</summary>
    private static JsonObject BuildProxyOutbound(ProxyNode node) => node.Type switch
    {
        "vless" => BuildVlessOutbound(node),
        "vmess" => BuildVmessOutbound(node),
        "ss" => BuildSsOutbound(node),
        "trojan" => BuildTrojanOutbound(node),
        "hysteria2" => BuildHysteria2Outbound(node),
        "tuic" => BuildTuicOutbound(node),
        _ => throw new ArgumentException($"Unsupported node type: \"{node.Type}\".", nameof(node)),
    };

    /// <summary>Builds a vless outbound: vnext users plus stream settings.</summary>
    private static JsonObject BuildVlessOutbound(ProxyNode node)
    {
        var p = EnsureParams(node);
        var uuid = RequiredString(p, "uuid", node);

        var user = new JsonObject { ["id"] = uuid, ["encryption"] = "none" };
        AddString(user, "flow", Str(p, "flow"));

        var vnext = new JsonObject
        {
            ["address"] = node.Address!,
            ["port"] = JsonValue.Create(node.Port),
            ["users"] = new JsonArray { user },
        };

        var outbound = new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "vless",
            ["settings"] = new JsonObject { ["vnext"] = new JsonArray { vnext } },
        };
        outbound["streamSettings"] = BuildStreamSettings(p, defaultSecurity: "none");
        return outbound;
    }

    /// <summary>Builds a vmess outbound: vnext users plus stream settings.</summary>
    private static JsonObject BuildVmessOutbound(ProxyNode node)
    {
        var p = EnsureParams(node);
        var uuid = RequiredString(p, "uuid", node);

        // The parser stores the TLS marker or the cipher in "security"; the
        // cipher "tls" is not a valid vmess user security value, so fall back
        // to "auto" when the field merely signals TLS.
        var cipher = Str(p, "security");
        if (string.IsNullOrEmpty(cipher) || cipher == "tls")
        {
            cipher = "auto";
        }

        var user = new JsonObject
        {
            ["id"] = uuid,
            ["alterId"] = JsonValue.Create(IntParam(p, "alterId") ?? 0),
            ["security"] = cipher,
        };

        var vnext = new JsonObject
        {
            ["address"] = node.Address!,
            ["port"] = JsonValue.Create(node.Port),
            ["users"] = new JsonArray { user },
        };

        var outbound = new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "vmess",
            ["settings"] = new JsonObject { ["vnext"] = new JsonArray { vnext } },
        };
        outbound["streamSettings"] = BuildVmessStreamSettings(p);
        return outbound;
    }

    /// <summary>Builds an ss outbound: a plain servers array, no stream settings.</summary>
    private static JsonObject BuildSsOutbound(ProxyNode node)
    {
        var p = EnsureParams(node);
        var server = new JsonObject
        {
            ["address"] = node.Address!,
            ["port"] = JsonValue.Create(node.Port),
        };
        AddString(server, "method", Str(p, "method"));
        AddString(server, "password", Str(p, "password"));

        return new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "ss",
            ["settings"] = new JsonObject { ["servers"] = new JsonArray { server } },
        };
    }

    /// <summary>Builds a trojan outbound: a servers array plus TLS/transport settings.</summary>
    private static JsonObject BuildTrojanOutbound(ProxyNode node)
    {
        var p = EnsureParams(node);
        var server = new JsonObject
        {
            ["address"] = node.Address!,
            ["port"] = JsonValue.Create(node.Port),
        };
        AddString(server, "password", Str(p, "password"));

        var outbound = new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "trojan",
            ["settings"] = new JsonObject { ["servers"] = new JsonArray { server } },
        };
        outbound["streamSettings"] = BuildStreamSettings(p, defaultSecurity: "tls");
        return outbound;
    }

    /// <summary>Builds a hysteria2 outbound: servers plus tls/obfs inside settings.</summary>
    private static JsonObject BuildHysteria2Outbound(ProxyNode node)
    {
        var p = EnsureParams(node);
        var server = new JsonObject
        {
            ["address"] = node.Address!,
            ["port"] = JsonValue.Create(node.Port),
        };
        AddString(server, "password", Str(p, "password"));

        var settings = new JsonObject { ["servers"] = new JsonArray { server } };

        var tls = new JsonObject { ["sni"] = Str(p, "sni") ?? node.Address! };
        if (BoolParam(p, "insecure") ?? false)
        {
            tls["insecure"] = true;
        }
        settings["tls"] = tls;

        var obfs = Str(p, "obfs");
        if (!string.IsNullOrEmpty(obfs))
        {
            var obfsSettings = new JsonObject { ["type"] = obfs };
            AddString(obfsSettings, "password", Str(p, "obfsPassword"));
            settings["obfs"] = obfsSettings;
        }

        return new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "hysteria2",
            ["settings"] = settings,
        };
    }

    /// <summary>Builds a tuic outbound: servers plus tls inside settings.</summary>
    private static JsonObject BuildTuicOutbound(ProxyNode node)
    {
        var p = EnsureParams(node);
        var uuid = RequiredString(p, "uuid", node);

        var server = new JsonObject
        {
            ["address"] = node.Address!,
            ["port"] = JsonValue.Create(node.Port),
            ["uuid"] = uuid,
            ["congestion_control"] = Str(p, "congestionControl") ?? "cubic",
        };
        AddString(server, "password", Str(p, "password"));
        AddString(server, "udp_relay_mode", Str(p, "udpRelayMode"));

        var tls = new JsonObject { ["sni"] = Str(p, "sni") ?? node.Address! };
        if (GetAlpn(p) is { } alpn)
        {
            tls["alpn"] = alpn;
        }
        if (BoolParam(p, "allowInsecure") ?? false)
        {
            tls["insecure"] = true;
        }

        var settings = new JsonObject
        {
            ["servers"] = new JsonArray { server },
            ["tls"] = tls,
        };

        return new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "tuic",
            ["settings"] = settings,
        };
    }

    /// <summary>
    /// Builds shared stream settings for vless/trojan: network, security,
    /// reality/tls settings, and the ws/grpc/http transport block.
    /// </summary>
    private static JsonObject BuildStreamSettings(Dictionary<string, JsonNode> p, string defaultSecurity)
    {
        var network = Str(p, "network");
        if (string.IsNullOrEmpty(network))
        {
            network = "tcp";
        }

        var security = Str(p, "security");
        if (string.IsNullOrEmpty(security))
        {
            security = defaultSecurity;
        }

        var stream = new JsonObject { ["network"] = network, ["security"] = security };

        var serverName = Str(p, "sni") ?? Str(p, "host");
        if (security == "reality")
        {
            stream["realitySettings"] = BuildRealitySettings(p, serverName);
        }
        else if (security == "tls")
        {
            stream["tlsSettings"] = BuildTlsSettings(p, serverName);
        }

        switch (network)
        {
            case "ws":
                stream["wsSettings"] = BuildWsSettings(p);
                break;
            case "grpc":
                stream["grpcSettings"] = BuildGrpcSettings(Str(p, "serviceName"));
                break;
            case "http":
                stream["httpSettings"] = BuildHttpSettings(p);
                break;
        }

        return stream;
    }

    /// <summary>
    /// Builds vmess stream settings. The vmess "headerType" maps to a tcp header
    /// type, the ws path, or the http host/path depending on the network.
    /// </summary>
    private static JsonObject BuildVmessStreamSettings(Dictionary<string, JsonNode> p)
    {
        var network = Str(p, "network");
        if (string.IsNullOrEmpty(network))
        {
            network = "tcp";
        }

        var isTls = string.Equals(Str(p, "security"), "tls", StringComparison.Ordinal);
        var stream = new JsonObject { ["network"] = network, ["security"] = isTls ? "tls" : "none" };

        if (isTls)
        {
            var tls = new JsonObject();
            AddString(tls, "serverName", Str(p, "host"));
            stream["tlsSettings"] = tls;
        }

        var headerType = Str(p, "headerType");
        switch (network)
        {
            case "ws":
                stream["wsSettings"] = BuildWsSettings(p);
                break;
            case "http":
                stream["httpSettings"] = BuildHttpSettings(p);
                break;
            case "grpc":
                // vmess links carry the service name in the path field.
                stream["grpcSettings"] = BuildGrpcSettings(Str(p, "path"));
                break;
            case "tcp" when !string.IsNullOrEmpty(headerType) && headerType != "none":
                stream["tcpSettings"] = new JsonObject
                {
                    ["header"] = new JsonObject { ["type"] = headerType },
                };
                break;
        }

        return stream;
    }

    /// <summary>Builds tlsSettings (serverName, fingerprint, allowInsecure, alpn).</summary>
    private static JsonObject BuildTlsSettings(Dictionary<string, JsonNode> p, string? serverName)
    {
        var tls = new JsonObject();
        AddString(tls, "serverName", serverName);
        AddString(tls, "fingerprint", Str(p, "fingerprint"));
        if (BoolParam(p, "allowInsecure") ?? false)
        {
            tls["allowInsecure"] = true;
        }
        if (GetAlpn(p) is { } alpn)
        {
            tls["alpn"] = alpn;
        }
        return tls;
    }

    /// <summary>Builds realitySettings (publicKey, shortId, serverName, fingerprint, spiderX).</summary>
    private static JsonObject BuildRealitySettings(Dictionary<string, JsonNode> p, string? serverName)
    {
        var reality = new JsonObject();
        AddString(reality, "publicKey", Str(p, "publicKey"));
        AddString(reality, "shortId", Str(p, "shortId"));
        AddString(reality, "serverName", serverName);
        AddString(reality, "fingerprint", Str(p, "fingerprint"));
        AddString(reality, "spiderX", Str(p, "spiderX"));
        return reality;
    }

    /// <summary>Builds wsSettings (path plus a headers.Host).</summary>
    private static JsonObject BuildWsSettings(Dictionary<string, JsonNode> p)
    {
        var ws = new JsonObject();
        AddString(ws, "path", Str(p, "path"));
        var host = Str(p, "host");
        if (!string.IsNullOrEmpty(host))
        {
            ws["headers"] = new JsonObject { ["Host"] = host };
        }
        return ws;
    }

    /// <summary>Builds grpcSettings with the given service name.</summary>
    private static JsonObject BuildGrpcSettings(string? serviceName)
    {
        var grpc = new JsonObject();
        AddString(grpc, "serviceName", serviceName);
        return grpc;
    }

    /// <summary>Builds httpSettings (host list plus path) for vmess h2 transport.</summary>
    private static JsonObject BuildHttpSettings(Dictionary<string, JsonNode> p)
    {
        var http = new JsonObject();
        var host = Str(p, "host");
        if (!string.IsNullOrEmpty(host))
        {
            http["host"] = new JsonArray { host };
        }
        AddString(http, "path", Str(p, "path"));
        return http;
    }

    /// <summary>
    /// Builds the routing rules array: one field rule per process rule (proxy/
    /// direct/block), the CN-direct geosite rule for Rule mode, and a catch-all
    /// rule whose outbound depends on the mode.
    /// </summary>
    private static JsonArray BuildRoutingRules(IReadOnlyList<ProcessRule> processRules, ProxyMode mode)
    {
        var rules = new JsonArray();

        foreach (var rule in processRules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.ProcessName))
            {
                continue;
            }

            var outboundTag = rule.Action switch
            {
                ProcessAction.Proxy => "proxy",
                ProcessAction.Direct => "direct",
                ProcessAction.Block => "block",
                _ => "proxy",
            };

            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["process"] = new JsonArray { rule.ProcessName },
                ["outboundTag"] = outboundTag,
            });
        }

        if (mode == ProxyMode.Rule)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["domain"] = new JsonArray { "geosite:cn" },
                ["outboundTag"] = "direct",
            });
        }

        var catchAll = mode is ProxyMode.DirectOnly or ProxyMode.ProcessOnly ? "direct" : "proxy";
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["network"] = "tcp,udp",
            ["outboundTag"] = catchAll,
        });

        return rules;
    }

    /// <summary>Returns the node's ExtraParams or an empty dictionary when absent.</summary>
    private static Dictionary<string, JsonNode> EnsureParams(ProxyNode node) =>
        node.ExtraParams ?? new Dictionary<string, JsonNode>(StringComparer.Ordinal);

    /// <summary>Reads a required string param, throwing a clear error when blank.</summary>
    private static string RequiredString(Dictionary<string, JsonNode> p, string key, ProxyNode node)
    {
        var value = Str(p, key);
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException(
                $"Node of type \"{node.Type}\" is missing required param \"{key}\".", nameof(node));
        }
        return value;
    }

    /// <summary>Reads a param as a string, coercing numbers and booleans to text.</summary>
    private static string? Str(Dictionary<string, JsonNode> p, string key)
    {
        if (!p.TryGetValue(key, out var value) || value is not JsonValue jsonValue)
        {
            return null;
        }
        if (jsonValue.TryGetValue<string>(out var text))
        {
            return text;
        }
        if (jsonValue.TryGetValue<int>(out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }
        if (jsonValue.TryGetValue<bool>(out var flag))
        {
            return flag ? "1" : "0";
        }
        return null;
    }

    /// <summary>Reads a param as an int (accepting JSON numbers and numeric strings).</summary>
    private static int? IntParam(Dictionary<string, JsonNode> p, string key)
    {
        if (!p.TryGetValue(key, out var value) || value is not JsonValue jsonValue)
        {
            return null;
        }
        if (jsonValue.TryGetValue<int>(out var number))
        {
            return number;
        }
        if (jsonValue.TryGetValue<string>(out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    /// <summary>Reads a param as a bool (accepting JSON booleans, "1"/"true", and 1/0).</summary>
    private static bool? BoolParam(Dictionary<string, JsonNode> p, string key)
    {
        if (!p.TryGetValue(key, out var value) || value is not JsonValue jsonValue)
        {
            return null;
        }
        if (jsonValue.TryGetValue<bool>(out var flag))
        {
            return flag;
        }
        if (jsonValue.TryGetValue<string>(out var text))
        {
            return text is "1" or "true";
        }
        if (jsonValue.TryGetValue<int>(out var number))
        {
            return number != 0;
        }
        return null;
    }

    /// <summary>Builds an alpn array from a comma-separated string; null when blank.</summary>
    private static JsonArray? GetAlpn(Dictionary<string, JsonNode> p)
    {
        var alpn = Str(p, "alpn");
        if (string.IsNullOrWhiteSpace(alpn))
        {
            return null;
        }

        var array = new JsonArray();
        foreach (var part in alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            array.Add(part);
        }
        return array.Count > 0 ? array : null;
    }

    /// <summary>Adds a string field to a JSON object, skipping null/blank values.</summary>
    private static void AddString(JsonObject obj, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            obj[name] = value;
        }
    }
}
