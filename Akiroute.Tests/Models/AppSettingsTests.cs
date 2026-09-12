using Akiroute.Models;

namespace Akiroute.Tests.Models;

/// <summary>
/// AppSettings POCO + ProxyMode/AppTheme enum tests.
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void NewSettings_HasProductionDefaults()
    {
        // Act: create fresh settings.
        var settings = new AppSettings();

        // Assert: every documented default is in place (later phases read these).
        Assert.Equal("", settings.SelectedNodeId);
        Assert.Equal(ProxyMode.Rule, settings.Mode);
        Assert.Equal(3333, settings.Port);
        Assert.False(settings.AutoConnect);
        Assert.Equal(0, settings.SubscriptionAutoUpdateMinutes);
        Assert.False(settings.TunEnabled);
        Assert.False(settings.StartMinimized);
        Assert.False(settings.LaunchOnStartup);
        Assert.Equal(AppTheme.System, settings.Theme);
    }

    [Fact]
    public void CopyFrom_MutatesInPlace_PreservingCollectionInstances()
    {
        // Arrange: a live settings instance with collections that view models
        // hold references to, and a restore snapshot with different content.
        var live = new AppSettings { Port = 3333, Mode = ProxyMode.Rule };
        var liveNodes = live.Nodes;
        var liveRules = live.ProcessRules;
        var liveSubs = live.Subscriptions;
        live.Nodes.Add(new ProxyNode { Id = "old", Name = "old", Address = "a", Port = 1, Type = "ss" });

        var snapshot = new AppSettings
        {
            Port = 5555,
            Mode = ProxyMode.Global,
            AutoConnect = true,
            StartMinimized = true,
            LaunchOnStartup = true,
            SubscriptionAutoUpdateMinutes = 30,
            AutoPingMinutes = 15,
            Theme = AppTheme.Dark,
            SelectedNodeId = "new",
        };
        snapshot.Nodes.Add(new ProxyNode { Id = "new", Name = "new", Address = "b", Port = 2, Type = "ss" });
        snapshot.ProcessRules.Add(new ProcessRule { ProcessName = "chrome.exe", Action = ProcessAction.Direct });

        // Act.
        live.CopyFrom(snapshot);

        // Assert: values replaced, SAME collection instances mutated in place.
        Assert.Equal(5555, live.Port);
        Assert.Equal(ProxyMode.Global, live.Mode);
        Assert.True(live.AutoConnect);
        Assert.True(live.StartMinimized);
        Assert.True(live.LaunchOnStartup);
        Assert.Equal(30, live.SubscriptionAutoUpdateMinutes);
        Assert.Equal(15, live.AutoPingMinutes);
        Assert.Equal(AppTheme.Dark, live.Theme);
        Assert.Equal("new", live.SelectedNodeId);

        Assert.Same(liveNodes, live.Nodes);
        Assert.Same(liveRules, live.ProcessRules);
        Assert.Same(liveSubs, live.Subscriptions);
        Assert.Single(live.Nodes);
        Assert.Equal("new", live.Nodes[0].Id);
        Assert.Single(live.ProcessRules);
    }

    [Fact]
    public void NewSettings_Collections_AreEmptyInstances()
    {
        // Act: create fresh settings.
        var settings = new AppSettings();

        // Assert: collections are non-null, empty, and usable.
        Assert.NotNull(settings.Nodes);
        Assert.NotNull(settings.ProcessRules);
        Assert.NotNull(settings.Subscriptions);
        Assert.Empty(settings.Nodes);
        Assert.Empty(settings.ProcessRules);
        Assert.Empty(settings.Subscriptions);
    }

    [Fact]
    public void Settings_Properties_AreSettable()
    {
        // Arrange: a fully configured settings document.
        var settings = new AppSettings
        {
            SelectedNodeId = "node-1",
            Mode = ProxyMode.ProcessOnly,
            Port = 8080,
            AutoConnect = true,
            SubscriptionAutoUpdateMinutes = 60,
            TunEnabled = true,
            Theme = AppTheme.Dark,
        };

        // Assert: every value round-trips through the POCO surface.
        Assert.Equal("node-1", settings.SelectedNodeId);
        Assert.Equal(ProxyMode.ProcessOnly, settings.Mode);
        Assert.Equal(8080, settings.Port);
        Assert.True(settings.AutoConnect);
        Assert.Equal(60, settings.SubscriptionAutoUpdateMinutes);
        Assert.True(settings.TunEnabled);
        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public void ModeAndThemeEnums_AreDefined()
    {
        // Assert: all documented enum members exist.
        Assert.True(Enum.IsDefined(typeof(ProxyMode), ProxyMode.Global));
        Assert.True(Enum.IsDefined(typeof(ProxyMode), ProxyMode.Rule));
        Assert.True(Enum.IsDefined(typeof(ProxyMode), ProxyMode.DirectOnly));
        Assert.True(Enum.IsDefined(typeof(ProxyMode), ProxyMode.ProcessOnly));
        Assert.True(Enum.IsDefined(typeof(AppTheme), AppTheme.System));
        Assert.True(Enum.IsDefined(typeof(AppTheme), AppTheme.Dark));
        Assert.True(Enum.IsDefined(typeof(AppTheme), AppTheme.Light));
    }
}
