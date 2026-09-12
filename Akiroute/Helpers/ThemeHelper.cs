using System.Diagnostics;
using Akiroute.Models;
using Microsoft.UI.Xaml;

namespace Akiroute.Helpers;

/// <summary>
/// Applies the dark/light/system theme preference to a window. Theme is applied
/// to the root element of the window content so every descendant picks it up
/// through property inheritance.
/// </summary>
public static class ThemeHelper
{
    /// <summary>
    /// Applies the requested theme to <paramref name="window"/>. Idempotent and
    /// safe to call before the window content is fully realized.
    ///
    /// NOTE: no Mica backdrop is set — MainWindow paints a fully opaque
    /// background layer (EtBgBase + glows), so a backdrop would never be
    /// visible. Revisit only if the window becomes translucent.
    /// </summary>
    /// <param name="window">The window to theme.</param>
    /// <param name="theme">The theme preference to apply.</param>
    public static void ApplyTheme(Window window, AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(window);

        // RequestedTheme is inherited by the whole visual tree, so a single
        // assignment on the root framework element themes the entire window.
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = ToElementTheme(theme);
        }
    }

    /// <summary>
    /// Same as <see cref="ApplyTheme"/> but never throws: all failures are
    /// swallowed and logged to the debug output.
    /// </summary>
    /// <param name="window">The window to theme.</param>
    /// <param name="theme">The theme preference to apply.</param>
    public static void ApplyThemeSafe(Window window, AppTheme theme)
    {
        try
        {
            ApplyTheme(window, theme);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeHelper] ApplyThemeSafe: {ex.Message}");
        }
    }

    /// <summary>Maps an <see cref="AppTheme"/> preference to a WinUI <see cref="ElementTheme"/>.</summary>
    /// <param name="theme">The theme preference.</param>
    /// <returns>The matching <see cref="ElementTheme"/> (System maps to Default).</returns>
    public static ElementTheme ToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.Dark => ElementTheme.Dark,
        AppTheme.Light => ElementTheme.Light,
        _ => ElementTheme.Default,
    };
}
