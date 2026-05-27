param(
    [string]$WalletPath = ".\artifacts\tuna-poc\wallet-test-nkn.json",
    [string]$WalletPassword = "",
    [string]$SidecarPath = ".\artifacts\portable\nLink\win-x64\tuna\win-x64\nlink-tuna-sidecar.exe",
    [ValidateSet("helpee", "helper", "both")]
    [string]$PayerMode = "helpee",
    [ValidateSet("none", "switch-off", "sidecar-kill")]
    [string]$Fault = "switch-off",
    [ValidateSet("handoff-fallback", "preactivated", "post-fallback", "v4-restart-v6-fallback", "live-v4-switch-off", "live-multi-toggle", "live-reactivation-second-transfer", "live-regular-activation-cycle")]
    [string]$RouteMode = "handoff-fallback",
    [string]$LiveToggleSequence = "",
    [ValidateSet("helpee-to-helper", "helper-to-helpee")]
    [string]$Direction = "helpee-to-helper",
    [string]$PayloadSize = "128MiB",
    [int]$TimeoutSeconds = 900,
    [int]$ProgressTimeoutSeconds = 180,
    [string]$ArtifactDir = "",
    [string]$ExePath = ".\artifacts\portable\nLink\win-x64\nLink.exe",
    [switch]$Mixed,
    [switch]$ExercisePause,
    [switch]$Build
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-TunaGuiLateSetupCleanupLine {
    param([AllowEmptyString()][string]$Line)

    if ([string]::IsNullOrEmpty($Line)) {
        return $false
    }

    if ($Line.IndexOf('event=filetransfer_v4_feedback_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $Line.IndexOf('frame_type=filetransfer.cancel.v4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $Line.IndexOf('OperationCanceledException', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $true
    }

    if ($Line.IndexOf('event=filetransfer_lifecycle_priority_send_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $Line.IndexOf('kind=cancel', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $Line.IndexOf('source=terminal_redundant_retry', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $true
    }

    return $false
}

function Select-TunaGuiControlledRestartLogSlices {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $logPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        return [pscustomobject]@{
            SetupPath = ''
            MeasuredPath = ''
            FilteredSetupCleanupLineCount = 0
        }
    }

    $lines = @(Get-Content -LiteralPath $logPath)
    $setupStartIndex = -1
    $firstFallbackIndex = -1
    $setupCanceledTerminalIndex = -1
    $startIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($setupStartIndex -lt 0 -and
            $lines[$i].IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('route=file_tuna_v4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $setupStartIndex = $i
        }

        if ($firstFallbackIndex -lt 0 -and
            $lines[$i].IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('route=post_tuna_fallback_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $firstFallbackIndex = $i
        }

        if ($firstFallbackIndex -ge 0 -and
            $setupCanceledTerminalIndex -lt 0 -and
            $lines[$i].IndexOf('event=file_transfer_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('state=Canceled', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $setupCanceledTerminalIndex = $i
        }

        if ($setupCanceledTerminalIndex -ge 0 -and
            $i -gt $setupCanceledTerminalIndex -and
            $lines[$i].IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('route=post_tuna_fallback_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $startIndex = $i
            break
        }
    }

    if ($startIndex -lt 0) {
        $startIndex = $firstFallbackIndex
    }

    $fullPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice-full.log'
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        Copy-Item -LiteralPath $logPath -Destination $fullPath -Force
    }

    $setupPath = Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log'
    if ($setupStartIndex -ge 0) {
        $setupEndIndex = if ($startIndex -gt $setupStartIndex) { $startIndex - 1 } else { $lines.Count - 1 }
        $setupLines = @(
            '[1970-01-01 00:00:00Z] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_started'
            $lines[$setupStartIndex..$setupEndIndex]
        )
        if ($startIndex -gt $setupStartIndex) {
            $setupLines += '[1970-01-01 00:00:00Z] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_cleanup_closed'
        }

        $setupLines | Set-Content -LiteralPath $setupPath -Encoding UTF8
    }
    else {
        '[1970-01-01 00:00:00Z] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_started; missing_setup_slice=1' |
            Set-Content -LiteralPath $setupPath -Encoding UTF8
    }

    if ($startIndex -lt 0) {
        return [pscustomobject]@{
            SetupPath = $setupPath
            MeasuredPath = ''
            FilteredSetupCleanupLineCount = 0
        }
    }

    $filteredSetupCleanupLineCount = 0
    $measuredLines = New-Object System.Collections.Generic.List[string]
    $measuredLines.Add('[1970-01-01 00:00:00Z] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=measured_post_tuna_fallback_v6_started') | Out-Null
    foreach ($line in @($lines[$startIndex..($lines.Count - 1)])) {
        if (Test-TunaGuiLateSetupCleanupLine -Line ([string]$line)) {
            $filteredSetupCleanupLineCount++
            continue
        }

        $measuredLines.Add([string]$line) | Out-Null
    }
    $measuredLines.Add('[1970-01-01 00:00:00Z] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=measured_post_tuna_fallback_v6_terminal') | Out-Null

    $measuredPath = Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log'
    $measuredLines.ToArray() | Set-Content -LiteralPath $measuredPath -Encoding UTF8
    return [pscustomobject]@{
        SetupPath = $setupPath
        MeasuredPath = $measuredPath
        FilteredSetupCleanupLineCount = $filteredSetupCleanupLineCount
    }
}

function Invoke-TunaGuiRetainedAnalysis {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$AnalysisDir,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [ValidateSet('None', 'SwitchOff', 'MultiToggle', 'RegularActivationCycle')]
        [string]$LiveRouteProofMode = 'None'
    )

    if ([string]::IsNullOrWhiteSpace($LogPath) -or -not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        throw "Retained log slice was not available: $LogPath"
    }

    New-Item -ItemType Directory -Force -Path $AnalysisDir | Out-Null
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot 'tools\FileTransfer-Ops.ps1') -Mode AnalyzeRetained -LogPath $LogPath -ArtifactDir $AnalysisDir -TailMinutes 0 -LiveRouteProofMode $LiveRouteProofMode
    if ($LASTEXITCODE -ne 0) {
        throw "Retained analysis failed with exit code $LASTEXITCODE. Artifacts: $AnalysisDir"
    }
}

function Invoke-TunaGuiRetainedAnalysisBestEffort {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$AnalysisDir,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [ValidateSet('None', 'SwitchOff', 'MultiToggle', 'RegularActivationCycle')]
        [string]$LiveRouteProofMode = 'None'
    )

    try {
        Invoke-TunaGuiRetainedAnalysis -RepoRoot $RepoRoot -AnalysisDir $AnalysisDir -LogPath $LogPath -LiveRouteProofMode $LiveRouteProofMode
    }
    catch {
        Write-Warning ("Retained analysis could not be completed for {0}: {1}" -f $LogPath, $_.Exception.Message)
    }
}

function Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [string]$FileName = 'filetransfer-retained-log-slice.log'
    )

    $milestonePath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-milestone-evidence.log'
    $retainedPath = Join-Path $ArtifactDir $FileName
    if (-not (Test-Path -LiteralPath $milestonePath -PathType Leaf)) {
        return
    }

    $milestoneLines = @(
        Get-Content -LiteralPath $milestonePath -ErrorAction SilentlyContinue |
            Where-Object {
                $line = [string]$_
                $line.IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $line.IndexOf('event=filetransfer_live_route_epoch_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $line.IndexOf('event=filetransfer_live_route_epoch_recovered', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
            }
    )
    if ($milestoneLines.Count -eq 0) {
        return
    }

    $existingText = ''
    if (Test-Path -LiteralPath $retainedPath -PathType Leaf) {
        $existingText = Get-Content -LiteralPath $retainedPath -Raw -ErrorAction SilentlyContinue
    }

    $missingLines = New-Object System.Collections.Generic.List[string]
    foreach ($line in @($milestoneLines)) {
        $text = [string]$line
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        if ($existingText.IndexOf($text, [System.StringComparison]::Ordinal) -lt 0) {
            $missingLines.Add($text) | Out-Null
        }
    }

    if ($missingLines.Count -eq 0) {
        return
    }

    $combined = @($missingLines.ToArray())
    if (-not [string]::IsNullOrEmpty($existingText)) {
        $combined += $existingText
    }

    [System.IO.File]::WriteAllText($retainedPath, ($combined -join [Environment]::NewLine), [System.Text.Encoding]::UTF8)
}

function Invoke-TunaGuiLiveRetainedAnalysisBestEffort {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$RouteMode
    )

    if ($RouteMode -ne 'preactivated' -and
        $RouteMode -ne 'live-v4-switch-off' -and
        $RouteMode -ne 'live-multi-toggle' -and
        $RouteMode -ne 'live-reactivation-second-transfer' -and
        $RouteMode -ne 'live-regular-activation-cycle') {
        return
    }

    $retainedPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    $liveRouteProofMode = if ($RouteMode -eq 'live-v4-switch-off') { 'SwitchOff' } elseif ($RouteMode -eq 'live-multi-toggle') { 'MultiToggle' } elseif ($RouteMode -eq 'live-regular-activation-cycle') { 'RegularActivationCycle' } else { 'None' }
    Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice -ArtifactDir $ArtifactDir
    Invoke-TunaGuiRetainedAnalysisBestEffort -RepoRoot $RepoRoot -AnalysisDir $ArtifactDir -LogPath $retainedPath -LiveRouteProofMode $liveRouteProofMode

    if ($RouteMode -eq 'live-reactivation-second-transfer') {
        $secondRetainedPath = Join-Path $ArtifactDir 'filetransfer-second-transfer-retained-log-slice.log'
        Invoke-TunaGuiRetainedAnalysisBestEffort -RepoRoot $RepoRoot -AnalysisDir (Join-Path $ArtifactDir 'second-transfer-analysis') -LogPath $secondRetainedPath
    }
}

function Invoke-TunaGuiMeasuredFallbackRetainedAnalysis {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    Invoke-TunaGuiRetainedAnalysis -RepoRoot $RepoRoot -AnalysisDir (Join-Path $ArtifactDir 'measured-fallback-analysis') -LogPath $LogPath
}

function Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    try {
        $slices = Select-TunaGuiControlledRestartLogSlices -ArtifactDir $ArtifactDir
        if (-not [string]::IsNullOrWhiteSpace([string]$slices.SetupPath) -and
            (Test-Path -LiteralPath ([string]$slices.SetupPath) -PathType Leaf)) {
            Invoke-TunaGuiRetainedAnalysisBestEffort -RepoRoot $RepoRoot -AnalysisDir (Join-Path $ArtifactDir 'setup-analysis') -LogPath $slices.SetupPath
        }

        if (-not [string]::IsNullOrWhiteSpace([string]$slices.MeasuredPath) -and
            (Test-Path -LiteralPath ([string]$slices.MeasuredPath) -PathType Leaf)) {
            Invoke-TunaGuiMeasuredFallbackRetainedAnalysis -RepoRoot $RepoRoot -ArtifactDir $ArtifactDir -LogPath $slices.MeasuredPath
        }
    }
    catch {
        Write-Warning ("Measured fallback retained analysis could not be completed: {0}" -f $_.Exception.Message)
    }
}

function Read-TunaGuiReportValue {
    param(
        [Parameter(Mandatory = $true)][string]$ReportPath,
        [Parameter(Mandatory = $true)][string]$Key,
        [string]$DefaultValue = ''
    )

    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
        return $DefaultValue
    }

    foreach ($line in @(Get-Content -LiteralPath $ReportPath)) {
        $text = [string]$line
        if ($text.StartsWith(($Key + '='), [System.StringComparison]::Ordinal)) {
            return $text.Substring($Key.Length + 1)
        }
    }

    return $DefaultValue
}

function Count-TunaGuiReportMatchingLines {
    param(
        [Parameter(Mandatory = $true)][string]$ReportPath,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
        return 0
    }

    return @((Get-Content -LiteralPath $ReportPath) | Select-String -Pattern $Pattern).Count
}

function Split-TunaGuiTokenList {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -eq '(none)') {
        return @()
    }

    return @(
        $Value.Split([char]',', [System.StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '(none)' }
    )
}

function Get-TunaGuiLogEventCount {
    param(
        [string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$EventName
    )

    return @($Lines | Where-Object { ([string]$_).IndexOf(('event=' + $EventName), [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count
}

function Get-TunaGuiMeasuredFallbackDiagnostics {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $measuredPath = Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log'
    $lines = @()
    if (Test-Path -LiteralPath $measuredPath -PathType Leaf) {
        $lines = @(Get-Content -LiteralPath $measuredPath)
    }

    $lastCommittedChunk = -1
    $highestObservedChunk = -1
    $finalTerminalState = '(none)'
    $bridgeQueueClearCount = 0
    $payloadBytes = 0
    foreach ($line in $lines) {
        $text = [string]$line
        if ($text -match 'file_size_bytes=([0-9]+)') {
            $payloadBytes = [Math]::Max($payloadBytes, [long]$Matches[1])
        }

        if ($text -match 'next_chunk_index=([0-9]+)') {
            $lastCommittedChunk = [Math]::Max($lastCommittedChunk, [int]$Matches[1])
        }

        if ($text -match 'highest_received_chunk_index=([0-9]+)') {
            $highestObservedChunk = [Math]::Max($highestObservedChunk, [int]$Matches[1])
        }

        if ($text.IndexOf('event=file_transfer_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $text.IndexOf('_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $text -match 'state=([^; ]+)') {
            $finalTerminalState = [string]$Matches[1]
        }

        if ($text.IndexOf('event=nkn_bridge_bulk_send_summary', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $text -match 'queue_clears=([0-9]+)') {
            $bridgeQueueClearCount += [int]$Matches[1]
        }

        if ($text.IndexOf('event=nkn_bridge_bulk_queue_state', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $text -match 'cleared_since_last=([0-9]+)') {
            $bridgeQueueClearCount += [int]$Matches[1]
        }
    }

    $frontierRequestCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_frontier_request_sent'
    $v6ChunkSendTimeoutCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_chunk_batch_send_timeout'
    $payloadMiB = if ($payloadBytes -gt 0) { [double]$payloadBytes / 1048576.0 } else { 0.0 }
    $operatorWarningKinds = @()
    $operatorVerdictPath = Join-Path $ArtifactDir 'measured-fallback-analysis\filetransfer-operator-verdict.txt'
    if (Test-Path -LiteralPath $operatorVerdictPath -PathType Leaf) {
        $operatorWarningKinds = @(Split-TunaGuiTokenList -Value (Read-TunaGuiReportValue -ReportPath $operatorVerdictPath -Key 'warning_kinds' -DefaultValue '(none)'))
    }

    return [ordered]@{
        measuredSlicePresent = (Test-Path -LiteralPath $measuredPath -PathType Leaf)
        lastCommittedChunk = $lastCommittedChunk
        highestObservedChunk = $highestObservedChunk
        frontierRequestCount = $frontierRequestCount
        duplicateFrontierRequestCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_frontier_request_duplicate_ignored'
        receiverStateDeferredCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_receiver_state_deferred'
        receiverStateCoalescedCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_receiver_state_coalesced'
        v6ChunkSendTimeoutCount = $v6ChunkSendTimeoutCount
        bridgeQueueClearCount = $bridgeQueueClearCount
        fallbackWarningKinds = @($operatorWarningKinds)
        sendTimeoutsPerMiB = if ($payloadMiB -gt 0.0) { [Math]::Round([double]$v6ChunkSendTimeoutCount / $payloadMiB, 3) } else { 0.0 }
        frontierRequestsPerMiB = if ($payloadMiB -gt 0.0) { [Math]::Round([double]$frontierRequestCount / $payloadMiB, 3) } else { 0.0 }
        fallbackRescueFreezeCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_post_tuna_fallback_normal_send_ahead_freeze_started'
        fallbackRescueWidenCount = Get-TunaGuiLogEventCount -Lines $lines -EventName 'filetransfer_v6_post_tuna_fallback_frontier_rescue_widened'
        finalTerminalState = $finalTerminalState
    }
}

function Resolve-TunaGuiControlledRestartFailurePhase {
    param(
        [AllowNull()]$Summary,
        [AllowNull()]$ErrorSummary,
        [object]$FallbackDiagnostics
    )

    $errorText = ''
    if ($null -ne $ErrorSummary -and $ErrorSummary.PSObject.Properties.Name -contains 'error') {
        $errorText = [string]$ErrorSummary.error
    }

    if (-not [bool]$FallbackDiagnostics['measuredSlicePresent']) {
        return 'measured_not_started'
    }

    if ($errorText.IndexOf('progress', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $errorText.IndexOf('timeout', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return 'measured_progress_timeout'
    }

    if ($null -ne $Summary -and
        ($Summary.PSObject.Properties.Name -contains 'completed') -and
        ($Summary.PSObject.Properties.Name -contains 'integrityOk') -and
        [bool]$Summary.completed -and
        [bool]$Summary.integrityOk) {
        return 'measured_completed_with_warnings'
    }

    if ([string]::Equals([string]$FallbackDiagnostics['finalTerminalState'], 'Completed', [System.StringComparison]::OrdinalIgnoreCase)) {
        return 'measured_completed_with_warnings'
    }

    return 'measured_terminal_failure'
}

function Test-TunaGuiControlledSetupCancelAccepted {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [AllowNull()]$Summary
    )

    if ($null -eq $Summary -or
        -not ($Summary.PSObject.Properties.Name -contains 'setupPhase') -or
        $null -eq $Summary.setupPhase) {
        return $false
    }

    $setup = $Summary.setupPhase
    $route = if ($setup.PSObject.Properties.Name -contains 'route') { [string]$setup.route } else { '' }
    $protocol = if ($setup.PSObject.Properties.Name -contains 'protocolVersion') { [int]$setup.protocolVersion } else { 0 }
    $inboundState = if ($setup.PSObject.Properties.Name -contains 'inboundState') { [string]$setup.inboundState } else { '' }
    $outboundState = if ($setup.PSObject.Properties.Name -contains 'outboundState') { [string]$setup.outboundState } else { '' }
    $inboundError = if ($setup.PSObject.Properties.Name -contains 'inboundErrorCode') { [string]$setup.inboundErrorCode } else { '' }
    $outboundError = if ($setup.PSObject.Properties.Name -contains 'outboundErrorCode') { [string]$setup.outboundErrorCode } else { '' }
    $setupRouteAccepted = ($route -eq 'file_tuna_v4' -and $protocol -eq 4) -or
        ($route -eq 'post_tuna_fallback_v6' -and $protocol -eq 6)
    if (-not $setupRouteAccepted -or
        $inboundState -ne 'Canceled' -or
        $outboundState -ne 'Canceled' -or
        $inboundError -ne 'canceled_remote' -or
        $outboundError -ne 'canceled_local') {
        return $false
    }

    $setupPath = Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log'
    $measuredPath = Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $measuredPath -PathType Leaf)) {
        return $false
    }

    $setupClosed = @(Get-Content -LiteralPath $setupPath | Where-Object { ([string]$_).IndexOf('phase=setup_file_tuna_v4_cleanup_closed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0
    $measuredStarted = @(Get-Content -LiteralPath $measuredPath | Where-Object { ([string]$_).IndexOf('phase=measured_post_tuna_fallback_v6_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0
    return $setupClosed -and $measuredStarted
}

function Write-TunaGuiControlledRestartFailureSummary {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [string]$RouteMode = 'v4-restart-v6-fallback'
    )

    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
        return
    }

    $errorPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json'
    $errorSummary = $null
    if (Test-Path -LiteralPath $errorPath -PathType Leaf) {
        $errorSummary = Get-Content -LiteralPath $errorPath -Raw | ConvertFrom-Json
    }

    $diagnostics = Get-TunaGuiMeasuredFallbackDiagnostics -ArtifactDir $ArtifactDir
    $failurePhase = Resolve-TunaGuiControlledRestartFailurePhase -Summary $null -ErrorSummary $errorSummary -FallbackDiagnostics $diagnostics
    $summary = [ordered]@{
        event = 'filetransfer_tuna_gui_handoff_fallback_summary'
        routeMode = $RouteMode
        completed = $false
        integrityOk = $false
        fallbackFailurePhase = $failurePhase
        fallbackFailureReason = if ($null -ne $errorSummary -and $errorSummary.PSObject.Properties.Name -contains 'error') { [string]$errorSummary.error } else { '(missing_summary)' }
        fallbackDiagnostics = $diagnostics
    }

    $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

function Update-TunaGuiControlledRestartSummary {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][int]$FilteredSetupCleanupLineCount
    )

    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        return
    }

    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    $setupVerdictPath = Join-Path $ArtifactDir 'setup-analysis\filetransfer-operator-verdict.txt'
    $measuredVerdictPath = Join-Path $ArtifactDir 'measured-fallback-analysis\filetransfer-operator-verdict.txt'
    $measuredRoutePath = Join-Path $ArtifactDir 'measured-fallback-analysis\filetransfer-route-consistency-summary.txt'
    $measuredStabilityPath = Join-Path $ArtifactDir 'measured-fallback-analysis\stability-gates-summary.txt'
    $fallbackDiagnostics = Get-TunaGuiMeasuredFallbackDiagnostics -ArtifactDir $ArtifactDir
    $fallbackFailurePhase = Resolve-TunaGuiControlledRestartFailurePhase -Summary $summary -ErrorSummary $null -FallbackDiagnostics $fallbackDiagnostics
    $setupRawOperatorVerdict = Read-TunaGuiReportValue -ReportPath $setupVerdictPath -Key 'verdict' -DefaultValue '(missing)'
    $setupControlledCancelAccepted = Test-TunaGuiControlledSetupCancelAccepted -ArtifactDir $ArtifactDir -Summary $summary
    $setupNormalizedVerdict = if ($setupControlledCancelAccepted) { 'expected_controlled_setup_cancel' } else { $setupRawOperatorVerdict }
    $fallbackDiagnostics['setupNormalizedVerdict'] = $setupNormalizedVerdict
    $summary | Add-Member -NotePropertyName controlledRestartAnalysis -NotePropertyValue ([ordered]@{
        setupVerdict = $setupRawOperatorVerdict
        setupRawOperatorVerdict = $setupRawOperatorVerdict
        setupControlledCancelAccepted = $setupControlledCancelAccepted
        setupNormalizedVerdict = $setupNormalizedVerdict
        measuredRouteVerdict = Read-TunaGuiReportValue -ReportPath $measuredRoutePath -Key 'route_consistency_verdict' -DefaultValue '(missing)'
        measuredOperatorVerdict = Read-TunaGuiReportValue -ReportPath $measuredVerdictPath -Key 'verdict' -DefaultValue '(missing)'
        setupCleanupWarningCount = $FilteredSetupCleanupLineCount
        fallbackBridgeRecoveryWarningCount = Count-TunaGuiReportMatchingLines -ReportPath $measuredStabilityPath -Pattern 'recovered post-Tuna fallback bridge queue clear'
    }) -Force
    $summary | Add-Member -NotePropertyName setupNormalizedVerdict -NotePropertyValue $setupNormalizedVerdict -Force
    $summary | Add-Member -NotePropertyName fallbackFailurePhase -NotePropertyValue $fallbackFailurePhase -Force
    $summary | Add-Member -NotePropertyName fallbackFailureReason -NotePropertyValue '(none)' -Force
    $summary | Add-Member -NotePropertyName fallbackDiagnostics -NotePropertyValue $fallbackDiagnostics -Force
    $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($WalletPassword)) {
        $WalletPassword = [string]$env:NLINK_TUNA_TEST_WALLET_PASSWORD
    }

    if ([string]::IsNullOrWhiteSpace($WalletPassword)) {
        throw 'Provide -WalletPassword or set NLINK_TUNA_TEST_WALLET_PASSWORD.'
    }

    if ($Build) {
        & "$env:ProgramFiles\PowerShell\7\pwsh.exe" -NoProfile -ExecutionPolicy Bypass -File ".\installer\Build-Portable.ps1" -Runtime win-x64
    }

    $resolvedWallet = (Resolve-Path -LiteralPath $WalletPath).Path
    $resolvedSidecar = (Resolve-Path -LiteralPath $SidecarPath).Path
    $sidecarManifest = Join-Path ([System.IO.Path]::GetDirectoryName($resolvedSidecar)) 'tuna-sidecar-manifest.json'
    if (-not (Test-Path -LiteralPath $sidecarManifest)) {
        $packagedSidecar = Join-Path $repoRoot 'artifacts\portable\nLink\win-x64\tuna\win-x64\nlink-tuna-sidecar.exe'
        $packagedManifest = Join-Path ([System.IO.Path]::GetDirectoryName($packagedSidecar)) 'tuna-sidecar-manifest.json'
        if (-not (Test-Path -LiteralPath $packagedSidecar) -or -not (Test-Path -LiteralPath $packagedManifest)) {
            throw "Tuna sidecar manifest missing beside '$resolvedSidecar'. Build portable/installer first."
        }

        $resolvedSidecar = (Resolve-Path -LiteralPath $packagedSidecar).Path
        Write-Host "[Tuna GUI] Using packaged sidecar with manifest: $resolvedSidecar" -ForegroundColor DarkGray
    }
    $resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
    $exeDirectory = [System.IO.Path]::GetDirectoryName($resolvedExe)
    $bundledBridge = Join-Path $exeDirectory 'bridge\win-x64\index.js'
    if (-not (Test-Path -LiteralPath $bundledBridge)) {
        $repoBridgeDir = Join-Path $repoRoot 'artifacts\bridge\win-x64'
        $repoBridge = Join-Path $repoBridgeDir 'index.js'
        if (-not (Test-Path -LiteralPath $repoBridge)) {
            $repoBridgeDir = Join-Path $repoRoot 'artifacts\portable\nLink\win-x64\bridge\win-x64'
            $repoBridge = Join-Path $repoBridgeDir 'index.js'
        }

        if (-not (Test-Path -LiteralPath $repoBridge)) {
            throw "NKN bridge bundle not found beside ExePath or under repo artifacts. Build portable/installer first."
        }

        $repoNode = Join-Path $repoBridgeDir 'node.exe'
        if (-not (Test-Path -LiteralPath $repoNode)) {
            throw "NKN Node runtime not found beside repo bridge bundle. Build portable/installer first."
        }

        $env:NLINK_NKN_BRIDGE_PATH = (Resolve-Path -LiteralPath $repoBridge).Path
        $env:NLINK_NKN_NODE_PATH = (Resolve-Path -LiteralPath $repoNode).Path
        Write-Host "[Tuna GUI] Using bridge override: $($env:NLINK_NKN_BRIDGE_PATH)" -ForegroundColor DarkGray
        Write-Host "[Tuna GUI] Using node override: $($env:NLINK_NKN_NODE_PATH)" -ForegroundColor DarkGray
    }

    if ([string]::IsNullOrWhiteSpace($ArtifactDir)) {
        $timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
        $ArtifactDir = Join-Path $repoRoot "artifacts\gui-smoke\tuna-filetransfer-$timestamp"
    }

    $resolvedArtifactDir = [System.IO.Path]::GetFullPath($ArtifactDir)
    $repoArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
    if (-not $resolvedArtifactDir.StartsWith($repoArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "ArtifactDir must resolve under repo artifacts/: $resolvedArtifactDir"
    }

    New-Item -ItemType Directory -Force -Path $resolvedArtifactDir | Out-Null
    $receivedRoot = Join-Path $resolvedArtifactDir 'received'
    New-Item -ItemType Directory -Force -Path $receivedRoot | Out-Null

    $env:NLINK_RUN_GUI_SMOKE = '1'
    $env:NLINK_RUN_TUNA_GUI_FILETRANSFER = '1'
    $env:NLINK_TRANSPORT = 'NKN'
    $env:NLINK_GUI_SMOKE_SCENARIOS = 'FILETRANSFER_TUNA_HANDOFF_FALLBACK'
    $env:NLINK_TUNA_GUI_WALLET_PATH = $resolvedWallet
    $env:NLINK_TUNA_TEST_WALLET_PASSWORD = $WalletPassword
    $env:NLINK_TUNA_GUI_SIDECAR_EXE = $resolvedSidecar
    $env:NLINK_TUNA_GUI_PAYER_MODE = $PayerMode
    $env:NLINK_TUNA_GUI_FAULT = $Fault
    $env:NLINK_TUNA_GUI_ROUTE_MODE = $RouteMode
    if (-not [string]::IsNullOrWhiteSpace($LiveToggleSequence)) {
        $env:NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE = $LiveToggleSequence
    }
    else {
        Remove-Item Env:NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE -ErrorAction SilentlyContinue
    }
    if ($Mixed) {
        $env:NLINK_TUNA_GUI_MIXED_SCREENSHARE = '1'
    }
    else {
        Remove-Item Env:NLINK_TUNA_GUI_MIXED_SCREENSHARE -ErrorAction SilentlyContinue
    }
    Remove-Item Env:NLINK_FILETRANSFER_DIAGNOSTIC_FILE_TUNA_V4 -ErrorAction SilentlyContinue
    if ($ExercisePause) {
        $env:NLINK_TUNA_GUI_EXERCISE_PAUSE = '1'
    }
    else {
        Remove-Item Env:NLINK_TUNA_GUI_EXERCISE_PAUSE -ErrorAction SilentlyContinue
    }
    $env:NLINK_FILETRANSFER_SOAK_DIRECTION = $Direction
    $env:NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES = $PayloadSize
    $env:NLINK_FILETRANSFER_SOAK_CYCLE_TIMEOUT_SECONDS = [string]$TimeoutSeconds
    $env:NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS = [string]$ProgressTimeoutSeconds
    $env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR = $resolvedArtifactDir
    $env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT = $receivedRoot

    Write-Host "[Tuna GUI] Running file-transfer handoff/fallback GUI smoke." -ForegroundColor Cyan
    Write-Host "[Tuna GUI] Artifacts: $resolvedArtifactDir" -ForegroundColor DarkGray
    Write-Host "[Tuna GUI] Direction=$Direction Payer=$PayerMode Fault=$Fault RouteMode=$RouteMode Payload=$PayloadSize Mixed=$($Mixed.IsPresent)" -ForegroundColor DarkGray

    & ".\tools\GuiSmoke-Windows.ps1" -ExePath $resolvedExe -TimeoutSeconds $TimeoutSeconds
    $guiSmokeExitCode = $LASTEXITCODE
    if ($guiSmokeExitCode -ne 0) {
        if ($RouteMode -eq 'v4-restart-v6-fallback') {
            Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort -RepoRoot $repoRoot -ArtifactDir $resolvedArtifactDir
            Write-TunaGuiControlledRestartFailureSummary -ArtifactDir $resolvedArtifactDir -RouteMode $RouteMode
        }
        else {
            Invoke-TunaGuiLiveRetainedAnalysisBestEffort -RepoRoot $repoRoot -ArtifactDir $resolvedArtifactDir -RouteMode $RouteMode
        }

        throw "GUI smoke failed with exit code $guiSmokeExitCode. Artifacts: $resolvedArtifactDir"
    }

    $summaryPath = Join-Path $resolvedArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        if ($RouteMode -eq 'v4-restart-v6-fallback') {
            Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort -RepoRoot $repoRoot -ArtifactDir $resolvedArtifactDir
            Write-TunaGuiControlledRestartFailureSummary -ArtifactDir $resolvedArtifactDir -RouteMode $RouteMode
        }
        else {
            Invoke-TunaGuiLiveRetainedAnalysisBestEffort -RepoRoot $repoRoot -ArtifactDir $resolvedArtifactDir -RouteMode $RouteMode
        }

        throw "GUI smoke did not write file-transfer Tuna summary. Artifacts: $resolvedArtifactDir"
    }

    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    if ($RouteMode -eq 'v4-restart-v6-fallback') {
        $slices = Select-TunaGuiControlledRestartLogSlices -ArtifactDir $resolvedArtifactDir
        if (-not [string]::IsNullOrWhiteSpace([string]$slices.SetupPath)) {
            Invoke-TunaGuiRetainedAnalysisBestEffort -RepoRoot $repoRoot -AnalysisDir (Join-Path $resolvedArtifactDir 'setup-analysis') -LogPath $slices.SetupPath
        }

        Invoke-TunaGuiMeasuredFallbackRetainedAnalysis -RepoRoot $repoRoot -ArtifactDir $resolvedArtifactDir -LogPath $slices.MeasuredPath
        Update-TunaGuiControlledRestartSummary -ArtifactDir $resolvedArtifactDir -FilteredSetupCleanupLineCount ([int]$slices.FilteredSetupCleanupLineCount)
    }
    elseif ($RouteMode -eq 'preactivated' -or $RouteMode -eq 'live-v4-switch-off' -or $RouteMode -eq 'live-multi-toggle' -or $RouteMode -eq 'live-reactivation-second-transfer' -or $RouteMode -eq 'live-regular-activation-cycle') {
        $retainedPath = Join-Path $resolvedArtifactDir 'filetransfer-retained-log-slice.log'
        $liveRouteProofMode = if ($RouteMode -eq 'live-v4-switch-off') { 'SwitchOff' } elseif ($RouteMode -eq 'live-multi-toggle') { 'MultiToggle' } elseif ($RouteMode -eq 'live-regular-activation-cycle') { 'RegularActivationCycle' } else { 'None' }
        Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice -ArtifactDir $resolvedArtifactDir
        Invoke-TunaGuiRetainedAnalysis -RepoRoot $repoRoot -AnalysisDir $resolvedArtifactDir -LogPath $retainedPath -LiveRouteProofMode $liveRouteProofMode
        if ($RouteMode -eq 'live-reactivation-second-transfer') {
            $secondRetainedPath = Join-Path $resolvedArtifactDir 'filetransfer-second-transfer-retained-log-slice.log'
            Invoke-TunaGuiRetainedAnalysis -RepoRoot $repoRoot -AnalysisDir (Join-Path $resolvedArtifactDir 'second-transfer-analysis') -LogPath $secondRetainedPath
        }
    }

    if (-not [bool]$summary.completed -or -not [bool]$summary.integrityOk) {
        throw ("GUI smoke summary reports incomplete transfer. completed={0}; integrity_ok={1}; inbound_state={2}; outbound_state={3}; inbound_error={4}; outbound_error={5}; artifacts={6}" -f `
            $summary.completed,
            $summary.integrityOk,
            $summary.inboundState,
            $summary.outboundState,
            $summary.inboundErrorCode,
            $summary.outboundErrorCode,
            $resolvedArtifactDir)
    }
}
finally {
    Pop-Location
}
