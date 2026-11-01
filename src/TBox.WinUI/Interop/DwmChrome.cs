using System.Runtime.InteropServices;

namespace TBox.Interop;

/// <summary>
/// Windows 11 draws a 1px border around every top-level window (and gives it rounded corners).
/// On a borderless panel that border shows up as a white outline, so it is switched off here and
/// the system corner radius is enabled to match the panel's own radius.
/// </summary>
internal static class DwmChrome
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;

    private const int DWMWCP_ROUND = 2;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private const int GWL_STYLE = -16;
    private const long WS_THICKFRAME = 0x00040000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    public static void ApplyBorderless(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // Pull the resize frame into the client area, otherwise the window keeps a 2px light
        // frame on the left, right and bottom where WinUI's white window background shows.
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        var hrFrame = DwmExtendFrameIntoClientArea(hwnd, ref margins);

        var none = unchecked((int)DWMWA_COLOR_NONE);
        var hrBorder = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));

        var round = DWMWCP_ROUND;
        var hrCorner = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        // WinUI keeps WS_THICKFRAME for native resizing; that 4px frame is what insets the
        // client area and shows the white window background. TBox has its own grip, drop it.
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        if ((style & WS_THICKFRAME) != 0)
        {
            SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)(style & ~WS_THICKFRAME));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        App.Log($"[dwm] style 0x{style:X} -> 0x{(style & ~WS_THICKFRAME):X} frame hr=0x{hrFrame:X} border hr=0x{hrBorder:X} corner hr=0x{hrCorner:X}");
    }
}
