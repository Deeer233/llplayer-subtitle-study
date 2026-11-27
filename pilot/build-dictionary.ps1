#Requires -Version 7.6
param(
    [Parameter(Mandatory)][string]$Csv,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Csv).Path
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$root = Split-Path $PSScriptRoot -Parent
$lookup = Join-Path $root 'upstream/LLPlayer/LLPlayer/Services/StudyDictionary.cs'
Add-Type -CompilerOptions '/nullable:enable' -Path @($lookup, (Join-Path $PSScriptRoot 'BuildStudyDictionary.cs'))
[BuildStudyDictionary]::Run($source, $destination)
