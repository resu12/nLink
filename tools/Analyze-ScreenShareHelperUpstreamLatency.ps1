param(
    [Parameter(Mandatory = $true)][string]$CandidateArtifactDir,
    [string[]]$ReferenceArtifactDirs = @(
        'C:\Users\Juraj\Desktop\Remote help\artifacts\soak\20260422-211838',
        'C:\Users\Juraj\Desktop\Remote help\artifacts\soak\20260422-211926',
        'C:\Users\Juraj\Desktop\Remote help\artifacts\soak\20260422-212015'
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-KeyValueSummaryFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    if (-not (Test-Path $Path)) {
        throw "Summary file not found: $Path"
    }

    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $separatorIndex = $line.IndexOf('=')
        if ($separatorIndex -le 0) {
            continue
        }

        $key = $line.Substring(0, $separatorIndex).Trim()
        if ([string]::IsNullOrWhiteSpace($key)) {
            continue
        }

        $values[$key] = $line.Substring($separatorIndex + 1).Trim()
    }

    return $values
}

function Get-SummaryIntValue {
    param(
        [hashtable]$Values,
        [string]$Key
    )

    if ($null -eq $Values -or [string]::IsNullOrWhiteSpace($Key) -or -not $Values.ContainsKey($Key)) {
        return -1
    }

    $rawValue = [string]$Values[$Key]
    if ([string]::IsNullOrWhiteSpace($rawValue) -or
        [string]::Equals($rawValue, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
        return -1
    }

    $parsed = 0
    if ([int]::TryParse($rawValue, [ref]$parsed)) {
        return $parsed
    }

    return -1
}

function Get-SummaryStringValue {
    param(
        [hashtable]$Values,
        [string]$Key,
        [string]$DefaultValue = ''
    )

    if ($null -eq $Values -or [string]::IsNullOrWhiteSpace($Key) -or -not $Values.ContainsKey($Key)) {
        return $DefaultValue
    }

    $rawValue = [string]$Values[$Key]
    if ([string]::IsNullOrWhiteSpace($rawValue)) {
        return $DefaultValue
    }

    return $rawValue.Trim()
}

function Get-IntStats {
    param([int[]]$Values)

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return @{
            Min = -1
            Median = -1
            Max = -1
        }
    }

    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    $median = if (($sorted.Count % 2) -eq 1) {
        $sorted[$middle]
    }
    else {
        [int][Math]::Floor(($sorted[$middle - 1] + $sorted[$middle]) / 2.0)
    }

    return @{
        Min = [int]$sorted[0]
        Median = [int]$median
        Max = [int]$sorted[$sorted.Count - 1]
    }
}

function Get-StageSnapshot {
    param(
        [hashtable]$Values,
        [string]$StagePrefix
    )

    return @{
        Avg = Get-SummaryIntValue -Values $Values -Key "${StagePrefix}_avg_ms"
        Median = Get-SummaryIntValue -Values $Values -Key "${StagePrefix}_median_ms"
        P95 = Get-SummaryIntValue -Values $Values -Key "${StagePrefix}_p95_ms"
        Max = Get-SummaryIntValue -Values $Values -Key "${StagePrefix}_max_ms"
    }
}

function Get-FixAreaForClassification {
    param([string]$Classification)

    switch -Exact ($Classification) {
        'pre_helper_arrival_latency' { return 'transport/reassembler ready path' }
        'viewer_admission_latency' { return 'helper viewer admission path' }
        'decode_start_latency' { return 'helper decode-start path' }
        default { return 'mixed_or_inconclusive' }
    }
}

$stageOrder = @(
    @{ Name = 'capture_to_frame_ready'; Classification = 'pre_helper_arrival_latency' },
    @{ Name = 'frame_ready_to_viewer_accept'; Classification = 'viewer_admission_latency' },
    @{ Name = 'viewer_accept_to_decode_enqueue'; Classification = 'viewer_admission_latency' },
    @{ Name = 'decode_enqueue_to_decode_start'; Classification = 'decode_start_latency' }
)

if (-not (Test-Path $CandidateArtifactDir)) {
    throw "Candidate artifact dir not found: $CandidateArtifactDir"
}

$normalizedReferenceArtifactDirs = @()
foreach ($referenceArtifactDir in $ReferenceArtifactDirs) {
    if ([string]::IsNullOrWhiteSpace($referenceArtifactDir)) {
        continue
    }

    foreach ($splitReferenceArtifactDir in ($referenceArtifactDir -split ',')) {
        if (-not [string]::IsNullOrWhiteSpace($splitReferenceArtifactDir)) {
            $normalizedReferenceArtifactDirs += $splitReferenceArtifactDir.Trim()
        }
    }
}

$candidateSummaryPath = Join-Path $CandidateArtifactDir 'helper-upstream-latency-summary.txt'
$candidateValues = Read-KeyValueSummaryFile -Path $candidateSummaryPath

$referenceSnapshots = @()
$referenceSummaryAvailabilityCount = 0
foreach ($referenceArtifactDir in $normalizedReferenceArtifactDirs) {
    if ([string]::IsNullOrWhiteSpace($referenceArtifactDir) -or -not (Test-Path $referenceArtifactDir)) {
        continue
    }

    $referenceSummaryPath = Join-Path $referenceArtifactDir 'helper-upstream-latency-summary.txt'
    if (-not (Test-Path $referenceSummaryPath)) {
        continue
    }

    $referenceValues = Read-KeyValueSummaryFile -Path $referenceSummaryPath
    $referenceSnapshots += @{
        ArtifactDir = $referenceArtifactDir
        Values = $referenceValues
    }
    $referenceSummaryAvailabilityCount++
}
$referenceComparisonAvailable = $referenceSnapshots.Count -gt 0

$stageResults = [ordered]@{}
foreach ($stage in $stageOrder + @(@{ Name = 'capture_to_decode_start'; Classification = '' })) {
    $stageName = [string]$stage.Name
    $candidateStage = Get-StageSnapshot -Values $candidateValues -StagePrefix $stageName
    $referenceMedians = @()
    foreach ($referenceSnapshot in $referenceSnapshots) {
        $referenceMedians += Get-SummaryIntValue -Values $referenceSnapshot.Values -Key "${stageName}_median_ms"
    }

    $referenceStats = Get-IntStats -Values @($referenceMedians | Where-Object { $_ -ge 0 })
    $deltaVsReference = if ($candidateStage.Median -ge 0 -and $referenceStats.Max -ge 0) {
        [Math]::Max(0, $candidateStage.Median - $referenceStats.Max)
    }
    elseif ($candidateStage.Median -ge 0 -and -not $referenceComparisonAvailable) {
        $candidateStage.Median
    }
    else {
        0
    }

    $stageResults[$stageName] = @{
        Candidate = $candidateStage
        Reference = $referenceStats
        DeltaVsReference = $deltaVsReference
        Classification = [string]$stage.Classification
    }
}

$captureToDecodeStartDelta = [int]$stageResults['capture_to_decode_start'].DeltaVsReference
$classification = 'mixed_or_inconclusive'
if ($captureToDecodeStartDelta -gt 0) {
    $bestStage = $null
    $bestContribution = -1.0
    foreach ($stage in $stageOrder) {
        $stageName = [string]$stage.Name
        $delta = [double]$stageResults[$stageName].DeltaVsReference
        $contribution = if ($captureToDecodeStartDelta -gt 0) {
            $delta / [double]$captureToDecodeStartDelta
        }
        else {
            0.0
        }

        if ($contribution -gt $bestContribution) {
            $bestContribution = $contribution
            $bestStage = $stageName
        }
    }

    if ($null -ne $bestStage -and $bestContribution -ge 0.6) {
        $classification = [string]$stageResults[$bestStage].Classification
    }
}

$smallestNextFixArea = Get-FixAreaForClassification -Classification $classification
$candidateHelperSessionPhase = Get-SummaryStringValue -Values $candidateValues -Key 'helper_session_phase' -DefaultValue '(none)'
$candidateHelperRecoveryMechanism = Get-SummaryStringValue -Values $candidateValues -Key 'helper_recovery_mechanism' -DefaultValue '(none)'
$referenceArtifactDirList = @($referenceSnapshots | ForEach-Object { $_.ArtifactDir })

$reportLines = New-Object System.Collections.Generic.List[string]
$reportLines.Add("classification=$classification")
$reportLines.Add("candidate_artifact_dir=$CandidateArtifactDir")
$reportLines.Add("reference_artifact_dirs=$([string]::Join(',', $referenceArtifactDirList))")
$reportLines.Add("reference_upstream_summary_availability=$referenceSummaryAvailabilityCount/$($normalizedReferenceArtifactDirs.Count)")
$reportLines.Add("reference_upstream_comparison_mode=$(if ($referenceComparisonAvailable) { 'reference_median_vs_max' } else { 'candidate_stage_composition_fallback' })")

foreach ($stageName in @('capture_to_frame_ready', 'frame_ready_to_viewer_accept', 'viewer_accept_to_decode_enqueue', 'decode_enqueue_to_decode_start', 'capture_to_decode_start')) {
    $stageResult = $stageResults[$stageName]
    $reportLines.Add("candidate_${stageName}_median_ms=$($stageResult.Candidate.Median)")
    $reportLines.Add("reference_${stageName}_median_ms_min=$($stageResult.Reference.Min)")
    $reportLines.Add("reference_${stageName}_median_ms_median=$($stageResult.Reference.Median)")
    $reportLines.Add("reference_${stageName}_median_ms_max=$($stageResult.Reference.Max)")
    $reportLines.Add("candidate_${stageName}_avg_ms=$($stageResult.Candidate.Avg)")
    $reportLines.Add("candidate_${stageName}_p95_ms=$($stageResult.Candidate.P95)")
    $reportLines.Add("candidate_${stageName}_max_ms=$($stageResult.Candidate.Max)")
    $reportLines.Add("delta_${stageName}_vs_reference=$($stageResult.DeltaVsReference)")
}

$reportLines.Add("candidate_helper_session_phase=$candidateHelperSessionPhase")
$reportLines.Add("candidate_helper_recovery_mechanism=$candidateHelperRecoveryMechanism")
$reportLines.Add("smallest_next_fix_area=$smallestNextFixArea")

$reportPath = Join-Path $CandidateArtifactDir 'helper-upstream-latency-analysis.txt'
Set-Content -Path $reportPath -Value $reportLines
$reportLines | ForEach-Object { Write-Output $_ }
