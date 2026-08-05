using Akiroute.Helpers;

namespace Akiroute.Tests.Models;

/// <summary>
/// AppPaths path-layout tests. Pure file-system logic, no UI required.
/// </summary>
public class AppPathsTests
{
    [Fact]
    public void ConfigFile_IsUnderLocalAppData_Akiroute()
    {
        // Assert: config must live under LocalApplicationData\Akiroute so we never touch system dirs.
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Akiroute");
        Assert.StartsWith(root, AppPaths.ConfigFile, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(root, AppPaths.LogsDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureDirectories_CreatesConfigAndLogs()
    {
        // Act: create directories (idempotent).
        AppPaths.EnsureDirectories();

        // Assert: both exist.
        Assert.True(Directory.Exists(AppPaths.ConfigDir));
        Assert.True(Directory.Exists(AppPaths.LogsDir));
    }

    [Fact]
    public void XrayConfigTemp_IsUnderTemp()
    {
        // Assert: generated xray config lives in the temp area, not the app dir.
        Assert.StartsWith(Path.GetTempPath(), AppPaths.XrayConfigTemp, StringComparison.OrdinalIgnoreCase);
    }
}
