using Akiroute.Models;

namespace Akiroute.Tests.Models;

/// <summary>
/// ProxyNode POCO tests: default state and settability. Pure data, no UI.
/// </summary>
public class ProxyNodeTests
{
    [Fact]
    public void NewNode_Id_Is32CharDashedGuid()
    {
        // Act: create a fresh node (no properties assigned).
        var node = new ProxyNode();

        // Assert: Id is a "N"-format GUID (32 hex chars, no dashes) as later
        // phases rely on stable node ids for selection and routing.
        Assert.Equal(32, node.Id.Length);
        Assert.True(Guid.TryParseExact(node.Id, "N", out _));
    }

    [Fact]
    public void NewNode_UntestedPing_IsMinusOne_NotSelected()
    {
        // Act: create a fresh node.
        var node = new ProxyNode();

        // Assert: PingMs == -1 (untested) and nothing is selected by default.
        Assert.Equal(-1, node.PingMs);
        Assert.False(node.IsSelected);
    }

    [Fact]
    public void NewNode_OptionalFields_DefaultToNull()
    {
        // Act: create a fresh node.
        var node = new ProxyNode();

        // Assert: unspecified members start null / zero (no invented defaults).
        Assert.Null(node.Name);
        Assert.Null(node.Type);
        Assert.Null(node.Address);
        Assert.Null(node.RawConfig);
        Assert.Null(node.ExtraParams);
        Assert.Equal(0, node.Port);
    }

    [Fact]
    public void Node_Properties_AreSettable()
    {
        // Arrange: a fully populated node.
        var node = new ProxyNode
        {
            Name = "SG-1",
            Type = "vless",
            Address = "203.0.113.10",
            Port = 443,
            PingMs = 42,
            IsSelected = true,
            RawConfig = "vless://203.0.113.10:443?encryption=none#SG-1",
        };

        // Assert: every value round-trips through the POCO surface.
        Assert.Equal("SG-1", node.Name);
        Assert.Equal("vless", node.Type);
        Assert.Equal("203.0.113.10", node.Address);
        Assert.Equal(443, node.Port);
        Assert.Equal(42, node.PingMs);
        Assert.True(node.IsSelected);
        Assert.StartsWith("vless://", node.RawConfig);
    }
}
