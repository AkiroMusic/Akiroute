using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Akiroute.Helpers;

/// <summary>
/// Extracts the associated icon of an executable (plan §3.2.3) and converts it to a
/// WinUI <see cref="BitmapImage"/> for display in the per-process panel.
///
/// WinUI-bound by design: this class requires the WinUI runtime and is never invoked
/// by unit tests — the x64 test host does not load WinUI, so the pure
/// <c>ProcessMonitorService</c> layer stays fully testable without it.
/// </summary>
public static class IconHelper
{
    /// <summary>
    /// Extracts the file's associated icon as a WinUI <see cref="BitmapImage"/>.
    /// Returns null on any failure — missing file, access denied, an unreadable
    /// image, or an uninitialized WinUI runtime — without throwing out of the
    /// method. All GDI/COM handles (Icon, Bitmap, stream) are released in a
    /// finally block so repeated refreshes cannot leak handles (plan §7.4).
    /// </summary>
    public static object? TryGetIcon(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        Icon? icon = null;
        Bitmap? bitmap = null;
        MemoryStream? stream = null;
        try
        {
            icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            // Render the icon to a PNG in memory, then hand the bytes to a
            // BitmapImage. ConfigureAwait(false) keeps the blocking continuation
            // off the UI dispatcher, so this never deadlocks when called on it.
            bitmap = icon.ToBitmap();
            stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;

            var image = new BitmapImage();
            image.SetSourceAsync(stream.AsRandomAccessStream())
                 .AsTask()
                 .ConfigureAwait(false)
                 .GetAwaiter()
                 .GetResult();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException or InvalidOperationException)
        {
            // Missing/locked file, unreadable image, or the WinUI runtime is not
            // initialized: no icon is available for this path.
            return null;
        }
        finally
        {
            icon?.Dispose();
            bitmap?.Dispose();
            stream?.Dispose();
        }
    }
}
