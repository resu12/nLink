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

function Get-LastWsReceiveSummaryLine {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) {
        return ''
    }

    $inSummaryLinesSection = $false
    $lastSummaryLine = ''
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if (-not $inSummaryLinesSection) {
            if ([string]::Equals($line.Trim(), 'helper_ws_receive_summary_lines:', [System.StringComparison]::OrdinalIgnoreCase)) {
                $inSummaryLinesSection = $true
            }

            continue
        }

        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line.IndexOf('screenshare_helper_ws_receive_summary', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
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

function Merge-WsReceiveValuesFromSummaryLine {
    param(
        [hashtable]$Values,
        [string]$SummaryPath
    )

    if ($null -eq $Values -or [string]::IsNullOrWhiteSpace($SummaryPath)) {
        return $Values
    }

    $summaryLine = Get-LastWsReceiveSummaryLine -Path $SummaryPath
    if ([string]::IsNullOrWhiteSpace($summaryLine)) {
        return $Values
    }

    $pairs = Get-StructuredLogPairs -Line $summaryLine
    if ($pairs.Count -eq 0) {
        return $Values
    }

    $fallbackAfterKey = 'helper_recovery_mechanism'
    $fallbackDefinitions = @(
        @{ Key = 'envelope_send_to_ws_receiver_write_entered_avg_ms'; Offset = 1 },
        @{ Key = 'envelope_send_to_ws_receiver_write_entered_median_ms'; Offset = 2 },
        @{ Key = 'envelope_send_to_ws_receiver_write_entered_p95_ms'; Offset = 3 },
        @{ Key = 'envelope_send_to_ws_receiver_write_entered_max_ms'; Offset = 4 },
        @{ Key = 'ws_receiver_write_entered_to_ws_message_emitted_avg_ms'; Offset = 5 },
        @{ Key = 'ws_receiver_write_entered_to_ws_message_emitted_median_ms'; Offset = 6 },
        @{ Key = 'ws_receiver_write_entered_to_ws_message_emitted_p95_ms'; Offset = 7 },
        @{ Key = 'ws_receiver_write_entered_to_ws_message_emitted_max_ms'; Offset = 8 },
        @{ Key = 'ws_message_emitted_to_sdk_handle_msg_entered_avg_ms'; Offset = 9 },
        @{ Key = 'ws_message_emitted_to_sdk_handle_msg_entered_median_ms'; Offset = 10 },
        @{ Key = 'ws_message_emitted_to_sdk_handle_msg_entered_p95_ms'; Offset = 11 },
        @{ Key = 'ws_message_emitted_to_sdk_handle_msg_entered_max_ms'; Offset = 12 }
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

    foreach ($stringKey in @('helper_session_phase', 'helper_recovery_mechanism', 'dominant_ws_receive_stage')) {
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
        'pre_ws_receiver_latency' { return 'Node socket / external NKN receive backlog work' }
        'ws_receiver_parse_latency' { return 'ws Receiver parse path' }
        'js_event_listener_latency' { return 'WebSocket message event listener handoff' }
        default { return 'mixed_or_inconclusive' }
    }
}

$stageOrder = @(
    @{ Name = 'envelope_send_to_ws_receiver_write_entered'; Classification = 'pre_ws_receiver_latency' },
    @{ Name = 'ws_receiver_write_entered_to_ws_message_emitted'; Classification = 'ws_receiver_parse_latency' },
    @{ Name = 'ws_message_emitted_to_sdk_handle_msg_entered'; Classification = 'js_event_listener_latency' }
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

$candidateSummaryPath = Join-Path $CandidateArtifactDir 'helper-ws-receive-summary.txt'
$candidateValues = Read-KeyValueSummaryFile -Path $candidateSummaryPath
$candidateValues = Merge-WsReceiveValuesFromSummaryLine -Values $candidateValues -SummaryPath $candidateSummaryPath

$referenceSnapshots = @()
$referenceSummaryAvailabilityCount = 0
foreach ($referenceArtifactDir in $normalizedReferenceArtifactDirs) {
    if ([string]::IsNullOrWhiteSpace($referenceArtifactDir) -or -not (Test-Path $referenceArtifactDir)) {
        continue
    }

    $referenceSummaryPath = Join-Path $referenceArtifactDir 'helper-ws-receive-summary.txt'
    if (-not (Test-Path $referenceSummaryPath)) {
        continue
    }

    $referenceValues = Read-KeyValueSummaryFile -Path $referenceSummaryPath
    $referenceValues = Merge-WsReceiveValuesFromSummaryLine -Values $referenceValues -SummaryPath $referenceSummaryPath
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
$referenceComparisonMode = if ($referenceComparisonAvailable) { 'reference_max_delta' } else { 'candidate_stage_composition_fallback' }

$reportLines = [System.Collections.Generic.List[string]]::new()
$reportLines.Add("candidate_artifact_dir=$CandidateArtifactDir")
$reportLines.Add("reference_artifact_dirs=$([string]::Join(', ', $normalizedReferenceArtifactDirs))")
$reportLines.Add("reference_ws_receive_summary_availability=$referenceSummaryAvailabilityCount/$($normalizedReferenceArtifactDirs.Count)")
$reportLines.Add("reference_ws_receive_comparison_mode=$referenceComparisonMode")
$reportLines.Add("classification=$classification")
$reportLines.Add("smallest_next_fix_area=$smallestNextFixArea")
$reportLines.Add("candidate_helper_session_phase=$candidateHelperSessionPhase")
$reportLines.Add("candidate_helper_recovery_mechanism=$candidateHelperRecoveryMechanism")

foreach ($stage in $stageOrder) {
    $stageName = [string]$stage.Name
    $stageResult = $stageResults[$stageName]
    $candidateStage = $stageResult.Candidate
    $referenceStats = $stageResult.Reference
    $reportLines.Add("candidate_${stageName}_median_ms=$($candidateStage.Median)")
    $reportLines.Add("candidate_${stageName}_p95_ms=$($candidateStage.P95)")
    $reportLines.Add("candidate_${stageName}_max_ms=$($candidateStage.Max)")
    $reportLines.Add("reference_${stageName}_min_ms=$($referenceStats.Min)")
    $reportLines.Add("reference_${stageName}_median_ms=$($referenceStats.Median)")
    $reportLines.Add("reference_${stageName}_max_ms=$($referenceStats.Max)")
    $reportLines.Add("${stageName}_delta_vs_reference=$($stageResult.DeltaVsReference)")
}

$reportLines.Add('')
$reportLines.Add('reference_artifact_dirs_used:')
foreach ($referenceArtifactDir in $referenceArtifactDirList) {
    $reportLines.Add($referenceArtifactDir)
}

$reportPath = Join-Path $CandidateArtifactDir 'helper-ws-receive-analysis.txt'
Set-Content -Path $reportPath -Value $reportLines
$reportLines
