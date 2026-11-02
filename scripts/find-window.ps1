param(
    [Parameter(Mandatory=$true)][int]$ProcId,
    [Parameter(Mandatory=$true)][int]$Width,
    [Parameter(Mandatory=$true)][int]$Height,
    [int]$Tolerance = 12
)
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public class FW {
    public delegate bool Proc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(Proc p, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
$found = [IntPtr]::Zero
$cb = [FW+Proc]{
    param($h, $l)
    $p = 0
    [FW]::GetWindowThreadProcessId($h, [ref]$p) | Out-Null
    if ($p -ne $ProcId -or -not [FW]::IsWindowVisible($h)) { return $true }
    $r = New-Object FW+RECT
    [FW]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $hg = $r.Bottom - $r.Top
    if ([Math]::Abs($w - $Width) -le $Tolerance -and [Math]::Abs($hg - $Height) -le $Tolerance) {
        $script:found = $h
        return $false
    }
    return $true
}
[FW]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
if ($script:found -eq [IntPtr]::Zero) { throw "window not found (${Width}x${Height})" }
'0x{0:X}' -f $script:found.ToInt64()
