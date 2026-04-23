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

function Get-StructuredLogPairs {
    param([string]$Line)

    $pairs = New-Object System.Collections.Generic.List[object]
    if ([string]::IsNullOrWhiteSpace($Line)) {
        return $pairs
    }

    foreach ($segment in ($Line -split ';')) {
        $trimmedSegment = $segment.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmedSegment)) {
            continue
        }

        $separatorIndex = $trimmedSegment.IndexOf('=')
        if ($separatorIndex -lt 0) {
            continue
        }

        $pairs.Add([pscustomobject]@{
                Key = $trimmedSegment.Substring(0, $separatorIndex).Trim()
                Value = $trimmedSegment.Substring($separatorIndex + 1).Trim()
            })
    }

    return $pairs
}

function Get-StructuredLogFieldValue {
    param(
        [System.Collections.IEnumerable]$Pairs,
        [string]$Key
    )

    if ($null -eq $Pairs -or [string]::IsNullOrWhiteSpace($Key)) {
        return ''
    }

    foreach ($pair in $Pairs) {
        if ($null -ne $pair -and [string]::Equals([string]$pair.Key, $Key, [System.StringComparison]::OrdinalIgnoreCase)) {
            return [string]$pair.Value
        }
    }

    return ''
}

function Get-StructuredLogFieldValueAfter {
    param(
        [System.Collections.IEnumerable]$Pairs,
        [string]$AfterKey,
        [int]$Offset
    )

    if ($null -eq $Pairs -or [string]::IsNullOrWhiteSpace($AfterKey) -or $Offset -lt 1) {
        return ''
    }

    $pairArray = @($Pairs)
    for ($index = 0; $index -lt $pairArray.Count; $index++) {
        $pair = $pairArray[$index]
        if ($null -eq $pair) {
            continue
        }

        if (-not [string]::Equals([string]$pair.Key, $AfterKey, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $targetIndex = $index + $Offset
        if ($targetIndex -ge 0 -and $targetIndex -lt $pairArray.Count) {
            return [string]$pairArray[$targetIndex].Value
        }

        return ''
    }

    return ''
}

function Get-LastReceivePathSummaryLine {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) {
        return ''
    }

    $inSummaryLinesSection = $false
    $lastSummaryLine = ''
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if (-not $inSummaryLinesSection) {
            if ([string]::Equals($line.Trim(), 'helper_receive_path_summary_lines:', [System.StringComparison]::OrdinalIgnoreCase)) {
                $inSummaryLinesSection = $true
            }

            continue
        }

        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line.IndexOf('screenshare_helper_receive_path_summary', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $lastSummaryLine = $line.Trim()
        }
    }

    return $lastSummaryLine
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

function Merge-ReceivePathValuesFromSummaryLine {
    param(
        [hashtable]$Values,
        [string]$SummaryPath
    )

    if ($null -eq $Values -or [string]::IsNullOrWhiteSpace($SummaryPath)) {
        return $Values
    }

    $summaryLine = Get-LastReceivePathSummaryLine -Path $SummaryPath
    if ([string]::IsNullOrWhiteSpace($summaryLine)) {
        return $Values
    }

    $pairs = Get-StructuredLogPairs -Line $summaryLine
    if ($pairs.Count -eq 0) {
        return $Values
    }

    $fallbackAfterKey = 'helper_recovery_mechanism'
    $fallbackDefinitions = @(
        @{ Key = 'capture_to_envelope_send_avg_ms'; Offset = 1 },
        @{ Key = 'capture_to_envelope_send_median_ms'; Offset = 2 },
        @{ Key = 'capture_to_envelope_send_p95_ms'; Offset = 3 },
        @{ Key = 'capture_to_envelope_send_max_ms'; Offset = 4 },
        @{ Key = 'envelope_send_to_bridge_ingress_avg_ms'; Offset = 5 },
        @{ Key = 'envelope_send_to_bridge_ingress_median_ms'; Offset = 6 },
        @{ Key = 'envelope_send_to_bridge_ingress_p95_ms'; Offset = 7 },
        @{ Key = 'envelope_send_to_bridge_ingress_max_ms'; Offset = 8 },
        @{ Key = 'bridge_ingress_to_envelope_parsed_avg_ms'; Offset = 9 },
        @{ Key = 'bridge_ingress_to_envelope_parsed_median_ms'; Offset = 10 },
        @{ Key = 'bridge_ingress_to_envelope_parsed_p95_ms'; Offset = 11 },
        @{ Key = 'bridge_ingress_to_envelope_parsed_max_ms'; Offset = 12 },
        @{ Key = 'envelope_parsed_to_secure_decrypt_avg_ms'; Offset = 13 },
        @{ Key = 'envelope_parsed_to_secure_decrypt_median_ms'; Offset = 14 },
        @{ Key = 'envelope_parsed_to_secure_decrypt_p95_ms'; Offset = 15 },
        @{ Key = 'envelope_parsed_to_secure_decrypt_max_ms'; Offset = 16 },
        @{ Key = 'secure_decrypt_to_fragment_deserialize_avg_ms'; Offset = 17 },
        @{ Key = 'secure_decrypt_to_fragment_deserialize_median_ms'; Offset = 18 },
        @{ Key = 'secure_decrypt_to_fragment_deserialize_p95_ms'; Offset = 19 },
        @{ Key = 'secure_decrypt_to_fragment_deserialize_max_ms'; Offset = 20 },
        @{ Key = 'fragment_deserialize_to_first_fragment_observed_avg_ms'; Offset = 21 },
        @{ Key = 'fragment_deserialize_to_first_fragment_observed_median_ms'; Offset = 22 },
        @{ Key = 'fragment_deserialize_to_first_fragment_observed_p95_ms'; Offset = 23 },
        @{ Key = 'fragment_deserialize_to_first_fragment_observed_max_ms'; Offset = 24 }
    )

    foreach ($definition in $fallbackDefinitions) {
        $key = [string]$definition.Key
        $currentValue = Get-SummaryIntValue -Values $Values -Key $key
        if ($currentValue -ge 0) {
            continue
        }

        $parsedValue = Get-StructuredLogFieldValue -Pairs $pairs -Key $key
        if ([string]::IsNullOrWhiteSpace($parsedValue)) {
            $parsedValue = Get-StructuredLogFieldValueAfter -Pairs $pairs -AfterKey $fallbackAfterKey -Offset ([int]$definition.Offset)
        }

        $intValue = 0
        if ([int]::TryParse($parsedValue, [ref]$intValue)) {
            $Values[$key] = [string]$intValue
        }
    }

    foreach ($stringKey in @('helper_session_phase', 'helper_recovery_mechanism', 'dominant_receive_path_stage')) {
        $currentStringValue = Get-SummaryStringValue -Values $Values -Key $stringKey -DefaultValue ''
        if (-not [string]::IsNullOrWhiteSpace($currentStringValue) -and -not [string]::Equals($currentStringValue, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $parsedStringValue = Get-StructuredLogFieldValue -Pairs $pairs -Key $stringKey
        if (-not [string]::IsNullOrWhiteSpace($parsedStringValue)) {
            $Values[$stringKey] = $parsedStringValue
        }
    }

    return $Values
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
        'sender_pre_send_latency' { return 'sender media transport pacing/payloadization' }
        'bridge_receive_latency' { return 'bridge/media receive backlog work' }
        'envelope_parse_latency' { return 'envelope decode/allocation/logging path' }
        'secure_decrypt_latency' { return 'secure envelope decrypt/replay validation path' }
        'fragment_dispatch_latency' { return 'fragment envelope deserialize + immediate handoff into reassembler' }
        default { return 'mixed_or_inconclusive' }
    }
}

$stageOrder = @(
    @{ Name = 'capture_to_envelope_send'; Classification = 'sender_pre_send_latency' },
    @{ Name = 'envelope_send_to_bridge_ingress'; Classification = 'bridge_receive_latency' },
    @{ Name = 'bridge_ingress_to_envelope_parsed'; Classification = 'envelope_parse_latency' },
    @{ Name = 'envelope_parsed_to_secure_decrypt'; Classification = 'secure_decrypt_latency' },
    @{ Name = 'secure_decrypt_to_fragment_deserialize'; Classification = 'fragment_dispatch_latency' },
    @{ Name = 'fragment_deserialize_to_first_fragment_observed'; Classification = 'fragment_dispatch_latency' }
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

$candidateSummaryPath = Join-Path $CandidateArtifactDir 'helper-receive-path-summary.txt'
$candidateValues = Read-KeyValueSummaryFile -Path $candidateSummaryPath
$candidateValues = Merge-ReceivePathValuesFromSummaryLine -Values $candidateValues -SummaryPath $candidateSummaryPath

$referenceSnapshots = @()
$referenceSummaryAvailabilityCount = 0
foreach ($referenceArtifactDir in $normalizedReferenceArtifactDirs) {
    if ([string]::IsNullOrWhiteSpace($referenceArtifactDir) -or -not (Test-Path $referenceArtifactDir)) {
        continue
    }

    $referenceSummaryPath = Join-Path $referenceArtifactDir 'helper-receive-path-summary.txt'
    if (-not (Test-Path $referenceSummaryPath)) {
        continue
    }

    $referenceValues = Read-KeyValueSummaryFile -Path $referenceSummaryPath
    $referenceValues = Merge-ReceivePathValuesFromSummaryLine -Values $referenceValues -SummaryPath $referenceSummaryPath
    $referenceSnapshots += @{
        ArtifactDir = $referenceArtifactDir
        Values = $referenceValues
    }
    $referenceSummaryAvailabilityCount++
}
$referenceComparisonAvailable = $referenceSnapshots.Count -gt 0

$stageResults = [ordered]@{}
foreach ($stage in $stageOrder) {
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

$totalDelta = 0
foreach ($stageResult in $stageResults.Values) {
    $totalDelta += [int]$stageResult['DeltaVsReference']
}

$classification = 'mixed_or_inconclusive'
if ($totalDelta -gt 0) {
    $bestStage = $null
    $bestContribution = -1.0
    foreach ($stage in $stageOrder) {
        $stageName = [string]$stage.Name
        $delta = [double]$stageResults[$stageName].DeltaVsReference
        $contribution = $delta / [double]$totalDelta
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
$reportLines.Add("reference_receive_path_summary_availability=$referenceSummaryAvailabilityCount/$($normalizedReferenceArtifactDirs.Count)")
$reportLines.Add("reference_receive_path_comparison_mode=$(if ($referenceComparisonAvailable) { 'reference_median_vs_max' } else { 'candidate_stage_composition_fallback' })")

foreach ($stageName in @(
    'capture_to_envelope_send',
    'envelope_send_to_bridge_ingress',
    'bridge_ingress_to_envelope_parsed',
    'envelope_parsed_to_secure_decrypt',
    'secure_decrypt_to_fragment_deserialize',
    'fragment_deserialize_to_first_fragment_observed')) {
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

$reportPath = Join-Path $CandidateArtifactDir 'helper-receive-path-analysis.txt'
Set-Content -Path $reportPath -Value $reportLines
$reportLines | ForEach-Object { Write-Output $_ }
