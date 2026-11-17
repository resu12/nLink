param([Parameter(Mandatory = $true)][string]$RepoRoot)
$ErrorActionPreference = 'Stop'

foreach ($path in @('tools\GuiSmoke-Windows.ps1', 'tools\GuiSmokeLogLedger.ps1')) {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot $path), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "Parse failure: $path" }
    foreach ($function in @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Parent.Parent -eq $ast -and
        ($node.Name -like '*TunaGuiFaultAdmission*' -or $node.Name -like 'Wait-TunaGuiAuthoritative*' -or
         $node.Name -in @('ConvertFrom-GuiSmokeSemicolonFields', 'Get-GuiSmokeFieldValue', 'ConvertTo-GuiSmokeInt',
            'Get-TunaGuiCompletedTerminalFromLogLine', 'Add-TunaGuiCompletedTerminalToTracker',
            'Get-GuiSmokeFileTransferTerminalDirection', 'Set-GuiSmokeFileTransferTerminalDefaults',
            'New-GuiSmokeLogLedgerState', 'Assert-GuiSmokeLogLedgerHealthy', 'Get-GuiSmokeLogLedgerLineCount',
            'Get-GuiSmokeLogLedgerBookmark', 'Invoke-GuiSmokeLogLedgerRangeVisitor',
            'Get-TunaGuiLiveReactivationRecognitionFailure'))
    }, $true))) {
        . ([scriptblock]::Create($function.Extent.Text))
    }
}
function Sync-GuiSmokeAppLogLedger {}
function Assert-GuiSmokeScenarioDeadline {}
function Start-Sleep { throw 'Unexpected admission wait in deterministic test.' }

function New-ProofLines {
    param([int]$Epoch = 3, [string]$InboundTransfer = 'transfer', [string]$InboundWriter = 'writer-in')
    $route = if ($Epoch % 2) { 'file_tuna_v4' } else { 'post_tuna_fallback_v6' }
    $protocol = if ($Epoch % 2) { 4 } else { 6 }
    $sourceRoute = if ($Epoch -eq 1) { 'regular_nkn_v4_fast' } elseif ($Epoch % 2) { 'post_tuna_fallback_v6' } else { 'file_tuna_v4' }
    $sourceProtocol = if ($sourceRoute -eq 'post_tuna_fallback_v6') { 6 } else { 4 }
    $kind = if ($Epoch -eq 1) { 'RegularV4ToTunaV4' } elseif ($Epoch % 2) { 'PostTunaFallbackV6ToTunaV4' } else { 'TunaV4ToPostTunaFallbackV6' }
    $sourceEpoch = $Epoch - 1
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($direction in @('outbound', 'inbound')) {
        $generation = if ($direction -eq 'outbound') { $Epoch + 1 } else { $Epoch + 8 }
        $bridge = if ($direction -eq 'outbound') { 1 } else { 8 }
        $transfer = if ($direction -eq 'outbound') { 'transfer' } else { $InboundTransfer }
        $writer = if ($direction -eq 'outbound') { 'writer-out' } else { $InboundWriter }
        $scope = "session_id=session; transfer_id=$transfer; direction=$direction; log_writer_id=$writer"
        $authority = "transition-$Epoch-$direction"
        $commitGeneration = if ($Epoch % 2) { $generation } else { 0 }
        $pair = if ($kind -eq 'PostTunaFallbackV6ToTunaV4') { "session|$transfer|$sourceRoute|$sourceProtocol|$sourceEpoch|1|(none)" } else { '(none)' }
        $lines.Add("event=filetransfer_route_transition_committed; $scope; authority_id=$authority; transition_generation=$Epoch; transition_kind=$kind; source_route=$sourceRoute; source_protocol_version=$sourceProtocol; source_live_route_epoch=$sourceEpoch; source_transport_epoch=1; source_bridge_recovery_generation=$bridge; checkpoint_request_id=(none); source_data_session_generation=$generation; pair_key=$pair; target_route=$route; target_protocol_version=$protocol; target_live_route_epoch=$Epoch; target_runtime_generation=$generation; target_terminal_generation=0; target_leg_generation=$generation; target_route_publication_generation=$generation; target_route_commit_generation=$commitGeneration; decision_status=Committed")
        $target = "route=$route; protocol_version=$protocol; live_route_epoch=$Epoch; runtime_generation=$generation; terminal_generation=0; leg_generation=$generation; route_publication_generation=$generation; route_commit_generation=$commitGeneration"
        $lines.Add("event=filetransfer_route_hint_authorized_publication_accepted; $scope; $target")
        $lines.Add("event=filetransfer_route_transition_disposition_applied; $scope; authority_id=$authority; transition_generation=$Epoch; target_route=$route; disposition=committed")
        $lines.Add("event=filetransfer_data_path_receive_result; $scope; $target")
    }
    return ,$lines.ToArray()
}
function Read-Proofs {
    param([string[]]$Lines, [int]$Bookmark = 0)
    $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
    $script:GuiSmokeAppLogLedger.Lines.AddRange($Lines)
    $tracker = New-TunaGuiFaultAdmissionTracker -Bookmark $Bookmark
    [void](Update-TunaGuiFaultAdmissionTracker -Tracker $tracker -EndBookmark $Lines.Count -SkipSynchronization)
    return $tracker
}

$baseline = New-ProofLines
$tracker = Read-Proofs $baseline
if ($tracker.AuthoritativeTunaDirections.Count -ne 2) { throw 'Fixture lacks two internally valid local proofs.' }
if ($null -eq $tracker.AuthoritativeTunaProjection) { throw 'Asymmetric local generations incorrectly prevent shared epoch-3 recognition.' }

$caseCount = 1
foreach ($epoch in @(1, 2, 4)) {
    $tracker = Read-Proofs (New-ProofLines -Epoch $epoch)
    $projection = if ($epoch % 2) { $tracker.AuthoritativeTunaProjection } else { $tracker.AuthoritativeFallbackProjection }
    if ($null -eq $projection) { throw "Asymmetric route-cycle epoch $epoch not recognized." }
    $caseCount++
}

$mutations = @(
    @{ Name = 'local publication'; Index = 5; Old = 'runtime_generation=11'; New = 'runtime_generation=99' },
    @{ Name = 'local payload'; Index = 7; Old = 'leg_generation=11'; New = 'leg_generation=99' },
    @{ Name = 'disposition authority'; Index = 6; Old = 'transition-3-inbound'; New = 'stale-authority' },
    @{ Name = 'disposition generation'; Index = 6; Old = 'transition_generation=3'; New = 'transition_generation=99' },
    @{ Name = 'rejected disposition'; Index = 6; Old = 'disposition=committed'; New = 'disposition=rejected; decision_status=Rejected' },
    @{ Name = 'source route'; Index = 4; Old = 'source_route=post_tuna_fallback_v6'; New = 'source_route=regular_nkn_v4_fast' },
    @{ Name = 'source protocol'; Index = 4; Old = 'source_protocol_version=6'; New = 'source_protocol_version=4' },
    @{ Name = 'source epoch'; Index = 4; Old = 'source_live_route_epoch=2'; New = 'source_live_route_epoch=1' },
    @{ Name = 'source transport'; Index = 4; Old = 'source_transport_epoch=1'; New = 'source_transport_epoch=2' },
    @{ Name = 'checkpoint'; Index = 4; Old = 'checkpoint_request_id=(none)'; New = 'checkpoint_request_id=other' },
    @{ Name = 'transition kind'; Index = 4; Old = 'transition_kind=PostTunaFallbackV6ToTunaV4'; New = 'transition_kind=RegularV4ToTunaV4' },
    @{ Name = 'missing pair'; Index = 4; Old = 'pair_key=session|transfer|post_tuna_fallback_v6|6|2|1|(none)'; New = 'pair_key=(none)' },
    @{ Name = 'wrong pair'; Index = 4; Old = 'pair_key=session|transfer|post_tuna_fallback_v6|6|2|1|(none)'; New = 'pair_key=unrelated-pair' },
    @{ Name = 'mixed local writers'; Index = 5; Old = 'log_writer_id=writer-in'; New = 'log_writer_id=other-writer' }
)
foreach ($mutation in $mutations) {
    $lines = [string[]]$baseline.Clone()
    $lines[$mutation.Index] = $lines[$mutation.Index].Replace($mutation.Old, $mutation.New)
    if ($lines[$mutation.Index] -ceq $baseline[$mutation.Index]) { throw "Mutation did not apply: $($mutation.Name)" }
    if ($null -ne (Read-Proofs $lines).AuthoritativeTunaProjection) { throw "Invalid proof admitted: $($mutation.Name)" }
    $caseCount++
}
foreach ($missing in @(2, 3, 6, 7)) {
    $lines = @($baseline[0..7] | Select-Object -Index @(0..7 | Where-Object { $_ -ne $missing }))
    if ($null -ne (Read-Proofs $lines).AuthoritativeTunaProjection) { throw "Missing edge $missing admitted." }
    $caseCount++
}
foreach ($change in @(
    @('session_id=session', 'session_id=other'), @('transfer_id=transfer', 'transfer_id=other'),
    @('live_route_epoch=3', 'live_route_epoch=5'), @('protocol_version=4', 'protocol_version=6'),
    @('route=file_tuna_v4', 'route=file_tuna_v6'), @('terminal_generation=0', 'terminal_generation=1'),
    @('log_writer_id=writer-in', 'log_writer_id=writer-out'), @('log_writer_id=writer-in', 'log_writer_id='),
    @('log_writer_id=writer-in', 'log_writer_id=(none)'), @('authority_id=transition-3-inbound', 'authority_id='),
    @('authority_id=transition-3-inbound', 'authority_id=[redacted]'), @('transition_generation=3', 'transition_generation=0'))) {
    $lines = [string[]]$baseline.Clone()
    foreach ($index in 4..7) { $lines[$index] = $lines[$index].Replace($change[0], $change[1]) }
    if ($null -ne (Read-Proofs $lines).AuthoritativeTunaProjection) { throw "Cross-direction mismatch admitted: $($change[0])" }
    $caseCount++
}
$other = New-ProofLines -InboundTransfer 'another-transfer' -InboundWriter 'another-writer'
if ($null -ne (Read-Proofs ($baseline + $other[4..7])).AuthoritativeTunaProjection) { throw 'Ambiguous inbound candidates admitted.' }
$caseCount++

$competingWriter = New-ProofLines -InboundWriter 'another-writer'
if ($null -ne (Read-Proofs ($baseline + $competingWriter[4..7])).AuthoritativeTunaProjection) { throw 'Coalesced competing writer admitted.' }
$conflictingAuthority = @($baseline[4..7] | ForEach-Object { $_.Replace('transition-3-inbound', 'another-authority') })
if ($null -ne (Read-Proofs ($baseline + $conflictingAuthority)).AuthoritativeTunaProjection) { throw 'Conflicting same-generation local authorities admitted.' }
$fallback = New-ProofLines -Epoch 4
$competingFallback = New-ProofLines -Epoch 4 -InboundWriter 'another-writer'
if ($null -ne (Read-Proofs ($fallback + $competingFallback[4..6])).AuthoritativeFallbackProjection) { throw 'Incomplete competing fallback writer admitted.' }
$caseCount += 3

$tracker = Read-Proofs $baseline -Bookmark 1
if ($null -ne $tracker.AuthoritativeTunaProjection) { throw 'Pre-bookmark commit admitted.' }
$tracker = Read-Proofs $baseline
[void](Set-TunaGuiFaultAdmissionProjectionBoundary -Tracker $tracker -RouteFamily Tuna -MinimumBookmark 8)
if ($null -ne $tracker.AuthoritativeTunaProjection) { throw 'Prior-window projection survived boundary replacement.' }
$caseCount += 2

$terminals = @('event=file_transfer_inbound_terminal; session_id=session; transfer_id=transfer; state=Completed; error_code=(none)',
    'event=file_transfer_outbound_terminal; session_id=session; transfer_id=transfer; state=Completed; error_code=(none)')
foreach ($lines in @(@($baseline + $terminals), @($terminals + $baseline))) {
    $tracker = Read-Proofs $lines
    if ($null -eq $tracker.TerminalPair) { throw 'Terminal fixture not recognized.' }
    $failed = $false
    try { [void](Wait-TunaGuiAuthoritativeTunaFaultAdmission -Tracker $tracker -TimeoutMs 100) }
    catch { $failed = $_.Exception.Message -like 'live_toggle_window_missed_transfer_completed:*' }
    if (-not $failed) { throw 'Terminal and proof in the same update admitted a late action.' }
    $caseCount++
}
foreach ($state in @('paired_but_action_window_closed', 'complete_but_unpaired', 'incomplete')) {
    $tracker = Read-Proofs $baseline
    if ($state -ne 'paired_but_action_window_closed') { $tracker.AuthoritativeTunaProjection = $null }
    if ($state -eq 'incomplete') { $tracker.AuthoritativeTunaDirections.Clear() }
    $before = $tracker | ConvertTo-Json -Depth 12 -Compress
    $failure = Get-TunaGuiLiveReactivationRecognitionFailure -Tracker $tracker -TransferId transfer -WaitFailure 'original wait; failure'
    if ($failure.proofState -ne $state -or $failure.waitFailure -ne 'original wait, failure' -or
        $failure.reason -ne 'completed_with_unresolved_authoritative_reactivation_recognition') {
        throw "Recognition diagnostic lost original failure or misclassified $state."
    }
    if (($tracker | ConvertTo-Json -Depth 12 -Compress) -cne $before) { throw 'Diagnostic mutated tracker state.' }
    $caseCount++
}
Write-Output "gui-route-pair-recognition-ok cases=$caseCount"
