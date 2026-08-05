using Akiroute.Models;
using Akiroute.Services;
using Akiroute.ViewModels;

namespace Akiroute.Tests.ViewModels;

/// <summary>
/// SettingsViewModel tests. Save/Load round-trips run against a temp-file
/// <see cref="SettingsService"/> instance through the internal ConfigFilePath seam;
/// the in-memory sync tests exercise the observable property wrappers directly.
/// </summary>
public class SettingsViewModelTests
{
    [Fact]
    public void Constructor_ReflectsCurrentSettings()
    {
        var settings = new AppSettings
        {
            Mode = ProxyMode.Global,
            Port = 4444,
            AutoConnect = true,
            SubscriptionAutoUpdateMinutes = 30,
            TunEnabled = true,
            Theme = AppTheme.Dark,
        };

        var vm = NewViewModel(settings);

        Assert.Equal(ProxyMode.Global, vm.Mode);
        Assert.Equal(4444, vm.Port);
        Assert.True(vm.AutoConnect);
        Assert.Equal(30, vm.SubscriptionAutoUpdateMinutes);
        Assert.True(vm.TunEnabled);
        Assert.Equal(AppTheme.Dark, vm.Theme);
    }

    [Fact]
    public void SettingMode_UpdatesSettingsAndRaisesSettingsChanged()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);
        var raised = 0;
        vm.SettingsChanged += (_, _) => raised++;

        vm.Mode = ProxyMode.Global;

        Assert.Equal(ProxyMode.Global, settings.Mode);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SettingTheme_UpdatesSettingsAndRaisesSettingsChanged()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);
        var raised = 0;
        vm.SettingsChanged += (_, _) => raised++;

        vm.Theme = AppTheme.Light;

        Assert.Equal(AppTheme.Light, settings.Theme);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Port_ClampsOutOfRangeValuesKeepingLastValid()
    {
        var settings = new AppSettings { Port = 3333 };
        var vm = NewViewModel(settings);

        vm.Port = 80;

        Assert.Equal(3333, vm.Port);
        Assert.Equal(3333, settings.Port);

        vm.Port = 65536;

        Assert.Equal(3333, vm.Port);
        Assert.Equal(3333, settings.Port);

        vm.Port = 1080;

        Assert.Equal(1080, vm.Port);
        Assert.Equal(1080, settings.Port);
    }

    [Fact]
    public void SettingPort_SyncsToSettingsAndRaisesSettingsChanged()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);
        var raised = 0;
        vm.SettingsChanged += (_, _) => raised++;

        vm.Port = 7890;

        Assert.Equal(7890, settings.Port);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SettingAutoConnect_SyncsToSettingsAndRaisesSettingsChanged()
    {
        var settings = new AppSettings();
        var vm = NewViewModel(settings);
        var raised = 0;
        vm.SettingsChanged += (_, _) => raised++;

        vm.AutoConnect = true;

        Assert.True(settings.AutoConnect);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsThroughTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "akiroute-tests-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var settings = new AppSettings
            {
                Mode = ProxyMode.Global,
                Port = 4000,
                AutoConnect = true,
                Theme = AppTheme.Dark,
            };
            var vm = NewViewModel(settings);
            vm.ConfigFilePath = path;

            vm.Save();

            Assert.True(File.Exists(path));
            Assert.Null(vm.SaveError);

            var loaded = new AppSettings();
            var vm2 = NewViewModel(loaded);
            vm2.ConfigFilePath = path;
            vm2.Load();

            Assert.Equal(ProxyMode.Global, loaded.Mode);
            Assert.Equal(4000, loaded.Port);
            Assert.True(loaded.AutoConnect);
            Assert.Equal(AppTheme.Dark, loaded.Theme);

            // The observable wrappers were refreshed from disk.
            Assert.Equal(ProxyMode.Global, vm2.Mode);
            Assert.Equal(4000, vm2.Port);
            Assert.Equal(AppTheme.Dark, vm2.Theme);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_WhenTargetDirectoryUnwritable_SetsSaveErrorInsteadOfThrowing()
    {
        // A path whose parent is a file cannot host a settings directory, so the
        // save fails deterministically with an I/O error.
        var tempFile = Path.Combine(Path.GetTempPath(), "akiroute-tests-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
        try
        {
            File.WriteAllText(tempFile, "occupied");
            var settings = new AppSettings();
            var vm = NewViewModel(settings);
            vm.ConfigFilePath = Path.Combine(tempFile, "settings.json");

            vm.Save();

            Assert.False(string.IsNullOrEmpty(vm.SaveError));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static SettingsViewModel NewViewModel(AppSettings settings) =>
        new(settings, static action => action());
}
