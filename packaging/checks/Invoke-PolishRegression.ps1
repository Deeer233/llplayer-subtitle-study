#Requires -Version 7.3
<#
.SYNOPSIS
Runs the same eight offline regression checks on an unpacked LLPlayer package.
.PARAMETER Package
Directory containing LLPlayer.dll, or the full path to that DLL.
.PARAMETER Dotnet
Optional absolute path to a .NET 10 SDK executable with Windows Desktop support.
.NOTES
The dedicated .pilot/polish-regression-* directory retains report.md, step logs,
and synthetic data. Exit code: 0 all pass, 1 regression, 2 incomplete probe.
The 2026-11-26 Anki delivery is expected to fail the field re-lock assertion.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$pilot = Join-Path $root '.pilot'
$packageItem = Get-Item -LiteralPath $Package
$dll = if ($packageItem.PSIsContainer) { Join-Path $packageItem.FullName 'LLPlayer.dll' } else { $packageItem.FullName }
$dll = (Get-Item -LiteralPath $dll).FullName
if ([IO.Path]::GetFileName($dll) -ine 'LLPlayer.dll') { throw 'Provide a package directory or its LLPlayer.dll.' }
$packageDir = Split-Path -Parent $dll
if (!$Dotnet) {
    $bundled = Join-Path $pilot 'dotnet-10.0.404\dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $bundled -PathType Leaf) { $bundled } else { (Get-Command dotnet -CommandType Application).Source }
}
$Dotnet = (Get-Item -LiteralPath $Dotnet).FullName
$null = New-Item -ItemType Directory -Path $pilot -Force
if ((Get-Item -LiteralPath $pilot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The .pilot directory must not be a reparse point.' }
$work = Join-Path $pilot ('polish-regression-' + [guid]::NewGuid().ToString('N').Substring(0,12))
$null = New-Item -ItemType Directory -Path $work
$privateDirs = @('temp','dotnet-home','packages','empty-feed')
foreach ($name in $privateDirs) { $null = New-Item -ItemType Directory -Path (Join-Path $work $name) }
$overrides = @{
    TMP = Join-Path $work 'temp'
    TEMP = Join-Path $work 'temp'
    DOTNET_CLI_HOME = Join-Path $work 'dotnet-home'
    NUGET_PACKAGES = Join-Path $work 'packages'
    DOTNET_NOLOGO = '1'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    MSBUILDDISABLENODEREUSE = '1'
    REVIEW_FFMPEG = $null
    LLPLAYER_STUDY_LOCAL_KEY = $null
}
$saved = @{}
foreach ($name in $overrides.Keys) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $overrides[$name], 'Process')
}

Write-Output "Run: $work"
$result = 2
Push-Location -LiteralPath $work
try {
    $project = Join-Path $PSScriptRoot 'PolishRegression.csproj'
    $artifacts = Join-Path $work 'artifacts'
    $build = Join-Path $work 'probe'
    $feed = Join-Path $work 'empty-feed'
    & $Dotnet build $project -c Release --artifacts-path $artifacts -o $build "-p:PolishPackageDir=$packageDir" "-p:RestoreSources=$feed" '-p:NuGetAudit=false' '-p:UseSharedCompilation=false' 2>&1 |
        Tee-Object -FilePath (Join-Path $work 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Probe build failed. See $work\build.log" }
    & $Dotnet (Join-Path $build 'PolishRegression.dll') $dll $work
    $result = $LASTEXITCODE
} finally {
    Pop-Location
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
exit $result
