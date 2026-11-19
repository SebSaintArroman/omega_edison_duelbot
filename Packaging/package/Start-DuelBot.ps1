# Starts YGO Omega (if it isn't running), the bot (if it isn't running), then opens the AI Room page.
# The bot runs in its own console window; close that window to stop it.

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$botDir  = Join-Path $root 'bot'
$pageUrl = 'http://localhost:58966/'
Add-Type -AssemblyName System.Windows.Forms

$settings = $null
$settingsPath = Join-Path $root 'settings.json'
if (Test-Path $settingsPath) { $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json }

# 0. Updates: a newer GitHub release than this install (tag.txt, build-<n>) is offered before anything starts. No tag.txt
# (a local build) or no internet: skipped.
function Get-BuildNumber($tag) { if ($tag -match '^build-(\d+)$') { [int]$Matches[1] } else { -1 } }
$tagPath = Join-Path $root 'tag.txt'
$botRunning = Get-Process -Name 'BotTest' -ErrorAction SilentlyContinue
if ((Test-Path $tagPath) -and -not $botRunning) {
    try {
        $current = (Get-Content $tagPath -Raw).Trim()
        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/SebSaintArroman/omega_edison_duelbot/releases/latest' `
            -Headers @{ 'User-Agent' = 'DuelBot-updater' } -TimeoutSec 5
        $asset = $release.assets | Where-Object { $_.name -eq 'DuelBot-Setup.exe' } | Select-Object -First 1
        if ($asset -and (Get-BuildNumber $release.tag_name) -gt (Get-BuildNumber $current)) {
            $answer = [System.Windows.Forms.MessageBox]::Show(
                "A new Duel Bot version is out ($($release.tag_name); you have $current).`n`nUpdate now? It takes a minute, keeps your settings and logs, then starts the bot.",
                'Duel Bot update', 'YesNo', 'Question')
            if ($answer -eq 'Yes') {
                $setup = Join-Path $env:TEMP 'DuelBot-Setup.exe'
                Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $setup -UseBasicParsing
                Start-Process -FilePath $setup
                exit 0
            }
        }
    } catch {
        # No update check this time (offline, GitHub unreachable): play the installed version.
    }
}

# 1. YGO Omega.
if (-not (Get-Process -Name 'YGO Omega' -ErrorAction SilentlyContinue)) {
    if ($settings -and $settings.OmegaPath -and (Test-Path $settings.OmegaPath)) {
        Start-Process -FilePath $settings.OmegaPath -WorkingDirectory (Split-Path $settings.OmegaPath)
    } else {
        [System.Windows.Forms.MessageBox]::Show('YGO Omega isn''t set up: run Setup.bat in the Duel Bot folder, or start Omega yourself.', 'Duel Bot') | Out-Null
    }
}

# 2. The bot. Its logs and comments go to bot\duel-logs.
function Test-Page { try { Invoke-WebRequest -Uri $pageUrl -UseBasicParsing -TimeoutSec 2 | Out-Null; $true } catch { $false } }
if (-not (Test-Page)) {
    $env:ASPNETCORE_URLS = 'http://localhost:58966'
    Start-Process -FilePath (Join-Path $botDir 'BotTest.exe') -WorkingDirectory $botDir
    $deadline = (Get-Date).AddMinutes(2)
    while (-not (Test-Page)) {
        if ((Get-Date) -gt $deadline) {
            [System.Windows.Forms.MessageBox]::Show('The bot didn''t start within 2 minutes. Check its console window for an error.', 'Duel Bot') | Out-Null
            exit 1
        }
        Start-Sleep -Milliseconds 750
    }
}

# 3. The AI Room page.
Start-Process $pageUrl
