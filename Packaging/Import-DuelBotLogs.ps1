# Unpacks a log export someone sent back (DuelBot-logs-<name>-<date>.zip) into BotTest\duel-logs\imported\<name>-<date>\,
# where the duel logs and comments.jsonl can be read like local ones.
#   powershell -ExecutionPolicy Bypass -File Packaging\Import-DuelBotLogs.ps1 C:\path\to\DuelBot-logs-alex-2026-11-20_1930.zip
param([Parameter(Mandatory)][string]$Zip)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$name = [IO.Path]::GetFileNameWithoutExtension($Zip) -replace '^DuelBot-logs-', ''
$dest = Join-Path $repo "BotTest\duel-logs\imported\$name"
if (Test-Path $dest) { throw "Already imported: $dest" }
New-Item -ItemType Directory -Force $dest | Out-Null
Expand-Archive -Path $Zip -DestinationPath $dest
$logs = (Get-ChildItem $dest -Filter *.log).Count
$comments = if (Test-Path (Join-Path $dest 'comments.jsonl')) { (Get-Content (Join-Path $dest 'comments.jsonl')).Count } else { 0 }
$version = if (Test-Path (Join-Path $dest 'version.txt')) { Get-Content (Join-Path $dest 'version.txt') } else { 'unknown build' }
Write-Host "Imported to $dest"
Write-Host "  $logs duel log(s), $comments comment(s); $version"
