param(
    [Parameter(Mandatory = $true)][string]$CandidateArtifactDir
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

function Get-SummarySectionLines {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Header,
        [Parameter(Mandatory = $true)][string]$EventMarker
    )

    if (-not (Test-Path $Path)) {
        return @()
    }

    $inSection = $false
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if (-not $inSection) {
            if ([string]::Equals($line.Trim(), $Header, [System.StringComparison]::OrdinalIgnoreCase)) {
                $inSection = $true
            }

            continue
        }

        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line.IndexOf($EventMarker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $lines.Add($line.Trim())
        }
    }

    return @($lines.ToArray())
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

function Get-IntValueOrDefault {
    param(
        [string]$RawValue,
        [int]$DefaultValue = -1
    )

    if ([string]::IsNullOrWhiteSpace($RawValue)) {
        return $DefaultValue
    }

    $parsed = 0
    if ([int]::TryParse($RawValue, [ref]$parsed)) {
        return $parsed
    }

    return $DefaultValue
}

function Get-LineTimestampUtc {
    param([string]$Line)

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return $null
    }

    $match = [System.Text.RegularExpressions.Regex]::Match($Line, '^\[(?<ts>[^\]]+)\]')
    if (-not $match.Success) {
        return $null
    }

    $timestamp = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse($match.Groups['ts'].Value, [ref]$timestamp)) {
        return $timestamp.ToUniversalTime()
    }

    return $null
}

function Get-FixAreaForClassification {
    param([string]$Classification)

    switch -Exact ($Classification) {
        'rpc_selection_churn_latency' { return 'bridge RPC selection/fallback policy' }
        'disconnect_recovery_churn_latency' { return 'bridge disconnect/reconnect hysteresis' }
        'bridge_transport_health_burst_latency' { return 'bridge transport health burst/backoff policy' }
        'steady_external_delivery_latency' { return 'external NKN/network receive backlog work' }
        default { return 'none' }
    }
}

function Get-NearestTransportWindow {
    param(
        [Parameter(Mandatory = $true)][datetimeoffset]$TimestampUtc,
        [Parameter(Mandatory = $true)][object[]]$TransportWindows,
        [int]$ToleranceMs = 3000
    )

    $nearest = $null
    $nearestDistance = [double]::PositiveInfinity
    foreach ($window in $TransportWindows) {
        if ($null -eq $window -or $null -eq $window.TimestampUtc) {
            continue
        }

        $distance = [Math]::Abs(($window.TimestampUtc - $TimestampUtc).TotalMilliseconds)
        if ($distance -lt $nearestDistance) {
            $nearest = $window
            $nearestDistance = $distance
        }
    }

    if ($nearestDistance -le $ToleranceMs) {
        return $nearest
    }

    return $null
}

if (-not (Test-Path $CandidateArtifactDir)) {
    throw "Candidate artifact dir not found: $CandidateArtifactDir"
}

$helperSocketSummaryPath = Join-Path $CandidateArtifactDir 'helper-socket-receive-summary.txt'
$bridgeTransportHealthSummaryPath = Join-Path $CandidateArtifactDir 'bridge-transport-health-summary.txt'

$helperSocketSummaryValues = Read-KeyValueSummaryFile -Path $helperSocketSummaryPath
$bridgeTransportHealthSummaryValues = Read-KeyValueSummaryFile -Path $bridgeTransportHealthSummaryPath

$badReceiveWindows = New-Object System.Collections.Generic.List[object]
foreach ($line in (Get-SummarySectionLines -Path $helperSocketSummaryPath -Header 'helper_socket_receive_summary_lines:' -EventMarker 'screenshare_helper_socket_receive_summary')) {
    $timestampUtc = Get-LineTimestampUtc -Line $line
    if ($null -eq $timestampUtc) {
        continue
    }

    $pairs = Get-StructuredLogPairs -Line $line
    $medianMs = Get-IntValueOrDefault -RawValue (Get-StructuredLogFieldValue -Pairs $pairs -Key 'envelope_send_to_socket_data_event_emitted_median_ms')
    if ($medianMs -lt 0) {
        $medianMs = Get-IntValueOrDefault -RawValue (Get-StructuredLogFieldValueAfter -Pairs $pairs -AfterKey 'helper_recovery_mechanism' -Offset 2)
    }

    $p95Ms = Get-IntValueOrDefault -RawValue (Get-StructuredLogFieldValue -Pairs $pairs -Key 'envelope_send_to_socket_data_event_emitted_p95_ms')
    if ($p95Ms -lt 0) {
        $p95Ms = Get-IntValueOrDefault -RawValue (Get-StructuredLogFieldValueAfter -Pairs $pairs -AfterKey 'helper_recovery_mechanism' -Offset 3)
    }

    if ($medianMs -ge 120 -or $p95Ms -ge 400) {
        $badReceiveWindows.Add([pscustomobject]@{
                TimestampUtc = $timestampUtc
                MedianMs = $medianMs
                P95Ms = $p95Ms
                RawLine = $line
            })
    }
}

$allTransportHealthWindows = New-Object System.Collections.Generic.List[object]
foreach ($line in (Get-SummarySectionLines -Path $bridgeTransportHealthSummaryPath -Header 'bridge_transport_health_summary_lines:' -EventMarker 'screenshare_bridge_transport_health_summary')) {
    $timestampUtc = Get-LineTimestampUtc -Line $line
    if ($null -eq $timestampUtc) {
        continue
    }

    $pairs = Get-StructuredLogPairs -Line $line
    $allTransportHealthWindows.Add([pscustomobject]@{
            TimestampUtc = $timestampUtc
            SelectedRpcKey = $( $value = Get-StructuredLogFieldValue -Pairs $pairs -Key 'selected_rpc_key'; if ([string]::IsNullOrWhiteSpace($value)) { Get-StructuredLogFieldValue -Pairs $pairs -Key 'srk' } else { $value } )
            SelectedRpcStage = $( $value = Get-StructuredLogFieldValue -Pairs $pairs -Key 'selected_rpc_stage'; if ([string]::IsNullOrWhiteSpace($value)) { Get-StructuredLogFieldValue -Pairs $pairs -Key 'srs' } else { $value } )
            ReadyEmitted = Get-IntValueOrDefault -RawValue $( $value = Get-StructuredLogFieldValue -Pairs $pairs -Key 'ready_emitted'; if ([string]::IsNullOrWhiteSpace($value)) { Get-StructuredLogFieldValue -Pairs $pairs -Key 'rdy' } else { $value } )
            ClientReadyAgeMs = Get-IntValueOrDefault -RawValue $( $value = Get-StructuredLogFieldValue -Pairs $pairs -Key 'client_ready_age_ms'; if ([string]::IsNullOrWhiteSpace($value)) { Get-StructuredLogFieldValue -Pairs $pairs -Key 'cra' } else { $value } )
            DisconnectCountSinceLast = Get-IntValueOrDefault -RawValue $( $value = Get-StructuredLogFieldValue -Pairs $pairs -Key 'disconnect_count_since_last'; if ([string]::IsNullOrWhiteSpace($value)) { Get-StructuredLogFieldValue -Pairs $pairs -Key 'dcc' } else { $value } )
            ConnectFailedCountSinceLast = Get-IntValueOrDefault -RawValue $( $value = Get-StructuredLogFieldValue -Pairs $pairs -Key 'connect_failed_count_since_last'; if ([string]::IsNullOrWhiteSpace($value)) { Get-StructuredLogFieldValue -Pairs $pairs -Key 'cfc' } else { $value } )
            WsErrorCountSinceLast = Get-IntValueOrDefault -RawValue $( $value = Get-StructuredLogFieldValue -Pairs $pairs -Key 'ws_error_count_since_last'; if ([string]::IsNullOrWhiteSpace($value)) { Get-StructuredLogFieldValue -Pairs $pairs -Key 'wec' } else { $value } )
            RpcFallbackAttemptCountSinceLast = Get-IntValueOrDefault -RawValue $( $value = Get-StructuredLogFieldValue -Pairs $pairs -Key 'rpc_fallback_attempt_count_since_last'; if ([string]::IsNullOrWhiteSpace($value)) { Get-StructuredLogFieldValue -Pairs $pairs -Key 'rfc' } else { $value } )
            FramesSentSinceLast = Get-IntValueOrDefault -RawValue $( $value = Get-StructuredLogFieldValue -Pairs $pairs -Key 'frames_sent_since_last'; if ([string]::IsNullOrWhiteSpace($value)) { Get-StructuredLogFieldValue -Pairs $pairs -Key 'fss' } else { $value } )
            RawLine = $line
        })
}

$transportHealthWindowsSorted = @($allTransportHealthWindows | Sort-Object TimestampUtc)
for ($index = 0; $index -lt $transportHealthWindowsSorted.Count; $index++) {
    $currentWindow = $transportHealthWindowsSorted[$index]
    $previousRpcKey = if ($index -gt 0) { [string]$transportHealthWindowsSorted[$index - 1].SelectedRpcKey } else { '' }
    $currentRpcKey = [string]$currentWindow.SelectedRpcKey
    $currentWindow | Add-Member -NotePropertyName SelectedRpcChangedFromPrevious -NotePropertyValue (
        -not [string]::IsNullOrWhiteSpace($previousRpcKey) -and
        -not [string]::IsNullOrWhiteSpace($currentRpcKey) -and
        -not [string]::Equals($previousRpcKey, $currentRpcKey, [System.StringComparison]::OrdinalIgnoreCase)
    ) -Force
}

$senderActiveTransportHealthWindows = @($transportHealthWindowsSorted | Where-Object { $_.FramesSentSinceLast -gt 0 })
$senderActiveWindowMode = 'sender_active_only'
$transportWindowsForCorrelation = $senderActiveTransportHealthWindows
if ($transportWindowsForCorrelation.Count -eq 0) {
    $senderActiveWindowMode = 'fallback_all_windows'
    $transportWindowsForCorrelation = $transportHealthWindowsSorted
}

$matchedBadReceiveWindowCount = 0
$rpcSelectionChurnMatchCount = 0
$disconnectRecoveryMatchCount = 0
$bridgeTransportHealthBurstMatchCount = 0
$steadyExternalMatchCount = 0

foreach ($badReceiveWindow in $badReceiveWindows) {
    $nearestWindow = Get-NearestTransportWindow -TimestampUtc $badReceiveWindow.TimestampUtc -TransportWindows $transportWindowsForCorrelation -ToleranceMs 3000
    if ($null -eq $nearestWindow) {
        continue
    }

    $matchedBadReceiveWindowCount++

    $rpcSelectionMarker =
        $nearestWindow.RpcFallbackAttemptCountSinceLast -gt 0 -or
        [string]::Equals([string]$nearestWindow.SelectedRpcStage, 'fallback', [System.StringComparison]::OrdinalIgnoreCase) -or
        $nearestWindow.SelectedRpcChangedFromPrevious

    $disconnectRecoveryMarker =
        $nearestWindow.DisconnectCountSinceLast -gt 0 -or
        $nearestWindow.ReadyEmitted -eq 0 -or
        ($nearestWindow.ClientReadyAgeMs -ge 0 -and $nearestWindow.ClientReadyAgeMs -lt 4000)

    $bridgeTransportHealthBurstMarker =
        ($nearestWindow.WsErrorCountSinceLast + $nearestWindow.ConnectFailedCountSinceLast) -gt 0

    if ($rpcSelectionMarker) {
        $rpcSelectionChurnMatchCount++
    }

    if ($disconnectRecoveryMarker) {
        $disconnectRecoveryMatchCount++
    }

    if ($bridgeTransportHealthBurstMarker) {
        $bridgeTransportHealthBurstMatchCount++
    }

    if (-not $rpcSelectionMarker -and -not $disconnectRecoveryMarker -and -not $bridgeTransportHealthBurstMarker) {
        $steadyExternalMatchCount++
    }
}

$classification = 'mixed_or_inconclusive'
if ($matchedBadReceiveWindowCount -gt 0) {
    $rpcSelectionShare = $rpcSelectionChurnMatchCount / [double]$matchedBadReceiveWindowCount
    $disconnectRecoveryShare = $disconnectRecoveryMatchCount / [double]$matchedBadReceiveWindowCount
    $bridgeTransportHealthBurstShare = $bridgeTransportHealthBurstMatchCount / [double]$matchedBadReceiveWindowCount
    $steadyExternalShare = $steadyExternalMatchCount / [double]$matchedBadReceiveWindowCount

    if ($rpcSelectionShare -ge 0.6) {
        $classification = 'rpc_selection_churn_latency'
    }
    elseif ($disconnectRecoveryShare -ge 0.6) {
        $classification = 'disconnect_recovery_churn_latency'
    }
    elseif ($bridgeTransportHealthBurstShare -ge 0.6) {
        $classification = 'bridge_transport_health_burst_latency'
    }
    elseif ($steadyExternalShare -ge 0.6) {
        $classification = 'steady_external_delivery_latency'
    }
}

$reportLines = @(
    ("candidate_artifact_dir={0}" -f $CandidateArtifactDir),
    'analysis_mode=candidate_window_correlation_only',
    ("classification={0}" -f $classification),
    ("smallest_next_fix_area={0}" -f (Get-FixAreaForClassification -Classification $classification)),
    'bad_receive_window_threshold=median_ms_ge_120_or_p95_ms_ge_400',
    'window_alignment_tolerance_ms=3000',
    ("sender_active_window_mode={0}" -f $senderActiveWindowMode),
    ("transport_window_count={0}" -f $transportWindowsForCorrelation.Count),
    ("bad_receive_window_count={0}" -f $badReceiveWindows.Count),
    ("matched_bad_receive_window_count={0}" -f $matchedBadReceiveWindowCount),
    ("rpc_selection_churn_match_count={0}" -f $rpcSelectionChurnMatchCount),
    ("disconnect_recovery_match_count={0}" -f $disconnectRecoveryMatchCount),
    ("bridge_transport_health_burst_match_count={0}" -f $bridgeTransportHealthBurstMatchCount),
    ("steady_external_match_count={0}" -f $steadyExternalMatchCount),
    ("latest_selected_rpc={0}" -f (Get-SummaryStringValue -Values $bridgeTransportHealthSummaryValues -Key 'selected_rpc' -DefaultValue '(none)')),
    ("latest_selected_rpc_key={0}" -f (Get-SummaryStringValue -Values $bridgeTransportHealthSummaryValues -Key 'selected_rpc_key' -DefaultValue '(none)')),
    ("latest_selected_rpc_stage={0}" -f (Get-SummaryStringValue -Values $bridgeTransportHealthSummaryValues -Key 'selected_rpc_stage' -DefaultValue 'none')),
    ("unique_selected_rpc_count={0}" -f (Get-SummaryIntValue -Values $bridgeTransportHealthSummaryValues -Key 'unique_selected_rpc_count')),
    ("latest_disconnect_reason={0}" -f (Get-SummaryStringValue -Values $bridgeTransportHealthSummaryValues -Key 'latest_disconnect_reason' -DefaultValue '(none)')),
    ("latest_frames_sent_since_last={0}" -f (Get-SummaryIntValue -Values $bridgeTransportHealthSummaryValues -Key 'frames_sent_since_last')),
    ("latest_envelope_send_to_socket_data_event_emitted_median_ms={0}" -f (Get-SummaryIntValue -Values $helperSocketSummaryValues -Key 'envelope_send_to_socket_data_event_emitted_median_ms')),
    ("latest_envelope_send_to_socket_data_event_emitted_p95_ms={0}" -f (Get-SummaryIntValue -Values $helperSocketSummaryValues -Key 'envelope_send_to_socket_data_event_emitted_p95_ms'))
)

$reportPath = Join-Path $CandidateArtifactDir 'helper-external-transport-health-analysis.txt'
Set-Content -Path $reportPath -Value $reportLines
$reportLines | ForEach-Object { Write-Output $_ }
