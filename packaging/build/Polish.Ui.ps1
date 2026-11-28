#Requires -Version 7.4

function Get-PolishUiFiles {
    $ui = Assert-PolishPlainPath (Join-PolishPath $script:PolishRoot 'packaging/ui') -Directory
    foreach ($candidate in [IO.Directory]::EnumerateFileSystemEntries($ui)) {
        $name = [IO.Path]::GetFileName($candidate)
        if ($name -notmatch '\.cs$') { continue }
        if (Test-PolishPrivatePath $name) { throw "Private path is not permitted as an injected source: $name" }
        $null = Assert-PolishPlainPath $candidate
        $name
    }
}

function Set-PolishCallSite([string]$Path, [string]$Pattern) {
    $Path = Assert-PolishPlainPath $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        if ($stream.Length -gt 1MB) { throw "Unexpectedly large call-site source: $Path" }
        $bytes = [byte[]]::new([int]$stream.Length)
        $stream.ReadExactly($bytes)
        $text = $script:PolishUtf8.GetString($bytes)
        $regex = [regex]::new($Pattern, [Text.RegularExpressions.RegexOptions]::Multiline)
        if ($regex.Matches($text).Count -ne 1) { throw "Expected exactly one original call site: $Path" }
        $patched = $regex.Replace($text, '${1}PolishedReviewWindow${2}')
        $stream.Position = 0
        $stream.Write($script:PolishUtf8.GetBytes($patched))
        $stream.SetLength($stream.Position)
    } finally { $stream.Dispose() }
}

function Add-PolishUi([string]$SourceRoot, [string[]]$Names) {
    $desktopRel = 'cloud/desktop'
    $desktop = Assert-PolishPlainPath (Join-PolishPath $SourceRoot $desktopRel) -Directory
    $existing = @(Get-ChildItem -LiteralPath $desktop -Force | ForEach-Object Name)
    $null = New-PolishPathIndex ($existing + $Names)
    foreach ($name in $Names) {
        Copy-PolishInput (Join-PolishPath $script:PolishRoot "packaging/ui/$name") (Join-PolishPath $desktop $name)
    }
    Set-PolishCallSite (Join-PolishPath $SourceRoot 'upstream/LLPlayer/LLPlayer/Controls/SubtitlesControl.xaml.cs') `
        '^([ \t]*)CloudReviewWindow(\.Open\(id\);[ \t]*\r?$)'
    Set-PolishCallSite (Join-PolishPath $SourceRoot 'upstream/LLPlayer/LLPlayer/Resources/PopupMenu.xaml.cs') `
        '^([ \t]*if \(PrivateReviewVisibility == Visibility\.Visible\) LLPlayer\.Services\.)CloudReviewWindow(\.Open\(\);[ \t]*\r?$)'
}
