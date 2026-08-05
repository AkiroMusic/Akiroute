namespace Akiroute.Helpers;

/// <summary>
/// Central path definitions for Akiroute's on-disk layout.
/// All user data lives under LocalApplicationData\Akiroute; the system
/// registry and program directories are never touched.
/// </summary>
public static class AppPaths
{
    /// <summary>Root folder for all Akiroute user data.</summary>
    public static string AppDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Akiroute");

    /// <summary>Persistent configuration directory.</summary>
    public static string ConfigDir { get; } = Path.Combine(AppDataRoot, "Config");

    /// <summary>Main settings JSON file (written atomically by <c>SettingsService</c>).</summary>
    public static string ConfigFile { get; } = Path.Combine(ConfigDir, "settings.json");

    /// <summary>Runtime log directory.</summary>
    public static string LogsDir { get; } = Path.Combine(AppDataRoot, "Logs");

    /// <summary>Main log file path.</summary>
    public static string LogFile { get; } = Path.Combine(LogsDir, "akiroute.log");

    /// <summary>Xray subprocess config temp path (regenerated on every start).</summary>
    public static string XrayConfigTemp { get; } = Path.Combine(
        Path.GetTempPath(), "akiroute-xray-config.json");

    /// <summary>Directory of the xray engine assets (xray.exe, wintun.dll).</summary>
    public static string EngineDir { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "engine");

    /// <summary>Full path to the xray executable.</summary>
    public static string XrayExe { get; } = Path.Combine(EngineDir, "xray.exe");

    /// <summary>Rule asset directory (geoip.dat, geosite.dat).</summary>
    public static string RulesDir { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "rules");

    /// <summary>
    /// Ensures all persistent directories exist. Idempotent.
    /// </summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(LogsDir);
    }
}
