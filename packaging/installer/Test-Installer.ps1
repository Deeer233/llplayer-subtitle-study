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
    [switch]$RegisteredUninstallEntry,
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

function Assert-NoUserRegistration {
    foreach ($view in 'Registry64', 'Registry32') {
        if ($null -ne (Read-Registration -View $view)) {
            throw "Unexpected current-user uninstall entry ($view): HKCU\$regPath"
        }
    }
}

function Assert-NoMachineRegistration {
    foreach ($view in 'Registry64', 'Registry32') {
        if ($null -ne (Read-Registration -Hive LocalMachine -View $view)) {
            throw "Unexpected machine uninstall entry ($view): HKLM\$regPath"
        }
    }
}

function Invoke-QuietNsis([string]$Path, [string]$Arguments, [string]$LogName, [int]$TimeoutSeconds = 240) {
    $log = Join-Path $run $LogName
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $process = New-HiddenProcess -FilePath $Path -Directory $run -Arguments @() -ChildEnvironment @{ TEMP = $temp; TMP = $temp }
        # NSIS treats the last /D= or _?= value as the rest of the raw command line, including spaces.
        $process.Arguments = $Arguments
        $result = Invoke-HiddenProcess -Info $process -TimeoutSeconds $TimeoutSeconds
    } catch {
        [IO.File]::WriteAllText($log, "arguments=$Arguments`nelapsedMs=$($timer.ElapsedMilliseconds)`nerror=$($_.Exception.Message)`n")
        throw
    } finally { $timer.Stop() }
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
$occupiedTarget = Join-Path $run 'used'
foreach ($installTarget in $target, $occupiedTarget) {
    if (($installTarget.Length + $manifest.Longest) -gt 246) { throw "Test install path exceeds the installer limit: $installTarget" }
}
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
    Assert-NoUserRegistration
    Assert-NoMachineRegistration

    $report.stage = 'silent install into an occupied directory'
    $report.existingTarget = $occupiedTarget
    Assert-AbsentFileSystemPath $occupiedTarget
    [void](New-Item -ItemType Directory -Path $occupiedTarget)
    $blockedName = 'preexisting-sentinel.txt'
    if ($manifest.Files.ContainsKey($blockedName)) { throw "Occupied-directory sentinel collides with the release: $blockedName" }
    $blockedSentinel = Assert-PlainPath (Join-Path $occupiedTarget $blockedName) -AllowMissing
    $bytes = [Text.Encoding]::UTF8.GetBytes('Synthetic file in the occupied install target.')
    $stream = [IO.File]::Open($blockedSentinel, 'CreateNew', 'Write', 'None')
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    $blockedHash = (Get-FileHash -LiteralPath $blockedSentinel -Algorithm SHA256).Hash
    $blockedWriteUtc = [IO.Directory]::GetLastWriteTimeUtc($occupiedTarget)
    $report.existingTargetSentinel = [ordered]@{ path = $blockedSentinel; beforeSha256 = $blockedHash }
    $report.installExistingTarget = Invoke-QuietNsis $setup "/S /D=$occupiedTarget" 'install-existing.log'
    if ($report.installExistingTarget.exitCode -ne 2) {
        throw "Expected occupied-target exit 2, got $($report.installExistingTarget.exitCode)."
    }
    $entries = @(Get-ChildItem -LiteralPath (Assert-PlainPath $occupiedTarget) -Force)
    if ($entries.Count -ne 1 -or $entries[0].Name -cne $blockedName -or $entries[0].PSIsContainer) {
        throw "Occupied target contents changed: $occupiedTarget"
    }
    $report.existingTargetSentinel.afterSha256 = (Get-FileHash -LiteralPath (Assert-PlainPath $blockedSentinel) -Algorithm SHA256).Hash
    if ($report.existingTargetSentinel.afterSha256 -cne $blockedHash -or
        [IO.Directory]::GetLastWriteTimeUtc($occupiedTarget) -ne $blockedWriteUtc) {
        throw "Occupied target or its sentinel changed: $occupiedTarget"
    }
    $report.existingTargetUnchanged = $true
    Assert-AbsentFileSystemPath $link
    Assert-NoUserRegistration
    Assert-NoMachineRegistration
    $report.existingTargetIntegrationUnchanged = $true

    $report.stage = 'silent install'
    Assert-AbsentFileSystemPath $target
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
        InstallLocation = $target; UninstallString = $uninstallCommand
        StartMenuLink = $link; PayloadSHA256 = $manifest.Hash
        EstimatedSize = [int][Math]::Ceiling($manifest.Bytes / 1KB); NoModify = 1; NoRepair = 1
    }
    foreach ($name in $expected.Keys) {
        if ($null -eq $values -or $values[$name] -ine $expected[$name]) { throw "Unexpected uninstall registration value: $name" }
    }
    if ($values.Contains('QuietUninstallString')) { throw 'Unexpected QuietUninstallString in the uninstall registration.' }
    $report.registeredValues = $values
    $report.quietUninstallStringAbsent = $true
    Assert-NoMachineRegistration
    [void](Assert-PlainPath $link)
    if (-not ('PolishShellLinkReader' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ShellLinkReader.cs') }
    $shortcutTarget, $shortcutDirectory, $shortcutArguments = [PolishShellLinkReader]::Read($link)
    $report.shortcutReader = 'IShellLinkW'
    $report.shortcutTarget = $shortcutTarget
    $report.shortcutWorkingDirectory = $shortcutDirectory
    $report.shortcutArguments = $shortcutArguments
    $expectedLinkHash = (Get-FileHash -LiteralPath $link -Algorithm SHA256).Hash
    $expectedUninstallerHash = (Get-FileHash -LiteralPath $uninstaller -Algorithm SHA256).Hash
    if ($values['StartMenuLinkSHA256'] -ine $expectedLinkHash -or $values['UninstallerSHA256'] -ine $expectedUninstallerHash) {
        throw 'Registered link or uninstaller fingerprint does not match the installed file.'
    }
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

    # A same-length change must be retained too; a size-only ownership check must fail this test.
    $replacementRelative = 'ECDICT-LICENSE.txt'
    if (-not $manifest.Files.ContainsKey($replacementRelative)) { throw "Missing replacement test payload: $replacementRelative" }
    $replacement = Assert-PlainPath (Join-Path $target $replacementRelative)
    $replacementItem = $manifest.Files[$replacementRelative]
    Assert-PayloadFile (Get-Item -LiteralPath $replacement) $replacementItem
    $stream = [IO.File]::Open($replacement, 'Open', 'ReadWrite', 'None')
    try {
        $firstByte = $stream.ReadByte()
        if ($firstByte -lt 0) { throw "Empty replacement test payload: $replacement" }
        $stream.Position = 0
        $stream.WriteByte([byte]($firstByte -bxor 1))
    } finally { $stream.Dispose() }
    $replacementHash = (Get-FileHash -LiteralPath $replacement -Algorithm SHA256).Hash
    if ($replacementHash -ieq $replacementItem.sha256 -or (Get-Item -LiteralPath $replacement).Length -ne $replacementItem.bytes) {
        throw "Expected a same-length change to the replacement payload: $replacement"
    }
    $sentinels[$replacementRelative] = $replacementHash
    $report.replacedPayload = [ordered]@{ path = $replacementRelative; originalSha256 = $replacementItem.sha256; replacementSha256 = $replacementHash }

    $report.stage = 'silent uninstall with a locked payload file'
    # Launch an isolated byte-identical copy so the installed uninstaller can be removed and its actual exit code captured.
    $uninstallProbe = Join-Path $run 'uninstall-probe.exe'
    [IO.File]::Copy($uninstaller, $uninstallProbe, $false)
    $report.uninstallerSha256 = (Get-FileHash -LiteralPath $uninstaller -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $uninstallProbe -Algorithm SHA256).Hash -cne $report.uninstallerSha256) {
        throw 'Uninstaller copy changed.'
    }
    $uninstallArguments = "/S _?=$target"
    $uninstallTimeoutSeconds = 600 # Allow time to hash the installed payload.
    $lockedRelative = 'Assets\completion.mp3'
    if (-not $manifest.Files.ContainsKey($lockedRelative)) { throw "Missing lock test payload: $lockedRelative" }
    $lockedPath = Assert-PlainPath (Join-Path $target $lockedRelative)
    $lockedItem = $manifest.Files[$lockedRelative]
    Assert-PayloadFile (Get-Item -LiteralPath $lockedPath) $lockedItem
    $shortcutHash = (Get-FileHash -LiteralPath (Assert-PlainPath $link) -Algorithm SHA256).Hash
    $report.lockedPayload = [ordered]@{ path = $lockedRelative; originalSha256 = $lockedItem.sha256 }
    # Permit the uninstaller to read and hash it, but not to delete it until this handle is closed.
    $lock = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $report.uninstallLocked = Invoke-QuietNsis $uninstallProbe $uninstallArguments 'uninstall-locked.log' -TimeoutSeconds $uninstallTimeoutSeconds
        if ($report.uninstallLocked.exitCode -ne 3) { throw "Expected locked uninstall exit 3, got $($report.uninstallLocked.exitCode)." }
        $report.lockedPayload.retainedSha256 = (Get-FileHash -LiteralPath (Assert-PlainPath $lockedPath) -Algorithm SHA256).Hash
        if ($report.lockedPayload.retainedSha256 -ine $lockedItem.sha256) { throw "Locked payload changed: $lockedPath" }
        $lockedValues = Read-Registration
        if ($null -eq $lockedValues -or $lockedValues.Count -ne $values.Count) { throw 'Locked uninstall changed the uninstall registration.' }
        foreach ($name in $values.Keys) {
            if (-not $lockedValues.Contains($name) -or $lockedValues[$name] -cne $values[$name]) {
                throw "Locked uninstall changed registration value: $name"
            }
        }
        if ((Get-FileHash -LiteralPath (Assert-PlainPath $link) -Algorithm SHA256).Hash -cne $shortcutHash) {
            throw "Locked uninstall changed the shortcut: $link"
        }
        if ((Get-FileHash -LiteralPath (Assert-PlainPath $uninstaller) -Algorithm SHA256).Hash -cne $report.uninstallerSha256) {
            throw "Locked uninstall changed the installed uninstaller: $uninstaller"
        }
        $report.lockedUninstallIntegrationUnchanged = $true
    } finally { $lock.Dispose() }

    $report.stage = 'silent uninstall retry after unlocking'
    if ($RegisteredUninstallEntry) {
        # /S uses the registered launcher path (verified above), without NSIS's internal _?= bypass.
        # A successful launcher exit is not the child's exit code. Assert physical cleanup separately.
        $report.uninstallLauncher = Invoke-QuietNsis $uninstaller '/S' 'uninstall-registered-launcher.log' -TimeoutSeconds $uninstallTimeoutSeconds
        if ($report.uninstallLauncher.exitCode -ne 0) { throw "Registered uninstall launcher exit code: $($report.uninstallLauncher.exitCode)" }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ([IO.File]::Exists($uninstaller) -or [IO.File]::Exists($link) -or $null -ne (Read-Registration)) {
            if ($timer.Elapsed.TotalSeconds -ge 90) { throw 'Registered uninstall launcher returned, but its child did not remove shell integration and the uninstall file.' }
            Start-Sleep -Milliseconds 300
        }
        $report.uninstallLauncherWaitMs = $timer.ElapsedMilliseconds
    } else {
        $report.uninstall = Invoke-QuietNsis $uninstallProbe $uninstallArguments 'uninstall-retry.log' -TimeoutSeconds $uninstallTimeoutSeconds
        if ($report.uninstall.exitCode -ne 0) { throw "Silent uninstall retry exit code: $($report.uninstall.exitCode)" }
    }
    $report.stage = 'uninstall removal and user-file retention'
    Assert-AbsentFileSystemPath $link
    Assert-AbsentFileSystemPath $uninstaller
    Assert-NoUserRegistration
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
