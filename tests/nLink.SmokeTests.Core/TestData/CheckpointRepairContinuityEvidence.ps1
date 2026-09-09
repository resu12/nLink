param([string]$RepoRoot, [string]$InputPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'GUI parse failed' }
$definition = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -ceq 'Get-TunaGuiFallbackFrontierStallGraceRecheckFailure'}, $true))
if ($definition.Count -ne 1) { throw 'Expected actual GUI consumer' }
Invoke-Expression $definition[0].Extent.Text
$profile = 'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1'
$catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $profile
$captured = [IO.File]::ReadAllLines($InputPath)
function Name([string]$Line) { Get-FileTransferCoordinatorSnapshotEventName $Line }
function Field([string]$Line, [string]$Name) { Get-FileTransferCoordinatorSnapshotLineField $Line $Name }
function Set-Field([string]$Line, [string]$Name, [string]$Value) {
    $pattern = "(^|; )$([regex]::Escape($Name))=[^;]*"
    if ([regex]::IsMatch($Line, $pattern)) {
        return [regex]::Replace($Line, $pattern, { param($match) $match.Groups[1].Value + $Name + '=' + $Value })
    }
    return "$Line; $Name=$Value"
}
function One([string[]]$Lines, [string]$EventName) {
    $found = @($Lines | Where-Object { (Name $_) -ceq $EventName })
    if ($found.Count -ne 1) { throw "Expected exactly one production $EventName, got $($found.Count)" }
    return $found[0]
}
function Parse([string[]]$Lines, [string]$Path) {
    for ($i=0; $i -lt $Lines.Count; $i++) {
        ConvertFrom-FileTransferLogLine -Line $Lines[$i] -FilePath $Path -LineNumber ($i+1) -Sequence ($i+1)
    }
}
function Quartet($Result) { @($Result.RawCount,$Result.DistinctCount,$Result.RecoveredCount,$Result.UnresolvedCount) -join '/' }
function Retained([string[]]$Lines, [string]$Path, $SnapshotEvidence) {
    $events = @(Parse $Lines $Path)
    $index = New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $events -SnapshotEvidence $SnapshotEvidence
    Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $events -Index $index -SnapshotEvidence $SnapshotEvidence
}
function Metadata($Record) {
    $f = $Record.Event.Fields
    $result = [ordered]@{}
    foreach ($field in @('log_writer_id','log_writer_sequence','session_id','transfer_id','direction','route','protocol_version',
        'live_route_epoch','runtime_generation','terminal_generation','leg_generation','transport_epoch','bridge_recovery_generation',
        'bridge_decision_generation','checkpoint_request_id','checkpoint_generation','route_publication_generation','route_commit_generation',
        'data_session_identity','data_session_generation','frontier_chunk_index','highest_received_chunk_index','stall_started_utc',
        'wake_generation','wake_deadline_utc')) { $result[$field] = [string]$f[$field] }
    $result.recovered = [bool]$Record.Recovered
    $result.recoverySequence = if ($null -ne $Record.RecoveryEvent) { [string]$Record.RecoveryEvent.Fields.log_writer_sequence } else { '' }
    ConvertTo-Json -InputObject $result -Depth 4 -Compress
}
$hold = One $captured 'filetransfer_v6_frontier_request_deferred'
$grant = One $captured 'filetransfer_fallback_frontier_repair_authority_granted'
$published = One $captured 'filetransfer_route_hint_authorized_publication_accepted'
$requestSent = One $captured 'filetransfer_v6_frontier_request_sent'
$forced = One $captured 'filetransfer_v6_fallback_frontier_repair_progress_state_forced'
$receiverState = One $captured 'filetransfer_v6_receiver_state_sent'
$admissionDenied = @($captured | Where-Object {
    (Name $_) -ceq 'filetransfer_runtime_unlock_admission_denied' -and
    (Field $_ 'session_id') -ceq (Field $hold 'session_id') -and
    (Field $_ 'transfer_id') -ceq (Field $hold 'transfer_id') -and
    [long](Field $_ 'log_writer_sequence') -gt [long](Field $hold 'log_writer_sequence') -and
    [long](Field $_ 'log_writer_sequence') -lt [long](Field $receiverState 'log_writer_sequence')
})
if ($admissionDenied.Count -eq 0) { throw 'Fixture lost the real intervening admission-denied diagnostic' }
$streamStarted = One $captured 'filetransfer_coordinator_snapshot_stream_started'
$streamCompleted = One $captured 'filetransfer_coordinator_snapshot_stream_completed'
if ([array]::IndexOf($captured, $streamStarted) -ge [array]::IndexOf($captured, $streamCompleted) -or
    (Field $streamStarted 'coordinator_instance_id') -cne (Field $streamCompleted 'coordinator_instance_id')) {
    throw 'Captured production stream start/completion scope or order differs'
}
if ((Field $hold 'checkpoint_request_id') -ceq (Field $grant 'checkpoint_request_id') -or
    (Field $grant 'checkpoint_request_id') -cne '(none)') { throw 'Fixture did not cross the real checkpoint acceptance scope' }
$attemptLine = @($captured | Where-Object { (Name $_) -ceq 'filetransfer_coordinator_transition_snapshot' -and
    (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Field $_ 'snapshot_payload')).Value.decisionKind -ceq 'CheckpointRepairContinuityValidated' })
if ($attemptLine.Count -ne 1) { throw 'Expected exactly one real continuity validation snapshot' }
$attempt = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Field $attemptLine[0] 'snapshot_payload')).Value
$operation = @($attempt.aggregate.operations | Where-Object { $_.operationRole -ceq 'checkpoint_repair_continuity_attempt' })[0]
if ($operation.stateApplied -or $attempt.aggregate.beforeStateGeneration -ne $attempt.aggregate.afterStateGeneration -or
    $attempt.aggregate.beforeStateFingerprint -cne $attempt.aggregate.afterStateFingerprint) { throw 'Read-only validation changed runtime state' }

$copiedStampMutations = [ordered]@{
    copied_stamp_authority = @{ authority_id = 'forged-authority' }
    copied_stamp_predecessor = @{ predecessor_authority_id = 'forged-predecessor' }
    copied_stamp_request = @{ primary_protocol_repair_request_id = 'forged-request'; repair_request_id = 'forged-request' }
    copied_stamp_correlation = @{ primary_request_correlation_id = 'forged-correlation'; request_correlation_id = 'forged-correlation' }
    copied_stamp_repair_key = @{ repair_request_key = 'forged-repair-key' }
    copied_stamp_range = @{ range_fingerprint = 'forged-range' }
    copied_stamp_bridge_generation = @{ bridge_decision_generation = '99' }
    copied_stamp_checkpoint_generation = @{ checkpoint_generation = '99' }
    copied_stamp_data_session = @{ data_session_identity = 'forged-data-session' }
    copied_stamp_data_generation = @{ data_session_generation = '99' }
}
foreach ($case in @('exact','exact_duplicate','missing_source_stamp','missing_repair_stamp','wrong_source_stamp','wrong_receipt_stamp',
    'wrong_request_stamp','wrong_data_session','missing_forced_state','missing_send','old_base_profile','old_receiver_profile',
    'unknown_profile','forged_effect','source_aba') + @($copiedStampMutations.Keys)) {
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($original in $captured) {
        $line = $original
        if ($case -ceq 'missing_source_stamp' -and $original -ceq $hold) { $line = [regex]::Replace($line, '; continuity_source_fingerprint=[^;]+', '') }
        if ($case -ceq 'missing_repair_stamp' -and $original -ceq $grant) { $line = [regex]::Replace($line, '; continuity_receipt_id=[^;]+', '') }
        if ($case -ceq 'wrong_source_stamp' -and $original -ceq $hold) { $line = Set-Field $line 'continuity_source_fingerprint' ('fte1-' + ('a' * 24)) }
        if ($case -ceq 'wrong_receipt_stamp' -and $original -ceq $grant) { $line = Set-Field $line 'continuity_receipt_id' ('fte1-' + ('b' * 24)) }
        if ($case -ceq 'wrong_request_stamp' -and $original -ceq $requestSent) { $line = Set-Field $line 'continuity_request_id' ('fte1-' + ('c' * 24)) }
        if ($case -ceq 'wrong_data_session' -and $original -ceq $forced) { $line = Set-Field $line 'data_session_generation' '2' }
        # Keep the authored stamps and the diagnostic chain mutually consistent;
        # only the untrusted raw fields contradict the accepted snapshot payload.
        if ($copiedStampMutations.Contains($case) -and (Field $line 'continuity_profile') -ceq $profile) {
            foreach ($field in $copiedStampMutations[$case].Keys) {
                if (-not [string]::IsNullOrWhiteSpace((Field $line $field))) {
                    $line = Set-Field $line $field $copiedStampMutations[$case][$field]
                }
            }
        }
        if (($case -ceq 'missing_forced_state' -and $original -ceq $forced) -or ($case -ceq 'missing_send' -and $original -ceq $requestSent)) { continue }
        if ($case -cin @('old_base_profile','old_receiver_profile','unknown_profile') -and (Name $line) -clike 'filetransfer_coordinator_*') {
            $old = if ($case -ceq 'old_base_profile') { Get-FileTransferCoordinatorEvidenceCatalog }
                else { Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion 'snapshot-authority-v1-receiver-barrier-v1' }
            $line = Set-Field $line 'snapshot_catalog_version' $(if ($case -ceq 'unknown_profile') { 'unknown-checkpoint-profile' } else { $old.Version })
            $line = Set-Field $line 'snapshot_catalog_hash' $old.Hash
        }
        $lines.Add($line)
        if ($case -ceq 'exact_duplicate' -and $original -ceq $grant) { $lines.Add($line) }
        if ($case -ceq 'source_aba' -and $original -ceq $grant) {
            $sequence = [long](Field $grant 'log_writer_sequence')
            $replacement = Set-Field $published 'route_publication_generation' ([string]([long](Field $published 'route_publication_generation') + 1))
            $replacement = Set-Field $replacement 'log_writer_sequence' ([string]($sequence + 1))
            $restored = Set-Field $published 'log_writer_sequence' ([string]($sequence + 2))
            $replacement = Set-Field $replacement 'log_writer_attempt_utc_ticks' ([string]([long](Field $grant 'log_writer_attempt_utc_ticks') + 1))
            $restored = Set-Field $restored 'log_writer_attempt_utc_ticks' ([string]([long](Field $grant 'log_writer_attempt_utc_ticks') + 2))
            $timestamp = [regex]::Match($grant, '^\[[^\]]+\]').Value
            $replacement = [regex]::Replace($replacement, '^\[[^\]]+\]', $timestamp)
            $restored = [regex]::Replace($restored, '^\[[^\]]+\]', $timestamp)
            $lines.Add($replacement); $lines.Add($restored)
        }
        if ($case -ceq 'forged_effect' -and $original -ceq $attemptLine[0]) {
            $receipt = $operation.checkpointRepairContinuity.receipts[0]
            $scope = @('snapshot_contract_version','snapshot_selection_mode','snapshot_catalog_version','snapshot_catalog_hash',
                'coordinator_instance_id','session_id','transfer_id','direction','log_writer_id')
            $prefix = @($scope | ForEach-Object { "$_=$(Field $original $_)" }) -join '; '
            $boundary = $operation.checkpointRepairContinuity.currentScope.boundary
            $effectScope = "route=$($boundary.route); protocol_version=$($boundary.protocolVersion); live_route_epoch=$($boundary.liveRouteEpoch); route_publication_generation=$($boundary.routePublicationGeneration); route_commit_generation=$($boundary.routeCommitGeneration); runtime_generation=$($boundary.runtimeGeneration); terminal_generation=$($boundary.terminalGeneration); leg_generation=$($boundary.legGeneration); transport_epoch=$($boundary.transportEpoch); bridge_recovery_generation=$($boundary.bridgeRecoveryGeneration); checkpoint_generation=$($boundary.checkpointGeneration); checkpoint_request_id=$($boundary.checkpointRequestId); data_session_identity=$($boundary.dataSessionIdentity); data_session_generation=$($boundary.dataSessionGeneration)"
            $lines.Add("event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($attempt.transitionId); coordinator_state_generation=$($attempt.aggregate.afterStateGeneration); operation_family=route_runtime; operation_key=$($operation.operationKey); operation_authority_id=$($operation.authorityId); operation_generation=$($operation.operationGeneration); effect_kind=checkpoint_repair_continuity_validated; effect_identity=$($receipt.receiptIdentity); effect_generation=$($receipt.receiptGeneration); receipt_identity=$($receipt.receiptIdentity); receipt_generation=$($receipt.receiptGeneration); receipt_status=$($receipt.receiptStatus); receipt_fingerprint=$($receipt.receiptFingerprint); $effectScope; authority_effect=applied")
        }
    }
    $fullLines = @(for ($i=0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($case -ceq 'source_aba') { $line = Set-Field $line 'log_writer_sequence' ([string]($i+1)) }
        $line + "; gui_evidence_sequence=$($i+1)"
    })
    $names = @(Get-FileTransferFallbackFrontierGraceCorrelationEventNames) + @($catalog.SnapshotAuthorityV1) + @($catalog.GenerationStampedEffects)
    $compactRecords = @(for ($i=0; $i -lt $fullLines.Count; $i++) {
        if ((Name $fullLines[$i]) -cin $names) { [pscustomobject]@{ Line=$fullLines[$i]; LineIndex=$i } }
    })
    $compactLines = @($compactRecords | ForEach-Object { $_.Line })
    if ($compactLines.Count -ge $fullLines.Count) { throw 'Compact input was not independently regenerated' }
    $fullSnapshots = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $fullLines
    $compactSnapshots = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $compactRecords
    if ($fullSnapshots.BoundaryHash -cne $compactSnapshots.BoundaryHash -or
        (ConvertTo-Json -InputObject @($fullSnapshots.Boundaries) -Depth 64 -Compress) -cne
        (ConvertTo-Json -InputObject @($compactSnapshots.Boundaries) -Depth 64 -Compress)) {
        throw "$case full/compact snapshot boundary JSON/hash differs"
    }
    $snapshotExpected = if ($case -cin @('old_base_profile','old_receiver_profile','unknown_profile','forged_effect')) { 'fail' } else { 'pass' }
    foreach ($snapshot in @($fullSnapshots,$compactSnapshots)) {
        if ($snapshot.ArtifactEvidenceVerdict -cne $snapshotExpected) {
            throw "$case snapshot expected $snapshotExpected, got $($snapshot.ArtifactEvidenceVerdict): $($snapshot.ArtifactEvidenceFailureReason)"
        }
    }
    $full = Retained $fullLines 'generated-full.log' $fullSnapshots
    $compact = Retained $compactLines 'regenerated-compact.log' $compactSnapshots
    $guiFull = Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $fullLines -IncludeEpisodeAudit -SnapshotEvidence $fullSnapshots
    $guiCompact = Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $compactLines -IncludeEpisodeAudit -SnapshotEvidence $compactSnapshots
    $recovered = $case -cin @('exact','exact_duplicate')
    $expected = if ($recovered) { '1/1/1/0' } else { '1/1/0/1' }
    foreach ($result in @($full,$compact)) {
        if ((Quartet $result) -cne $expected) { throw "$case expected $expected, got $(Quartet $result)" }
    }
    foreach ($guiInput in @(@{ Name='full'; Result=$guiFull }, @{ Name='regenerated_compact'; Result=$guiCompact })) {
        $result = $guiInput.Result
        if (@($result.EpisodeAudit).Count -ne 1 -or $result.EpisodeAudit[0].Excluded -or
            $result.EpisodeAudit[0].Recovered -ne $recovered -or $result.Unresolved -eq $recovered) {
            $diagnostic = [ordered]@{
                input = $guiInput.Name; expectedRecovered = $recovered; resultIsNull = $null -eq $result
                resultProperties = @($result.PSObject.Properties.Name); unresolved = $result.Unresolved
                firstSequence = $result.FirstSequence; parsedRecords = $result.ParsedRecords
                episodeAuditIsNull = $null -eq $result.EpisodeAudit; episodeAuditCount = @($result.EpisodeAudit).Count
                episodeAudit = @(foreach ($episode in @($result.EpisodeAudit)) {
                    [ordered]@{
                        isNull = $null -eq $episode; excluded = $episode.Excluded; recovered = $episode.Recovered
                        eventIsNull = $null -eq $episode.Event
                        eventSequence = if ($null -eq $episode.Event) { $null } else { $episode.Event.Fields['gui_evidence_sequence'] }
                        writerSequence = if ($null -eq $episode.Event) { $null } else { $episode.Event.Fields['log_writer_sequence'] }
                        recoveryEventIsNull = $null -eq $episode.RecoveryEvent
                        recoverySequence = if ($null -eq $episode.RecoveryEvent) { $null } else { $episode.RecoveryEvent.Fields['gui_evidence_sequence'] }
                        recoveryWriterSequence = if ($null -eq $episode.RecoveryEvent) { $null } else { $episode.RecoveryEvent.Fields['log_writer_sequence'] }
                    }
                })
            }
            throw "$case GUI parity failed: $(ConvertTo-Json -InputObject $diagnostic -Depth 6 -Compress)"
        }
    }
    $boundary = Metadata $full.CandidateRecords[0]
    $boundaryHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.Encoding]::UTF8.GetBytes($boundary))
    foreach ($record in @($compact.CandidateRecords[0],$guiFull.EpisodeAudit[0],$guiCompact.EpisodeAudit[0])) {
        $otherBoundary = Metadata $record
        if ($otherBoundary -cne $boundary -or
            (Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.Encoding]::UTF8.GetBytes($otherBoundary))) -cne $boundaryHash) {
            throw "$case normalized metadata JSON/hash differs"
        }
    }
    "pass $case"
}
'checkpoint-continuity-evidence-ok'
