# Starts everything for a bot session: YGO Omega (if it isn't running) and the BotTest bot server, restarting the bot if
# it's already running (so a fresh build is picked up), then opens the AI Room page, or reloads it if one is open already.
# Omega starts fullscreen.
# The desktop shortcut "Duel Bot" runs this.
#   -Build   rebuild first (default: start the last build as is; you rebuild yourself)
# Every launch writes what it did to launcher.log next to this script.
param([switch]$Build)

$ErrorActionPreference = 'Stop'
$repo     = $PSScriptRoot
$project  = Join-Path $repo 'BotTest'
$omegaExe = 'C:\Program Files (x86)\YGO Omega\YGO Omega.exe'
$pageUrl  = 'http://localhost:58966/'
$probeUrl = 'http://127.0.0.1:58966/api/state'
$pageOpenUrl = 'http://127.0.0.1:58966/api/page-open'
$pageReloadUrl = 'http://127.0.0.1:58966/api/page-reload'
# Omega is a Unity game: these open it as a borderless fullscreen window.
$omegaArgs = @('-screen-fullscreen', '1', '-window-mode', 'borderless')
$logFile  = Join-Path $repo 'launcher.log'
Add-Type -AssemblyName System.Windows.Forms

function Log($text) { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')  $text" | Add-Content $logFile -Encoding utf8 }
function Fail($text) {
    Log "FAILED: $text"
    [System.Windows.Forms.MessageBox]::Show("$text`n`nDetails: $logFile", 'Duel Bot') | Out-Null
    exit 1
}
function Test-Page {
    try { Invoke-WebRequest -Uri $probeUrl -UseBasicParsing -TimeoutSec 3 | Out-Null; return $true } catch { return $false }
}
# Whether an AI Room page has polled the bot within the last $withinMs (the page polls every second; a background tab's
# timers can be throttled to about once a minute). $null when the bot can't say (not running, or an older build).
function Test-PageOpen($withinMs) {
    try {
        $r = Invoke-RestMethod -Uri "$($pageOpenUrl)?withinMs=$withinMs" -TimeoutSec 3
        return [bool]$r.open
    } catch { return $null }
}

try {
    Log '--- launch'

    # 1. Omega
    if (Get-Process -Name 'YGO Omega' -ErrorAction SilentlyContinue) {
        Log 'Omega already running'
    } elseif (Test-Path $omegaExe) {
        Start-Process -FilePath $omegaExe -ArgumentList $omegaArgs -WorkingDirectory (Split-Path $omegaExe)
        Log 'started Omega (fullscreen)'
    } else {
        Log "Omega not found at $omegaExe"
    }

    # 2. A bot already running: note whether a page is open on it, then stop it (a restart picks up the latest build).
    $pageWasOpen = $false
    $existing = Get-Process -Name 'BotTest' -ErrorAction SilentlyContinue
    if ($existing) {
        $pageWasOpen = (Test-PageOpen 70000) -eq $true
        Log "bot running (pid $($existing.Id -join ', ')), page open on it: $pageWasOpen; restarting it"
        $existing | Stop-Process -Force
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Process -Name 'BotTest' -ErrorAction SilentlyContinue) -or (Test-Page)) {
            if ((Get-Date) -gt $deadline) { Fail 'The running bot did not stop within 15 seconds.' }
            Start-Sleep -Milliseconds 300
        }
        Log 'old bot stopped'
    }

    # 3. Start the bot server, in its own console window (close that window to stop the bot).
    # The Release build: optimised, so the play search runs much faster than in a Debug build.
    $exe = Join-Path $project 'bin\Release\net9.0\win-x64\BotTest.exe'
    if (-not $Build -and (Test-Path $exe)) {
        $env:ASPNETCORE_URLS = 'http://localhost:58966'
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        Start-Process -FilePath $exe -WorkingDirectory $project
        Log "started $exe (last build from $((Get-Item $exe).LastWriteTime))"
    } else {
        $runArgs = @('run', '--project', "`"$project`"", '-c', 'Release', '--launch-profile', 'BotTest')
        Start-Process -FilePath 'dotnet' -ArgumentList $runArgs -WorkingDirectory $project
        Log 'started dotnet run (builds first)'
    }
    $deadline = (Get-Date).AddMinutes(3)
    while (-not (Test-Page)) {
        if ((Get-Date) -gt $deadline) { Fail "The bot didn't answer on $pageUrl within 3 minutes. Check its console window." }
        if (-not (Get-Process -Name 'BotTest' -ErrorAction SilentlyContinue) -and -not (Get-Process -Name 'dotnet' -ErrorAction SilentlyContinue)) {
            Start-Sleep -Seconds 2
            if (-not (Get-Process -Name 'BotTest' -ErrorAction SilentlyContinue) -and -not (Test-Page)) {
                Fail 'The bot stopped while starting. Run it from the BotTest folder to see the error.'
            }
        }
        Start-Sleep -Milliseconds 750
    }
    Log 'bot answering'

    # 4. The AI Room page: only if none is open. An open page reconnects to the new bot by itself; give it a few seconds
    #    to poll (the old bot's answer covers a throttled background tab).
    $open = $pageWasOpen
    if (-not $open) {
        $deadline = (Get-Date).AddSeconds(6)
        while (-not $open -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            $open = (Test-PageOpen 5000) -eq $true
        }
    }
    if ($open) {
        try {
            Invoke-RestMethod -Method Post -Uri $pageReloadUrl -TimeoutSec 3 | Out-Null
            Log 'a page is already open: asked it to reload'
        } catch {
            Log "a page is already open; reload request failed: $($_.Exception.Message)"
        }
    } else {
        Start-Process $pageUrl
        Log "opened $pageUrl"
    }
} catch {
    Fail "Launcher error: $($_.Exception.Message)"
}
