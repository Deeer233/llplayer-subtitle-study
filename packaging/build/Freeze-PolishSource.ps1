#Requires -Version 7.4

[CmdletBinding(DefaultParameterSetName = 'Freeze')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Verify')]
    [string]$VerifySnapshot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Polish.Common.ps1')

if ($PSCmdlet.ParameterSetName -eq 'Verify') {
    $snapshot = Assert-PolishPlainPath $VerifySnapshot -Directory
    $verified = Confirm-PolishSnapshot $snapshot
    [pscustomobject]@{ Snapshot = $snapshot; UpstreamHead = $verified.upstreamHead; SourceFiles = $verified.files.Count }
    return
}

$upstream = Assert-PolishPlainPath (Join-PolishPath $script:PolishRoot 'upstream/LLPlayer') -Directory
$null = Assert-PolishPlainPath (Join-PolishPath $upstream '.git') -Directory
$git = (Get-Command git.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source

function Read-FreezeGit([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $git
    $info.WorkingDirectory = $upstream
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $script:PolishUtf8
    foreach ($name in @($info.Environment.Keys)) {
        if ($name -like 'GIT_*') { [void]$info.Environment.Remove($name) }
    }
    $info.Environment['GIT_TERMINAL_PROMPT'] = '0'
    $info.Environment['GIT_NO_LAZY_FETCH'] = '1'
    $info.Environment['GIT_NO_REPLACE_OBJECTS'] = '1'
    $safeDirectory = $upstream.Replace('\', '/')
    foreach ($arg in @('--no-optional-locks', '-c', "safe.directory=$safeDirectory", '-C', $upstream) + $Arguments) {
        $info.ArgumentList.Add($arg)
    }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Git $($Arguments -join ' ') failed ($($process.ExitCode)): $($stderr.GetAwaiter().GetResult())" }
        return $stdout.GetAwaiter().GetResult()
    } finally { $process.Dispose() }
}

function Get-FreezeHead {
    $top = (Read-FreezeGit @('rev-parse', '--show-toplevel')).Trim()
    if ($top.Replace('/', '\') -ine $upstream) { throw "Git worktree does not match the expected upstream: $top" }
    $head = (Read-FreezeGit @('rev-parse', '--verify', 'HEAD^{commit}')).Trim()
    if ($head -cnotmatch '\A[0-9a-f]{40}\z') { throw 'Expected a full upstream SHA-1 commit ID.' }
    $null = Read-FreezeGit @('merge-base', '--is-ancestor', $script:PolishBaseHead, $head)
    return $head
}

function Get-FreezeSelection {
    $tracked = Read-FreezeGit @('ls-files', '--stage', '-z')
    foreach ($item in $tracked.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        $tab = $item.IndexOf("`t")
        if ($tab -lt 0) { throw 'Malformed Git index record.' }
        $name = $item.Substring($tab + 1)
        if (Test-PolishSnapshotExcluded $name) { continue }
        Assert-PolishRelative $name
        if ($item.Substring(0, $tab) -cnotmatch '\A100(?:644|755) [0-9a-f]{40} 0\z') {
            throw "Git index entry is not a regular resolved file: $name"
        }
        "upstream/LLPlayer/$name"
    }
    $untracked = Read-FreezeGit @('ls-files', '--others', '--exclude-standard', '-z')
    foreach ($name in $untracked.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        if (Test-PolishSnapshotExcluded $name) { continue }
        if ($name -match '\.(?:cs|xaml|resx|csproj|props|targets)$' -or
            $name -match '(?:^|/)(?:Assets|Resources|Images|Themes)/.+\.(?:png|jpe?g|gif|bmp|ico|svg|mp3|wav|ttf|otf)$') {
            Assert-PolishRelative $name
            "upstream/LLPlayer/$name"
        }
    }
    $desktop = Assert-PolishPlainPath (Join-PolishPath $script:PolishRoot 'cloud/desktop') -Directory
    foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($desktop)) {
        $name = [IO.Path]::GetFileName($path)
        if ($name -notmatch '\.cs$' -or (Test-PolishSnapshotExcluded $name)) { continue }
        $null = Assert-PolishPlainPath $path
        "cloud/desktop/$name"
    }
    $script:PolishReviewNames
}

$head = Get-FreezeHead
$selection = [string[]]@(Get-FreezeSelection)
$null = New-PolishPathIndex $selection
$run = New-PolishRun 'freeze'
Write-Verbose "Writing new snapshot: $run"
$zip = Join-PolishPath $run 'source.zip'
$manifest = Join-PolishPath $run 'source-manifest.json'
$rows = @(New-PolishArchive $script:PolishRoot $selection $zip -SourceHead $head -ManifestPath $manifest)

# Detect working-file or selection changes rather than silently freezing a moving tree.
$after = [string[]]@(Get-FreezeSelection)
$afterSet = New-PolishPathIndex $after
if ($after.Count -ne $selection.Count -or !$afterSet.SetEquals($selection) -or (Get-FreezeHead) -cne $head) {
    throw "Git HEAD or input list changed during freezing. Do not use: $run"
}
foreach ($row in $rows) {
    if ((Get-PolishFileHash (Join-PolishPath $script:PolishRoot $row.path)) -cne $row.sha256) {
        throw "Working bytes changed during freezing: $($row.path). Do not use: $run"
    }
}

$hash = Write-PolishZipChecksum $zip
$verified = Confirm-PolishSnapshot $run
foreach ($path in @($zip, "$zip.sha256", $manifest)) {
    [IO.File]::SetAttributes($path, ([IO.File]::GetAttributes($path) -bor [IO.FileAttributes]::ReadOnly))
}
[pscustomobject]@{
    Snapshot = $run; Zip = $zip; Sha256File = "$zip.sha256"; Sha256 = $hash; Manifest = $manifest
    UpstreamHead = $verified.upstreamHead; SourceFiles = $verified.files.Count
}
