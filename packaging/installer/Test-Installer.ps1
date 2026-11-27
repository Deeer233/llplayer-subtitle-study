#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$InstallerSha256,
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$ManifestPath,
    [string]$RunDirectory,
    [string]$ExpectedUser,
    [string]$ExpectedPrograms,
    [switch]$VerifyArtifactsOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Installer.Common.ps1')

function Assert-AbsentFileSystemPath([string]$Path) {
    try { [void][IO.File]::GetAttributes($Path) }
    catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] { return }
    throw "Refusing to overwrite existing path: $Path"
}

function Assert-ReleaseZip([string]$Path, $Manifest) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.Replace('/', '\')
            if (-not $Manifest.Files.ContainsKey($relative) -or -not $seen.Add($relative)) {
                throw "Unlisted or duplicate ZIP entry: $($entry.FullName)"
            }
            $item = $Manifest.Files[$relative]
            $stream = $entry.Open()
            try { $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            if ($entry.FullName -cne $item.path -or $entry.Length -ne $item.bytes -or $digest -ine $item.sha256) {
                throw "ZIP file does not match the release manifest: $($entry.FullName)"
            }
        }
        if ($seen.Count -ne $Manifest.Files.Count) { throw 'ZIP is missing release files.' }
        $seen.Count
    } finally { $archive.Dispose() }
}

$regPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\LLPlayer-Anki-Polish-144e1786'
$linkName = 'LLPlayer Original Review (144e1786).lnk'

function Read-Registration(
    [Microsoft.Win32.RegistryHive]$Hive = 'CurrentUser',
    [Microsoft.Win32.RegistryView]$View = 'Registry64'
) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, $View)
    try {
        $key = $base.OpenSubKey($regPath, $false)
        if ($null -eq $key) { return }
        try {
            $values = [ordered]@{}
            foreach ($name in $key.GetValueNames()) {
                $values[$name] = $key.GetValue($name, $null, 'DoNotExpandEnvironmentNames')
            }
            $values
        } finally { $key.Dispose() }
    } finally { $base.Dispose() }
}

function Assert-NoMachineRegistration {
    foreach ($view in 'Registry64', 'Registry32') {
        if ($null -ne (Read-Registration -Hive LocalMachine -View $view)) {
            throw "Unexpected machine uninstall entry ($view): HKLM\$regPath"
        }
    }
}

function Invoke-QuietNsis([string]$Path, [string]$Arguments, [string]$LogName) {
    $process = New-HiddenProcess -FilePath $Path -Directory $run -Arguments @() -ChildEnvironment @{ TEMP = $temp; TMP = $temp }
    # NSIS treats the last /D= or _?= value as the rest of the raw command line, including spaces.
    $process.Arguments = $Arguments
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $result = Invoke-HiddenProcess -Info $process -TimeoutSeconds 240
    $timer.Stop()
    $log = Join-Path $run $LogName
    [IO.File]::WriteAllText($log, "exitCode=$($result.ExitCode)`nelapsedMs=$($timer.ElapsedMilliseconds)`n$($result.Out)$($result.Err)")
    [ordered]@{ path = $Path; arguments = $Arguments; exitCode = $result.ExitCode; elapsedMs = $timer.ElapsedMilliseconds; log = $log }
}

$setup = Assert-PlainPath $InstallerPath
$zip = Assert-PlainPath $ZipPath
$manifest = Get-PinnedManifest $ManifestPath
if ([IO.Path]::GetFileName($setup) -cnotin @($script:InstallerName, 'Setup.exe') -or
    -not $setup.StartsWith($script:Workspace + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Expected the release installer within the workspace.'
}
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
if ($setupHash -ine $InstallerSha256) { throw "Installer SHA-256 mismatch: $setupHash" }
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$zipCount = Assert-ReleaseZip -Path $zip -Manifest $manifest
$report = [ordered]@{
    status = 'ARTIFACTS_VERIFIED'; startUtc = [DateTimeOffset]::UtcNow.ToString('o'); stage = 'artifacts'
    installer = $setup; installerSha256 = $setupHash; zip = $zip; zipSha256 = $zipHash
    manifest = $manifest.Path; manifestSha256 = $manifest.Hash; zipFiles = $zipCount; payloadBytes = $manifest.Bytes
}
if ($VerifyArtifactsOnly) { [pscustomobject]$report; return }

if (-not ($RunDirectory -and $ExpectedUser -and $ExpectedPrograms)) {
    throw 'A fresh RunDirectory, the expected current user and the expected Programs folder are required.'
}
$run = Get-RunDirectory $RunDirectory
if ([IO.Path]::GetFileName($run) -notmatch '^polish-install-check-144e1786-[a-z0-9-]+$') {
    throw "Unexpected test directory name: $run"
}
Assert-AbsentFileSystemPath $run
$target = Join-Path $run 'app'
if (($target.Length + $manifest.Longest) -gt 246) { throw "Test install path exceeds the installer limit: $target" }
[void](New-Item -ItemType Directory -Path $run)
$temp = [IO.Directory]::CreateDirectory((Join-Path $run 'tmp')).FullName
$receipt = Join-Path $run 'report.json'
$report.target = $target
$report.status = 'FAIL'
try {
    $report.stage = 'account and existing target checks'
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $report.user = $identity.Name
        $report.sid = $identity.User.Value
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        $report.administratorToken = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if ($identity.Name -ine $ExpectedUser -or $report.administratorToken) {
            throw "Expected the unelevated user $ExpectedUser; observed $($identity.Name)."
        }
    } finally { $identity.Dispose() }
    $programs = Assert-PlainPath ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs))
    if ($programs -ine (Assert-PlainPath $ExpectedPrograms) -or -not [IO.Directory]::Exists($programs)) {
        throw "Unexpected current-user Programs directory: $programs"
    }
    $link = Assert-PlainPath (Join-Path $programs $linkName) -AllowMissing
    $report.shortcut = $link
    $report.registration = "HKCU\$regPath"
    Assert-AbsentFileSystemPath $link
    Assert-AbsentFileSystemPath $target
    if ($null -ne (Read-Registration)) { throw "Refusing to replace an existing uninstall entry: HKCU\$regPath" }
    Assert-NoMachineRegistration

    $report.stage = 'silent install'
    $report.install = Invoke-QuietNsis $setup "/S /D=$target" 'install.log'
    if ($report.install.exitCode -ne 0) { throw "Silent install exit code: $($report.install.exitCode)" }
    $player = Join-Path $target 'LLPlayer.exe'
    $uninstaller = Assert-PlainPath (Join-Path $target 'Uninstall.exe')
    $started = @(Get-Process -Name LLPlayer -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $player })
    $report.playerProcessObserved = ($started.Count -ne 0)
    if ($report.playerProcessObserved) { throw 'The silent installer unexpectedly started the installed player.' }

    $report.stage = 'installed files and shell integration'
    $count = 0
    foreach ($file in (Get-PlainTree $target)) {
        $relative = [IO.Path]::GetRelativePath($target, $file.FullName)
        if ($relative -ceq 'Uninstall.exe') { continue }
        if (-not $manifest.Files.ContainsKey($relative) -or $relative.Replace('\', '/') -cne $manifest.Files[$relative].path) {
            throw "Unexpected installed file: $($file.FullName)"
        }
        Assert-PayloadFile $file $manifest.Files[$relative]
        $count++
    }
    $report.installedFilesMatchingZip = $count
    if ($count -ne $zipCount) { throw "Installed file count $count does not match ZIP count $zipCount." }
    $values = Read-Registration
    $uninstallCommand = '"' + $uninstaller + '"'
    $expected = [ordered]@{
        DisplayName = 'LLPlayer Original Review'; DisplayVersion = '0.3.0-polish.1'; DisplayIcon = "$player,0"
        InstallLocation = $target; UninstallString = $uninstallCommand; QuietUninstallString = "$uninstallCommand /S"
        StartMenuLink = $link; PayloadSHA256 = $manifest.Hash
        EstimatedSize = [int][Math]::Ceiling($manifest.Bytes / 1KB); NoModify = 1; NoRepair = 1
    }
    foreach ($name in $expected.Keys) {
        if ($null -eq $values -or $values[$name] -ine $expected[$name]) { throw "Unexpected uninstall registration value: $name" }
    }
    $report.registeredValues = $values
    Assert-NoMachineRegistration
    [void](Assert-PlainPath $link)
    if (-not ('PolishShellLinkReader' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ShellLinkReader.cs') }
    $shortcutTarget, $shortcutDirectory, $shortcutArguments = [PolishShellLinkReader]::Read($link)
    $report.shortcutReader = 'IShellLinkW'
    $report.shortcutTarget = $shortcutTarget
    $report.shortcutWorkingDirectory = $shortcutDirectory
    $report.shortcutArguments = $shortcutArguments
    if ($shortcutTarget -ine $player -or $shortcutDirectory -ine $target -or $shortcutArguments -or
        -not [IO.File]::Exists($shortcutTarget)) {
        throw "Unexpected shortcut target, working directory or arguments: $link"
    }

    $report.stage = 'isolated synthetic user files'
    $fixtures = [ordered]@{
        'PrivateReview\state.json' = '{"cards":[],"testMarker":"synthetic wordbook"}'
        'LLPlayer.Config.json' = '{"testMarker":"synthetic user settings"}'
        'FFmpeg\installer-user-note.txt' = 'User-owned test file in a payload directory.'
    }
    $sentinels = [ordered]@{}
    foreach ($relative in $fixtures.Keys) {
        if ($manifest.Files.ContainsKey($relative)) { throw "Synthetic file collides with the release: $relative" }
        $path = Assert-PlainPath (Join-Path $target $relative) -AllowMissing
        Assert-AbsentFileSystemPath $path
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        $stream = [IO.File]::Open($path, 'CreateNew', 'Write', 'None')
        $bytes = [Text.Encoding]::UTF8.GetBytes($fixtures[$relative])
        try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        $sentinels[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }

    $report.stage = 'silent uninstall'
    # Launch an isolated byte-identical copy so the installed uninstaller can be removed and its actual exit code captured.
    $uninstallProbe = Join-Path $run 'uninstall-probe.exe'
    [IO.File]::Copy($uninstaller, $uninstallProbe, $false)
    $report.uninstallerSha256 = (Get-FileHash -LiteralPath $uninstaller -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $uninstallProbe -Algorithm SHA256).Hash -cne $report.uninstallerSha256) {
        throw 'Uninstaller copy changed.'
    }
    $report.uninstall = Invoke-QuietNsis $uninstallProbe "/S _?=$target" 'uninstall.log'
    if ($report.uninstall.exitCode -ne 0) { throw "Silent uninstall exit code: $($report.uninstall.exitCode)" }
    $report.stage = 'uninstall removal and user-file retention'
    Assert-AbsentFileSystemPath $link
    Assert-AbsentFileSystemPath $uninstaller
    if ($null -ne (Read-Registration)) { throw "Uninstall registration was not removed: HKCU\$regPath" }
    Assert-NoMachineRegistration
    $remaining = @(Get-PlainTree $target)
    if ($remaining.Count -ne $sentinels.Count) { throw "Unexpected post-uninstall file count: $($remaining.Count)" }
    foreach ($file in $remaining) {
        $relative = [IO.Path]::GetRelativePath($target, $file.FullName)
        if (-not $sentinels.Contains($relative) -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -cne $sentinels[$relative]) {
            throw "Unexpected or changed post-uninstall file: $($file.FullName)"
        }
    }
    $report.preservedUserFiles = $sentinels
    $report.registrationRemoved = $true
    $report.shortcutRemoved = $true
    $report.stage = 'complete'
    $report.status = 'PASS'
} catch {
    $report.error = $_.Exception.Message
    throw
} finally {
    $report.endUtc = [DateTimeOffset]::UtcNow.ToString('o')
    [IO.File]::WriteAllText($receipt, ($report | ConvertTo-Json -Depth 5))
    Write-Host "Installer evidence: $receipt"
}
[pscustomobject]$report
