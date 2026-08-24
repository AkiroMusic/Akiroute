using System.Diagnostics;

namespace Akiroute.Helpers;

/// <summary>
/// Lightweight, thread-safe file logger for field diagnostics.
/// Writes timestamped lines to <see cref="AppPaths.LogFile"/> with a simple
/// single-generation rotation policy.  All I/O is wrapped — failures are
/// reported to <see cref="Debug.WriteLine"/> and never escape.
/// </summary>
/// <remarks>
/// API surface: <see cref="Info(string)"/>, <see cref="Warn(string)"/>,
/// <see cref="Error(string)"/>, and <see cref="Error(string, Exception)"/>.
/// The logger is AOT-safe (plain <c>File</c> I/O, zero reflection) and
/// thread-safe (serialised by a private lock).
/// </remarks>
public static class AppLogger
{
    /// <summary>Default maximum log file size in bytes before rotation (512 KB).</summary>
    private const long DefaultMaxBytes = 512L * 1024;

    private static readonly object SyncRoot = new();

    /// <summary>Current rotation cap; mutable only via <see cref="InternalSetCapForTests"/>.</summary>
    private static long _maxBytes = DefaultMaxBytes;

    /// <summary>Active log file path; mutable only via <see cref="InternalRedirectPathForTests"/>.</summary>
    private static string _logFile = AppPaths.LogFile;

    /// <summary>Active old-generation file path derived from <see cref="_logFile"/>.</summary>
    private static string OldLogFile => _logFile + ".old";

    // ─── Public API ───────────────────────────────────────────────────

    /// <summary>Appends an informational message to the log.</summary>
    /// <param name="message">Human-readable description of the event.</param>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>Appends a warning message to the log.</summary>
    /// <param name="message">Human-readable description of the condition.</param>
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>Appends an error message to the log.</summary>
    /// <param name="message">Human-readable description of the failure.</param>
    public static void Error(string message) => Write("ERROR", message);

    /// <summary>
    /// Appends an error message and the full exception details to the log.
    /// </summary>
    /// <param name="message">Human-readable description of the failure.</param>
    /// <param name="ex">Exception to append (via <c>Exception.ToString()</c>).</param>
    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}\n{ex}");

    // ─── Internal test seams ──────────────────────────────────────────

    /// <summary>
    /// Sets the rotation cap in bytes.  Intended only for tests that verify
    /// rotation behaviour; production code must never call this.
    /// </summary>
    internal static void InternalSetCapForTests(long bytes)
    {
        lock (SyncRoot)
        {
            _maxBytes = bytes;
        }
    }

    /// <summary>
    /// Redirects the log file to <paramref name="path"/>.  Intended only for
    /// tests; production code uses <see cref="AppPaths.LogFile"/>.
    /// </summary>
    internal static void InternalRedirectPathForTests(string path)
    {
        lock (SyncRoot)
        {
            _logFile = path;
        }
    }

    /// <summary>
    /// Resets the logger to production defaults (path and cap).  Call from
    /// test teardown to avoid leaking state between test classes.
    /// </summary>
    internal static void InternalResetDefaults()
    {
        lock (SyncRoot)
        {
            _logFile = AppPaths.LogFile;
            _maxBytes = DefaultMaxBytes;
        }
    }

    // ─── Read API ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the last <paramref name="maxLines"/> lines of the current log
    /// file.  Honours the <see cref="InternalRedirectPathForTests"/> seam so
    /// tests can target a temporary directory.  Whole-file read is acceptable
    /// (file is capped at ~512 KB).
    /// </summary>
    /// <remarks>
    /// Never throws — mirrors the existing write-side convention.  If the file
    /// is missing, unreadable, or the target is broken, an empty array is
    /// returned.  <paramref name="maxLines"/> is clamped to ≥ 0 and the
    /// returned count is capped at 300 regardless of input.
    /// </remarks>
    internal static string[] ReadTail(int maxLines)
    {
        try
        {
            // Clamp to non-negative; cap returned lines at 300.
            maxLines = Math.Max(0, Math.Min(maxLines, 300));

            if (maxLines == 0)
            {
                return [];
            }

            string logPath;
            lock (SyncRoot)
            {
                logPath = _logFile;
            }

            if (!File.Exists(logPath))
            {
                return [];
            }

            // Whole-file read acceptable for files capped at ~512 KB.
            var lines = File.ReadAllLines(logPath);

            if (lines.Length <= maxLines)
            {
                return lines;
            }

            // Return the last maxLines entries, preserving order.
            var start = lines.Length - maxLines;
            var result = new string[maxLines];
            Array.Copy(lines, start, result, 0, maxLines);
            return result;
        }
        catch (Exception ex)
        {
            // NEVER throw from the logger — report and swallow.
            Debug.WriteLine($"[AppLogger] ReadTail failed: {ex.Message}");
            return [];
        }
    }

    // ─── Implementation ───────────────────────────────────────────────

    /// <summary>
    /// Formats and appends a single log line, rotating the file when the size
    /// cap is exceeded.  Never throws — all I/O failures are swallowed with
    /// a <see cref="Debug.WriteLine"/> diagnostic.
    /// </summary>
    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";

            lock (SyncRoot)
            {
                var dir = Path.GetDirectoryName(_logFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                RotateIfNeeded();

                // Append using a short-lived StreamWriter — the lock prevents
                // interleaved writes from concurrent callers.
                using var stream = new StreamWriter(
                    new FileStream(
                        _logFile,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.None));
                stream.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            // NEVER throw from the logger — report and swallow.
            Debug.WriteLine($"[AppLogger] Write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks whether the current log file exceeds <see cref="_maxBytes"/> and,
    /// if so, rotates: deletes the old generation, moves the current file to
    /// the old slot, and starts a fresh file.
    /// </summary>
    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_logFile))
            {
                return;
            }

            var info = new FileInfo(_logFile);
            if (info.Length <= _maxBytes)
            {
                return;
            }

            // Keep exactly ONE old generation: delete stale backup, then rotate.
            if (File.Exists(OldLogFile))
            {
                File.Delete(OldLogFile);
            }

            File.Move(_logFile, OldLogFile);
        }
        catch (Exception ex)
        {
            // Rotation failure is non-fatal — the log may grow beyond the cap
            // but writing continues.
            Debug.WriteLine($"[AppLogger] Rotation failed: {ex.Message}");
        }
    }
}
