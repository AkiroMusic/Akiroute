using System.Diagnostics;
using System.Text.Json;
using Akiroute.Helpers;
using Akiroute.Models;

namespace Akiroute.Services;

/// <summary>
/// Loads and saves the application settings file.
/// Design note: the parameterless <see cref="Load()"/> / <see cref="Save(AppSettings)"/>
/// overloads are the production entry points and always target
/// <see cref="AppPaths.ConfigFile"/>; the path-based overloads are the test seam
/// (and serve future alternate configs) so unit tests can point at temp files.
/// </summary>
public static class SettingsService
{
    private static readonly object SaveLock = new();

    /// <summary>Loads settings from the default config file; never throws.</summary>
    /// <returns>The persisted settings, or fresh defaults when the file is missing or corrupt.</returns>
    public static AppSettings Load() => Load(AppPaths.ConfigFile);

    /// <summary>
    /// Loads settings from <paramref name="configFilePath"/>; never throws.
    /// A missing file returns defaults. A corrupt, empty, or whitespace-only
    /// file is renamed to "&lt;name&gt;.corrupt-&lt;yyyyMMddHHmmss&gt;" before
    /// defaults are returned. Failures are logged to Debug output.
    /// </summary>
    public static AppSettings Load(string configFilePath)
    {
        string json;
        try
        {
            json = File.ReadAllText(configFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[SettingsService] Cannot read {configFilePath}: {ex.Message}");
            return new AppSettings();
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            Debug.WriteLine($"[SettingsService] Settings file is empty, backing up: {configFilePath}");
            RenameCorruptFile(configFilePath);
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[SettingsService] Corrupt settings JSON in {configFilePath}: {ex.Message}");
            RenameCorruptFile(configFilePath);
            return new AppSettings();
        }
    }

    /// <summary>Saves settings to the default config file (atomic, thread-safe).</summary>
    public static void Save(AppSettings settings)
    {
        AppPaths.EnsureDirectories();
        Save(settings, AppPaths.ConfigFile);
    }

    /// <summary>
    /// Atomically saves <paramref name="settings"/> to <paramref name="configFilePath"/>:
    /// the JSON is written to "&lt;path&gt;.tmp" first, then moved over the target.
    /// If the target is locked the move is retried once after 50 ms; a second
    /// failure is rethrown, never swallowed. Writes are serialized by a static lock.
    /// </summary>
    public static void Save(AppSettings settings, string configFilePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(configFilePath);

        var fullPath = Path.GetFullPath(configFilePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"Config file path has no directory: {configFilePath}", nameof(configFilePath));
        Directory.CreateDirectory(directory);

        var tmpPath = fullPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, AppJsonSerializerContext.Default.AppSettings);

        lock (SaveLock)
        {
            File.WriteAllText(tmpPath, json);
            try
            {
                File.Move(tmpPath, fullPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Target may be transiently locked; retry once, then surface the failure.
                Thread.Sleep(50);
                try
                {
                    File.Move(tmpPath, fullPath, overwrite: true);
                }
                catch (Exception retryEx) when (retryEx is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"[SettingsService] Save failed, target locked: {fullPath}: {retryEx.Message}");
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Renames a corrupt settings file to "&lt;name&gt;.corrupt-&lt;timestamp&gt;"
    /// so the original is never silently destroyed. Best-effort: a rename that
    /// fails (e.g. the file is still locked) is logged, never thrown.
    /// </summary>
    private static void RenameCorruptFile(string configFilePath)
    {
        var backupPath = $"{configFilePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
        try
        {
            File.Move(configFilePath, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[SettingsService] Cannot back up corrupt settings file {configFilePath}: {ex.Message}");
        }
    }
}
