#Requires -Version 5.1

param([switch]$Anki, [string]$ClipFfmpeg)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $root '.pilot'
$repo = Join-Path $root 'upstream/LLPlayer'
$dotnet = Join-Path $cache 'dotnet-10.0.404/dotnet.exe'
$dictionary = Join-Path $cache 'StudyDictionary.zip'
$license = Join-Path $cache 'ecdict-full/LICENSE'
$ffmpeg = Join-Path $repo 'FFmpeg'
foreach ($path in $dotnet, $dictionary, $license) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing local dependency: $path" }
}
if (!(Test-Path -LiteralPath $ffmpeg -PathType Container)) { throw "Missing packaged FFmpeg directory: $ffmpeg" }
if ($Anki) {
    # The FFmpeg folder above contains Flyleaf libraries, not a standalone command.
    if (!$ClipFfmpeg) { $ClipFfmpeg = (Get-Command ffmpeg.exe -CommandType Application).Source }
    $cli = Get-Item -LiteralPath $ClipFfmpeg
    if ($cli.PSIsContainer -or $cli.Extension -ine '.exe') { throw "Expected a standalone ffmpeg.exe: $ClipFfmpeg" }
    $ClipFfmpeg = $cli.FullName
}

$env:DOTNET_CLI_HOME = Join-Path $cache 'dotnet-cli-home'
$env:NUGET_PACKAGES = Join-Path $cache 'nuget-packages'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$flavor = if ($Anki) { 'anki-player' } else { 'cloud-player' }
$ankiFlag = if ($Anki) { 'true' } else { 'false' }
$pub = Join-Path $cache ("$flavor-$stamp-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$null = New-Item -ItemType Directory -Path $pub
& $dotnet publish (Join-Path $repo 'LLPlayer/LLPlayer.csproj') -c Release -r win-x64 --self-contained true --no-restore -p:StudyPlayerOnly=false "-p:StudyAnkiExport=$ankiFlag" -p:NuGetAudit=false -o $pub
if ($LASTEXITCODE -ne 0) { throw "Cloud desktop build failed. Do not use the incomplete output: $pub" }
Copy-Item -LiteralPath $ffmpeg -Destination $pub -Recurse
if ($Anki) {
    $clipTools = Join-Path $pub 'ClipTools'
    $null = New-Item -ItemType Directory -Path $clipTools
    Copy-Item -LiteralPath $ClipFfmpeg -Destination (Join-Path $clipTools 'ffmpeg.exe')
}
Copy-Item -LiteralPath $dictionary -Destination $pub
Copy-Item -LiteralPath $license -Destination (Join-Path $pub 'ECDICT-LICENSE.txt')
Write-Output (Get-Item -LiteralPath (Join-Path $pub 'LLPlayer.exe')).FullName
