#Requires -Version 7.4

[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Build')]
    [string]$Snapshot,
    [Parameter(ParameterSetName = 'Build')]
    [switch]$NoInject,
    [Parameter(ParameterSetName = 'Build')]
    [string]$ClipFfmpeg,
    [Parameter(Mandatory, ParameterSetName = 'Verify')]
    [string]$VerifyPayload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Polish.Common.ps1')
. (Join-Path $PSScriptRoot 'Polish.Ui.ps1')

function Assert-PayloadPaths([string[]]$Names) {
    foreach ($name in $Names) {
        if ($name -cin @('LLPlayer.deps.json', 'LLPlayer.runtimeconfig.json')) { continue }
        if (Test-PolishSnapshotExcluded $name) { throw "Unexpected configuration or private file in payload: $name" }
    }
}

function Confirm-PublishedPayload([string]$Run) {
    $Run = Assert-PolishPlainPath $Run -Directory
    $payload = Join-PolishPath $Run 'payload'
    $zip = Join-PolishPath $Run 'payload.zip'
    $manifest = Read-PolishManifest (Join-PolishPath $Run 'payload-manifest.json')
    $rows = $manifest.Data.files
    $names = [Collections.Generic.HashSet[string]]::new([string[]]$rows.path, [StringComparer]::Ordinal)
    Assert-PayloadPaths @($names)
    $required = @('LLPlayer.exe', 'LLPlayer.dll', 'LLPlayer.deps.json', 'LLPlayer.runtimeconfig.json', 'coreclr.dll',
        'ClipTools/ffmpeg.exe', 'StudyDictionary.zip', 'ECDICT-LICENSE.txt')
    $required += @($script:PolishFfmpegNames | ForEach-Object { "FFmpeg/$_" })
    foreach ($name in $required) { if (!$names.Contains($name)) { throw "Missing required payload file: $name" } }
    $diskNames = [string[]]@(Get-PolishTreeFiles $payload)
    if ($diskNames.Count -ne $names.Count -or !$names.SetEquals($diskNames)) { throw 'Payload file set differs from manifest.' }
    foreach ($row in $rows) {
        $stream = Open-PolishInput (Join-PolishPath $payload $row.path)
        try {
            if ($stream.Length -ne $row.bytes -or (Get-PolishHash $stream) -cne $row.sha256) {
                throw "Payload file SHA256 or length mismatch: $($row.path)"
            }
        } finally { $stream.Dispose() }
    }
    $stream = Open-PolishCheckedZip $zip
    try { Confirm-PolishZip $stream $rows } finally { $stream.Dispose() }
    return $rows.Count
}

if ($PSCmdlet.ParameterSetName -eq 'Verify') {
    $run = Assert-PolishPlainPath $VerifyPayload -Directory
    $count = Confirm-PublishedPayload $run
    [pscustomobject]@{ Run = $run; PayloadFiles = $count; Verified = $true }
    return
}

function Invoke-PolishDotnet([string[]]$Arguments, [string]$LogName) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $dotnet
    $info.WorkingDirectory = $stage
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($name in @($info.Environment.Keys)) {
        if ($name -match '^(?:DOTNET_|NUGET_|MSBUILD)') { [void]$info.Environment.Remove($name) }
    }
    foreach ($name in $environment.Keys) { $info.Environment[$name] = [string]$environment[$name] }
    foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    $log = Join-PolishPath $run $LogName
    $process = [Diagnostics.Process]::Start($info)
    try {
        $standardOut = $process.StandardOutput.ReadToEndAsync()
        $standardErr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $standardOut.GetAwaiter().GetResult()
        $stderr = $standardErr.GetAwaiter().GetResult()
        Write-PolishText $log ("exit=$($process.ExitCode)`n$stdout$stderr")
        if ($process.ExitCode -ne 0) {
            $diagnostics = @($stdout, $stderr) -join "`n"
            $errors = @($diagnostics -split '\r?\n' | Where-Object { $_ -match '\berror\b' } | Select-Object -First 12)
            throw "dotnet $($Arguments[0]) failed ($($process.ExitCode)). Log: $log`n$($errors -join "`n")"
        }
        return $stdout
    } finally { $process.Dispose() }
}

$Snapshot = Assert-PolishPlainPath $Snapshot -Directory
$uiNames = if (!$NoInject) { @(Get-PolishUiFiles) }
if (!$NoInject) {
    if (!$uiNames) { throw 'No packaging/ui/*.cs sources. Use -NoInject only for a non-delivery build.' }
    $null = New-PolishPathIndex @($uiNames)
}
$dotnetSdk = Assert-PolishPlainPath (Join-PolishPath $script:PolishRoot '.pilot/dotnet-10.0.404') -Directory
$dotnet = Assert-PolishPlainPath (Join-PolishPath $dotnetSdk 'dotnet.exe')
$feed = Assert-PolishPlainPath (Join-PolishPath $script:PolishRoot '.pilot/nuget-packages') -Directory
Get-PolishTreeFiles $dotnetSdk -CheckOnly
Get-PolishTreeFiles $feed -CheckOnly

$cliHash = '3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec'
if (!$NoInject) {
    $deliveryDir = [string][char]0x4ea4 + [char]0x4ed8
    if (!$ClipFfmpeg) {
        $ClipFfmpeg = Join-PolishPath $script:PolishRoot "$deliveryDir/LLPlayer-Anki-20261127-中文字幕错时补配版/ClipTools/ffmpeg.exe"
    }
    $clip = Assert-PolishPlainPath $ClipFfmpeg
    if ((Get-PolishFileHash $clip) -cne $cliHash) { throw "Unapproved ClipTools ffmpeg.exe: $clip" }
    $dictionary = Assert-PolishPlainPath (Join-PolishPath $script:PolishRoot '.pilot/StudyDictionary.zip')
    $license = Assert-PolishPlainPath (Join-PolishPath $script:PolishRoot '.pilot/ecdict-full/LICENSE')
    $dictionaryHash = Get-PolishFileHash $dictionary
    $licenseHash = Get-PolishFileHash $license
}

$kind = if ($NoInject) { 'no-inject' } else { 'release' }
$run = New-PolishRun $kind
Write-Verbose "Using new offline build directory: $run"
$stage = New-PolishDirectory (Join-PolishPath $run 'staging')
$src = New-PolishDirectory (Join-PolishPath $stage 'source')
$frozen = Confirm-PolishSnapshot $Snapshot -ExtractRoot $src
$project = Join-PolishPath $src 'upstream/LLPlayer/LLPlayer/LLPlayer.csproj'
$targets = Assert-PolishPlainPath (Join-PolishPath $src 'upstream/LLPlayer/Directory.Build.targets')
$projectXml = [xml][IO.File]::ReadAllText((Assert-PolishPlainPath $project))
if ($projectXml.SelectSingleNode('/Project/PropertyGroup/Authors').InnerText -cne 'umlx5h' -or
    $projectXml.SelectSingleNode('/Project/PropertyGroup/ApplicationIcon').InnerText -cne 'LLPlayer.ico') {
    throw 'The frozen project does not name the original author or application icon.'
}

if (!$NoInject) {
    Add-PolishUi $src $uiNames
}

$packages = New-PolishDirectory (Join-PolishPath $stage 'nuget-packages')
$cliHome = New-PolishDirectory (Join-PolishPath $stage 'cli-home')
$temp = New-PolishDirectory (Join-PolishPath $stage 'temp')
$nugetConfig = Join-PolishPath $stage 'NuGet.Config'
$feedXml = [Security.SecurityElement]::Escape($feed)
Write-PolishText $nugetConfig @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear/><add key="polish-local" value="$feedXml" /></packageSources>
  <fallbackPackageFolders><clear/></fallbackPackageFolders>
  <auditSources><clear/></auditSources>
</configuration>
"@
Write-PolishText (Join-PolishPath $stage 'global.json') '{"sdk":{"version":"10.0.404","rollForward":"disable","allowPrerelease":false}}'

$environment = @{
    DOTNET_CLI_HOME = $cliHome; DOTNET_CLI_TELEMETRY_OPTOUT = 1; DOTNET_NOLOGO = 1
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = 1; DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'; DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 0
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'; DOTNET_CLI_USE_MSBUILD_SERVER = 0; MSBUILDDISABLENODEREUSE = 1
    NUGET_PACKAGES = $packages; NUGET_HTTP_CACHE_PATH = (Join-PolishPath $stage 'nuget-http-cache')
    NUGET_SCRATCH = (Join-PolishPath $stage 'nuget-scratch'); NUGET_CERT_REVOCATION_MODE = 'offline'
    TEMP = $temp; TMP = $temp; TMPDIR = $temp
}
if ((Invoke-PolishDotnet @('--version') 'sdk.log').Trim() -cne '10.0.404') { throw "Incorrect SDK; see $run\sdk.log" }

$version = "0.3.0+$($frozen.upstreamHead)"
$props = @('-p:Configuration=Release', '-p:SelfContained=true', '-p:StudyPlayerOnly=false', '-p:StudyAnkiExport=true',
    "-p:Version=$version", "-p:InformationalVersion=$version", "-p:SourceRevisionId=$($frozen.upstreamHead)",
    '-p:IncludeSourceRevisionInInformationalVersion=false', '-p:EnableSourceLink=false', '-p:EnableSourceControlManagerQueries=false',
    '-p:ImportDirectoryBuildProps=false', "-p:DirectoryBuildTargetsPath=$targets", '-p:ImportDirectoryPackagesProps=false',
    '-p:NuGetAudit=false', '-p:RestoreAdditionalProjectSources=', '-p:RestoreFallbackFolders=', '-p:RestoreAdditionalProjectFallbackFolders=',
    '-p:DisableImplicitNuGetFallbackFolder=true', '-p:DebugType=None', '-p:DebugSymbols=false')
$restoreArgs = @('restore', $project, '-r', 'win-x64', '--configfile', $nugetConfig, '--source', $feed, '--packages', $packages,
    '--no-http-cache', '--disable-build-servers', '--verbosity', 'quiet', '-nologo') + $props
$null = Invoke-PolishDotnet $restoreArgs 'restore.log'

$outputName = if ($NoInject) { 'compile-check-only' } else { 'payload' }
$output = New-PolishDirectory (Join-PolishPath $run $outputName)
$publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore',
    '--disable-build-servers', '--verbosity', 'quiet', '-nologo', '-o', $output) + $props
$null = Invoke-PolishDotnet $publishArgs 'publish.log'

$exe = Assert-PolishPlainPath (Join-PolishPath $output 'LLPlayer.exe')
$app = Assert-PolishPlainPath (Join-PolishPath $output 'LLPlayer.dll')
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($app)
if ($info.ProductVersion -cne $version -or $info.CompanyName -cne 'umlx5h') {
    throw "Published version or author differs from expected: $($info.ProductVersion), $($info.CompanyName)"
}
if ($NoInject) {
    [pscustomobject]@{ Run = $run; Version = $version; CompileCheckOnly = $exe; Delivery = $false }
    return
}

$ffmpegNames = @($frozen.files | Where-Object { $_.path -cmatch '\Aupstream/LLPlayer/FFmpeg/[^/]+\.[dD][lL][lL]\z' })
$ffmpegOut = New-PolishDirectory (Join-PolishPath $output 'FFmpeg')
foreach ($row in $ffmpegNames) {
    $name = $row.path.Split('/')[-1]
    Copy-PolishInput (Join-PolishPath $src $row.path) (Join-PolishPath $ffmpegOut $name) $row.sha256
}
$clipOut = New-PolishDirectory (Join-PolishPath $output 'ClipTools')
Copy-PolishInput $clip (Join-PolishPath $clipOut 'ffmpeg.exe') $cliHash
Copy-PolishInput $dictionary (Join-PolishPath $output 'StudyDictionary.zip') $dictionaryHash
Copy-PolishInput $license (Join-PolishPath $output 'ECDICT-LICENSE.txt') $licenseHash

$names = [string[]]@(Get-PolishTreeFiles $output)
Assert-PayloadPaths $names
$zip = Join-PolishPath $run 'payload.zip'
$manifestPath = Join-PolishPath $run 'payload-manifest.json'
$rows = @(New-PolishArchive $output $names $zip)
Write-PolishText $manifestPath (ConvertTo-Json -InputObject ([ordered]@{ files = $rows }) -Depth 5)
$hash = Write-PolishZipChecksum $zip
$count = Confirm-PublishedPayload $run
[pscustomobject]@{
    Run = $run; Payload = $output; Executable = $exe; Version = $version; InjectedSources = @($uiNames).Count
    PayloadFiles = $count; Zip = $zip; Sha256 = $hash; Sha256File = "$zip.sha256"; Manifest = $manifestPath
}
