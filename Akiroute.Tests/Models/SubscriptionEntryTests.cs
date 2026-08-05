using Akiroute.Models;

namespace Akiroute.Tests.Models;

/// <summary>
/// SubscriptionEntry POCO tests.
/// </summary>
public class SubscriptionEntryTests
{
    [Fact]
    public void NewEntry_Id_Is32CharDashedGuid()
    {
        // Act: create a fresh subscription entry.
        var entry = new SubscriptionEntry();

        // Assert: Id is a "N"-format GUID (32 hex chars, no dashes).
        Assert.Equal(32, entry.Id.Length);
        Assert.True(Guid.TryParseExact(entry.Id, "N", out _));
    }

    [Fact]
    public void NewEntry_NeverUpdated_DefaultsToNullAndZero()
    {
        // Act: create a fresh subscription entry.
        var entry = new SubscriptionEntry();

        // Assert: no feed url/name yet, never updated, auto-update off.
        Assert.Null(entry.Name);
        Assert.Null(entry.Url);
        Assert.Null(entry.LastUpdated);
        Assert.Equal(0, entry.AutoUpdateMinutes);
    }

    [Fact]
    public void Entry_Properties_AreSettable()
    {
        // Arrange: a fully populated subscription entry.
        var updated = DateTimeOffset.Parse("2026-08-01T12:00:00+00:00");
        var entry = new SubscriptionEntry
        {
            Name = "Free Feed",
            Url = "https://example.com/sub",
            LastUpdated = updated,
            AutoUpdateMinutes = 1440,
        };

        // Assert: every value round-trips through the POCO surface.
        Assert.Equal("Free Feed", entry.Name);
        Assert.Equal("https://example.com/sub", entry.Url);
        Assert.Equal(updated, entry.LastUpdated);
        Assert.Equal(1440, entry.AutoUpdateMinutes);
    }
}
