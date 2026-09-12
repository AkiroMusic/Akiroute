using System.Diagnostics;
using System.Text.Json;
using Akiroute.Models;

namespace Akiroute.Services;

/// <summary>
/// 配置备份与恢复服务 / Configuration backup and restore service.
///
/// <para><b>Export</b>: serializes an <see cref="AppSettings"/> instance to a
/// user-chosen JSON file. The backup protects accumulated nodes, process rules,
/// and subscriptions so they survive a config reset or machine migration.
/// Backups are deliberately written as PLAINTEXT (unlike the at-rest encrypted
/// live config) so they stay portable across machines — the UI warns the user
/// that the file carries credentials in the clear. Writes are atomic
/// (tmp+move) — delegates to
/// <see cref="SettingsService.Save(AppSettings, string, bool)"/> to reuse its
/// lock/retry logic and avoid duplication.</para>
///
/// <para><b>Import</b>: reads a backup JSON file and deserializes it into a
/// validated <see cref="AppSettings"/> object. Both plaintext backups and a
/// DPAPI-encrypted copy of the live config are accepted (an encrypted copy is
/// only restorable on the same Windows user/machine). The returned object is
/// independent from the live config — the caller decides when (and whether)
/// to apply it. Import never throws on user-facing paths: missing files,
/// unreadable files, invalid JSON, and undecryptable payloads all return
/// <c>null</c> with a diagnostic trace.</para>
/// </summary>
public static class ConfigBackupService
{
    /// <summary>
    /// 将当前配置序列化为 JSON 并原子写入指定文件。
    /// Serializes the current settings to JSON and writes atomically to the
    /// specified file path. Delegates to <see cref="SettingsService.Save(AppSettings, string)"/>
    /// so the atomic tmp+move write, file-lock retry, and static-lock
    /// semantics are reused verbatim — no duplicated I/O logic.
    /// </summary>
    /// <param name="settings">The settings snapshot to back up.</param>
    /// <param name="filePath">Destination path (overwritten if it exists).</param>
    /// <remarks>May propagate I/O exceptions from the underlying write; callers
    /// (UI handlers) are expected to catch and surface them to the user.</remarks>
    public static void ExportToFile(AppSettings settings, string filePath)
    {
        // Delegate to SettingsService.Save to reuse its atomic tmp+move
        // mechanics, SaveLock serialization, and single-retry semantics.
        // encrypt: false — backups are PLAINTEXT by design (portable across
        // machines); the UI warns the user about the credential exposure.
        // Duplicating that logic here would violate DRY and risk drift.
        SettingsService.Save(settings, filePath, encrypt: false);
    }

    /// <summary>
    /// 从备份 JSON 文件中读取配置。
    /// Reads a backup file and deserializes into <see cref="AppSettings"/>.
    /// Accepts plaintext backups AND a DPAPI-encrypted copy of the live
    /// config (restorable on the same Windows user/machine only).
    /// Returns <c>null</c> when the file is missing, unreadable, contains
    /// invalid JSON, or is encrypted for a different user/machine — never
    /// throws on user-facing paths. Failures are logged to <see cref="Debug"/>
    /// output for diagnostics.
    /// </summary>
    /// <param name="filePath">Path to the backup JSON file.</param>
    /// <returns>The deserialized settings, or <c>null</c> on any read/deserialize failure.</returns>
    public static AppSettings? ImportFromFile(string filePath)
    {
        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            // Missing or unreadable file — return null so the caller can
            // surface a user-friendly "文件无法读取" message.
            Debug.WriteLine($"[ConfigBackupService] Cannot read backup file {filePath}: {ex.Message}");
            return null;
        }

        // An encrypted copy of the live config: unwrap it first. A blob
        // encrypted for a different Windows user/machine cannot be decrypted
        // here and is reported as invalid, like any other unreadable backup.
        if (SettingsEncryption.IsEncryptedPayload(json))
        {
            try
            {
                json = SettingsEncryption.DecryptPayloadToJson(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConfigBackupService] Cannot decrypt backup file {filePath}: {ex.Message}");
                return null;
            }
        }

        try
        {
            return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[ConfigBackupService] Invalid JSON in backup file {filePath}: {ex.Message}");
            return null;
        }
    }
}
