param([string]$Out = "F:\zcode\TBox\.shots\winui-panel.png", [string]$ProcessName = "TBox")
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public class EP {
  public delegate bool Proc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(Proc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, Proc p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder b, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  public static IntPtr FindChild(uint pid, out string topClass) {
    IntPtr found = IntPtr.Zero; string cls = "";
    EnumWindows((w,l) => {
      var sb = new StringBuilder(64); GetClassName(w, sb, 64);
      var top = sb.ToString();
      EnumChildWindows(w, (c,ll) => {
        uint p; GetWindowThreadProcessId(c, out p);
        if (p == pid) { found = c; cls = top; return false; }
        return true;
      }, IntPtr.Zero);
      return found == IntPtr.Zero;
    }, IntPtr.Zero);
    topClass = cls;
    return found;
  }
}
'@
$proc = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Error "no process"; exit 1 }
$top = ""
$hwnd = [EP]::FindChild([uint32]$proc.Id, [ref]$top)
if ($hwnd -eq 0) { Write-Error "no child window found"; exit 1 }
$r = New-Object EP+RECT
[EP]::GetWindowRect($hwnd, [ref]$r) | Out-Null
"hwnd=0x$($hwnd.ToString('X')) under=$top rect=$($r.Left),$($r.Top) $($r.Right-$r.Left)x$($r.Bottom-$r.Top)"
$bmp = New-Object System.Drawing.Bitmap ([Math]::Max(1,$r.Right-$r.Left)), ([Math]::Max(1,$r.Bottom-$r.Top))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc(); $ok = [EP]::PrintWindow($hwnd, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out); $bmp.Dispose()
"printwindow=$ok saved=$Out"
