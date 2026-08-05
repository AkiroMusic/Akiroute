using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Akiroute.Helpers;
using Akiroute.Models;

namespace Akiroute.Services;

/// <summary>
/// Enumerates the user session's UI processes into <see cref="ProcessInfoItem"/>
/// snapshots for the per-process routing panel: blacklist filtering (plan §3.2.4),
/// friendly display names (plan §3.2.2), per-path routing-action persistence, and a
/// WinUI icon cache (plan §3.2.3).
///
/// Testability design: all pure logic lives in <see cref="GetCurrentProcesses"/> and
/// reads its input through the injectable enumerator seam, so the x64 test host can
/// feed hand-built <see cref="ProcessRecord"/>s without ever initializing the WinUI
/// runtime. WinUI-dependent icon extraction is confined to <see cref="EnsureIcons"/>
/// (and <see cref="IconHelper"/>), which unit tests never call.
/// </summary>
public sealed class ProcessMonitorService
{
    /// <summary>
    /// A single process observation handed to the service by an enumerator. The
    /// default enumerator fills it from <see cref="Process.GetProcesses()"/>; tests
    /// hand-build records through the injectable constructor seam.
    /// </summary>
    /// <param name="Pid">OS process id.</param>
    /// <param name="ProcessName">Executable name, with or without the ".exe" extension (matched case-insensitively).</param>
    /// <param name="Path">Full executable path.</param>
    /// <param name="HasWindow">True when the process owns a main window (a user-facing UI process).</param>
    internal readonly record struct ProcessRecord(int Pid, string ProcessName, string Path, bool HasWindow);

    /// <summary>
    /// Process names excluded outright (case-insensitive). Stored as listed and in
    /// their extension-stripped form, because <see cref="Process.ProcessName"/>
    /// omits the ".exe" extension while rule-style records may include it.
    /// </summary>
    private static readonly HashSet<string> BlacklistedProcessNames = BuildBlacklist();

    /// <summary>Windows root directories to match against: the environment's
    /// reported one plus the canonical <c>C:\Windows</c>.</summary>
    private static readonly string[] WindowsRoots = BuildWindowsRoots();

    /// <summary>Sentinel stored in the icon cache when extraction failed, so a failed
    /// extraction is only attempted once (<see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/>
    /// forbids null values).</summary>
    private static readonly object IconCacheMiss = new();

    /// <summary>Canonical process items keyed by executable path (case-insensitive).
    /// The same instance is returned across scans so the user's <see cref="ProcessAction"/>
    /// survives rescanning.</summary>
    private readonly ConcurrentDictionary<string, ProcessInfoItem> _items = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cached icons keyed by executable path; guards against GDI handle churn.</summary>
    private readonly ConcurrentDictionary<string, object> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<IEnumerable<ProcessRecord>> _enumerator;

    /// <summary>
    /// Creates a service that enumerates the real process table (windowed UI
    /// processes of the current user session).
    /// </summary>
    public ProcessMonitorService()
        : this(EnumerateProcesses)
    {
    }

    /// <summary>
    /// Creates a service backed by a caller-supplied enumerator. Internal test seam:
    /// unit tests inject fixed <see cref="ProcessRecord"/> lists so the pure
    /// enumeration/filtering/naming logic runs without a real desktop session.
    /// </summary>
    internal ProcessMonitorService(Func<IEnumerable<ProcessRecord>> enumerator)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
    }

    /// <summary>
    /// Returns a fresh snapshot of the user session's UI processes: windowed
    /// processes that survive the process-name blacklist and the System32 filter,
    /// each carrying a friendly display name. Previously seen paths keep their
    /// cached <see cref="ProcessInfoItem"/> instance — so the user's
    /// <see cref="ProcessAction"/> survives rescans — while paths absent from this
    /// scan are dropped from the cache. Safe to call from a background timer: all
    /// cache access is concurrent-safe.
    /// </summary>
    public IReadOnlyList<ProcessInfoItem> GetCurrentProcesses()
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = new List<ProcessInfoItem>();

        foreach (var record in _enumerator())
        {
            AddIfUserVisible(record, seenPaths, snapshot);
        }

        // Drop cached items whose process is no longer present in this scan.
        foreach (var path in _items.Keys)
        {
            if (!seenPaths.Contains(path))
            {
                _items.TryRemove(path, out _);
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Returns the cached item for <paramref name="path"/>, creating one with
    /// defaults when this is the first sighting. Used by the rule-binding layer to
    /// attach routing state to a process without re-scanning the process table.
    /// </summary>
    public ProcessInfoItem GetOrAddItemForPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var trimmed = path.Trim();
        return _items.GetOrAdd(trimmed, static p => new ProcessInfoItem
        {
            Path = p,
            ProcessName = Path.GetFileName(p),
            DisplayName = Path.GetFileName(p),
            IsSystem = IsUnderWindows(p),
        });
    }

    /// <summary>
    /// Releases the cached icon cache: disposes any cached icon objects that
    /// implement <see cref="IDisposable"/> (releasing their GDI/COM handles, plan
    /// §7.4) and empties the cache.
    /// </summary>
    public void ClearCache()
    {
        foreach (var icon in _iconCache.Values)
        {
            if (icon is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
                {
                    // Already disposed, or the runtime already released the handle.
                }
            }
        }

        _iconCache.Clear();
    }

    /// <summary>
    /// Returns the cached icon for <paramref name="path"/> without touching the
    /// WinUI runtime (a pure cache lookup), or null when nothing is cached yet.
    /// </summary>
    public object? GetCachedIcon(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (_iconCache.TryGetValue(path, out var icon) && !ReferenceEquals(icon, IconCacheMiss))
        {
            return icon;
        }

        return null;
    }

    /// <summary>
    /// Ensures each item has its icon extracted, cached, and assigned. Requires the
    /// WinUI runtime: real extraction is delegated to <see cref="IconHelper.TryGetIcon"/>
    /// and is only attempted once per path. The UI layer calls this on the UI
    /// thread; unit tests never do.
    /// </summary>
    public void EnsureIcons(IEnumerable<ProcessInfoItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (var item in items)
        {
            if (item is null || string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            // GetOrAdd stores a sentinel on failure so a missing icon is only
            // attempted once; the dictionary never holds null (GetOrAdd forbids it).
            var icon = _iconCache.GetOrAdd(item.Path, static path => IconHelper.TryGetIcon(path) ?? IconCacheMiss);
            item.Icon = ReferenceEquals(icon, IconCacheMiss) ? null : icon;
        }
    }

    /// <summary>
    /// Directly seeds the icon cache for a path. Internal test seam: lets unit tests
    /// exercise <see cref="ClearCache"/> and <see cref="GetCachedIcon"/> without
    /// loading the WinUI runtime (real extraction lives behind <see cref="EnsureIcons"/>).
    /// </summary>
    internal void SetCachedIcon(string path, object icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        _iconCache[path] = icon;
    }

    /// <summary>
    /// Decides whether a single record belongs in the user-facing snapshot and, if
    /// so, merges it into the item cache and appends it. The cached item's volatile
    /// fields are refreshed while <see cref="ProcessInfoItem.Action"/> is left
    /// untouched so the user's routing choice survives rescans.
    /// </summary>
    private void AddIfUserVisible(ProcessRecord record, HashSet<string> seenPaths, List<ProcessInfoItem> snapshot)
    {
        // Enumeration targets windowed UI processes only (plan §3.2.1).
        if (!record.HasWindow)
        {
            return;
        }

        // Blacklist filtering by process name (plan §3.2.4).
        if (IsBlacklistedName(record.ProcessName))
        {
            return;
        }

        var path = record.Path.Trim();
        if (path.Length == 0)
        {
            // No executable path to key on; nothing to display or cache.
            return;
        }

        // System32 processes are only user-facing when they carry a product
        // description (e.g. notepad.exe -> "Notepad"); svchost.exe has none and is
        // dropped here regardless of its name-based blacklist status.
        var description = TryGetFileDescription(path);
        if (IsUnderSystem32(path) && string.IsNullOrWhiteSpace(description))
        {
            return;
        }

        // Duplicate paths (several instances of one exe) are listed once.
        if (!seenPaths.Add(path))
        {
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(description) ? Path.GetFileName(path) : description.Trim();
        var isSystem = IsUnderWindows(path);

        var item = _items.GetOrAdd(path, static p => new ProcessInfoItem
        {
            Path = p,
            ProcessName = Path.GetFileName(p),
        });

        // Refresh the volatile fields; Action is intentionally not touched here.
        item.ProcessName = Path.GetFileName(path);
        item.DisplayName = displayName;
        item.Pid = record.Pid;
        item.IsSystem = isSystem;

        snapshot.Add(item);
    }

    /// <summary>Returns true when <paramref name="processName"/> is on the blacklist.</summary>
    private static bool IsBlacklistedName(string processName)
    {
        var name = processName.Trim();
        if (BlacklistedProcessNames.Contains(name))
        {
            return true;
        }

        // A rule-style name with the extension (e.g. "System.exe") still maps to a
        // listed extension-less entry.
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && BlacklistedProcessNames.Contains(name[..^4]);
    }

    /// <summary>Returns the process's FileDescription, or null when it cannot be read.</summary>
    private static string? TryGetFileDescription(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileDescription;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable or missing file, or a path that is not a PE image.
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> lives under a Windows root directory.</summary>
    private static bool IsUnderWindows(string path)
    {
        foreach (var root in WindowsRoots)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="path"/> lives inside the System32 directory.</summary>
    private static bool IsUnderSystem32(string path)
    {
        foreach (var root in WindowsRoots)
        {
            if (path.StartsWith(root + Path.DirectorySeparatorChar + "System32" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the process-name blacklist and its extension-stripped forms.</summary>
    private static HashSet<string> BuildBlacklist()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "svchost.exe",
            "explorer.exe",
            "System",
            "csrss.exe",
            "winlogon.exe",
            "services.exe",
            "smss.exe",
            "lsass.exe",
            "fontdrvhost.exe",
            "dwm.exe",
            "conhost.exe",
            "sihost.exe",
            "taskhostw.exe",
            "SearchHost.exe",
            "dllhost.exe",
            "RuntimeBroker.exe",
            "ShellExperienceHost.exe",
            "StartMenuExperienceHost.exe",
            "TextInputHost.exe",
            "Widgets.exe",
            "spoolsv.exe",
            "registry",
            "memory compression",
            "Idle",
        };

        // Process.ProcessName drops the ".exe" extension; add stripped forms so
        // both the raw OS name and the rule-style name match.
        foreach (var name in names.ToArray())
        {
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name[..^4]);
            }
        }

        return names;
    }

    /// <summary>Resolves the Windows root directories the environment reports, plus the canonical C:\Windows.</summary>
    private static string[] BuildWindowsRoots()
    {
        const string canonical = @"C:\Windows";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows))
        {
            return [canonical];
        }

        var trimmed = windows.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Equals(canonical, StringComparison.OrdinalIgnoreCase)
            ? [trimmed]
            : [trimmed, canonical];
    }

    /// <summary>
    /// Enumerates the real process table, keeping only windowed UI processes with a
    /// resolvable executable path. Every <see cref="Process"/> member access is
    /// guarded because a process can exit — or refuse access — between enumeration
    /// and access; unreadable processes are skipped, never thrown.
    /// </summary>
    private static IEnumerable<ProcessRecord> EnumerateProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // The process table could not be read at all; enumerate nothing.
            processes = Array.Empty<Process>();
        }

        foreach (var process in processes)
        {
            using (process)
            {
                var record = TryReadRecord(process);
                if (record is { } value)
                {
                    yield return value;
                }
            }
        }
    }

    /// <summary>Reads a single process into a <see cref="ProcessRecord"/>; returns null for
    /// processes without a main window or an unreadable executable path.</summary>
    private static ProcessRecord? TryReadRecord(Process process)
    {
        try
        {
            if (process.MainWindowHandle == 0)
            {
                // No main window: a background service, not a user-facing UI app.
                return null;
            }

            var path = process.MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(path))
            {
                // MainModule is often unreadable for elevated or protected processes.
                return null;
            }

            return new ProcessRecord(process.Id, process.ProcessName, path, HasWindow: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or Win32Exception)
        {
            // Process exited or access was denied mid-read; skip it.
            return null;
        }
    }
}
