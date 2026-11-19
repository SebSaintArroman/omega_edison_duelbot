# Duel Bot setup: run once (double-click Setup.bat). It:
#  1. installs the Microsoft Visual C++ runtime (64-bit) if it's missing (the rules engine needs it),
#  2. finds YGO Omega (or asks where it is),
#  3. asks for a name to put on your log exports,
#  4. makes two desktop shortcuts: "Duel Bot" and "Duel Bot - Export Logs".
# The bot itself needs no .NET install: it ships with its own runtime.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$settingsPath = Join-Path $root 'settings.json'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName Microsoft.VisualBasic

Write-Host '== Duel Bot setup ==' -ForegroundColor Cyan

# 1. Visual C++ runtime (x64).
$vc = Join-Path $env:SystemRoot 'System32\vcruntime140.dll'
if (-not (Test-Path $vc)) {
    Write-Host 'Installing the Microsoft Visual C++ runtime (64-bit)...'
    $installer = Join-Path $env:TEMP 'vc_redist.x64.exe'
    Invoke-WebRequest 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $installer -UseBasicParsing
    Start-Process $installer -ArgumentList '/install', '/quiet', '/norestart' -Wait -Verb RunAs
} else {
    Write-Host 'Visual C++ runtime: already installed.'
}

# 2. YGO Omega.
$omega = $null
$candidates = @(
    'C:\Program Files (x86)\YGO Omega\YGO Omega.exe',
    'C:\Program Files\YGO Omega\YGO Omega.exe',
    (Join-Path $env:LOCALAPPDATA 'YGO Omega\YGO Omega.exe')
)
$running = Get-Process -Name 'YGO Omega' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($running -and $running.Path) { $candidates = @($running.Path) + $candidates }
foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { $omega = $c; break } }
if (-not $omega) {
    [System.Windows.Forms.MessageBox]::Show("YGO Omega wasn't found in the usual places. Please pick 'YGO Omega.exe' next.", 'Duel Bot setup') | Out-Null
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Filter = 'YGO Omega|YGO Omega.exe|Programs (*.exe)|*.exe'
    $dialog.Title = 'Where is YGO Omega.exe?'
    if ($dialog.ShowDialog() -eq 'OK') { $omega = $dialog.FileName }
}
if ($omega) { Write-Host "YGO Omega: $omega" } else { Write-Host 'YGO Omega: not set (start it yourself before the bot).' -ForegroundColor Yellow }

# 3. A name for the log exports (only used in the export file's name, so the logs can be told apart).
$name = [Microsoft.VisualBasic.Interaction]::InputBox('A name or nickname for your log exports (only used in the file name):', 'Duel Bot setup', $env:USERNAME)
$name = ($name -replace '[^A-Za-z0-9_-]', '')
if (-not $name) { $name = 'player' }

@{ OmegaPath = $omega; Name = $name } | ConvertTo-Json | Set-Content $settingsPath -Encoding utf8

# 4. Desktop shortcuts.
$desktop = [Environment]::GetFolderPath('Desktop')
$shell = New-Object -ComObject WScript.Shell
function New-Shortcut($file, $script, $description) {
    $lnk = $shell.CreateShortcut((Join-Path $desktop $file))
    $lnk.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $lnk.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $root $script)`""
    $lnk.WorkingDirectory = $root
    if ($omega) { $lnk.IconLocation = "$omega,0" }
    $lnk.Description = $description
    $lnk.Save()
}
New-Shortcut 'Duel Bot.lnk' 'Start-DuelBot.ps1' 'Start YGO Omega, the duel bot and the AI Room page'
New-Shortcut 'Duel Bot - Export Logs.lnk' 'Export-Logs.ps1' 'Zip the duel logs and comments to send back'

Write-Host ''
Write-Host 'Setup done. Use the "Duel Bot" shortcut on your desktop to play.' -ForegroundColor Green
Write-Host 'To send your logs back: "Duel Bot - Export Logs" puts a zip on your desktop.'
Read-Host 'Press Enter to close'
