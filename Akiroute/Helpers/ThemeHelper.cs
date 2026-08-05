using System.Diagnostics;
using Akiroute.Models;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Akiroute.Helpers;

/// <summary>
/// Applies the Mica backdrop and the dark/light/system theme preference to a
/// window. Theme is applied to the root element of the window content so every
/// descendant picks it up through property inheritance.
/// </summary>
public static class ThemeHelper
{
    /// <summary>
    /// Applies the requested theme to <paramref name="window"/>. Idempotent and
    /// safe to call before the window content is fully realized.
    /// </summary>
    /// <param name="window">The window to theme.</param>
    /// <param name="theme">The theme preference to apply.</param>
    public static void ApplyTheme(Window window, AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(window);

        // SystemBackdrop can throw on some Windows App SDK builds when the
        // window has not been fully realized yet (e.g. called right after the
        // Window is constructed but before Activate). Fall back to the default
        // solid backdrop in that case instead of crashing startup.
        try
        {
            window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeHelper] MicaBackdrop unavailable: {ex.Message}");
        }

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
