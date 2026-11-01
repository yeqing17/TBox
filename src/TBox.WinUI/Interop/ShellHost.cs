using System.Runtime.InteropServices;

namespace TBox.Interop;

/// <summary>
/// A hidden message window that owns things WinUI 3 doesn't expose: the tray icon and the
/// global hotkey. Both post to this window, which forwards them as events.
/// </summary>
internal sealed class ShellHost : IDisposable
{
    private const string ClassName = "TBoxShellHost";
    private const uint WM_TRAY = 0x8001;          // WM_APP + 1
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x5442;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_T = 0x54;

    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;

    private const uint MF_STRING = 0x0;
    private const uint MF_SEPARATOR = 0x800;
    private const uint MF_CHECKED = 0x8;
    private const uint TPM_LEFTALIGN = 0x0;
    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint TPM_RETURNCMD = 0x100;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly WndProcDelegate _wndProc;
    private IntPtr _hwnd;
    private IntPtr _trayIcon;
    private bool _trayAdded;
    private bool _hotkeyRegistered;

    public event Action? HotkeyPressed;
    public event Action? TrayLeftClick;
    public event Action? TrayRightClick;

    public bool HotkeyRegistered => _hotkeyRegistered;
    public IntPtr Handle => _hwnd;

    public ShellHost()
    {
        _wndProc = Proc;

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            lpszClassName = ClassName,
            hInstance = GetModuleHandle(null)
        };
        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(0, ClassName, "TBox", 0, 0, 0, 0, 0,
            new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    public bool RegisterHotkey()
    {
        _hotkeyRegistered = RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_T);
        return _hotkeyRegistered;
    }

    public bool AddTrayIcon(string iconPath, string tooltip)
    {
        _trayIcon = LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, 0, 0,
            0x10 /* LR_LOADFROMFILE */ | 0x40 /* LR_DEFAULTSIZE */);

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = _trayIcon,
            szTip = tooltip
        };

        _trayAdded = Shell_NotifyIcon(0 /* NIM_ADD */, ref data);
        return _trayAdded;
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_trayAdded) return;
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_TIP,
            szTip = tooltip
        };
        Shell_NotifyIcon(1 /* NIM_MODIFY */, ref data);
    }

    public sealed record MenuItemSpec(string Text, int Id, bool Checked = false, bool Separator = false);

    /// <summary>Shows a native popup menu at the cursor; returns the chosen id (0 when dismissed).</summary>
    public int ShowMenu(IEnumerable<MenuItemSpec> items)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return 0;

        try
        {
            foreach (var item in items)
            {
                if (item.Separator)
                {
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                    continue;
                }

                var flags = MF_STRING | (item.Checked ? MF_CHECKED : 0);
                AppendMenu(menu, flags, (UIntPtr)item.Id, item.Text);
            }

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(menu, TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
            return command;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            return IntPtr.Zero;
        }

        if (msg == WM_TRAY)
        {
            var evt = (uint)lParam & 0xFFFF;
            if (evt == 0x0202 /* WM_LBUTTONUP */) TrayLeftClick?.Invoke();
            else if (evt == 0x0205 /* WM_RBUTTONUP */) TrayRightClick?.Invoke();
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_trayAdded)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1
            };
            Shell_NotifyIcon(2 /* NIM_DELETE */, ref data);
            _trayAdded = false;
        }

        if (_trayIcon != IntPtr.Zero) DestroyIcon(_trayIcon);
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    // ---------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? item);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved,
        IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
