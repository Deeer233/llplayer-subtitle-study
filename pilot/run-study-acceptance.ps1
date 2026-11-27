#Requires -Version 7.6
[CmdletBinding(DefaultParameterSetName='Timeline')]
param(
    [Parameter(Mandatory, ParameterSetName='Playback')][string]$Playback,
    [Parameter(Mandatory, ParameterSetName='Timeline')][string]$Anchors,
    [Parameter(Mandatory, ParameterSetName='Prepare')][string]$Samples,
    [Parameter(Mandatory, ParameterSetName='Score')][string]$Grades,
    [Parameter(Mandatory, ParameterSetName='Score')][string]$Report
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $root '.pilot'
$timelineDir = Join-Path $cache 'alignment-EDNCKS'
$dialogueDir = Join-Path $cache 'dialogue-audit-RZkQbF'
$reviewer = Join-Path $PSScriptRoot 'review-study-translation.mjs'
$package = Join-Path $cache 'player-20261125-231635-248'

function Resolve-InputFile([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    if ($file -isnot [IO.FileInfo]) { throw "Expected a JSON file: $Path" }
    $file.FullName
}

function Read-AuditJson([string]$Path) {
    ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Path)) -AsHashtable -NoEnumerate
}

function Assert-PackageReport($Data) {
    $packageHash = (Get-FileHash -LiteralPath (Join-Path $package 'LLPlayer.dll')).Hash
    if ($Data.playerAssemblySha256 -ne $packageHash) {
        throw 'Blind report was not produced by the current packaged LLPlayer.dll. Prepare a new blind review.'
    }
}

switch ($PSCmdlet.ParameterSetName) {
    'Playback' {
        & node (Join-Path $PSScriptRoot 'study-playback-check.mjs') check $package (Resolve-InputFile $Playback)
        exit $LASTEXITCODE
    }
    'Timeline' {
        $inputPath = Resolve-InputFile $Anchors
        $inputData = Read-AuditJson $inputPath
        $template = Read-AuditJson (Join-Path $timelineDir 'anchors.json')
        if ($inputData -isnot [System.Collections.IDictionary] -or $inputData.anchors -isnot [array] -or
            $inputData.durationMs -ne $template.durationMs -or
            ($inputData.anchors.cue -join ',') -cne ($template.anchors.cue -join ',')) {
            throw 'Alignment export does not match the 10 cues in this episode audit.'
        }
        if ($inputData.episodeMatches -isnot [bool] -or @($inputData.anchors | Where-Object {
            $_.actualStartMs -eq $null -or $_.sameLine -isnot [bool]
        }).Count -ne 0) {
            throw 'Alignment export is incomplete. Enter the episode choice, start time and dialogue choice for all 10 rows.'
        }
        & node (Join-Path $PSScriptRoot 'feasibility.mjs') timeline (Join-Path $timelineDir 'english.srt') --anchors $inputPath
        exit $LASTEXITCODE
    }
    'Score' {
        $reportPath = Resolve-InputFile $Report
        $gradesPath = Resolve-InputFile $Grades
        Assert-PackageReport (Read-AuditJson $reportPath)
        $result = & node $reviewer score $reportPath $gradesPath
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $result
        $score = ConvertFrom-Json -InputObject ($result -join "`n")
        if (!$score.meetsThresholds) { exit 2 }
        exit 0
    }
    'Prepare' {
        $inputPath = Resolve-InputFile $Samples
        $inputRows = Read-AuditJson $inputPath
        $template = Read-AuditJson (Join-Path $dialogueDir 'samples.json')
        if ($inputRows -isnot [array] -or $inputRows.Count -ne 100 -or $template.Count -ne 100) {
            throw 'Expected all 100 rows from the dialogue audit page.'
        }
        $unverified = 0
        $targets = 0
        for ($i = 0; $i -lt $inputRows.Count; $i++) {
            $row = $inputRows[$i]
            $original = $template[$i]
            if ($row -isnot [System.Collections.IDictionary] -or $row.id -cne $original.id -or
                $row.line -isnot [string] -or [string]::IsNullOrWhiteSpace($row.line) -or $row.verified -isnot [bool]) {
                throw "Invalid dialogue row $($i + 1): expected $($original.id), nonempty English and an explicit verification flag."
            }
            foreach ($side in 'before', 'after') {
                if ($row[$side] -isnot [array] -or $row[$side].Count -gt 2 -or
                    @($row[$side] | Where-Object { $_ -isnot [string] }).Count -ne 0) {
                    throw "Invalid $side context for $($row.id)."
                }
            }
            if ($original.target) {
                if ($row.target -isnot [string] -or [string]::IsNullOrWhiteSpace($row.target) -or
                    !$row.line.Contains($row.target, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Target missing from the English line: $($row.id)."
                }
                $targets++
            } elseif ($null -ne $row.target) {
                throw "Unexpected target for $($row.id)."
            }
            if (!$row.verified) { $unverified++ }
        }
        if ($targets -ne 50) { throw 'Expected exactly 50 target rows.' }
        if ($unverified -ne 0) { throw "Dialogue export is incomplete: $unverified of 100 rows are not confirmed." }
        $null = Resolve-InputFile (Join-Path $package 'LLPlayer.dll')

        # Freeze the validated input before inference; reports stay by the existing audio clips.
        $run = [guid]::NewGuid().ToString('N')
        $snapshot = Join-Path $dialogueDir "verified-input-$run.json"
        $snapshotBytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $inputRows -Depth 8))
        $file = [IO.File]::Open($snapshot, [IO.FileMode]::CreateNew)
        try { $file.Write($snapshotBytes, 0, $snapshotBytes.Length) } finally { $file.Dispose() }
        $reportPath = Join-Path $dialogueDir "candidate-verified-$run.json"
        & (Join-Path $PSScriptRoot 'start-study-candidate.ps1') -DynamicPort `
            -Server (Join-Path $cache 'llama-b11193/mirror-verified-bin/llama-server.exe') `
            -Model (Join-Path $cache 'model-qwen3-pilot/Qwen3-4B-Q4_K_M.gguf') `
            -Samples $snapshot -SamplesPackage $package -Report $reportPath 3>$null | Out-Null
        $data = Read-AuditJson $reportPath
        Assert-PackageReport $data
        if ($data.inputSha256 -ne (Get-FileHash -LiteralPath $snapshot).Hash) {
            throw 'Blind report input hash does not match the validated snapshot.'
        }
        $previousEncoding = [Console]::OutputEncoding
        try {
            # Node writes UTF-8 even when this PowerShell session uses a legacy code page.
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $prepared = & node $reviewer prepare $reportPath
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        } finally { [Console]::OutputEncoding = $previousEncoding }
        $blind = ConvertFrom-Json -InputObject ($prepared -join "`n")
        if (!$blind.scoreAllowed) { throw 'Blind review unexpectedly disabled scoring.' }
        [ordered]@{ html=$blind.html; report=$reportPath; verifiedInput=$snapshot } | ConvertTo-Json -EscapeHandling EscapeNonAscii
        exit 0
    }
}
