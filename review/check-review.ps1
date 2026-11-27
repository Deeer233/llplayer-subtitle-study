#Requires -Version 7.6
param([string]$Root, [string]$Dictionary)

$ErrorActionPreference = 'Stop'
$pilot = Join-Path (Split-Path $PSScriptRoot -Parent) '.pilot'
if (!$Root) { $Root = Join-Path $pilot ('review-check-' + [guid]::NewGuid().ToString('N')) }
if (!$Dictionary) { $Dictionary = Join-Path $pilot 'StudyDictionary.zip' }
$Dictionary = (Resolve-Path -LiteralPath $Dictionary).Path
$source = Join-Path $PSScriptRoot 'OriginalReview'
Add-Type -CompilerOptions '/nullable:enable' -Path @(
    (Join-Path $source 'ReviewStore.cs'), (Join-Path $source 'WordImport.cs'), (Join-Path $source 'ReviewDictionary.cs'),
    (Join-Path $PSScriptRoot 'ReviewChecks.cs')
)
[ReviewChecks]::Run($Root, $Dictionary)
