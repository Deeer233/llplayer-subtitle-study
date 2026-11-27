#Requires -Version 7.4
[CmdletBinding(DefaultParameterSetName = 'Package')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Package')][string]$Package,
    [Parameter(Mandatory, ParameterSetName = 'BuildOnly')][switch]$BuildOnly,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')][switch]$SelfTest,
    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (!$IsWindows) { throw 'This probe requires Windows Desktop WPF.' }

function Assert-PlainDirectory([string]$Path) {
    for ($dir = [IO.DirectoryInfo]::new($Path); $dir; $dir = $dir.Parent) {
        if (!$dir.Exists -or ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Not a plain directory: $($dir.FullName)"
        }
    }
}

$dll = if ($PSCmdlet.ParameterSetName -eq 'Package') {
    $item = Get-Item -LiteralPath $Package
    if ($item.PSIsContainer) { $item = Get-Item -LiteralPath (Join-Path $item.FullName 'LLPlayer.dll') }
    if ($item.PSIsContainer -or $item.Name -ine 'LLPlayer.dll') { throw 'Provide LLPlayer.dll or its package directory.' }
    $item.FullName
}
if (!$Dotnet) {
    $repoPilot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\.pilot'))
    $bundled = if ([IO.Directory]::Exists($repoPilot)) {
        Get-ChildItem -LiteralPath $repoPilot -Directory -Filter 'dotnet-10.*' |
            Where-Object { $_.Name -match '^dotnet-10\.\d+\.\d+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'dotnet.exe') -PathType Leaf) } |
            Sort-Object { [version]$_.Name.Substring(7) } -Descending | Select-Object -First 1
    }
    $Dotnet = if ($bundled) { Join-Path $bundled.FullName 'dotnet.exe' } else { (Get-Command dotnet.exe -CommandType Application).Source }
}
$dotnetItem = Get-Item -LiteralPath $Dotnet
if ($dotnetItem.PSIsContainer -or $dotnetItem.Name -ine 'dotnet.exe') { throw 'Provide a .NET 10 dotnet.exe.' }
$Dotnet = $dotnetItem.FullName

Assert-PlainDirectory $PSScriptRoot
$pilot = Join-Path $PSScriptRoot '.pilot'
[void][IO.Directory]::CreateDirectory($pilot)
Assert-PlainDirectory $pilot
$run = Join-Path $pilot ('ui-qa-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
$null = New-Item -ItemType Directory -Path $run
foreach ($leaf in 'temp', 'dotnet-home', 'packages', 'empty-feed', 'appdata', 'local-appdata') {
    [void][IO.Directory]::CreateDirectory((Join-Path $run $leaf))
}

$overrides = @{
    TEMP = Join-Path $run 'temp'
    TMP = Join-Path $run 'temp'
    APPDATA = Join-Path $run 'appdata'
    LOCALAPPDATA = Join-Path $run 'local-appdata'
    DOTNET_CLI_HOME = Join-Path $run 'dotnet-home'
    NUGET_PACKAGES = Join-Path $run 'packages'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_NOLOGO = '1'
    MSBUILDDISABLENODEREUSE = '1'
}
$saved = @{}
foreach ($name in $overrides.Keys) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $overrides[$name], 'Process')
}

Write-Output "Run: $run"
$result = 2
Push-Location -LiteralPath $run
try {
    $bin = Join-Path $run 'bin'
    $project = Join-Path $PSScriptRoot 'OffscreenUiQa.csproj'
    $feed = Join-Path $run 'empty-feed'
    & $Dotnet build $project -c Release --disable-build-servers --artifacts-path (Join-Path $run 'artifacts') -o $bin "-p:RestoreSources=$feed" '-p:NuGetAudit=false' '-p:UseSharedCompilation=false' 2>&1 |
        Tee-Object -FilePath (Join-Path $run 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Probe build failed. See $run\build.log" }
    $result = 0
    if (!$BuildOnly) {
        $probeArgs = if ($SelfTest) { '--self-test' } else { $dll }
        & $Dotnet (Join-Path $bin 'OffscreenUiQa.dll') $probeArgs 2>&1 |
            Tee-Object -FilePath (Join-Path $run 'run.log')
        $result = $LASTEXITCODE
    }
} finally {
    Pop-Location
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
exit $result
