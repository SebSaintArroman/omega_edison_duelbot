# Builds the one-click installer: dist\DuelBot-Setup-<date>.exe.
# Run it on the development machine from the repo:
#   powershell -ExecutionPolicy Bypass -File Packaging\Build-Installer.ps1
# It makes the package zip (Package-DuelBot.ps1), then wraps it with installer\Install.ps1 into one self-extracting exe
# using IExpress (built into Windows). On the other PC: double-click the exe. It installs to
# %LOCALAPPDATA%\Programs\DuelBot (no admin needed; an earlier install's settings and logs are kept) and runs Setup.
# The exe isn't signed, so Windows SmartScreen may warn: "More info" -> "Run anyway".

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$dist = Join-Path $repo 'dist'

# 1. The package zip.
& (Join-Path $PSScriptRoot 'Package-DuelBot.ps1')
$zip = Get-ChildItem $dist -Filter 'DuelBot-*.zip' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $zip) { throw 'no package zip was made' }
$stamp = $zip.BaseName.Substring('DuelBot-'.Length)

# 2. The files IExpress packs (flat: the zip, the install script and its launcher).
$stage = Join-Path $dist 'installer-stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $zip.FullName (Join-Path $stage 'DuelBot.zip')
Copy-Item (Join-Path $PSScriptRoot 'installer\Install.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'installer\Install.cmd') $stage

# 3. The IExpress directive file.
$exe = Join-Path $dist "DuelBot-Setup-$stamp.exe"
$sed = Join-Path $stage 'DuelBot.sed'
@"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$exe
FriendlyName=Duel Bot Setup
AppLaunched=cmd /c Install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[Strings]
FILE0="DuelBot.zip"
FILE1="Install.ps1"
FILE2="Install.cmd"
[SourceFiles]
SourceFiles0=$stage\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
"@ | Set-Content $sed -Encoding ascii

Write-Host 'Building the installer exe...'
if (Test-Path $exe) { Remove-Item $exe -Force }
Start-Process (Join-Path $env:SystemRoot 'System32\iexpress.exe') -ArgumentList '/N', '/Q', $sed -Wait
if (-not (Test-Path $exe)) { throw 'IExpress did not make the exe' }
Remove-Item $stage -Recurse -Force
$size = '{0:N0} MB' -f ((Get-Item $exe).Length / 1MB)
Write-Host "Done: $exe ($size)"
