using System.Text.Json.Nodes;
using Akiroute.Models;
using Akiroute.Services;

namespace Akiroute.Tests.Services;

/// <summary>
/// ClashConfigParser tests. The parser reads a Clash YAML proxy list using
/// YamlDotNet's DOM-only API (no deserializer) and must never throw for
/// hostile or malformed input.
/// </summary>
public class ClashConfigParserTests
{
    private static ProxyNode ByName(List<ProxyNode> nodes, string name) =>
        Assert.Single(nodes, n => n.Name == name);

    private static string Str(ProxyNode node, string key) =>
        node.ExtraParams![key]!.GetValue<string>();

    private static int Int(ProxyNode node, string key) =>
        node.ExtraParams![key]!.GetValue<int>();

    private static bool Bool(ProxyNode node, string key) =>
        node.ExtraParams![key]!.GetValue<bool>();

    [Fact]
    public void Parse_WithOneNodeOfEachSupportedType_MapsCoreFieldsAndExtraParams()
    {
        // Arrange: a Clash config with one node per supported protocol.
        var yaml = """
            proxies:
              - name: "SS-1"
                type: ss
                server: 203.0.113.1
                port: 8388
                cipher: aes-256-gcm
                password: "ss-password"
                udp: true

              - name: "Vmess-1"
                type: vmess
                server: 203.0.113.2
                port: 443
                uuid: 11111111-1111-1111-1111-111111111111
                alterId: 0
                cipher: auto
                tls: true
                ws-opts:
                  path: /vmess
                  headers:
                    Host: vmess.example.com

              - name: "Vless-1"
                type: vless
                server: 203.0.113.3
                port: 443
                uuid: 22222222-2222-2222-2222-222222222222
                tls: true
                servername: vless.example.com
                flow: xtls-rprx-vision
                client-fingerprint: chrome
                reality-opts:
                  public-key: "REALITY_PUBLIC_KEY"
                  short-id: "abcd1234"
                  server-name: reality.example.com
                grpc-opts:
                  grpc-service-name: "vless-grpc"

              - name: "Trojan-1"
                type: trojan
                server: 203.0.113.4
                port: 443
                password: "trojan-password"
                sni: trojan.example.com
                skip-cert-verify: true
                alpn:
                  - h2
                  - http/1.1

              - name: "Hy2-1"
                type: hysteria2
                server: 203.0.113.5
                port: 443
                password: "hy2-password"
                sni: hy2.example.com
                obfs: salamander
                obfs-password: "obfs-secret"
                skip-cert-verify: true

              - name: "Tuic-1"
                type: tuic
                server: 203.0.113.6
                port: 443
                uuid: 33333333-3333-3333-3333-333333333333
                password: "tuic-password"
                congestion-controller: bbr
                udp-relay-mode: native
                alpn:
                  - h3
            """;

        // Act.
        var nodes = ClashConfigParser.Parse(yaml);

        // Assert: all six supported nodes are present with distinct ids.
        Assert.Equal(6, nodes.Count);
        Assert.Equal(6, nodes.Select(n => n.Id).Distinct().Count());
        Assert.All(nodes, n => Assert.Matches("^[0-9a-fA-F]{32}$", n.Id));

        // ss: cipher -> method, password, udp.
        var ss = ByName(nodes, "SS-1");
        Assert.Equal("ss", ss.Type);
        Assert.Equal("203.0.113.1", ss.Address);
        Assert.Equal(8388, ss.Port);
        Assert.Equal("aes-256-gcm", Str(ss, "method"));
        Assert.Equal("ss-password", Str(ss, "password"));
        Assert.True(Bool(ss, "udp"));

        // RawConfig is a compact JSON snapshot of the raw mapping.
        var raw = JsonNode.Parse(ss.RawConfig!)!;
        Assert.Equal("SS-1", raw["name"]!.GetValue<string>());
        Assert.Equal(8388L, raw["port"]!.GetValue<long>());

        // vmess: ws-opts presence implies network "ws" plus path and host.
        var vmess = ByName(nodes, "Vmess-1");
        Assert.Equal("vmess", vmess.Type);
        Assert.Equal("203.0.113.2", vmess.Address);
        Assert.Equal(443, vmess.Port);
        Assert.Equal("11111111-1111-1111-1111-111111111111", Str(vmess, "uuid"));
        Assert.Equal(0, Int(vmess, "alterId"));
        Assert.Equal("auto", Str(vmess, "security"));
        Assert.True(Bool(vmess, "tls"));
        Assert.Equal("ws", Str(vmess, "network"));
        Assert.Equal("/vmess", Str(vmess, "path"));
        Assert.Equal("vmess.example.com", Str(vmess, "host"));

        // vless: reality overrides security/sni; grpc-opts implies network "grpc".
        var vless = ByName(nodes, "Vless-1");
        Assert.Equal("vless", vless.Type);
        Assert.Equal("22222222-2222-2222-2222-222222222222", Str(vless, "uuid"));
        Assert.Equal("reality", Str(vless, "security"));
        Assert.Equal("reality.example.com", Str(vless, "sni"));
        Assert.Equal("REALITY_PUBLIC_KEY", Str(vless, "publicKey"));
        Assert.Equal("abcd1234", Str(vless, "shortId"));
        Assert.Equal("xtls-rprx-vision", Str(vless, "flow"));
        Assert.Equal("chrome", Str(vless, "fingerprint"));
        Assert.Equal("grpc", Str(vless, "network"));
        Assert.Equal("vless-grpc", Str(vless, "serviceName"));
        Assert.True(Bool(vless, "tls"));

        // trojan: alpn is joined with ","; skip-cert-verify -> allowInsecure.
        var trojan = ByName(nodes, "Trojan-1");
        Assert.Equal("trojan", trojan.Type);
        Assert.Equal("trojan-password", Str(trojan, "password"));
        Assert.Equal("trojan.example.com", Str(trojan, "sni"));
        Assert.True(Bool(trojan, "allowInsecure"));
        Assert.Equal("h2,http/1.1", Str(trojan, "alpn"));
        Assert.False(trojan.ExtraParams!.ContainsKey("network"));

        // hysteria2: skip-cert-verify maps to "insecure", never "allowInsecure".
        var hy2 = ByName(nodes, "Hy2-1");
        Assert.Equal("hysteria2", hy2.Type);
        Assert.Equal("hy2-password", Str(hy2, "password"));
        Assert.Equal("hy2.example.com", Str(hy2, "sni"));
        Assert.Equal("salamander", Str(hy2, "obfs"));
        Assert.Equal("obfs-secret", Str(hy2, "obfsPassword"));
        Assert.True(Bool(hy2, "insecure"));
        Assert.False(hy2.ExtraParams!.ContainsKey("allowInsecure"));

        // tuic: transport tuning fields.
        var tuic = ByName(nodes, "Tuic-1");
        Assert.Equal("tuic", tuic.Type);
        Assert.Equal("33333333-3333-3333-3333-333333333333", Str(tuic, "uuid"));
        Assert.Equal("tuic-password", Str(tuic, "password"));
        Assert.Equal("bbr", Str(tuic, "congestionControl"));
        Assert.Equal("native", Str(tuic, "udpRelayMode"));
        Assert.Equal("h3", Str(tuic, "alpn"));
    }

    [Fact]
    public void Parse_WhenUnsupportedTypes_AreSkipped_KeepsSupportedOnes()
    {
        // Arrange: ssr and socks5 are not importable proxy types.
        var yaml = """
            proxies:
              - name: "SSR-1"
                type: ssr
                server: 203.0.113.10
                port: 8388
              - name: "Socks-1"
                type: socks5
                server: 203.0.113.11
                port: 1080
              - name: "SS-ok"
                type: ss
                server: 203.0.113.12
                port: 8388
                cipher: chacha20-ietf-poly1305
                password: "pw"
            """;

        // Act.
        var result = ClashConfigParser.Parse(yaml);

        // Assert: only the supported node survives.
        var node = Assert.Single(result);
        Assert.Equal("SS-ok", node.Name);
        Assert.Equal("ss", node.Type);
        Assert.Equal("203.0.113.12", node.Address);
    }

    [Fact]
    public void Parse_WhenDuplicateNames_KeepsBothWithDistinctIds()
    {
        // Arrange: two nodes sharing a display name.
        var yaml = """
            proxies:
              - name: "Same"
                type: ss
                server: 203.0.113.20
                port: 8388
                cipher: aes-256-gcm
                password: "pw-1"
              - name: "Same"
                type: trojan
                server: 203.0.113.21
                port: 443
                password: "pw-2"
            """;

        // Act.
        var result = ClashConfigParser.Parse(yaml);

        // Assert: both are kept and the Id keeps them distinct.
        Assert.Equal(2, result.Count);
        Assert.All(result, n => Assert.Equal("Same", n.Name));
        Assert.Equal(2, result.Select(n => n.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("proxies: []")]
    [InlineData("mode: rule")]
    public void Parse_WhenNoProxies_ReturnsEmptyList(string yaml)
    {
        // Act: no proxies key, an empty proxies sequence, or an empty document.
        var result = ClashConfigParser.Parse(yaml);

        // Assert: no nodes, no exception.
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("key: [unclosed")]
    [InlineData("{{{{{{{{")]
    [InlineData("\t\troot indented with a tab")]
    public void Parse_WhenYamlMalformed_ReturnsEmptyListWithoutThrowing(string yaml)
    {
        // Act: garbage input must never throw.
        var result = ClashConfigParser.Parse(yaml);

        // Assert: empty result instead of an exception.
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_BoolFields_CoerceFromOneAndZero()
    {
        // Arrange: booleans expressed as YAML "1"/"0" strings and integers.
        var yaml = """
            proxies:
              - name: "SS-coerce"
                type: ss
                server: 203.0.113.30
                port: 8388
                cipher: aes-256-gcm
                password: "pw"
                udp: "1"
                skip-cert-verify: "0"
              - name: "Trojan-coerce"
                type: trojan
                server: 203.0.113.31
                port: 443
                password: "pw"
                skip-cert-verify: 1
            """;

        // Act.
        var result = ClashConfigParser.Parse(yaml);

        // Assert: "1"/1 become true, "0" becomes false.
        var ss = ByName(result, "SS-coerce");
        Assert.True(Bool(ss, "udp"));
        Assert.False(Bool(ss, "allowInsecure"));

        var trojan = ByName(result, "Trojan-coerce");
        Assert.True(Bool(trojan, "allowInsecure"));
    }
}
