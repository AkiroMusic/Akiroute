using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Akiroute.Models;
using Akiroute.Services;

namespace Akiroute.Tests.Services;

/// <summary>
/// XrayService tests. Unit tests never touch the real engine. Integration tests
/// use the source-tree xray.exe (Akiroute\Assets\engine\xray.exe), located by
/// walking up from the test output dir — the main project's asset None items do
/// not propagate to the referencing test project — and early-return when the
/// engine is missing so the suite never hard-fails on machines without assets.
/// Every test disposes the service so no xray process leaks between tests.
/// </summary>
public class XrayServiceTests : IDisposable
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";

    private static readonly string? SourceXrayExe = LocateSourceXrayExe();

    private readonly string _tempDir;
    private readonly List<XrayService> _services = new();

    public XrayServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "akiroute-tests-xray", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }
        _services.Clear();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_MissingExe_ReturnsFalseAndFails()
    {
        // Arrange: an exe path that cannot exist, and a guaranteed-free preferred
        // port so the failure is the exe check, not port resolution.
        var freePort = PortFinder.FindFreePort(42000, 28);
        Assert.NotEqual(0, freePort);
        using var service = NewService(Path.Combine(_tempDir, "no-such-xray.exe"), NewConfigPath());
        var states = new ConcurrentQueue<XrayServiceState>();
        service.StateChanged += (_, state) => states.Enqueue(state);

        // Act: start against a nonexistent engine.
        var result = await service.StartAsync(VlessNode(), Array.Empty<ProcessRule>(), ProxyMode.Global, freePort);

        // Assert: no process started, Failed state, summary points at the missing exe.
        Assert.False(result);
        Assert.False(service.IsRunning);
        Assert.Contains(XrayServiceState.Failed, states);
        Assert.Equal(XrayServiceState.Failed, states.Last());
        Assert.NotNull(service.LastCrashSummary);
        Assert.Contains("xray.exe not found", service.LastCrashSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_AllPortsOccupied_ReturnsFalseWithNoFreePort()
    {
        // Arrange: occupy the entire 40000..40027 probe window (below Windows'
        // ephemeral range) so every candidate the service can try is taken.
        var listeners = BindRange(40000, 40027);
        try
        {
            using var service = NewService(Path.Combine(_tempDir, "xray.exe"), NewConfigPath());
            var states = new ConcurrentQueue<XrayServiceState>();
            service.StateChanged += (_, state) => states.Enqueue(state);

            // Act: no candidate port is free, so the service must fail fast.
            var result = await service.StartAsync(VlessNode(), Array.Empty<ProcessRule>(), ProxyMode.Global, 40000);

            // Assert: no process, Failed state, exact "No free port" summary.
            Assert.False(result);
            Assert.False(service.IsRunning);
            Assert.Contains(XrayServiceState.Failed, states);
            Assert.Equal(XrayServiceState.Failed, states.Last());
            Assert.Equal("No free port", service.LastCrashSummary);
        }
        finally
        {
            Release(listeners);
        }
    }

    [Fact]
    public void AppendLogLine_RingBufferKeepsLastCapacityInOrder()
    {
        // Arrange: a service fed through the internal log-append path.
        using var service = new XrayService();
        var received = 0;
        service.LogReceived += (_, _) => received++;
        var capacity = XrayService.LogRingCapacity;
        var total = capacity + 25; // push past capacity so the oldest are dropped.

        // Act: push more lines than the ring can hold.
        for (var i = 1; i <= total; i++)
        {
            service.AppendLogLine($"line {i}");
        }

        // Assert: exactly `capacity` retained, oldest dropped, order preserved.
        Assert.Equal(capacity, service.RecentLogs.Count);
        Assert.Equal($"line {total - capacity + 1}", service.RecentLogs[0]);
        Assert.Equal($"line {total}", service.RecentLogs[^1]);
        Assert.Equal(total, received);
    }

    [Fact]
    public async Task StartStopAsync_WithRealXray_StartsListensAndStops()
    {
        if (SourceXrayExe is null)
        {
            return; // Skipped: source-tree xray.exe not available on this machine.
        }

        // Arrange: an unreachable outbound (127.0.0.1:1) is fine — xray starts and
        // listens regardless; no internet needed, no traffic flows.
        var preferredPort = FindFreePair(41000);
        using var service = NewService(SourceXrayExe, NewConfigPath());
        var states = new ConcurrentQueue<XrayServiceState>();
        var logLines = new ConcurrentQueue<string>();
        service.StateChanged += (_, state) => states.Enqueue(state);
        service.LogReceived += (_, line) => logLines.Enqueue(line);

        // Act: start against the real engine.
        var started = await service.StartAsync(VlessNode(), Array.Empty<ProcessRule>(), ProxyMode.Global, preferredPort);

        try
        {
            // Assert: running, resolved port in the probe window, reachable inbound.
            Assert.True(started);
            Assert.True(service.IsRunning);
            Assert.Contains(XrayServiceState.Running, states);
            Assert.InRange(service.LocalPort, preferredPort, preferredPort + 27);
            Assert.True(CanConnect(service.LocalPort), "SOCKS inbound must be reachable on the resolved port");
            Assert.NotEmpty(logLines);
        }
        finally
        {
            // Stop and assert the full teardown.
            await service.StopAsync();
            Assert.False(service.IsRunning);
            Assert.Contains(XrayServiceState.Stopped, states);
            Assert.Equal(XrayServiceState.Stopped, states.Last());
        }
    }

    [Fact]
    public async Task StartAsync_ConfigDirMissing_ReturnsFalseWithCrashSummary()
    {
        if (SourceXrayExe is null)
        {
            return; // Skipped: source-tree xray.exe not available on this machine.
        }

        // Arrange: the config temp path lives under a directory that does not
        // exist and is never created, so the atomic config write fails
        // deterministically (no dependence on xray's JSON parser).
        var configPath = Path.Combine(_tempDir, "missing-dir", "config.json");
        var freePort = PortFinder.FindFreePort(42000, 28);
        Assert.NotEqual(0, freePort);
        using var service = NewService(SourceXrayExe, configPath);
        var states = new ConcurrentQueue<XrayServiceState>();
        service.StateChanged += (_, state) => states.Enqueue(state);

        // Act: the write failure must surface as a failed start, not an exception.
        var result = await service.StartAsync(VlessNode(), Array.Empty<ProcessRule>(), ProxyMode.Global, freePort);

        // Assert: failed state and a non-empty summary naming the config write.
        Assert.False(result);
        Assert.False(service.IsRunning);
        Assert.Contains(XrayServiceState.Failed, states);
        Assert.Equal(XrayServiceState.Failed, states.Last());
        Assert.False(string.IsNullOrEmpty(service.LastCrashSummary));
        Assert.Contains("Failed to write config", service.LastCrashSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WritesBuilderOutputToConfigFile()
    {
        if (SourceXrayExe is null)
        {
            return; // Skipped: source-tree xray.exe not available on this machine.
        }

        // Arrange: a real start so the config file is produced and consumed.
        var preferredPort = FindFreePair(41000);
        var configPath = NewConfigPath();
        using var service = NewService(SourceXrayExe, configPath);

        try
        {
            // Act: start against the real engine.
            var started = await service.StartAsync(VlessNode(), Array.Empty<ProcessRule>(), ProxyMode.Global, preferredPort);
            Assert.True(started);

            // Assert: the temp config file on disk carries the builder output —
            // the SOCKS inbound tag and its loopback listen address.
            var json = File.ReadAllText(configPath);
            Assert.Contains("socks-in", json, StringComparison.Ordinal);
            Assert.Contains("127.0.0.1", json, StringComparison.Ordinal);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    private string NewConfigPath() => Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".json");

    private XrayService NewService(string? xrayExePath, string configPath)
    {
        var service = new XrayService(xrayExePath, configPath);
        _services.Add(service);
        return service;
    }

    private static ProxyNode VlessNode() => new()
    {
        Type = "vless",
        Address = "127.0.0.1",
        Port = 1,
        ExtraParams = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            ["uuid"] = JsonValue.Create(Uuid)!,
            ["encryption"] = JsonValue.Create("none")!,
        },
    };

    /// <summary>
    /// Walks up from the test output dir until the repo root (containing
    /// Akiroute\Assets\engine\xray.exe) is found; returns null when absent.
    /// </summary>
    private static string? LocateSourceXrayExe()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Akiroute", "Assets", "engine", "xray.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>Finds a port where both it and its successor are free (xray also
    /// binds an HTTP inbound on localPort + 1).</summary>
    private static int FindFreePair(int preferred)
    {
        for (var port = preferred; port < preferred + 100; port++)
        {
            if (PortFinder.IsPortAvailable(port) && PortFinder.IsPortAvailable(port + 1))
            {
                return port;
            }
        }
        return preferred;
    }

    private static bool CanConnect(int port)
    {
        using var client = new TcpClient();
        try
        {
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
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
