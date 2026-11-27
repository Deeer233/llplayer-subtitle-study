#Requires -Version 7.6

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
$pilot = Join-Path $root '.pilot'
$repo = Join-Path $root 'upstream/LLPlayer'
$dotnet = Join-Path $pilot 'dotnet-10.0.404/dotnet.exe'
$dictionary = Join-Path $pilot 'StudyDictionary.zip'
$license = Join-Path $pilot 'ecdict-full/LICENSE'
foreach ($file in $dotnet, $dictionary, $license) { $null = Get-Item -LiteralPath $file -ErrorAction Stop }
$env:DOTNET_CLI_HOME = Join-Path $pilot 'dotnet-cli-home'
$env:NUGET_PACKAGES = Join-Path $pilot 'nuget-packages'
$pub = Join-Path $pilot ('player-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
if (Test-Path -LiteralPath $pub) { throw "Output already exists: $pub" }
& $dotnet publish (Join-Path $repo 'LLPlayer/LLPlayer.csproj') -c Release -r win-x64 --self-contained true --no-restore -p:StudyPlayerOnly=true -o $pub
Copy-Item -LiteralPath (Join-Path $repo 'FFmpeg') -Destination $pub -Recurse
Copy-Item -LiteralPath $dictionary -Destination $pub
Copy-Item -LiteralPath $license -Destination (Join-Path $pub 'ECDICT-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-Local-Translation.cmd') -Destination $pub
Write-Output (Join-Path $pub 'LLPlayer.exe')
