using System.Net;
using System.Net.Sockets;
using Akiroute.Models;
using Akiroute.Services;

namespace Akiroute.Tests.Services;

/// <summary>
/// TcpProxyTester tests: a real loopback listener, a closed port, an invalid
/// node, and an in-test SOCKS5 server that accepts or rejects the handshake.
/// Every test is self-contained on loopback — nothing hits the real internet.
/// </summary>
public class TcpProxyTesterTests
{
    [Fact]
    public async Task MeasureAsync_OpenListener_ReturnsLatency()
    {
        // Arrange: a real TcpListener bound to an ephemeral loopback port.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act: probe the listening port.
        var latency = await new TcpProxyTester().MeasureAsync(NewNode(port), cts.Token);

        // Assert: a non-negative latency under the 3-second probe ceiling.
        Assert.NotNull(latency);
        Assert.InRange(latency.Value, 0, 3000);
    }

    [Fact]
    public async Task MeasureAsync_ClosedPort_ReturnsNull()
    {
        // Arrange: bind an ephemeral port, then stop the listener so nothing listens.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act: probe the abandoned port.
        var latency = await new TcpProxyTester().MeasureAsync(NewNode(port), cts.Token);

        // Assert: the connect is refused and the tester reports unreachable.
        Assert.Null(latency);
    }

    [Fact]
    public async Task MeasureAsync_InvalidNode_ReturnsNull()
    {
        // Arrange: a node with no address and an out-of-range port.
        var node = new ProxyNode { Address = null, Port = 0 };

        // Act: probe the invalid node.
        var latency = await new TcpProxyTester().MeasureAsync(node, CancellationToken.None);

        // Assert: no throw, no latency.
        Assert.Null(latency);
    }

    [Fact]
    public async Task MeasureAsync_Socks5ServerAccepts_ReturnsLatency()
    {
        // Arrange: a minimal SOCKS5 server that accepts the no-auth greeting
        // and answers the CONNECT with success.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act: probe through the server.
        var latency = await MeasureThroughSocksAsync(
            methodReply: new byte[] { 0x05, 0x00 },
            connectReply: new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0x01, 0xBB },
            cts.Token);

        // Assert: the full handshake completed, so a latency was measured.
        Assert.NotNull(latency);
        Assert.InRange(latency.Value, 0, 3000);
    }

    [Fact]
    public async Task MeasureAsync_Socks5ServerRejectsGreeting_ReturnsNull()
    {
        // Arrange: the server refuses every auth method (0xFF).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act + Assert: the handshake fails, so the probe reports unreachable.
        var latency = await MeasureThroughSocksAsync(
            methodReply: new byte[] { 0x05, 0xFF },
            connectReply: Array.Empty<byte>(),
            cts.Token);
        Assert.Null(latency);
    }

    [Fact]
    public async Task MeasureAsync_Socks5ServerRejectsConnect_ReturnsNull()
    {
        // Arrange: the server accepts the greeting but rejects the CONNECT (0x05).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act + Assert: the failed CONNECT makes the probe report unreachable.
        var latency = await MeasureThroughSocksAsync(
            methodReply: new byte[] { 0x05, 0x00 },
            connectReply: new byte[] { 0x05, 0x05, 0x00, 0x01, 127, 0, 0, 1, 0x01, 0xBB },
            cts.Token);
        Assert.Null(latency);
    }

    private static ProxyNode NewNode(int port) => new()
    {
        Address = "127.0.0.1",
        Port = port,
        Type = "vless",
    };

    /// <summary>
    /// Runs an in-test SOCKS5 server on an ephemeral loopback port and probes it
    /// through <see cref="TcpProxyTester"/>. The server answers the greeting
    /// with <paramref name="methodReply"/> and the CONNECT with
    /// <paramref name="connectReply"/>. Returns the measured latency (or null).
    /// </summary>
    private static async Task<long?> MeasureThroughSocksAsync(byte[] methodReply, byte[] connectReply, CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var node = new ProxyNode { Address = "127.0.0.1", Port = port, Type = "socks5" };

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            var stream = client.GetStream();

            var greeting = new byte[3];
            await stream.ReadExactlyAsync(greeting, cancellationToken);
            await stream.WriteAsync(methodReply, cancellationToken);

            if (methodReply[1] != 0x00)
            {
                // No acceptable auth method: the client is expected to hang up now.
                return;
            }

            var connect = new byte[4];
            await stream.ReadExactlyAsync(connect, cancellationToken);
            var hostLength = new byte[1];
            await stream.ReadExactlyAsync(hostLength, cancellationToken);
            var host = new byte[hostLength[0]];
            await stream.ReadExactlyAsync(host, cancellationToken);
            var portBytes = new byte[2];
            await stream.ReadExactlyAsync(portBytes, cancellationToken);

            await stream.WriteAsync(connectReply, cancellationToken);
        });

        long? latency;
        try
        {
            latency = await new TcpProxyTester().MeasureAsync(node, cancellationToken);
            await server;
        }
        finally
        {
            listener.Stop();
        }

        return latency;
    }
}
