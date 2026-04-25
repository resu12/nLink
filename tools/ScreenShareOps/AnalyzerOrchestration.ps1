Set-StrictMode -Version Latest

function Get-ScreenShareOpsManifestPath {
    return (Join-Path $PSScriptRoot "retained-analyzer-chain.json")
}

function Get-RequiredManifestString {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$PropertyName,
        [switch]$AllowEmpty
    )

    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        throw "Retained analyzer manifest entry is missing '$PropertyName'."
    }

    $value = [string]$property.Value
    if (-not $AllowEmpty -and [string]::IsNullOrWhiteSpace($value)) {
        throw "Retained analyzer manifest entry has an empty '$PropertyName'."
    }

    return $value
}

function Assert-NoDuplicateManifestValues {
    param(
        [Parameter(Mandatory = $true)][string[]]$Values,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $duplicates = @(
        $Values |
            Group-Object |
            Where-Object { $_.Count -gt 1 } |
            Select-Object -ExpandProperty Name
    )

    if ($duplicates.Count -gt 0) {
        throw ("Retained analyzer manifest has duplicate {0}: {1}" -f $Name, ($duplicates -join ", "))
    }
}

function Get-ScreenShareRetainedAnalyzerManifest {
    $manifestPath = Get-ScreenShareOpsManifestPath
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Retained analyzer manifest not found: $manifestPath"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([int]$manifest.schema_version -ne 1) {
        throw ("Unsupported retained analyzer manifest schema version: {0}" -f $manifest.schema_version)
    }

    $rawAnalyzers = @($manifest.retained_analyzers)
    if ($rawAnalyzers.Count -eq 0) {
        throw "Retained analyzer manifest contains no analyzer entries."
    }

    $analyzers = foreach ($entry in $rawAnalyzers) {
        [pscustomobject]@{
            Id = Get-RequiredManifestString -Object $entry -PropertyName "id"
            Script = Get-RequiredManifestString -Object $entry -PropertyName "script"
            Report = Get-RequiredManifestString -Object $entry -PropertyName "report"
            ClassificationStage = Get-RequiredManifestString -Object $entry -PropertyName "classification_stage" -AllowEmpty
        }
    }

    Assert-NoDuplicateManifestValues -Values ([string[]]@($analyzers | ForEach-Object { $_.Id })) -Name "ids"
    Assert-NoDuplicateManifestValues -Values ([string[]]@($analyzers | ForEach-Object { $_.Script })) -Name "scripts"
    Assert-NoDuplicateManifestValues -Values ([string[]]@($analyzers | ForEach-Object { $_.Report })) -Name "reports"

    $classificationStages = [string[]]@(
        $analyzers |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_.ClassificationStage) } |
            ForEach-Object { $_.ClassificationStage }
    )
    Assert-NoDuplicateManifestValues -Values $classificationStages -Name "classification stages"

    $externalClassifications = [string[]]@($manifest.external_transport_classifications | ForEach-Object { [string]$_ })
    if ($externalClassifications.Count -eq 0) {
        throw "Retained analyzer manifest contains no external transport classifications."
    }
    Assert-NoDuplicateManifestValues -Values $externalClassifications -Name "external transport classifications"

    return [pscustomobject]@{
        SchemaVersion = 1
        RetainedAnalyzers = @($analyzers)
        ExternalTransportClassifications = @($externalClassifications)
    }
}

function Get-ScreenShareRetainedClassificationReports {
    $manifest = Get-ScreenShareRetainedAnalyzerManifest
    foreach ($analyzer in @($manifest.RetainedAnalyzers)) {
        if ([string]::IsNullOrWhiteSpace($analyzer.ClassificationStage)) {
            continue
        }

        [pscustomobject]@{
            Stage = $analyzer.ClassificationStage
            FileName = $analyzer.Report
        }
    }
}

function Get-ScreenShareExternalTransportClassifications {
    $manifest = Get-ScreenShareRetainedAnalyzerManifest
    return [string[]]@($manifest.ExternalTransportClassifications)
}

function Resolve-ScreenShareAnalyzerScriptPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ScriptName
    )

    $analyzerRoot = $env:NLINK_SCREENSHARE_OPS_ANALYZER_ROOT
    if ([string]::IsNullOrWhiteSpace($analyzerRoot)) {
        $analyzerRoot = Join-Path $RepoRoot "tools"
    }
    elseif (-not [System.IO.Path]::IsPathRooted($analyzerRoot)) {
        $analyzerRoot = Join-Path $RepoRoot $analyzerRoot
    }

    return (Join-Path $analyzerRoot $ScriptName)
}

function Invoke-ScreenShareRetainedAnalyzerChain {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    if ($env:NLINK_SCREENSHARE_OPS_VERDICT_ONLY -eq "1") {
        Write-Host "[ScreenShareOps] verdict-only test hook active; retained analyzers are not invoked." -ForegroundColor Yellow
        return 0
    }

    if ($null -eq (Get-Command Invoke-PowerShellScript -ErrorAction SilentlyContinue)) {
        throw "Invoke-PowerShellScript must be defined before invoking the retained analyzer chain."
    }

    $manifest = Get-ScreenShareRetainedAnalyzerManifest
    foreach ($analyzer in @($manifest.RetainedAnalyzers)) {
        $exitCode = Invoke-PowerShellScript `
            -ScriptPath (Resolve-ScreenShareAnalyzerScriptPath -RepoRoot $RepoRoot -ScriptName $analyzer.Script) `
            -Parameters ([ordered]@{ CandidateArtifactDir = $ArtifactDir })
        if ($exitCode -ne 0) {
            return $exitCode
        }
    }

    return 0
}

function Read-KeyValueReport {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $values = [ordered]@{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*([A-Za-z0-9_]+)=(.*)$') {
            $values[$matches[1]] = $matches[2].Trim()
        }
    }

    return $values
}

function Get-KeyValue {
    param(
        [AllowNull()][System.Collections.IDictionary]$Values,
        [Parameter(Mandatory = $true)][string]$Key
    )

    if ($null -eq $Values -or -not $Values.Contains($Key)) {
        return $null
    }

    return [string]$Values[$Key]
}

function Get-ReportStringValue {
    param(
        [AllowNull()][System.Collections.IDictionary]$Values,
        [Parameter(Mandatory = $true)][string]$Key,
        [string]$DefaultValue = "(missing)"
    )

    $value = Get-KeyValue -Values $Values -Key $Key
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace($value)) {
        return $DefaultValue
    }

    return $value
}

function Get-ReportIntValue {
    param(
        [AllowNull()][System.Collections.IDictionary]$Values,
        [Parameter(Mandatory = $true)][string]$Key,
        [int]$DefaultValue = -1
    )

    $value = Get-KeyValue -Values $Values -Key $Key
    if ($null -eq $value) {
        return $DefaultValue
    }

    $parsed = 0
    if ([int]::TryParse($value, [ref]$parsed)) {
        return $parsed
    }

    return $DefaultValue
}

function Get-ReportLongValue {
    param(
        [AllowNull()][System.Collections.IDictionary]$Values,
        [Parameter(Mandatory = $true)][string]$Key,
        [long]$DefaultValue = -1
    )

    $value = Get-KeyValue -Values $Values -Key $Key
    if ($null -eq $value) {
        return $DefaultValue
    }

    $parsed = [long]0
    if ([long]::TryParse($value, [ref]$parsed)) {
        return $parsed
    }

    return $DefaultValue
}

function Get-ReportDoubleValue {
    param(
        [AllowNull()][System.Collections.IDictionary]$Values,
        [Parameter(Mandatory = $true)][string]$Key,
        [double]$DefaultValue = -1
    )

    $value = Get-KeyValue -Values $Values -Key $Key
    if ($null -eq $value) {
        return $DefaultValue
    }

    $parsed = [double]0
    if ([double]::TryParse(
            $value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        return $parsed
    }

    return $DefaultValue
}

function Read-ReportLines {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    return @(Get-Content -LiteralPath $Path)
}

function Get-AllStructuredFieldValues {
    param(
        [AllowNull()][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Key
    )

    $values = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Lines) {
        return [string[]]@()
    }

    $pattern = "(?:^|[;|]\s*){0}=([^;|]+)" -f [regex]::Escape($Key)
    foreach ($line in $Lines) {
        foreach ($match in [regex]::Matches($line, $pattern)) {
            $value = $match.Groups[1].Value.Trim()
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $values.Add($value) | Out-Null
            }
        }
    }

    return [string[]]@($values)
}

function Get-LatestStructuredFieldValue {
    param(
        [AllowNull()][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Key,
        [string]$DefaultValue = "(missing)"
    )

    $values = @(Get-AllStructuredFieldValues -Lines $Lines -Key $Key)
    if ($values.Count -eq 0) {
        return $DefaultValue
    }

    return $values[$values.Count - 1]
}

function Get-LatestStructuredDoubleValue {
    param(
        [AllowNull()][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Key,
        [double]$DefaultValue = -1
    )

    $value = Get-LatestStructuredFieldValue -Lines $Lines -Key $Key -DefaultValue "(missing)"
    $parsed = [double]0
    if ([double]::TryParse(
            $value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        return $parsed
    }

    return $DefaultValue
}

function Format-ScreenShareDouble {
    param([double]$Value)

    if ($Value -lt 0) {
        return "(missing)"
    }

    return $Value.ToString("0.###", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-LatestRecentEntryField {
    param(
        [string]$RecentEntries,
        [Parameter(Mandatory = $true)][string]$Key,
        [string]$DefaultValue = "(missing)"
    )

    if ([string]::IsNullOrWhiteSpace($RecentEntries) -or $RecentEntries -eq "(none)") {
        return $DefaultValue
    }

    $entries = @($RecentEntries -split "~" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($entries.Count -eq 0) {
        return $DefaultValue
    }

    $lastEntry = $entries[$entries.Count - 1]
    $pattern = "(?:^|\|){0}=([^|]+)" -f [regex]::Escape($Key)
    if ($lastEntry -match $pattern) {
        return $matches[1].Trim()
    }

    return $DefaultValue
}

function Get-SenderModeCountsText {
    param(
        [int]$NormalCount,
        [int]$ReducedCount,
        [int]$CatchUpCount
    )

    return ("normal:{0},reduced:{1},catch_up:{2}" -f `
        [Math]::Max(0, $NormalCount),
        [Math]::Max(0, $ReducedCount),
        [Math]::Max(0, $CatchUpCount))
}

function Get-SenderModeTransitionText {
    param([string[]]$Modes)

    $previous = ""
    $transitions = New-Object System.Collections.Generic.List[string]
    foreach ($mode in @($Modes)) {
        if ([string]::IsNullOrWhiteSpace($mode)) {
            continue
        }

        if (-not [string]::IsNullOrWhiteSpace($previous) -and
            -not [string]::Equals($previous, $mode, [System.StringComparison]::OrdinalIgnoreCase)) {
            $transitions.Add(("{0}->{1}" -f $previous, $mode)) | Out-Null
        }

        $previous = $mode
    }

    if ($transitions.Count -eq 0) {
        return "(none)"
    }

    return ($transitions -join ",")
}

function Write-ScreenShareLowFpsCatchUpReport {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $qualityPath = Join-Path $ArtifactDir "quality-presentation-summary.txt"
    $helperQualityPath = Join-Path $ArtifactDir "helper-quality-summary.txt"
    $helperPressurePath = Join-Path $ArtifactDir "helper-pressure-summary.txt"
    $healthPath = Join-Path $ArtifactDir "health-snapshot-summary.txt"
    $promotionPath = Join-Path $ArtifactDir "reduced-promotion-summary.txt"
    $externalDeliveryPath = Join-Path $ArtifactDir "helper-external-delivery-analysis.txt"
    $externalTransportHealthPath = Join-Path $ArtifactDir "helper-external-transport-health-analysis.txt"

    $quality = Read-KeyValueReport -Path $qualityPath
    $helperQuality = Read-KeyValueReport -Path $helperQualityPath
    $helperPressure = Read-KeyValueReport -Path $helperPressurePath
    $health = Read-KeyValueReport -Path $healthPath
    $promotion = Read-KeyValueReport -Path $promotionPath
    $externalDelivery = Read-KeyValueReport -Path $externalDeliveryPath
    $externalTransportHealth = Read-KeyValueReport -Path $externalTransportHealthPath

    $qualityLines = Read-ReportLines -Path $qualityPath
    $helperQualityLines = Read-ReportLines -Path $helperQualityPath
    $helperPressureLines = Read-ReportLines -Path $helperPressurePath
    $healthLines = Read-ReportLines -Path $healthPath
    $promotionLines = Read-ReportLines -Path $promotionPath

    $activeTargetFps = Get-ReportIntValue -Values $quality -Key "active_encode_target_fps"
    if ($activeTargetFps -lt 0) {
        $activeTargetFps = [int](Get-LatestStructuredDoubleValue -Lines $qualityLines -Key "active_encode_target_fps")
    }

    $avgApplyIntervalMs = Get-ReportDoubleValue -Values $helperQuality -Key "avg_apply_interval_ms"
    if ($avgApplyIntervalMs -lt 0) {
        $avgApplyIntervalMs = Get-LatestStructuredDoubleValue -Lines $helperQualityLines -Key "avg_apply_interval_ms"
    }

    $effectiveApplyFps = -1.0
    if ($avgApplyIntervalMs -gt 0) {
        $effectiveApplyFps = 1000.0 / $avgApplyIntervalMs
    }

    $normalModeCount = Get-ReportIntValue -Values $quality -Key "normal_mode_summary_count"
    $reducedModeCount = Get-ReportIntValue -Values $quality -Key "reduced_mode_summary_count"
    $catchUpModeCount = Get-ReportIntValue -Values $quality -Key "catch_up_mode_summary_count"
    $freshnessModes = @(Get-AllStructuredFieldValues -Lines $qualityLines -Key "sender_freshness_mode")
    if ($normalModeCount -lt 0) { $normalModeCount = @($freshnessModes | Where-Object { $_ -eq "normal" }).Count }
    if ($reducedModeCount -lt 0) { $reducedModeCount = @($freshnessModes | Where-Object { $_ -eq "reduced" }).Count }
    if ($catchUpModeCount -lt 0) { $catchUpModeCount = @($freshnessModes | Where-Object { $_ -eq "catch_up" }).Count }

    $latestSenderFreshnessMode = Get-ReportStringValue -Values $quality -Key "sender_freshness_mode"
    if ($latestSenderFreshnessMode -eq "(missing)") {
        $latestSenderFreshnessMode = Get-LatestStructuredFieldValue -Lines $qualityLines -Key "sender_freshness_mode"
    }

    $latestSenderOperatingState = Get-ReportStringValue -Values $quality -Key "sender_operating_state"
    if ($latestSenderOperatingState -eq "(missing)") {
        $latestSenderOperatingState = Get-ReportStringValue -Values $health -Key "sender_operating_state"
    }

    $senderModeTransitions = Get-SenderModeTransitionText -Modes $freshnessModes
    $senderModeCounts = Get-SenderModeCountsText -NormalCount $normalModeCount -ReducedCount $reducedModeCount -CatchUpCount $catchUpModeCount

    $recentEntries = Get-ReportStringValue -Values $promotion -Key "recent_entries" -DefaultValue ""
    $latestPressure = Get-LatestRecentEntryField -RecentEntries $recentEntries -Key "pressure"
    $remotePressureMode = Get-LatestStructuredFieldValue -Lines $qualityLines -Key "remote_pressure_mode" -DefaultValue "(missing)"
    $remotePressureReason = "(missing)"
    if ($latestPressure -ne "(missing)") {
        $pressureParts = @($latestPressure -split "/", 2)
        if ($pressureParts.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($pressureParts[0])) {
            $remotePressureMode = $pressureParts[0]
        }
        if ($pressureParts.Count -gt 1 -and -not [string]::IsNullOrWhiteSpace($pressureParts[1])) {
            $remotePressureReason = $pressureParts[1]
        }
    }
    elseif ($remotePressureMode -eq "none") {
        $remotePressureReason = "healthy"
    }

    $helperPressureBlocker = Get-ReportStringValue -Values $helperPressure -Key "dominant_helper_pressure_blocker"
    if ($helperPressureBlocker -eq "(missing)") {
        $helperPressureBlocker = Get-ReportStringValue -Values $helperQuality -Key "dominant_helper_pressure_blocker"
    }

    $helperSessionPhase = Get-ReportStringValue -Values $health -Key "helper_session_phase"
    if ($helperSessionPhase -eq "(missing)") {
        $helperSessionPhase = Get-LatestStructuredFieldValue -Lines $healthLines -Key "helper_session_phase"
    }

    $helperRecoveryMechanism = Get-ReportStringValue -Values $health -Key "helper_recovery_mechanism"
    if ($helperRecoveryMechanism -eq "(missing)") {
        $helperRecoveryMechanism = Get-LatestStructuredFieldValue -Lines $healthLines -Key "helper_recovery_mechanism"
    }

    $senderGuardState = Get-ReportStringValue -Values $health -Key "sender_guard_state"
    $dominantPressureBlocker = Get-ReportStringValue -Values $health -Key "dominant_pressure_blocker"
    $dominantTroubleDomain = Get-ReportStringValue -Values $health -Key "dominant_trouble_domain"
    $steadyVisibleProgressActive = Get-ReportIntValue -Values $health -Key "steady_visible_progress_active"
    $recoveryActive = Get-ReportIntValue -Values $health -Key "recovery_active"

    $helperQualitySessionPhase = Get-ReportStringValue -Values $helperQuality -Key "helper_session_phase"
    $helperQualityRecoveryMechanism = Get-ReportStringValue -Values $helperQuality -Key "helper_recovery_mechanism"
    $visibleApplyRatio = Get-ReportDoubleValue -Values $helperQuality -Key "visible_apply_ratio"
    $gapCount = Get-ReportLongValue -Values $helperQuality -Key "gap_count"
    $resyncCount = Get-ReportLongValue -Values $helperQuality -Key "resync_count"
    $recoveryKeyframePendingVisibleApplyCount = Get-ReportLongValue -Values $helperQuality -Key "recovery_keyframe_pending_visible_apply_count"
    $dominantAdmissionRejectReason = Get-ReportStringValue -Values $helperQuality -Key "dominant_helper_admission_reject_reason" -DefaultValue "none"
    $recoveryWindowActive = Get-ReportLongValue -Values $helperQuality -Key "recovery_window_active"
    $recoveryProgressCorridorSuccessCount = Get-ReportLongValue -Values $helperQuality -Key "recovery_progress_corridor_success_count"
    $preCandidateGapTailEmittedToViewerCount = Get-ReportLongValue -Values $helperQuality -Key "pre_candidate_gap_tail_emitted_to_viewer_count"
    $actionableLateFragmentCount = Get-ReportLongValue -Values $helperQuality -Key "actionable_late_fragment_count"
    $actionableHighFrameAgeCount = Get-ReportLongValue -Values $helperPressure -Key "actionable_high_frame_age_count"
    $recoveryLockTimeMs = Get-ReportLongValue -Values $helperPressure -Key "worst_epoch_recovery_lock_time_ms"
    if ($recoveryLockTimeMs -lt 0) {
        $recoveryLockTimeMs = Get-ReportLongValue -Values $helperQuality -Key "worst_epoch_recovery_lock_time_ms"
    }

    $qualityResolvedRecovery =
        $helperQualitySessionPhase -eq "visible_stable" -and
        ($helperQualityRecoveryMechanism -eq "none" -or
            $helperQualityRecoveryMechanism -eq "(missing)" -or
            $helperQualityRecoveryMechanism -eq "(none)") -and
        $recoveryWindowActive -le 0 -and
        $preCandidateGapTailEmittedToViewerCount -le 0 -and
        $actionableLateFragmentCount -le 0 -and
        ($visibleApplyRatio -lt 0 -or $visibleApplyRatio -ge 0.98) -and
        ($recoveryProgressCorridorSuccessCount -gt 0 -or $gapCount -le 0 -or $resyncCount -le 0)
    if ($qualityResolvedRecovery) {
        $helperSessionPhase = "visible_stable"
        $helperRecoveryMechanism = "none"
        $recoveryActive = 0
        $steadyVisibleProgressActive = 1
    }

    $avgDecodeCompleteToVisibleApplyMs = Get-ReportDoubleValue -Values $helperQuality -Key "avg_decode_complete_to_visible_apply_ms"
    $avgUiPostApplyMs = Get-ReportDoubleValue -Values $helperQuality -Key "avg_ui_post_apply_ms"
    $promotionHelperPressureTicks = Get-ReportLongValue -Values $promotion -Key "promotion_blocker_helper_pressure_ticks"
    $promotionRecoveryLockTicks = Get-ReportLongValue -Values $promotion -Key "promotion_blocker_recovery_lock_ticks"
    $promotionCaptureAgeTicks = Get-ReportLongValue -Values $promotion -Key "promotion_blocker_capture_age_ticks"
    $promotionEncodeBudgetTicks = Get-ReportLongValue -Values $promotion -Key "promotion_blocker_encode_budget_ticks"
    $promotionTransitionGraceTicks = Get-ReportLongValue -Values $promotion -Key "promotion_blocker_transition_grace_ticks"
    $promotionEncodeSoftSpikeCount = Get-ReportLongValue -Values $promotion -Key "promotion_encode_soft_spike_count"
    $blockedByEncodeBudget = Get-ReportLongValue -Values $promotion -Key "blocked_by_encode_budget"
    $blockedByEncodeBudgetAlone = Get-ReportLongValue -Values $promotion -Key "blocked_by_encode_budget_alone"
    $healthyTickResetReasonCounts = Get-ReportStringValue -Values $promotion -Key "healthy_tick_reset_reason_counts" -DefaultValue "(none)"
    $postReceiptBlockerSuppressedCount = Get-ReportLongValue -Values $promotion -Key "post_receipt_blocker_suppressed_count"
    $lastPostReceiptBlockerSuppressedSet = Get-ReportStringValue -Values $promotion -Key "last_post_receipt_blocker_suppressed_set" -DefaultValue "(none)"

    $externalDeliveryClassification = Get-ReportStringValue -Values $externalDelivery -Key "classification"
    $externalTransportHealthClassification = Get-ReportStringValue -Values $externalTransportHealth -Key "classification"
    $deepestExternalDeliveryClassification = $externalDeliveryClassification
    if ($externalTransportHealthClassification -ne "(missing)") {
        $deepestExternalDeliveryClassification = $externalTransportHealthClassification
    }

    $networkResidualMs = Get-ReportLongValue -Values $externalDelivery -Key "candidate_network_delivery_residual_ms"
    $localSenderDeltaMs = Get-ReportLongValue -Values $externalDelivery -Key "candidate_local_sender_delta_ms"
    $queueDepth = Get-ReportLongValue -Values $externalDelivery -Key "candidate_queue_depth"
    $queueDrops = Get-ReportLongValue -Values $externalDelivery -Key "candidate_queue_drops"
    $sendFailures = Get-ReportLongValue -Values $externalDelivery -Key "candidate_send_failures"

    $hasLowSenderModeEvidence = ($reducedModeCount + $catchUpModeCount) -gt 0 -or
        $latestSenderFreshnessMode -eq "reduced" -or
        $latestSenderFreshnessMode -eq "catch_up" -or
        $latestSenderOperatingState -eq "reduced" -or
        $latestSenderOperatingState -eq "catch_up"
    $activeTargetIsLow = $activeTargetFps -gt 0 -and $activeTargetFps -lt 8
    $effectiveApplyIsLow = $activeTargetFps -gt 0 -and $effectiveApplyFps -gt 0 -and $effectiveApplyFps -lt ($activeTargetFps * 0.8)
    $visibleApplyRatioLow = $visibleApplyRatio -ge 0 -and $visibleApplyRatio -lt 0.98
    $localSenderDeltaClean = $localSenderDeltaMs -ge 0 -and $localSenderDeltaMs -le 10
    $localQueueClean = $localSenderDeltaClean -and $queueDepth -eq 0 -and $queueDrops -eq 0 -and $sendFailures -eq 0
    $helperVisibleStable = $helperSessionPhase -eq "visible_stable" -or $steadyVisibleProgressActive -eq 1
    $decodeAndUiCheap = ($avgDecodeCompleteToVisibleApplyMs -lt 0 -or $avgDecodeCompleteToVisibleApplyMs -le 10) -and
        ($avgUiPostApplyMs -lt 0 -or $avgUiPostApplyMs -le 10)
    $activeHelperRecoveryEvidence =
        $recoveryActive -eq 1 -or
        $helperRecoveryMechanism -eq "waiting_for_recovery_keyframe" -or
        $helperRecoveryMechanism -eq "recovery_corridor"
    $activeAdmissionRejectEvidence =
        $dominantAdmissionRejectReason -eq "waiting_for_recovery_keyframe" -and
        ($activeHelperRecoveryEvidence -or -not $helperVisibleStable)
    $activeHelperRecoveryEvidence = $activeHelperRecoveryEvidence -or $activeAdmissionRejectEvidence
    $activePendingVisibleRecovery =
        $recoveryKeyframePendingVisibleApplyCount -gt 0 -and
        $activeHelperRecoveryEvidence
    $staleContinuityOnlyVisibleStable =
        $helperVisibleStable -and
        $recoveryActive -ne 1 -and
        -not $activePendingVisibleRecovery -and
        ($helperRecoveryMechanism -eq "none" -or $helperRecoveryMechanism -eq "(missing)") -and
        -not $activeAdmissionRejectEvidence -and
        ($helperPressureBlocker -eq "none" -or $helperPressureBlocker -eq "(missing)") -and
        $actionableHighFrameAgeCount -le 0 -and
        $localQueueClean -and
        $remotePressureMode -eq "none" -and
        $remotePressureReason -eq "continuity_loss"
    $helperVisibilityResolved =
        ($helperVisibleStable -and
         $recoveryActive -ne 1 -and
         -not $activePendingVisibleRecovery -and
         -not $visibleApplyRatioLow -and
         ($helperRecoveryMechanism -eq "none" -or $helperRecoveryMechanism -eq "(missing)") -and
         -not $activeAdmissionRejectEvidence) -or
        $staleContinuityOnlyVisibleStable
    $historicalRecoveryOnly =
        $helperVisibilityResolved -and
        ($gapCount -gt 0 -or $resyncCount -gt 0 -or $recoveryLockTimeMs -gt 0)
    $helperRecoveryOrVisibilityEvidence =
        $activeHelperRecoveryEvidence -or
        $activePendingVisibleRecovery -or
        ($visibleApplyRatioLow -and -not $staleContinuityOnlyVisibleStable) -or
        ((($recoveryLockTimeMs -gt 0) -or ($gapCount -gt 0) -or ($resyncCount -gt 0)) -and -not $historicalRecoveryOnly)
    $externalDeliveryEvidence =
        $networkResidualMs -gt 0 -and
        $localQueueClean -and
        ($hasLowSenderModeEvidence -or $activeTargetIsLow -or $effectiveApplyIsLow -or ($visibleApplyRatioLow -and $staleContinuityOnlyVisibleStable)) -and
        -not $activeHelperRecoveryEvidence -and
        -not $activePendingVisibleRecovery -and
        ($remotePressureMode -eq "reduce_fps" -or
            $remotePressureMode -eq "catch_up_only" -or
            $remotePressureReason -eq "high_frame_age" -or
            $remotePressureReason -eq "slow_apply_cadence" -or
            $remotePressureReason -eq "continuity_loss" -or
            $remotePressureReason -eq "bridge_health" -or
            $helperVisibilityResolved -or
            ($helperVisibleStable -and $deepestExternalDeliveryClassification -eq "steady_external_delivery_latency")) -and
        ($actionableHighFrameAgeCount -le 0 -or $helperVisibilityResolved)
    $senderBudgetEvidence =
        $localQueueClean -and
        ($promotionCaptureAgeTicks -gt 0 -or
         $promotionEncodeBudgetTicks -gt 0 -or
         $blockedByEncodeBudget -gt 0 -or
         $blockedByEncodeBudgetAlone -gt 0 -or
         $promotionEncodeSoftSpikeCount -gt 0)
    $policyHysteresisEvidence =
        $hasLowSenderModeEvidence -and
        ($remotePressureMode -eq "none" -or $remotePressureReason -eq "healthy") -and
        ($promotionRecoveryLockTicks -gt 0 -or
         $promotionTransitionGraceTicks -gt 0 -or
         $healthyTickResetReasonCounts -ne "(none)" -or
         $postReceiptBlockerSuppressedCount -gt 0)

    $classification = "no_low_fps_catch_up_evidence"
    $primaryBlocker = "none"
    $nextAction = "Keep the current presentation and transport evidence parked; no low-FPS/catch-up follow-up is indicated by this artifact."

    if ($externalDeliveryEvidence) {
        $classification = "external_delivery_driven_catch_up"
        $primaryBlocker = "external_delivery"
        $nextAction = "Fold this result back into the external topology or delivery lane; do not tune sender logic from this artifact alone."
    }
    elseif ($helperRecoveryOrVisibilityEvidence) {
        $classification = "helper_recovery_or_visibility_catch_up"
        $primaryBlocker = "helper_recovery_or_visibility"
        $nextAction = "Only reopen the helper recovery, visible-proof, or visible-apply mechanism named by this summary."
    }
    elseif ($helperVisibleStable -and $decodeAndUiCheap -and $effectiveApplyIsLow) {
        $classification = "helper_apply_cadence_limited"
        $primaryBlocker = "helper_apply_cadence"
        $nextAction = "Investigate helper apply cadence and viewer presentation scheduling before sender tuning."
    }
    elseif ($senderBudgetEvidence) {
        $classification = "sender_capture_or_encode_budget_limited"
        $primaryBlocker = "sender_capture_or_encode_budget"
        $nextAction = "Open a capture/encode budget lane; do not change transport."
    }
    elseif ($policyHysteresisEvidence) {
        $classification = "sender_policy_hysteresis"
        $primaryBlocker = "sender_policy_hysteresis"
        $nextAction = "Add focused ScreenShareSenderAutoTuneEvaluator tests before changing thresholds."
    }
    elseif ($hasLowSenderModeEvidence -or $activeTargetIsLow -or $effectiveApplyIsLow) {
        $classification = "helper_apply_cadence_limited"
        $primaryBlocker = "unclassified_low_fps_or_mode_reduction"
        $nextAction = "Keep this in the low-FPS lane and add narrower evidence before runtime tuning."
    }

    $reportLines = @(
        ("classification={0}" -f $classification),
        ("primary_blocker={0}" -f $primaryBlocker),
        ("next_action={0}" -f $nextAction),
        ("active_target_fps={0}" -f $(if ($activeTargetFps -lt 0) { "(missing)" } else { $activeTargetFps })),
        ("avg_apply_interval_ms={0}" -f (Format-ScreenShareDouble -Value $avgApplyIntervalMs)),
        ("effective_apply_fps={0}" -f (Format-ScreenShareDouble -Value $effectiveApplyFps)),
        ("sender_mode_counts={0}" -f $senderModeCounts),
        ("sender_mode_transitions={0}" -f $senderModeTransitions),
        ("latest_sender_freshness_mode={0}" -f $latestSenderFreshnessMode),
        ("latest_sender_operating_state={0}" -f $latestSenderOperatingState),
        ("remote_pressure_mode={0}" -f $remotePressureMode),
        ("remote_pressure_reason={0}" -f $remotePressureReason),
        ("helper_pressure_blocker={0}" -f $helperPressureBlocker),
        ("helper_session_phase={0}" -f $helperSessionPhase),
        ("helper_recovery_mechanism={0}" -f $helperRecoveryMechanism),
        ("sender_guard_state={0}" -f $senderGuardState),
        ("dominant_pressure_blocker={0}" -f $dominantPressureBlocker),
        ("dominant_trouble_domain={0}" -f $dominantTroubleDomain),
        ("visible_apply_ratio={0}" -f (Format-ScreenShareDouble -Value $visibleApplyRatio)),
        ("gap_count={0}" -f $(if ($gapCount -lt 0) { "(missing)" } else { $gapCount })),
        ("resync_count={0}" -f $(if ($resyncCount -lt 0) { "(missing)" } else { $resyncCount })),
        ("recovery_lock_time_ms={0}" -f $(if ($recoveryLockTimeMs -lt 0) { "(missing)" } else { $recoveryLockTimeMs })),
        ("actionable_high_frame_age_count={0}" -f $(if ($actionableHighFrameAgeCount -lt 0) { "(missing)" } else { $actionableHighFrameAgeCount })),
        ("avg_decode_complete_to_visible_apply_ms={0}" -f (Format-ScreenShareDouble -Value $avgDecodeCompleteToVisibleApplyMs)),
        ("avg_ui_post_apply_ms={0}" -f (Format-ScreenShareDouble -Value $avgUiPostApplyMs)),
        ("promotion_blocker_helper_pressure_ticks={0}" -f $(if ($promotionHelperPressureTicks -lt 0) { "(missing)" } else { $promotionHelperPressureTicks })),
        ("promotion_blocker_recovery_lock_ticks={0}" -f $(if ($promotionRecoveryLockTicks -lt 0) { "(missing)" } else { $promotionRecoveryLockTicks })),
        ("promotion_blocker_capture_age_ticks={0}" -f $(if ($promotionCaptureAgeTicks -lt 0) { "(missing)" } else { $promotionCaptureAgeTicks })),
        ("promotion_blocker_encode_budget_ticks={0}" -f $(if ($promotionEncodeBudgetTicks -lt 0) { "(missing)" } else { $promotionEncodeBudgetTicks })),
        ("promotion_encode_soft_spike_count={0}" -f $(if ($promotionEncodeSoftSpikeCount -lt 0) { "(missing)" } else { $promotionEncodeSoftSpikeCount })),
        ("blocked_by_encode_budget={0}" -f $(if ($blockedByEncodeBudget -lt 0) { "(missing)" } else { $blockedByEncodeBudget })),
        ("blocked_by_encode_budget_alone={0}" -f $(if ($blockedByEncodeBudgetAlone -lt 0) { "(missing)" } else { $blockedByEncodeBudgetAlone })),
        ("healthy_tick_reset_reason_counts={0}" -f $healthyTickResetReasonCounts),
        ("post_receipt_blocker_suppressed_count={0}" -f $(if ($postReceiptBlockerSuppressedCount -lt 0) { "(missing)" } else { $postReceiptBlockerSuppressedCount })),
        ("last_post_receipt_blocker_suppressed_set={0}" -f $lastPostReceiptBlockerSuppressedSet),
        ("external_delivery_classification={0}" -f $externalDeliveryClassification),
        ("external_transport_health_classification={0}" -f $externalTransportHealthClassification),
        ("deepest_external_delivery_classification={0}" -f $deepestExternalDeliveryClassification),
        ("candidate_network_delivery_residual_ms={0}" -f $(if ($networkResidualMs -lt 0) { "(missing)" } else { $networkResidualMs })),
        ("candidate_local_sender_delta_ms={0}" -f $(if ($localSenderDeltaMs -lt 0) { "(missing)" } else { $localSenderDeltaMs })),
        ("candidate_queue_depth={0}" -f $(if ($queueDepth -lt 0) { "(missing)" } else { $queueDepth })),
        ("candidate_queue_drops={0}" -f $(if ($queueDrops -lt 0) { "(missing)" } else { $queueDrops })),
        ("candidate_send_failures={0}" -f $(if ($sendFailures -lt 0) { "(missing)" } else { $sendFailures }))
    )

    $reportPath = Join-Path $ArtifactDir "low-fps-catch-up-summary.txt"
    Set-Content -LiteralPath $reportPath -Value $reportLines
    Write-Host ("[ScreenShareOps] wrote {0}" -f $reportPath) -ForegroundColor Cyan

    return $reportPath
}

function Get-ScreenShareExternalTopologyProfile {
    param([AllowNull()][System.Collections.IDictionary]$BridgeTransportHealth)

    $profile = Get-ReportStringValue -Values $BridgeTransportHealth -Key "external_topology_profile" -DefaultValue "Default"
    if ([string]::IsNullOrWhiteSpace($profile) -or $profile -eq "(missing)") {
        return "Default"
    }

    return $profile
}

function Write-ScreenShareExternalTopologyReport {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $bridgeTransportPath = Join-Path $ArtifactDir "bridge-transport-health-summary.txt"
    $externalDeliveryPath = Join-Path $ArtifactDir "helper-external-delivery-analysis.txt"
    $externalTransportHealthPath = Join-Path $ArtifactDir "helper-external-transport-health-analysis.txt"
    $socketReceivePath = Join-Path $ArtifactDir "helper-socket-receive-analysis.txt"
    $lowFpsPath = Join-Path $ArtifactDir "low-fps-catch-up-summary.txt"

    $bridgeTransport = Read-KeyValueReport -Path $bridgeTransportPath
    $externalDelivery = Read-KeyValueReport -Path $externalDeliveryPath
    $externalTransportHealth = Read-KeyValueReport -Path $externalTransportHealthPath
    $socketReceive = Read-KeyValueReport -Path $socketReceivePath
    $lowFps = Read-KeyValueReport -Path $lowFpsPath

    $profile = Get-ScreenShareExternalTopologyProfile -BridgeTransportHealth $bridgeTransport
    $selectedRpc = Get-ReportStringValue -Values $bridgeTransport -Key "selected_rpc" -DefaultValue "(missing)"
    $selectedRpcKey = Get-ReportStringValue -Values $bridgeTransport -Key "selected_rpc_key" -DefaultValue "(missing)"
    $selectedRpcStage = Get-ReportStringValue -Values $bridgeTransport -Key "selected_rpc_stage" -DefaultValue "(missing)"
    $controlSubClients = Get-ReportIntValue -Values $bridgeTransport -Key "control_subclients"
    $mediaSubClients = Get-ReportIntValue -Values $bridgeTransport -Key "media_subclients"
    $bulkSubClients = Get-ReportIntValue -Values $bridgeTransport -Key "bulk_subclients"
    if ($controlSubClients -lt 0) { $controlSubClients = 4 }
    if ($mediaSubClients -lt 0) { $mediaSubClients = 4 }
    if ($bulkSubClients -lt 0) { $bulkSubClients = 4 }

    $disconnectCount = Get-ReportIntValue -Values $bridgeTransport -Key "disconnect_count_since_last" -DefaultValue 0
    $connectFailedCount = Get-ReportIntValue -Values $bridgeTransport -Key "connect_failed_count_since_last" -DefaultValue 0
    $wsErrorCount = Get-ReportIntValue -Values $bridgeTransport -Key "ws_error_count_since_last" -DefaultValue 0
    $rpcFallbackCount = Get-ReportIntValue -Values $bridgeTransport -Key "rpc_fallback_attempt_count_since_last" -DefaultValue 0
    $uniqueSelectedRpcCount = Get-ReportIntValue -Values $bridgeTransport -Key "unique_selected_rpc_count" -DefaultValue 0

    $externalDeliveryClassification = Get-ReportStringValue -Values $externalDelivery -Key "classification" -DefaultValue "(missing)"
    $externalTransportHealthClassification = Get-ReportStringValue -Values $externalTransportHealth -Key "classification" -DefaultValue "(missing)"
    $socketReceiveClassification = Get-ReportStringValue -Values $socketReceive -Key "classification" -DefaultValue "(missing)"
    $lowFpsClassification = Get-ReportStringValue -Values $lowFps -Key "classification" -DefaultValue "(missing)"
    $effectiveApplyFps = Get-ReportStringValue -Values $lowFps -Key "effective_apply_fps" -DefaultValue "(missing)"
    $senderModeCounts = Get-ReportStringValue -Values $lowFps -Key "sender_mode_counts" -DefaultValue "(missing)"

    $socketMedianMs = Get-ReportIntValue -Values $socketReceive -Key "candidate_envelope_send_to_socket_data_event_emitted_median_ms"
    if ($socketMedianMs -lt 0) {
        $socketMedianMs = Get-ReportIntValue -Values $externalDelivery -Key "candidate_envelope_send_to_socket_data_event_emitted_median_ms"
    }

    $socketP95Ms = Get-ReportIntValue -Values $socketReceive -Key "candidate_envelope_send_to_socket_data_event_emitted_p95_ms"
    $networkResidualMs = Get-ReportIntValue -Values $externalDelivery -Key "candidate_network_delivery_residual_ms"
    $localSenderDeltaMs = Get-ReportIntValue -Values $externalDelivery -Key "candidate_local_sender_delta_ms"
    $queueDepth = Get-ReportIntValue -Values $externalDelivery -Key "candidate_queue_depth" -DefaultValue 0
    $queueDrops = Get-ReportIntValue -Values $externalDelivery -Key "candidate_queue_drops" -DefaultValue 0
    $sendFailures = Get-ReportIntValue -Values $externalDelivery -Key "candidate_send_failures" -DefaultValue 0

    $classification = "no_topology_evidence"
    $nextAction = "Collect a complete retained external topology artifact before changing NKN topology."
    if ($queueDepth -gt 0 -or $queueDrops -gt 0 -or $sendFailures -gt 0) {
        $classification = "local_queue_regression"
        $nextAction = "Stop topology comparison and reopen only the implicated local bridge queue or send path."
    }
    elseif ($disconnectCount -gt 0 -or $connectFailedCount -gt 0 -or $wsErrorCount -gt 0 -or $rpcFallbackCount -gt 0 -or $uniqueSelectedRpcCount -gt 1) {
        $classification = "transport_health_churn"
        $nextAction = "Compare this profile as a transport-health event run, not as steady external delivery."
    }
    elseif ($externalTransportHealthClassification -eq "steady_external_delivery_latency" -or
        $externalDeliveryClassification -eq "network_delivery_latency" -or
        $socketReceiveClassification -eq "external_receive_latency") {
        $classification = "external_delivery_candidate"
        $nextAction = "Use this artifact in the external topology A/B matrix; keep sender and helper runtime parked."
    }

    $reportLines = @(
        ("external_topology_profile={0}" -f $profile),
        ("external_topology_classification={0}" -f $classification),
        ("external_topology_next_action={0}" -f $nextAction),
        ("selected_rpc={0}" -f $selectedRpc),
        ("selected_rpc_key={0}" -f $selectedRpcKey),
        ("selected_rpc_stage={0}" -f $selectedRpcStage),
        ("control_subclients={0}" -f $controlSubClients),
        ("media_subclients={0}" -f $mediaSubClients),
        ("bulk_subclients={0}" -f $bulkSubClients),
        ("socket_receive_median_ms={0}" -f $(if ($socketMedianMs -lt 0) { "(missing)" } else { $socketMedianMs })),
        ("socket_receive_p95_ms={0}" -f $(if ($socketP95Ms -lt 0) { "(missing)" } else { $socketP95Ms })),
        ("network_delivery_residual_ms={0}" -f $(if ($networkResidualMs -lt 0) { "(missing)" } else { $networkResidualMs })),
        ("local_sender_delta_ms={0}" -f $(if ($localSenderDeltaMs -lt 0) { "(missing)" } else { $localSenderDeltaMs })),
        ("queue_depth={0}" -f $queueDepth),
        ("queue_drops={0}" -f $queueDrops),
        ("send_failures={0}" -f $sendFailures),
        ("disconnect_count_since_last={0}" -f $disconnectCount),
        ("connect_failed_count_since_last={0}" -f $connectFailedCount),
        ("ws_error_count_since_last={0}" -f $wsErrorCount),
        ("rpc_fallback_attempt_count_since_last={0}" -f $rpcFallbackCount),
        ("unique_selected_rpc_count={0}" -f $uniqueSelectedRpcCount),
        ("external_delivery_classification={0}" -f $externalDeliveryClassification),
        ("external_transport_health_classification={0}" -f $externalTransportHealthClassification),
        ("socket_receive_classification={0}" -f $socketReceiveClassification),
        ("low_fps_catch_up_classification={0}" -f $lowFpsClassification),
        ("effective_apply_fps={0}" -f $effectiveApplyFps),
        ("sender_mode_counts={0}" -f $senderModeCounts)
    )

    $reportPath = Join-Path $ArtifactDir "external-topology-summary.txt"
    Set-Content -LiteralPath $reportPath -Value $reportLines
    Write-Host ("[ScreenShareOps] wrote {0}" -f $reportPath) -ForegroundColor Cyan

    return $reportPath
}

function Get-TopologyAuditAverage {
    param([double[]]$Values)

    $usableValues = @($Values | Where-Object { $_ -ge 0 })
    if ($usableValues.Count -eq 0) {
        return -1.0
    }

    return ($usableValues | Measure-Object -Average).Average
}

function Read-ScreenShareExternalTopologyRow {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $summaryPath = Join-Path $ArtifactDir "external-topology-summary.txt"
    if (-not (Test-Path -LiteralPath $summaryPath)) {
        Write-ScreenShareExternalTopologyReport -ArtifactDir $ArtifactDir | Out-Null
    }

    $summary = Read-KeyValueReport -Path $summaryPath
    return [pscustomobject]@{
        ArtifactDir = $ArtifactDir
        Artifact = Split-Path -Leaf $ArtifactDir
        Profile = Get-ReportStringValue -Values $summary -Key "external_topology_profile" -DefaultValue "Default"
        Classification = Get-ReportStringValue -Values $summary -Key "external_topology_classification" -DefaultValue "no_topology_evidence"
        SelectedRpcKey = Get-ReportStringValue -Values $summary -Key "selected_rpc_key" -DefaultValue "(missing)"
        SelectedRpcStage = Get-ReportStringValue -Values $summary -Key "selected_rpc_stage" -DefaultValue "(missing)"
        MediaSubClients = Get-ReportIntValue -Values $summary -Key "media_subclients" -DefaultValue 4
        SocketMedianMs = Get-ReportIntValue -Values $summary -Key "socket_receive_median_ms"
        SocketP95Ms = Get-ReportIntValue -Values $summary -Key "socket_receive_p95_ms"
        LocalSenderDeltaMs = Get-ReportIntValue -Values $summary -Key "local_sender_delta_ms"
        QueueDepth = Get-ReportIntValue -Values $summary -Key "queue_depth" -DefaultValue 0
        QueueDrops = Get-ReportIntValue -Values $summary -Key "queue_drops" -DefaultValue 0
        SendFailures = Get-ReportIntValue -Values $summary -Key "send_failures" -DefaultValue 0
        LowFpsClassification = Get-ReportStringValue -Values $summary -Key "low_fps_catch_up_classification" -DefaultValue "(missing)"
        EffectiveApplyFps = Get-ReportStringValue -Values $summary -Key "effective_apply_fps" -DefaultValue "(missing)"
        SenderModeCounts = Get-ReportStringValue -Values $summary -Key "sender_mode_counts" -DefaultValue "(missing)"
    }
}

function Write-ScreenShareExternalTopologyComparison {
    param(
        [Parameter(Mandatory = $true)][string[]]$ArtifactDirs,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    $rows = @($ArtifactDirs | ForEach-Object { Read-ScreenShareExternalTopologyRow -ArtifactDir $_ })
    $baselineRows = @($rows | Where-Object { $_.Profile -eq "Default" })
    if ($baselineRows.Count -eq 0 -and $rows.Count -gt 0) {
        $baselineRows = @($rows | Select-Object -First 1)
    }

    $baselineMedian = Get-TopologyAuditAverage -Values ([double[]]@($baselineRows | ForEach-Object { [double]$_.SocketMedianMs }))
    $baselineP95 = Get-TopologyAuditAverage -Values ([double[]]@($baselineRows | ForEach-Object { [double]$_.SocketP95Ms }))
    $localRegressionRows = @($rows | Where-Object { $_.Classification -eq "local_queue_regression" })

    $auditClassification = "insufficient_topology_matrix"
    $winnerProfile = "(none)"
    $winnerReason = "Need at least one Default run plus candidate profile runs."

    if ($localRegressionRows.Count -gt 0) {
        $auditClassification = "local_queue_regression"
        $winnerReason = "At least one profile produced local queue/drop/send-failure evidence."
    }
    elseif ($rows.Count -gt 1 -and $baselineMedian -ge 0 -and $baselineP95 -ge 0) {
        $candidateGroups = @(
            $rows |
                Where-Object { $_.Profile -ne "Default" } |
                Group-Object Profile
        )
        $winnerCandidates = New-Object System.Collections.Generic.List[object]
        $regressionCandidates = New-Object System.Collections.Generic.List[object]

        foreach ($group in $candidateGroups) {
            $profileRows = @($group.Group)
            $improvedRuns = @(
                $profileRows |
                    Where-Object {
                        $_.Classification -ne "local_queue_regression" -and
                        $_.SocketMedianMs -ge 0 -and
                        $_.SocketP95Ms -ge 0 -and
                        $_.SocketMedianMs -le ($baselineMedian * 0.70) -and
                        ($_.SocketP95Ms -le ($baselineP95 * 0.75) -or ($baselineP95 -ge 2000 -and $_.SocketP95Ms -lt $baselineP95))
                    }
            )
            $regressedRuns = @(
                $profileRows |
                    Where-Object {
                        $_.SocketMedianMs -gt ($baselineMedian * 1.25) -or
                        $_.SocketP95Ms -gt ($baselineP95 * 1.25)
                    }
            )

            if ($improvedRuns.Count -ge 2) {
                $winnerCandidates.Add([pscustomobject]@{
                    Profile = [string]$group.Name
                    ImprovedRuns = $improvedRuns.Count
                    AverageMedian = Get-TopologyAuditAverage -Values ([double[]]@($profileRows | ForEach-Object { [double]$_.SocketMedianMs }))
                }) | Out-Null
            }
            elseif ($regressedRuns.Count -ge 2) {
                $regressionCandidates.Add([pscustomobject]@{
                    Profile = [string]$group.Name
                    RegressedRuns = $regressedRuns.Count
                }) | Out-Null
            }
        }

        if ($winnerCandidates.Count -gt 0) {
            $winner = @($winnerCandidates | Sort-Object AverageMedian, Profile | Select-Object -First 1)[0]
            $auditClassification = "winner"
            $winnerProfile = $winner.Profile
            $winnerReason = ("{0} improved median/p95 in {1} run(s) versus Default." -f $winner.Profile, $winner.ImprovedRuns)
        }
        elseif ($regressionCandidates.Count -gt 0) {
            $auditClassification = "regression"
            $winnerReason = "At least one profile worsened socket receive median or p95 in repeated runs."
        }
        else {
            $auditClassification = "no_change"
            $winnerReason = "No candidate met the 30% median and 25% p95 improvement gate."
        }
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(("audit_classification={0}" -f $auditClassification)) | Out-Null
    $lines.Add(("winner_profile={0}" -f $winnerProfile)) | Out-Null
    $lines.Add(("winner_reason={0}" -f $winnerReason)) | Out-Null
    $lines.Add(("baseline_profile={0}" -f $(if ($baselineRows.Count -gt 0) { $baselineRows[0].Profile } else { "(missing)" }))) | Out-Null
    $lines.Add(("baseline_socket_receive_median_ms={0}" -f $(if ($baselineMedian -lt 0) { "(missing)" } else { [Math]::Round($baselineMedian, 3).ToString([System.Globalization.CultureInfo]::InvariantCulture) }))) | Out-Null
    $lines.Add(("baseline_socket_receive_p95_ms={0}" -f $(if ($baselineP95 -lt 0) { "(missing)" } else { [Math]::Round($baselineP95, 3).ToString([System.Globalization.CultureInfo]::InvariantCulture) }))) | Out-Null
    $lines.Add(("artifact_count={0}" -f $rows.Count)) | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("artifact|profile|classification|selected_rpc_key|selected_rpc_stage|media_subclients|socket_median_ms|socket_p95_ms|local_sender_delta_ms|queue_depth|queue_drops|send_failures|low_fps_classification|effective_apply_fps|sender_mode_counts") | Out-Null
    foreach ($row in $rows) {
        $lines.Add(("{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11}|{12}|{13}|{14}" -f `
            $row.Artifact,
            $row.Profile,
            $row.Classification,
            $row.SelectedRpcKey,
            $row.SelectedRpcStage,
            $row.MediaSubClients,
            $row.SocketMedianMs,
            $row.SocketP95Ms,
            $row.LocalSenderDeltaMs,
            $row.QueueDepth,
            $row.QueueDrops,
            $row.SendFailures,
            $row.LowFpsClassification,
            $row.EffectiveApplyFps,
            $row.SenderModeCounts)) | Out-Null
    }

    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }

    Set-Content -LiteralPath $OutputPath -Value $lines
    Write-Host ("[ScreenShareOps] wrote {0}" -f $OutputPath) -ForegroundColor Cyan
    return $OutputPath
}

function Add-MissingVerdictInput {
    param(
        [Parameter(Mandatory = $true)]$MissingInputs,
        [Parameter(Mandatory = $true)][string]$FileName,
        [string]$Key = ""
    )

    if ([string]::IsNullOrWhiteSpace($Key)) {
        $MissingInputs.Add($FileName) | Out-Null
        return
    }

    $MissingInputs.Add(("{0}:{1}" -f $FileName, $Key)) | Out-Null
}

function Get-ScreenShareNoSessionEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [AllowNull()][System.Collections.IDictionary]$Transport,
        [AllowNull()][System.Collections.IDictionary]$Quality
    )

    $helperQuality = Read-KeyValueReport -Path (Join-Path $ArtifactDir "helper-quality-summary.txt")
    $helperPressure = Read-KeyValueReport -Path (Join-Path $ArtifactDir "helper-pressure-summary.txt")
    $health = Read-KeyValueReport -Path (Join-Path $ArtifactDir "health-snapshot-summary.txt")
    $bridgeMedia = Read-KeyValueReport -Path (Join-Path $ArtifactDir "bridge-media-send-summary.txt")
    $hasExplicitNoSessionEvidenceSurface = $null -ne $helperQuality -and
        $null -ne $helperPressure -and
        $null -ne $health -and
        $null -ne $bridgeMedia

    $effectiveQuality = if ($null -ne $Quality) { $Quality } else { $helperQuality }
    $helperApplyMsAvg = Get-ReportDoubleValue -Values $helperQuality -Key "helper_apply_ms_avg"
    $visibleApplyRatio = Get-ReportDoubleValue -Values $helperQuality -Key "visible_apply_ratio"
    $qualityBaselineEstablished = Get-ReportIntValue -Values $helperQuality -Key "baseline_established"
    $pressureBaselineEstablished = Get-ReportIntValue -Values $helperPressure -Key "baseline_established"
    $framesSent = Get-ReportIntValue -Values $bridgeMedia -Key "frames_sent"
    $mediaPlaneFramesSent = Get-ReportIntValue -Values $Transport -Key "media_plane_frames_sent"
    $activeTargetFps = Get-ReportIntValue -Values $effectiveQuality -Key "active_encode_target_fps"
    $helperSessionPhase = Get-ReportStringValue -Values $health -Key "helper_session_phase" -DefaultValue "(missing)"

    $noFrameSendEvidence = $framesSent -le 0 -and $mediaPlaneFramesSent -le 0
    $noHelperApplyEvidence = $helperApplyMsAvg -lt 0 -and $visibleApplyRatio -lt 0
    $noVisibleBaseline = $qualityBaselineEstablished -le 0 -and $pressureBaselineEstablished -le 0
    $phaseIsNoVisibleBaseline =
        [string]::Equals($helperSessionPhase, "no_visible_baseline", [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($helperSessionPhase, "(missing)", [System.StringComparison]::OrdinalIgnoreCase)

    $reasons = New-Object System.Collections.Generic.List[string]
    if ($noFrameSendEvidence) { $reasons.Add("no_frames_sent") | Out-Null }
    if ($noHelperApplyEvidence) { $reasons.Add("no_helper_apply_samples") | Out-Null }
    if ($noVisibleBaseline) { $reasons.Add("no_visible_baseline") | Out-Null }
    if ($phaseIsNoVisibleBaseline) { $reasons.Add(("helper_session_phase={0}" -f $helperSessionPhase)) | Out-Null }

    return [pscustomobject]@{
        IsNoSession = $hasExplicitNoSessionEvidenceSurface -and $noFrameSendEvidence -and $noHelperApplyEvidence -and $noVisibleBaseline -and $phaseIsNoVisibleBaseline
        Reason = if ($reasons.Count -eq 0) { "(none)" } else { $reasons -join "," }
        FramesSent = $framesSent
        MediaPlaneFramesSent = $mediaPlaneFramesSent
        HelperApplyMsAvg = $helperApplyMsAvg
        VisibleApplyRatio = $visibleApplyRatio
        BaselineEstablished = [Math]::Max($qualityBaselineEstablished, $pressureBaselineEstablished)
        HelperSessionPhase = $helperSessionPhase
        ActiveTargetFps = $activeTargetFps
    }
}

function Write-ScreenShareOperatorVerdictReport {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $missingInputs = New-Object System.Collections.Generic.List[string]

    $stabilityFile = "stability-gates-summary.txt"
    $latencyFile = "latency-regression-analysis.txt"
    $transportFile = "transport-mode-summary.txt"
    $recoveryFile = "recovery-burst-summary.txt"
    $qualityFile = "quality-presentation-summary.txt"
    $lowFpsFile = "low-fps-catch-up-summary.txt"
    $externalTopologyFile = "external-topology-summary.txt"

    $stability = Read-KeyValueReport -Path (Join-Path $ArtifactDir $stabilityFile)
    $latency = Read-KeyValueReport -Path (Join-Path $ArtifactDir $latencyFile)
    $transport = Read-KeyValueReport -Path (Join-Path $ArtifactDir $transportFile)
    $recovery = Read-KeyValueReport -Path (Join-Path $ArtifactDir $recoveryFile)
    $quality = Read-KeyValueReport -Path (Join-Path $ArtifactDir $qualityFile)
    $lowFps = Read-KeyValueReport -Path (Join-Path $ArtifactDir $lowFpsFile)
    $externalTopology = Read-KeyValueReport -Path (Join-Path $ArtifactDir $externalTopologyFile)
    $noSessionEvidence = Get-ScreenShareNoSessionEvidence -ArtifactDir $ArtifactDir -Transport $transport -Quality $quality

    if ($null -eq $stability) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $stabilityFile }
    if ($null -eq $latency) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $latencyFile }
    if ($null -eq $transport) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $transportFile }
    if ($null -eq $recovery) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $recoveryFile }

    $behaviorFirstGateStatus = Get-KeyValue -Values $stability -Key "behavior_first_gate_status"
    $regressionClassification = Get-KeyValue -Values $latency -Key "regression_classification"
    $effectiveMediaPlaneActive = Get-KeyValue -Values $transport -Key "effective_media_plane_active"
    $steadyStateUsedControlFallback = Get-KeyValue -Values $transport -Key "steady_state_used_control_fallback"
    $recoveryCompletionAccountingMismatch = Get-KeyValue -Values $recovery -Key "recovery_completion_accounting_mismatch"

    if ($null -eq $behaviorFirstGateStatus) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $stabilityFile -Key "behavior_first_gate_status" }
    if ($null -eq $regressionClassification) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $latencyFile -Key "regression_classification" }
    if ($null -eq $effectiveMediaPlaneActive) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $transportFile -Key "effective_media_plane_active" }
    if ($null -eq $steadyStateUsedControlFallback) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $transportFile -Key "steady_state_used_control_fallback" }
    if ($null -eq $recoveryCompletionAccountingMismatch) { Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $recoveryFile -Key "recovery_completion_accounting_mismatch" }

    $classificationEvidence = [ordered]@{}
    $deepestStage = "(none)"
    $deepestClassification = "(missing)"
    $deepestSmallestNextFixArea = "(none)"

    foreach ($report in @(Get-ScreenShareRetainedClassificationReports)) {
        $stage = [string]$report.Stage
        $fileName = [string]$report.FileName
        $values = Read-KeyValueReport -Path (Join-Path $ArtifactDir $fileName)

        if ($null -eq $values) {
            Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $fileName
            continue
        }

        $classification = Get-KeyValue -Values $values -Key "classification"
        if ($null -eq $classification) {
            Add-MissingVerdictInput -MissingInputs $missingInputs -FileName $fileName -Key "classification"
            continue
        }

        $classificationEvidence[$stage] = $classification
        $deepestStage = $stage
        $deepestClassification = $classification

        $smallestNextFixArea = Get-KeyValue -Values $values -Key "smallest_next_fix_area"
        if ($null -ne $smallestNextFixArea) {
            $deepestSmallestNextFixArea = $smallestNextFixArea
        }
    }

    $hasMissingInputs = $missingInputs.Count -gt 0
    $isPass = -not $hasMissingInputs -and
        [string]::Equals($behaviorFirstGateStatus, "pass", [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($regressionClassification, "no_material_latency_regression", [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($effectiveMediaPlaneActive, "1", [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($steadyStateUsedControlFallback, "0", [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($recoveryCompletionAccountingMismatch, "0", [System.StringComparison]::OrdinalIgnoreCase)

    $operatorVerdict = "fail_local_regression"
    if ($noSessionEvidence.IsNoSession) {
        $operatorVerdict = "invalid_no_screenshare_session"
    }
    elseif ($hasMissingInputs) {
        $operatorVerdict = "inconclusive_missing_artifact"
    }
    elseif ([string]::Equals($deepestClassification, "mixed_or_inconclusive", [System.StringComparison]::OrdinalIgnoreCase)) {
        $operatorVerdict = "inconclusive_mixed"
    }
    elseif ($isPass) {
        $operatorVerdict = "pass"
    }
    elseif ((Get-ScreenShareExternalTransportClassifications) -contains $deepestClassification) {
        $operatorVerdict = "fail_live_transport_evidence"
    }

    $operatorSummary = switch ($operatorVerdict) {
        "invalid_no_screenshare_session" { "Screenshare artifact never reached a real visible/apply session; exclude it from fluency and quality comparisons." }
        "pass" { "Screenshare artifact passed behavior, media-plane, recovery-accounting, and retained closeout checks." }
        "fail_live_transport_evidence" { "Screenshare did not meet pass gates, and retained evidence points at live or external transport delivery." }
        "inconclusive_missing_artifact" { "Screenshare verdict could not be completed because required artifact reports or fields are missing." }
        "inconclusive_mixed" { "Screenshare retained evidence is mixed or inconclusive; do not patch forward from this artifact." }
        default { "Screenshare did not meet pass gates, and retained evidence points at a local or code-owned regression." }
    }

    $nextOperatorAction = switch ($operatorVerdict) {
        "invalid_no_screenshare_session" { "Debug setup or connection failure before screenshare; collect a fresh soak only after the session reaches visible/apply evidence." }
        "pass" { "Use this artifact as operator evidence and continue with the planned workstream." }
        "fail_live_transport_evidence" { "Use this artifact for the external NKN/network reliability lane; do not start local runtime tuning without a new plan." }
        "inconclusive_missing_artifact" { "Rerun AnalyzeRetained on a complete NKN soak artifact, or validate why the retained reports were not materialized." }
        "inconclusive_mixed" { "Stop and prepare a new investigation plan before extending Track B or local runtime diagnostics." }
        default { "Fix the local regression indicated by the retained classification before collecting more live transport evidence." }
    }

    $missingInputText = if ($missingInputs.Count -eq 0) { "(none)" } else { $missingInputs -join "," }
    $reportLines = New-Object System.Collections.Generic.List[string]
    $reportLines.Add(("operator_verdict={0}" -f $operatorVerdict)) | Out-Null
    $reportLines.Add(("operator_summary={0}" -f $operatorSummary)) | Out-Null
    $reportLines.Add(("next_operator_action={0}" -f $nextOperatorAction)) | Out-Null
    $reportLines.Add(("artifact_dir={0}" -f $ArtifactDir)) | Out-Null
    $reportLines.Add(("missing_required_inputs={0}" -f $missingInputText)) | Out-Null
    $reportLines.Add(("behavior_first_gate_status={0}" -f $(if ($null -eq $behaviorFirstGateStatus) { "(missing)" } else { $behaviorFirstGateStatus }))) | Out-Null
    $reportLines.Add(("regression_classification={0}" -f $(if ($null -eq $regressionClassification) { "(missing)" } else { $regressionClassification }))) | Out-Null
    $reportLines.Add(("effective_media_plane_active={0}" -f $(if ($null -eq $effectiveMediaPlaneActive) { "(missing)" } else { $effectiveMediaPlaneActive }))) | Out-Null
    $reportLines.Add(("steady_state_used_control_fallback={0}" -f $(if ($null -eq $steadyStateUsedControlFallback) { "(missing)" } else { $steadyStateUsedControlFallback }))) | Out-Null
    $reportLines.Add(("recovery_completion_accounting_mismatch={0}" -f $(if ($null -eq $recoveryCompletionAccountingMismatch) { "(missing)" } else { $recoveryCompletionAccountingMismatch }))) | Out-Null
    $reportLines.Add(("deepest_track_b_stage={0}" -f $deepestStage)) | Out-Null
    $reportLines.Add(("deepest_track_b_classification={0}" -f $deepestClassification)) | Out-Null
    $reportLines.Add(("deepest_track_b_smallest_next_fix_area={0}" -f $deepestSmallestNextFixArea)) | Out-Null
    $reportLines.Add(("no_screenshare_session={0}" -f $(if ($noSessionEvidence.IsNoSession) { "1" } else { "0" }))) | Out-Null
    $reportLines.Add(("no_screenshare_session_reason={0}" -f $noSessionEvidence.Reason)) | Out-Null
    $reportLines.Add(("no_screenshare_frames_sent={0}" -f $noSessionEvidence.FramesSent)) | Out-Null
    $reportLines.Add(("no_screenshare_media_plane_frames_sent={0}" -f $noSessionEvidence.MediaPlaneFramesSent)) | Out-Null
    $reportLines.Add(("no_screenshare_helper_apply_ms_avg={0}" -f $noSessionEvidence.HelperApplyMsAvg)) | Out-Null
    $reportLines.Add(("no_screenshare_visible_apply_ratio={0}" -f $noSessionEvidence.VisibleApplyRatio)) | Out-Null
    $reportLines.Add(("no_screenshare_baseline_established={0}" -f $noSessionEvidence.BaselineEstablished)) | Out-Null
    $reportLines.Add(("no_screenshare_helper_session_phase={0}" -f $noSessionEvidence.HelperSessionPhase)) | Out-Null
    $reportLines.Add(("no_screenshare_active_target_fps={0}" -f $noSessionEvidence.ActiveTargetFps)) | Out-Null

    if ($null -ne $quality) {
        $qualityKeys = @(
            "active_encode_target_width",
            "active_encode_target_height",
            "active_encode_target_bitrate",
            "active_encode_target_fps",
            "encoder_profile",
            "sender_freshness_mode",
            "sender_operating_state",
            "effective_quality_preset",
            "capture_scale",
            "helper_surface_interpolation_mode",
            "helper_surface_frame_width",
            "helper_surface_frame_height",
            "helper_surface_viewport_width",
            "helper_surface_viewport_height",
            "helper_surface_render_scaling",
            "helper_surface_scale_ratio"
        )

        foreach ($qualityKey in $qualityKeys) {
            $qualityValue = Get-KeyValue -Values $quality -Key $qualityKey
            if ($null -ne $qualityValue) {
                $reportLines.Add(("quality_{0}={1}" -f $qualityKey, $qualityValue)) | Out-Null
            }
        }
    }

    if ($null -ne $lowFps) {
        $lowFpsKeys = @(
            "classification",
            "primary_blocker",
            "effective_apply_fps",
            "sender_mode_counts",
            "next_action"
        )

        foreach ($lowFpsKey in $lowFpsKeys) {
            $lowFpsValue = Get-KeyValue -Values $lowFps -Key $lowFpsKey
            if ($null -ne $lowFpsValue) {
                $verdictKey = switch ($lowFpsKey) {
                    "classification" { "low_fps_catch_up_classification" }
                    "primary_blocker" { "low_fps_primary_blocker" }
                    "effective_apply_fps" { "low_fps_effective_apply_fps" }
                    "sender_mode_counts" { "low_fps_sender_mode_counts" }
                    default { "low_fps_next_action" }
                }
                $reportLines.Add(("{0}={1}" -f $verdictKey, $lowFpsValue)) | Out-Null
            }
        }
    }

    if ($null -ne $externalTopology) {
        $externalTopologyKeys = @(
            "external_topology_profile",
            "selected_rpc_key",
            "media_subclients",
            "external_topology_classification",
            "external_topology_next_action"
        )

        foreach ($externalTopologyKey in $externalTopologyKeys) {
            $externalTopologyValue = Get-KeyValue -Values $externalTopology -Key $externalTopologyKey
            if ($null -ne $externalTopologyValue) {
                $verdictKey = switch ($externalTopologyKey) {
                    "selected_rpc_key" { "external_topology_selected_rpc_key" }
                    "media_subclients" { "external_topology_media_subclients" }
                    default { $externalTopologyKey }
                }
                $reportLines.Add(("{0}={1}" -f $verdictKey, $externalTopologyValue)) | Out-Null
            }
        }
    }

    foreach ($stage in $classificationEvidence.Keys) {
        $reportLines.Add(("classification_{0}={1}" -f $stage, $classificationEvidence[$stage])) | Out-Null
    }

    $reportPath = Join-Path $ArtifactDir "screenshare-operator-verdict.txt"
    Set-Content -LiteralPath $reportPath -Value $reportLines
    Write-Host ("[ScreenShareOps] operator verdict: {0}" -f $operatorVerdict) -ForegroundColor Cyan
    Write-Host ("[ScreenShareOps] wrote {0}" -f $reportPath) -ForegroundColor Cyan

    return [pscustomobject]@{
        OperatorVerdict = $operatorVerdict
        ReportPath = $reportPath
        MissingRequiredInputs = $missingInputText
    }
}
