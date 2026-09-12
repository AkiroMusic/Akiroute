using System;
using Akiroute.Helpers;
using Microsoft.Win32;

namespace Akiroute.Services;

/// <summary>
/// Manages the Windows auto-start registration via the HKCU
/// <c>Software\Microsoft\Windows\CurrentVersion\Run</c> key. This is
/// a convenience feature that writes a single "Run" entry so the app
/// launches automatically on Windows login.
///
/// NOTE: This writes to HKCU Run for AUTO-LAUNCH only — it does NOT
/// touch the Internet Settings proxy keys. The project rule
/// "Never touches Windows registry PROXY settings" refers specifically
/// to <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings</c>,
/// not general app configuration entries.
/// </summary>
public static class StartupHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Akiroute";

    /// <summary>
    /// Test seam: opens the registry hive the Run key lives under. Production
    /// always returns <see cref="Registry.CurrentUser"/>; unit tests redirect
    /// this to a scratch key so the real Run entry is never touched.
    /// </summary>
    internal static Func<RegistryKey>? RootKeyFactoryForTests { get; set; }

    private static RegistryKey Root => RootKeyFactoryForTests?.Invoke() ?? Registry.CurrentUser;

    /// <summary>
    /// Adds or removes the app from the Windows auto-start registry entry.
    /// Never throws — failures are logged and reported through the return
    /// value so callers (e.g. the settings panel) can surface them.
    /// </summary>
    /// <param name="enable">true to register for auto-start; false to remove.</param>
    /// <returns>true when the registry state now matches <paramref name="enable"/>.</returns>
    public static bool SetLaunchOnStartup(bool enable)
    {
        try
        {
            using var root = Root;
            using var key = root.OpenSubKey(RunKey, writable: true);
            if (key is null)
            {
                AppLogger.Warn("[StartupHelper] Cannot open HKCU Run key for writing; auto-start state unchanged");
                return false;
            }

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    AppLogger.Warn("[StartupHelper] Environment.ProcessPath is empty; cannot register auto-start");
                    return false;
                }

                key.SetValue(AppName, exePath);
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[StartupHelper] SetLaunchOnStartup({enable}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns the executable path currently registered for auto-start,
    /// or null when no entry exists or registry access fails.
    /// </summary>
    public static string? GetRegisteredExePath()
    {
        try
        {
            using var root = Root;
            using var key = root.OpenSubKey(RunKey);
            return key?.GetValue(AppName) as string;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[StartupHelper] Reading the Run entry failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks whether the app is currently registered for auto-start.
    /// Returns false if registry access fails.
    /// </summary>
    public static bool IsLaunchOnStartupEnabled() => GetRegisteredExePath() is not null;

    /// <summary>
    /// Aligns the registry with the persisted setting: re-registers when the
    /// setting is on but the entry is missing or points at a stale exe path
    /// (app moved/updated), and removes a leftover entry when the setting is
    /// off. Called at startup and after a config restore; failures are logged
    /// and never thrown.
    /// </summary>
    /// <param name="desired">The persisted <c>LaunchOnStartup</c> value.</param>
    public static void ReconcileLaunchOnStartup(bool desired)
    {
        var registered = GetRegisteredExePath();
        var currentPath = Environment.ProcessPath;

        if (desired)
        {
            var stale = registered is null
                || (currentPath is not null
                    && !string.Equals(registered, currentPath, StringComparison.OrdinalIgnoreCase));
            if (stale && !SetLaunchOnStartup(true))
            {
                AppLogger.Warn("[StartupHelper] LaunchOnStartup is enabled in settings but the registry update failed");
            }
        }
        else if (registered is not null && !SetLaunchOnStartup(false))
        {
            AppLogger.Warn("[StartupHelper] LaunchOnStartup is disabled in settings but the stale Run entry could not be removed");
        }
    }
}
