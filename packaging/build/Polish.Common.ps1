#Requires -Version 7.4

$script:PolishRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:PolishBaseHead = 'da101d87681bb4d1d87a59884bca95043dd1158b'
$script:PolishUtf8 = [Text.UTF8Encoding]::new($false, $true)
$script:PolishFfmpegNames = @(
    'avcodec-62.dll', 'avdevice-62.dll', 'avfilter-11.dll', 'avformat-62.dll',
    'avutil-60.dll', 'swresample-6.dll', 'swscale-9.dll'
)
$script:PolishReviewNames = @('review/OriginalReview/WordImport.cs', 'review/OriginalReview/ReviewStore.cs')

function Assert-PolishRelative([string]$Name) {
    if (!$Name -or $Name.Length -gt 1024) { throw "Invalid relative path: $Name" }
    foreach ($part in $Name.Split('/')) {
        if (!$part -or $part -eq '.' -or $part -eq '..' -or $part -match '[\x00-\x1f\x7f<>:"\\|?*~]|[ .]$' -or
            $part -match '^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9\u00b9\u00b2\u00b3]|LPT[0-9\u00b9\u00b2\u00b3]) *(\.|$)') {
            throw "Unsafe relative path: $Name"
        }
    }
}

function Assert-PolishPlainPath([string]$Path, [switch]$Directory, [switch]$AllowMissingLeaf) {
    if ($Path -notmatch '^[a-zA-Z]:[\\/]' -or !$IsWindows) { throw "Expected an absolute local Windows path: $Path" }
    $tail = $Path.Substring(3).Replace('\', '/').TrimEnd('/')
    if ($tail) { Assert-PolishRelative $tail }
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    $current = [IO.Path]::GetPathRoot($full)
    $parts = @(if ($tail) { $tail.Split('/') })
    for ($i = -1; $i -lt $parts.Count; $i++) {
        if ($i -ge 0) { $current = [IO.Path]::Combine($current, $parts[$i]) }
        $leaf = $i -eq ($parts.Count - 1)
        try { $attrs = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] {
            if ($leaf -and $AllowMissingLeaf) { return $full }
            throw
        }
        if ($attrs -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point is not allowed: $current" }
        $needDir = !$leaf -or $Directory
        if ($needDir -ne [bool]($attrs -band [IO.FileAttributes]::Directory)) { throw "Unexpected path type: $current" }
    }
    return $full
}

function Join-PolishPath([string]$Root, [string]$Name) {
    Assert-PolishRelative $Name
    return [IO.Path]::Combine($Root, $Name.Replace('/', '\'))
}

function New-PolishDirectory([string]$Path) {
    $Path = Assert-PolishPlainPath $Path -Directory -AllowMissingLeaf
    if ([IO.Directory]::Exists($Path)) { throw "Output directory already exists: $Path" }
    [void][IO.Directory]::CreateDirectory($Path)
    return (Assert-PolishPlainPath $Path -Directory)
}

function New-PolishRun([string]$Kind) {
    $pilot = Assert-PolishPlainPath (Join-PolishPath $script:PolishRoot '.pilot') -Directory
    $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
    return (New-PolishDirectory (Join-PolishPath $pilot "polish-$Kind-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$id"))
}

function Open-PolishInput([string]$Path) {
    $Path = Assert-PolishPlainPath $Path
    return [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
}

function Open-PolishOutput([string]$Path) {
    $Path = Assert-PolishPlainPath $Path -AllowMissingLeaf
    return [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
}

function Write-PolishBytes([string]$Path, [byte[]]$Bytes) {
    $stream = Open-PolishOutput $Path
    try { $stream.Write($Bytes) } finally { $stream.Dispose() }
}

function Write-PolishText([string]$Path, [string]$Text) {
    Write-PolishBytes $Path ($script:PolishUtf8.GetBytes($Text + "`n"))
}

function Get-PolishHash([IO.Stream]$Stream) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Stream)).ToLowerInvariant()
}

function Get-PolishFileHash([string]$Path) {
    $stream = Open-PolishInput $Path
    try { return (Get-PolishHash $stream) } finally { $stream.Dispose() }
}

function Copy-PolishInput([string]$Source, [string]$Destination, [string]$ExpectedHash) {
    $inputStream = Open-PolishInput $Source
    try {
        if ($ExpectedHash) {
            if ((Get-PolishHash $inputStream) -cne $ExpectedHash) { throw "Input SHA256 mismatch: $Source" }
            $inputStream.Position = 0
        }
        $outputStream = Open-PolishOutput $Destination
        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }
}

function Get-PolishTreeFiles([string]$Root, [switch]$CheckOnly) {
    $Root = Assert-PolishPlainPath $Root -Directory
    $dirs = [Collections.Generic.Stack[string]]::new()
    $dirs.Push($Root)
    while ($dirs.Count) {
        $dir = Assert-PolishPlainPath ($dirs.Pop()) -Directory
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($dir)) {
            $relative = [IO.Path]::GetRelativePath($Root, $path).Replace('\', '/')
            Assert-PolishRelative $relative
            $attrs = [IO.File]::GetAttributes($path)
            if ($attrs -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point is not allowed: $path" }
            if ($attrs -band [IO.FileAttributes]::Directory) { $dirs.Push($path) }
            elseif (!$CheckOnly) { $relative }
        }
    }
}

function Test-PolishPrivatePath([string]$Name) {
    return ($Name -match '(?:^|/)(?:\.git(?:ignore|attributes|modules)?|\.svn|\.hg|\.vs|\.idea|\.vscode|\.aws|\.ssh|bin|obj|node_modules|AppData|usersecrets|[sS]ecrets?|[cC]redentials?)(?:/|$)' -or
        $Name -match '(?:^|/)(?:\.env(?:[./]|$)|\.npmrc$|\.netrc$|known_hosts$|id_(?:rsa|dsa|ecdsa|ed25519)(?:\.|$)|(?:secrets?|credentials?|token|api[-_]?key|private[-_]?key)(?:[._-][^/]+|$))' -or
        $Name -match '\.(?:pfx|p12|pem|key|snk|kdbx|db|sqlite|sqlite3|user|suo|log|bak|tmp|dmp)$')
}

function Test-PolishSnapshotExcluded([string]$Name) {
    return ((Test-PolishPrivatePath $Name) -or
        $Name -match '(?:^|/)(?:\.editorconfig|\.gitconfig|global\.json|llplayer\.config)(?:/|$)' -or
        $Name -match '\.(?:jsonc?|ya?ml|ini|toml|conf|config|pubxml|settings|runsettings|psd1|reg)$')
}

function New-PolishPathIndex([string[]]$Names) {
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $dirs = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $Names) {
        Assert-PolishRelative $name
        if (!$files.Add($name)) { throw "Duplicate path: $name" }
        $cut = $name.LastIndexOf('/')
        while ($cut -ge 0) {
            $dir = $name.Substring(0, $cut)
            if ($dirs.ContainsKey($dir) -and $dirs[$dir] -cne $dir) { throw "Case-colliding directory: $dir" }
            $dirs[$dir] = $dir
            $cut = $dir.LastIndexOf('/')
        }
    }
    foreach ($dir in $dirs.Keys) {
        if ($files.Contains($dir)) { throw "A ZIP path is both a directory and a file: $dir" }
    }
    return ,$files
}

function New-PolishArchive([string]$Tree, [string[]]$Names, [string]$ZipPath, [string]$SourceHead, [string]$ManifestPath) {
    $Tree = Assert-PolishPlainPath $Tree -Directory
    $namesCopy = [string[]]$Names.Clone()
    [Array]::Sort($namesCopy, [StringComparer]::Ordinal)
    $paths = if ($SourceHead) { $namesCopy + 'source-manifest.json' } else { $namesCopy }
    $null = New-PolishPathIndex $paths
    $rows = [Collections.Generic.List[object]]::new()
    $fixedTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $stream = Open-PolishOutput $ZipPath
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true, $script:PolishUtf8)
        try {
            foreach ($name in $namesCopy) {
                $source = Open-PolishInput (Join-PolishPath $Tree $name)
                try {
                    $rows.Add([ordered]@{ path = $name; sha256 = (Get-PolishHash $source); bytes = $source.Length })
                    $source.Position = 0
                    $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Fastest)
                    $entry.LastWriteTime = $fixedTime
                    $dest = $entry.Open()
                    try { $source.CopyTo($dest) } finally { $dest.Dispose() }
                } finally { $source.Dispose() }
            }
            if ($SourceHead) {
                $manifest = [ordered]@{
                    schema = 1; upstreamHead = $SourceHead; requiredUpstreamHead = $script:PolishBaseHead; files = $rows.ToArray()
                }
                $bytes = $script:PolishUtf8.GetBytes((ConvertTo-Json -InputObject $manifest -Depth 5) + "`n")
                $entry = $archive.CreateEntry('source-manifest.json')
                $entry.LastWriteTime = $fixedTime
                $dest = $entry.Open()
                try { $dest.Write($bytes) } finally { $dest.Dispose() }
                Write-PolishBytes $ManifestPath $bytes
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
    return $rows.ToArray()
}

function Read-PolishManifest([string]$Path, [switch]$Source) {
    $stream = Open-PolishInput $Path
    try {
        $size = $stream.Length
        if ($size -gt 8MB) { throw "Manifest exceeds 8 MiB: $Path" }
        $hash = Get-PolishHash $stream
        $stream.Position = 0
        $reader = [IO.StreamReader]::new($stream, $script:PolishUtf8, $false)
        try { $json = ConvertFrom-Json -InputObject ($reader.ReadToEnd()) -AsHashtable -Depth 8 }
        finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
    if ($json -isnot [Collections.IDictionary] -or $json['files'] -isnot [Array] -or !$json['files'].Count) {
        throw "Invalid manifest: $Path"
    }
    if ($Source) {
        if ($json.Count -ne 4 -or $json['schema'] -ne 1 -or $json['upstreamHead'] -isnot [string] -or
            $json['upstreamHead'] -cnotmatch '\A[0-9a-f]{40}\z' -or $json['requiredUpstreamHead'] -cne $script:PolishBaseHead) {
            throw 'Invalid frozen upstream revision metadata.'
        }
    } elseif ($json.Count -ne 1) { throw "Unexpected payload manifest metadata: $Path" }
    foreach ($row in $json['files']) {
        if ($row -isnot [Collections.IDictionary] -or $row.Count -ne 3 -or $row['path'] -isnot [string] -or
            $row['sha256'] -isnot [string] -or $row['sha256'] -cnotmatch '\A[0-9a-f]{64}\z' -or
            ($row['bytes'] -isnot [int] -and $row['bytes'] -isnot [long]) -or $row['bytes'] -lt 0) {
            throw "Invalid file record in manifest: $Path"
        }
    }
    $null = New-PolishPathIndex @($json['files'].path)
    return [pscustomobject]@{ Data = $json; Sha256 = $hash; Bytes = $size }
}

function Write-PolishZipChecksum([string]$ZipPath) {
    $hash = Get-PolishFileHash $ZipPath
    Write-PolishText "$ZipPath.sha256" "$hash  $([IO.Path]::GetFileName($ZipPath))"
    return $hash
}

function Open-PolishCheckedZip([string]$ZipPath) {
    $checksum = Open-PolishInput "$ZipPath.sha256"
    try {
        if ($checksum.Length -gt 128) { throw "Invalid SHA256 sidecar: $ZipPath.sha256" }
        $reader = [IO.StreamReader]::new($checksum, $script:PolishUtf8, $false)
        try { $expected = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $checksum.Dispose() }
    $zipName = [regex]::Escape([IO.Path]::GetFileName($ZipPath))
    if ($expected -cnotmatch "\A([0-9a-f]{64})  $zipName\r?\n\z") { throw "Invalid SHA256 sidecar: $ZipPath.sha256" }
    $expectedHash = $Matches[1]
    $stream = Open-PolishInput $ZipPath
    try {
        if ((Get-PolishHash $stream) -cne $expectedHash) { throw "ZIP SHA256 mismatch: $ZipPath" }
        $stream.Position = 0
        return $stream
    } catch { $stream.Dispose(); throw }
}

function New-PolishExtractParent([string]$Root, [string]$Name) {
    $parts = $Name.Split('/')
    $dir = $Root
    for ($i = 0; $i -lt $parts.Count - 1; $i++) {
        $dir = Assert-PolishPlainPath ([IO.Path]::Combine($dir, $parts[$i])) -Directory -AllowMissingLeaf
        if (![IO.Directory]::Exists($dir)) { $dir = New-PolishDirectory $dir }
    }
}

function Confirm-PolishZip([IO.Stream]$Stream, [object[]]$Rows, [string]$ExtractRoot, [string]$SkipExtract) {
    $null = New-PolishPathIndex @($Rows.path)
    $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($row in $Rows) { $expected.Add($row.path, $row) }
    if ($ExtractRoot) {
        $ExtractRoot = Assert-PolishPlainPath $ExtractRoot -Directory
        if (@([IO.Directory]::EnumerateFileSystemEntries($ExtractRoot)).Count) { throw 'Extraction requires an empty directory.' }
    }
    $archive = [IO.Compression.ZipArchive]::new($Stream, [IO.Compression.ZipArchiveMode]::Read, $true, $script:PolishUtf8)
    try {
        $null = New-PolishPathIndex @($archive.Entries.FullName)
        if ($archive.Entries.Count -ne $expected.Count) { throw 'ZIP entry count differs from manifest.' }
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            $type = ($entry.ExternalAttributes -shr 16) -band 0xf000
            if (($type -ne 0 -and $type -ne 0x8000) -or ($entry.ExternalAttributes -band 0x410)) {
                throw "ZIP entry is not a plain file: $name"
            }
            if (!$expected.ContainsKey($name) -or $entry.Length -ne $expected[$name].bytes) {
                throw "Unexpected ZIP entry or length: $name"
            }
        }
        $buffer = [byte[]]::new(1MB)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            $row = $expected[$name]
            $source = $entry.Open()
            $dest = $null
            $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            try {
                if ($ExtractRoot -and $name -cne $SkipExtract) {
                    New-PolishExtractParent $ExtractRoot $name
                    $dest = Open-PolishOutput (Join-PolishPath $ExtractRoot $name)
                }
                $bytes = 0L
                while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $bytes += $read
                    if ($bytes -gt $row.bytes) { throw "ZIP entry exceeds declared length: $name" }
                    $hash.AppendData($buffer, 0, $read)
                    if ($null -ne $dest) { $dest.Write($buffer, 0, $read) }
                }
                if ($bytes -ne $row.bytes -or [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant() -cne $row.sha256) {
                    throw "ZIP entry SHA256 or length mismatch: $name"
                }
            } finally {
                if ($null -ne $dest) { $dest.Dispose() }
                $source.Dispose()
                $hash.Dispose()
            }
        }
    } finally { $archive.Dispose() }
}

function Confirm-PolishSnapshot([string]$Snapshot, [string]$ExtractRoot) {
    $Snapshot = Assert-PolishPlainPath $Snapshot -Directory
    $manifestPath = Join-PolishPath $Snapshot 'source-manifest.json'
    $manifest = Read-PolishManifest $manifestPath -Source
    $rows = $manifest.Data.files
    $names = [Collections.Generic.HashSet[string]]::new([string[]]$rows.path, [StringComparer]::Ordinal)
    foreach ($name in $names) {
        if (Test-PolishSnapshotExcluded $name) { throw "Excluded snapshot path: $name" }
        if ($name -cnotmatch '\A(?:upstream/LLPlayer/.+|cloud/desktop/[^/]+\.[cC][sS])\z' -and $name -cnotin $script:PolishReviewNames) {
            throw "Path outside frozen build inputs: $name"
        }
    }
    $required = $script:PolishReviewNames + @( 'cloud/desktop/CloudReviewWindow.cs',
        'upstream/LLPlayer/LLPlayer/LLPlayer.csproj', 'upstream/LLPlayer/LLPlayer/LLPlayer.ico',
        'upstream/LLPlayer/LLPlayer/Controls/SubtitlesControl.xaml.cs', 'upstream/LLPlayer/LLPlayer/Resources/PopupMenu.xaml.cs',
        'upstream/LLPlayer/FlyleafLib/FlyleafLib.csproj', 'upstream/LLPlayer/WpfColorFontDialog/WpfColorFontDialog.csproj' )
    $required += @($script:PolishFfmpegNames | ForEach-Object { "upstream/LLPlayer/FFmpeg/$_" })
    foreach ($name in $required) { if (!$names.Contains($name)) { throw "Missing required frozen input: $name" } }
    $allRows = @($rows) + @(@{ path = 'source-manifest.json'; sha256 = $manifest.Sha256; bytes = $manifest.Bytes })
    $stream = Open-PolishCheckedZip (Join-PolishPath $Snapshot 'source.zip')
    try { Confirm-PolishZip $stream $allRows -ExtractRoot $ExtractRoot -SkipExtract 'source-manifest.json' }
    finally { $stream.Dispose() }
    return $manifest.Data
}
