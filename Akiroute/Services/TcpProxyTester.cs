using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Akiroute.Models;

namespace Akiroute.Services;

/// <summary>
/// Probes a node's reachability by opening a real TCP connection to
/// <see cref="ProxyNode.Address"/>:<see cref="ProxyNode.Port"/> and timing the
/// connect. SOCKS5 nodes (<see cref="ProxyNode.Type"/> "socks"/"socks5")
/// additionally complete a real SOCKS5 handshake (no-auth greeting plus a
/// CONNECT to a probe target) before success is counted — the project spec's
/// "真 socket SOCKS5 测试". Other protocol types only need the TCP connect.
/// Never throws out of <see cref="MeasureAsync"/>.
/// </summary>
public sealed class TcpProxyTester : IProxyTester
{
    /// <summary>Hard ceiling for a single probe; a node that never answers is unreachable.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Host the SOCKS5 CONNECT is issued against.</summary>
    private const string SocksProbeHost = "cp.cloudflare.com";

    /// <summary>Port the SOCKS5 CONNECT is issued against.</summary>
    private const int SocksProbePort = 443;

    /// <inheritdoc />
    public async Task<long?> MeasureAsync(ProxyNode node, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(node.Address) || node.Port is < 1 or > 65535)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);

            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();

            await client.ConnectAsync(node.Address, node.Port, timeout.Token).ConfigureAwait(false);
            if (IsSocksNode(node))
            {
                await CompleteSocksHandshakeAsync(client, timeout.Token).ConfigureAwait(false);
            }

            stopwatch.Stop();
            return stopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException)
        {
            // Hard timeout or caller cancellation: the node did not answer in time.
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TcpProxyTester] Probe failed for {node.Address}:{node.Port}: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when the node speaks the SOCKS5 protocol.</summary>
    private static bool IsSocksNode(ProxyNode node) =>
        node.Type is "socks" or "socks5";

    /// <summary>
    /// Completes a SOCKS5 handshake over <paramref name="client"/>: the
    /// no-authentication greeting followed by a CONNECT to
    /// <see cref="SocksProbeHost"/>:<see cref="SocksProbePort"/>. Throws on any
    /// protocol violation or failure reply so the caller reports the node down.
    /// </summary>
    private static async Task CompleteSocksHandshakeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();

        // Greeting: SOCKS5, one method, "no authentication required".
        byte[] greeting = { 0x05, 0x01, 0x00 };
        await stream.WriteAsync(greeting, cancellationToken).ConfigureAwait(false);

        var methodReply = new byte[2];
        await stream.ReadExactlyAsync(methodReply, cancellationToken).ConfigureAwait(false);
        if (methodReply[0] != 0x05 || methodReply[1] != 0x00)
        {
            throw new IOException($"SOCKS5 server rejected the greeting (method {methodReply[1]}).");
        }

        // CONNECT request using domain-name addressing.
        var hostBytes = Encoding.UTF8.GetBytes(SocksProbeHost);
        using var request = new MemoryStream();
        request.WriteByte(0x05); // version
        request.WriteByte(0x01); // command: CONNECT
        request.WriteByte(0x00); // reserved
        request.WriteByte(0x03); // address type: domain name
        request.WriteByte((byte)hostBytes.Length);
        request.Write(hostBytes);
        request.WriteByte((byte)(SocksProbePort >> 8));
        request.WriteByte((byte)(SocksProbePort & 0xFF));

        await stream.WriteAsync(request.ToArray(), cancellationToken).ConfigureAwait(false);

        // Reply header: version, status, reserved, address type.
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (header[0] != 0x05)
        {
            throw new IOException($"SOCKS5 reply used an unexpected version {header[0]}.");
        }

        if (header[1] != 0x00)
        {
            throw new IOException($"SOCKS5 CONNECT failed with reply code {header[1]}.");
        }

        await ReadReplyAddressAsync(stream, header[3], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the variable-length bound address from a SOCKS5 success reply.</summary>
    private static async Task ReadReplyAddressAsync(Stream stream, byte addressType, CancellationToken cancellationToken)
    {
        switch (addressType)
        {
            case 0x01: // IPv4
                await ReadBytesAsync(stream, 4, cancellationToken).ConfigureAwait(false);
                break;
            case 0x03: // domain name
                var lengthBytes = await ReadBytesAsync(stream, 1, cancellationToken).ConfigureAwait(false);
                await ReadBytesAsync(stream, lengthBytes[0], cancellationToken).ConfigureAwait(false);
                break;
            case 0x04: // IPv6
                await ReadBytesAsync(stream, 16, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new IOException($"SOCKS5 reply used an unknown address type {addressType}.");
        }

        await ReadBytesAsync(stream, 2, cancellationToken).ConfigureAwait(false); // bound port
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes, or throws when the peer closes early.</summary>
    private static async Task<byte[]> ReadBytesAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }
}
