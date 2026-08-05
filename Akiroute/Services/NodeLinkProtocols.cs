using System.Text.Json;
using System.Text.Json.Nodes;
using Akiroute.Models;

namespace Akiroute.Services;

/// <summary>
/// Protocol-specific link parsers (ss, vmess, vless, trojan, hysteria2, tuic).
/// Each parser returns null for a malformed link so the caller skips it.
/// </summary>
public static partial class NodeLinkParser
{
    /// <summary>
    /// Parses an ss:// link. Supports the SIP002 form
    /// "BASE64(method:password)@host:port" and the legacy form where the whole
    /// "method:password@host:port" is base64 encoded. Both standard and URL-safe
    /// base64 are accepted. The "?plugin=" query is ignored.
    /// </summary>
    private static ProxyNode? ParseSs(string body, string raw)
    {
        SplitBody(body, out var authority, out _, out var fragment);
        if (authority.Length == 0)
        {
            return null;
        }

        string method;
        string password;
        string hostPort;

        var at = authority.IndexOf('@');
        if (at >= 0)
        {
            var decodedUser = TryDecodeBase64(authority[..at]);
            if (decodedUser is null || !TrySplitMethodPassword(decodedUser, out method, out password))
            {
                return null;
            }
            hostPort = authority[(at + 1)..];
        }
        else
        {
            var decoded = TryDecodeBase64(authority);
            if (decoded is null)
            {
                return null;
            }
            var decodedAt = decoded.IndexOf('@');
            if (decodedAt < 0 || !TrySplitMethodPassword(decoded[..decodedAt], out method, out password))
            {
                return null;
            }
            hostPort = decoded[(decodedAt + 1)..];
        }

        if (!TryParseHostPort(hostPort, out var address, out var port))
        {
            return null;
        }

        var @params = NewParams();
        AddString(@params, "method", method);
        AddString(@params, "password", password);

        return NewNode("ss", address, port, ResolveName(fragment, "ss", address, port), raw, @params);
    }

    /// <summary>
    /// Splits a decoded "method:password" userinfo; returns false when the
    /// method is missing. Values are URL-decoded for percent-encoded passwords.
    /// </summary>
    private static bool TrySplitMethodPassword(string userInfo, out string method, out string password)
    {
        var colon = userInfo.IndexOf(':');
        if (colon <= 0)
        {
            method = string.Empty;
            password = string.Empty;
            return false;
        }

        method = Uri.UnescapeDataString(userInfo[..colon]);
        password = Uri.UnescapeDataString(userInfo[(colon + 1)..]);
        return method.Length > 0;
    }

    /// <summary>
    /// Parses a vmess:// link whose payload is base64 JSON. Fields map to
    /// ExtraParams: uuid=id, alterId=aid, security=tls|scy (the TLS setting when
    /// present, otherwise the cipher), network=net, headerType=type, host, path.
    /// The JSON "ps" field is the display name (URL-decoded); a missing "add",
    /// invalid "port", or non-JSON payload is skipped.
    /// </summary>
    private static ProxyNode? ParseVmess(string body, string raw)
    {
        SplitBody(body, out var authority, out _, out var fragment);
        var json = TryDecodeBase64(authority);
        if (json is null)
        {
            return null;
        }

        JsonObject obj;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed)
            {
                return null;
            }
            obj = parsed;
        }
        catch (JsonException)
        {
            return null;
        }

        var address = GetString(obj, "add");
        if (string.IsNullOrEmpty(address)
            || !int.TryParse(GetString(obj, "port"), out var port)
            || port is < 1 or > 65535)
        {
            return null;
        }

        var @params = NewParams();
        AddString(@params, "uuid", GetString(obj, "id"));
        AddString(@params, "alterId", GetString(obj, "aid"));
        var tls = GetString(obj, "tls");
        var scy = GetString(obj, "scy");
        AddString(@params, "security", !string.IsNullOrEmpty(tls) ? tls : scy);
        AddString(@params, "network", GetString(obj, "net"));
        AddString(@params, "headerType", GetString(obj, "type"));
        AddString(@params, "host", GetString(obj, "host"));
        AddString(@params, "path", GetString(obj, "path"));

        var name = GetString(obj, "ps");
        var resolvedName = !string.IsNullOrEmpty(name)
            ? Uri.UnescapeDataString(name)
            : ResolveName(fragment, "vmess", address, port);

        return NewNode("vmess", address, port, resolvedName, raw, @params);
    }

    /// <summary>Reads a JSON field as a string, coercing JSON numbers to text.</summary>
    private static string? GetString(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }
        if (value.TryGetValue<int>(out var number))
        {
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }

    /// <summary>
    /// Parses a vless:// link "uuid@host:port?...". Query params are remapped
    /// for the Xray builder: type→network, fp→fingerprint, pbk→publicKey,
    /// sid→shortId; all others keep their name. A missing uuid or host/port is
    /// skipped.
    /// </summary>
    private static ProxyNode? ParseVless(string body, string raw)
    {
        SplitBody(body, out var authority, out var query, out var fragment);
        SplitAuthority(authority, out var userInfo, out var hostPort);
        if (userInfo.Length == 0 || !TryParseHostPort(hostPort, out var address, out var port))
        {
            return null;
        }

        var queryParams = ParseQuery(query);
        var @params = NewParams();
        AddString(@params, "uuid", Uri.UnescapeDataString(userInfo));
        AddString(@params, "encryption", GetQuery(queryParams, "encryption"));
        AddString(@params, "security", GetQuery(queryParams, "security"));
        AddString(@params, "sni", GetQuery(queryParams, "sni"));
        AddString(@params, "fingerprint", GetQuery(queryParams, "fp"));
        AddString(@params, "publicKey", GetQuery(queryParams, "pbk"));
        AddString(@params, "shortId", GetQuery(queryParams, "sid"));
        AddString(@params, "flow", GetQuery(queryParams, "flow"));
        AddString(@params, "network", GetQuery(queryParams, "type"));
        AddString(@params, "headerType", GetQuery(queryParams, "headerType"));
        AddString(@params, "host", GetQuery(queryParams, "host"));
        AddString(@params, "path", GetQuery(queryParams, "path"));
        AddString(@params, "serviceName", GetQuery(queryParams, "serviceName"));
        AddString(@params, "allowInsecure", GetQuery(queryParams, "allowInsecure"));
        AddString(@params, "alpn", GetQuery(queryParams, "alpn"));

        return NewNode("vless", address, port, ResolveName(fragment, "vless", address, port), raw, @params);
    }

    /// <summary>
    /// Parses a trojan:// link "password@host:port?...". Query params map
    /// directly except type→network. An empty password or missing host/port is
    /// skipped.
    /// </summary>
    private static ProxyNode? ParseTrojan(string body, string raw)
    {
        SplitBody(body, out var authority, out var query, out var fragment);
        SplitAuthority(authority, out var userInfo, out var hostPort);
        if (userInfo.Length == 0 || !TryParseHostPort(hostPort, out var address, out var port))
        {
            return null;
        }

        var queryParams = ParseQuery(query);
        var @params = NewParams();
        AddString(@params, "password", Uri.UnescapeDataString(userInfo));
        AddString(@params, "security", GetQuery(queryParams, "security"));
        AddString(@params, "sni", GetQuery(queryParams, "sni"));
        AddString(@params, "allowInsecure", GetQuery(queryParams, "allowInsecure"));
        AddString(@params, "network", GetQuery(queryParams, "type"));
        AddString(@params, "host", GetQuery(queryParams, "host"));
        AddString(@params, "path", GetQuery(queryParams, "path"));
        AddString(@params, "serviceName", GetQuery(queryParams, "serviceName"));
        AddString(@params, "alpn", GetQuery(queryParams, "alpn"));

        return NewNode("trojan", address, port, ResolveName(fragment, "trojan", address, port), raw, @params);
    }

    /// <summary>
    /// Parses a hysteria2:// or hy2:// link "auth@host:port?...". The scheme
    /// prefix is normalized to "hysteria2". ExtraParams: password=auth,
    /// insecure (bool from "0"/"1"), sni, obfs, obfsPassword=obfs-password,
    /// alpn. Auth is optional; a missing host/port is skipped.
    /// </summary>
    private static ProxyNode? ParseHysteria2(string body, string raw)
    {
        SplitBody(body, out var authority, out var query, out var fragment);
        SplitAuthority(authority, out var userInfo, out var hostPort);
        if (!TryParseHostPort(hostPort, out var address, out var port))
        {
            return null;
        }

        var queryParams = ParseQuery(query);
        var @params = NewParams();
        AddString(@params, "password", Uri.UnescapeDataString(userInfo));
        AddBool(@params, "insecure", GetQuery(queryParams, "insecure"));
        AddString(@params, "sni", GetQuery(queryParams, "sni"));
        AddString(@params, "obfs", GetQuery(queryParams, "obfs"));
        AddString(@params, "obfsPassword", GetQuery(queryParams, "obfs-password"));
        AddString(@params, "alpn", GetQuery(queryParams, "alpn"));

        return NewNode("hysteria2", address, port, ResolveName(fragment, "hysteria2", address, port), raw, @params);
    }

    /// <summary>
    /// Parses a tuic:// link "uuid:password@host:port?...". Query params are
    /// remapped: congestion_control→congestionControl, udp_relay_mode→udpRelayMode,
    /// allow_insecure→allowInsecure (bool). A missing uuid or host/port is skipped.
    /// </summary>
    private static ProxyNode? ParseTuic(string body, string raw)
    {
        SplitBody(body, out var authority, out var query, out var fragment);
        SplitAuthority(authority, out var userInfo, out var hostPort);
        if (userInfo.Length == 0 || !TryParseHostPort(hostPort, out var address, out var port))
        {
            return null;
        }

        var decodedUser = Uri.UnescapeDataString(userInfo);
        var colon = decodedUser.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        var queryParams = ParseQuery(query);
        var @params = NewParams();
        AddString(@params, "uuid", decodedUser[..colon]);
        AddString(@params, "password", decodedUser[(colon + 1)..]);
        AddString(@params, "congestionControl", GetQuery(queryParams, "congestion_control"));
        AddString(@params, "alpn", GetQuery(queryParams, "alpn"));
        AddString(@params, "udpRelayMode", GetQuery(queryParams, "udp_relay_mode"));
        AddBool(@params, "allowInsecure", GetQuery(queryParams, "allow_insecure"));
        AddString(@params, "sni", GetQuery(queryParams, "sni"));

        return NewNode("tuic", address, port, ResolveName(fragment, "tuic", address, port), raw, @params);
    }
}
