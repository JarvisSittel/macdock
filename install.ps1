# Installs (or updates) MacDock and starts it at login.
# Usage:  powershell -ExecutionPolicy Bypass -File install.ps1
#
# Builds a release copy into %LOCALAPPDATA%\MacDock\app, so rebuilding the project during development never
# changes the dock you log into. Run this again whenever you want to update the installed copy.
# Settings live separately in %APPDATA%\MacDock and are kept.

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'MacDock'
$appDir = Join-Path $env:LOCALAPPDATA 'MacDock\app'
$exe = Join-Path $appDir 'MacDock.exe'

# 1. Ask any running dock to exit cleanly (it restores the Windows taskbar on the way out).
$running = Get-Process MacDock -ErrorAction SilentlyContinue | Select-Object -First 1
if ($running) {
    Write-Host 'Closing the running dock...'
    Start-Process $running.Path -ArgumentList '--quit' -Wait
    for ($i = 0; $i -lt 20 -and (Get-Process MacDock -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    if (Get-Process MacDock -ErrorAction SilentlyContinue) { throw 'The dock did not exit; close it from its right-click menu and run this again.' }
}

# 2. Publish a release build.
Write-Host "Building release copy into $appDir ..."
dotnet publish $project -c Release -o $appDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# 3. Start at login (per-user; no admin rights needed).
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'MacDock' -Value "`"$exe`""
Write-Host 'Set to start at login.'

# 4. Start it now.
Start-Process $exe
Write-Host 'MacDock installed and running.'
