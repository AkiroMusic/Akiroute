using System.Xml.Linq;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Akiroute.Helpers;

/// <summary>
/// Static localization helper for unpackaged WinUI 3 apps. Wraps MRT Core's
/// <see cref="ResourceLoader"/> to provide simple key-based string lookup from
/// the Strings/{locale}/Resources.resw files. Falls back to a direct XML parse
/// of the zh-CN resw when the ResourceLoader is unavailable (e.g. unit tests).
///
/// Usage in code-behind / view models:
/// <code>string text = Loc.Get("Status.Connected");</code>
///
/// 静态本地化辅助类，封装 MRT Core 的 ResourceLoader，用于在代码中按键名
/// 读取 Strings/{locale}/Resources.resw 里的本地化字符串。当 ResourceLoader
/// 不可用时（如单元测试），直接解析 zh-CN resw 文件作为回退。
/// </summary>
public static class Loc
{
    private static readonly ResourceLoader? s_loader = CreateLoader();
    private static readonly Dictionary<string, string> s_fallback = LoadFallback();

    /// <summary>
    /// Returns the localized string for <paramref name="key"/>, or the key itself
    /// when the resource is not found.
    /// </summary>
    /// <param name="key">The resource key (e.g. "Status.Connected").</param>
    /// <returns>Localized string or the key as fallback.</returns>
    public static string Get(string key)
    {
        // Try ResourceLoader first (works at runtime in WinUI app).
        if (s_loader is not null)
        {
            try
            {
                var value = s_loader.GetString(key);
                // ResourceLoader returns the key itself when not found —
                // treat that as a miss and try the fallback dictionary.
                if (!string.IsNullOrEmpty(value) && value != key)
                {
                    return value;
                }
            }
            catch
            {
                // ResourceLoader threw (e.g. no WinUI runtime); fall through.
            }
        }

        // Fallback: direct zh-CN resw dictionary (works in unit tests).
        return s_fallback.TryGetValue(key, out var localized) ? localized : key;
    }

    private static ResourceLoader? CreateLoader()
    {
        try
        {
            return new ResourceLoader("Akiroute/Resources");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the zh-CN/Resources.resw XML file directly as a fallback
    /// dictionary. Used when the ResourceLoader is unavailable (unit tests).
    /// </summary>
    private static Dictionary<string, string> LoadFallback()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            // Locate the resw next to the executing assembly (build output dir).
            var baseDir = AppContext.BaseDirectory;
            var reswPath = Path.Combine(baseDir, "Strings", "zh-CN", "Resources.resw");
            if (!File.Exists(reswPath))
            {
                // Try the project source directory (for unit tests that
                // reference the main project but run from a different output).
                var projectDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Akiroute"));
                reswPath = Path.Combine(projectDir, "Strings", "zh-CN", "Resources.resw");
            }

            if (!File.Exists(reswPath))
            {
                return dict;
            }

            var doc = XDocument.Load(reswPath);
            foreach (var data in doc.Descendants("data"))
            {
                var name = data.Attribute("name")?.Value;
                var value = data.Element("value")?.Value;
                if (name is not null && value is not null)
                {
                    dict[name] = value;
                }
            }
        }
        catch
        {
            // Swallow: fallback is best-effort.
        }

        return dict;
    }
}
