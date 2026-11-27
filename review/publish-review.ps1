#Requires -Version 7.6
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$pilot = Join-Path (Split-Path $PSScriptRoot -Parent) '.pilot'
$project = Join-Path $PSScriptRoot 'OriginalReview/OriginalReview.csproj'
$dotnet = Join-Path $pilot 'dotnet-10.0.404/dotnet.exe'
$dictionary = Join-Path $pilot 'StudyDictionary.zip'
$license = Join-Path $pilot 'ecdict-full/LICENSE'
foreach ($file in $dotnet, $dictionary, $license) { $null = Get-Item -LiteralPath $file -ErrorAction Stop }
$env:DOTNET_CLI_HOME = Join-Path $pilot 'dotnet-cli-home'
$env:NUGET_PACKAGES = Join-Path $pilot 'nuget-packages'
$pub = Join-Path $pilot ('review-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
if (Test-Path -LiteralPath $pub) { throw "Output already exists: $pub" }
& $dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -o $pub
Copy-Item -LiteralPath $dictionary -Destination $pub
Copy-Item -LiteralPath $license -Destination (Join-Path $pub 'ECDICT-LICENSE.txt')
Write-Output (Join-Path $pub 'OriginalReview.exe')
