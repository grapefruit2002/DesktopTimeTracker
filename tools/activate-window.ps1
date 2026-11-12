param([string]$ProcessName = "msedge")
$proc = Get-Process $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "NO WINDOW for $ProcessName"; exit 1 }
$ws = New-Object -ComObject WScript.Shell
$ok = $ws.AppActivate($proc.Id)
Start-Sleep -Milliseconds 600
Write-Output ("activated={0} pid={1} title='{2}'" -f $ok, $proc.Id, $proc.MainWindowTitle)