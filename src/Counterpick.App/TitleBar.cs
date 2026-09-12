using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Counterpick.App;

/// <summary>
/// Paints what is left of the native window chrome in the page's colours.
///
/// The window is frameless now - MainWindow.xaml hands the caption to the page - so the
/// caption and text colours below never show. They are kept because they are the
/// fallback: if WindowChrome is ever removed, or a Windows build refuses it, the caption
/// comes back and it should come back dark rather than white.
///
/// What still matters every run is the border colour: the 1px line Windows draws around
/// the window, which is the only chrome a frameless window has left, and dark mode, which
/// the system uses for the resize shadow and the right-click system menu. Every call is
/// best-effort: an older build refuses the attribute, which is cosmetic.
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
        Set(hwnd, DWMWA_CAPTION_COLOR, Colorref(0x01, 0x0A, 0x13)); // --ground, if a caption ever returns
        Set(hwnd, DWMWA_TEXT_COLOR, Colorref(0xC8, 0xAA, 0x6E));    // --gold, the wordmark
        Set(hwnd, DWMWA_BORDER_COLOR, Colorref(0x78, 0x5A, 0x28));  // --line-hard, the frame gold
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
