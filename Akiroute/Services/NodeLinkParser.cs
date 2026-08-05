using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Akiroute.Models;

namespace Akiroute.Services;

/// <summary>
/// Parses raw proxy subscription text into <see cref="ProxyNode"/> entries.
/// Supports ss, vmess, vless, trojan, hysteria2 (aliased as "hy2"), and tuic
/// links, one per whitespace-separated token. Malformed or unsupported links
/// are skipped — parsing user-supplied input never throws.
/// </summary>
public static partial class NodeLinkParser
{
    /// <summary>
    /// Parses every link in <paramref name="rawText"/> into nodes. Links may be
    /// separated by any whitespace (spaces, newlines, tabs). Malformed or
    /// unsupported links are skipped silently; an empty result never throws.
    /// </summary>
    /// <returns>Valid nodes in order of appearance.</returns>
    public static List<ProxyNode> Parse(string rawText)
    {
        var result = new List<ProxyNode>();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return result;
        }

        foreach (var token in rawText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseLink(token) is { } node)
            {
                result.Add(node);
            }
        }

        return result;
    }

    /// <summary>Builds a node from a single link token; null when unsupported or malformed.</summary>
    private static ProxyNode? TryParseLink(string token)
    {
        try
        {
            return ParseLinkCore(token);
        }
        catch (Exception ex)
        {
            // Safety net: input-driven parsing must never surface an exception.
            Debug.WriteLine($"[NodeLinkParser] Skipping malformed link: {ex.Message}");
            return null;
        }
    }

    /// <summary>Dispatches a link token to its protocol parser by scheme.</summary>
    private static ProxyNode? ParseLinkCore(string token)
    {
        var schemeEnd = token.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || schemeEnd == token.Length - 3)
        {
            return null;
        }

        var scheme = token[..schemeEnd].ToLowerInvariant();
        var body = token[(schemeEnd + 3)..];

        return scheme switch
        {
            "ss" => ParseSs(body, token),
            "vmess" => ParseVmess(body, token),
            "vless" => ParseVless(body, token),
            "trojan" => ParseTrojan(body, token),
            "hysteria2" or "hy2" => ParseHysteria2(body, token),
            "tuic" => ParseTuic(body, token),
            _ => null,
        };
    }

    /// <summary>Splits a URI body into its authority, query, and fragment parts.</summary>
    private static void SplitBody(string body, out string authority, out string query, out string fragment)
    {
        var questionMark = body.IndexOf('?');
        var hash = body.IndexOf('#');

        var authorityEnd = body.Length;
        if (questionMark >= 0) authorityEnd = Math.Min(authorityEnd, questionMark);
        if (hash >= 0) authorityEnd = Math.Min(authorityEnd, hash);
        authority = body[..authorityEnd];

        query = string.Empty;
        if (questionMark >= 0 && (hash < 0 || questionMark < hash))
        {
            query = body[(questionMark + 1)..(hash >= 0 ? hash : body.Length)];
        }

        fragment = hash >= 0 ? body[(hash + 1)..] : string.Empty;
    }

    /// <summary>Splits an authority into its "userinfo@host:port" components.</summary>
    private static void SplitAuthority(string authority, out string userInfo, out string hostPort)
    {
        var at = authority.IndexOf('@');
        if (at < 0)
        {
            userInfo = string.Empty;
            hostPort = authority;
        }
        else
        {
            userInfo = authority[..at];
            hostPort = authority[(at + 1)..];
        }
    }

    /// <summary>
    /// Parses a "host:port" pair, honoring bracketed IPv6 hosts like "[::1]:443".
    /// Returns false when the host is empty or the port is not a valid 1-65535 value.
    /// </summary>
    private static bool TryParseHostPort(string hostPort, out string address, out int port)
    {
        address = string.Empty;
        port = 0;

        if (string.IsNullOrEmpty(hostPort))
        {
            return false;
        }

        string host;
        string portText;
        if (hostPort[0] == '[')
        {
            var close = hostPort.IndexOf(']');
            if (close <= 0 || close + 2 > hostPort.Length || hostPort[close + 1] != ':')
            {
                return false;
            }
            host = hostPort[1..close];
            portText = hostPort[(close + 2)..];
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon <= 0 || colon == hostPort.Length - 1)
            {
                return false;
            }
            host = hostPort[..colon];
            portText = hostPort[(colon + 1)..];
        }

        if (host.Length == 0 || !int.TryParse(portText, out port) || port is < 1 or > 65535)
        {
            return false;
        }

        address = host;
        return true;
    }

    /// <summary>
    /// Decodes base64 text to UTF-8, accepting both the standard alphabet (+/)
    /// and the URL-safe alphabet (-_), with or without padding. Returns null when
    /// the input is not valid base64.
    /// </summary>
    private static string? TryDecodeBase64(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        var normalized = input.Replace('-', '+').Replace('_', '/');
        var padded = (normalized.Length % 4) switch
        {
            0 => normalized,
            2 => normalized + "==",
            3 => normalized + "=",
            _ => null,
        };
        if (padded is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a URL query string into key/value pairs. Values are URL-decoded
    /// (so "path=%2Fvless" becomes "/vless"); duplicate keys keep the last value.
    /// </summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? string.Empty : pair[(equals + 1)..];
            if (key.Length > 0)
            {
                result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves a node name from its URI fragment. The fragment is URL-decoded
    /// (it may be CJK); when absent or empty the name falls back to
    /// "&lt;Type&gt; &lt;Address&gt;:&lt;Port&gt;".
    /// </summary>
    private static string ResolveName(string fragment, string type, string address, int port)
    {
        if (fragment.Length > 0)
        {
            var decoded = Uri.UnescapeDataString(fragment);
            if (decoded.Length > 0)
            {
                return decoded;
            }
        }

        return $"{type} {address}:{port}";
    }

    /// <summary>Creates an empty ExtraParams dictionary.</summary>
    private static Dictionary<string, JsonNode> NewParams() =>
        new(StringComparer.Ordinal);

    /// <summary>Adds a string param, skipping null/empty values.</summary>
    private static void AddString(Dictionary<string, JsonNode> @params, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            @params[key] = JsonValue.Create(value)!;
        }
    }

    /// <summary>Adds a boolean param parsed from "1"/"true" (true) or anything else (false).</summary>
    private static void AddBool(Dictionary<string, JsonNode> @params, string key, string? rawValue)
    {
        if (rawValue is not null)
        {
            @params[key] = JsonValue.Create(rawValue is "1" or "true");
        }
    }

    /// <summary>Returns a decoded query value by key, or null when absent.</summary>
    private static string? GetQuery(Dictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) ? value : null;

    /// <summary>Builds a fully populated ProxyNode with the given precomputed name.</summary>
    private static ProxyNode NewNode(
        string type,
        string address,
        int port,
        string name,
        string raw,
        Dictionary<string, JsonNode> extraParams) =>
        new()
        {
            Type = type,
            Address = address,
            Port = port,
            Name = name,
            RawConfig = raw,
            ExtraParams = extraParams,
        };
}
