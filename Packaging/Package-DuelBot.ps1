# Builds the shareable Duel Bot package: dist\DuelBot-<date>.zip (one zip to hand over).
# Run it on the development machine from the repo (it needs the .NET SDK, which you already have):
#   powershell -ExecutionPolicy Bypass -File Packaging\Package-DuelBot.ps1
# Inside the zip:
#   Setup.bat              run once: checks the Visual C++ runtime, finds YGO Omega, makes the desktop shortcuts
#   Start Duel Bot.bat     starts Omega (if needed), the bot, and the AI Room page
#   Export Logs.bat        zips the duel logs + comments onto the Desktop, to send back
#   bot\                   the bot (self-contained: no .NET install needed)
#   DuelSim\               the rules engine's card database and scripts (the bot's play search reads them)

$ErrorActionPreference = 'Stop'
$repo  = Split-Path $PSScriptRoot
$stamp = Get-Date -Format 'yyyy-MM-dd_HHmm'
$dist  = Join-Path $repo 'dist'
$out   = Join-Path $dist 'DuelBot'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

# 1. The bot, published self-contained for 64-bit Windows (the rules engine is 64-bit since 2026-11-19).
Write-Host 'Publishing the bot...'
dotnet publish (Join-Path $repo 'BotTest\BotTest.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $out 'bot')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
$engine = Join-Path $repo 'DuelSim\vendor\ocgcore-x64\ocgcore.dll'
if (-not (Test-Path (Join-Path $out 'bot\ocgcore.dll'))) { Copy-Item $engine (Join-Path $out 'bot') }

# 2. The card database and scripts. The bot finds them through the DuelSim folder next to its own (DuelSim.csproj marks it).
Write-Host 'Copying the card database and scripts...'
$sim = Join-Path $out 'DuelSim'
New-Item -ItemType Directory -Force (Join-Path $sim 'vendor\cdb') | Out-Null
Copy-Item (Join-Path $repo 'DuelSim\DuelSim.csproj') $sim
Copy-Item (Join-Path $repo 'DuelSim\vendor\cdb\cards.cdb') (Join-Path $sim 'vendor\cdb')
Copy-Item (Join-Path $repo 'DuelSim\vendor\scripts') (Join-Path $sim 'vendor') -Recurse
if (Test-Path (Join-Path $repo 'DuelSim\scripts')) { Copy-Item (Join-Path $repo 'DuelSim\scripts') $sim -Recurse }
# Cards newer than the vendored database (Mutiny in the Sky): loaded after it.
if (Test-Path (Join-Path $repo 'DuelSim\cards-extra.cdb')) { Copy-Item (Join-Path $repo 'DuelSim\cards-extra.cdb') $sim }

# 3. The user-facing scripts and the readme.
Copy-Item (Join-Path $PSScriptRoot 'package\*') $out -Recurse

# 4. Which build this is (goes into every log export, so logs can be matched to the code that played them).
$commit = try { (git -C $repo rev-parse --short HEAD 2>$null) } catch { 'unknown' }
$dirty  = try { if (git -C $repo status --porcelain 2>$null) { ' (with uncommitted changes)' } else { '' } } catch { '' }
"Duel Bot build $stamp, commit $commit$dirty" | Set-Content (Join-Path $out 'version.txt') -Encoding utf8
# The release tag (GitHub builds set DUELBOT_TAG = build-<n>): the installed bot compares it with the latest release to offer updates.
# A local build has none, so it never offers one.
if ($env:DUELBOT_TAG) { $env:DUELBOT_TAG | Set-Content (Join-Path $out 'tag.txt') -Encoding ascii }

# 5. One zip.
$zip = Join-Path $dist "DuelBot-$stamp.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Write-Host 'Zipping...'
# .NET's zip, not Compress-Archive (Windows PowerShell's took over 10 minutes on the ~13,000 card scripts). The DuelBot
# folder stays the zip's root, as the installer expects.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$size = '{0:N0} MB' -f ((Get-Item $zip).Length / 1MB)
Write-Host "Done: $zip ($size)"
