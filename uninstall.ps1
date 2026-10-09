# Stops MacDock, removes it from login, and deletes the installed copy in %LOCALAPPDATA%\MacDock\app.
# Your settings in %APPDATA%\MacDock are left alone.
# Usage:  powershell -ExecutionPolicy Bypass -File uninstall.ps1

$appDir = Join-Path $env:LOCALAPPDATA 'MacDock\app'

$running = Get-Process MacDock -ErrorAction SilentlyContinue | Select-Object -First 1
if ($running) {
    Start-Process $running.Path -ArgumentList '--quit' -Wait
    Start-Sleep -Seconds 2
}

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'MacDock' -ErrorAction SilentlyContinue
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
Write-Host 'MacDock removed from login and uninstalled. The Windows taskbar is back.'
