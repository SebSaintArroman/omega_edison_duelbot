# Downloads everything the simulator needs into DuelSim/vendor (gitignored). Versions are pinned together:
# the engine DLL from the EDOPro 41.0.2 release (April 2025) and the card scripts / card database / engine headers
# committed just before it, so scripts and engine agree.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$vendor = Join-Path $root 'vendor'
New-Item -ItemType Directory -Force $vendor | Out-Null
$tmp = Join-Path $vendor '_download'
New-Item -ItemType Directory -Force $tmp | Out-Null

$coreCommit    = 'b3c846db2d22a3908455e98a11e522abfd95b075'   # edo9300/ygopro-core, 2025-04-27
$scriptsCommit = '92f3e36b33de65b64971f312f7a732d22b093bbf'   # ProjectIgnis/CardScripts, 2025-04-28
$cdbCommit     = '0d454e6fac9de8acc2e2c3bd6dff19041bc3e3d1'   # ProjectIgnis/BabelCDB

# 1. Engine (32-bit ocgcore.dll from the EDOPro Windows update package)
if (-not (Test-Path "$vendor\ocgcore\ocgcore.dll")) {
    Write-Host 'Downloading ocgcore.dll (EDOPro 41.0.2)...'
    Invoke-WebRequest 'https://github.com/ProjectIgnis/edopro-assets/releases/download/41.0.2/IgnisUpdate-41.0.2-windows.zip' -OutFile "$tmp\update.zip"
    Expand-Archive "$tmp\update.zip" -DestinationPath "$tmp\update" -Force
    New-Item -ItemType Directory -Force "$vendor\ocgcore" | Out-Null
    Copy-Item "$tmp\update\ocgcore.dll" "$vendor\ocgcore\ocgcore.dll"
}

# 2. Card scripts
if (-not (Test-Path "$vendor\scripts\constant.lua")) {
    Write-Host 'Downloading card scripts...'
    Invoke-WebRequest "https://codeload.github.com/ProjectIgnis/CardScripts/zip/$scriptsCommit" -OutFile "$tmp\scripts.zip"
    Expand-Archive "$tmp\scripts.zip" -DestinationPath "$tmp\scripts" -Force
    Move-Item "$tmp\scripts\CardScripts-$scriptsCommit" "$vendor\scripts"
}

# 3. Card database
if (-not (Test-Path "$vendor\cdb\cards.cdb")) {
    Write-Host 'Downloading card database...'
    New-Item -ItemType Directory -Force "$vendor\cdb" | Out-Null
    Invoke-WebRequest "https://raw.githubusercontent.com/ProjectIgnis/BabelCDB/$cdbCommit/cards.cdb" -OutFile "$vendor\cdb\cards.cdb"
}

# 4. Engine headers/sources (reference only: message and response formats)
if (-not (Test-Path "$vendor\core-src\ocgapi.h")) {
    New-Item -ItemType Directory -Force "$vendor\core-src" | Out-Null
    foreach ($f in 'ocgapi.h','ocgapi_types.h','common.h','playerop.cpp','card.cpp','field.cpp','processor.cpp','operations.cpp','ocgapi.cpp','duel.cpp','duel.h','interpreter.cpp') {
        Invoke-WebRequest "https://raw.githubusercontent.com/edo9300/ygopro-core/$coreCommit/$f" -OutFile "$vendor\core-src\$f"
    }
}

Remove-Item -Recurse -Force $tmp
Write-Host 'Done. Build with: dotnet build DuelSim\DuelSim.csproj'
