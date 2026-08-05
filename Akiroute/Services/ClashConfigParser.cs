using System.Diagnostics;
using System.Globalization;
using Akiroute.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Akiroute.Services;

/// <summary>
/// Parses the <c>proxies:</c> section of a Clash configuration YAML document
/// into <see cref="ProxyNode"/> instances.
/// Parsing uses YamlDotNet's DOM-only API (<see cref="YamlStream"/> /
/// <see cref="YamlMappingNode"/> / <see cref="YamlSequenceNode"/> /
/// <see cref="YamlScalarNode"/>) and never the reflection-based Deserializer,
/// which is forbidden for Native AOT. The parser is defensive by contract:
/// malformed or hostile YAML yields an empty list or skips the offending node
/// — it never throws for bad input.
/// </summary>
public static class ClashConfigParser
{
    /// <summary>
    /// Parses <paramref name="yaml"/> into a list of proxy nodes.
    /// Unsupported protocols (ssr, snell, http, socks5, wireguard) and nodes
    /// missing <c>name</c>/<c>server</c> are skipped. A missing or empty
    /// <c>proxies:</c> section, empty input, or malformed YAML returns an empty
    /// list. Node identity is preserved by a fresh <see cref="ProxyNode.Id"/>
    /// per node, so duplicate display names are kept distinct.
    /// </summary>
    public static List<ProxyNode> Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return [];
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (Exception ex)
        {
            // Hostile-input boundary: a garbage document must never surface
            // as an exception to the caller. Log and yield nothing.
            Debug.WriteLine($"[ClashConfigParser] Malformed YAML, returning no nodes: {ex.Message}");
            return [];
        }

        var nodes = new List<ProxyNode>();
        foreach (var document in stream.Documents)
        {
            if (document.RootNode is not YamlMappingNode root)
            {
                continue;
            }

            if (!YamlMapReader.TryGetValue(root, "proxies", out var proxies) ||
                proxies is not YamlSequenceNode sequence)
            {
                continue;
            }

            foreach (var item in sequence.Children)
            {
                if (item is not YamlMappingNode proxyMap)
                {
                    continue;
                }

                try
                {
                    var node = ClashNodeMapper.MapProxyNode(proxyMap);
                    if (node is not null)
                    {
                        nodes.Add(node);
                    }
                }
                catch (Exception ex)
                {
                    // One malformed entry must not discard the whole list.
                    Debug.WriteLine($"[ClashConfigParser] Skipping a malformed proxy node: {ex.Message}");
                }
            }
        }

        return nodes;
    }
}

/// <summary>
/// Defensive, never-throwing readers for scalar/mapping/sequence values held
/// in a <see cref="YamlMappingNode"/>. Shared by the parser and the mapper.
/// </summary>
internal static class YamlMapReader
{
    /// <summary>
    /// Returns the value node stored under <paramref name="key"/> if the key is
    /// present as a scalar; otherwise <c>false</c> with a null value.
    /// </summary>
    public static bool TryGetValue(YamlMappingNode map, string key, out YamlNode? value)
    {
        foreach (var pair in map.Children)
        {
            if (pair.Key is YamlScalarNode scalar && scalar.Value == key)
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>Gets a scalar value by key, or null when absent or not a scalar.</summary>
    public static string? GetString(YamlMappingNode map, string key)
    {
        return TryGetValue(map, key, out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;
    }

    /// <summary>
    /// Reads a boolean by key. Only <c>true</c>/<c>1</c> yield <c>true</c>;
    /// <c>false</c>/<c>0</c> and anything else yield <c>false</c>.
    /// </summary>
    public static bool GetBool(YamlMappingNode map, string key) => ParseBool(GetString(map, key));

    /// <summary>
    /// Reads an integer by key with culture-invariant parsing; falls back to
    /// <paramref name="fallback"/> when the key is absent or not an integer.
    /// </summary>
    public static int GetInt(YamlMappingNode map, string key, int fallback = 0)
    {
        var raw = GetString(map, key);
        if (raw is null)
        {
            return fallback;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    /// <summary>Gets a nested mapping by key, or null when absent or not a mapping.</summary>
    public static YamlMappingNode? GetMapping(YamlMappingNode map, string key)
    {
        return TryGetValue(map, key, out var value) && value is YamlMappingNode mapping
            ? mapping
            : null;
    }

    /// <summary>
    /// Reads a sequence of scalars by key and joins its string values with
    /// <c>","</c>; returns null when absent, empty, or not a sequence.
    /// </summary>
    public static string? GetJoinedSequence(YamlMappingNode map, string key)
    {
        if (!TryGetValue(map, key, out var value) || value is not YamlSequenceNode sequence)
        {
            return null;
        }

        var parts = sequence.Children
            .OfType<YamlScalarNode>()
            .Select(scalar => scalar.Value)
            .Where(part => part is not null)
            .ToList();

        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    /// <summary>Coerces a raw scalar to a bool: only <c>true</c>/<c>1</c> are true.</summary>
    internal static bool ParseBool(string? raw) => raw is "true" or "1";
}
