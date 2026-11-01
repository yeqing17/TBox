using System.Runtime.InteropServices;

namespace TBox.Interop;

/// <summary>
/// Makes WinUI windows truly borderless.
///
/// Closing the title bar through OverlappedPresenter is not enough: WinUI's window procedure still
/// reports an 8px non-client frame from WM_NCCALCSIZE, and that band is painted with the window
/// background, which shows up as a light outline. Subclassing the window procedure and returning 0
/// for WM_NCCALCSIZE makes client area == window.
///
/// The original procedures are tracked per HWND — TBox has two windows (panel + capsule) and a
/// single shared field would make one window chain into the other's procedure and recurse.
/// </summary>
internal static class WindowChrome
{
    private const int GWLP_WNDPROC = -4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;

    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOZORDER = 0x0004;
    private const int HWND_BOTTOM = 1;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    private static readonly Dictionary<IntPtr, IntPtr> OriginalProcedures = new();
    private static WndProc? _replacement;
    private static IntPtr _panelHandle;

    /// <summary>When true the panel stays at the bottom of the z-order (desktop mode).</summary>
    public static bool KeepAtBottom { get; set; }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    public static void RemoveNonClientFrame(IntPtr hwnd, bool isPanel = false)
    {
        if (hwnd == IntPtr.Zero) return;
        if (isPanel) _panelHandle = hwnd;
        if (OriginalProcedures.ContainsKey(hwnd)) return;

        _replacement ??= Proc;

        // Register before swapping: messages can arrive immediately after SetWindowLongPtr and the
        // hook must already know the previous procedure, otherwise window creation is swallowed.
        var original = GetWindowLongPtr(hwnd, GWLP_WNDPROC);
        OriginalProcedures[hwnd] = original;
        SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_replacement));

        // Keep these windows out of Alt+Tab / the taskbar: they behave like system flyouts.
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)exStyle);

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        App.Log($"[chrome] subclassed hwnd=0x{hwnd:X} old=0x{original:X}");
    }

    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!OriginalProcedures.TryGetValue(hwnd, out var original))
            return DefWindowProc(hwnd, msg, wParam, lParam);

        if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
            return IntPtr.Zero;   // client area covers the whole window

        if (msg == WM_WINDOWPOSCHANGING && KeepAtBottom && hwnd == _panelHandle)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.hwndInsertAfter = new IntPtr(HWND_BOTTOM);
            pos.flags &= ~SWP_NOZORDER;
            Marshal.StructureToPtr(pos, lParam, false);
        }

        return CallWindowProc(original, hwnd, msg, wParam, lParam);
    }
}
