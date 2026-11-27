#requires -Version 7.0

$script:ExpectedManifestHash = '557137312A4AF704083B6C5850B89AB047AE436E7686A13AA77681C8055E79EE'
$script:ExpectedPayloadCount = 972
$script:ExpectedPayloadBytes = 821867840
$script:CompilerPath = 'C:\Program Files (x86)\NSIS\Bin\makensis.exe'
$script:CompilerHash = '25D1AA7081DB1A9DE9690B59983F9652B7409A189C3444328CC1841EFF693E8D'
$script:InstallerName = 'LLPlayer-Anki-0.3.0-polish.1-win-x64-setup.exe'
$script:Workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:PilotRoot = Join-Path $script:Workspace '.pilot'

function Assert-PlainPath {
    param([Parameter(Mandatory)][string]$Path, [switch]$AllowMissing)

    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    if ($full -notmatch '^[A-Za-z]:\\' -or $full -match '["$\r\n]' -or $full.Substring(2).Contains(':')) {
        throw "Expected an ordinary local Windows path: $full"
    }
    for ($part = $full; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        try { $attributes = [IO.File]::GetAttributes($part) }
        catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] {
            if (-not $AllowMissing) { throw }
            continue
        }
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse path blocked: $part" }
    }
    $full
}

function Get-RunDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $run = Assert-PlainPath -Path $Path -AllowMissing
    if ([IO.Path]::GetDirectoryName($run) -ine $script:PilotRoot) {
        throw "The run directory must be a direct child of $script:PilotRoot"
    }
    $run
}

function Get-PinnedManifest {
    param([Parameter(Mandatory)][string]$Path)

    $full = Assert-PlainPath $Path
    $raw = [IO.File]::ReadAllBytes($full)
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))
    if ($digest -cne $script:ExpectedManifestHash) { throw "Unexpected release manifest SHA-256: $digest" }
    $json = [Text.Encoding]::UTF8.GetString($raw).TrimStart([char]0xFEFF) | ConvertFrom-Json -AsHashtable
    $files = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $dirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $total = 0L
    $longest = 0
    foreach ($item in $json.files) {
        $parts = ([string]$item.path).Split('/')
        foreach ($part in $parts) {
            if ($part -notmatch '^[A-Za-z0-9_][A-Za-z0-9._-]*$' -or $part.EndsWith('.') -or
                $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') { throw "Unsafe manifest path: $($item.path)" }
        }
        $rel = $parts -join '\'
        if ($rel -match '^(Uninstall\.exe|LLPlayer\.(Config|PlayerConfig|Engine)\.json|PrivateReview(\\|$))' -or
            $item.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$item.bytes -le 0) {
            throw "Invalid or reserved manifest entry: $rel"
        }
        $files.Add($rel, $item)
        $total += [long]$item.bytes
        $longest = [Math]::Max($longest, $rel.Length)
        $dir = [IO.Path]::GetDirectoryName($rel)
        while ($dir) {
            [void]$dirs.Add($dir)
            $dir = [IO.Path]::GetDirectoryName($dir)
        }
    }
    if ($files.Count -ne $script:ExpectedPayloadCount -or $total -ne $script:ExpectedPayloadBytes -or -not $files.ContainsKey('LLPlayer.exe')) {
        throw 'Release manifest count, size, or entry point does not match the accepted candidate.'
    }
    foreach ($dir in $dirs) { if ($files.ContainsKey($dir)) { throw "File/directory collision: $dir" } }
    [pscustomobject]@{ Path = $full; Hash = $digest; Files = $files; Directories = $dirs; Bytes = $total; Longest = $longest }
}

function Get-PlainTree {
    param([Parameter(Mandatory)][string]$Root)

    $resolved = Assert-PlainPath $Root
    $queue = [Collections.Generic.Stack[string]]::new()
    $queue.Push($resolved)
    while ($queue.Count -gt 0) {
        foreach ($entry in [IO.DirectoryInfo]::new($queue.Pop()).EnumerateFileSystemInfos()) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse entry blocked: $($entry.FullName)" }
            if ($entry.Attributes -band [IO.FileAttributes]::Directory) { $queue.Push($entry.FullName); continue }
            $entry
        }
    }
}

function Assert-PayloadFile {
    param([Parameter(Mandatory)][IO.FileInfo]$File, [Parameter(Mandatory)]$Item)

    if ($File.Length -ne [long]$Item.bytes -or (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash -ine $Item.sha256) {
        throw "Payload does not match the manifest: $($File.FullName)"
    }
}

function New-HiddenProcess {
    param([string]$FilePath, [string]$Directory, [string[]]$Arguments, [hashtable]$ChildEnvironment = @{})

    $info = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $info.WorkingDirectory = $Directory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    foreach ($key in $ChildEnvironment.Keys) { $info.Environment[$key] = $ChildEnvironment[$key] }
    $info
}

function Invoke-HiddenProcess {
    param([Parameter(Mandatory)][Diagnostics.ProcessStartInfo]$Info, [int]$TimeoutSeconds = 60)

    $process = [Diagnostics.Process]::Start($Info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            [void]$process.WaitForExit(10000)
            throw "Process timed out: $($Info.FileName)"
        }
        [pscustomobject]@{ ExitCode = $process.ExitCode; Out = $stdout.GetAwaiter().GetResult(); Err = $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}
