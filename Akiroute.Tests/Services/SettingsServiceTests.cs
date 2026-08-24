using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using Akiroute.Models;
using Akiroute.Services;

namespace Akiroute.Tests.Services;

/// <summary>
/// SettingsService tests. Every test uses a per-test temp directory as the
/// config path — the real LocalApplicationData path is never touched.
/// </summary>
public class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "akiroute-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string ConfigPath => Path.Combine(_tempDir, "settings.json");

    private static string ToJson(AppSettings settings) =>
        JsonSerializer.Serialize(settings, AppJsonSerializerContext.Default.AppSettings);

    private static AppSettings SampleSettings() => new()
    {
        SelectedNodeId = "n1",
        Mode = ProxyMode.ProcessOnly,
        Port = 9090,
        AutoConnect = true,
        SubscriptionAutoUpdateMinutes = 30,
        TunEnabled = true,
        Theme = AppTheme.Dark,
        Nodes = { new ProxyNode { Id = "n1", Name = "SG-1", Type = "vless", Address = "203.0.113.10", Port = 443 } },
        ProcessRules = { new ProcessRule { ProcessName = "chrome.exe", Action = ProcessAction.Block } },
        Subscriptions = { new SubscriptionEntry { Id = "s1", Name = "Feed", Url = "https://example.com/sub" } },
    };

    [Fact]
    public void SaveThenLoad_RoundTripsSettings()
    {
        // Arrange: fully populated settings and a fresh temp config path.
        var settings = SampleSettings();

        // Act: save, then load from the same path.
        SettingsService.Save(settings, ConfigPath);
        var restored = SettingsService.Load(ConfigPath);

        // Assert: the loaded document is equivalent to what was saved.
        Assert.NotNull(restored);
        Assert.Equal(ToJson(settings), ToJson(restored));
        Assert.True(File.Exists(ConfigPath));
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaults()
    {
        // Act: load from a path that does not exist.
        var result = SettingsService.Load(ConfigPath);

        // Assert: fresh defaults, no exception, no file or backup created.
        Assert.NotNull(result);
        Assert.Equal(ToJson(new AppSettings()), ToJson(result));
        Assert.False(File.Exists(ConfigPath));
        Assert.Empty(Directory.Exists(_tempDir)
            ? Directory.GetFiles(_tempDir, "*.corrupt-*")
            : Array.Empty<string>());
    }

    [Fact]
    public void Save_LeavesNoTmpFileBehind()
    {
        // Act: save through the atomic-write path.
        SettingsService.Save(SampleSettings(), ConfigPath);

        // Assert: the temporary staging file is gone after a successful save.
        Assert.False(File.Exists(ConfigPath + ".tmp"));
    }

    [Fact]
    public void Load_WhenJsonCorrupt_BacksUpFileAndReturnsDefaults()
    {
        // Arrange: a config file containing garbage (not JSON).
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(ConfigPath, "{ not valid json {{{");

        // Act: load — must not throw.
        var result = SettingsService.Load(ConfigPath);

        // Assert: defaults are returned, the corrupt file is renamed to a
        // timestamped backup, and the original is gone.
        Assert.NotNull(result);
        Assert.Equal(ToJson(new AppSettings()), ToJson(result));
        Assert.False(File.Exists(ConfigPath));
        var backups = Directory.GetFiles(_tempDir, "settings.json.corrupt-*");
        Assert.Single(backups);
        Assert.StartsWith("settings.json.corrupt-", Path.GetFileName(backups[0]));
    }

    [Fact]
    public void Load_WhenFileEmpty_TreatedAsCorrupt()
    {
        // Arrange: an empty config file.
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(ConfigPath, "");

        // Act: load — must not throw.
        var result = SettingsService.Load(ConfigPath);

        // Assert: defaults are returned and the empty file was backed up.
        Assert.NotNull(result);
        Assert.Equal(ToJson(new AppSettings()), ToJson(result));
        Assert.False(File.Exists(ConfigPath));
        Assert.Single(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Load_WhenFileWhitespaceOnly_TreatedAsCorrupt()
    {
        // Arrange: a whitespace-only config file.
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(ConfigPath, " \r\n\t ");

        // Act: load — must not throw.
        var result = SettingsService.Load(ConfigPath);

        // Assert: defaults are returned and the file was backed up.
        Assert.NotNull(result);
        Assert.Equal(ToJson(new AppSettings()), ToJson(result));
        Assert.False(File.Exists(ConfigPath));
        Assert.Single(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Save_WhenTargetLocked_Throws_AfterRetry()
    {
        // Arrange: hold the target file open with exclusive sharing.
        Directory.CreateDirectory(_tempDir);
        using var lockStream = new FileStream(ConfigPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        // Act: Save must retry once (50 ms) and then rethrow rather than swallow.
        var exception = Record.Exception(() => SettingsService.Save(SampleSettings(), ConfigPath));

        // Assert: an IO-shaped failure surfaces (the exact runtime exception
        // type differs between .NET versions: IOException or UnauthorizedAccessException).
        Assert.NotNull(exception);
        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Unexpected exception type: {exception.GetType()}");
    }

    [Fact]
    public void Save_NullSettings_ThrowsArgumentNullException()
    {
        // Act + Assert: a null settings document is a programmer error.
        Assert.Throws<ArgumentNullException>(() => SettingsService.Save(null!, ConfigPath));
    }

    /// <summary>
    /// Concurrent stress test: 8 parallel tasks each perform 50 iterations of
    /// mutate-field → Save → Load → assert persisted. No exceptions should escape;
    /// the final Load must succeed and reflect a valid state (last-writer-wins
    /// since Save serializes via a static lock).
    /// </summary>
    [Fact]
    public void Save_Load_Concurrent_Stress()
    {
        const int taskCount = 8;
        const int iterationsPerTask = 50;
        var barrier = new System.Threading.Barrier(taskCount);

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, taskCount, new ParallelOptions { MaxDegreeOfParallelism = taskCount }, taskIndex =>
        {
            barrier.SignalAndWait();

            for (var i = 0; i < iterationsPerTask; i++)
            {
                try
                {
                    var settings = new AppSettings
                    {
                        Port = 1024 + (taskIndex * iterationsPerTask + i),
                        Mode = ProxyMode.Global,
                    };

                    SettingsService.Save(settings, ConfigPath);
                    var loaded = SettingsService.Load(ConfigPath);

                    Assert.NotNull(loaded);
                    Assert.True(loaded.Port >= 1024, $"Port should be >= 1024, got {loaded.Port}");
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        // Assert: no exceptions escaped during concurrent writes.
        Assert.Empty(exceptions);

        // Assert: final Load succeeds and returns valid settings.
        var final = SettingsService.Load(ConfigPath);
        Assert.NotNull(final);
        Assert.True(final.Port >= 1024, $"Final port should be >= 1024, got {final.Port}");
    }
}
