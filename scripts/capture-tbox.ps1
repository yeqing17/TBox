param([string]$Out = "F:\zcode\TBox\.shots\winui-panel.png", [string]$ProcessName = "TBox")
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class TW {
  public delegate bool Proc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(Proc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, Proc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  public static IntPtr Best(uint pid) {
    IntPtr best = IntPtr.Zero; long bestArea = 0;
    Action<IntPtr> consider = (h) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid || !IsWindowVisible(h)) return;
      RECT r; if (!GetWindowRect(h, out r)) return;
      long w = r.Right - r.Left, ht = r.Bottom - r.Top;
      if (w <= 0 || ht <= 0) return;
      long area = w * ht;
      if (area > bestArea) { bestArea = area; best = h; }
    };
    EnumWindows((w,l) => {
      consider(w);
      EnumChildWindows(w, (c,ll) => { consider(c); return true; }, IntPtr.Zero);
      return true;
    }, IntPtr.Zero);
    return best;
  }
}
'@
$proc = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Error "no process"; exit 1 }
$hwnd = [TW]::Best([uint32]$proc.Id)
if ($hwnd -eq 0) { Write-Error "no window"; exit 1 }
$r = New-Object TW+RECT
[TW]::GetWindowRect($hwnd, [ref]$r) | Out-Null
"hwnd=0x$($hwnd.ToString('X')) rect=$($r.Left),$($r.Top) $($r.Right-$r.Left)x$($r.Bottom-$r.Top)"
$bmp = New-Object System.Drawing.Bitmap ([Math]::Max(1,$r.Right-$r.Left)), ([Math]::Max(1,$r.Bottom-$r.Top))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc(); $ok = [TW]::PrintWindow($hwnd, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out); $bmp.Dispose()
"printwindow=$ok saved=$Out"
