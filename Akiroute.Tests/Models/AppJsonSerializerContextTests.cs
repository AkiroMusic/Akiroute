using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Akiroute.Models;

namespace Akiroute.Tests.Models;

/// <summary>
/// AppJsonSerializerContext tests: every persisted model must round-trip through
/// the source-generated context, enums must serialize as strings, and null
/// optional members must be omitted from the JSON.
/// </summary>
public class AppJsonSerializerContextTests
{
    private static string ToJson<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);

    private static T FromJson<T>(string json, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(json, typeInfo)!;

    private static AppSettings SampleSettings() => new()
    {
        SelectedNodeId = "n1",
        Mode = ProxyMode.Rule,
        Port = 3333,
        AutoConnect = true,
        SubscriptionAutoUpdateMinutes = 30,
        TunEnabled = true,
        Theme = AppTheme.Dark,
        Nodes =
        {
            new ProxyNode
            {
                Id = "n1",
                Name = "SG-1",
                Type = "vless",
                Address = "203.0.113.10",
                Port = 443,
                PingMs = 42,
                IsSelected = true,
                RawConfig = "vless://203.0.113.10:443?encryption=none#SG-1",
                ExtraParams = new Dictionary<string, JsonNode>
                {
                    ["uuid"] = JsonValue.Create("9c1f1c2e-8d1e-4f3a-9a2e-6b1e5c8a4d3f"),
                    ["network"] = JsonValue.Create("ws"),
                    ["tls"] = JsonObject.Parse("""{"sni":"cdn.example.com"}""")!,
                },
            },
        },
        ProcessRules =
        {
            new ProcessRule { ProcessName = "chrome.exe", Action = ProcessAction.Block },
            new ProcessRule { ProcessName = "steam.exe", Action = ProcessAction.Direct },
        },
        Subscriptions =
        {
            new SubscriptionEntry
            {
                Id = "s1",
                Name = "Free Feed",
                Url = "https://example.com/sub",
                LastUpdated = DateTimeOffset.Parse("2026-08-01T12:00:00+00:00"),
                AutoUpdateMinutes = 1440,
            },
        },
    };

    [Fact]
    public void AppSettings_RoundTrips_ThroughSourceGenContext()
    {
        // Arrange: a fully populated settings document.
        var settings = SampleSettings();

        // Act: serialize and deserialize via the source-generated context.
        var json = ToJson(settings, AppJsonSerializerContext.Default.AppSettings);
        var restored = FromJson(json, AppJsonSerializerContext.Default.AppSettings);

        // Assert: the deserialized document is byte-identical on re-serialization.
        Assert.Equal(json, ToJson(restored, AppJsonSerializerContext.Default.AppSettings));
        Assert.Equal(settings.SelectedNodeId, restored.SelectedNodeId);
        Assert.Equal(settings.Mode, restored.Mode);
        Assert.Equal(settings.Port, restored.Port);
        Assert.Equal(settings.Theme, restored.Theme);
        Assert.Single(restored.Nodes);
        Assert.Equal(2, restored.ProcessRules.Count);
        Assert.Single(restored.Subscriptions);
    }

    [Fact]
    public void ProxyNode_ExtraParams_JsonNodesRoundTrip()
    {
        // Arrange: a node with nested protocol parameters.
        var node = new ProxyNode
        {
            Id = "n1",
            Name = "SG-1",
            Type = "vless",
            ExtraParams = new Dictionary<string, JsonNode>
            {
                ["uuid"] = JsonValue.Create("9c1f1c2e-8d1e-4f3a-9a2e-6b1e5c8a4d3f"),
                ["tls"] = JsonObject.Parse("""{"sni":"cdn.example.com"}""")!,
            },
        };

        // Act: serialize and deserialize via the source-generated context.
        var json = ToJson(node, AppJsonSerializerContext.Default.ProxyNode);
        var restored = FromJson(json, AppJsonSerializerContext.Default.ProxyNode);

        // Assert: values survive, including the nested JSON object.
        Assert.Equal("9c1f1c2e-8d1e-4f3a-9a2e-6b1e5c8a4d3f", restored.ExtraParams!["uuid"]!.GetValue<string>());
        Assert.Equal("cdn.example.com", restored.ExtraParams["tls"]!["sni"]!.GetValue<string>());
        Assert.Equal("vless", restored.Type);
    }

    [Fact]
    public void ProcessRule_Action_SerializesAsString()
    {
        // Arrange: a rule with a non-default action.
        var rule = new ProcessRule { ProcessName = "chrome.exe", Action = ProcessAction.Block };

        // Act: serialize through the source-generated context.
        var json = ToJson(rule, AppJsonSerializerContext.Default.ProcessRule);

        // Assert: the enum member appears as a JSON string, never a number
        // (Block's underlying value is 2; the raw JSON must not contain it).
        Assert.Contains("\"action\":\"Block\"", json);
        Assert.DoesNotContain("\"action\":2", json);
    }

    [Fact]
    public void AppSettings_ModeAndTheme_SerializeAsStrings()
    {
        // Arrange: settings with non-default enums.
        var settings = new AppSettings { Mode = ProxyMode.DirectOnly, Theme = AppTheme.Dark };

        // Act: serialize through the source-generated context.
        var json = ToJson(settings, AppJsonSerializerContext.Default.AppSettings);

        // Assert: enum members appear as JSON strings.
        Assert.Contains("\"mode\":\"DirectOnly\"", json);
        Assert.Contains("\"theme\":\"Dark\"", json);
    }

    [Fact]
    public void NullOptionalMembers_AreOmittedFromJson()
    {
        // Arrange: a bare node (all optional members null).
        var node = new ProxyNode { Id = "n1" };

        // Act: serialize through the source-generated context.
        var json = ToJson(node, AppJsonSerializerContext.Default.ProxyNode);

        // Assert: null members are omitted entirely (WhenWritingNull).
        Assert.DoesNotContain("rawConfig", json);
        Assert.DoesNotContain("extraParams", json);
        Assert.Contains("\"id\":\"n1\"", json);
    }

    [Fact]
    public void CollectionRegistrations_AreAvailableAndRoundTrip()
    {
        // Arrange: two nodes.
        var nodes = new List<ProxyNode>
        {
            new() { Id = "a", Name = "A" },
            new() { Id = "b", Name = "B" },
        };

        // Act: serialize and deserialize via the generated List<ProxyNode> info.
        var json = ToJson(nodes, AppJsonSerializerContext.Default.ListProxyNode);
        var restored = FromJson(json, AppJsonSerializerContext.Default.ListProxyNode);

        // Assert: both nodes survive and the raw JSON carries the list shape.
        Assert.Equal(2, restored.Count);
        Assert.Equal("A", restored[0].Name);
        Assert.Equal("B", restored[1].Name);
        Assert.StartsWith("[", json);
    }
}
