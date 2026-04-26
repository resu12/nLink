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

function Get-LastSocketReceiveSummaryLine {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) {
        return ''
    }

    $inSummaryLinesSection = $false
    $lastSummaryLine = ''
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if (-not $inSummaryLinesSection) {
            if ([string]::Equals($line.Trim(), 'helper_socket_receive_summary_lines:', [System.StringComparison]::OrdinalIgnoreCase)) {
                $inSummaryLinesSection = $true
            }

            continue
        }

        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line.IndexOf('screenshare_helper_socket_receive_summary', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
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

function Merge-SocketReceiveValuesFromSummaryLine {
    param(
        [hashtable]$Values,
        [string]$SummaryPath
    )

    if ($null -eq $Values -or [string]::IsNullOrWhiteSpace($SummaryPath)) {
        return $Values
    }

    $summaryLine = Get-LastSocketReceiveSummaryLine -Path $SummaryPath
    if ([string]::IsNullOrWhiteSpace($summaryLine)) {
        return $Values
    }

    $pairs = Get-StructuredLogPairs -Line $summaryLine
    if ($pairs.Count -eq 0) {
        return $Values
    }

    $fallbackAfterKey = 'helper_recovery_mechanism'
    $fallbackDefinitions = @(
        @{ Key = 'envelope_send_to_socket_data_event_emitted_avg_ms'; Offset = 1 },
        @{ Key = 'envelope_send_to_socket_data_event_emitted_median_ms'; Offset = 2 },
        @{ Key = 'envelope_send_to_socket_data_event_emitted_p95_ms'; Offset = 3 },
        @{ Key = 'envelope_send_to_socket_data_event_emitted_max_ms'; Offset = 4 },
        @{ Key = 'socket_data_event_emitted_to_ws_receiver_write_entered_avg_ms'; Offset = 5 },
        @{ Key = 'socket_data_event_emitted_to_ws_receiver_write_entered_median_ms'; Offset = 6 },
        @{ Key = 'socket_data_event_emitted_to_ws_receiver_write_entered_p95_ms'; Offset = 7 },
        @{ Key = 'socket_data_event_emitted_to_ws_receiver_write_entered_max_ms'; Offset = 8 }
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

    foreach ($stringKey in @('helper_session_phase', 'helper_recovery_mechanism', 'dominant_socket_receive_stage')) {
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
        'external_receive_latency' { return 'Node socket / external NKN receive backlog work' }
        'node_event_loop_backlog_latency' { return 'bridge-local Node event-loop backlog' }
        'socket_to_receiver_latency' { return 'socket data event to ws Receiver handoff' }
        default { return 'mixed_or_inconclusive' }
    }
}

$stageOrder = @(
    @{ Name = 'envelope_send_to_socket_data_event_emitted'; Classification = 'external_or_event_loop' },
    @{ Name = 'socket_data_event_emitted_to_ws_receiver_write_entered'; Classification = 'socket_to_receiver_latency' }
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

$candidateSummaryPath = Join-Path $CandidateArtifactDir 'helper-socket-receive-summary.txt'
$candidateValues = Read-KeyValueSummaryFile -Path $candidateSummaryPath
$candidateValues = Merge-SocketReceiveValuesFromSummaryLine -Values $candidateValues -SummaryPath $candidateSummaryPath
$bridgeEventLoopSummaryPath = Join-Path $CandidateArtifactDir 'bridge-event-loop-summary.txt'
$bridgeEventLoopValues = if (Test-Path $bridgeEventLoopSummaryPath) { Read-KeyValueSummaryFile -Path $bridgeEventLoopSummaryPath } else { @{} }

$referenceSnapshots = @()
$referenceSummaryAvailabilityCount = 0
foreach ($referenceArtifactDir in $normalizedReferenceArtifactDirs) {
    if ([string]::IsNullOrWhiteSpace($referenceArtifactDir) -or -not (Test-Path $referenceArtifactDir)) {
        continue
    }

    $referenceSummaryPath = Join-Path $referenceArtifactDir 'helper-socket-receive-summary.txt'
    if (-not (Test-Path $referenceSummaryPath)) {
        continue
    }

    $referenceValues = Read-KeyValueSummaryFile -Path $referenceSummaryPath
    $referenceValues = Merge-SocketReceiveValuesFromSummaryLine -Values $referenceValues -SummaryPath $referenceSummaryPath
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

$eventLoopP95Ms = Get-SummaryIntValue -Values $bridgeEventLoopValues -Key 'event_loop_p95_ms'
$eventLoopMaxMs = Get-SummaryIntValue -Values $bridgeEventLoopValues -Key 'event_loop_max_ms'
$eventLoopMeanMs = Get-SummaryIntValue -Values $bridgeEventLoopValues -Key 'event_loop_mean_ms'
$eventLoopSampleWindowMs = Get-SummaryIntValue -Values $bridgeEventLoopValues -Key 'sample_window_ms'

$totalDelta = 0
foreach ($stageResult in $stageResults.Values) {
    $totalDelta += [int]$stageResult['DeltaVsReference']
}

$classification = 'mixed_or_inconclusive'
if ($totalDelta -gt 0) {
    $stage1Delta = [double]$stageResults['envelope_send_to_socket_data_event_emitted'].DeltaVsReference
    $stage2Delta = [double]$stageResults['socket_data_event_emitted_to_ws_receiver_write_entered'].DeltaVsReference
    $stage1Contribution = $stage1Delta / [double]$totalDelta
    $stage2Contribution = $stage2Delta / [double]$totalDelta

    if ($stage2Contribution -ge 0.6) {
        $classification = 'socket_to_receiver_latency'
    }
    elseif ($stage1Contribution -ge 0.6) {
        if (($eventLoopP95Ms -ge 40) -or ($eventLoopMaxMs -ge 120)) {
            $classification = 'node_event_loop_backlog_latency'
        }
        else {
            $classification = 'external_receive_latency'
        }
    }
}

$smallestNextFixArea = Get-FixAreaForClassification -Classification $classification
$candidateHelperSessionPhase = Get-SummaryStringValue -Values $candidateValues -Key 'helper_session_phase' -DefaultValue '(none)'
$candidateHelperRecoveryMechanism = Get-SummaryStringValue -Values $candidateValues -Key 'helper_recovery_mechanism' -DefaultValue '(none)'
$referenceArtifactDirList = @($referenceSnapshots | ForEach-Object { $_.ArtifactDir })

$lines = @(
    ("candidate_artifact_dir={0}" -f $CandidateArtifactDir),
    ("reference_artifact_dirs={0}" -f ($referenceArtifactDirList -join ', ')),
    ("reference_socket_receive_summary_availability={0}/{1}" -f $referenceSummaryAvailabilityCount, $normalizedReferenceArtifactDirs.Count),
    ("reference_socket_receive_comparison_mode={0}" -f $(if ($referenceComparisonAvailable) { 'reference_summary_comparison' } else { 'candidate_stage_composition_fallback' })),
    ("classification={0}" -f $classification),
    ("smallest_next_fix_area={0}" -f $smallestNextFixArea),
    ("candidate_helper_session_phase={0}" -f $candidateHelperSessionPhase),
    ("candidate_helper_recovery_mechanism={0}" -f $candidateHelperRecoveryMechanism),
    ("candidate_envelope_send_to_socket_data_event_emitted_median_ms={0}" -f $stageResults['envelope_send_to_socket_data_event_emitted'].Candidate.Median),
    ("candidate_envelope_send_to_socket_data_event_emitted_p95_ms={0}" -f $stageResults['envelope_send_to_socket_data_event_emitted'].Candidate.P95),
    ("candidate_envelope_send_to_socket_data_event_emitted_max_ms={0}" -f $stageResults['envelope_send_to_socket_data_event_emitted'].Candidate.Max),
    ("reference_envelope_send_to_socket_data_event_emitted_min_ms={0}" -f $stageResults['envelope_send_to_socket_data_event_emitted'].Reference.Min),
    ("reference_envelope_send_to_socket_data_event_emitted_median_ms={0}" -f $stageResults['envelope_send_to_socket_data_event_emitted'].Reference.Median),
    ("reference_envelope_send_to_socket_data_event_emitted_max_ms={0}" -f $stageResults['envelope_send_to_socket_data_event_emitted'].Reference.Max),
    ("envelope_send_to_socket_data_event_emitted_delta_vs_reference={0}" -f $stageResults['envelope_send_to_socket_data_event_emitted'].DeltaVsReference),
    ("candidate_socket_data_event_emitted_to_ws_receiver_write_entered_median_ms={0}" -f $stageResults['socket_data_event_emitted_to_ws_receiver_write_entered'].Candidate.Median),
    ("candidate_socket_data_event_emitted_to_ws_receiver_write_entered_p95_ms={0}" -f $stageResults['socket_data_event_emitted_to_ws_receiver_write_entered'].Candidate.P95),
    ("candidate_socket_data_event_emitted_to_ws_receiver_write_entered_max_ms={0}" -f $stageResults['socket_data_event_emitted_to_ws_receiver_write_entered'].Candidate.Max),
    ("reference_socket_data_event_emitted_to_ws_receiver_write_entered_min_ms={0}" -f $stageResults['socket_data_event_emitted_to_ws_receiver_write_entered'].Reference.Min),
    ("reference_socket_data_event_emitted_to_ws_receiver_write_entered_median_ms={0}" -f $stageResults['socket_data_event_emitted_to_ws_receiver_write_entered'].Reference.Median),
    ("reference_socket_data_event_emitted_to_ws_receiver_write_entered_max_ms={0}" -f $stageResults['socket_data_event_emitted_to_ws_receiver_write_entered'].Reference.Max),
    ("socket_data_event_emitted_to_ws_receiver_write_entered_delta_vs_reference={0}" -f $stageResults['socket_data_event_emitted_to_ws_receiver_write_entered'].DeltaVsReference),
    ("candidate_event_loop_p95_ms={0}" -f $eventLoopP95Ms),
    ("candidate_event_loop_max_ms={0}" -f $eventLoopMaxMs),
    ("candidate_event_loop_mean_ms={0}" -f $eventLoopMeanMs),
    ("candidate_event_loop_sample_window_ms={0}" -f $eventLoopSampleWindowMs),
    '',
    'reference_artifact_dirs_used:'
) + @($referenceArtifactDirList)

$outputPath = Join-Path $CandidateArtifactDir 'helper-socket-receive-analysis.txt'
[System.IO.File]::WriteAllLines($outputPath, $lines)
$lines -join [Environment]::NewLine
