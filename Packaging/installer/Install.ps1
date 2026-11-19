# Duel Bot one-click installer (runs from inside DuelBot-Setup.exe).
# Installs to %LOCALAPPDATA%\Programs\DuelBot (no admin needed), keeping an earlier install's settings and duel logs,
# then runs the normal setup (Visual C++ runtime, YGO Omega's location, a name for log exports, desktop shortcuts).

$ErrorActionPreference = 'Stop'
$zip = Join-Path $PSScriptRoot 'DuelBot.zip'
$target = Join-Path $env:LOCALAPPDATA 'Programs\DuelBot'
$temp = Join-Path $env:TEMP ("DuelBot-install-" + [guid]::NewGuid().ToString('N'))

try {
    Write-Host '== Duel Bot installer ==' -ForegroundColor Cyan
    # A running bot locks its files: ask to close it first.
    while (Get-Process -Name 'BotTest' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($target, 'OrdinalIgnoreCase') }) {
        Read-Host 'The Duel Bot is running. Close its console window, then press Enter'
    }

    $updating = Test-Path (Join-Path $target 'settings.json')
    Write-Host 'Unpacking...'
    Expand-Archive -Path $zip -DestinationPath $temp -Force
    $source = Join-Path $temp 'DuelBot'

    Write-Host "Installing to $target ..."
    New-Item -ItemType Directory -Force $target | Out-Null
    # Copies the new files over the old ones; anything else already there (settings.json, duel logs, comments) stays.
    robocopy $source $target /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying the files failed (robocopy code $LASTEXITCODE)" }

    # An update (already set up: settings.json kept from before): no questions, just start the bot again. A first install: the setup.
    if ($updating) {
        Write-Host 'Updated. Starting the Duel Bot...' -ForegroundColor Green
        Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$(Join-Path $target 'Start-DuelBot.ps1')`""
    } else {
        & (Join-Path $target 'Setup.ps1')
    }
}
catch {
    Write-Host ''
    Write-Host "Install failed: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}
finally {
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
