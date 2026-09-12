using Akiroute.Models;
using Akiroute.Services;
using Akiroute.ViewModels;

namespace Akiroute.Tests.ViewModels;

/// <summary>
/// MainViewModel tests. The coordinator is constructed with the injectable
/// thread-dispatch seam (inline execution), a stub ping tester, an empty process
/// monitor, and an <see cref="XrayService"/> pointed at a nonexistent engine, so no
/// real processes, probes, or engine runs are ever involved.
/// </summary>
public class MainViewModelTests
{
    [Fact]
    public void Constructor_WiresChildViewModels()
    {
        var vm = NewViewModel(new AppSettings());

        Assert.NotNull(vm.Nodes);
        Assert.NotNull(vm.Settings);
        Assert.NotNull(vm.Status);
        Assert.NotNull(vm.Processes);
        Assert.False(vm.IsProxyRunning);
    }

    [Fact]
    public async Task ToggleProxyAsync_DelegatesToStatus_NoNodeSetsLastError()
    {
        var settings = new AppSettings(); // No selected node.
        var vm = NewViewModel(settings);

        await vm.ToggleProxyAsync();

        Assert.Equal("No node selected", vm.Status.LastError);
        Assert.False(vm.Status.IsRunning);
        Assert.False(vm.IsProxyRunning);
    }

    [Fact]
    public void SaveSettings_WritesSettingsToTempFileWithoutError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "akiroute-tests-main", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var settings = new AppSettings { Port = 4242 };
            var vm = NewViewModel(settings);
            vm.ConfigFilePath = path;

            vm.SaveSettings();

            Assert.True(File.Exists(path));
            Assert.Null(vm.Settings.SaveError);

            var loaded = SettingsService.Load(path);
            Assert.Equal(4242, loaded.Port);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PingAllAsync_DelegatesWithoutThrowingOnEmptyNodes()
    {
        var vm = NewViewModel(new AppSettings());

        await vm.PingAllAsync();

        Assert.False(vm.Nodes.IsPinging);
        Assert.Empty(vm.Nodes.Nodes);
    }

    private static MainViewModel NewViewModel(AppSettings settings)
    {
        // The engine path can never exist, so the service never spawns a process
        // and needs no disposal; the coordinator keeps it for its whole lifetime.
        var xray = new XrayService(Path.Combine(Path.GetTempPath(), "no-such-xray.exe"));
        var monitor = new ProcessMonitorService(() => Array.Empty<ProcessMonitorService.ProcessRecord>());
        var ping = new PingService(new StubTester());
        return new MainViewModel(settings, xray, ping, monitor, static action => action());
    }

    /// <summary>Stub latency tester: reports a fixed latency, never touches the network.</summary>
    private sealed class StubTester : IProxyTester
    {
        public Task<long?> MeasureAsync(ProxyNode node, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(42);
    }
}
