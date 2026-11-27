#Requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$Model,
    [string]$Player,
    [string]$Samples,
    [string]$SamplesPackage,
    [string]$Report,
    [switch]$UnverifiedPilot,
    [switch]$Probe8B,
    [string]$VerifyPackage,
    [string]$VerifyEpisode,
    [switch]$CheckOnly,
    [switch]$DynamicPort
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if ($CheckOnly -and !$Player) { throw '-CheckOnly requires the player executable to verify.' }
if (!$Player -and $PSVersionTable.PSVersion -lt [version]'7.6') { throw 'Model audit commands require PowerShell 7.6.' }
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1')
}
if (($Player -and ($Samples -or $Report -or $UnverifiedPilot -or $Probe8B)) -or ($UnverifiedPilot -and !$Samples)) {
    throw 'Choose a player session or a probe; an unverified probe needs a sample file.'
}
if (($VerifyPackage -or $VerifyEpisode) -and (!$VerifyPackage -or !$VerifyEpisode -or $Player -or $Samples -or $Report -or $UnverifiedPilot -or $Probe8B)) {
    throw 'Pass -VerifyPackage and -VerifyEpisode together; other launch modes cannot be combined.'
}
if ($SamplesPackage -and (!$Samples -or $Player -or $Probe8B)) {
    throw '-SamplesPackage requires -Samples and the 4B batch mode.'
}
if ($VerifyPackage) {
    $VerifyPackage = (Resolve-Path -LiteralPath $VerifyPackage).Path
    $VerifyEpisode = (Resolve-Path -LiteralPath $VerifyEpisode).Path
}
$serverFile = Get-Item -LiteralPath (Resolve-Path -LiteralPath $Server).Path
$modelFile = Get-Item -LiteralPath (Resolve-Path -LiteralPath $Model).Path
if ($serverFile.Name -ne 'llama-server.exe' -or $serverFile.PSIsContainer) { throw 'Expected llama-server.exe.' }
$pin = if ($Probe8B) {
    @{Name='Qwen3-8B-Q4_K_M'; Alias='study-qwen3-8b'; Bytes=5027783488L; Hash='d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785'}
} else {
    @{Name='Qwen3-4B-Q4_K_M'; Alias='study-qwen3-4b'; Bytes=2497280256L; Hash='7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5'}
}
if ($modelFile.Length -ne $pin.Bytes) {
    throw "Expected pinned $($pin.Name), $($pin.Bytes) bytes. Model size mismatch."
}
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $stream = [IO.File]::OpenRead($modelFile.FullName)
    try { $hash = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose() }
} finally { $sha256.Dispose() }
if ($hash -ne $pin.Hash) {
    throw "Expected pinned $($pin.Name), $($pin.Bytes) bytes. Model size or hash mismatch."
}
if ($Player) {
    $Player = (Resolve-Path -LiteralPath $Player).Path
    if ([IO.Path]::GetFileName($Player) -ne 'LLPlayer.exe' -or !(Test-Path -LiteralPath $Player -PathType Leaf)) { throw 'Expected a published LLPlayer.exe.' }
}
if ($Samples) { $Samples = (Resolve-Path -LiteralPath $Samples).Path }
if ($SamplesPackage) { $SamplesPackage = (Resolve-Path -LiteralPath $SamplesPackage).Path }
if ($Report) {
    $Report = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Report)
    if (Test-Path -LiteralPath $Report) { throw "Report already exists: $Report" }
}

$requestedPort = if ($DynamicPort) { 0 } else { 18081 }
$tcp = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $requestedPort)
try {
    $tcp.Start()
    $port = $tcp.LocalEndpoint.Port.ToString([Globalization.CultureInfo]::InvariantCulture)
} catch {
    $socketError = $_.Exception.GetBaseException()
    if (!$DynamicPort -and $socketError -is [Net.Sockets.SocketException] -and
        $socketError.SocketErrorCode -eq [Net.Sockets.SocketError]::AddressAlreadyInUse) {
        throw 'Local translation port 18081 is in use. Close the earlier translation session or use the newer Start-Local-Translation.cmd that selects a free port.'
    }
    throw
} finally { $tcp.Stop() }
if (!('StudyModelProcess' -as [type])) {
    $source = Join-Path $PSScriptRoot 'StudyModelProcess.cs'
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        # The published player's System.dll can shadow the .NET Framework reference in the current directory.
        Add-Type -LiteralPath $source -ReferencedAssemblies ([System.Diagnostics.Process].Assembly.Location)
    } else {
        Add-Type -LiteralPath $source
    }
}
$logs = Join-Path (Split-Path $PSScriptRoot -Parent) ('.pilot/llama-session-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $logs
$stderr = Join-Path $logs 'server.err'
$keyFile = Join-Path $logs 'session.key'
$generator = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $keyBytes = New-Object byte[] 32
    $generator.GetBytes($keyBytes)
    $key = [BitConverter]::ToString($keyBytes).Replace('-', '').ToLowerInvariant()
} finally { $generator.Dispose() }
$previous = $env:LLPLAYER_STUDY_LOCAL_KEY
$previousPort = $env:LLPLAYER_STUDY_LOCAL_PORT
$modelJob = $null
$serverProcess = $null
$http = $null
try {
    [IO.File]::WriteAllText($keyFile, $key)
    # A relative model path avoids passing the workspace's non-ASCII parent path to llama-server.
    $baseUri = [Uri]::new($logs + [IO.Path]::DirectorySeparatorChar)
    $relativeUri = $baseUri.MakeRelativeUri([Uri]::new($modelFile.FullName))
    $relativeModel = if ($relativeUri.IsAbsoluteUri) { $modelFile.FullName } else { [Uri]::UnescapeDataString($relativeUri.OriginalString) }
    $modelArguments = @('--host', '127.0.0.1', '--port', $port, '--alias', $pin.Alias,
        '--model', ('"' + $relativeModel + '"'), '--api-key-file', 'session.key',
        '--ctx-size', '2048', '--parallel', '1', '--n-gpu-layers', '99', '--reasoning', 'off', '--no-webui') -join ' '
    $modelJob = [StudyModelProcess]::new($serverFile.FullName, $modelArguments, $logs, (Join-Path $logs 'server.out'), $stderr)
    $serverProcess = $modelJob.Process
    $limit = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false
    Add-Type -AssemblyName System.Net.Http
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $http = [System.Net.Http.HttpClient]::new($handler)
    $http.Timeout = [TimeSpan]::FromSeconds(2)
    $http.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $key)
    if ($Player) { Write-Host 'Starting the local translation model...' }
    while (!$serverProcess.HasExited -and $limit.Elapsed.TotalSeconds -lt 90) {
        $response = $null
        try {
            $response = $http.GetAsync("http://127.0.0.1:${port}/v1/models").GetAwaiter().GetResult()
            if ($response.IsSuccessStatusCode) {
                $r = ConvertFrom-Json -InputObject $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                if ($r.data.id -contains $pin.Alias) { $ready = $true; break }
            }
        } catch [System.Net.Http.HttpRequestException] {
        } catch [System.Threading.Tasks.TaskCanceledException] {
        } finally { if ($response) { $response.Dispose() } }
        Start-Sleep -Milliseconds 350
    }
    if (!$ready) { throw "Local candidate did not start. See $stderr" }
    # The model reads the key at startup; do not leave the file around for the player session.
    Remove-Item -LiteralPath $keyFile
    $env:LLPLAYER_STUDY_LOCAL_KEY = $key
    $env:LLPLAYER_STUDY_LOCAL_PORT = $port
    if ($CheckOnly) {
        Write-Output "PASS local model ready on 127.0.0.1:${port} ($($pin.Name)); no player was opened."
    } elseif ($VerifyPackage) {
        & (Join-Path $PSScriptRoot 'check-player-engine.ps1') -Video $VerifyEpisode -PlayerPackage $VerifyPackage -VerifyPilotModel
    } elseif ($Player) {
        Write-Host 'Local translation is ready. Leave this window open while using the player.'
        $app = Start-Process -FilePath $Player -WorkingDirectory (Split-Path $Player -Parent) -PassThru
        $app.WaitForExit()
        if ($app.ExitCode -ne 0) { throw "The player exited with code $($app.ExitCode)." }
    } else {
        $arguments = @{}
        if ($Samples) { $arguments.Samples = $Samples }
        if ($UnverifiedPilot) { $arguments.UnverifiedPilot = $true }
        if ($Probe8B) { $arguments.Probe8B = $true }
        $raw = if ($SamplesPackage) {
            & (Join-Path $PSScriptRoot 'check-player-engine.ps1') -PlayerPackage $SamplesPackage -PackagedSamples $Samples -UnverifiedPilot:$UnverifiedPilot
        } else {
            & (Join-Path $PSScriptRoot 'check-study-model.ps1') @arguments
        }
        $summary = ConvertFrom-Json -InputObject $raw -AsHashtable
        $summary.localModel = @{name=$pin.Name; sha256=$pin.Hash; bytes=$pin.Bytes}
        $json = ConvertTo-Json -InputObject $summary -Depth 10
        if ($Report) {
            $output = [IO.File]::Open($Report, [IO.FileMode]::CreateNew)
            try { $output.Write([Text.Encoding]::UTF8.GetBytes(($json -join "`n"))) } finally { $output.Dispose() }
        }
        $json
        if ($summary.translated -ne $summary.requested) {
            if (!$Samples) { throw 'One or more smoke samples did not return a translation.' }
            Write-Warning 'Some sample sentences returned no translation. The report preserves those blanks for scoring.'
        }
    }
} finally {
    $env:LLPLAYER_STUDY_LOCAL_KEY = $previous
    $env:LLPLAYER_STUDY_LOCAL_PORT = $previousPort
    if ($http) { $http.Dispose() }
    if ($modelJob) { $modelJob.Dispose() }
    if ($serverProcess) {
        try { $serverProcess.WaitForExit() } finally { $serverProcess.Dispose() }
    }
    if (Test-Path -LiteralPath $keyFile -PathType Leaf) { Remove-Item -LiteralPath $keyFile }
}
