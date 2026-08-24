using System.Text.Json.Nodes;
using System.Threading;
using Akiroute.Models;
using Akiroute.Services;
using Akiroute.ViewModels;

namespace Akiroute.Tests.ViewModels;

/// <summary>
/// ProxyStatusViewModel tests. Every test injects an <see cref="XrayService"/>
/// pointed at a nonexistent xray.exe (failure paths) or never starts it (traffic
/// sampling), so no real engine and no WinUI runtime are ever involved. The traffic
/// loop is driven synchronously through the internal sampling seam and with a
/// scripted file-size provider.
/// </summary>
public class ProxyStatusViewModelTests
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";

    [Fact]
    public async Task StartProxy_NoSelectedNode_SetsLastErrorAndStaysStopped()
    {
        using var xray = NewXray();
        var settings = new AppSettings(); // SelectedNodeId is empty.
        var vm = NewViewModel(xray, settings);

        await vm.StartProxyAsync();

        Assert.False(vm.IsRunning);
        Assert.False(vm.IsStarting);
        Assert.Equal("未选择节点", vm.LastError);
    }

    [Fact]
    public async Task ToggleProxy_StartWithMissingExe_FailsWithNotFound()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "akiroute-tests-status", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new AppSettings();
            var node = VlessNode();
            settings.Nodes.Add(node);
            settings.SelectedNodeId = node.Id;
            settings.Port = PortFinder.FindFreePort(42000, 28);
            Assert.NotEqual(0, settings.Port);

            using var xray = new XrayService(Path.Combine(tempDir, "no-such-xray.exe"), Path.Combine(tempDir, "config.json"));
            var vm = NewViewModel(xray, settings);

            await vm.ToggleProxyAsync();

            Assert.False(vm.IsRunning);
            Assert.False(vm.IsStarting);
            Assert.Contains("xray.exe not found", vm.LastError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SampleTrafficOnce_ComputesThroughputFromFileGrowth()
    {
        using var xray = NewXray();
        var script = new Queue<long>(new long[] { 0, 500, 1200 });
        var vm = NewViewModel(xray, new AppSettings(), () => script.Dequeue());

        vm.SampleTrafficOnce();
        vm.SampleTrafficOnce();

        Assert.Equal(2, vm.TrafficSeries.Count);
        Assert.Equal(0, vm.TrafficSeries[0]);
        Assert.Equal(500, vm.TrafficSeries[1]);
        Assert.Equal(500, vm.ThroughputBytesPerSecond);
    }

    [Fact]
    public async Task TrafficLoop_SamplesGrowthWhileRunningAndStopsOnStop()
    {
        using var xray = NewXray();
        var script = new Queue<long>(new long[] { 0, 120, 340, 780, 1500, 2500 });
        var vm = NewViewModel(xray, new AppSettings(), () => script.Count > 0 ? script.Dequeue() : 0);
        vm.SampleIntervalMs = 20;

        vm.StartTrafficLoop();
        await Task.Delay(130);

        var grownCount = vm.TrafficSeries.Count;
        Assert.True(grownCount >= 3, $"expected at least 3 samples, got {grownCount}");

        vm.StopTrafficLoop();
        var loopTask = vm.TrafficLoopTask;
        Assert.NotNull(loopTask);
        await loopTask;

        // Once the loop task completed, no further samples may be produced.
        var stoppedCount = vm.TrafficSeries.Count;
        await Task.Delay(80);
        Assert.Equal(stoppedCount, vm.TrafficSeries.Count);
    }

    [Fact]
    public void LogReceived_MirrorsXrayRecentLogs()
    {
        using var xray = NewXray();
        var vm = NewViewModel(xray, new AppSettings());

        xray.AppendLogLine("hello xray");

        Assert.Equal("hello xray", Assert.Single(vm.RecentLogs));
    }

    [Fact]
    public async Task StopProxy_ClearsRunningState()
    {
        using var xray = NewXray();
        var vm = NewViewModel(xray, new AppSettings());

        await vm.StopProxyAsync();

        Assert.False(vm.IsRunning);
        Assert.False(vm.IsStarting);
    }

    private static XrayService NewXray() =>
        new(Path.Combine(Path.GetTempPath(), "no-such-xray.exe"));

    private static ProxyStatusViewModel NewViewModel(XrayService xray, AppSettings settings, Func<long>? fileSizeProvider = null) =>
        new(xray, settings, static action => action(), fileSizeProvider);

    /// <summary>
    /// Verifies that concurrent calls to ToggleProxyAsync do not both enter
    /// StartProxyAsync — the debounce guard ensures StartAsync is called
    /// exactly once for rapid back-to-back invocations. Both calls invoke
    /// StartAsync through XrayService (which serializes via _lifecycleLock);
    /// The state-change counter tracks how many times _runOnUiThread is invoked
    /// for lifecycle events — with the guard it is 1; without it, 2.
    /// </summary>
    [Fact]
    public async Task ToggleProxyAsync_ConcurrentCalls_StartOnlyOnce()
    {
        using var xray = NewXray();
        var settings = new AppSettings();
        var node = VlessNode();
        settings.Nodes.Add(node);
        settings.SelectedNodeId = node.Id;
        settings.Port = PortFinder.FindFreePort(42000, 28);
        Assert.NotEqual(0, settings.Port);

        var stateChangeCount = 0;
        var vm = new ProxyStatusViewModel(
            xray,
            settings,
            action => { Interlocked.Increment(ref stateChangeCount); action(); });

        // Reset after construction — ApplyState runs in ctor without _runOnUiThread.
        stateChangeCount = 0;

        // Fire two concurrent toggle calls against a nonexistent xray.exe.
        await Task.WhenAll(vm.ToggleProxyAsync(), vm.ToggleProxyAsync());

        // A correct re-entry guard would ensure StartAsync is called exactly once,
        // producing one state-change callback. Without the guard, both calls
        // invoke StartAsync (serialized by XrayService._lifecycleLock) and both
        // trigger a Failed state change — the counter reaches 2.
        Assert.Equal(1, stateChangeCount);
        Assert.False(vm.IsStarting);
        Assert.False(vm.IsRunning);
    }

    /// <summary>
    /// Drives SampleTrafficOnce through scripted fileSizeProvider values:
    /// increasing sizes, then a decrease (log rotation), then more increases
    /// past 60 samples. Asserts the series never exceeds TrafficSeriesCapacity
    /// and every delta is non-negative (negative deltas clamp to zero).
    /// </summary>
    [Fact]
    public void TrafficSeries_CapsAtCapacity_And_ClampsNegativeDeltas()
    {
        using var xray = NewXray();
        var sizes = new List<long>();
        for (var i = 0; i < 50; i++)
        {
            sizes.Add(i * 1000); // steady growth: 0, 1000, 2000, ...
        }

        // Simulate log rotation: file shrinks back to a small value.
        sizes.Add(500);

        // Then more growth past the 60-sample cap.
        for (var i = 0; i < 30; i++)
        {
            sizes.Add(500 + (i + 1) * 1000);
        }

        var index = 0;
        var vm = NewViewModel(xray, new AppSettings(), () =>
        {
            if (index < sizes.Count)
            {
                return sizes[index++];
            }

            return sizes[^1];
        });

        // Drive SampleTrafficOnce for every scripted value.
        for (var i = 0; i < sizes.Count; i++)
        {
            vm.SampleTrafficOnce();
        }

        // The series must never exceed the production cap.
        Assert.True(
            vm.TrafficSeries.Count <= ProxyStatusViewModel.TrafficSeriesCapacity,
            $"Series count {vm.TrafficSeries.Count} exceeds capacity {ProxyStatusViewModel.TrafficSeriesCapacity}");

        // Every value must be non-negative (negative deltas from log rotation clamp to 0).
        foreach (var value in vm.TrafficSeries)
        {
            Assert.True(value >= 0, $"Negative throughput value: {value}");
        }
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
}
