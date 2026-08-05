using System.Diagnostics;
using Akiroute.Models;
using Akiroute.Services;

namespace Akiroute.Tests.Services;

/// <summary>
/// PingService tests driven by a controllable fake <see cref="IProxyTester"/>:
/// result propagation into <see cref="ProxyNode.PingMs"/>, the -1 failure
/// default, the concurrency cap of 10, and prompt cancellation.
/// </summary>
public class PingServiceTests
{
    [Fact]
    public async Task PingAllAsync_SuccessfulNodes_WriteMeasuredLatency()
    {
        // Arrange: five nodes and a tester that always succeeds.
        var nodes = CreateNodes(5);
        var service = new PingService(new FakeTester(TimeSpan.FromMilliseconds(10), _ => 42));

        // Act: ping all.
        await service.PingAllAsync(nodes, CancellationToken.None);

        // Assert: every node picked up the measured value.
        Assert.All(nodes, n => Assert.Equal(42, n.PingMs));
    }

    [Fact]
    public async Task PingAllAsync_FailedNodes_KeepPingMsMinusOne()
    {
        // Arrange: five nodes and a tester that always fails.
        var nodes = CreateNodes(5);
        var service = new PingService(new FakeTester(TimeSpan.FromMilliseconds(10), _ => null));

        // Act: ping all.
        await service.PingAllAsync(nodes, CancellationToken.None);

        // Assert: every node stayed at the -1 untested default.
        Assert.All(nodes, n => Assert.Equal(-1, n.PingMs));
    }

    [Fact]
    public async Task PingAllAsync_MixedResults_WritePerNodeOutcome()
    {
        // Arrange: a tester that fails even ports and succeeds odd ones.
        var nodes = CreateNodes(6);
        var service = new PingService(
            new FakeTester(TimeSpan.FromMilliseconds(10), n => (n.Port % 2) == 0 ? null : 100 + n.Port));

        // Act: ping all.
        await service.PingAllAsync(nodes, CancellationToken.None);

        // Assert: each node reflects its own outcome.
        for (var i = 0; i < nodes.Count; i++)
        {
            var expected = (nodes[i].Port % 2) == 0 ? -1 : 100 + nodes[i].Port;
            Assert.Equal(expected, nodes[i].PingMs);
        }
    }

    [Fact]
    public async Task PingAllAsync_MoreThanTenNodes_NeverExceedsConcurrencyCap()
    {
        // Arrange: 25 nodes (well above the cap of 10) behind a slow tester.
        var nodes = CreateNodes(25);
        var tester = new FakeTester(TimeSpan.FromMilliseconds(30), _ => 42);
        var service = new PingService(tester);

        // Act: ping all.
        await service.PingAllAsync(nodes, CancellationToken.None);

        // Assert: at no point were more than 10 probes in flight simultaneously.
        Assert.InRange(tester.MaxInFlight, 1, 10);
    }

    [Fact]
    public async Task PingAllAsync_Cancellation_ReturnsPromptly()
    {
        // Arrange: a tester that never completes, cancelled after 50 ms.
        var nodes = CreateNodes(20);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var service = new PingService(new FakeTester(TimeSpan.FromSeconds(30), _ => 42));

        // Act: ping all; the cancel must unwind it quickly.
        var stopwatch = Stopwatch.StartNew();
        await service.PingAllAsync(nodes, cts.Token);
        stopwatch.Stop();

        // Assert: the call returned far before the slow probes would finish.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task PingAsync_ReturnsMeasuredLatencyWithoutSideEffects()
    {
        // Arrange: a tester that reports a fixed value.
        var node = NewNode(1);
        var service = new PingService(new FakeTester(TimeSpan.Zero, _ => 123));

        // Act: ping one node.
        var latency = await service.PingAsync(node, CancellationToken.None);

        // Assert: the value is surfaced and PingMs is left untouched.
        Assert.Equal(123, latency);
        Assert.Equal(-1, node.PingMs);
    }

    private static List<ProxyNode> CreateNodes(int count) =>
        Enumerable.Range(1, count).Select(NewNode).ToList();

    private static ProxyNode NewNode(int port) => new()
    {
        Id = $"n{port}",
        Name = $"Node {port}",
        Type = "vless",
        Address = "127.0.0.1",
        Port = port,
    };

    /// <summary>
    /// Fake tester whose latency/result is fully controlled by the test and that
    /// tracks the maximum number of simultaneously in-flight probes.
    /// </summary>
    private sealed class FakeTester : IProxyTester
    {
        private readonly TimeSpan _delay;
        private readonly Func<ProxyNode, long?> _result;
        private int _inFlight;
        private int _maxInFlight;

        public FakeTester(TimeSpan delay, Func<ProxyNode, long?> result)
        {
            _delay = delay;
            _result = result;
        }

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public async Task<long?> MeasureAsync(ProxyNode node, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inFlight);
            UpdateMax(now);
            try
            {
                await Task.Delay(_delay, cancellationToken);
                return _result(node);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void UpdateMax(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxInFlight);
                if (value <= current || Interlocked.CompareExchange(ref _maxInFlight, value, current) == current)
                {
                    return;
                }
            }
        }
    }
}
