# One-off migration helper: repoint the auto-start Run value to the D-drive exe path.
$exe = 'D:\DesktopTimeTracker\src\DesktopTimeTracker\bin\Debug\net10.0-windows\DesktopTimeTracker.exe'
if (-not (Test-Path $exe)) { Write-Output "MISSING EXE: $exe"; exit 1 }
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty -Path $key -Name 'DesktopTimeTracker' -Value "`"$exe`" --minimized"
Write-Output ("updated: " + (Get-ItemProperty -Path $key -Name 'DesktopTimeTracker').DesktopTimeTracker)
