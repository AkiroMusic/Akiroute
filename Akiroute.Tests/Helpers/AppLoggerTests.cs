using Akiroute.Helpers;

namespace Akiroute.Tests.Helpers;

/// <summary>
/// AppLogger tests.  Every test redirects the log file to a per-test temp
/// directory and resets defaults on disposal — the real <c>LocalApplicationData</c>
/// path is never touched.
/// </summary>
public class AppLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public AppLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "akiroute-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        AppLogger.InternalRedirectPathForTests(LogPath);
    }

    public void Dispose()
    {
        AppLogger.InternalResetDefaults();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string LogPath => Path.Combine(_tempDir, "akiroute.log");

    private string OldLogPath => LogPath + ".old";

    [Fact]
    public void Write_Info_WritesTimestampedLine()
    {
        // Act: write a single info message.
        AppLogger.Info("hello world");

        // Assert: the file contains the level tag and the message text.
        Assert.True(File.Exists(LogPath));
        var content = File.ReadAllText(LogPath);
        Assert.Contains("[INFO]", content);
        Assert.Contains("hello world", content);
    }

    [Fact]
    public void Rotation_ExceedsCap_RotatesOldFile()
    {
        // Arrange: set a tiny rotation cap (100 bytes).
        AppLogger.InternalSetCapForTests(100);

        // Act: write enough lines to exceed the cap.
        for (var i = 0; i < 20; i++)
        {
            AppLogger.Info($"line-{i:D4} padding to exceed the cap");
        }

        // Assert: the old generation file exists and the current file is
        // fresh (small — only the lines written after rotation).
        Assert.True(File.Exists(OldLogPath), "akiroute.log.old should exist after rotation");
        var currentSize = new FileInfo(LogPath).Length;
        Assert.True(currentSize < 512, $"Current log should be small after rotation, was {currentSize} bytes");
    }

    [Fact]
    public void Concurrent_Writes_AllPersisted()
    {
        // Arrange: 8 parallel tasks × 25 unique markers = 200 markers total.
        const int taskCount = 8;
        const int writesPerTask = 25;
        var barrier = new System.Threading.Barrier(taskCount);

        Parallel.For(0, taskCount, new ParallelOptions { MaxDegreeOfParallelism = taskCount }, taskIndex =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < writesPerTask; i++)
            {
                var marker = $"MARKER-t{taskIndex}-i{i}";
                AppLogger.Info(marker);
            }
        });

        // Assert: every marker is present in the log file.
        var content = File.ReadAllText(LogPath);
        for (var t = 0; t < taskCount; t++)
        {
            for (var i = 0; i < writesPerTask; i++)
            {
                var marker = $"MARKER-t{t}-i{i}";
                Assert.Contains(marker, content);
            }
        }
    }

    [Fact]
    public void BrokenTarget_NeverThrows()
    {
        // Arrange: point the log path INTO an existing FILE (not a directory),
        // so Directory.CreateDirectory and File.Append will fail.
        var blocker = Path.Combine(_tempDir, "blocker.txt");
        File.WriteAllText(blocker, "not a directory");
        AppLogger.InternalRedirectPathForTests(Path.Combine(blocker, "nested", "akiroute.log"));

        // Act + Assert: no exception escapes.
        var exception = Record.Exception(() => AppLogger.Info("should not throw"));
        Assert.Null(exception);

        // Also test Error with exception — must not throw.
        exception = Record.Exception(() => AppLogger.Error("error path", new InvalidOperationException("test")));
        Assert.Null(exception);
    }

    // ─── ReadTail ─────────────────────────────────────────────────────

    [Fact]
    public void ReadTail_LastNLines_ReturnsCorrectTail()
    {
        // Arrange: write 10 known lines.
        for (var i = 1; i <= 10; i++)
        {
            AppLogger.Info($"line-{i:D2}");
        }

        // Act: request the last 3 lines.
        var tail = AppLogger.ReadTail(3);

        // Assert: exactly 3 lines, the last 3 written, in order.
        Assert.Equal(3, tail.Length);
        Assert.Contains("line-08", tail[0]);
        Assert.Contains("line-09", tail[1]);
        Assert.Contains("line-10", tail[2]);
    }

    [Fact]
    public void ReadTail_FewerLinesThanN_ReturnsAllLines()
    {
        // Arrange: write exactly 2 lines.
        AppLogger.Info("first");
        AppLogger.Info("second");

        // Act: request more lines than exist.
        var tail = AppLogger.ReadTail(10);

        // Assert: both lines are returned.
        Assert.Equal(2, tail.Length);
        Assert.Contains("first", tail[0]);
        Assert.Contains("second", tail[1]);
    }

    [Fact]
    public void ReadTail_MissingFile_ReturnsEmpty()
    {
        // Arrange: redirect to a fresh directory where no log file exists yet.
        var freshDir = Path.Combine(_tempDir, "fresh");
        Directory.CreateDirectory(freshDir);
        AppLogger.InternalRedirectPathForTests(Path.Combine(freshDir, "akiroute.log"));

        // Act: read tail from a file that doesn't exist.
        var tail = AppLogger.ReadTail(50);

        // Assert: empty, no exception.
        Assert.NotNull(tail);
        Assert.Empty(tail);
    }

    [Fact]
    public void ReadTail_BrokenTarget_NeverThrows()
    {
        // Arrange: point the log path INTO an existing FILE (not a directory),
        // so the target cannot be resolved as a readable file.
        var blocker = Path.Combine(_tempDir, "blocker2.txt");
        File.WriteAllText(blocker, "not a directory");
        AppLogger.InternalRedirectPathForTests(Path.Combine(blocker, "nested", "akiroute.log"));

        // Act + Assert: no exception escapes; returns empty.
        var exception = Record.Exception(() => AppLogger.ReadTail(100));
        Assert.Null(exception);
    }
}
