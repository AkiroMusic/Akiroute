using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Akiroute.Models;

/// <summary>
/// AOT-safe JSON serialization metadata for every persisted model. All
/// System.Text.Json calls in Akiroute must go through this context (the
/// <c>JsonTypeInfo&lt;T&gt;</c> overloads) — never the reflection-based
/// options-instance overload, which would produce IL2026/IL3050 warnings
/// under Native AOT publish.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ProxyNode))]
[JsonSerializable(typeof(ProcessRule))]
[JsonSerializable(typeof(SubscriptionEntry))]
[JsonSerializable(typeof(List<ProxyNode>))]
[JsonSerializable(typeof(List<ProcessRule>))]
[JsonSerializable(typeof(List<SubscriptionEntry>))]
[JsonSerializable(typeof(Dictionary<string, JsonNode>))]
public partial class AppJsonSerializerContext : JsonSerializerContext
{
}
