using Akiroute.Models;
using Akiroute.Services;
using Akiroute.ViewModels;

namespace Akiroute.Tests.ViewModels;

/// <summary>
/// ProcessListViewModel tests. The view model is constructed with the injectable
/// thread-dispatch seam (inline execution) and a monitor backed by a fake enumerator,
/// so no real processes and no WinUI runtime are ever involved.
/// </summary>
public class ProcessListViewModelTests
{
    [Fact]
    public async Task RefreshAsync_PopulatesItemsFromTheMonitor()
    {
        var service = NewService(new[]
        {
            new ProcessMonitorService.ProcessRecord(1, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", HasWindow: true),
            new ProcessMonitorService.ProcessRecord(2, "code.exe", @"C:\Program Files\Microsoft VS Code\Code.exe", HasWindow: true),
        });
        var vm = NewViewModel(service);

        await vm.RefreshAsync();

        Assert.Equal(2, vm.Items.Count);
        Assert.Contains(vm.Items, i => i.ProcessName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.Items, i => i.ProcessName.Equals("code.exe", StringComparison.OrdinalIgnoreCase));
        Assert.False(vm.IsRefreshing);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task RefreshAsync_PreservesUserActionAcrossScans()
    {
        const string chromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var records = new List<ProcessMonitorService.ProcessRecord>
        {
            new(1, "chrome.exe", chromePath, HasWindow: true),
            new(2, "code.exe", @"C:\Program Files\Microsoft VS Code\Code.exe", HasWindow: true),
        };
        var service = new ProcessMonitorService(() => records);
        var vm = NewViewModel(service);

        await vm.RefreshAsync();
        var chrome = vm.Items.First(i => i.Path.Equals(chromePath, StringComparison.OrdinalIgnoreCase));
        vm.SetAction(chrome, ProcessAction.Direct);

        // Act: a second scan re-emits the same processes.
        await vm.RefreshAsync();

        var refreshed = vm.Items.First(i => i.Path.Equals(chromePath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProcessAction.Direct, refreshed.Action);
    }

    [Fact]
    public async Task RefreshAsync_EmptyResult_DrivesEmptyState()
    {
        var vm = NewViewModel(NewService(Array.Empty<ProcessMonitorService.ProcessRecord>()));

        await vm.RefreshAsync();

        Assert.Empty(vm.Items);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public void BuildProcessRules_IncludesOnlyExplicitNonProxyRules_DedupedSortedAndStable()
    {
        var vm = NewViewModel(NewService(Array.Empty<ProcessMonitorService.ProcessRecord>()));
        vm.Items.Add(new ProcessInfoItem { ProcessName = "chrome.exe", Action = ProcessAction.Direct });
        vm.Items.Add(new ProcessInfoItem { ProcessName = "blocked.exe", Action = ProcessAction.Block });
        vm.Items.Add(new ProcessInfoItem { ProcessName = "proxy.exe", Action = ProcessAction.Proxy });
        vm.Items.Add(new ProcessInfoItem { ProcessName = "", Action = ProcessAction.Block });
        vm.Items.Add(new ProcessInfoItem { ProcessName = "blocked.exe", Action = ProcessAction.Direct });

        var rules = vm.BuildProcessRules();

        Assert.Equal(2, rules.Count);
        Assert.Equal("blocked.exe", rules[0].ProcessName);
        Assert.Equal(ProcessAction.Direct, rules[0].Action);
        Assert.Equal("chrome.exe", rules[1].ProcessName);
        Assert.Equal(ProcessAction.Direct, rules[1].Action);
    }

    [Fact]
    public void SetAction_UpdatesItemPersistsRulesAndRaisesRulesChanged()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(NewService(Array.Empty<ProcessMonitorService.ProcessRecord>()), settings);
        var item = new ProcessInfoItem { ProcessName = "chrome.exe" };
        vm.Items.Add(item);

        var raised = 0;
        vm.RulesChanged += (_, _) => raised++;

        vm.SetAction(item, ProcessAction.Block);

        Assert.Equal(ProcessAction.Block, item.Action);
        Assert.Equal(1, raised);

        var rule = Assert.Single(settings.ProcessRules);
        Assert.Equal("chrome.exe", rule.ProcessName);
        Assert.Equal(ProcessAction.Block, rule.Action);
    }

    [Fact]
    public async Task RefreshAsync_PreCancelledToken_LeavesItemsUntouchedAndPropagatesCancellation()
    {
        var service = NewService(new[]
        {
            new ProcessMonitorService.ProcessRecord(1, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", HasWindow: true),
        });
        var vm = NewViewModel(service);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // ThrowsAnyAsync: a pre-cancelled Task.Run surfaces TaskCanceledException,
        // which derives from OperationCanceledException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.RefreshAsync(cts.Token));

        Assert.Empty(vm.Items);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public async Task RefreshAsync_AppliesPersistedRulesFromSettings_ToFreshlyMaterializedItems()
    {
        // Persisted state: a Direct rule for QQ.exe saved by a previous session.
        var settings = new AppSettings();
        settings.ProcessRules.Add(new ProcessRule { ProcessName = "QQ.exe", Action = ProcessAction.Direct });

        var service = NewService(new[]
        {
            new ProcessMonitorService.ProcessRecord(1, "QQ.exe", @"C:\Program Files\Tencent\QQ\QQ.exe", HasWindow: true),
        });
        var vm = NewViewModel(service, settings);

        // Act: first refresh materializes the item for the first time this session.
        await vm.RefreshAsync();

        // The freshly created item must inherit the persisted routing action, so
        // the UI matches reality (and the next BuildProcessRules round-trips it).
        var qq = Assert.Single(vm.Items);
        Assert.Equal(ProcessAction.Direct, qq.Action);
    }

    [Fact]
    public async Task RefreshAsync_ItemWithoutPersistedRule_StaysProxy()
    {
        var settings = new AppSettings();
        settings.ProcessRules.Add(new ProcessRule { ProcessName = "QQ.exe", Action = ProcessAction.Direct });

        var service = NewService(new[]
        {
            new ProcessMonitorService.ProcessRecord(1, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", HasWindow: true),
        });
        var vm = NewViewModel(service, settings);

        await vm.RefreshAsync();

        var chrome = Assert.Single(vm.Items);
        Assert.Equal(ProcessAction.Proxy, chrome.Action);
    }

    [Fact]
    public async Task RefreshAsync_PersistedRuleMatch_IsCaseInsensitive()
    {
        // The persisted rule uses a different casing than the live process name.
        var settings = new AppSettings();
        settings.ProcessRules.Add(new ProcessRule { ProcessName = "qq.exe", Action = ProcessAction.Direct });

        var service = NewService(new[]
        {
            new ProcessMonitorService.ProcessRecord(1, "QQ.exe", @"C:\Program Files\Tencent\QQ\QQ.exe", HasWindow: true),
        });
        var vm = NewViewModel(service, settings);

        await vm.RefreshAsync();

        var qq = Assert.Single(vm.Items);
        Assert.Equal(ProcessAction.Direct, qq.Action);
    }

    [Fact]
    public async Task RefreshAsync_UserRevertsToProxy_AndRefreshDoesNotReapplyRule()
    {
        // Seed a persisted Direct rule, then simulate the user reverting QQ to
        // Proxy (SetAction rebuilds ProcessRules excluding the Proxy item).
        var settings = new AppSettings();
        settings.ProcessRules.Add(new ProcessRule { ProcessName = "QQ.exe", Action = ProcessAction.Direct });

        var service = NewService(new[]
        {
            new ProcessMonitorService.ProcessRecord(1, "QQ.exe", @"C:\Program Files\Tencent\QQ\QQ.exe", HasWindow: true),
        });
        var vm = NewViewModel(service, settings);

        await vm.RefreshAsync();
        var qq = Assert.Single(vm.Items);
        Assert.Equal(ProcessAction.Direct, qq.Action);

        // User explicitly reverts to Proxy: SetAction drops the rule entirely.
        vm.SetAction(qq, ProcessAction.Proxy);
        Assert.Empty(settings.ProcessRules);

        // Act: a later refresh must not resurrect the deleted rule.
        await vm.RefreshAsync();

        Assert.Equal(ProcessAction.Proxy, qq.Action);
    }

    private static ProcessMonitorService NewService(IEnumerable<ProcessMonitorService.ProcessRecord> records) =>
        new(() => records);

    private static ProcessListViewModel NewViewModel(ProcessMonitorService monitor, AppSettings? settings = null) =>
        new(monitor, settings ?? new AppSettings(), static action => action());
}
