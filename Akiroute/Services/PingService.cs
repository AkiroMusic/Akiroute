using Akiroute.Models;

namespace Akiroute.Services;

/// <summary>
/// Pings proxy nodes through an <see cref="IProxyTester"/> and writes each
/// measured latency back to <see cref="ProxyNode.PingMs"/>. Concurrent probes
/// are bounded by a semaphore (<see cref="MaxConcurrency"/>) so pinging a large
/// subscription cannot saturate the uplink.
/// </summary>
public sealed class PingService
{
    /// <summary>Maximum number of in-flight probes (plan: 并发上限 10).</summary>
    private const int MaxConcurrency = 10;

    private readonly IProxyTester _tester;
    private readonly SemaphoreSlim _gate;

    /// <summary>Creates a pinger that measures through <paramref name="tester"/>.</summary>
    public PingService(IProxyTester tester)
    {
        ArgumentNullException.ThrowIfNull(tester);
        _tester = tester;
        _gate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
    }

    /// <summary>
    /// Pings every node in <paramref name="nodes"/> concurrently. A successful
    /// measurement writes the latency to <see cref="ProxyNode.PingMs"/>; a
    /// failed probe resets it to -1. Cancellation stops the pending probes and
    /// never throws out of this method.
    /// </summary>
    public async Task PingAllAsync(IEnumerable<ProxyNode> nodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var tasks = new List<Task>();
        foreach (var node in nodes)
        {
            if (node is null)
            {
                continue;
            }

            tasks.Add(PingWithGateAsync(node, cancellationToken));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected; every cancelled probe already unwound.
        }
    }

    /// <summary>
    /// Pings a single node and returns its latency in milliseconds, or null
    /// when the node is unreachable or the probe is cancelled.
    /// <see cref="ProxyNode.PingMs"/> is left untouched.
    /// </summary>
    public async Task<long?> PingAsync(ProxyNode node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        return await _tester.MeasureAsync(node, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Probes one node under the concurrency gate and writes its result.</summary>
    private async Task PingWithGateAsync(ProxyNode node, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var latency = await _tester.MeasureAsync(node, cancellationToken).ConfigureAwait(false);
            if (latency is long measured)
            {
                node.PingMs = (int)measured;
            }
            else
            {
                node.PingMs = -1;
            }
        }
        catch (OperationCanceledException)
        {
            // Probe was cancelled mid-flight; the node keeps its previous value.
        }
        finally
        {
            _gate.Release();
        }
    }
}
