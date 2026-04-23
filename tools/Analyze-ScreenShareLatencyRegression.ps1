[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CandidateArtifactDir,

    [Parameter()]
    [string[]]$ReferenceArtifactDirs = @(
        "C:\Users\Juraj\Desktop\Remote help\artifacts\soak\20260422-211838",
        "C:\Users\Juraj\Desktop\Remote help\artifacts\soak\20260422-211926",
        "C:\Users\Juraj\Desktop\Remote help\artifacts\soak\20260422-212015"
    ),

    [Parameter()]
    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$InvariantCulture = [System.Globalization.CultureInfo]::InvariantCulture
$ReferenceBaselineEnvelopeMax = 404
$ReferenceHelperApplyEnvelopeMax = 520
$ReferenceVisibleApplyRatioFloor = 0.98
$ReferenceReassemblerLossEnvelopeMax = 10

function Read-KeyValueFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Required file not found: $Path"
    }

    $map = [ordered]@{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -match '^[A-Za-z0-9_]+=(.*)$') {
            $parts = $line -split '=', 2
            $map[$parts[0]] = $parts[1]
        }
    }

    return $map
}

function Get-StringValue {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Map,

        [Parameter(Mandatory = $true)]
        [string]$Key,

        [string]$Default = ""
    )

    if ($Map.Contains($Key)) {
        return [string]$Map[$Key]
    }

    return $Default
}

function Get-LongValue {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Map,

        [Parameter(Mandatory = $true)]
        [string]$Key,

        [long]$Default = 0
    )

    if (-not $Map.Contains($Key)) {
        return $Default
    }

    return [long]::Parse($Map[$Key], $InvariantCulture)
}

function Get-DoubleValue {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Map,

        [Parameter(Mandatory = $true)]
        [string]$Key,

        [double]$Default = 0
    )

    if (-not $Map.Contains($Key)) {
        return $Default
    }

    return [double]::Parse($Map[$Key], $InvariantCulture)
}

function Parse-EventLine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Line
    )

    if ($Line -notmatch '^\[(?<timestamp>[^\]]+)\]') {
        return $null
    }

    $timestamp = [DateTimeOffset]::Parse($Matches.timestamp, $InvariantCulture)
    $fields = [ordered]@{}
    foreach ($segment in ($Line -split ';')) {
        $trimmed = $segment.Trim()
        if ($trimmed -match '(?<key>[A-Za-z0-9_]+)=(?<value>.*)$') {
            $fields[$Matches.key] = $Matches.value.Trim()
        }
    }

    return [pscustomobject]@{
        Timestamp = $timestamp
        Fields = $fields
        Raw = $Line
    }
}

function Get-SummaryEventObjects {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$EventName
    )

    $events = New-Object System.Collections.Generic.List[object]
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -notmatch "event=$EventName") {
            continue
        }

        $parsed = Parse-EventLine -Line $line
        if ($null -ne $parsed) {
            $events.Add($parsed)
        }
    }

    return $events.ToArray()
}

function Get-NullableEventFieldDouble {
    param(
        [Parameter()]
        [AllowNull()]
        [object[]]$Events,

        [Parameter(Mandatory = $true)]
        [string]$Key
    )

    if ($Events.Count -eq 0) {
        return $null
    }

    $last = $Events[-1]
    if (-not $last.Fields.Contains($Key)) {
        return $null
    }

    return [double]::Parse($last.Fields[$Key], $InvariantCulture)
}

function Get-NullableEventFieldString {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Events,

        [Parameter(Mandatory = $true)]
        [string]$Key
    )

    if ($Events.Count -eq 0) {
        return $null
    }

    $last = $Events[-1]
    if (-not $last.Fields.Contains($Key)) {
        return $null
    }

    return [string]$last.Fields[$Key]
}

function Get-Median {
    param(
        [Parameter(Mandatory = $true)]
        [double[]]$Values
    )

    $ordered = $Values | Sort-Object
    if ($ordered.Count -eq 0) {
        throw "Cannot compute median of an empty set."
    }

    $mid = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1) {
        return $ordered[$mid]
    }

    return ($ordered[$mid - 1] + $ordered[$mid]) / 2.0
}

function Format-Double {
    param(
        [Parameter(Mandatory = $true)]
        [double]$Value
    )

    return [string]::Format($InvariantCulture, '{0:F1}', $Value)
}

function Format-Double2 {
    param(
        [Parameter(Mandatory = $true)]
        [double]$Value
    )

    return [string]::Format($InvariantCulture, '{0:F2}', $Value)
}

function Format-Timestamp {
    param(
        [object]$Value
    )

    if ($null -eq $Value) {
        return "(none)"
    }

    return $Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", $InvariantCulture)
}

function Get-Stats {
    param(
        [Parameter(Mandatory = $true)]
        [double[]]$Values
    )

    return [pscustomobject]@{
        Min = ($Values | Measure-Object -Minimum).Minimum
        Median = Get-Median -Values $Values
        Max = ($Values | Measure-Object -Maximum).Maximum
    }
}

function Get-WindowAnchor {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$HealthEvents
    )

    $firstRecovery = $HealthEvents | Where-Object {
        $_.Fields["helper_session_phase"] -eq "recovering"
    } | Select-Object -First 1

    $firstStableAfterRecovery = $null
    if ($null -ne $firstRecovery) {
        $firstStableAfterRecovery = $HealthEvents | Where-Object {
            $_.Timestamp -gt $firstRecovery.Timestamp -and
            $_.Fields["helper_session_phase"] -eq "visible_stable"
        } | Select-Object -First 1
    }

    $finalStable = $HealthEvents | Where-Object {
        $_.Fields["helper_session_phase"] -eq "visible_stable"
    } | Select-Object -Last 1

    $finalEvent = if ($HealthEvents.Count -gt 0) { $HealthEvents[-1] } else { $null }

    return [pscustomobject]@{
        FirstRecovery = $firstRecovery
        FirstStableAfterRecovery = $firstStableAfterRecovery
        FinalSteadyTail = if ($null -ne $finalStable) { $finalStable } else { $finalEvent }
        FinalEvent = $finalEvent
    }
}

function Get-WindowExcerpts {
    param(
        [Parameter()]
        [AllowNull()]
        [object[]]$Events,

        [Parameter(Mandatory = $false)]
        [object]$Anchor,

        [int]$WindowSeconds = 4,

        [int]$MaxLines = 3
    )

    if ($null -eq $Anchor -or $null -eq $Events -or $Events.Count -eq 0) {
        return @()
    }

    $halfWindow = [TimeSpan]::FromSeconds([double]$WindowSeconds / 2.0)
    $matches = @($Events | Where-Object {
        [Math]::Abs(($_.Timestamp - $Anchor.Timestamp).TotalSeconds) -le $halfWindow.TotalSeconds + 0.01
    } | Sort-Object Timestamp)

    if ($matches.Count -eq 0) {
        $matches = @($Events | Sort-Object { [Math]::Abs(($_.Timestamp - $Anchor.Timestamp).TotalSeconds) } | Select-Object -First $MaxLines | Sort-Object Timestamp)
    }
    elseif ($matches.Count -gt $MaxLines) {
        $matches = @($matches | Select-Object -First $MaxLines)
    }

    return @($matches | ForEach-Object { $_.Raw })
}

function Resolve-RunTimeRange {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$EventCollections
    )

    $timestamps = New-Object System.Collections.Generic.List[DateTimeOffset]
    foreach ($collection in $EventCollections) {
        foreach ($event in $collection) {
            $timestamps.Add($event.Timestamp)
        }
    }

    if ($timestamps.Count -eq 0) {
        return [pscustomobject]@{
            Start = $null
            End = $null
        }
    }

    $ordered = $timestamps | Sort-Object
    return [pscustomobject]@{
        Start = $ordered[0]
        End = $ordered[-1]
    }
}

function Get-RawLogPressureEvents {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedLogPath,

        [Parameter(Mandatory = $true)]
        [object]$TimeRange
    )

    if ([string]::IsNullOrWhiteSpace($ResolvedLogPath) -or
        -not (Test-Path -LiteralPath $ResolvedLogPath) -or
        $null -eq $TimeRange.Start -or
        $null -eq $TimeRange.End) {
        return @()
    }

    $start = $TimeRange.Start.AddSeconds(-2)
    $end = $TimeRange.End.AddSeconds(2)
    $events = New-Object System.Collections.Generic.List[object]
    foreach ($line in Get-Content -LiteralPath $ResolvedLogPath) {
        if ($line -notmatch 'event=screenshare_pressure_state_sent') {
            continue
        }

        $parsed = Parse-EventLine -Line $line
        if ($null -eq $parsed) {
            continue
        }

        if ($parsed.Timestamp -lt $start -or $parsed.Timestamp -gt $end) {
            continue
        }

        $events.Add($parsed)
    }

    return $events.ToArray()
}

function Read-Artifact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArtifactDir,

        [string]$ResolvedLogPath
    )

    $resolvedArtifactDir = (Resolve-Path -LiteralPath $ArtifactDir).Path
    $helperQualityPath = Join-Path $resolvedArtifactDir "helper-quality-summary.txt"
    $helperPressurePath = Join-Path $resolvedArtifactDir "helper-pressure-summary.txt"
    $healthPath = Join-Path $resolvedArtifactDir "health-snapshot-summary.txt"

    $helperQuality = Read-KeyValueFile -Path $helperQualityPath
    $helperPressure = Read-KeyValueFile -Path $helperPressurePath
    $health = Read-KeyValueFile -Path $healthPath

    $qualityEvents = Get-SummaryEventObjects -Path $helperQualityPath -EventName "screenshare_helper_quality_summary"
    $pressureEvents = Get-SummaryEventObjects -Path $helperPressurePath -EventName "screenshare_helper_pressure_epoch_summary"
    $healthEvents = Get-SummaryEventObjects -Path $healthPath -EventName "screenshare_health_snapshot"
    $anchor = Get-WindowAnchor -HealthEvents $healthEvents
    $timeRange = Resolve-RunTimeRange -EventCollections @($qualityEvents, $pressureEvents, $healthEvents)
    $rawPressureEvents = Get-RawLogPressureEvents -ResolvedLogPath $ResolvedLogPath -TimeRange $timeRange

    return [pscustomobject]@{
        ArtifactDir = $resolvedArtifactDir
        HelperQuality = $helperQuality
        HelperPressure = $helperPressure
        Health = $health
        QualityEvents = $qualityEvents
        PressureEvents = $pressureEvents
        HealthEvents = $healthEvents
        RawPressureEvents = $rawPressureEvents
        TimeRange = $timeRange
        Anchors = $anchor
        BaselineCaptureToRenderMs = Get-LongValue -Map $helperQuality -Key "baseline_capture_to_render_ms"
        HelperApplyMsAvg = Get-DoubleValue -Map $helperQuality -Key "helper_apply_ms_avg"
        VisibleApplyRatio = Get-DoubleValue -Map $helperQuality -Key "visible_apply_ratio"
        ReassemblerLossCount = Get-LongValue -Map $helperQuality -Key "reassembler_loss_count"
        AvgCaptureToRenderMs = [double](Get-NullableEventFieldDouble -Events $qualityEvents -Key "avg_capture_to_render_ms")
        FinalSenderOperatingState = Get-StringValue -Map $health -Key "sender_operating_state" -Default "(none)"
        FinalSenderGuardState = Get-StringValue -Map $health -Key "sender_guard_state" -Default "(none)"
        FinalHelperSessionPhase = Get-StringValue -Map $health -Key "helper_session_phase" -Default "(none)"
        FinalHelperRecoveryMechanism = Get-StringValue -Map $health -Key "helper_recovery_mechanism" -Default "(none)"
        FinalDominantPressureBlocker = Get-StringValue -Map $health -Key "dominant_pressure_blocker" -Default "(none)"
        FinalDominantTroubleDomain = Get-StringValue -Map $health -Key "dominant_trouble_domain" -Default "(none)"
        FinalDominantLossClass = Get-StringValue -Map $health -Key "dominant_loss_class" -Default "(none)"
        BaselineEstablished = Get-LongValue -Map $helperPressure -Key "baseline_established"
        BaselineReseedInProgress = Get-LongValue -Map $helperPressure -Key "baseline_reseed_in_progress"
        BaselineFrozenDueToStallCount = Get-LongValue -Map $helperPressure -Key "baseline_frozen_due_to_stall_count"
        BaselineReseedAfterRecoveryCount = Get-LongValue -Map $helperPressure -Key "baseline_reseed_after_recovery_count"
        CadenceStallWindowCount = Get-LongValue -Map $helperPressure -Key "cadence_stall_window_count"
        CadenceStallTriggerCount = Get-LongValue -Map $helperPressure -Key "cadence_stall_trigger_count"
        ActionableHighFrameAgeCount = Get-LongValue -Map $helperPressure -Key "actionable_high_frame_age_count"
        DominantHelperPressureBlocker = Get-StringValue -Map $helperPressure -Key "dominant_helper_pressure_blocker" -Default "(none)"
    }
}

function Get-EffectiveLogPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CandidateArtifactDir,

        [string]$OverrideLogPath
    )

    if (-not [string]::IsNullOrWhiteSpace($OverrideLogPath)) {
        return $OverrideLogPath
    }

    $defaultLogPath = "C:\Users\Juraj\AppData\Local\nLink\logs\nlink.log"
    if (Test-Path -LiteralPath $defaultLogPath) {
        return $defaultLogPath
    }

    $candidateHelperQuality = Read-KeyValueFile -Path (Join-Path (Resolve-Path -LiteralPath $CandidateArtifactDir).Path "helper-quality-summary.txt")
    $candidateLogPath = Get-StringValue -Map $candidateHelperQuality -Key "log_path" -Default ""
    if (-not [string]::IsNullOrWhiteSpace($candidateLogPath)) {
        return $candidateLogPath
    }

    return ""
}

function Add-WindowSection {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Lines,

        [Parameter(Mandatory = $true)]
        [string]$Prefix,

        [Parameter(Mandatory = $true)]
        [string]$WindowName,

        [Parameter()]
        [AllowNull()]
        [object]$Anchor,

        [Parameter()]
        [AllowNull()]
        [object[]]$QualityEvents,

        [Parameter()]
        [AllowNull()]
        [object[]]$PressureEvents,

        [Parameter()]
        [AllowNull()]
        [object[]]$HealthEvents,

        [Parameter()]
        [AllowNull()]
        [object[]]$RawPressureEvents
    )

    $normalizedName = $WindowName -replace '\s+', '_'
    $Lines.Add("")
    $Lines.Add("${Prefix}_${normalizedName}_anchor_utc=$(Format-Timestamp -Value $(if ($null -eq $Anchor) { $null } else { $Anchor.Timestamp }))")
    $Lines.Add("${Prefix}_${normalizedName}_quality_lines:")
    foreach ($line in Get-WindowExcerpts -Events $QualityEvents -Anchor $Anchor) {
        $Lines.Add($line)
    }
    $Lines.Add("${Prefix}_${normalizedName}_pressure_summary_lines:")
    foreach ($line in Get-WindowExcerpts -Events $PressureEvents -Anchor $Anchor) {
        $Lines.Add($line)
    }
    $Lines.Add("${Prefix}_${normalizedName}_health_lines:")
    foreach ($line in Get-WindowExcerpts -Events $HealthEvents -Anchor $Anchor) {
        $Lines.Add($line)
    }
    $Lines.Add("${Prefix}_${normalizedName}_pressure_state_sent_lines:")
    foreach ($line in Get-WindowExcerpts -Events $RawPressureEvents -Anchor $Anchor) {
        $Lines.Add($line)
    }
}

$effectiveLogPath = Get-EffectiveLogPath -CandidateArtifactDir $CandidateArtifactDir -OverrideLogPath $LogPath
$normalizedReferenceArtifactDirs = if ($ReferenceArtifactDirs.Count -eq 1 -and
    $ReferenceArtifactDirs[0] -like "*,*") {
    @($ReferenceArtifactDirs[0].Split(",", [System.StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
}
else {
    $ReferenceArtifactDirs
}
$candidate = Read-Artifact -ArtifactDir $CandidateArtifactDir -ResolvedLogPath $effectiveLogPath
$references = @($normalizedReferenceArtifactDirs | ForEach-Object {
    Read-Artifact -ArtifactDir $_ -ResolvedLogPath $effectiveLogPath
})

$referenceBaselineStats = Get-Stats -Values @($references | ForEach-Object { [double]$_.BaselineCaptureToRenderMs })
$referenceAvgCaptureStats = Get-Stats -Values @($references | ForEach-Object { [double]$_.AvgCaptureToRenderMs })
$referenceHelperApplyStats = Get-Stats -Values @($references | ForEach-Object { [double]$_.HelperApplyMsAvg })
$referenceVisibleApplyStats = Get-Stats -Values @($references | ForEach-Object { [double]$_.VisibleApplyRatio })
$referenceReassemblerLossStats = Get-Stats -Values @($references | ForEach-Object { [double]$_.ReassemblerLossCount })

$referenceBaselineEstablishedStats = Get-Stats -Values @($references | ForEach-Object { [double]$_.BaselineEstablished })
$referenceBaselineReseedInProgressStats = Get-Stats -Values @($references | ForEach-Object { [double]$_.BaselineReseedInProgress })
$referenceBaselineFrozenStats = Get-Stats -Values @($references | ForEach-Object { [double]$_.BaselineFrozenDueToStallCount })
$referenceBaselineReseedAfterRecoveryStats = Get-Stats -Values @($references | ForEach-Object { [double]$_.BaselineReseedAfterRecoveryCount })
$referenceCadenceStallWindowStats = Get-Stats -Values @($references | ForEach-Object { [double]$_.CadenceStallWindowCount })
$referenceCadenceStallTriggerStats = Get-Stats -Values @($references | ForEach-Object { [double]$_.CadenceStallTriggerCount })
$referenceActionableHighFrameAgeStats = Get-Stats -Values @($references | ForEach-Object { [double]$_.ActionableHighFrameAgeCount })
$referenceDominantHelperPressureBlockers = @($references | ForEach-Object { $_.DominantHelperPressureBlocker } | Sort-Object -Unique)

$comparisonFailures = New-Object System.Collections.Generic.List[string]
if ($candidate.BaselineCaptureToRenderMs -gt $ReferenceBaselineEnvelopeMax) {
    $comparisonFailures.Add("baseline_capture_to_render_ms_exceeded")
}
if ($candidate.HelperApplyMsAvg -gt $ReferenceHelperApplyEnvelopeMax) {
    $comparisonFailures.Add("helper_apply_ms_avg_exceeded")
}
if ($candidate.VisibleApplyRatio -lt $ReferenceVisibleApplyRatioFloor) {
    $comparisonFailures.Add("visible_apply_ratio_below_floor")
}
if ($candidate.ReassemblerLossCount -gt $ReferenceReassemblerLossEnvelopeMax) {
    $comparisonFailures.Add("reassembler_loss_count_exceeded")
}

$comparisonStatus = if ($comparisonFailures.Count -eq 0) {
    "within_reference_envelope"
}
else {
    "outside_reference_envelope"
}

$referenceAvgCaptureToleranceMax = $referenceAvgCaptureStats.Max * 1.15
$avgCaptureStayedNearReference = $candidate.AvgCaptureToRenderMs -le $referenceAvgCaptureToleranceMax
$helperApplyStayedNearReference = $candidate.HelperApplyMsAvg -le $ReferenceHelperApplyEnvelopeMax
$visibleProgressStayedHealthy = $candidate.VisibleApplyRatio -ge $ReferenceVisibleApplyRatioFloor
$lossStayedHealthy = $candidate.ReassemblerLossCount -le $ReferenceReassemblerLossEnvelopeMax

$pressureLifecycleEvidence = New-Object System.Collections.Generic.List[string]
if ($candidate.BaselineFrozenDueToStallCount -gt $referenceBaselineFrozenStats.Max) {
    $pressureLifecycleEvidence.Add("baseline_frozen_due_to_stall_count")
}
if ($candidate.CadenceStallWindowCount -gt $referenceCadenceStallWindowStats.Max) {
    $pressureLifecycleEvidence.Add("cadence_stall_window_count")
}
if ($candidate.CadenceStallTriggerCount -gt $referenceCadenceStallTriggerStats.Max) {
    $pressureLifecycleEvidence.Add("cadence_stall_trigger_count")
}
if ($candidate.ActionableHighFrameAgeCount -gt $referenceActionableHighFrameAgeStats.Max) {
    $pressureLifecycleEvidence.Add("actionable_high_frame_age_count")
}
if ($referenceDominantHelperPressureBlockers.Count -gt 0 -and
    $referenceDominantHelperPressureBlockers -notcontains $candidate.DominantHelperPressureBlocker) {
    $pressureLifecycleEvidence.Add("dominant_helper_pressure_blocker")
}

$classification = "no_material_latency_regression"
$smallestFixArea = "none"
$evidenceSummary = New-Object System.Collections.Generic.List[string]
if ($comparisonStatus -eq "outside_reference_envelope") {
    if (-not $avgCaptureStayedNearReference -or
        -not $helperApplyStayedNearReference -or
        -not $visibleProgressStayedHealthy -or
        -not $lossStayedHealthy) {
        $classification = "real_helper_latency_regression"
        $smallestFixArea = "helper visible/apply latency path"
        if (-not $avgCaptureStayedNearReference) {
            $evidenceSummary.Add("avg_capture_to_render_ms_moved_with_baseline")
        }
        if (-not $helperApplyStayedNearReference) {
            $evidenceSummary.Add("helper_apply_ms_avg_degraded")
        }
        if (-not $visibleProgressStayedHealthy) {
            $evidenceSummary.Add("visible_apply_ratio_degraded")
        }
        if (-not $lossStayedHealthy) {
            $evidenceSummary.Add("reassembler_loss_count_degraded")
        }
    }
    elseif ($pressureLifecycleEvidence.Count -gt 0) {
        $classification = "pressure_baseline_lifecycle_regression"
        $smallestFixArea = "helper pressure baseline lifecycle"
        foreach ($entry in $pressureLifecycleEvidence) {
            $evidenceSummary.Add($entry)
        }
    }
    else {
        $classification = "baseline_metric_drift"
        $smallestFixArea = "baseline metric tracking / soak proxy choice"
        $evidenceSummary.Add("baseline_capture_to_render_ms_diverged_while_apply_and_loss_metrics_stayed_near_reference")
    }
}

$reportLines = New-Object System.Collections.Generic.List[string]
$reportLines.Add("comparison_status=$comparisonStatus")
$reportLines.Add("regression_classification=$classification")
$reportLines.Add("smallest_next_fix_area=$smallestFixArea")
$reportLines.Add("candidate_artifact_dir=$($candidate.ArtifactDir)")
$reportLines.Add("reference_artifact_dirs=$($references.ArtifactDir -join ';')")
$reportLines.Add("log_path=$effectiveLogPath")
$reportLines.Add("comparison_failures=$($comparisonFailures -join ',')")
$reportLines.Add("classification_evidence=$($evidenceSummary -join ',')")
$reportLines.Add("reference_avg_capture_to_render_tolerance_max=$(Format-Double -Value $referenceAvgCaptureToleranceMax)")

$reportLines.Add("candidate_baseline_capture_to_render_ms=$($candidate.BaselineCaptureToRenderMs)")
$reportLines.Add("reference_baseline_capture_to_render_ms_min=$([long]$referenceBaselineStats.Min)")
$reportLines.Add("reference_baseline_capture_to_render_ms_median=$([long]$referenceBaselineStats.Median)")
$reportLines.Add("reference_baseline_capture_to_render_ms_max=$([long]$referenceBaselineStats.Max)")

$reportLines.Add("candidate_avg_capture_to_render_ms=$(Format-Double -Value $candidate.AvgCaptureToRenderMs)")
$reportLines.Add("reference_avg_capture_to_render_ms_min=$(Format-Double -Value $referenceAvgCaptureStats.Min)")
$reportLines.Add("reference_avg_capture_to_render_ms_median=$(Format-Double -Value $referenceAvgCaptureStats.Median)")
$reportLines.Add("reference_avg_capture_to_render_ms_max=$(Format-Double -Value $referenceAvgCaptureStats.Max)")

$reportLines.Add("candidate_helper_apply_ms_avg=$(Format-Double -Value $candidate.HelperApplyMsAvg)")
$reportLines.Add("reference_helper_apply_ms_avg_min=$(Format-Double -Value $referenceHelperApplyStats.Min)")
$reportLines.Add("reference_helper_apply_ms_avg_median=$(Format-Double -Value $referenceHelperApplyStats.Median)")
$reportLines.Add("reference_helper_apply_ms_avg_max=$(Format-Double -Value $referenceHelperApplyStats.Max)")

$reportLines.Add("candidate_visible_apply_ratio=$(Format-Double2 -Value $candidate.VisibleApplyRatio)")
$reportLines.Add("reference_visible_apply_ratio_min=$(Format-Double2 -Value $referenceVisibleApplyStats.Min)")
$reportLines.Add("reference_visible_apply_ratio_median=$(Format-Double2 -Value $referenceVisibleApplyStats.Median)")
$reportLines.Add("reference_visible_apply_ratio_max=$(Format-Double2 -Value $referenceVisibleApplyStats.Max)")

$reportLines.Add("candidate_reassembler_loss_count=$($candidate.ReassemblerLossCount)")
$reportLines.Add("reference_reassembler_loss_count_min=$([long]$referenceReassemblerLossStats.Min)")
$reportLines.Add("reference_reassembler_loss_count_median=$([long]$referenceReassemblerLossStats.Median)")
$reportLines.Add("reference_reassembler_loss_count_max=$([long]$referenceReassemblerLossStats.Max)")

$reportLines.Add("candidate_sender_operating_state=$($candidate.FinalSenderOperatingState)")
$reportLines.Add("candidate_sender_guard_state=$($candidate.FinalSenderGuardState)")
$reportLines.Add("candidate_helper_session_phase=$($candidate.FinalHelperSessionPhase)")
$reportLines.Add("candidate_helper_recovery_mechanism=$($candidate.FinalHelperRecoveryMechanism)")
$reportLines.Add("candidate_dominant_loss_class=$($candidate.FinalDominantLossClass)")
$reportLines.Add("candidate_dominant_pressure_blocker=$($candidate.FinalDominantPressureBlocker)")
$reportLines.Add("candidate_dominant_trouble_domain=$($candidate.FinalDominantTroubleDomain)")
$reportLines.Add("reference_sender_operating_states=$($references.FinalSenderOperatingState -join ',')")
$reportLines.Add("reference_sender_guard_states=$($references.FinalSenderGuardState -join ',')")
$reportLines.Add("reference_helper_session_phases=$($references.FinalHelperSessionPhase -join ',')")
$reportLines.Add("reference_helper_recovery_mechanisms=$($references.FinalHelperRecoveryMechanism -join ',')")

$reportLines.Add("candidate_baseline_established=$($candidate.BaselineEstablished)")
$reportLines.Add("reference_baseline_established_min=$([long]$referenceBaselineEstablishedStats.Min)")
$reportLines.Add("reference_baseline_established_median=$([long]$referenceBaselineEstablishedStats.Median)")
$reportLines.Add("reference_baseline_established_max=$([long]$referenceBaselineEstablishedStats.Max)")

$reportLines.Add("candidate_baseline_reseed_in_progress=$($candidate.BaselineReseedInProgress)")
$reportLines.Add("reference_baseline_reseed_in_progress_min=$([long]$referenceBaselineReseedInProgressStats.Min)")
$reportLines.Add("reference_baseline_reseed_in_progress_median=$([long]$referenceBaselineReseedInProgressStats.Median)")
$reportLines.Add("reference_baseline_reseed_in_progress_max=$([long]$referenceBaselineReseedInProgressStats.Max)")

$reportLines.Add("candidate_baseline_frozen_due_to_stall_count=$($candidate.BaselineFrozenDueToStallCount)")
$reportLines.Add("reference_baseline_frozen_due_to_stall_count_min=$([long]$referenceBaselineFrozenStats.Min)")
$reportLines.Add("reference_baseline_frozen_due_to_stall_count_median=$([long]$referenceBaselineFrozenStats.Median)")
$reportLines.Add("reference_baseline_frozen_due_to_stall_count_max=$([long]$referenceBaselineFrozenStats.Max)")

$reportLines.Add("candidate_baseline_reseed_after_recovery_count=$($candidate.BaselineReseedAfterRecoveryCount)")
$reportLines.Add("reference_baseline_reseed_after_recovery_count_min=$([long]$referenceBaselineReseedAfterRecoveryStats.Min)")
$reportLines.Add("reference_baseline_reseed_after_recovery_count_median=$([long]$referenceBaselineReseedAfterRecoveryStats.Median)")
$reportLines.Add("reference_baseline_reseed_after_recovery_count_max=$([long]$referenceBaselineReseedAfterRecoveryStats.Max)")

$reportLines.Add("candidate_cadence_stall_window_count=$($candidate.CadenceStallWindowCount)")
$reportLines.Add("reference_cadence_stall_window_count_min=$([long]$referenceCadenceStallWindowStats.Min)")
$reportLines.Add("reference_cadence_stall_window_count_median=$([long]$referenceCadenceStallWindowStats.Median)")
$reportLines.Add("reference_cadence_stall_window_count_max=$([long]$referenceCadenceStallWindowStats.Max)")

$reportLines.Add("candidate_cadence_stall_trigger_count=$($candidate.CadenceStallTriggerCount)")
$reportLines.Add("reference_cadence_stall_trigger_count_min=$([long]$referenceCadenceStallTriggerStats.Min)")
$reportLines.Add("reference_cadence_stall_trigger_count_median=$([long]$referenceCadenceStallTriggerStats.Median)")
$reportLines.Add("reference_cadence_stall_trigger_count_max=$([long]$referenceCadenceStallTriggerStats.Max)")

$reportLines.Add("candidate_actionable_high_frame_age_count=$($candidate.ActionableHighFrameAgeCount)")
$reportLines.Add("reference_actionable_high_frame_age_count_min=$([long]$referenceActionableHighFrameAgeStats.Min)")
$reportLines.Add("reference_actionable_high_frame_age_count_median=$([long]$referenceActionableHighFrameAgeStats.Median)")
$reportLines.Add("reference_actionable_high_frame_age_count_max=$([long]$referenceActionableHighFrameAgeStats.Max)")
$reportLines.Add("candidate_dominant_helper_pressure_blocker=$($candidate.DominantHelperPressureBlocker)")
$reportLines.Add("reference_dominant_helper_pressure_blockers=$($referenceDominantHelperPressureBlockers -join ',')")

Add-WindowSection -Lines $reportLines -Prefix "candidate" -WindowName "first_recovery" -Anchor $candidate.Anchors.FirstRecovery -QualityEvents $candidate.QualityEvents -PressureEvents $candidate.PressureEvents -HealthEvents $candidate.HealthEvents -RawPressureEvents $candidate.RawPressureEvents
Add-WindowSection -Lines $reportLines -Prefix "candidate" -WindowName "first_visible_stable_return" -Anchor $candidate.Anchors.FirstStableAfterRecovery -QualityEvents $candidate.QualityEvents -PressureEvents $candidate.PressureEvents -HealthEvents $candidate.HealthEvents -RawPressureEvents $candidate.RawPressureEvents
Add-WindowSection -Lines $reportLines -Prefix "candidate" -WindowName "final_steady_state_tail" -Anchor $candidate.Anchors.FinalSteadyTail -QualityEvents $candidate.QualityEvents -PressureEvents $candidate.PressureEvents -HealthEvents $candidate.HealthEvents -RawPressureEvents $candidate.RawPressureEvents

$referenceIndex = 1
foreach ($reference in $references) {
    Add-WindowSection -Lines $reportLines -Prefix "reference${referenceIndex}" -WindowName "first_recovery" -Anchor $reference.Anchors.FirstRecovery -QualityEvents $reference.QualityEvents -PressureEvents $reference.PressureEvents -HealthEvents $reference.HealthEvents -RawPressureEvents $reference.RawPressureEvents
    Add-WindowSection -Lines $reportLines -Prefix "reference${referenceIndex}" -WindowName "first_visible_stable_return" -Anchor $reference.Anchors.FirstStableAfterRecovery -QualityEvents $reference.QualityEvents -PressureEvents $reference.PressureEvents -HealthEvents $reference.HealthEvents -RawPressureEvents $reference.RawPressureEvents
    Add-WindowSection -Lines $reportLines -Prefix "reference${referenceIndex}" -WindowName "final_steady_state_tail" -Anchor $reference.Anchors.FinalSteadyTail -QualityEvents $reference.QualityEvents -PressureEvents $reference.PressureEvents -HealthEvents $reference.HealthEvents -RawPressureEvents $reference.RawPressureEvents
    $referenceIndex++
}

$reportPath = Join-Path $candidate.ArtifactDir "latency-regression-analysis.txt"
$reportLines | Set-Content -LiteralPath $reportPath
$reportLines | ForEach-Object { Write-Output $_ }
