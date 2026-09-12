using Akiroute.Services;

namespace Akiroute.Tests.Services;

/// <summary>
/// SettingsEncryption tests: DPAPI wrapper format round-trip, format
/// detection, and the failure modes (tampered/foreign blob, broken base64)
/// that SettingsService routes to its corrupt-file path.
/// </summary>
public class SettingsEncryptionTests
{
    private const string PlainJson = """{"port":3333,"nodes":[{"name":"SG-1"}]}""";

    [Fact]
    public void EncryptThenDecrypt_RoundTrips()
    {
        var payload = SettingsEncryption.EncryptToPayload(PlainJson);
        var decrypted = SettingsEncryption.DecryptPayloadToJson(payload);

        Assert.Equal(PlainJson, decrypted);
    }

    [Fact]
    public void EncryptToPayload_ProducesWrapperDocument_WithoutPlaintext()
    {
        var payload = SettingsEncryption.EncryptToPayload(PlainJson);

        Assert.True(SettingsEncryption.IsEncryptedPayload(payload));
        Assert.DoesNotContain("SG-1", payload);
    }

    [Fact]
    public void IsEncryptedPayload_PlainJson_ReturnsFalse()
    {
        Assert.False(SettingsEncryption.IsEncryptedPayload(PlainJson));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage not json {{{")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"format\":\"something-else\",\"data\":\"AAAA\"}")]
    public void IsEncryptedPayload_NonWrapperContent_ReturnsFalse(string content)
    {
        Assert.False(SettingsEncryption.IsEncryptedPayload(content));
    }

    [Fact]
    public void DecryptPayloadToJson_TamperedData_Throws()
    {
        // Arrange: a valid wrapper whose data blob has been flipped to a
        // different valid-base64 value — DPAPI must reject it as tampered or
        // foreign (this is also what a config copied from another user/machine
        // looks like).
        var payload = SettingsEncryption.EncryptToPayload(PlainJson);
        var obj = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
        var cipher = Convert.FromBase64String(obj["data"]!.GetValue<string>());
        cipher[^1] ^= 0xFF;
        obj["data"] = Convert.ToBase64String(cipher);
        var tampered = obj.ToJsonString();

        // Act + Assert.
        Assert.ThrowsAny<Exception>(() => SettingsEncryption.DecryptPayloadToJson(tampered));
    }

    [Fact]
    public void DecryptPayloadToJson_BrokenBase64_Throws()
    {
        var payload = "{\"format\":\"akiroute-encrypted-v1\",\"cipher\":\"dpapi-currentuser\",\"data\":\"%%%not-base64%%%\"}";

        Assert.ThrowsAny<Exception>(() => SettingsEncryption.DecryptPayloadToJson(payload));
    }
}
