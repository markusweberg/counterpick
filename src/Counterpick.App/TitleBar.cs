using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Counterpick.App;

/// <summary>
/// Paints the native title bar in the page's colours.
///
/// WPF draws the window chrome light no matter what the window contains, so the app
/// opened with a white bar over a dark page. DWM exposes a dark-caption switch on
/// Windows 10 1809+ and explicit caption, text and border colours on Windows 11; setting
/// them keeps the standard title bar (drag, snap, the system buttons) and only changes
/// its paint. Every call is best-effort: an older build refuses the attribute and keeps
/// the light bar, which is cosmetic.
/// </summary>
internal static class TitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Call once the window has a handle, i.e. from OnSourceInitialized.</summary>
    public static void Paint(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        Set(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Set(hwnd, DWMWA_CAPTION_COLOR, Colorref(0x08, 0x0D, 0x14)); // --sunk, the topbar's own ground
        Set(hwnd, DWMWA_TEXT_COLOR, Colorref(0xD9, 0xA5, 0x4C));    // --gold, the wordmark
        Set(hwnd, DWMWA_BORDER_COLOR, Colorref(0x2B, 0x3A, 0x4D));  // --line-hard
    }

    /// <summary>COLORREF is 0x00BBGGRR.</summary>
    private static int Colorref(int r, int g, int b) => r | (g << 8) | (b << 16);

    private static void Set(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch (Exception)
        {
            // No dwmapi, or a build that predates the attribute. The light bar stays.
        }
    }
}
