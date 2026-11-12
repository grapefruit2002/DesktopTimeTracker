param([string]$ProcessName = "DesktopTimeTracker")
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Wm {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@
$p = Get-Process $ProcessName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO WINDOW"; exit 1 }
[Wm]::PostMessage($p.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null  # WM_CLOSE
Start-Sleep -Milliseconds 800
$alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
Write-Output ("after WM_CLOSE: alive={0} pid={1}" -f ([bool]$alive), $p.Id)
