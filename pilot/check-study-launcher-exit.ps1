#Requires -Version 5.1
[CmdletBinding()]
param([string]$PlayerPackage)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $root '.pilot'
$server = (Get-Item -LiteralPath (Join-Path $cache 'llama-b11193/mirror-verified-bin/llama-server.exe')).FullName
$model = (Get-Item -LiteralPath (Join-Path $cache 'model-qwen3-pilot/Qwen3-4B-Q4_K_M.gguf')).FullName
if ($PlayerPackage) {
    $package = (Get-Item -LiteralPath $PlayerPackage).FullName
    if (!(Test-Path -LiteralPath (Join-Path $package 'LLPlayer.exe') -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $package 'System.dll') -PathType Leaf)) {
        throw 'Expected a self-contained player package with LLPlayer.exe and System.dll.'
    }
}
$fixture = Join-Path $cache ('launcher-exit-check-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture
$player = Join-Path $fixture 'LLPlayer.exe'
$marker = Join-Path $fixture 'ready.txt'
$release = Join-Path $fixture 'release.txt'
$stderr = Join-Path $fixture 'launcher.err'
$launch = Join-Path $PSScriptRoot 'start-study-candidate.ps1'
$windowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'

# Keeps the real launcher in its player wait without opening a window or modifying user settings.
$source = Join-Path $fixture 'WaitForRelease.cs'
[IO.File]::WriteAllText($source, @"
using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
internal static class WaitForRelease
{
    private static void Main()
    {
        using (var self = Process.GetCurrentProcess())
            File.WriteAllText("ready.tmp", self.Id + ":" + Environment.GetEnvironmentVariable("LLPLAYER_STUDY_LOCAL_PORT"));
        File.Move("ready.tmp", "ready.txt");
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed.TotalSeconds < 30 && !File.Exists("release.txt")) Thread.Sleep(100);
    }
}
"@)
$sdk = Join-Path $cache 'dotnet-10.0.404'
$framework = Join-Path $env:SystemRoot 'Microsoft.NET/Framework64/v4.0.30319'
$env:DOTNET_CLI_HOME = Join-Path $cache 'dotnet-cli-home'
& (Join-Path $sdk 'dotnet.exe') (Join-Path $sdk 'sdk/10.0.404/Roslyn/bincore/csc.dll') `
    -nologo -noconfig -nostdlib -target:winexe "-r:$framework/mscorlib.dll" "-r:$framework/System.dll" "-out:$player" $source
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $player -PathType Leaf)) { throw 'Could not compile the fixture player.' }

$launcher = $null
$ownedModel = $null
$stub = $null
try {
    # Reproduce the CMD working directory without opening the real player.
    $launcherDirectory = if ($PlayerPackage) { $package } else { $fixture }
    $launcher = Start-Process -FilePath $windowsPowerShell -WindowStyle Hidden -PassThru `
        -WorkingDirectory $launcherDirectory -RedirectStandardOutput (Join-Path $fixture 'launcher.out') -RedirectStandardError $stderr `
        -ArgumentList @('-NoProfile', '-File', ('"' + $launch + '"'), '-DynamicPort',
            '-Player', ('"' + $player + '"'), '-Server', ('"' + $server + '"'), '-Model', ('"' + $model + '"'))
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $marker -PathType Leaf) -and !$launcher.HasExited -and $deadline.Elapsed.TotalSeconds -lt 105) {
        Start-Sleep -Milliseconds 150
    }
    if (!(Test-Path -LiteralPath $marker -PathType Leaf)) {
        throw "Fixture player never started. See $stderr"
    }
    $identity = [IO.File]::ReadAllText($marker)
    if ($identity -notmatch '\A(\d+):(\d+)\z') { throw 'Fixture did not receive a player PID and model port.' }
    $stub = Get-Process -Id ([int]$Matches[1])
    $port = [int]$Matches[2]
    $listener = Get-NetTCPConnection -State Listen -LocalAddress 127.0.0.1 -LocalPort $port
    $ownerId = $listener.OwningProcess
    $candidate = Get-Process -Id $ownerId
    $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId=$ownerId"
    if ($candidate.Path -ne $server -or $processInfo.ParentProcessId -ne $launcher.Id) {
        throw 'Unexpected listener; refusing to stop an unrelated process.'
    }
    # Keep the verified process handle so PID reuse cannot affect test cleanup.
    $null = $candidate.Handle
    $ownedModel = $candidate
    $launcher.Kill()
    $launcher.WaitForExit()
    if (!$ownedModel.WaitForExit(6000)) { throw "Model remained running on 127.0.0.1:${port} after forced launcher exit." }
    if (Get-NetTCPConnection -State Listen -LocalAddress 127.0.0.1 -LocalPort $port -ErrorAction SilentlyContinue) {
        throw 'The model port remained open after forced launcher exit.'
    }
    Write-Output "PASS forced launcher exit stopped its model and released loopback port $port."
} finally {
    [IO.File]::WriteAllText($release, '')
    if ($stub) { $null = $stub.WaitForExit(3000); $stub.Dispose() }
    if ($launcher -and !$launcher.WaitForExit(3000)) { $launcher.Kill(); $launcher.WaitForExit() }
    if ($ownedModel -and !$ownedModel.HasExited) { $ownedModel.Kill(); $ownedModel.WaitForExit() }
    if ($ownedModel) { $ownedModel.Dispose() }
    if ($launcher) { $launcher.Dispose() }
}
