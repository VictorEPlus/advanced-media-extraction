# Captures a running app window to a PNG, at full resolution and even when the window is covered.
# Usage: tools\screenshot\capture-window.ps1 -ProcessId <pid> -Out shot.png
param([int]$ProcessId, [string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public struct RECT { public int L, T, R, B; }
}
"@
[void][Win]::SetProcessDPIAware()
$p = Get-Process -Id $ProcessId
$h = $p.MainWindowHandle
$r = New-Object Win+RECT; [void][Win]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $hgt = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
[void][Win]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
$bmp.Save($Out); $bmp.Dispose(); "saved $w x $hgt"
