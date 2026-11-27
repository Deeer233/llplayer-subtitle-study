#Requires -Version 7.6
param([string]$Samples, [switch]$UnverifiedPilot, [switch]$Probe8B)

$ErrorActionPreference = 'Stop'
if ($env:OLLAMA_NO_CLOUD -ne '1' -and $env:LLPLAYER_STUDY_LOCAL_KEY -cnotmatch '\A[0-9a-f]{64}\z') {
    throw 'Start the candidate launcher or set OLLAMA_NO_CLOUD=1 for both local Ollama and this shell.'
}
if ($Probe8B -and $env:LLPLAYER_STUDY_LOCAL_KEY -cnotmatch '\A[0-9a-f]{64}\z') { throw 'The 8B probe requires its local candidate launcher.' }
$app = Join-Path (Split-Path $PSScriptRoot -Parent) 'upstream/LLPlayer/LLPlayer'
Add-Type -CompilerOptions '/nullable:enable' -Path @(
    (Join-Path $app 'Services/StudyDialogue.cs'),
    (Join-Path $app 'Services/StudyLocalLookup.cs'),
    (Join-Path $PSScriptRoot 'StudyModelProbe.cs')
)
$inputJson = if ($Samples) { Get-Content -Raw -LiteralPath $Samples } else { '' }
$json = [StudyModelProbe]::Run($inputJson, $UnverifiedPilot.IsPresent, $Probe8B.IsPresent, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
$json
$report = ConvertFrom-Json -InputObject $json
if (!$Samples -and $report.translated -ne $report.requested) { exit 2 }
