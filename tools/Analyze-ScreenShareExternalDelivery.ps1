param(
    [Parameter(Mandatory = $true)][string]$CandidateArtifactDir,
    [string[]]$ReferenceArtifactDirs = @(
        'C:\Users\Juraj\Desktop\Remote help\artifacts\soak\20260423-142248',
        'C:\Users\Juraj\Desktop\Remote help\artifacts\soak\20260423-153448'
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-KeyValueSummaryFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) {
        throw "Summary file not found: $Path"
    }

    $values = @{}
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

function Get-BestBridgeMediaSendSummaryLine {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) {
        return ''
    }

    $inSummaryLinesSection = $false
    $bestFramesSent = -1
    $bestSummaryLine = ''
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if (-not $inSummaryLinesSection) {
            if ([string]::Equals($line.Trim(), 'bridge_media_send_summary_lines:', [System.StringComparison]::OrdinalIgnoreCase)) {
                $inSummaryLinesSection = $true
            }

            continue
        }

        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line.IndexOf('screenshare_bridge_media_send_summary', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            continue
        }

        $pairs = Get-StructuredLogPairs -Line $line.Trim()
        $framesSentValue = Get-StructuredLogFieldValue -Pairs $pairs -Key 'frames_sent'
        $framesSent = -1
        [void][int]::TryParse($framesSentValue, [ref]$framesSent)
        if ($framesSent -ge $bestFramesSent) {
            $bestFramesSent = $framesSent
            $bestSummaryLine = $line.Trim()
        }
    }

    return $bestSummaryLine
}

function Merge-BridgeMediaSendValuesFromSummaryLine {
    param(
        [hashtable]$Values,
        [string]$SummaryPath
    )

    if ($null -eq $Values -or [string]::IsNullOrWhiteSpace($SummaryPath)) {
        return $Values
    }

    $summaryLine = Get-BestBridgeMediaSendSummaryLine -Path $SummaryPath
    if ([string]::IsNullOrWhiteSpace($summaryLine)) {
        return $Values
    }

    $pairs = Get-StructuredLogPairs -Line $summaryLine
    if ($pairs.Count -eq 0) {
        return $Values
    }

    foreach ($key in @(
            'binary_send_frame_observed_to_queue_enqueue_avg_ms',
            'binary_send_frame_observed_to_queue_enqueue_median_ms',
            'binary_send_frame_observed_to_queue_enqueue_p95_ms',
            'binary_send_frame_observed_to_queue_enqueue_max_ms',
            'queue_enqueue_to_queue_dequeue_avg_ms',
            'queue_enqueue_to_queue_dequeue_median_ms',
            'queue_enqueue_to_queue_dequeue_p95_ms',
            'queue_enqueue_to_queue_dequeue_max_ms',
            'queue_dequeue_to_media_send_started_avg_ms',
            'queue_dequeue_to_media_send_started_median_ms',
            'queue_dequeue_to_media_send_started_p95_ms',
            'queue_dequeue_to_media_send_started_max_ms',
            'media_send_started_to_media_send_resolved_avg_ms',
            'media_send_started_to_media_send_resolved_median_ms',
            'media_send_started_to_media_send_resolved_p95_ms',
            'media_send_started_to_media_send_resolved_max_ms',
            'frames_sent',
            'send_failures',
            'queue_drops',
            'queue_depth',
            'oldest_queued_age_ms',
            'sample_window_ms',
            'queue_mode')) {
        $value = Get-StructuredLogFieldValue -Pairs $pairs -Key $key
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $Values[$key] = $value
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

function Get-StageDelta {
    param(
        [int]$CandidateMedian,
        [int]$ReferenceMax,
        [bool]$ReferenceComparisonAvailable
    )

    if ($CandidateMedian -lt 0) {
        return 0
    }

    if ($ReferenceComparisonAvailable -and $ReferenceMax -ge 0) {
        return [Math]::Max(0, $CandidateMedian - $ReferenceMax)
    }

    return $CandidateMedian
}

function Get-FixAreaForClassification {
    param([string]$Classification)

    switch -Exact ($Classification) {
        'bridge_send_ingress_latency' { return 'sender bridge binary-send ingress' }
        'sender_bridge_queue_latency' { return 'sender bridge media queue' }
        'sender_bridge_publish_latency' { return 'sender bridge NKN publish path' }
        'network_delivery_latency' { return 'external NKN/network receive backlog work' }
        default { return 'mixed_or_inconclusive' }
    }
}

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

$candidateSocketSummaryPath = Join-Path $CandidateArtifactDir 'helper-socket-receive-summary.txt'
$candidateBridgeSummaryPath = Join-Path $CandidateArtifactDir 'bridge-media-send-summary.txt'
$candidateSocketValues = Read-KeyValueSummaryFile -Path $candidateSocketSummaryPath
$candidateBridgeValues = Read-KeyValueSummaryFile -Path $candidateBridgeSummaryPath
$candidateBridgeValues = Merge-BridgeMediaSendValuesFromSummaryLine -Values $candidateBridgeValues -SummaryPath $candidateBridgeSummaryPath

$referenceSnapshots = @()
foreach ($referenceArtifactDir in $normalizedReferenceArtifactDirs) {
    if ([string]::IsNullOrWhiteSpace($referenceArtifactDir) -or -not (Test-Path $referenceArtifactDir)) {
        continue
    }

    $referenceSocketSummaryPath = Join-Path $referenceArtifactDir 'helper-socket-receive-summary.txt'
    $referenceBridgeSummaryPath = Join-Path $referenceArtifactDir 'bridge-media-send-summary.txt'
    if (-not (Test-Path $referenceSocketSummaryPath) -or -not (Test-Path $referenceBridgeSummaryPath)) {
        continue
    }

    $referenceSnapshots += @{
        ArtifactDir = $referenceArtifactDir
        SocketValues = Read-KeyValueSummaryFile -Path $referenceSocketSummaryPath
        BridgeValues = (Merge-BridgeMediaSendValuesFromSummaryLine -Values (Read-KeyValueSummaryFile -Path $referenceBridgeSummaryPath) -SummaryPath $referenceBridgeSummaryPath)
    }
}

$referenceComparisonAvailable = $referenceSnapshots.Count -gt 0
$comparisonMode = if ($referenceComparisonAvailable) { 'reference_summary_comparison' } else { 'candidate_stage_composition_fallback' }

$externalStage = Get-StageSnapshot -Values $candidateSocketValues -StagePrefix 'envelope_send_to_socket_data_event_emitted'
$ingressStage = Get-StageSnapshot -Values $candidateBridgeValues -StagePrefix 'binary_send_frame_observed_to_queue_enqueue'
$queueStage = Get-StageSnapshot -Values $candidateBridgeValues -StagePrefix 'queue_enqueue_to_queue_dequeue'
$sendStartedStage = Get-StageSnapshot -Values $candidateBridgeValues -StagePrefix 'queue_dequeue_to_media_send_started'
$sendResolvedStage = Get-StageSnapshot -Values $candidateBridgeValues -StagePrefix 'media_send_started_to_media_send_resolved'
$publishStage = @{
    Avg = if ($sendStartedStage.Avg -ge 0 -and $sendResolvedStage.Avg -ge 0) { $sendStartedStage.Avg + $sendResolvedStage.Avg } else { -1 }
    Median = if ($sendStartedStage.Median -ge 0 -and $sendResolvedStage.Median -ge 0) { $sendStartedStage.Median + $sendResolvedStage.Median } else { -1 }
    P95 = if ($sendStartedStage.P95 -ge 0 -and $sendResolvedStage.P95 -ge 0) { $sendStartedStage.P95 + $sendResolvedStage.P95 } else { -1 }
    Max = if ($sendStartedStage.Max -ge 0 -and $sendResolvedStage.Max -ge 0) { $sendStartedStage.Max + $sendResolvedStage.Max } else { -1 }
}

$referenceExternalStats = Get-IntStats -Values @(
    foreach ($referenceSnapshot in $referenceSnapshots) {
        $value = Get-SummaryIntValue -Values $referenceSnapshot.SocketValues -Key 'envelope_send_to_socket_data_event_emitted_median_ms'
        if ($value -ge 0) { $value }
    }
)
$referenceIngressStats = Get-IntStats -Values @(
    foreach ($referenceSnapshot in $referenceSnapshots) {
        $value = Get-SummaryIntValue -Values $referenceSnapshot.BridgeValues -Key 'binary_send_frame_observed_to_queue_enqueue_median_ms'
        if ($value -ge 0) { $value }
    }
)
$referenceQueueStats = Get-IntStats -Values @(
    foreach ($referenceSnapshot in $referenceSnapshots) {
        $value = Get-SummaryIntValue -Values $referenceSnapshot.BridgeValues -Key 'queue_enqueue_to_queue_dequeue_median_ms'
        if ($value -ge 0) { $value }
    }
)
$referenceSendStartedStats = Get-IntStats -Values @(
    foreach ($referenceSnapshot in $referenceSnapshots) {
        $value = Get-SummaryIntValue -Values $referenceSnapshot.BridgeValues -Key 'queue_dequeue_to_media_send_started_median_ms'
        if ($value -ge 0) { $value }
    }
)
$referenceSendResolvedStats = Get-IntStats -Values @(
    foreach ($referenceSnapshot in $referenceSnapshots) {
        $value = Get-SummaryIntValue -Values $referenceSnapshot.BridgeValues -Key 'media_send_started_to_media_send_resolved_median_ms'
        if ($value -ge 0) { $value }
    }
)
$referencePublishStats = Get-IntStats -Values @(
    foreach ($referenceSnapshot in $referenceSnapshots) {
        $startValue = Get-SummaryIntValue -Values $referenceSnapshot.BridgeValues -Key 'queue_dequeue_to_media_send_started_median_ms'
        $resolvedValue = Get-SummaryIntValue -Values $referenceSnapshot.BridgeValues -Key 'media_send_started_to_media_send_resolved_median_ms'
        if ($startValue -ge 0 -and $resolvedValue -ge 0) {
            $startValue + $resolvedValue
        }
    }
)

$externalDelta = Get-StageDelta -CandidateMedian $externalStage.Median -ReferenceMax $referenceExternalStats.Max -ReferenceComparisonAvailable $referenceComparisonAvailable
$ingressDelta = Get-StageDelta -CandidateMedian $ingressStage.Median -ReferenceMax $referenceIngressStats.Max -ReferenceComparisonAvailable $referenceComparisonAvailable
$queueDelta = Get-StageDelta -CandidateMedian $queueStage.Median -ReferenceMax $referenceQueueStats.Max -ReferenceComparisonAvailable $referenceComparisonAvailable
$publishDelta = Get-StageDelta -CandidateMedian $publishStage.Median -ReferenceMax $referencePublishStats.Max -ReferenceComparisonAvailable $referenceComparisonAvailable
$localSenderDelta = $ingressDelta + $queueDelta + $publishDelta
$networkResidualDelta = [Math]::Max(0, $externalDelta - $localSenderDelta)

$classification = 'mixed_or_inconclusive'
if ($externalDelta -gt 0) {
    $localSenderContribution = $localSenderDelta / [double]$externalDelta
    if ($localSenderContribution -ge 0.6 -and $localSenderDelta -gt 0) {
        $ingressContribution = $ingressDelta / [double]$localSenderDelta
        $queueContribution = $queueDelta / [double]$localSenderDelta
        $publishContribution = $publishDelta / [double]$localSenderDelta

        if ($ingressContribution -ge 0.6) {
            $classification = 'bridge_send_ingress_latency'
        }
        elseif ($queueContribution -ge 0.6) {
            $classification = 'sender_bridge_queue_latency'
        }
        elseif ($publishContribution -ge 0.6) {
            $classification = 'sender_bridge_publish_latency'
        }
    }

    if ([string]::Equals($classification, 'mixed_or_inconclusive', [System.StringComparison]::Ordinal) -and
        (($networkResidualDelta / [double]$externalDelta) -ge 0.6)) {
        $classification = 'network_delivery_latency'
    }
}

$smallestNextFixArea = Get-FixAreaForClassification -Classification $classification
$helperSessionPhase = Get-SummaryStringValue -Values $candidateSocketValues -Key 'helper_session_phase' -DefaultValue '(none)'
$helperRecoveryMechanism = Get-SummaryStringValue -Values $candidateSocketValues -Key 'helper_recovery_mechanism' -DefaultValue '(none)'
$queueMode = Get-SummaryStringValue -Values $candidateBridgeValues -Key 'queue_mode' -DefaultValue 'normal'
$framesSent = Get-SummaryIntValue -Values $candidateBridgeValues -Key 'frames_sent'
$sendFailures = Get-SummaryIntValue -Values $candidateBridgeValues -Key 'send_failures'
$queueDrops = Get-SummaryIntValue -Values $candidateBridgeValues -Key 'queue_drops'
$queueDepth = Get-SummaryIntValue -Values $candidateBridgeValues -Key 'queue_depth'
$oldestQueuedAgeMs = Get-SummaryIntValue -Values $candidateBridgeValues -Key 'oldest_queued_age_ms'
$sampleWindowMs = Get-SummaryIntValue -Values $candidateBridgeValues -Key 'sample_window_ms'

$reportLines = @(
    ("candidate_artifact_dir={0}" -f $CandidateArtifactDir),
    ("reference_artifact_dirs={0}" -f $(if ($normalizedReferenceArtifactDirs.Count -gt 0) { [string]::Join(',', $normalizedReferenceArtifactDirs) } else { '(none)' })),
    ("reference_external_delivery_summary_availability={0}/{1}" -f $referenceSnapshots.Count, $normalizedReferenceArtifactDirs.Count),
    ("reference_external_delivery_comparison_mode={0}" -f $comparisonMode),
    ("classification={0}" -f $classification),
    ("smallest_next_fix_area={0}" -f $smallestNextFixArea),
    ("helper_session_phase={0}" -f $helperSessionPhase),
    ("helper_recovery_mechanism={0}" -f $helperRecoveryMechanism),
    ("candidate_envelope_send_to_socket_data_event_emitted_median_ms={0}" -f $externalStage.Median),
    ("candidate_binary_send_frame_observed_to_queue_enqueue_median_ms={0}" -f $ingressStage.Median),
    ("candidate_queue_enqueue_to_queue_dequeue_median_ms={0}" -f $queueStage.Median),
    ("candidate_queue_dequeue_to_media_send_started_median_ms={0}" -f $sendStartedStage.Median),
    ("candidate_media_send_started_to_media_send_resolved_median_ms={0}" -f $sendResolvedStage.Median),
    ("candidate_sender_bridge_publish_median_ms={0}" -f $publishStage.Median),
    ("candidate_local_sender_delta_ms={0}" -f $localSenderDelta),
    ("candidate_network_delivery_residual_ms={0}" -f $networkResidualDelta),
    ("candidate_frames_sent={0}" -f $framesSent),
    ("candidate_send_failures={0}" -f $sendFailures),
    ("candidate_queue_drops={0}" -f $queueDrops),
    ("candidate_queue_mode={0}" -f $queueMode),
    ("candidate_queue_depth={0}" -f $queueDepth),
    ("candidate_oldest_queued_age_ms={0}" -f $oldestQueuedAgeMs),
    ("candidate_sample_window_ms={0}" -f $sampleWindowMs),
    ("reference_envelope_send_to_socket_data_event_emitted_min_ms={0}" -f $referenceExternalStats.Min),
    ("reference_envelope_send_to_socket_data_event_emitted_median_ms={0}" -f $referenceExternalStats.Median),
    ("reference_envelope_send_to_socket_data_event_emitted_max_ms={0}" -f $referenceExternalStats.Max),
    ("reference_binary_send_frame_observed_to_queue_enqueue_min_ms={0}" -f $referenceIngressStats.Min),
    ("reference_binary_send_frame_observed_to_queue_enqueue_median_ms={0}" -f $referenceIngressStats.Median),
    ("reference_binary_send_frame_observed_to_queue_enqueue_max_ms={0}" -f $referenceIngressStats.Max),
    ("reference_queue_enqueue_to_queue_dequeue_min_ms={0}" -f $referenceQueueStats.Min),
    ("reference_queue_enqueue_to_queue_dequeue_median_ms={0}" -f $referenceQueueStats.Median),
    ("reference_queue_enqueue_to_queue_dequeue_max_ms={0}" -f $referenceQueueStats.Max),
    ("reference_queue_dequeue_to_media_send_started_min_ms={0}" -f $referenceSendStartedStats.Min),
    ("reference_queue_dequeue_to_media_send_started_median_ms={0}" -f $referenceSendStartedStats.Median),
    ("reference_queue_dequeue_to_media_send_started_max_ms={0}" -f $referenceSendStartedStats.Max),
    ("reference_media_send_started_to_media_send_resolved_min_ms={0}" -f $referenceSendResolvedStats.Min),
    ("reference_media_send_started_to_media_send_resolved_median_ms={0}" -f $referenceSendResolvedStats.Median),
    ("reference_media_send_started_to_media_send_resolved_max_ms={0}" -f $referenceSendResolvedStats.Max),
    ("reference_sender_bridge_publish_min_ms={0}" -f $referencePublishStats.Min),
    ("reference_sender_bridge_publish_median_ms={0}" -f $referencePublishStats.Median),
    ("reference_sender_bridge_publish_max_ms={0}" -f $referencePublishStats.Max)
)

$reportPath = Join-Path $CandidateArtifactDir 'helper-external-delivery-analysis.txt'
Set-Content -Path $reportPath -Value $reportLines
$reportLines
