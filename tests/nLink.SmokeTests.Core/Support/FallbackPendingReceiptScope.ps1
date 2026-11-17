param([string]$RepoRoot, [string]$FixtureBase64)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')

function Set-ReceiptField {
    param([string]$Line, [string]$Name, [string]$Value)
    return [regex]::Replace($Line, '(?<=; )' + [regex]::Escape($Name) + '=[^;]*',
        [System.Text.RegularExpressions.MatchEvaluator]{ param($match) "$Name=$Value" })
}

[string[]]$lines = ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($FixtureBase64)))
$boundIndex = 2
if ($lines[$boundIndex] -notmatch 'event=filetransfer_fallback_tail_peer_proof_receipt_decided;') {
    throw 'Expected the existing peer-first aggregate fixture.'
}
$lines[$boundIndex] = $lines[$boundIndex].Replace('event=filetransfer_fallback_tail_peer_proof_receipt_decided;',
    'event=filetransfer_fallback_tail_peer_proof_pending_bound;')
$pending = $lines[$boundIndex].Replace('event=filetransfer_fallback_tail_peer_proof_pending_bound;',
    'event=filetransfer_fallback_tail_peer_proof_pending_decided;')
$pending = Set-ReceiptField $pending 'authority_id' '(none)'
$pending = Set-ReceiptField $pending 'yield_generation' '0'

function Assert-PendingScope {
    param([string]$Name, [string[]]$PendingLines, [bool]$Valid)
    # Each side is parsed afresh; no live input, shared event objects or proof caches.
    $caseLines = @($lines[0]) + $PendingLines + @($lines[1..($lines.Count - 1)])
    $events = @(for ($i = 0; $i -lt $caseLines.Count; $i++) {
        ConvertFrom-FileTransferLogLine -Line $caseLines[$i] -FilePath 'synthetic-pending-scope.log' -LineNumber ($i+1) -Sequence ($i+1)
    })
    $index = New-FileTransferRetainedEventIndex -Events $events -IncludeFieldStatistics $false
    $selected = @(foreach ($eventName in @($events.EventName | Sort-Object -Unique)) {
        Get-FileTransferCompactIndexEventsByName -Index $index -Name $eventName
    })
    $compact = @(foreach ($event in @($selected | Sort-Object Sequence, LineNumber)) {
        ConvertFrom-FileTransferLogLine -Line $event.RawLine -FilePath $event.FilePath -LineNumber $event.LineNumber -Sequence $event.Sequence
    })
    foreach ($inputEvents in @(@{Events=$events}, @{Events=$compact})) {
        $proof = Get-FileTransferFallbackTailCoordinatorAggregateCorrelation -Events $inputEvents.Events
        if ($proof.ValidPairingCount -ne [int]$Valid -or
            $proof.InvalidUnresolvedCount -ne [int](-not $Valid) -or
            $proof.InvalidRecoveredCount -ne 0) {
            throw "${Name}: expected valid=$Valid, got valid=$($proof.ValidPairingCount), unresolved=$($proof.InvalidUnresolvedCount), recovered=$($proof.InvalidRecoveredCount)"
        }
    }
    Write-Output "PASS $Name"
}

Assert-PendingScope 'exact' @($pending) $true
$historical = Set-ReceiptField $pending 'route_publication_generation' '3'
$historical = Set-ReceiptField $historical 'runtime_generation' '1'
$historical = Set-ReceiptField $historical 'leg_generation' '1'
$historical = Set-ReceiptField $historical 'owner_key' 'previous-local-owner'
Assert-PendingScope 'same_peer_prior_local_publication' @($historical, $pending) $true
foreach ($field in @('log_writer_id', 'session_id', 'transfer_id', 'direction', 'owner_key', 'pair_key',
        'route', 'protocol_version', 'route_publication_generation', 'route_commit_generation',
        'live_route_epoch', 'runtime_generation', 'terminal_generation', 'leg_generation',
        'bridge_recovery_generation', 'transport_epoch', 'checkpoint_generation', 'checkpoint_request_id',
        'data_session_identity', 'data_session_generation')) {
    $old = Set-ReceiptField $pending $field 'different-scope'
    Assert-PendingScope "exact_with_other_$field" @($old, $pending) $true
    Assert-PendingScope "other_only_$field" @($old) $false
    $missing = [regex]::Replace($pending, '; ' + [regex]::Escape($field) + '=[^;]*', '')
    Assert-PendingScope "missing_$field" @($missing) $false
}
Assert-PendingScope 'missing_pending' @() $false
Assert-PendingScope 'ambiguous_exact_pending' @($pending, $pending) $false
Assert-PendingScope 'rejected_pending' @((Set-ReceiptField $pending 'decision_status' 'Rejected')) $false
Assert-PendingScope 'wrong_peer_identity' @((Set-ReceiptField $pending 'peer_observation_identity' 'other-peer')) $false
Assert-PendingScope 'wrong_peer_fingerprint' @((Set-ReceiptField $pending 'peer_frame_fingerprint' 'other-frame')) $false
