#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$RunDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Installer.Common.ps1')

$run = Get-RunDirectory $RunDirectory
$root = Assert-PlainPath $PayloadRoot
$manifest = Get-PinnedManifest $ManifestPath
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in (Get-PlainTree $root)) {
    $rel = [IO.Path]::GetRelativePath($root, $file.FullName)
    if (-not $manifest.Files.ContainsKey($rel) -or -not $seen.Add($rel)) { throw "Unlisted or duplicate payload file: $rel" }
    Assert-PayloadFile $file $manifest.Files[$rel]
}
if ($seen.Count -ne $manifest.Files.Count) { throw "Payload is incomplete: $($seen.Count)/$($manifest.Files.Count)" }

$compiler = Assert-PlainPath $script:CompilerPath
if ((Get-FileHash -LiteralPath $compiler).Hash -cne $script:CompilerHash) { throw "Unverified compiler: $compiler" }
$nsis = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($compiler))
foreach ($dependency in @('Bin\zlib1.dll', 'Include\LogicLib.nsh', 'Include\FileFunc.nsh',
        'Stubs\lzma_solid-x86-unicode', 'Plugins\x86-unicode\System.dll',
        'Contrib\Language files\English.nlf', 'Contrib\Language files\SimpChinese.nlf')) {
    [void](Assert-PlainPath (Join-Path $nsis $dependency))
}

[void][IO.Directory]::CreateDirectory($run)
$build = Join-Path $run ('build-' + [Guid]::NewGuid().ToString('N').Substring(0, 10))
if (Test-Path -LiteralPath $build) { throw "Build path already exists: $build" }
[void][IO.Directory]::CreateDirectory($build)
$tmp = Join-Path $build 'tmp'
[void][IO.Directory]::CreateDirectory($tmp)
$environment = @{ NSISDIR = $nsis; TMP = $tmp; TEMP = $tmp }
$info = New-HiddenProcess $compiler $build @('/VERSION') $environment
$version = Invoke-HiddenProcess $info 10
if ($version.ExitCode -ne 0 -or $version.Out.Trim() -cne 'v3.12') { throw "Compiler probe failed: $($version.Out) $($version.Err)" }

$include = Join-Path $build 'Payload.generated.nsh'
$output = Join-Path $build $script:InstallerName
$source = Assert-PlainPath (Join-Path $PSScriptRoot 'LLPlayer.nsi')
[string[]]$files = @($manifest.Files.Keys)
[Array]::Sort($files, [StringComparer]::OrdinalIgnoreCase)
[string[]]$dirs = @($manifest.Directories | Sort-Object { $_.Length }, { $_ })
$lines = [Collections.Generic.List[string]]::new()
$lines.Add(('!define PAYLOAD_SHA256 "{0}"' -f $manifest.Hash))
$lines.Add(('!define PAYLOAD_MAX_RELATIVE {0}' -f $manifest.Longest))
$lines.Add(('!define PAYLOAD_KIB {0}' -f [long][Math]::Ceiling($manifest.Bytes / 1KB)))
$lines.Add('!macro PayloadDirectories Prefix Failure')
foreach ($dir in $dirs) { $lines.Add(('  !insertmacro CheckPayloadDirectory "${{Prefix}}" "{0}" "${{Failure}}"' -f $dir)) }
$lines.Add('!macroend')
$lines.Add('!macro PayloadAbsent')
foreach ($rel in $files) { $lines.Add(('  !insertmacro RequireAbsent "$INSTDIR\{0}"' -f $rel)) }
$lines.Add('!macroend')
$lines.Add('!macro PayloadInstall')
$last = $null
foreach ($rel in $files) {
    $dir = [IO.Path]::GetDirectoryName($rel)
    if ($dir -cne $last) {
        $outPath = if ($dir) { '$INSTDIR\' + $dir } else { '$INSTDIR' }
        $lines.Add(('  SetOutPath "{0}"' -f $outPath))
        $lines.Add('  IfErrors install_failed')
        $last = $dir
    }
    $lines.Add(('  File "{0}"' -f (Join-Path $root $rel)))
    $lines.Add('  IfErrors install_failed')
}
$lines.Add('!macroend')
$lines.Add('!macro PayloadUninstall')
foreach ($rel in $files) { $lines.Add(('  Delete "$INSTDIR\{0}"' -f $rel)) }
$lines.Add('!macroend')
$lines.Add('!macro PayloadRemoveEmptyDirectories')
for ($i = $dirs.Length - 1; $i -ge 0; $i--) { $lines.Add(('  RMDir "$INSTDIR\{0}"' -f $dirs[$i])) }
$lines.Add('!macroend')
[IO.File]::WriteAllLines($include, $lines, [Text.UTF8Encoding]::new($true))

$arguments = @('/NOCONFIG', '/NOCD', '/WX', '/V3', '/INPUTCHARSET', 'UTF8', '/OUTPUTCHARSET', 'UTF8',
    "/DPAYLOAD_INCLUDE=$include", "/DSETUP_OUTPUT=$output", $source)
Write-Host "Verified $($manifest.Files.Count) files; compiling in $build"
$result = Invoke-HiddenProcess (New-HiddenProcess $compiler $build $arguments $environment) 300
$log = Join-Path $build 'compile.log'
[IO.File]::WriteAllText($log, ($result.Out + $result.Err), [Text.UTF8Encoding]::new($false))
if ($result.ExitCode -ne 0 -or -not [IO.File]::Exists($output)) {
    throw "makensis exit $($result.ExitCode); log: $log`n$($result.Out)$($result.Err)"
}
$exe = [IO.FileInfo]::new($output)
$evidence = [ordered]@{
    compiler = $compiler
    compilerSha256 = $script:CompilerHash
    compilerVersion = $version.Out.Trim()
    manifest = $manifest.Path
    manifestSha256 = $manifest.Hash
    files = $manifest.Files.Count
    payloadBytes = $manifest.Bytes
    installer = $exe.FullName
    installerBytes = $exe.Length
    installerSha256 = (Get-FileHash -LiteralPath $exe.FullName).Hash
    compileLog = $log
}
$receipt = Join-Path $build 'build.json'
[IO.File]::WriteAllText($receipt, ($evidence | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
[pscustomobject]$evidence
