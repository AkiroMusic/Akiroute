using System.Net;
using Akiroute.Models;
using Akiroute.Services;

namespace Akiroute.Tests.Services;

/// <summary>
/// HttpProxyTester tests driven by a fake <see cref="HttpMessageHandler"/>, so
/// no socket or real endpoint is involved — the probe URL never leaves the test
/// process.
/// </summary>
public class HttpProxyTesterTests
{
    [Fact]
    public async Task MeasureAsync_EndpointReturns204_ReturnsLatency()
    {
        // Arrange: a handler that answers 204 No Content.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tester = CreateTester(new StubHandler(HttpStatusCode.NoContent));

        // Act: probe.
        var latency = await tester.MeasureAsync(NewNode(), cts.Token);

        // Assert: the endpoint is reachable, so a latency is reported.
        Assert.NotNull(latency);
        Assert.InRange(latency.Value, 0, 3000);
    }

    [Fact]
    public async Task MeasureAsync_EndpointReturns500_ReturnsNull()
    {
        // Arrange: a handler that answers 500 Internal Server Error.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tester = CreateTester(new StubHandler(HttpStatusCode.InternalServerError));

        // Act: probe.
        var latency = await tester.MeasureAsync(NewNode(), cts.Token);

        // Assert: any non-204 answer counts as a failed probe.
        Assert.Null(latency);
    }

    [Fact]
    public async Task MeasureAsync_EndpointUnreachable_ReturnsNull()
    {
        // Arrange: a handler that simulates a transport-level failure.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tester = CreateTester(new ThrowingHandler());

        // Act + Assert: the failure is swallowed and reported as null.
        var latency = await tester.MeasureAsync(NewNode(), cts.Token);
        Assert.Null(latency);
    }

    [Fact]
    public async Task MeasureAsync_AlreadyCancelled_ReturnsNull()
    {
        // Arrange: a probe token that is already cancelled.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tester = CreateTester(new StubHandler(HttpStatusCode.NoContent));

        // Act + Assert: the request is aborted and reported as null.
        var latency = await tester.MeasureAsync(NewNode(), cts.Token);
        Assert.Null(latency);
    }

    [Fact]
    public async Task MeasureAsync_InvalidNode_ReturnsNull()
    {
        // Arrange: a node with no address and an out-of-range port.
        var node = new ProxyNode { Address = null, Port = 0 };
        var tester = CreateTester(new StubHandler(HttpStatusCode.NoContent));

        // Act + Assert: no throw, no latency.
        var latency = await tester.MeasureAsync(node, CancellationToken.None);
        Assert.Null(latency);
    }

    private static ProxyNode NewNode() => new() { Address = "127.0.0.1", Port = 443, Type = "vless" };

    private static HttpProxyTester CreateTester(HttpMessageHandler handler) =>
        new(new HttpClient(handler), "http://127.0.0.1:9/generate_204");

    /// <summary>Replies with a fixed status code to every request.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public StubHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_statusCode));
    }

    /// <summary>Faults like a connection failure at the transport layer.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Simulated connection refused"));
    }
}
