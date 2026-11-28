#Requires -Version 7.4

[CmdletBinding()]
param(
    [string]$DotNet = 'dotnet',
    [string]$UpstreamSource = 'https://github.com/umlx5h/LLPlayer.git',
    [string]$NuGetSource,
    [switch]$PrepareOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Polish.Common.ps1')
. (Join-Path $PSScriptRoot 'Polish.Ui.ps1')

function Invoke-PlayerTool([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command $($Arguments[0]) failed ($LASTEXITCODE)." }
}

if (!$IsWindows) { throw 'The LLPlayer WPF build requires Windows.' }
$git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
if (!$PrepareOnly) {
    $DotNet = (Get-Command $DotNet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
}
$patch = Assert-PolishPlainPath (Join-PolishPath $PSScriptRoot 'LLPlayer-da101d8.patch')
$pilot = Assert-PolishPlainPath (Join-PolishPath $script:PolishRoot '.pilot') -Directory -AllowMissingLeaf
if (![IO.Directory]::Exists($pilot)) { $null = New-PolishDirectory $pilot }
$run = New-PolishRun 'source-build'
Write-Host "Build workspace: $run"
$upstreamRoot = New-PolishDirectory (Join-PolishPath $run 'upstream')
$upstream = Join-PolishPath $upstreamRoot 'LLPlayer'
Invoke-PlayerTool $git @('init', '--quiet', $upstream)
Invoke-PlayerTool $git @('-C', $upstream, 'fetch', '--quiet', '--depth=1', '--no-tags', '--', $UpstreamSource, $script:PolishBaseHead)
Invoke-PlayerTool $git @('-C', $upstream, '-c', 'core.autocrlf=false', 'switch', '--quiet', '--detach', $script:PolishBaseHead)
Invoke-PlayerTool $git @('-C', $upstream, '-c', 'core.autocrlf=false', 'apply', '--check', '--', $patch)
Invoke-PlayerTool $git @('-C', $upstream, '-c', 'core.autocrlf=false', 'apply', '--whitespace=nowarn', '--', $patch)

$null = New-PolishDirectory (Join-PolishPath $run 'cloud')
$desktop = New-PolishDirectory (Join-PolishPath $run 'cloud/desktop')
$null = New-PolishDirectory (Join-PolishPath $run 'review')
$null = New-PolishDirectory (Join-PolishPath $run 'review/OriginalReview')
foreach ($source in Get-ChildItem -LiteralPath (Join-PolishPath $script:PolishRoot 'cloud/desktop') -Filter '*.cs' -File) {
    Copy-PolishInput $source.FullName (Join-PolishPath $desktop $source.Name) (Get-PolishFileHash $source.FullName)
}
foreach ($name in $script:PolishReviewNames) {
    $source = Join-PolishPath $script:PolishRoot $name
    Copy-PolishInput $source (Join-PolishPath $run $name) (Get-PolishFileHash $source)
}
$ui = @(Get-PolishUiFiles)
if (!$ui) { throw 'Missing packaging/ui sources.' }
Add-PolishUi $run $ui

$project = Join-PolishPath $upstream 'LLPlayer/LLPlayer.csproj'
if ($PrepareOnly) {
    [pscustomobject]@{ Workspace = $run; Project = $project; Upstream = $script:PolishBaseHead }
    return
}
$output = New-PolishDirectory (Join-PolishPath $run 'player')
$version = "0.3.0+$($script:PolishBaseHead)"
$arguments = @('build', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '--disable-build-servers',
    '-o', $output, '-p:StudyPlayerOnly=false', '-p:StudyAnkiExport=true', "-p:Version=$version",
    '-p:IncludeSourceRevisionInInformationalVersion=false', '-p:EnableSourceLink=false', '-p:EnableSourceControlManagerQueries=false')
if ($NuGetSource) { $arguments += @('--source', $NuGetSource) }
Invoke-PlayerTool $DotNet $arguments
$ffmpeg = New-PolishDirectory (Join-PolishPath $output 'FFmpeg')
foreach ($name in $script:PolishFfmpegNames) {
    $source = Join-PolishPath $upstream "FFmpeg/$name"
    Copy-PolishInput $source (Join-PolishPath $ffmpeg $name) (Get-PolishFileHash $source)
}
$executable = Assert-PolishPlainPath (Join-PolishPath $output 'LLPlayer.exe')
[pscustomobject]@{ Workspace = $run; Executable = $executable; Upstream = $script:PolishBaseHead }
