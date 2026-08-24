using System.Text;
using System.Text.Json.Nodes;
using Akiroute.Models;
using Akiroute.Services;

namespace Akiroute.Tests.Services;

/// <summary>
/// NodeLinkParser tests: one valid link per protocol, one malformed link per
/// protocol (all skipped), mixed multi-link input, IPv6 bracket hosts, and
/// URL-decoded fragment names (including CJK).
/// </summary>
public class NodeLinkParserTests
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    private const string SsUserInfo = "YWVzLTI1Ni1nY206dGVzdHBhc3MxMjM=";          // aes-256-gcm:testpass123
    private const string SsLegacyPayload = "YWVzLTI1Ni1nY206dGVzdHBhc3NAZXhhbXBsZS5jb206ODM4OA=="; // aes-256-gcm:testpass@example.com:8388
    private const string VmessPayload =
        "eyJ2IjoiMiIsInBzIjoiVk1lc3MgTm9kZSIsImFkZCI6IjIwMy4wLjExMy4xMCIsInBvcnQiOiI0NDMiLCJpZCI6ImI4MzEzODFkLTYzMjQtNGQ1My1hZDRmLThjZGE0OGIzMDgxMSIsImFpZCI6IjAiLCJzY3kiOiJhdXRvIiwibmV0Ijoid3MiLCJ0eXBlIjoibm9uZSIsImhvc3QiOiJjZG4uZXhhbXBsZS5jb20iLCJwYXRoIjoiL3dzIiwidGxzIjoidGxzIn0=";
    private const string NotJson = "dGhpcyBpcyBub3QganNvbg==";                    // "this is not json"

    private static JsonNode? GetParam(ProxyNode node, string key) =>
        node.ExtraParams is not null && node.ExtraParams.TryGetValue(key, out var value) ? value : null;

    private static string? GetStringParam(ProxyNode node, string key) =>
        GetParam(node, key)?.GetValue<string>();

    private static bool? GetBoolParam(ProxyNode node, string key) =>
        GetParam(node, key)?.GetValue<bool>();

    private static ProxyNode Single(string rawLink)
    {
        var nodes = NodeLinkParser.Parse(rawLink);
        Assert.Single(nodes);
        return nodes[0];
    }

    [Fact]
    public void Parse_ShadowsocksLink_SetsTypeAddressPortNameAndParams()
    {
        // Arrange: a SIP002 ss link with a URL-encoded fragment name.
        var raw = $"ss://{SsUserInfo}@example.com:8388#Tokyo%20SS";

        // Act: parse the single link.
        var node = Single(raw);

        // Assert: core fields and the method/password params.
        Assert.Equal("ss", node.Type);
        Assert.Equal("example.com", node.Address);
        Assert.Equal(8388, node.Port);
        Assert.Equal("Tokyo SS", node.Name);
        Assert.Equal(raw, node.RawConfig);
        Assert.Equal("aes-256-gcm", GetStringParam(node, "method"));
        Assert.Equal("testpass123", GetStringParam(node, "password"));
    }

    [Fact]
    public void Parse_ShadowsocksLegacyForm_ParsesUserInfoAndHostFromBase64()
    {
        // Arrange: the legacy ss form where method:password@host:port is all inside base64.
        var raw = $"ss://{SsLegacyPayload}?plugin=obfs-local;obfs=http#Legacy%20SS";

        // Act: parse the single link.
        var node = Single(raw);

        // Assert: fields decoded from the legacy payload.
        Assert.Equal("ss", node.Type);
        Assert.Equal("example.com", node.Address);
        Assert.Equal(8388, node.Port);
        Assert.Equal("Legacy SS", node.Name);
        Assert.Equal("aes-256-gcm", GetStringParam(node, "method"));
        Assert.Equal("testpass", GetStringParam(node, "password"));
    }

    [Fact]
    public void Parse_VmessLink_SetsTypeAddressPortNameAndParams()
    {
        // Arrange: a vmess link carrying a base64 JSON payload.
        var raw = $"vmess://{VmessPayload}";

        // Act: parse the single link.
        var node = Single(raw);

        // Assert: JSON fields are surfaced as typed params.
        Assert.Equal("vmess", node.Type);
        Assert.Equal("203.0.113.10", node.Address);
        Assert.Equal(443, node.Port);
        Assert.Equal("VMess Node", node.Name);
        Assert.Equal(Uuid, GetStringParam(node, "uuid"));
        Assert.Equal("0", GetStringParam(node, "alterId"));
        Assert.Equal("tls", GetStringParam(node, "security"));
        Assert.Equal("ws", GetStringParam(node, "network"));
        Assert.Equal("none", GetStringParam(node, "headerType"));
        Assert.Equal("cdn.example.com", GetStringParam(node, "host"));
        Assert.Equal("/ws", GetStringParam(node, "path"));
    }

    [Fact]
    public void Parse_VlessLink_SetsTypeAddressPortNameAndParams()
    {
        // Arrange: a vless link exercising the query-param remapping (fp/pbk/sid/type).
        var raw =
            $"vless://{Uuid}@example.com:443?encryption=none&security=tls&sni=example.com&fp=chrome" +
            "&pbk=abc123&sid=def456&flow=xtls-rprx-vision&type=ws&headerType=none&host=example.com" +
            "&path=%2Fvless&serviceName=svc&allowInsecure=1&alpn=h2%2Ch3#VLESS%20Node";

        // Act: parse the single link.
        var node = Single(raw);

        // Assert: core fields plus every remapped query param.
        Assert.Equal("vless", node.Type);
        Assert.Equal("example.com", node.Address);
        Assert.Equal(443, node.Port);
        Assert.Equal("VLESS Node", node.Name);
        Assert.Equal(Uuid, GetStringParam(node, "uuid"));
        Assert.Equal("none", GetStringParam(node, "encryption"));
        Assert.Equal("tls", GetStringParam(node, "security"));
        Assert.Equal("example.com", GetStringParam(node, "sni"));
        Assert.Equal("chrome", GetStringParam(node, "fingerprint"));
        Assert.Equal("abc123", GetStringParam(node, "publicKey"));
        Assert.Equal("def456", GetStringParam(node, "shortId"));
        Assert.Equal("xtls-rprx-vision", GetStringParam(node, "flow"));
        Assert.Equal("ws", GetStringParam(node, "network"));
        Assert.Equal("none", GetStringParam(node, "headerType"));
        Assert.Equal("example.com", GetStringParam(node, "host"));
        Assert.Equal("/vless", GetStringParam(node, "path"));
        Assert.Equal("svc", GetStringParam(node, "serviceName"));
        Assert.Equal("1", GetStringParam(node, "allowInsecure"));
        Assert.Equal("h2,h3", GetStringParam(node, "alpn"));
    }

    [Fact]
    public void Parse_TrojanLink_SetsTypeAddressPortNameAndParams()
    {
        // Arrange: a trojan link with ws transport query params.
        var raw =
            $"trojan://password123@example.com:443?security=tls&sni=example.com&allowInsecure=1" +
            "&type=ws&host=example.com&path=%2Ftrojan&serviceName=svc&alpn=h2#Trojan%20Node";

        // Act: parse the single link.
        var node = Single(raw);

        // Assert: core fields plus password and transport params.
        Assert.Equal("trojan", node.Type);
        Assert.Equal("example.com", node.Address);
        Assert.Equal(443, node.Port);
        Assert.Equal("Trojan Node", node.Name);
        Assert.Equal("password123", GetStringParam(node, "password"));
        Assert.Equal("tls", GetStringParam(node, "security"));
        Assert.Equal("example.com", GetStringParam(node, "sni"));
        Assert.Equal("1", GetStringParam(node, "allowInsecure"));
        Assert.Equal("ws", GetStringParam(node, "network"));
        Assert.Equal("example.com", GetStringParam(node, "host"));
        Assert.Equal("/trojan", GetStringParam(node, "path"));
        Assert.Equal("svc", GetStringParam(node, "serviceName"));
        Assert.Equal("h2", GetStringParam(node, "alpn"));
    }

    [Fact]
    public void Parse_Hysteria2Link_SetsTypeAddressPortNameAndParams()
    {
        // Arrange: a hysteria2 link with obfs and an insecure=1 bool flag.
        var raw =
            $"hysteria2://authpass@example.com:8443?insecure=1&sni=example.com&obfs=salamander" +
            "&obfs-password=obfspw&alpn=h3#Hy2%20Node";

        // Act: parse the single link.
        var node = Single(raw);

        // Assert: core fields, auth as password, obfs params, and the bool flag.
        Assert.Equal("hysteria2", node.Type);
        Assert.Equal("example.com", node.Address);
        Assert.Equal(8443, node.Port);
        Assert.Equal("Hy2 Node", node.Name);
        Assert.Equal("authpass", GetStringParam(node, "password"));
        Assert.Equal(true, GetBoolParam(node, "insecure"));
        Assert.Equal("example.com", GetStringParam(node, "sni"));
        Assert.Equal("salamander", GetStringParam(node, "obfs"));
        Assert.Equal("obfspw", GetStringParam(node, "obfsPassword"));
        Assert.Equal("h3", GetStringParam(node, "alpn"));
    }

    [Fact]
    public void Parse_Hy2AliasScheme_NormalizesTypeToHysteria2()
    {
        // Arrange: the hy2:// scheme alias with insecure=0.
        var raw = $"hy2://aliaspass@example.com:8443?insecure=0#Alias";

        // Act: parse the single link.
        var node = Single(raw);

        // Assert: type is normalized and the bool flag is false.
        Assert.Equal("hysteria2", node.Type);
        Assert.Equal("example.com", node.Address);
        Assert.Equal(8443, node.Port);
        Assert.Equal("Alias", node.Name);
        Assert.Equal("aliaspass", GetStringParam(node, "password"));
        Assert.Equal(false, GetBoolParam(node, "insecure"));
    }

    [Fact]
    public void Parse_TuicLink_SetsTypeAddressPortNameAndParams()
    {
        // Arrange: a tuic link with UUID:password userinfo and bool allow_insecure.
        var raw =
            $"tuic://{Uuid}:tupass@example.com:7777?congestion_control=bbr&alpn=h3" +
            "&udp_relay_mode=native&allow_insecure=1&sni=example.com#TUIC%20Node";

        // Act: parse the single link.
        var node = Single(raw);

        // Assert: core fields plus remapped tuic params.
        Assert.Equal("tuic", node.Type);
        Assert.Equal("example.com", node.Address);
        Assert.Equal(7777, node.Port);
        Assert.Equal("TUIC Node", node.Name);
        Assert.Equal(Uuid, GetStringParam(node, "uuid"));
        Assert.Equal("tupass", GetStringParam(node, "password"));
        Assert.Equal("bbr", GetStringParam(node, "congestionControl"));
        Assert.Equal("h3", GetStringParam(node, "alpn"));
        Assert.Equal("native", GetStringParam(node, "udpRelayMode"));
        Assert.Equal(true, GetBoolParam(node, "allowInsecure"));
        Assert.Equal("example.com", GetStringParam(node, "sni"));
    }

    [Fact]
    public void Parse_MalformedLinkPerProtocol_AllSkipped()
    {
        // Arrange: one malformed link per protocol (plus garbage and bad schemes).
        string[] malformed =
        {
            "ss://!!!notbase64!!!@example.com:8388",                        // bad base64 userinfo
            "vmess://!!!!notbase64!!!!",                                    // bad base64 payload
            $"vmess://{NotJson}",                                           // base64 of non-JSON
            "vless://example.com:443?encryption=none#X",                    // missing uuid
            "trojan://@example.com:443#X",                                  // empty password
            "hysteria2://auth@example.com#X",                               // missing port
            "tuic://example.com:7777#X",                                    // missing uuid:password
            "socks5://user:pass@example.com:1080#X",                        // unsupported scheme
            "just some random text",                                        // not a link at all
        };

        // Act: parse each malformed link in isolation.
        // Assert: every one is skipped without throwing and yields no nodes.
        foreach (var link in malformed)
        {
            var nodes = NodeLinkParser.Parse(link);
            Assert.Empty(nodes);
        }
    }

    [Fact]
    public void Parse_MixedValidAndInvalid_ReturnsOnlyValidInOrder()
    {
        // Arrange: a multi-link payload mixing valid and malformed lines.
        var raw =
            $"vless://{Uuid}@example.com:443#Node%20A\n" +
            "ss://!!!bad!!!\n" +
            $"vmess://{VmessPayload}\n" +
            "trojan://@bad:443\n" +
            $"tuic://{Uuid}:tupass@example.com:7777#Node%20C\n" +
            "not a link";

        // Act: parse the whole payload.
        var nodes = NodeLinkParser.Parse(raw);

        // Assert: exactly the three valid links, in order, with decoded names.
        Assert.Equal(3, nodes.Count);
        Assert.Equal("vless", nodes[0].Type);
        Assert.Equal("Node A", nodes[0].Name);
        Assert.Equal("vmess", nodes[1].Type);
        Assert.Equal("VMess Node", nodes[1].Name);
        Assert.Equal("tuic", nodes[2].Type);
        Assert.Equal("Node C", nodes[2].Name);
    }

    [Fact]
    public void Parse_IPv6BracketedHost_AddressAndPortCorrect()
    {
        // Arrange: ss and vless links with bracketed IPv6 hosts.
        var ssRaw = $"ss://{SsUserInfo}@[2001:db8::1]:8388#V6%20SS";
        var vlessRaw = $"vless://{Uuid}@[::1]:443#Loopback";

        // Act: parse both links.
        var ssNode = Single(ssRaw);
        var vlessNode = Single(vlessRaw);

        // Assert: the bracket content is the address and the port follows it.
        Assert.Equal("2001:db8::1", ssNode.Address);
        Assert.Equal(8388, ssNode.Port);
        Assert.Equal("::1", vlessNode.Address);
        Assert.Equal(443, vlessNode.Port);
    }

    [Fact]
    public void Parse_UrlDecodedFragmentNames_ChineseAndEncoded()
    {
        // Arrange: an encoded CJK fragment and a raw UTF-8 fragment.
        var encoded = $"vless://{Uuid}@example.com:443#%E6%B5%8B%E8%AF%95%E8%8A%82%E7%82%B9";
        var rawUtf8 = $"ss://{SsUserInfo}@example.com:8388#香港节点";

        // Act: parse both links.
        var encodedNode = Single(encoded);
        var rawUtf8Node = Single(rawUtf8);

        // Assert: both decode to the same CJK name.
        Assert.Equal("测试节点", encodedNode.Name);
        Assert.Equal("香港节点", rawUtf8Node.Name);
    }

    [Fact]
    public void Parse_MissingFragment_GeneratesReadableName()
    {
        // Arrange: valid links with no fragment (or an empty one).
        var noFragment = $"vless://{Uuid}@example.com:443";
        var emptyFragment = $"vless://{Uuid}@example.com:443#";

        // Act: parse both links.
        var first = Single(noFragment);
        var second = Single(emptyFragment);

        // Assert: the generated name is "<Type> <Address>:<Port>".
        Assert.Equal("vless example.com:443", first.Name);
        Assert.Equal("vless example.com:443", second.Name);
    }

    [Fact]
    public void Parse_EmptyOrWhitespaceInput_ReturnsEmptyList()
    {
        // Act: parse blank inputs.
        var empty = NodeLinkParser.Parse("");
        var whitespace = NodeLinkParser.Parse(" \r\n\t ");

        // Assert: no nodes, no exception.
        Assert.Empty(empty);
        Assert.Empty(whitespace);
    }

    [Fact]
    public void Parse_Base64SubscriptionBody_SingleNode()
    {
        // Arrange: a single vless link base64-encoded as a subscription body.
        var link = $"vless://{Uuid}@example.com:443?encryption=none#Sub%20Node";
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(link));

        // Act: parse the encoded body.
        var nodes = NodeLinkParser.Parse(body);

        // Assert: the node comes through with its fields intact.
        var node = Assert.Single(nodes);
        Assert.Equal("vless", node.Type);
        Assert.Equal("example.com", node.Address);
        Assert.Equal(443, node.Port);
        Assert.Equal("Sub Node", node.Name);
        Assert.Equal(Uuid, GetStringParam(node, "uuid"));
    }

    [Fact]
    public void Parse_Base64SubscriptionBody_MultiNode_WithNewlines()
    {
        // Arrange: three links joined by newlines, base64-encoded as a body.
        var links =
            $"vless://{Uuid}@node-a.example.com:443#Node%20A\n" +
            $"vless://{Uuid}@node-b.example.com:8443#Node%20B\n" +
            $"ss://{SsUserInfo}@node-c.example.com:8388#Node%20C";
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(links));

        // Act: parse the encoded body.
        var nodes = NodeLinkParser.Parse(body);

        // Assert: every link parsed, in order, with decoded names.
        Assert.Equal(3, nodes.Count);
        Assert.Equal("Node A", nodes[0].Name);
        Assert.Equal("Node B", nodes[1].Name);
        Assert.Equal("Node C", nodes[2].Name);
    }

    [Fact]
    public void Parse_Base64SubscriptionBody_WithWhitespace()
    {
        // Arrange: base64 of one link with spaces, newlines, and tabs interleaved.
        var link = $"vless://{Uuid}@example.com:443#WS%20Node";
        var compact = Convert.ToBase64String(Encoding.UTF8.GetBytes(link));
        var body = $" {compact[..10]}\r\n\t{compact[10..20]} {compact[20..]} ";

        // Act: parse the whitespace-laced body.
        var nodes = NodeLinkParser.Parse(body);

        // Assert: whitespace stripped before decoding, node parsed.
        var node = Assert.Single(nodes);
        Assert.Equal("vless", node.Type);
        Assert.Equal("WS Node", node.Name);
    }

    [Fact]
    public void Parse_GarbageText_ReturnsEmpty()
    {
        // Act: plain prose that is neither links nor a subscription body.
        var nodes = NodeLinkParser.Parse("hello world this is not a subscription");

        // Assert: no nodes, no exception.
        Assert.Empty(nodes);
    }

    [Fact]
    public void Parse_Base64OfGarbage_ReturnsEmpty()
    {
        // Arrange: base64-encoded text that is not a link.
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes("this is not a node link at all"));

        // Act: parse the encoded body.
        var nodes = NodeLinkParser.Parse(body);

        // Assert: decoded but not a link, so nothing is returned.
        Assert.Empty(nodes);
    }

    [Fact]
    public void Parse_ShortBase64Like_ReturnsEmpty()
    {
        // Arrange: a base64-ish token shorter than the 16-char subscription threshold.
        var body = "abc123456789=";

        // Act: parse the token.
        var nodes = NodeLinkParser.Parse(body);

        // Assert: not treated as a subscription body, no nodes.
        Assert.Empty(nodes);
    }

    [Fact]
    public void Parse_InvalidLinks_AreSkipped_ValidKept()
    {
        // Arrange: a plain-text line next to a valid vless link.
        var raw = $"this is plain text\nvless://{Uuid}@example.com:443#Mixed%20Node";

        // Act: parse the mixed payload.
        var nodes = NodeLinkParser.Parse(raw);

        // Assert: the plausible-link pre-check drops the garbage and keeps the valid link.
        var node = Assert.Single(nodes);
        Assert.Equal("Mixed Node", node.Name);
    }

    /// <summary>
    /// Theory covering IPv6 negative cases: no port, empty port, unclosed
    /// bracket, and non-numeric port. Each must be rejected (empty result).
    /// Verified against NodeLinkParser.TryParseHostPort behavior.
    /// </summary>
    [Theory]
    [InlineData("[::1]")]           // no port after bracket
    [InlineData("[::1]:")]          // empty port
    [InlineData("[::1")]            // unclosed bracket
    [InlineData("[::1]:abc")]       // non-numeric port
    public void Parse_IPv6NegativeCases_Rejected(string hostPort)
    {
        // Arrange: embed the malformed IPv6 host:port into a vless link.
        var raw = $"vless://{Uuid}@{hostPort}#BadV6";

        // Act: parse the link.
        var nodes = NodeLinkParser.Parse(raw);

        // Assert: malformed IPv6 host:port is rejected (no nodes returned).
        Assert.Empty(nodes);
    }

    /// <summary>
    /// Theory covering IPv6 port boundary edges: port 0 (below valid range),
    /// port 65536 and 99999 (above valid range). All must be rejected.
    /// Verified against NodeLinkParser.TryParseHostPort port range [1, 65535].
    /// </summary>
    [Theory]
    [InlineData("[::1]:0")]       // port 0: below valid range (1-65535)
    [InlineData("[::1]:65536")]   // port 65536: above valid range
    [InlineData("[::1]:99999")]   // port 99999: above valid range
    public void Parse_VlessLink_Ipv6PortEdge_OutOfRange_Rejected(string hostPort)
    {
        // Arrange: embed the out-of-range port into a vless link with IPv6 host.
        var raw = $"vless://{Uuid}@{hostPort}#EdgePort";

        // Act: parse the link.
        var nodes = NodeLinkParser.Parse(raw);

        // Assert: out-of-range port is rejected (no nodes returned).
        Assert.Empty(nodes);
    }

    [Fact]
    public void Parse_VlessLink_Ipv6PortEdge_ValidPort443_Parsed()
    {
        // Arrange: a vless link with bracketed IPv6 host and valid port 443 (control case).
        var raw = $"vless://{Uuid}@[::1]:443#ControlV6";

        // Act: parse the link.
        var node = Single(raw);

        // Assert: valid IPv6 host and port are parsed correctly.
        Assert.Equal("::1", node.Address);
        Assert.Equal(443, node.Port);
        Assert.Equal("ControlV6", node.Name);
    }

    /// <summary>
    /// Theory covering IPv6 valid-port boundary edges: port 1 (minimum valid)
    /// and port 65535 (maximum valid). Both must parse successfully with the
    /// correct port value. Verified against NodeLinkParser.TryParseHostPort
    /// port range [1, 65535].
    /// </summary>
    [Theory]
    [InlineData("[::1]:1")]         // port 1: minimum valid port
    [InlineData("[::1]:65535")]     // port 65535: maximum valid port
    public void Parse_VlessLink_Ipv6PortBoundary_Valid(string hostPort)
    {
        // Arrange: embed the boundary port into a vless link with IPv6 host.
        var raw = $"vless://{Uuid}@{hostPort}#BoundaryV6";

        // Act: parse the link.
        var node = Single(raw);

        // Assert: boundary port is accepted and parsed correctly.
        Assert.Equal("::1", node.Address);
        // Extract the expected port from the hostPort string (e.g. "[::1]:1" → 1).
        var expectedPort = int.Parse(hostPort[(hostPort.LastIndexOf(':') + 1)..]);
        Assert.Equal(expectedPort, node.Port);
        Assert.Equal("BoundaryV6", node.Name);
    }
}
