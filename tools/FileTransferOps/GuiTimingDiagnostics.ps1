# Reporting only: callers supply their already-selected evidence. Do not scan logs or evaluate authority here.
function Get-GuiSmokeTerminalWaitTimingDiagnostic {
    param(
        [long]$ElapsedTicks,
        [long]$SynchronizationTicks,
        [long]$ProjectionTicks,
        [long]$PathResolutionTicks,
        [long]$PollSleepTicks,
        [long]$PollCount,
        [long]$LineVisitCount,
        [long]$ParsedEventCount
    )

    $result = [ordered]@{
        reportingOnly = $true
        scope = 'terminal_wait_exclusive_wall_time'
        measurementEnd = 'before_timing_diagnostic_construction'
        status = 'unavailable'
        reason = 'invalid_timing_counters'
        elapsedMs = $null
        synchronizationMs = $null
        projectionMs = $null
        pathResolutionMs = $null
        pollSleepMs = $null
        otherMs = $null
        pollCount = $PollCount
        lineVisitCount = $LineVisitCount
        parsedEventCount = $ParsedEventCount
    }
    $accountedTicks = [double]$SynchronizationTicks + $ProjectionTicks + $PathResolutionTicks + $PollSleepTicks
    if ($ElapsedTicks -lt 0 -or $SynchronizationTicks -lt 0 -or $ProjectionTicks -lt 0 -or
        $PathResolutionTicks -lt 0 -or $PollSleepTicks -lt 0 -or $PollCount -lt 0 -or
        $LineVisitCount -lt 0 -or $ParsedEventCount -lt 0 -or $accountedTicks -gt $ElapsedTicks) {
        return $result
    }
    $millisecondsPerTick = 1000.0 / [Diagnostics.Stopwatch]::Frequency
    $result.status = 'available'
    $result.reason = 'exclusive_intervals_recorded'
    $result.elapsedMs = [Math]::Round($ElapsedTicks * $millisecondsPerTick, 3)
    $result.synchronizationMs = [Math]::Round($SynchronizationTicks * $millisecondsPerTick, 3)
    $result.projectionMs = [Math]::Round($ProjectionTicks * $millisecondsPerTick, 3)
    $result.pathResolutionMs = [Math]::Round($PathResolutionTicks * $millisecondsPerTick, 3)
    $result.pollSleepMs = [Math]::Round($PollSleepTicks * $millisecondsPerTick, 3)
    $result.otherMs = [Math]::Round(($ElapsedTicks - $accountedTicks) * $millisecondsPerTick, 3)
    return $result
}

function Get-GuiSmokeRouteToTerminalTimingDiagnostic {
    param(
        [AllowEmptyString()][string]$RouteLine = '',
        [AllowEmptyString()][string]$OutboundTerminalLine = '',
        [AllowEmptyString()][string]$ExpectedTransferId = '',
        [long]$PayloadBytes,
        [datetime]$NotBeforeUtc,
        [datetime]$NotAfterUtc
    )

    $result = [ordered]@{
        scope = 'same_writer_route_to_outbound_terminal'
        reportingOnly = $true
        timestampSource = 'log_writer_attempt_utc_ticks'
        status = 'unavailable'
        reason = 'missing_selected_evidence'
        durationMs = $null
        payloadBytesPerSecond = $null
    }
    if ([string]::IsNullOrWhiteSpace($RouteLine) -or [string]::IsNullOrWhiteSpace($OutboundTerminalLine)) { return $result }

    $records = @()
    foreach ($line in @($RouteLine, $OutboundTerminalLine)) {
        $fields = @{}
        $message = $line -replace '^\s*(?:\[[^\]]*\]\s*)+', ''
        foreach ($part in @($message -split ';')) {
            $pair = $part.Trim() -split '=', 2
            if ($pair.Count -ne 2) { continue }
            $name = $pair[0].Trim()
            if ($fields.ContainsKey($name)) {
                $result.reason = 'ambiguous_fields'
                return $result
            }
            $fields[$name] = $pair[1].Trim()
        }
        $records += $fields
    }
    $route = $records[0]
    $terminal = $records[1]
    $terminalCompleted = $terminal['state'] -eq 'Completed'
    # Service terminals omit state. Recognize only the canonical completed payload shape,
    # not the primary tracker's broader reason-substring normalization.
    if (-not $terminal.ContainsKey('state') -and $terminal['event'] -ceq 'transfer_terminal' -and
        $terminal['direction'] -ceq 'outbound' -and $terminal['reason'] -ceq 'Transfer complete.' -and
        $terminal['error_code'] -ceq '(none)') {
        $fileSize = 0L
        $transferredBytes = 0L
        $chunkCount = 0L
        $transferredChunks = 0L
        $terminalCompleted =
            [long]::TryParse($terminal['file_size_bytes'], [ref]$fileSize) -and
            [long]::TryParse($terminal['bytes_transferred'], [ref]$transferredBytes) -and
            [long]::TryParse($terminal['chunk_count'], [ref]$chunkCount) -and
            [long]::TryParse($terminal['chunks_transferred'], [ref]$transferredChunks) -and
            $PayloadBytes -gt 0 -and $fileSize -eq $PayloadBytes -and $transferredBytes -eq $fileSize -and
            $chunkCount -gt 0 -and $transferredChunks -eq $chunkCount
    }
    if ($route['event'] -ne 'filetransfer_route_selected' -or $route['direction'] -ne 'outbound' -or
        ($terminal['event'] -ne 'file_transfer_outbound_terminal' -and
            ($terminal['event'] -ne 'transfer_terminal' -or $terminal['direction'] -ne 'outbound')) -or
        ($terminal.ContainsKey('direction') -and $terminal['direction'] -ne 'outbound') -or
        -not $terminalCompleted -or $terminal['error_code'] -ne '(none)') {
        $result.reason = 'unsupported_selected_evidence'
        return $result
    }

    foreach ($value in @($ExpectedTransferId, $route['transfer_id'], $terminal['transfer_id'],
            $route['session_id'], $terminal['session_id'], $route['log_writer_id'], $terminal['log_writer_id'])) {
        if ([string]::IsNullOrWhiteSpace([string]$value) -or $value -match '(?i)redacted|^\((none|unknown)\)$') {
            $result.reason = 'missing_or_redacted_identity'
            return $result
        }
    }
    foreach ($name in @('transfer_id', 'session_id', 'log_writer_id')) {
        if (-not [string]::Equals($route[$name], $terminal[$name], [StringComparison]::Ordinal)) {
            $result.reason = 'identity_mismatch'
            return $result
        }
    }
    if (-not [string]::Equals($terminal['transfer_id'], $ExpectedTransferId, [StringComparison]::Ordinal)) {
        $result.reason = 'selected_transfer_mismatch'
        return $result
    }

    $routeTicks = 0L
    $terminalTicks = 0L
    $routeSequence = 0L
    $terminalSequence = 0L
    if (-not [long]::TryParse($route['log_writer_attempt_utc_ticks'], [ref]$routeTicks) -or
        -not [long]::TryParse($terminal['log_writer_attempt_utc_ticks'], [ref]$terminalTicks) -or
        -not [long]::TryParse($route['log_writer_sequence'], [ref]$routeSequence) -or
        -not [long]::TryParse($terminal['log_writer_sequence'], [ref]$terminalSequence) -or
        $routeTicks -le 0 -or $terminalTicks -gt [datetime]::MaxValue.Ticks -or
        $terminalTicks -le $routeTicks -or $routeSequence -le 0 -or $terminalSequence -le $routeSequence) {
        $result.reason = 'missing_or_invalid_writer_timing'
        return $result
    }
    if ($NotBeforeUtc -eq [datetime]::MinValue -or $NotAfterUtc -eq [datetime]::MinValue -or
        $routeTicks -lt $NotBeforeUtc.ToUniversalTime().Ticks -or
        $terminalTicks -gt $NotAfterUtc.ToUniversalTime().Ticks) {
        $result.reason = 'outside_measurement_window'
        return $result
    }
    if ($PayloadBytes -le 0) {
        $result.reason = 'invalid_payload_size'
        return $result
    }

    $durationTicks = $terminalTicks - $routeTicks
    $result.status = 'available'
    $result.reason = 'selected_evidence_matched'
    $result.durationMs = [Math]::Round($durationTicks / [double][TimeSpan]::TicksPerMillisecond, 3)
    $result.payloadBytesPerSecond = [Math]::Round($PayloadBytes / ($durationTicks / [double][TimeSpan]::TicksPerSecond), 3)
    return $result
}
