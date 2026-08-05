using System.Diagnostics;
using Akiroute.Models;
using Akiroute.Services;

namespace Akiroute.Tests.Services;

/// <summary>
/// ProcessMonitorService tests. All tests are pure: they inject a fake enumerator
/// returning hand-built <see cref="ProcessMonitorService.ProcessRecord"/>s, so no
/// real windows, no WinUI runtime, and no System.Drawing calls are ever involved.
/// </summary>
public class ProcessMonitorServiceTests
{
    [Fact]
    public void GetCurrentProcesses_FiltersBlacklistedNamesAndWindowlessProcesses()
    {
        // Arrange: windowed svchost/explorer (blacklisted by name), a System32
        // svchost (blacklisted and a bare System component), plus notepad (a real
        // System32 app with a FileDescription) and chrome (a user app).
        var notepadPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var service = NewService(new[]
        {
            new ProcessMonitorService.ProcessRecord(1, "svchost.exe", @"C:\Windows\System32\svchost.exe", HasWindow: true),
            new ProcessMonitorService.ProcessRecord(2, "explorer.exe", @"C:\Windows\explorer.exe", HasWindow: true),
            new ProcessMonitorService.ProcessRecord(3, "notepad.exe", notepadPath, HasWindow: true),
            new ProcessMonitorService.ProcessRecord(4, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", HasWindow: true),
            new ProcessMonitorService.ProcessRecord(5, "svchost.exe", @"C:\Windows\System32\svchost.exe", HasWindow: false),
        });

        // Act.
        var items = service.GetCurrentProcesses();

        // Assert: blacklisted and windowless processes are absent; user apps are present.
        Assert.DoesNotContain(items, i => i.ProcessName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.ProcessName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(items, i => i.ProcessName.Equals("notepad.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(items, i => i.ProcessName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCurrentProcesses_FriendlyName_UsesFileDescriptionAndFallsBack()
    {
        // Arrange: a real system exe so FileVersionInfo yields a FileDescription,
        // plus a fabricated path that must fall back to the raw file name.
        var realPath = LocateRealSystemExe();
        Assert.True(File.Exists(realPath), "A real system executable is required for the friendly-name test.");
        var service = NewService(new[]
        {
            new ProcessMonitorService.ProcessRecord(1, Path.GetFileName(realPath), realPath, HasWindow: true),
            new ProcessMonitorService.ProcessRecord(2, "fake.exe", @"C:\nonexistent\fake.exe", HasWindow: true),
        });

        // Act.
        var items = service.GetCurrentProcesses();

        // Assert: the real exe carries its FileDescription (any UI language, e.g.
        // "Notepad" or a localized equivalent); the missing exe falls back to the
        // raw file name.
        var realItem = items.First(i => i.Path.Equals(realPath, StringComparison.OrdinalIgnoreCase));
        var expectedDescription = FileVersionInfo.GetVersionInfo(realPath).FileDescription;
        Assert.False(string.IsNullOrWhiteSpace(expectedDescription));
        Assert.Equal(expectedDescription, realItem.DisplayName);
        Assert.NotEqual(Path.GetFileName(realPath), realItem.DisplayName);

        var fakeItem = items.First(i => i.Path.EndsWith(@"fake.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("fake.exe", fakeItem.DisplayName);
    }

    [Fact]
    public void GetCurrentProcesses_PreservesActionAndTracksAppearanceAndDisappearance()
    {
        // Arrange: an enumerator backed by a mutable list so the second scan differs.
        const string chromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        const string vanishingPath = @"C:\Program Files\Vanishing\vanishing.exe";
        const string appearingPath = @"C:\Program Files\Appearing\appearing.exe";

        var records = new List<ProcessMonitorService.ProcessRecord>
        {
            new(1, "chrome.exe", chromePath, HasWindow: true),
            new(2, "vanishing.exe", vanishingPath, HasWindow: true),
        };
        var service = new ProcessMonitorService(() => records);

        // Act: first scan, then flip chrome to Direct on the returned snapshot.
        var first = service.GetCurrentProcesses();
        first.First(i => i.Path.Equals(chromePath, StringComparison.OrdinalIgnoreCase)).Action = ProcessAction.Direct;

        records.Clear();
        records.Add(new ProcessMonitorService.ProcessRecord(1, "chrome.exe", chromePath, HasWindow: true));
        records.Add(new ProcessMonitorService.ProcessRecord(3, "appearing.exe", appearingPath, HasWindow: true));
        var second = service.GetCurrentProcesses();

        // Assert: chrome kept its Direct action; appearing is new; vanishing is gone.
        Assert.Equal(
            ProcessAction.Direct,
            second.First(i => i.Path.Equals(chromePath, StringComparison.OrdinalIgnoreCase)).Action);
        Assert.Contains(second, i => i.Path.Equals(appearingPath, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(second, i => i.Path.Equals(vanishingPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCurrentProcesses_MarksWindowsPathsAsSystem()
    {
        // Arrange: a real System32 exe (system) and a fabricated Program Files path (user).
        var notepadPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(File.Exists(notepadPath), "System32\notepad.exe must exist for the IsSystem test.");
        var service = NewService(new[]
        {
            new ProcessMonitorService.ProcessRecord(1, "notepad.exe", notepadPath, HasWindow: true),
            new ProcessMonitorService.ProcessRecord(2, "fakeapp.exe", @"C:\Program Files\FakeApp\fakeapp.exe", HasWindow: true),
        });

        // Act.
        var items = service.GetCurrentProcesses();

        // Assert: IsSystem derives from the path location only.
        Assert.True(items.First(i => i.Path.Equals(notepadPath, StringComparison.OrdinalIgnoreCase)).IsSystem);
        Assert.False(items.First(i => i.Path.Contains("FakeApp", StringComparison.OrdinalIgnoreCase)).IsSystem);
    }

    [Fact]
    public void IconCache_GetCachedIconNullBeforeSeedingAndClearedByClearCache()
    {
        // Arrange: a service whose WinUI-bound extraction is never invoked.
        var service = NewService(Array.Empty<ProcessMonitorService.ProcessRecord>());
        const string path = @"C:\Program Files\Test\app.exe";

        // Before any extraction the cache is empty.
        Assert.Null(service.GetCachedIcon(path));

        // Seed through the internal test seam (real extraction is WinUI-bound).
        var probe = new object();
        service.SetCachedIcon(path, probe);
        Assert.Same(probe, service.GetCachedIcon(path));

        // ClearCache empties the cache.
        service.ClearCache();
        Assert.Null(service.GetCachedIcon(path));
    }

    [Fact]
    public void ClearCache_DisposesCachedIconObjects()
    {
        var service = NewService(Array.Empty<ProcessMonitorService.ProcessRecord>());
        var probe = new DisposableProbe();
        service.SetCachedIcon(@"C:\Program Files\Test\app.exe", probe);

        service.ClearCache();

        Assert.True(probe.Disposed);
    }

    [Fact]
    public void GetOrAddItemForPath_ReturnsTheSameInstanceForTheSamePath()
    {
        var service = NewService(Array.Empty<ProcessMonitorService.ProcessRecord>());
        const string path = @"C:\Program Files\App\app.exe";

        var first = service.GetOrAddItemForPath(path);
        first.Action = ProcessAction.Block;

        var second = service.GetOrAddItemForPath(path);

        Assert.Same(first, second);
        Assert.Equal(ProcessAction.Block, second.Action);
        Assert.Equal("app.exe", second.DisplayName);
    }

    [Fact]
    public void GetCurrentProcesses_ReusesItemsBoundThroughGetOrAddItemForPath()
    {
        // Arrange: an item bound ahead of the scan (Phase 4.2 style) with a chosen action.
        const string path = @"C:\Program Files\App\app.exe";
        var service = NewService(new[]
        {
            new ProcessMonitorService.ProcessRecord(1, "app.exe", path, HasWindow: true),
        });

        var preBound = service.GetOrAddItemForPath(path);
        preBound.Action = ProcessAction.Direct;

        // Act: the scan re-encounters the same executable.
        var scanned = service.GetCurrentProcesses().First(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

        // Assert: same instance, action preserved — no duplicate item is created.
        Assert.Same(preBound, scanned);
        Assert.Equal(ProcessAction.Direct, scanned.Action);
    }

    private static ProcessMonitorService NewService(IEnumerable<ProcessMonitorService.ProcessRecord> records) =>
        new(() => records);

    /// <summary>
    /// Locates a real system executable whose FileVersionInfo exposes a non-empty
    /// FileDescription and that survives the service's blacklist, so the friendly-name
    /// assertions work on any machine.
    /// </summary>
    private static string LocateRealSystemExe()
    {
        foreach (var candidate in new[] { "notepad.exe", "cmd.exe", "calc.exe", "regedit.exe", "mspaint.exe", "write.exe" })
        {
            var path = Path.Combine(Environment.SystemDirectory, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(Environment.SystemDirectory, "notepad.exe");
    }

    private sealed class DisposableProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
