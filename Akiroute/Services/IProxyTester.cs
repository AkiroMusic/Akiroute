using Akiroute.Models;

namespace Akiroute.Services;

/// <summary>
/// Measures the latency of a single <see cref="ProxyNode"/>. Implementations
/// own their probing strategy (raw TCP connect, SOCKS5 handshake, HTTP request)
/// and must never throw out of <see cref="MeasureAsync"/>: every failure is
/// reported as a null result so callers can treat nodes uniformly.
/// </summary>
public interface IProxyTester
{
    /// <summary>
    /// Measures the round-trip latency to <paramref name="node"/> in
    /// milliseconds, or returns null when the node is unreachable, times out,
    /// or the probe fails for any reason.
    /// </summary>
    /// <param name="node">The node to probe.</param>
    /// <param name="cancellationToken">Cancels an in-progress probe.</param>
    /// <returns>Latency in milliseconds, or null when the probe failed.</returns>
    Task<long?> MeasureAsync(ProxyNode node, CancellationToken cancellationToken);
}
