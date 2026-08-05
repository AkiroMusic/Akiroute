namespace Akiroute.Models;

/// <summary>A subscription feed (a URL that supplies a node list).</summary>
public class SubscriptionEntry
{
    /// <summary>Unique entry identifier (32-char hex GUID, no dashes).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name of the subscription.</summary>
    public string? Name { get; set; }

    /// <summary>Feed URL the node list is fetched from.</summary>
    public string? Url { get; set; }

    /// <summary>Time of the last successful update; null = never updated.</summary>
    public DateTimeOffset? LastUpdated { get; set; }

    /// <summary>Minutes between automatic updates; 0 = use the global setting.</summary>
    public int AutoUpdateMinutes { get; set; }
}
