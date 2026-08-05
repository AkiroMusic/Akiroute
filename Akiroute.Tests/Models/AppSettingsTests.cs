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
        Assert.Equal(AppTheme.System, settings.Theme);
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
