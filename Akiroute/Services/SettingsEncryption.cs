using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Akiroute.Services;

/// <summary>
/// Encrypts the settings document at rest with Windows DPAPI
/// (<see cref="ProtectedData"/>, CurrentUser scope): a key derived from the
/// Windows logon credential of THIS user on THIS machine protects the stored
/// node credentials (uuids, passwords, psks) and tokenized subscription URLs.
/// The on-disk format is a small JSON wrapper:
///
/// <code>
/// { "format": "akiroute-encrypted-v1", "cipher": "dpapi-currentuser", "data": "&lt;base64&gt;" }
/// </code>
///
/// Threat model: protects against the config file leaving the machine (backups,
/// sync, file copies). It does NOT protect against another process running as
/// the same Windows user — DPAPI decrypts silently for its own user.
///
/// Plain-JSON settings files written by older builds remain readable
/// (<see cref="IsEncryptedPayload"/> returns false for them) and are migrated
/// to the encrypted format on the next save.
/// </summary>
public static class SettingsEncryption
{
    /// <summary>Format marker inside the wrapper document.</summary>
    private const string FormatMarker = "akiroute-encrypted-v1";

    /// <summary>
    /// Application-specific salt for DPAPI. NOT a secret — it only prevents
    /// blobs from being interchangeable with other apps' DPAPI payloads.
    /// Changing it makes every stored settings file undecryptable; bump it
    /// only together with a format migration.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Akiroute.settings.v1");

    /// <summary>
    /// True when <paramref name="fileContent"/> is one of our encrypted wrapper
    /// documents. Garbage, empty content, and legacy plain JSON all return false.
    /// </summary>
    public static bool IsEncryptedPayload(string fileContent)
    {
        try
        {
            return JsonNode.Parse(fileContent) is JsonObject obj
                && obj["format"]?.GetValue<string>() == FormatMarker;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not JSON at all, or a non-object root — a plaintext/garbage file.
            return false;
        }
    }

    /// <summary>
    /// Encrypts a plain settings JSON document into the wrapper payload format.
    /// </summary>
    public static string EncryptToPayload(string plaintextJson)
    {
        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintextJson), Entropy, DataProtectionScope.CurrentUser);
        return new JsonObject
        {
            ["format"] = FormatMarker,
            ["cipher"] = "dpapi-currentuser",
            ["data"] = Convert.ToBase64String(cipher),
        }.ToJsonString();
    }

    /// <summary>
    /// Decrypts a wrapper payload document back to the plain settings JSON.
    /// Throws (<see cref="CryptographicException"/> for a foreign user/machine
    /// or tampered blob, <see cref="FormatException"/> for broken base64) —
    /// the caller decides how to surface the failure.
    /// </summary>
    public static string DecryptPayloadToJson(string fileContent)
    {
        if (JsonNode.Parse(fileContent) is not JsonObject obj)
        {
            throw new InvalidOperationException("Encrypted settings payload is not a JSON object.");
        }

        var data = obj["data"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Encrypted settings payload has no data field.");

        var plain = ProtectedData.Unprotect(
            Convert.FromBase64String(data), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
