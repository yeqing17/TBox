using System.Runtime.InteropServices;

namespace TBox.Interop;

/// <summary>
/// DPI plumbing.
///
/// Everything XAML lays out is measured in device-independent pixels (DIPs), while every number this
/// app hands to Win32 or <c>AppWindow</c> — <c>MoveAndResize</c>, <c>SetWindowPos</c>,
/// <c>GetWindowRect</c>, <c>GetCursorPos</c>, <c>DisplayArea.WorkArea</c> — is a *physical* pixel on
/// the current monitor. A length that comes from a XAML constant (the capsule's 56 × N, the dialog's
/// 480, the minimum panel width) is therefore a DIP and has to be multiplied by the window's scale
/// factor before it is used as a window dimension. Skipping that step works at 100% and silently
/// clips the window's content at 125/150/200%, because the window ends up smaller than its content.
/// </summary>
internal static class Dpi
{
    private const double BaseDpi = 96.0;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForSystem();

    /// <summary>
    /// Scale factor of the monitor a window is on: 1.0 = 100%, 1.5 = 150%, 2.0 = 200%.
    /// Cheap enough to call per layout/placement (it is just a look-up plus a multiply).
    /// Falls back to the system DPI before a window exists, and to 1.0 if even that is unavailable.
    /// </summary>
    public static double ScaleFor(IntPtr hwnd)
    {
        var dpi = hwnd == IntPtr.Zero ? 0 : (int)GetDpiForWindow(hwnd);
        if (dpi <= 0) dpi = (int)GetDpiForSystem();
        return dpi > 0 ? dpi / BaseDpi : 1.0;
    }

    /// <summary>Converts a DIP length to whole physical pixels (half-up, so 56 × 1.25 = 70).</summary>
    public static int Px(double dip, double scale)
        => (int)Math.Round(dip * (scale > 0 ? scale : 1.0), MidpointRounding.AwayFromZero);

    /// <summary>Converts a physical-pixel length back to DIPs.</summary>
    public static double Dip(double px, double scale)
        => scale > 0 ? px / scale : px;
}
