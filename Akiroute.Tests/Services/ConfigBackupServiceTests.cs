using System.Text.Json;
using Akiroute.Models;
using Akiroute.Services;

namespace Akiroute.Tests.Services;

/// <summary>
/// ConfigBackupService tests. Every test uses a per-test temp directory for
/// file I/O — the real config path is never touched.
/// </summary>
public class ConfigBackupServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigBackupServiceTests()
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

    private string BackupPath => Path.Combine(_tempDir, "backup.json");

    private static string ToJson(AppSettings settings) =>
        JsonSerializer.Serialize(settings, AppJsonSerializerContext.Default.AppSettings);

    /// <summary>
    /// 构造带有多种数据类型的测试用设置 / Creates settings with multiple data
    /// types to verify round-trip fidelity across nodes, rules, and subscriptions.
    /// </summary>
    private static AppSettings SampleSettings() => new()
    {
        SelectedNodeId = "n1",
        Mode = ProxyMode.ProcessOnly,
        Port = 9090,
        AutoConnect = true,
        SubscriptionAutoUpdateMinutes = 30,
        AutoPingMinutes = 15,
        TunEnabled = true,
        Theme = AppTheme.Dark,
        Nodes =
        {
            new ProxyNode { Id = "n1", Name = "Tokyo-1", Type = "vless", Address = "198.51.100.10", Port = 443 },
            new ProxyNode { Id = "n2", Name = "Frankfurt-2", Type = "vmess", Address = "203.0.113.20", Port = 8443 },
        },
        ProcessRules =
        {
            new ProcessRule { ProcessName = "game.exe", Action = ProcessAction.Block },
        },
        Subscriptions =
        {
            new SubscriptionEntry { Id = "s1", Name = "Primary Feed", Url = "https://example.com/sub" },
        },
    };

    // ── Test 1: Round-trip preserves all data ─────────────────────────────

    /// <summary>
    /// 导出后导入应完整保留所有设置项。
    /// Export then import must preserve every field: node names/addresses/ports,
    /// process rule processName + action, subscription URL, and AutoPingMinutes.
    /// </summary>
    [Fact]
    public void ExportImport_RoundTrip_PreservesData()
    {
        // Arrange
        Directory.CreateDirectory(_tempDir);
        var original = SampleSettings();

        // Act: export to file, then import back.
        ConfigBackupService.ExportToFile(original, BackupPath);
        var restored = ConfigBackupService.ImportFromFile(BackupPath);

        // Assert: restored settings are equivalent to the original.
        Assert.NotNull(restored);

        // Nodes: two distinct nodes with different names, addresses, ports.
        Assert.Equal(2, restored.Nodes.Count);
        Assert.Equal("Tokyo-1", restored.Nodes[0].Name);
        Assert.Equal("198.51.100.10", restored.Nodes[0].Address);
        Assert.Equal(443, restored.Nodes[0].Port);
        Assert.Equal("Frankfurt-2", restored.Nodes[1].Name);
        Assert.Equal("203.0.113.20", restored.Nodes[1].Address);
        Assert.Equal(8443, restored.Nodes[1].Port);

        // Process rule: processName + action.
        Assert.Single(restored.ProcessRules);
        Assert.Equal("game.exe", restored.ProcessRules[0].ProcessName);
        Assert.Equal(ProcessAction.Block, restored.ProcessRules[0].Action);

        // Subscription: URL preserved.
        Assert.Single(restored.Subscriptions);
        Assert.Equal("https://example.com/sub", restored.Subscriptions[0].Url);

        // Scalar: AutoPingMinutes.
        Assert.Equal(15, restored.AutoPingMinutes);
    }

    // ── Test 1b: Export stays plaintext (portable by design) ─────────────

    /// <summary>
    /// 备份导出必须是明文 JSON：跨机器可恢复是备份的核心用途，
    /// 代价（凭据明文）由 UI 警告向用户说明。
    /// The export must be PLAINTEXT JSON — portability is the point of a
    /// backup; the UI warns about the credential exposure.
    /// </summary>
    [Fact]
    public void Export_WritesPlaintextJson_NotEncryptedWrapper()
    {
        // Arrange
        Directory.CreateDirectory(_tempDir);

        // Act.
        ConfigBackupService.ExportToFile(SampleSettings(), BackupPath);
        var raw = File.ReadAllText(BackupPath);

        // Assert: readable plaintext, NOT the encrypted wrapper format.
        Assert.False(SettingsEncryption.IsEncryptedPayload(raw));
        Assert.Contains("https://example.com/sub", raw);
    }

    // ── Test 1c: Import accepts a DPAPI-encrypted copy of the live config ─

    /// <summary>
    /// 导入加密格式的配置副本（用户直接复制 settings.json 当备份的场景）
    /// 应当成功解密读取 —— 仅限同一 Windows 用户/机器。
    /// Importing an ENCRYPTED copy of the live config (e.g. the user backed up
    /// settings.json directly) must decrypt and load on the same machine.
    /// </summary>
    [Fact]
    public void Import_EncryptedConfigCopy_RestoresOnSameMachine()
    {
        // Arrange: write the live-format encrypted payload to the backup path.
        Directory.CreateDirectory(_tempDir);
        var plainJson = ToJson(SampleSettings());
        File.WriteAllText(BackupPath, SettingsEncryption.EncryptToPayload(plainJson));

        // Act.
        var restored = ConfigBackupService.ImportFromFile(BackupPath);

        // Assert.
        Assert.NotNull(restored);
        Assert.Equal("Tokyo-1", restored.Nodes[0].Name);
        Assert.Equal("https://example.com/sub", restored.Subscriptions[0].Url);
    }

    // ── Test 2: Invalid JSON returns null ─────────────────────────────────

    /// <summary>
    /// 无效 JSON 文件应返回 null，不抛出异常。
    /// A file containing invalid JSON must return null, never throw.
    /// </summary>
    [Fact]
    public void Import_InvalidJson_ReturnsNull()
    {
        // Arrange: write garbage JSON.
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(BackupPath, "{ not json at all }}");

        // Act — must not throw.
        var result = ConfigBackupService.ImportFromFile(BackupPath);

        // Assert: null return, no exception.
        Assert.Null(result);
    }

    // ── Test 3: Missing file returns null ─────────────────────────────────

    /// <summary>
    /// 不存在的文件应返回 null，不抛出异常。
    /// A non-existent file path must return null, never throw.
    /// </summary>
    [Fact]
    public void Import_MissingFile_ReturnsNull()
    {
        // Act: import from a path that does not exist.
        var result = ConfigBackupService.ImportFromFile(BackupPath);

        // Assert: null, no file created.
        Assert.Null(result);
        Assert.False(File.Exists(BackupPath));
    }

    // ── Test 4: Export creates parseable file ─────────────────────────────

    /// <summary>
    /// 导出后文件应存在、非空且可被反序列化解析。
    /// After export, the file must exist, be non-empty, and deserialize without
    /// exception — proving the output is valid JSON.
    /// </summary>
    [Fact]
    public void Export_CreatesParseableFile()
    {
        // Arrange
        Directory.CreateDirectory(_tempDir);
        var settings = SampleSettings();

        // Act
        ConfigBackupService.ExportToFile(settings, BackupPath);

        // Assert: file exists and is non-empty.
        Assert.True(File.Exists(BackupPath));
        var content = File.ReadAllText(BackupPath);
        Assert.False(string.IsNullOrWhiteSpace(content));

        // Assert: content deserializes without exception (round-trip parse).
        var parsed = JsonSerializer.Deserialize<AppSettings>(content, AppJsonSerializerContext.Default.AppSettings);
        Assert.NotNull(parsed);
    }
}
