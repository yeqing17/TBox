param(
    [string]$Out = "F:\zcode\TBox\.shots\shot.png",
    [string]$ProcessName = "TBox",
    [int]$DelayMs = 1200
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class NativeWin {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[NativeWin]::SetProcessDPIAware() | Out-Null

Start-Sleep -Milliseconds $DelayMs

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Error "process '$ProcessName' has no visible window"; exit 1 }

$hwnd = $proc.MainWindowHandle
$rect = New-Object NativeWin+RECT
[NativeWin]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

$dir = Split-Path -Parent $Out
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)

# PW_RENDERFULLCONTENT renders DWM-composed content even when occluded.
$hdc = $g.GetHdc()
$ok = [NativeWin]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)

if (-not $ok) {
    [NativeWin]::ShowWindow($hwnd, 9) | Out-Null
    [NativeWin]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 400
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
}

$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Output "saved $Out ($w x $h) printwindow=$ok"
