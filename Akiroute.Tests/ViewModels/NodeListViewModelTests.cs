using Akiroute.Models;
using Akiroute.Services;
using Akiroute.ViewModels;

namespace Akiroute.Tests.ViewModels;

/// <summary>
/// NodeListViewModel tests. The view model is constructed with the injectable
/// thread-dispatch seam (inline execution) and a real <see cref="PingService"/>
/// backed by a stub <see cref="IProxyTester"/>, so no real network probing and no
/// WinUI runtime are ever involved.
/// </summary>
public class NodeListViewModelTests
{
    private const string SsLink = "ss://YWVzLTI1Ni1nY206dGVzdHBhc3MxMjM=@example.com:8388#Tokyo%20SS";

    [Fact]
    public async Task PingAllAsync_SetsAndClearsIsPinging()
    {
        var vm = NewViewModel(new AppSettings());
        var states = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NodeListViewModel.IsPinging))
            {
                states.Add(vm.IsPinging);
            }
        };

        await vm.PingAllAsync();

        // The flag must have been toggled on and off across the (no-op) sweep.
        Assert.Contains(true, states);
        Assert.False(vm.IsPinging);
    }

    [Fact]
    public void SearchText_FiltersNodesCaseInsensitivelyAndRestoresOnClear()
    {
        var vm = NewViewModel(new AppSettings());
        vm.AddNode(new ProxyNode { Name = "Tokyo Fast", Address = "1.2.3.4", Port = 443, Type = "ss" });
        vm.AddNode(new ProxyNode { Name = "LA Slow", Address = "5.6.7.8", Port = 443, Type = "ss" });
        vm.AddNode(new ProxyNode { Name = "HK Stable", Address = "9.9.9.9", Port = 8443, Type = "trojan" });

        vm.SearchText = "tokyo";

        var tokyo = Assert.Single(vm.Nodes);
        Assert.Equal("Tokyo Fast", tokyo.Name);

        // Address is searched too.
        vm.SearchText = "5.6.7.8";
        Assert.Equal("LA Slow", Assert.Single(vm.Nodes).Name);

        vm.SearchText = "";

        Assert.Equal(3, vm.Nodes.Count);
    }

    [Fact]
    public void SelectNode_SetsSelectionPersistsIdAndClears()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);
        var node = new ProxyNode { Name = "A", Address = "1.1.1.1", Port = 443, Type = "ss" };
        vm.AddNode(node);

        vm.SelectNode(node);

        Assert.Same(node, vm.SelectedNode);
        Assert.True(node.IsSelected);
        Assert.Equal(node.Id, settings.SelectedNodeId);

        vm.SelectNode(null);

        Assert.Null(vm.SelectedNode);
        Assert.False(node.IsSelected);
        Assert.Equal("", settings.SelectedNodeId);
    }

    [Fact]
    public async Task ImportSubscriptionAsync_UnreachableUrl_SetsImportErrorAndRaisesNothing()
    {
        var vm = NewViewModel(new AppSettings());
        var raised = 0;
        vm.NodesImported += (_, _) => raised++;

        // Port 9 (discard) on loopback refuses connections immediately — no internet.
        await vm.ImportSubscriptionAsync("http://127.0.0.1:9/sub");

        Assert.False(string.IsNullOrEmpty(vm.ImportError));
        Assert.Equal(0, raised);
        Assert.Empty(vm.Nodes);
    }

    [Fact]
    public async Task ImportSubscriptionAsync_InvalidUrl_SetsImportError()
    {
        var vm = NewViewModel(new AppSettings());

        await vm.ImportSubscriptionAsync("not-a-url");

        Assert.False(string.IsNullOrEmpty(vm.ImportError));
        Assert.Empty(vm.Nodes);
    }

    [Fact]
    public async Task ImportAsync_ValidLink_AddsNodeAndRaisesNodesImported()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);
        var raised = 0;
        vm.NodesImported += (_, _) => raised++;

        var result = await vm.ImportAsync(SsLink);

        Assert.NotNull(result);
        Assert.Equal(1, raised);
        var node = Assert.Single(vm.Nodes);
        Assert.Equal("ss", node.Type);
        Assert.Equal("example.com", node.Address);
        Assert.Equal(8388, node.Port);
    }

    [Fact]
    public async Task ImportAsync_ClashYaml_AddsNodes()
    {
        var vm = NewViewModel(new AppSettings());
        var yaml = """
            proxies:
              - name: "SS-1"
                type: ss
                server: 203.0.113.1
                port: 8388
                cipher: aes-256-gcm
                password: "ss-password"
            """;

        var result = await vm.ImportAsync(yaml);

        Assert.NotNull(result);
        var node = Assert.Single(vm.Nodes);
        Assert.Equal("SS-1", node.Name);
        Assert.Equal("ss", node.Type);
    }

    [Fact]
    public async Task ImportAsync_GarbageText_AddsNothing()
    {
        var vm = NewViewModel(new AppSettings());
        var raised = 0;
        vm.NodesImported += (_, _) => raised++;

        var result = await vm.ImportAsync("this is not a proxy link or yaml");

        Assert.Null(result);
        Assert.Empty(vm.Nodes);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ImportAsync_DuplicateNode_IsNotAddedTwice()
    {
        var vm = NewViewModel(new AppSettings());
        await vm.ImportAsync(SsLink);

        var raised = 0;
        vm.NodesImported += (_, _) => raised++;
        await vm.ImportAsync(SsLink);

        Assert.Single(vm.Nodes);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ImportAsync_AddsNodeToSettingsNodes_ForPersistence()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);

        await vm.ImportAsync(SsLink);

        // NodesImported -> App layer SaveSettings persists AppSettings.Nodes, so
        // the imported node must be visible there, not just in the runtime repo.
        var node = Assert.Single(settings.Nodes);
        Assert.Equal("ss", node.Type);
        Assert.Equal("example.com", node.Address);
        Assert.Equal(8388, node.Port);
    }

    [Fact]
    public async Task ImportAsync_ClashYaml_SyncsNodesToSettings()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);
        var yaml = """
            proxies:
              - name: "SS-1"
                type: ss
                server: clash.example.com
                port: 443
                cipher: aes-256-gcm
                password: secret
            """;

        await vm.ImportAsync(yaml);

        Assert.Single(settings.Nodes);
    }

    [Fact]
    public void DeleteNode_SelectedMiddleNode_SelectsAdjacent()
    {
        // Arrange: seed nodes A, B, C; select the middle node B.
        var settings = new AppSettings();
        var vm = NewViewModel(settings);
        var nodeA = new ProxyNode { Name = "A", Address = "1.1.1.1", Port = 443, Type = "ss" };
        var nodeB = new ProxyNode { Name = "B", Address = "2.2.2.2", Port = 443, Type = "ss" };
        var nodeC = new ProxyNode { Name = "C", Address = "3.3.3.3", Port = 443, Type = "ss" };
        vm.AddNode(nodeA);
        vm.AddNode(nodeB);
        vm.AddNode(nodeC);
        vm.SelectNode(nodeB);

        // Act: delete the selected middle node B.
        vm.DeleteNodeCommand!.Execute(nodeB);

        // Assert: selection moves to the adjacent same-index node (C at index 1).
        Assert.Same(nodeC, vm.SelectedNode);
        Assert.True(nodeC.IsSelected);
        Assert.Equal(nodeC.Id, settings.SelectedNodeId);
        Assert.DoesNotContain(nodeB, settings.Nodes);
    }

    [Fact]
    public void DeleteNode_SelectedLastNode_SelectsPrevious()
    {
        // Arrange: seed nodes A, B, C; select the last node C.
        var settings = new AppSettings();
        var vm = NewViewModel(settings);
        var nodeA = new ProxyNode { Name = "A", Address = "1.1.1.1", Port = 443, Type = "ss" };
        var nodeB = new ProxyNode { Name = "B", Address = "2.2.2.2", Port = 443, Type = "ss" };
        var nodeC = new ProxyNode { Name = "C", Address = "3.3.3.3", Port = 443, Type = "ss" };
        vm.AddNode(nodeA);
        vm.AddNode(nodeB);
        vm.AddNode(nodeC);
        vm.SelectNode(nodeC);

        // Act: delete the selected last node C.
        vm.DeleteNodeCommand!.Execute(nodeC);

        // Assert: selection falls to the previous node B (Math.Clamp(2, 0, 1) = 1).
        Assert.Same(nodeB, vm.SelectedNode);
        Assert.True(nodeB.IsSelected);
        Assert.Equal(nodeB.Id, settings.SelectedNodeId);
        Assert.DoesNotContain(nodeC, settings.Nodes);
    }

    [Fact]
    public void DeleteNode_LastRemainingNode_ClearsSelection()
    {
        // Arrange: seed a single node A and select it.
        var settings = new AppSettings();
        var vm = NewViewModel(settings);
        var nodeA = new ProxyNode { Name = "A", Address = "1.1.1.1", Port = 443, Type = "ss" };
        vm.AddNode(nodeA);
        vm.SelectNode(nodeA);

        // Act: delete the only remaining node.
        vm.DeleteNodeCommand!.Execute(nodeA);

        // Assert: selection is cleared and settings updated.
        Assert.Null(vm.SelectedNode);
        Assert.Equal("", settings.SelectedNodeId);
        Assert.DoesNotContain(nodeA, settings.Nodes);
    }

    [Fact]
    public void DeleteNode_NonSelectedNode_KeepsSelection()
    {
        // Arrange: seed nodes A, B, C; select B.
        var settings = new AppSettings();
        var vm = NewViewModel(settings);
        var nodeA = new ProxyNode { Name = "A", Address = "1.1.1.1", Port = 443, Type = "ss" };
        var nodeB = new ProxyNode { Name = "B", Address = "2.2.2.2", Port = 443, Type = "ss" };
        var nodeC = new ProxyNode { Name = "C", Address = "3.3.3.3", Port = 443, Type = "ss" };
        vm.AddNode(nodeA);
        vm.AddNode(nodeB);
        vm.AddNode(nodeC);
        vm.SelectNode(nodeB);

        // Act: delete the non-selected node C.
        vm.DeleteNodeCommand!.Execute(nodeC);

        // Assert: selection remains on B.
        Assert.Same(nodeB, vm.SelectedNode);
        Assert.True(nodeB.IsSelected);
        Assert.Equal(nodeB.Id, settings.SelectedNodeId);
        Assert.DoesNotContain(nodeC, settings.Nodes);
    }

    private static NodeListViewModel NewViewModel(AppSettings settings)
    {
        var ping = new PingService(new StubTester());
        return new NodeListViewModel(settings, ping, static action => action());
    }

    /// <summary>Stub latency tester: reports a fixed latency, never touches the network.</summary>
    private sealed class StubTester : IProxyTester
    {
        public Task<long?> MeasureAsync(ProxyNode node, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(42);
    }

    /// <summary>Stub latency tester that counts probe invocations.</summary>
    private sealed class CountingTester : IProxyTester
    {
        public int ProbeCount { get; private set; }

        public Task<long?> MeasureAsync(ProxyNode node, CancellationToken cancellationToken)
        {
            ProbeCount++;
            return Task.FromResult<long?>(42);
        }
    }

    // ── Subscription test constants ────────────────────────────────

    private const string SsUserInfo = "YWVzLTI1Ni1nY206dGVzdHBhc3MxMjM=";

    /// <summary>Node S1: ss|example.com|8388</summary>
    private const string S1Link =
        $"ss://{SsUserInfo}@example.com:8388#S1";

    /// <summary>Node S2: ss|other.com|443</summary>
    private const string S2Link =
        $"ss://{SsUserInfo}@other.com:443#S2";

    /// <summary>Node S3: ss|third.com|443</summary>
    private const string S3Link =
        $"ss://{SsUserInfo}@third.com:443#S3";

    // ── ShouldRunUpdate ────────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]   // interval <= 0 → disabled
    [InlineData(-1, false)]
    public void ShouldRunUpdate_IntervalNonPositive_AlwaysFalse(int interval, bool expected)
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, NodeListViewModel.ShouldRunUpdate(now, null, interval));
        Assert.Equal(expected, NodeListViewModel.ShouldRunUpdate(now, now.AddMinutes(-10), interval));
    }

    [Fact]
    public void ShouldRunUpdate_NullLastUpdated_ReturnsTrue()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.True(NodeListViewModel.ShouldRunUpdate(now, null, 30));
    }

    [Fact]
    public void ShouldRunUpdate_ElapsedLessThanInterval_ReturnsFalse()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var lastUpdated = now.AddMinutes(-20);

        Assert.False(NodeListViewModel.ShouldRunUpdate(now, lastUpdated, 30));
    }

    [Fact]
    public void ShouldRunUpdate_ElapsedEqualToInterval_ReturnsTrue()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var lastUpdated = now.AddMinutes(-30);

        Assert.True(NodeListViewModel.ShouldRunUpdate(now, lastUpdated, 30));
    }

    [Fact]
    public void ShouldRunUpdate_ElapsedGreaterThanInterval_ReturnsTrue()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var lastUpdated = now.AddMinutes(-60);

        Assert.True(NodeListViewModel.ShouldRunUpdate(now, lastUpdated, 30));
    }

    // ── ShouldRunPing ─────────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]   // interval <= 0 → disabled
    [InlineData(-1, false)]
    public void ShouldRunPing_IntervalNonPositive_AlwaysFalse(int interval, bool expected)
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, NodeListViewModel.ShouldRunPing(now, null, interval));
        Assert.Equal(expected, NodeListViewModel.ShouldRunPing(now, now.AddMinutes(-10), interval));
    }

    [Fact]
    public void ShouldRunPing_NullLastPing_ReturnsTrue()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.True(NodeListViewModel.ShouldRunPing(now, null, 30));
    }

    [Fact]
    public void ShouldRunPing_ElapsedLessThanInterval_ReturnsFalse()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var lastPingAt = now.AddMinutes(-20);

        Assert.False(NodeListViewModel.ShouldRunPing(now, lastPingAt, 30));
    }

    [Fact]
    public void ShouldRunPing_ElapsedEqualToInterval_ReturnsTrue()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var lastPingAt = now.AddMinutes(-30);

        Assert.True(NodeListViewModel.ShouldRunPing(now, lastPingAt, 30));
    }

    [Fact]
    public void ShouldRunPing_ElapsedGreaterThanInterval_ReturnsTrue()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var lastPingAt = now.AddMinutes(-60);

        Assert.True(NodeListViewModel.ShouldRunPing(now, lastPingAt, 30));
    }

    // ── RunAutoPingIfDue ──────────────────────────────────────────

    [Fact]
    public async Task RunAutoPingIfDue_Disabled_DoesNotProbe()
    {
        var settings = new AppSettings { AutoPingMinutes = 0 };
        var tester = new CountingTester();
        var ping = new PingService(tester);
        var vm = new NodeListViewModel(settings, ping, static action => action());
        vm.AddNode(new ProxyNode { Name = "A", Address = "1.1.1.1", Port = 443, Type = "ss" });

        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await vm.RunAutoPingIfDueAsync(now);

        Assert.Equal(0, tester.ProbeCount);
    }

    [Fact]
    public async Task RunAutoPingIfDue_FirstRun_ProbesOnce_AndSecondImmediateCallSkips()
    {
        var settings = new AppSettings { AutoPingMinutes = 30 };
        var tester = new CountingTester();
        var ping = new PingService(tester);
        var vm = new NodeListViewModel(settings, ping, static action => action());
        vm.AddNode(new ProxyNode { Name = "A", Address = "1.1.1.1", Port = 443, Type = "ss" });

        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await vm.RunAutoPingIfDueAsync(now);

        // First call: 1 node → 1 probe.
        Assert.Equal(1, tester.ProbeCount);

        // Second call with same now (within interval) → should skip.
        await vm.RunAutoPingIfDueAsync(now);

        Assert.Equal(1, tester.ProbeCount);
    }

    // ── ImportSubscriptionAsync — upsert & tag ──────────────────────

    [Fact]
    public async Task ImportSubscriptionAsync_TagsNodesAndUpsertsEntry()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);

        // Fetch returns two SS links.
        var body = $"{S1Link}\n{S2Link}";
        vm.SubscriptionFetchOverrideForTests = (_, _) => Task.FromResult(body);

        await vm.ImportSubscriptionAsync("https://feed.example.com/sub");

        // One entry created for this URL.
        var entry = Assert.Single(settings.Subscriptions);
        Assert.Equal("https://feed.example.com/sub", entry.Url);
        Assert.NotNull(entry.LastUpdated);

        // Both imported nodes are tagged with the entry id.
        Assert.Equal(2, settings.Nodes.Count);
        Assert.All(settings.Nodes, n => Assert.Equal(entry.Id, n.SourceSubscriptionId));
    }

    [Fact]
    public async Task ImportSubscriptionAsync_SameUrlTwice_SingleEntry()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);

        vm.SubscriptionFetchOverrideForTests = (_, _) => Task.FromResult($"{S1Link}\n{S2Link}");
        await vm.ImportSubscriptionAsync("https://feed.example.com/sub");

        // Second import with a different payload but the same URL.
        // S1 is a dupe (same key), S3 is new.
        vm.SubscriptionFetchOverrideForTests = (_, _) => Task.FromResult($"{S1Link}\n{S3Link}");
        await vm.ImportSubscriptionAsync("https://feed.example.com/sub");

        // Still a single entry; LastUpdated refreshed.
        var entry = Assert.Single(settings.Subscriptions);
        Assert.NotNull(entry.LastUpdated);

        // S1 + S2 (from first import) + S3 (new) = 3 nodes total.
        Assert.Equal(3, settings.Nodes.Count);
    }

    // ── UpdateSubscriptionAsync ─────────────────────────────────────

    [Fact]
    public async Task UpdateSubscriptionAsync_RemovesStale_AddsFresh_PreservesManual()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);

        // Step 1: import S1 + S2 via subscription URL.
        vm.SubscriptionFetchOverrideForTests = (_, _) => Task.FromResult($"{S1Link}\n{S2Link}");
        await vm.ImportSubscriptionAsync("https://feed.example.com/sub");
        var entry = Assert.Single(settings.Subscriptions);

        // Step 2: add a manual node M (no SourceSubscriptionId).
        var manualNode = new ProxyNode
        {
            Name = "Manual",
            Address = "10.0.0.1",
            Port = 9999,
            Type = "ss",
        };
        vm.AddNode(manualNode);
        Assert.Equal(3, settings.Nodes.Count);

        // Step 3: update subscription — returns S1' (same key as S1) + S3 (new).
        // S2 becomes stale and should be removed.
        // S1' has same Type|Address|Port as S1 but a different Name.
        var s1PrimeLink = $"ss://{SsUserInfo}@example.com:8388#S1prime";
        vm.SubscriptionFetchOverrideForTests = (_, _) => Task.FromResult($"{s1PrimeLink}\n{S3Link}");
        var result = await vm.UpdateSubscriptionAsync(entry);

        // Manual node M is preserved; S2 removed; S1 retained (same key);
        // S3 added. Total = M + S1 + S3 = 3.
        Assert.Equal(3, settings.Nodes.Count);
        Assert.Contains(settings.Nodes, n => ReferenceEquals(n, manualNode));
        Assert.DoesNotContain(settings.Nodes, n => n.SourceSubscriptionId == entry.Id
                                                     && n.Address == "other.com");
        Assert.Contains(settings.Nodes, n => n.Address == "third.com" && n.SourceSubscriptionId == entry.Id);

        // Verify the summary counts.
        Assert.Contains("新增 1", result);
        Assert.Contains("移除 1", result);
    }

    [Fact]
    public async Task UpdateSubscriptionAsync_RemovedSelectedNode_ClearsSelection()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);

        // Import S1 + S2.
        vm.SubscriptionFetchOverrideForTests = (_, _) => Task.FromResult($"{S1Link}\n{S2Link}");
        await vm.ImportSubscriptionAsync("https://feed.example.com/sub");
        var entry = Assert.Single(settings.Subscriptions);

        // Select S2 (the node that will become stale).
        var s2Node = settings.Nodes.First(n => n.Address == "other.com");
        vm.SelectNode(s2Node);
        Assert.Same(s2Node, vm.SelectedNode);

        // Update: only S1 survives, S2 is stale.
        vm.SubscriptionFetchOverrideForTests = (_, _) => Task.FromResult(S1Link);
        await vm.UpdateSubscriptionAsync(entry);

        // Selection cleared because the selected node was removed.
        Assert.Null(vm.SelectedNode);
        Assert.Equal("", settings.SelectedNodeId);
    }

    [Fact]
    public async Task UpdateSubscriptionAsync_FetchFailure_NoMutation()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);

        // Import S1 + S2 first.
        vm.SubscriptionFetchOverrideForTests = (_, _) => Task.FromResult($"{S1Link}\n{S2Link}");
        await vm.ImportSubscriptionAsync("https://feed.example.com/sub");
        var entry = Assert.Single(settings.Subscriptions);

        // Pre-set LastUpdated to a known value.
        var preUpdate = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero);
        entry.LastUpdated = preUpdate;
        var preCount = settings.Nodes.Count;

        // Now override fetch to throw.
        vm.SubscriptionFetchOverrideForTests = (_, _) =>
            throw new HttpRequestException("connection refused");

        var result = await vm.UpdateSubscriptionAsync(entry);

        // Nodes unchanged.
        Assert.Equal(preCount, settings.Nodes.Count);
        // LastUpdated unchanged.
        Assert.Equal(preUpdate, entry.LastUpdated);
        // Result string indicates failure.
        Assert.Contains("订阅获取失败", result);
    }
}
