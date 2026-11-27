#Requires -Version 7.6
param([string]$Video, [int]$EnglishStream = 4, [int]$SeekMs = 137000, [switch]$VerifyPilotDialogue, [switch]$VerifyPilotModel,
    [string]$PlayerPackage, [string]$PackagedSamples, [switch]$UnverifiedPilot)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if ($VerifyPilotModel -and (!$Video -or !$PlayerPackage -or $VerifyPilotDialogue -or $env:LLPLAYER_STUDY_LOCAL_KEY -cnotmatch '\A[0-9a-f]{64}\z')) {
    throw 'The model check needs a published package, the pilot episode, and the explicit local model launcher.'
}
if (($PackagedSamples -and (!$PlayerPackage -or $Video -or $VerifyPilotDialogue -or $VerifyPilotModel -or
        $env:LLPLAYER_STUDY_LOCAL_KEY -cnotmatch '\A[0-9a-f]{64}\z')) -or ($UnverifiedPilot -and !$PackagedSamples)) {
    throw 'The packaged batch needs explicit local model credentials, a published package and a sample file.'
}
$root = Split-Path $PSScriptRoot -Parent
$pilot = Join-Path $root '.pilot'
$dotnet = Join-Path $pilot 'dotnet-10.0.404/dotnet.exe'
$repo = Join-Path $root 'upstream/LLPlayer'
$work = Join-Path $pilot ('engine-check-' + [guid]::NewGuid().ToString('N'))
$env:DOTNET_CLI_HOME = Join-Path $pilot 'dotnet-cli-home'
$env:NUGET_PACKAGES = Join-Path $pilot 'nuget-packages'
$null = New-Item -ItemType Directory -Path $work
if (!$VerifyPilotModel -and !$PackagedSamples) {
    $seed = Join-Path $work 'seed.srt'
    $caption = "1`n00:00:01,000 --> 00:00:04,000`nI thought you were going to bail on me.`n`n2`n00:00:05,000 --> 00:00:07,000`nI was here with you all of the time.`n"
    Set-Content -LiteralPath $seed, (Join-Path $work 'embedded.en.srt'), (Join-Path $work 'sidecar.srt') -Value $caption -Encoding utf8
    $bare = Join-Path $work 'no-english.mkv'
    & ffmpeg -nostdin -v error -n -f lavfi -i 'color=s=128x72:r=20:d=9' -c:v mpeg4 -g 20 $bare
    & ffmpeg -nostdin -v error -n -i $bare -i $seed -map 0:v -map 1:s -c:v copy -c:s mov_text -metadata:s:s:0 language=eng (Join-Path $work 'embedded.mp4')
    Copy-Item -LiteralPath $bare -Destination (Join-Path $work 'sidecar.mkv')
}
$build = Join-Path $work 'probe'
$publish = @('publish', (Join-Path $PSScriptRoot 'EngineProbe/EngineProbe.csproj'), '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', '-p:StudyPlayerOnly=true', '-p:NuGetAudit=false', '--source', $env:NUGET_PACKAGES, '-o', $build)
if ($PackagedSamples) { & $dotnet @publish | ForEach-Object { Write-Verbose $_ } } else { & $dotnet @publish }
$exe = Join-Path $build 'EngineProbe.exe'
$ffmpeg = Join-Path $repo 'FFmpeg'
if ($PlayerPackage) {
    $package = (Resolve-Path -LiteralPath $PlayerPackage).Path
    foreach ($file in 'LLPlayer.dll', 'FlyleafLib.dll') {
        $source = Join-Path $package $file
        $target = Join-Path $build $file
        Copy-Item -LiteralPath $source -Destination $target -Force
        if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { throw "Package assembly mismatch: $file" }
    }
    $ffmpeg = (Resolve-Path -LiteralPath (Join-Path $package 'FFmpeg')).Path
    if (!$PackagedSamples) { Write-Output "PASS probe uses verified packaged managed assemblies and FFmpeg from $package" }
}
if ($PackagedSamples) {
    $samplePath = (Resolve-Path -LiteralPath $PackagedSamples).Path
    $verification = if ($UnverifiedPilot) { 'unverified' } else { 'verified' }
    $previousEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        & $exe 'packaged-model-batch' $samplePath $verification
    } finally { [Console]::OutputEncoding = $previousEncoding }
    return
}
if (!$VerifyPilotModel) {
    foreach ($mode in 'record', 'resume', 'autoplay') { & $exe $mode $work $ffmpeg }
}
if ($Video) {
    $episodeMode = if ($VerifyPilotModel) { 'episode-model' } else { 'episode' }
    $episodeArgs = @($episodeMode, $work, $ffmpeg, (Resolve-Path -LiteralPath $Video).Path, "$EnglishStream", "$SeekMs")
    if ($VerifyPilotDialogue) { $episodeArgs += 'pilot-dialogue' }
    & $exe @episodeArgs
}
