# Zips the duel logs and comments onto the Desktop as DuelBot-logs-<name>-<date>.zip, to send back by hand.
# The zip holds bot\duel-logs (every duel's log, the comments box's comments.jsonl) and version.txt (which build played them).

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$logs = Join-Path $root 'bot\duel-logs'
Add-Type -AssemblyName System.Windows.Forms

if (-not (Test-Path $logs) -or -not (Get-ChildItem $logs -File -ErrorAction SilentlyContinue)) {
    [System.Windows.Forms.MessageBox]::Show('There are no duel logs yet: play a duel against the bot first.', 'Duel Bot') | Out-Null
    exit 0
}

$name = 'player'
$settingsPath = Join-Path $root 'settings.json'
if (Test-Path $settingsPath) { $s = Get-Content $settingsPath -Raw | ConvertFrom-Json; if ($s.Name) { $name = $s.Name } }

$stamp   = Get-Date -Format 'yyyy-MM-dd_HHmm'
$staging = Join-Path $env:TEMP "DuelBot-logs-$name-$stamp"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item (Join-Path $logs '*') $staging -Recurse
if (Test-Path (Join-Path $root 'version.txt')) { Copy-Item (Join-Path $root 'version.txt') $staging }

$zip = Join-Path ([Environment]::GetFolderPath('Desktop')) "DuelBot-logs-$name-$stamp.zip"
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -Force
Remove-Item $staging -Recurse -Force

[System.Windows.Forms.MessageBox]::Show("Saved to your desktop:`n$(Split-Path $zip -Leaf)`n`nSend that file back.", 'Duel Bot') | Out-Null
Start-Process explorer.exe "/select,`"$zip`""
