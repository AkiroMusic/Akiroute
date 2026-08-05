using System.Diagnostics;
using System.Net;
using Akiroute.Models;

namespace Akiroute.Services;

/// <summary>
/// Probes a node by issuing an HTTP GET against a fixed latency endpoint
/// (<c>http://cp.cloudflare.com/generate_204</c> by default) and timing the
/// round-trip with a <see cref="Stopwatch"/>. A 204 No Content reply counts as
/// reachable; anything else (non-204 status, exception, or the 3-second
/// timeout) reports the node as unreachable. Never throws out of
/// <see cref="MeasureAsync"/>.
/// </summary>
public sealed class HttpProxyTester : IProxyTester
{
    /// <summary>Default latency endpoint; answers HTTP 204 when reachable.</summary>
    private const string DefaultProbeUrl = "http://cp.cloudflare.com/generate_204";

    /// <summary>Hard ceiling for a single probe.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _httpClient;
    private readonly Uri _probeUrl;

    /// <summary>
    /// Creates a tester over its own <see cref="HttpClient"/> (3-second timeout)
    /// probing the default <c>cp.cloudflare.com/generate_204</c> endpoint.
    /// </summary>
    public HttpProxyTester()
        : this(null, DefaultProbeUrl)
    {
    }

    /// <summary>
    /// Creates a tester over a caller-provided <see cref="HttpClient"/> (a new
    /// one is created when <paramref name="httpClient"/> is null), probing
    /// <paramref name="probeUrl"/> instead of the default so tests can point at
    /// a local listener. The 3-second timeout is always applied.
    /// </summary>
    public HttpProxyTester(HttpClient? httpClient, string probeUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(probeUrl);
        _httpClient = httpClient ?? new HttpClient();
        _probeUrl = new Uri(probeUrl, UriKind.Absolute);
        _httpClient.Timeout = RequestTimeout;
    }

    /// <inheritdoc />
    public async Task<long?> MeasureAsync(ProxyNode node, CancellationToken cancellationToken)
    {
        if (node.Address is null || node.Port is < 1 or > 65535)
        {
            return null;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await _httpClient
                .GetAsync(_probeUrl, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();

            return response.StatusCode == HttpStatusCode.NoContent ? stopwatch.ElapsedMilliseconds : null;
        }
        catch (OperationCanceledException)
        {
            // Timeout or caller cancellation: the endpoint did not answer in time.
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HttpProxyTester] Probe failed for {node.Address}:{node.Port}: {ex.Message}");
            return null;
        }
    }
}
