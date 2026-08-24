using System.Text.Json;
using System.Text.Json.Nodes;
using Akiroute.Models;
using Akiroute.Services;

namespace Akiroute.Tests.Services;

/// <summary>
/// XrayConfigBuilder tests. Pure JSON assertions — no real network, no xray
/// process. Every test builds a config document and inspects the JsonObject.
/// </summary>
public class XrayConfigBuilderTests
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    private const int LocalPort = 3333;

    private static Dictionary<string, JsonNode> Params(params (string Key, string Value)[] entries)
    {
        var result = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            result[key] = JsonValue.Create(value)!;
        }
        return result;
    }

    private static ProxyNode VlessNode() => new()
    {
        Type = "vless",
        Address = "example.com",
        Port = 443,
        ExtraParams = Params(
            ("uuid", Uuid),
            ("encryption", "none"),
            ("security", "tls"),
            ("sni", "example.com"),
            ("network", "tcp")),
    };

    [Fact]
    public void Build_WithVlessNode_HasCompleteSectionsAndBothInbounds()
    {
        // Arrange: a minimal vless node and no process rules.
        var node = VlessNode();

        // Act.
        var config = XrayConfigBuilder.Build(node, Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Global);

        // Assert: every top-level section is present.
        Assert.NotNull(config["log"]);
        Assert.NotNull(config["inbounds"]);
        Assert.NotNull(config["outbounds"]);
        Assert.NotNull(config["routing"]);

        // Assert: two inbounds, socks on localPort and http on localPort + 1.
        var inbounds = config["inbounds"]!.AsArray();
        Assert.Equal(2, inbounds.Count);
        Assert.Equal("socks-in", inbounds[0]!["tag"]!.GetValue<string>());
        Assert.Equal("http-in", inbounds[1]!["tag"]!.GetValue<string>());
        Assert.Equal(LocalPort, inbounds[0]!["port"]!.GetValue<int>());
        Assert.Equal(LocalPort + 1, inbounds[1]!["port"]!.GetValue<int>());
        Assert.True(inbounds[0]!["settings"]!["udp"]!.GetValue<bool>());

        // Assert: three outbounds tagged proxy/direct/block, proxy is vless.
        var outbounds = config["outbounds"]!.AsArray();
        Assert.Equal(3, outbounds.Count);
        Assert.Equal("proxy", outbounds[0]!["tag"]!.GetValue<string>());
        Assert.Equal("vless", outbounds[0]!["protocol"]!.GetValue<string>());
        Assert.Equal("direct", outbounds[1]!["tag"]!.GetValue<string>());
        Assert.Equal("block", outbounds[2]!["tag"]!.GetValue<string>());

        // Assert: log paths live under the app logs directory.
        var log = config["log"]!.AsObject();
        Assert.Equal("warning", log["loglevel"]!.GetValue<string>());
        Assert.EndsWith("akiroute-xray-error.log", log["error"]!.GetValue<string>());
        Assert.EndsWith("akiroute-xray-access.log", log["access"]!.GetValue<string>());
    }

    [Fact]
    public void Build_WithVlessNode_TlsSettingsAndUsersMapped()
    {
        // Arrange: a vless node carrying tls, flow, and alpn params.
        var node = VlessNode();
        node.ExtraParams!["flow"] = "xtls-rprx-vision";
        node.ExtraParams!["allowInsecure"] = JsonValue.Create("1");
        node.ExtraParams!["alpn"] = JsonValue.Create("h2,h3");

        // Act.
        var config = XrayConfigBuilder.Build(node, Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Global);

        // Assert: the vnext user carries id/encryption/flow.
        var users = config["outbounds"]![0]!["settings"]!["vnext"]![0]!["users"]!.AsArray();
        Assert.Equal(Uuid, users[0]!["id"]!.GetValue<string>());
        Assert.Equal("none", users[0]!["encryption"]!.GetValue<string>());
        Assert.Equal("xtls-rprx-vision", users[0]!["flow"]!.GetValue<string>());

        // Assert: streamSettings carries tls + transport.
        var stream = config["outbounds"]![0]!["streamSettings"]!;
        Assert.Equal("tcp", stream["network"]!.GetValue<string>());
        Assert.Equal("tls", stream["security"]!.GetValue<string>());
        var tls = stream["tlsSettings"]!;
        Assert.Equal("example.com", tls["serverName"]!.GetValue<string>());
        Assert.True(tls["allowInsecure"]!.GetValue<bool>());
        Assert.Equal("h2", tls["alpn"]![0]!.GetValue<string>());
        Assert.Equal("h3", tls["alpn"]![1]!.GetValue<string>());
    }

    [Fact]
    public void Build_WithProcessRules_MapsActionsAndOrdersRulesFirst()
    {
        // Arrange: proxy/direct/block rules plus null and blank-name rules to skip.
        IReadOnlyList<ProcessRule> rules = new List<ProcessRule>
        {
            new() { ProcessName = "chrome.exe", Action = ProcessAction.Proxy },
            new() { ProcessName = "firefox.exe", Action = ProcessAction.Direct },
            new() { ProcessName = "steam.exe", Action = ProcessAction.Block },
            null!,
            new() { ProcessName = "", Action = ProcessAction.Direct },
        };

        // Act.
        var config = XrayConfigBuilder.Build(VlessNode(), rules, LocalPort, ProxyMode.Rule);
        var routingRules = config["routing"]!["rules"]!.AsArray();

        // Assert: the three process rules come first, in order, with correct tags.
        Assert.Equal("chrome.exe", routingRules[0]!["process"]![0]!.GetValue<string>());
        Assert.Equal("proxy", routingRules[0]!["outboundTag"]!.GetValue<string>());
        Assert.Equal("firefox.exe", routingRules[1]!["process"]![0]!.GetValue<string>());
        Assert.Equal("direct", routingRules[1]!["outboundTag"]!.GetValue<string>());
        Assert.Equal("steam.exe", routingRules[2]!["process"]![0]!.GetValue<string>());
        Assert.Equal("block", routingRules[2]!["outboundTag"]!.GetValue<string>());

        // Assert: the last rule is the catch-all (after process + geosite rules).
        var last = routingRules[^1]!;
        Assert.Equal("tcp,udp", last["network"]!.GetValue<string>());
        Assert.Equal("proxy", last["outboundTag"]!.GetValue<string>());
    }

    [Fact]
    public void Build_ModeRule_AddsGeositeCnDirectRule()
    {
        // Act: Rule mode with no process rules.
        var config = XrayConfigBuilder.Build(VlessNode(), Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Rule);
        var rules = config["routing"]!["rules"]!.AsArray();

        // Assert: exactly two rules — the CN-direct geosite rule then the catch-all.
        Assert.Equal(2, rules.Count);
        Assert.Equal("geosite:cn", rules[0]!["domain"]![0]!.GetValue<string>());
        Assert.Equal("direct", rules[0]!["outboundTag"]!.GetValue<string>());
        Assert.Equal("proxy", rules[1]!["outboundTag"]!.GetValue<string>());
    }

    [Fact]
    public void Build_ModeDirectOnly_LastRuleRoutesDirect()
    {
        // Act: DirectOnly mode — routing decision only, the proxy outbound stays.
        var config = XrayConfigBuilder.Build(VlessNode(), Array.Empty<ProcessRule>(), LocalPort, ProxyMode.DirectOnly);

        // Assert: the catch-all routes to direct and the proxy outbound is still emitted.
        var rules = config["routing"]!["rules"]!.AsArray();
        Assert.Equal("direct", rules[^1]!["outboundTag"]!.GetValue<string>());
        var outbounds = config["outbounds"]!.AsArray();
        Assert.Equal(3, outbounds.Count);
        Assert.Equal("proxy", outbounds[0]!["tag"]!.GetValue<string>());
    }

    [Fact]
    public void Build_ModeProcessOnly_LastRuleRoutesDirect()
    {
        // Act: ProcessOnly mode — catch-all direct, process rules still applied.
        IReadOnlyList<ProcessRule> rules = new List<ProcessRule>
        {
            new() { ProcessName = "chrome.exe", Action = ProcessAction.Proxy },
        };
        var config = XrayConfigBuilder.Build(VlessNode(), rules, LocalPort, ProxyMode.ProcessOnly);
        var routingRules = config["routing"]!["rules"]!.AsArray();

        // Assert: the process rule precedes a direct catch-all.
        Assert.Equal("proxy", routingRules[0]!["outboundTag"]!.GetValue<string>());
        Assert.Equal("direct", routingRules[^1]!["outboundTag"]!.GetValue<string>());
    }

    [Fact]
    public void Build_WithSsNode_MapsServersMethodAndPassword()
    {
        // Arrange: an ss node.
        var node = new ProxyNode
        {
            Type = "ss",
            Address = "example.com",
            Port = 8388,
            ExtraParams = Params(("method", "aes-256-gcm"), ("password", "testpass123")),
        };

        // Act.
        var config = XrayConfigBuilder.Build(node, Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Global);

        // Assert: the outbound is ss with the method/password on the server entry.
        var outbound = config["outbounds"]![0]!;
        Assert.Equal("ss", outbound["protocol"]!.GetValue<string>());
        var server = outbound["settings"]!["servers"]![0]!;
        Assert.Equal("example.com", server["address"]!.GetValue<string>());
        Assert.Equal(8388, server["port"]!.GetValue<int>());
        Assert.Equal("aes-256-gcm", server["method"]!.GetValue<string>());
        Assert.Equal("testpass123", server["password"]!.GetValue<string>());
        Assert.Null(outbound["streamSettings"]);
    }

    [Fact]
    public void Build_WithHysteria2Node_MapsTlsSniInsecureAndObfs()
    {
        // Arrange: a hysteria2 node with an insecure bool and obfs.
        var node = new ProxyNode
        {
            Type = "hysteria2",
            Address = "example.com",
            Port = 8443,
            ExtraParams = new Dictionary<string, JsonNode>
            {
                ["password"] = JsonValue.Create("authpass")!,
                ["insecure"] = JsonValue.Create(true),
                ["sni"] = JsonValue.Create("example.com")!,
                ["obfs"] = JsonValue.Create("salamander")!,
                ["obfsPassword"] = JsonValue.Create("obfspw")!,
            },
        };

        // Act.
        var config = XrayConfigBuilder.Build(node, Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Global);

        // Assert: settings.tls maps sni/insecure; obfs carries type and password.
        var settings = config["outbounds"]![0]!["settings"]!;
        Assert.Equal("hysteria2", config["outbounds"]![0]!["protocol"]!.GetValue<string>());
        Assert.Equal("example.com", settings["tls"]!["sni"]!.GetValue<string>());
        Assert.True(settings["tls"]!["insecure"]!.GetValue<bool>());
        Assert.Equal("salamander", settings["obfs"]!["type"]!.GetValue<string>());
        Assert.Equal("obfspw", settings["obfs"]!["password"]!.GetValue<string>());
    }

    [Fact]
    public void Build_WithHysteria2Node_InsecureDefaultsToFalse()
    {
        // Arrange: a hysteria2 node with no insecure param.
        var node = new ProxyNode
        {
            Type = "hysteria2",
            Address = "example.com",
            Port = 8443,
            ExtraParams = Params(("password", "authpass")),
        };

        // Act.
        var config = XrayConfigBuilder.Build(node, Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Global);

        // Assert: no insecure field is emitted when absent.
        var tls = config["outbounds"]![0]!["settings"]!["tls"]!;
        Assert.Equal("example.com", tls["sni"]!.GetValue<string>());
        Assert.Null(tls["insecure"]);
    }

    [Fact]
    public void Build_WithTuicNode_MapsServersAndTls()
    {
        // Arrange: a tuic node.
        var node = new ProxyNode
        {
            Type = "tuic",
            Address = "example.com",
            Port = 7777,
            ExtraParams = new Dictionary<string, JsonNode>
            {
                ["uuid"] = JsonValue.Create(Uuid)!,
                ["password"] = JsonValue.Create("tupass")!,
                ["congestionControl"] = JsonValue.Create("bbr")!,
                ["udpRelayMode"] = JsonValue.Create("native")!,
                ["alpn"] = JsonValue.Create("h3")!,
                ["allowInsecure"] = JsonValue.Create(true),
                ["sni"] = JsonValue.Create("example.com")!,
            },
        };

        // Act.
        var config = XrayConfigBuilder.Build(node, Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Global);

        // Assert: server fields and the tls block.
        var outbound = config["outbounds"]![0]!;
        Assert.Equal("tuic", outbound["protocol"]!.GetValue<string>());
        var server = outbound["settings"]!["servers"]![0]!;
        Assert.Equal(Uuid, server["uuid"]!.GetValue<string>());
        Assert.Equal("bbr", server["congestion_control"]!.GetValue<string>());
        Assert.Equal("native", server["udp_relay_mode"]!.GetValue<string>());
        var tls = outbound["settings"]!["tls"]!;
        Assert.Equal("example.com", tls["sni"]!.GetValue<string>());
        Assert.Equal("h3", tls["alpn"]![0]!.GetValue<string>());
        Assert.True(tls["insecure"]!.GetValue<bool>());
    }

    [Fact]
    public void Build_WithVmessNodeAndAlterId_AlterIdIsNumber()
    {
        // Arrange: a vmess node whose alterId arrives as a string (parser output).
        var node = new ProxyNode
        {
            Type = "vmess",
            Address = "203.0.113.10",
            Port = 443,
            ExtraParams = Params(
                ("uuid", Uuid),
                ("alterId", "64"),
                ("security", "tls"),
                ("network", "ws"),
                ("headerType", "none"),
                ("host", "cdn.example.com"),
                ("path", "/ws")),
        };

        // Act.
        var config = XrayConfigBuilder.Build(node, Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Global);

        // Assert: the vmess user carries a JSON number alterId and stream settings.
        var outbound = config["outbounds"]![0]!;
        Assert.Equal("vmess", outbound["protocol"]!.GetValue<string>());
        var user = outbound["settings"]!["vnext"]![0]!["users"]![0]!;
        Assert.Equal(64, user["alterId"]!.GetValue<int>());
        Assert.Equal("auto", user["security"]!.GetValue<string>());
        var stream = outbound["streamSettings"]!;
        Assert.Equal("ws", stream["network"]!.GetValue<string>());
        Assert.Equal("tls", stream["security"]!.GetValue<string>());
        Assert.Equal("/ws", stream["wsSettings"]!["path"]!.GetValue<string>());
        Assert.Equal("cdn.example.com", stream["wsSettings"]!["headers"]!["Host"]!.GetValue<string>());
    }

    [Fact]
    public void Build_NullNode_ThrowsArgumentException()
    {
        // Act + Assert: a null node is a programming error surfaced as ArgumentException.
        Assert.Throws<ArgumentException>(() =>
            XrayConfigBuilder.Build(null!, Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Global));
    }

    [Fact]
    public void Build_NodeWithoutAddress_ThrowsArgumentException()
    {
        // Arrange: a node missing its address.
        var node = new ProxyNode { Type = "vless", Port = 443 };

        // Act + Assert.
        Assert.Throws<ArgumentException>(() =>
            XrayConfigBuilder.Build(node, Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Global));
    }

    [Fact]
    public void Build_NodeWithoutUuid_ThrowsArgumentException()
    {
        // Arrange: a vless node with no uuid in ExtraParams.
        var node = VlessNode();
        node.ExtraParams!.Remove("uuid");

        // Act + Assert.
        var exception = Assert.Throws<ArgumentException>(() =>
            XrayConfigBuilder.Build(node, Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Global));
        Assert.Contains("uuid", exception.Message);
    }

    [Fact]
    public void Build_UnsupportedType_ThrowsArgumentException()
    {
        // Arrange: a node whose protocol is not supported by the builder.
        var node = new ProxyNode { Type = "socks5", Address = "example.com", Port = 1080 };

        // Act + Assert.
        Assert.Throws<ArgumentException>(() =>
            XrayConfigBuilder.Build(node, Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Global));
    }

    [Fact]
    public void Build_ResultSerializesAndRoundTripsWithoutThrowing()
    {
        // Arrange: a full config for a vless node in Rule mode.
        var config = XrayConfigBuilder.Build(VlessNode(), Array.Empty<ProcessRule>(), LocalPort, ProxyMode.Rule);

        // Act: serialize the JsonObject, then parse it back.
        var json = JsonSerializer.Serialize(config);
        var reparsed = JsonNode.Parse(json);

        // Assert: the document survives the round trip with identical structure.
        Assert.NotNull(reparsed);
        Assert.Equal("vless", reparsed!["outbounds"]![0]!["protocol"]!.GetValue<string>());
        Assert.Equal(LocalPort, reparsed["inbounds"]![0]!["port"]!.GetValue<int>());
        Assert.Equal("warning", reparsed["log"]!["loglevel"]!.GetValue<string>());
        Assert.Equal("IPIfNonMatch", reparsed["routing"]!["domainStrategy"]!.GetValue<string>());
    }

    /// <summary>
    /// Theory covering all four ProxyMode values with two process rules (one
    /// Direct, one Block). Asserts: correct outboundTag per rule present in
    /// routing rules; geosite:cn rule present iff mode == Rule; catch-all
    /// outboundTag == "proxy" for Global/Rule and "direct" for DirectOnly/ProcessOnly.
    /// </summary>
    [Theory]
    [InlineData(ProxyMode.Global)]
    [InlineData(ProxyMode.Rule)]
    [InlineData(ProxyMode.DirectOnly)]
    [InlineData(ProxyMode.ProcessOnly)]
    public void Build_AllModes_ProcessRulesAndCatchAll(ProxyMode mode)
    {
        // Arrange: two process rules — one Direct, one Block.
        IReadOnlyList<ProcessRule> rules = new List<ProcessRule>
        {
            new() { ProcessName = "notepad.exe", Action = ProcessAction.Direct },
            new() { ProcessName = "steam.exe", Action = ProcessAction.Block },
        };

        // Act.
        var config = XrayConfigBuilder.Build(VlessNode(), rules, LocalPort, mode);
        var routingRules = config["routing"]!["rules"]!.AsArray();

        // Assert: the two process rules are present with correct outboundTags.
        Assert.Equal("notepad.exe", routingRules[0]!["process"]![0]!.GetValue<string>());
        Assert.Equal("direct", routingRules[0]!["outboundTag"]!.GetValue<string>());
        Assert.Equal("steam.exe", routingRules[1]!["process"]![0]!.GetValue<string>());
        Assert.Equal("block", routingRules[1]!["outboundTag"]!.GetValue<string>());

        // Assert: geosite:cn rule present iff mode == Rule.
        var hasGeositeCn = false;
        for (var i = 0; i < routingRules.Count; i++)
        {
            var domain = routingRules[i]!["domain"];
            if (domain is not null && domain.AsArray().Count > 0
                && domain![0]!.GetValue<string>() == "geosite:cn")
            {
                hasGeositeCn = true;
                Assert.Equal("direct", routingRules[i]!["outboundTag"]!.GetValue<string>());
            }
        }

        Assert.Equal(mode == ProxyMode.Rule, hasGeositeCn);

        // Assert: catch-all (last rule) outboundTag depends on mode.
        var expectedCatchAll = mode is ProxyMode.DirectOnly or ProxyMode.ProcessOnly ? "direct" : "proxy";
        Assert.Equal("tcp,udp", routingRules[^1]!["network"]!.GetValue<string>());
        Assert.Equal(expectedCatchAll, routingRules[^1]!["outboundTag"]!.GetValue<string>());
    }
}
