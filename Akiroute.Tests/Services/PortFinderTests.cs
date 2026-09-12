using System.Net;
using System.Net.Sockets;
using Akiroute.Services;

namespace Akiroute.Tests.Services;

/// <summary>
/// PortFinder tests. Every test that binds a listener releases it in a finally
/// block so ports never leak into sibling tests or other test classes.
/// </summary>
public class PortFinderTests
{
    [Fact]
    public void IsPortAvailable_ReturnsTrue_WhenNoListenerIsBound()
    {
        // Arrange: obtain a free port by binding and immediately releasing an ephemeral listener.
        var port = GetFreeEphemeralPort();

        // Act + Assert: with nothing bound, the port reports as available.
        Assert.True(PortFinder.IsPortAvailable(port));
    }

    [Fact]
    public void IsPortAvailable_ReturnsFalse_WhileAListenerIsBound()
    {
        // Arrange: bind a listener on loopback at an ephemeral port.
        var port = GetFreeEphemeralPort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();

            // Act + Assert: the bound port reports as unavailable...
            Assert.False(PortFinder.IsPortAvailable(port));
        }
        finally
        {
            // ...and is available again once released.
            listener.Stop();
        }

        Assert.True(PortFinder.IsPortAvailable(port));
    }

    [Fact]
    public void IsPortAvailable_ReturnsFalse_ForOutOfRangePort()
    {
        // Act + Assert: invalid ports can never be available.
        Assert.False(PortFinder.IsPortAvailable(0));
        Assert.False(PortFinder.IsPortAvailable(65536));
    }

    [Fact]
    public void FindFreePort_ReturnsPreferred_WhenPreferredIsFree()
    {
        // Act: with 3333 unoccupied, the preferred port wins immediately.
        var port = PortFinder.FindFreePort(3333, 28);

        // Assert.
        Assert.Equal(3333, port);
    }

    [Fact]
    public void FindFreePort_SkipsOccupiedRange_AndReturnsFirstFreePort()
    {
        // Arrange: occupy 3333..3340 so the first free candidate is 3341.
        var listeners = BindRange(3333, 3340);
        try
        {
            // Act.
            var port = PortFinder.FindFreePort(3333, 28);

            // Assert: the result skips the occupied range and is itself available.
            Assert.True(port >= 3341, $"Expected a port >= 3341, got {port}");
            Assert.True(PortFinder.IsPortAvailable(port));
        }
        finally
        {
            Release(listeners);
        }
    }

    [Fact]
    public void FindFreePort_ReturnsZero_WhenWholeProbeRangeIsOccupied()
    {
        // Arrange: occupy the entire 3333..3360 probe window.
        var listeners = BindRange(3333, 3360);
        try
        {
            // Act: no candidate in the window is free.
            var port = PortFinder.FindFreePort(3333, 28);

            // Assert: 0 signals "no free port" to the caller.
            Assert.Equal(0, port);
        }
        finally
        {
            Release(listeners);
        }
    }

    [Fact]
    public void FindFreePort_ReturnsZero_WhenProbeWindowOverflowsPortSpace()
    {
        // Act: 65530 + 27 exceeds 65535, so the window cannot exist.
        var port = PortFinder.FindFreePort(65530, 28);

        // Assert.
        Assert.Equal(0, port);
    }

    [Fact]
    public void FindFreePort_ReturnsZero_ForInvalidPreferredPort()
    {
        // Act + Assert: preferred ports outside 1-65535 are rejected outright.
        Assert.Equal(0, PortFinder.FindFreePort(0, 28));
        Assert.Equal(0, PortFinder.FindFreePort(65536, 28));
    }

    [Fact]
    public void FindFreePortPair_ReturnsPreferred_WhenPreferredAndNextAreFree()
    {
        // Act: with 3333 and 3334 both free, the preferred port starts the pair.
        var port = PortFinder.FindFreePortPair(3333, 28);

        // Assert.
        Assert.Equal(3333, port);
    }

    [Fact]
    public void FindFreePortPair_SkipsOddOccupancy_AndReturnsConsecutiveFreePair()
    {
        // Arrange: occupy 3333 and 3335..3360 — only 3334 is free, so the first
        // CONSECUTIVE free pair starts at 3361... which is outside the window;
        // with 3334+3336 blocked by occupancy the finder must skip past the
        // whole occupied stretch. Bind 3333..3345 so the first pair is 3346+3347.
        var listeners = BindRange(3333, 3345);
        try
        {
            var port = PortFinder.FindFreePortPair(3333, 28);

            Assert.True(port >= 3346, $"Expected a pair starting >= 3346, got {port}");
            Assert.True(PortFinder.IsPortAvailable(port));
            Assert.True(PortFinder.IsPortAvailable(port + 1));
        }
        finally
        {
            Release(listeners);
        }
    }

    [Fact]
    public void FindFreePortPair_SkipsPortWhoseNeighborIsOccupied()
    {
        // Arrange: occupy ONLY 3334 — 3333 is free but its pair partner is not,
        // so the finder must continue to the next free pair.
        var listeners = BindRange(3334, 3334);
        try
        {
            var port = PortFinder.FindFreePortPair(3333, 28);

            Assert.NotEqual(3333, port);
            Assert.NotEqual(0, port);
            Assert.True(PortFinder.IsPortAvailable(port));
            Assert.True(PortFinder.IsPortAvailable(port + 1));
        }
        finally
        {
            Release(listeners);
        }
    }

    [Fact]
    public void FindFreePortPair_ReturnsZero_WhenWholeProbeRangeIsOccupied()
    {
        // Arrange: occupy the entire 3333..3360 probe window.
        var listeners = BindRange(3333, 3360);
        try
        {
            var port = PortFinder.FindFreePortPair(3333, 28);

            Assert.Equal(0, port);
        }
        finally
        {
            Release(listeners);
        }
    }

    [Fact]
    public void FindFreePortPair_ReturnsZero_ForInvalidInput()
    {
        Assert.Equal(0, PortFinder.FindFreePortPair(0, 28));
        Assert.Equal(0, PortFinder.FindFreePortPair(65536, 28));
        Assert.Equal(0, PortFinder.FindFreePortPair(65535, 28)); // pair would overflow
    }

    private static int GetFreeEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static List<TcpListener> BindRange(int start, int end)
    {
        var listeners = new List<TcpListener>(end - start + 1);
        for (var port = start; port <= end; port++)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listeners.Add(listener);
        }
        return listeners;
    }

    private static void Release(IEnumerable<TcpListener> listeners)
    {
        foreach (var listener in listeners)
        {
            listener.Stop();
        }
    }
}
