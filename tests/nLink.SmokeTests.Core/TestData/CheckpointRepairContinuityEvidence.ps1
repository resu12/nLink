param([string]$RepoRoot, [string]$InputPath, [switch]$CommittedRetirement, [switch]$DelayedDisposition)
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
if ($CommittedRetirement) {
    foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult', 'Get-TunaGuiExplicitFailureClassification',
        'Get-TunaGuiFileTransferSetupFailureClassification')) {
        $definitions = @($ast.FindAll({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
        }, $true))
        if ($definitions.Count -ne 1) { throw "Shared GUI function is not unique: $name" }
        Invoke-Expression $definitions[0].Extent.Text
    }
    function Copy-Value($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
    function Replace-Envelope([string]$Line, $Envelope) {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Envelope -Depth 64 -Compress))
        $line = Set-Field $Line 'snapshot_sha256' (Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes)
        Set-Field $line 'snapshot_payload' ([Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_'))
    }
    function Origin-Operation($Envelope) {
        $operations = @($Envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'checkpoint_acceptance_retained' })
        if ($operations.Count -ne 1) { throw 'Expected one continuity origin operation' }
        $operations[0]
    }
    $snapshots = @(for ($i=0; $i -lt $captured.Count; $i++) {
        if ((Name $captured[$i]) -ceq 'filetransfer_coordinator_transition_snapshot') {
            [pscustomobject]@{ Index=$i; Envelope=(ConvertFrom-FileTransferCoordinatorSnapshotPayload (Field $captured[$i] 'snapshot_payload')).Value }
        }
    })
    $commits = @($snapshots | Where-Object {
        $_.Envelope.decisionKind -ceq 'CommitFileTransferRouteTransition' -and $_.Envelope.decisionStatus -ceq 'Committed' -and
        @($_.Envelope.aggregate.operations | Where-Object {
            $_.operationRole -ceq 'checkpoint_acceptance_retained' -and $_.beforeState -ceq 'Retained' -and $_.afterState -ceq 'None'
        }).Count -eq 1
    })
    if ($commits.Count -ne 1) { throw 'Expected exactly one real Committed continuity retirement' }
    $candidate = $commits[0]
    $retirement = Origin-Operation $candidate.Envelope
    $births = @($snapshots | Where-Object {
        $_.Envelope.decisionKind -ceq 'FallbackCheckpointAccepted' -and
        @($_.Envelope.aggregate.operations | Where-Object {
            $_.operationKey -ceq $retirement.operationKey -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Retained'
        }).Count -eq 1
    })
    if ($births.Count -ne 1) { throw 'Expected the exact accepted origin before route preparation' }
    $birth = $births[0]
    $origin = Origin-Operation $birth.Envelope
    if ($birth.Index -ge $candidate.Index -or
        (ConvertTo-Json -InputObject $origin.checkpointRepairContinuity -Depth 64 -Compress) -cne
        (ConvertTo-Json -InputObject $retirement.checkpointRepairContinuity -Depth 64 -Compress) -or
        $retirement.receiptStatus -cne 'Accepted') { throw 'Real Committed retirement changed its historical Accepted receipt/payload' }
    $validations = @($snapshots | Where-Object {
        $_.Envelope.decisionKind -ceq 'CheckpointRepairContinuityValidated' -and $_.Index -gt $birth.Index -and $_.Index -lt $candidate.Index -and
        @($_.Envelope.aggregate.operations | Where-Object { $_.afterState -ceq 'Validated' }).Count -eq 1
    })
    if ($validations.Count -eq 0) { throw 'Missing actual pre-retirement current-repair validation' }
    foreach ($kind in @('PrepareFileTransferRouteTransition', 'BeginFileTransferRouteTransitionPhysicalDrain',
        'ApplyFileTransferRouteTransitionDispositionReceipt')) {
        if (@($snapshots | Where-Object { $_.Envelope.decisionKind -ceq $kind }).Count -eq 0) {
            throw "Missing actual route lifecycle decision: $kind"
        }
    }
    $routeCommits = @($candidate.Envelope.aggregate.operations | Where-Object {
        $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_commit' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Committed'
    })
    if ($routeCommits.Count -ne 1) { throw 'Missing independent exact route-commit operation' }
    $cases = @('exact', 'exact_duplicate', 'unknown_committed', 'wrong_reason', 'rejected_mutation', 'idempotent_mutation',
        'generation_gap', 'unchanged_hash', 'unchanged_route', 'wrong_before_scope', 'wrong_data_session', 'wrong_data_generation',
        'historical_source', 'origin_fingerprint', 'receipt_command_identity_consistent', 'receipt_command_fingerprint_consistent',
        'receipt_identity_consistent', 'missing_origin', 'missing_retirement', 'duplicate_removal', 'competing_removal',
        'missing_route_commit', 'invalid_route_receipt', 'missing_route_effect', 'conflicting_snapshot')
    $outputs = [Collections.Generic.List[object]]::new()
    $baseline = $null
    foreach ($case in $cases) {
        "checkpoint-committed-retirement-case=$case"
        $lines = @($captured)
        $item = if ($case -ceq 'missing_origin') { $birth } else { $candidate }
        $envelope = Copy-Value $item.Envelope
        $subject = Origin-Operation $envelope
        $payload = $subject.checkpointRepairContinuity
        $receipt = $payload.receipts[0]
        switch -CaseSensitive ($case) {
            'unknown_committed' { $envelope.decisionKind = 'UnknownRouteCommit' }
            'wrong_reason' { $envelope.decisionReason = 'not_the_route_commit_reason' }
            'rejected_mutation' { $envelope.decisionStatus = 'Rejected' }
            'idempotent_mutation' { $envelope.decisionStatus = 'Idempotent' }
            'generation_gap' { $envelope.aggregate.afterStateGeneration++ }
            'unchanged_hash' { $envelope.aggregate.afterStateFingerprint = $envelope.aggregate.beforeStateFingerprint }
            'unchanged_route' { $envelope.aggregate.afterBoundary = Copy-Value $envelope.aggregate.beforeBoundary }
            'wrong_before_scope' { $envelope.aggregate.beforeBoundary.routePublicationGeneration++ }
            'wrong_data_session' { $envelope.aggregate.afterBoundary.dataSessionIdentity = 'fte1-' + ('a' * 24) }
            'wrong_data_generation' { $envelope.aggregate.afterBoundary.dataSessionGeneration++ }
            'historical_source' { $payload.sourceScope.scopeFingerprint = 'fte1-' + ('b' * 24) }
            'origin_fingerprint' { $payload.originReceiptFingerprint = 'fte1-' + ('c' * 24) }
            'receipt_command_identity_consistent' { $subject.commandIdentity = 'fte1-' + ('d' * 24); $receipt.commandIdentity = $subject.commandIdentity }
            'receipt_command_fingerprint_consistent' { $subject.commandFingerprint = 'fte1-' + ('e' * 24); $receipt.commandFingerprint = $subject.commandFingerprint }
            'receipt_identity_consistent' {
                $subject.receiptIdentity = 'fte1-' + ('f' * 24)
                $receipt.receiptIdentity = $subject.receiptIdentity; $payload.originReceiptIdentity = $subject.receiptIdentity
            }
            'missing_origin' { $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object { $_ -ne $subject }) }
            'missing_retirement' { $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object { $_ -ne $subject }) }
            'duplicate_removal' { $envelope.aggregate.operations += (Copy-Value $subject) }
            'competing_removal' {
                $other = Copy-Value $subject
                $other.authorityId = 'fte1-' + ('1' * 24)
                $other.operationKey = $other.operationKey.Replace($subject.authorityId, $other.authorityId)
                $other.checkpointRepairContinuity.acceptanceIdentity = $other.authorityId
                $other.checkpointRepairContinuity.originOperationKey = $other.operationKey
                $envelope.aggregate.operations += $other
            }
            'missing_route_commit' { $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object {
                -not ($_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_commit') }) }
            'invalid_route_receipt' {
                $routeCommit = @($envelope.aggregate.operations | Where-Object { $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_commit' })[0]
                $routeCommit.receiptValidated = $false
            }
            'conflicting_snapshot' { $receipt.commandIdentity = 'fte1-' + ('2' * 24) }
        }
        if ($case -cin @('receipt_command_identity_consistent', 'receipt_command_fingerprint_consistent')) {
            if (-not (Test-FileTransferCoordinatorCheckpointRepairContinuityOperation $envelope $subject $catalog)) {
                throw "$case must reach immutable origin-history validation, not fail only outer/typed consistency"
            }
        }
        if ($case -cnotin @('exact', 'exact_duplicate', 'missing_route_effect')) {
            $changed = Replace-Envelope $lines[$item.Index] $envelope
            if ($changed -ceq $lines[$item.Index]) { throw "Mutation did not change evidence: $case" }
            if ($case -ceq 'conflicting_snapshot') {
                $lines = @($lines[0..$item.Index]) + @($changed) + @($lines[($item.Index+1)..($lines.Count-1)])
            } else { $lines[$item.Index] = $changed }
        }
        if ($case -ceq 'exact_duplicate') {
            $lines = @($lines[0..$candidate.Index]) + @($lines[$candidate.Index]) + @($lines[($candidate.Index+1)..($lines.Count-1)])
        }
        if ($case -ceq 'missing_route_effect') {
            $lines = @($lines | Where-Object { -not ((Name $_) -ceq 'filetransfer_coordinator_authority_effect' -and
                (Field $_ 'transition_id') -ceq $candidate.Envelope.transitionId -and (Field $_ 'effect_kind') -ceq 'route_commit_adopted') })
            if ($lines.Count -ge $captured.Count) { throw 'No real projected route-commit effect removed' }
        }
        $fullLines = @('event=unrelated_fixture_diagnostic; note=independent_compaction') + $lines
        $compactRecords = @(for ($i=0; $i -lt $fullLines.Count; $i++) {
            if ((Name $fullLines[$i]) -cin (@($catalog.SnapshotAuthorityV1) + @($catalog.GenerationStampedEffects))) {
                [pscustomobject]@{ Line=$fullLines[$i]; LineIndex=$i }
            }
        })
        if ($compactRecords.Count -ge $fullLines.Count) { throw 'Compact input was not independently regenerated' }
        $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $fullLines
        $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $compactRecords
        $successfulFixture = $case -cin @('exact', 'exact_duplicate')
        $expectedValidCatalog = $successfulFixture -or $case -ceq 'missing_route_effect'
        $expected = if ($expectedValidCatalog) { 'pass' } else { 'fail' }
        foreach ($evaluation in @($full, $compact)) {
            if ($evaluation.ArtifactEvidenceVerdict -cne $expected) {
                throw "$case expected=$expected; actual=$($evaluation.ArtifactEvidenceVerdict); reason=$($evaluation.ArtifactEvidenceFailureReason)"
            }
            if ($successfulFixture -and @($evaluation.Boundaries | Where-Object {
                $_.boundaryId -ceq $candidate.Envelope.transitionId -and $_.verdict -ceq 'fail'
            }).Count -ne 0) { throw "$case introduced a semantic failure at the exact committed retirement" }
            if ($case -cin @('receipt_command_identity_consistent', 'receipt_command_fingerprint_consistent') -and
                $evaluation.ArtifactEvidenceFailureReason -cnotmatch 'snapshot_checkpoint_repair_continuity_retirement_unbound') {
                throw "$case did not reject the exact immutable historical receipt tuple: $($evaluation.ArtifactEvidenceFailureReason)"
            }
        }
        $json = ConvertTo-Json -InputObject @($full.Boundaries) -Depth 100 -Compress
        if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
            $json -cne (ConvertTo-Json -InputObject @($compact.Boundaries) -Depth 100 -Compress)) {
            throw "$case full/regenerated-compact normalized boundary JSON/hash differs"
        }
        $boundary = $full.FirstUnresolvedBoundary
        $routeJson = $null
        if ($expectedValidCatalog) {
            if (-not $full.StreamComplete -or $full.LegacySelectionCount -ne 0) { throw "$case lost exact complete snapshot authority" }
            $route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
            $compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
            if ((Compare-FileTransferCoordinatorSnapshotRouteConsistency $route $compactRoute).Verdict -cne 'pass') { throw "$case route-result parity failed" }
            $routeJson = ConvertTo-Json -InputObject @($route.Findings) -Depth 100 -Compress
            if ($case -ceq 'exact') { $baseline = $full; $baselineRoute = $routeJson }
            elseif ($successfulFixture -and $routeJson -cne $baselineRoute) { throw 'Byte-identical duplicate changed independent route findings' }
            if ($case -ceq 'missing_route_effect') {
                $missingAdoption = @($full.Boundaries | Where-Object {
                    $_.boundaryId -ceq $candidate.Envelope.transitionId -and $_.verdict -ceq 'fail' -and
                    $_.taxonomyClass -ceq 'publication_authority_missing' -and $_.domain -ceq 'route_runtime' -and
                    $_.rawCount -eq 1 -and $_.distinctCount -eq 1 -and $_.recoveredCount -eq 0 -and $_.unresolvedCount -eq 1
                })
                if ($missingAdoption.Count -ne 1) { throw 'Missing route adoption did not retain its exact unresolved publication-authority boundary' }
            }
            if ($null -eq $boundary) { $boundary = $route.FirstUnresolvedBoundary }
            $origins = @($full.__CheckpointRepairContinuityEvidence.OriginsByKey.Values)
            if ($origins.Count -ne 1 -or $null -eq $origins[0].RetiredBoundary -or
                $origins[0].RetiredBoundary.boundaryId -cne $candidate.Envelope.transitionId) {
                throw "$case did not retire exactly the original accepted origin at the real commit"
            }
        }
        $guiOutputs = @()
        foreach ($input in @(@{ Name='full'; Lines=$fullLines }, @{ Name='compact'; Lines=@($compactRecords | ForEach-Object Line) })) {
            $path = Join-Path ([IO.Path]::GetDirectoryName($InputPath)) ($input.Name + '-retirement-gui.log')
            [IO.File]::WriteAllLines($path, [string[]]$input.Lines)
            $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
            if ($null -eq $boundary) {
                if ($null -ne $gui) { throw "$case GUI invented a boundary" }
            } elseif ($null -eq $gui -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
                $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or $gui.CoordinatorWriter -cne $boundary.writer -or
                $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or $gui.CoordinatorRawCount -ne $boundary.rawCount -or
                $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or
                $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) { throw "$case shared GUI boundary/episode metadata differs" }
            $guiOutputs += if ($null -eq $gui) { $null } else { [ordered]@{ BoundaryId=$gui.CoordinatorBoundaryId; BoundaryHash=$gui.CoordinatorBoundaryHash } }
        }
        $outputs.Add([ordered]@{ Case=$case; Verdict=$full.ArtifactEvidenceVerdict; Boundaries=$json; Route=$routeJson; Gui=$guiOutputs })
    }
    # Reducer-only hostile histories start from the independently validated real
    # graph. These copies test retirement/reuse bookkeeping, not producer authority.
    $originBoundary = @($baseline.Boundaries | Where-Object { $_.boundaryId -ceq $birth.Envelope.transitionId })[0]
    $retiredBoundary = @($baseline.Boundaries | Where-Object { $_.boundaryId -ceq $candidate.Envelope.transitionId })[0]
    $attemptBoundary = @($baseline.Boundaries | Where-Object { $_.boundaryId -ceq $validations[0].Envelope.transitionId })[0]
    foreach ($case in @('prior_retirement', 'origin_aba', 'stale_post_commit_validation', 'competing_origin')) {
        $history = @($baseline.Boundaries | ForEach-Object { Copy-Value $_ })
        if ($case -ceq 'prior_retirement') {
            $prior = @($history | Where-Object { $_.boundaryId -ceq $attemptBoundary.boundaryId })[0]
            $prior.operations = @((Copy-Value $retirement))
        } else {
            $extra = Copy-Value $(if ($case -ceq 'stale_post_commit_validation') { $attemptBoundary } else { $originBoundary })
            $extra.boundaryId += '-' + $case
            $extra.beforeStateGeneration = [long]$retiredBoundary.afterStateGeneration + 100
            $extra.afterStateGeneration = $extra.beforeStateGeneration + 1
            if ($case -ceq 'competing_origin') { $extra.beforeStateGeneration = $originBoundary.afterStateGeneration; $extra.afterStateGeneration = $extra.beforeStateGeneration }
            $history += $extra
        }
        $historyResult = Get-FileTransferCheckpointRepairContinuityEvidence -Boundaries $history -Catalog $catalog
        $reason = @($historyResult.Errors) -join ';'
        $expectedReason = if ($case -ceq 'prior_retirement') { 'retirement_unbound' }
            elseif ($case -ceq 'stale_post_commit_validation') { 'origin_unbound' } else { 'origin_reused' }
        if ($reason -cnotmatch $expectedReason) { throw "$case expected history rejection $expectedReason, got $reason" }
        "pass $case"
    }
    $binding = @($baseline.__CheckpointRepairContinuityEvidence.BindingsByReceiptKey.Values)[0]
    if ($null -eq $binding -or $null -eq $binding.Origin.RetiredBoundary) { throw 'Missing real historical repair binding and retirement poison' }
    $payload = $binding.Operation.checkpointRepairContinuity
    $stamp = @{ continuity_profile=$profile; continuity_coordinator_id=$payload.coordinatorInstanceId;
        continuity_source_fingerprint=$payload.sourceScope.scopeFingerprint; continuity_acceptance_id=$payload.acceptanceIdentity;
        continuity_accepted_state_generation=$payload.acceptedStateGeneration; continuity_origin_receipt_id=$payload.originReceiptIdentity;
        continuity_origin_receipt_fingerprint=$payload.originReceiptFingerprint; continuity_receipt_id=$binding.Operation.receiptIdentity;
        continuity_receipt_fingerprint=$binding.Operation.receiptFingerprint; continuity_current_scope_fingerprint=$payload.currentScope.scopeFingerprint;
        continuity_repair_authority_id=$payload.repair.authorityId; continuity_repair_generation=$payload.repair.decisionGeneration;
        continuity_request_id=$payload.repair.protocolRepairRequestId; continuity_correlation_id=$payload.repair.requestCorrelationId }
    if ($null -eq (Resolve-FileTransferCheckpointRepairContinuityBinding -SnapshotEvidence $baseline -SourceFields $stamp -CurrentFields $stamp) -or
        $null -ne (Resolve-FileTransferCheckpointRepairContinuityBinding -SnapshotEvidence $baseline -SourceFields $stamp -CurrentFields $stamp `
            -EvidenceUtcMilliseconds ([long]$binding.Origin.RetiredBoundary.decisionUtcMilliseconds))) {
        throw 'Historical binding control or exact post-retirement reuse rejection failed'
    }
    'checkpoint-committed-retirement-normalized=' + (ConvertTo-Json -InputObject $outputs.ToArray() -Depth 100 -Compress)
    'checkpoint-committed-retirement-evidence-ok'
    return
}
$hold = One $captured 'filetransfer_v6_frontier_request_deferred'
$recoveryCaptured = if ($DelayedDisposition) { @($captured | Where-Object { -not [string]::IsNullOrWhiteSpace((Field $_ 'continuity_receipt_id')) }) } else { $captured }
$grant = One $recoveryCaptured 'filetransfer_fallback_frontier_repair_authority_granted'
$publicationCaptured = if ($DelayedDisposition) { @($captured | Where-Object {
    (Field $_ 'route_publication_generation') -ceq (Field $hold 'route_publication_generation') -and
    (Field $_ 'runtime_generation') -ceq (Field $hold 'runtime_generation') -and
    (Field $_ 'route') -ceq (Field $hold 'route')
}) } else { $captured }
$published = One $publicationCaptured 'filetransfer_route_hint_authorized_publication_accepted'
$requestSent = One $recoveryCaptured 'filetransfer_v6_frontier_request_sent'
$forced = One $recoveryCaptured 'filetransfer_v6_fallback_frontier_repair_progress_state_forced'
$receiverState = One $recoveryCaptured 'filetransfer_v6_receiver_state_sent'
$admissionDenied = @($captured | Where-Object {
    (Name $_) -ceq 'filetransfer_runtime_unlock_admission_denied' -and
    (Field $_ 'session_id') -ceq (Field $hold 'session_id') -and
    (Field $_ 'transfer_id') -ceq (Field $hold 'transfer_id') -and
    [long](Field $_ 'log_writer_sequence') -gt [long](Field $hold 'log_writer_sequence') -and
    [long](Field $_ 'log_writer_sequence') -lt [long](Field $receiverState 'log_writer_sequence')
})
if (-not $DelayedDisposition -and $admissionDenied.Count -eq 0) { throw 'Fixture lost the real intervening admission-denied diagnostic' }
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

$dispositionMutations = [ordered]@{}
$delayedOutputs = [Collections.Generic.List[object]]::new()
if ($DelayedDisposition) {
    $disposition = One @($captured | Where-Object {
        (Field $_ 'authority_schema') -ceq '2' -and (Field $_ 'proof_boundary_kind') -ceq 'fallback_peer_proof'
    }) 'filetransfer_route_transition_disposition_applied'
    if ([long](Field $disposition 'log_writer_sequence') -le [long](Field $hold 'log_writer_sequence') -or
        [long](Field $disposition 'log_writer_sequence') -ge [long](Field $grant 'log_writer_sequence') -or
        (Field $disposition 'data_session_identity') -cne '(none)' -or
        (Field $disposition 'checkpoint_request_id') -cne '(none)' -or
        (Field $disposition 'decision_status') -cne 'Applied' -or
        (Field $disposition 'transition_kind') -cne 'TunaV4ToPostTunaFallbackV6') {
        throw 'Fixture did not preserve the real delayed fallback-disposition activation metadata collision'
    }
    $dispositionMutations = [ordered]@{
        disposition_authority = @{ authority_id='fta2-filetransfer_route_transition-forged' }
        disposition_generation = @{ transition_generation='999' }
        disposition_kind = @{ transition_kind='PostTunaFallbackV6ToTunaV4' }
        disposition_proof = @{ proof_boundary_kind='tuna_activation' }
        disposition_status = @{ decision_status='Rejected' }
        disposition_reason = @{ decision_reason='not_the_route_commit_reason' }
        disposition_target_route = @{ target_route='file_tuna_v4' }
        disposition_target_protocol = @{ target_protocol_version='4' }
        disposition_target_transport = @{ target_transport='tuna' }
        disposition_transport_epoch = @{ target_transport_epoch='999' }
        disposition_result_epoch = @{ resulting_live_route_epoch='999' }
        disposition_result_runtime = @{ resulting_runtime_generation='999' }
        disposition_result_terminal = @{ resulting_terminal_generation='999' }
        disposition_result_leg = @{ resulting_leg_generation='999' }
        disposition_result_commit = @{ resulting_route_commit_generation='999' }
        disposition_result_publication = @{ resulting_route_publication_generation='999' }
        disposition_source_data_generation = @{ source_data_session_generation='999' }
        disposition_activation_mode = @{ activation_authority_mode='responder' }
        disposition_activation_generation = @{ offer_generation='1' }
        disposition_activation_identity = @{ data_session_identity='forged-activation-data-session' }
        disposition_activation_checkpoint = @{ checkpoint_request_id='forged-activation-checkpoint' }
        disposition_explicit_current_scope = @{ runtime_generation='999' }
    }
    $dispositionAuthority = 'fte1-' + (Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
        [Text.Encoding]::UTF8.GetBytes((Field $disposition 'authority_id')))).Substring(0,24)
    $dispositionSnapshots = @($captured | Where-Object {
        if ((Name $_) -cne 'filetransfer_coordinator_transition_snapshot') { return $false }
        $value = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Field $_ 'snapshot_payload')).Value
        return $value.decisionKind -ceq 'ApplyFileTransferRouteTransitionDispositionReceipt' -and
            @($value.aggregate.operations | Where-Object {
                $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'inbound' -and $_.authorityId -ceq $dispositionAuthority
            }).Count -eq 1
    })
    if ($dispositionSnapshots.Count -ne 1) { throw 'Missing exact normal Family1 disposition receipt subject' }
    $dispositionSnapshotLine = $dispositionSnapshots[0]
    $originSnapshots = @($captured | Where-Object {
        if ((Name $_) -cne 'filetransfer_coordinator_transition_snapshot') { return $false }
        $value = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Field $_ 'snapshot_payload')).Value
        return $value.decisionKind -ceq 'FallbackCheckpointAccepted' -and
            [long]$value.aggregate.afterStateGeneration -eq [long]$operation.checkpointRepairContinuity.acceptedStateGeneration
    })
    if ($originSnapshots.Count -ne 1) { throw 'Missing exact checkpoint acceptance snapshot' }
    $originSnapshotLine = $originSnapshots[0]
    $originValue = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Field $originSnapshotLine 'snapshot_payload')).Value
    if (@($originValue.aggregate.operations | Where-Object {
        $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' -and $_.authorityId -ceq $dispositionAuthority
    }).Count -ne 1) { throw 'Checkpoint origin does not carry the exact fallback terminal subject' }
    $dispositionEffects = @($captured | Where-Object {
        (Name $_) -ceq 'filetransfer_coordinator_authority_effect' -and
        (Field $_ 'operation_authority_id') -ceq $dispositionAuthority -and
        (Field $_ 'effect_kind') -ceq 'route_transition_disposition_applied'
    })
    if ($dispositionEffects.Count -ne 1) { throw 'Missing exact normal Family1 disposition effect' }
    $dispositionEffectLine = $dispositionEffects[0]
}

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
$cases = if ($DelayedDisposition) {
    @('exact','exact_duplicate','unstamped_alias','disposition_duplicate','disposition_conflict','disposition_typed_receipt',
        'origin_terminal_missing','missing_disposition_effect',
        'missing_source_stamp','missing_repair_stamp','wrong_receipt_stamp','wrong_data_session',
        'missing_forced_state','missing_send','source_aba','current_data_session_removed','current_pause') + @($dispositionMutations.Keys)
} else { @('exact','exact_duplicate','missing_source_stamp','missing_repair_stamp','wrong_source_stamp','wrong_receipt_stamp',
    'wrong_request_stamp','wrong_data_session','missing_forced_state','missing_send','old_base_profile','old_receiver_profile',
    'unknown_profile','forged_effect','source_aba') + @($copiedStampMutations.Keys) }
foreach ($case in $cases) {
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($original in $captured) {
        $line = $original
        if ($case -ceq 'missing_disposition_effect' -and $original -ceq $dispositionEffectLine) { continue }
        if ($DelayedDisposition -and $dispositionMutations.Contains($case) -and $original -ceq $disposition) {
            foreach ($field in $dispositionMutations[$case].Keys) { $line = Set-Field $line $field $dispositionMutations[$case][$field] }
        }
        if (($case -ceq 'disposition_typed_receipt' -and $original -ceq $dispositionSnapshotLine) -or
            ($case -ceq 'origin_terminal_missing' -and $original -ceq $originSnapshotLine)) {
            $value = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Field $line 'snapshot_payload')).Value
            if ($case -ceq 'origin_terminal_missing') {
                $value.aggregate.operations = @($value.aggregate.operations | Where-Object {
                    -not ($_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' -and $_.authorityId -ceq $dispositionAuthority)
                })
            } else {
                $subject = @($value.aggregate.operations | Where-Object {
                    $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'inbound' -and $_.authorityId -ceq $dispositionAuthority
                })[0]
                $subject.receiptFingerprint = 'fte1-' + ('f' * 24)
            }
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $value -Depth 64 -Compress))
            $line = Set-Field $line 'snapshot_sha256' (Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes)
            $line = Set-Field $line 'snapshot_payload' ([Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_'))
        }
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
        if ($case -ceq 'unstamped_alias' -and $original -ceq $requestSent) {
            # Diagnostic noise only: keep the genuine primary authority tuple,
            # but give this unstamped wire alias no continuity receipt authority.
            $alias = [regex]::Replace($line, '; continuity_[^=;]+=[^;]*', '')
            foreach ($field in @('repair_request_id','protocol_repair_request_id')) {
                $alias = Set-Field $alias $field ((Field $requestSent 'repair_request_id') + ':alias')
            }
            $alias = Set-Field $alias 'request_correlation_id' ((Field $requestSent 'request_correlation_id') + '-alias')
            $alias = Set-Field $alias 'log_writer_attempt_utc_ticks' ([string]([long](Field $requestSent 'log_writer_attempt_utc_ticks') + 1))
            $lines.Add($alias)
        }
        if ($case -ceq 'disposition_duplicate' -and $original -ceq $disposition) { $lines.Add($line) }
        if ($case -ceq 'disposition_conflict' -and $original -ceq $disposition) {
            $lines.Add((Set-Field $line 'resulting_runtime_generation' '999'))
        }
        if ($case -cin @('current_data_session_removed','current_pause') -and $original -ceq $grant) {
            $poison = Set-Field $hold 'event' $(if ($case -ceq 'current_pause') { 'filetransfer_v6_receiver_state_sent' } else { 'filetransfer_data_session_removed' })
            $poison = Set-Field $poison 'log_writer_attempt_utc_ticks' ([string]([long](Field $grant 'log_writer_attempt_utc_ticks') + 1))
            if ($case -ceq 'current_pause') { $poison = Set-Field $poison 'transfer_paused' '1' }
            $lines.Add($poison)
        }
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
        if ($case -cin @('source_aba','unstamped_alias','current_data_session_removed','current_pause')) { $line = Set-Field $line 'log_writer_sequence' ([string]($i+1)) }
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
    $snapshotExpected = if ($case -cin @('old_base_profile','old_receiver_profile','unknown_profile','forged_effect','disposition_typed_receipt')) { 'fail' } else { 'pass' }
    foreach ($snapshot in @($fullSnapshots,$compactSnapshots)) {
        if ($snapshot.ArtifactEvidenceVerdict -cne $snapshotExpected) {
            throw "$case snapshot expected $snapshotExpected, got $($snapshot.ArtifactEvidenceVerdict): $($snapshot.ArtifactEvidenceFailureReason)"
        }
    }
    if ($DelayedDisposition -and $case -ceq 'exact') {
        foreach ($snapshot in @($fullSnapshots,$compactSnapshots)) {
            if (@($snapshot.Boundaries | Where-Object {
                $_.verdict -ceq 'fail' -and @($_.operations | Where-Object {
                    $_.family -ceq 'route_transition' -and $_.authorityId -ceq $dispositionAuthority
                }).Count -gt 0
            }).Count -ne 0) { throw 'Exact fixture already has a failed Family1 boundary for its fallback disposition authority' }
        }
    }
    if ($case -ceq 'missing_disposition_effect') {
        foreach ($snapshot in @($fullSnapshots,$compactSnapshots)) {
            $unresolvedRoute = @($snapshot.Boundaries | Where-Object {
                $_.verdict -ceq 'fail' -and $_.taxonomyClass -ceq 'filetransfer_route_transition_incomplete' -and
                @($_.operations | Where-Object { $_.authorityId -ceq $dispositionAuthority }).Count -gt 0
            })
            if ($unresolvedRoute.Count -eq 0) { throw 'Missing disposition effect lost its independent Family1 route failure' }
        }
    }
    $full = Retained $fullLines 'generated-full.log' $fullSnapshots
    $compact = Retained $compactLines 'regenerated-compact.log' $compactSnapshots
    $guiFull = Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $fullLines -IncludeEpisodeAudit -SnapshotEvidence $fullSnapshots
    $guiCompact = Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $compactLines -IncludeEpisodeAudit -SnapshotEvidence $compactSnapshots
    $recovered = $case -cin @('exact','exact_duplicate','unstamped_alias','disposition_duplicate','missing_disposition_effect')
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
    if ($DelayedDisposition -and $case -ceq 'exact') {
        $events = @(Parse $fullLines 'partition-control.log')
        $index = New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $events -SnapshotEvidence $fullSnapshots
        $owned = [Collections.Generic.HashSet[object]]::new()
        $actualHold = @($events | Where-Object { $_.EventName -ceq 'filetransfer_v6_frontier_request_deferred' })
        if ($actualHold.Count -ne 1) { throw 'Partition control must own only the genuine single hold' }
        [void]$owned.Add($actualHold[0])
        $transferKey = Get-FileTransferFallbackFrontierGraceTransferKey -Event $actualHold[0]
        $frontier = [long](Field $hold 'frontier_chunk_index')
        $publishedFrontier = [long](Field $receiverState 'contiguous_committed_chunk_index')
        foreach ($selection in @(
            [pscustomobject]@{ Name='outside'; Frontiers=[long[]]@($frontier) },
            [pscustomobject]@{ Name='inside'; Frontiers=[long[]]@($frontier,$publishedFrontier) })) {
            $slice = @(Get-FileTransferFallbackFrontierGracePartitionSlice -Index $index -TransferKey $transferKey -Frontiers $selection.Frontiers)
            if (@($slice | Where-Object { $_.EventName -ceq 'filetransfer_v6_receiver_state_sent' -and
                $_.Fields['log_writer_sequence'] -ceq (Field $receiverState 'log_writer_sequence') }).Count -ne 1) {
                throw "Partition $($selection.Name) lost the exact stamped receiver publication"
            }
            $result = & $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $slice -OwnedCandidateEvents $owned -SnapshotEvidence $fullSnapshots
            if ((Quartet $result) -cne '1/1/1/0' -or @($result.CandidateRecords).Count -ne 1 -or
                (Metadata $result.CandidateRecords[0]) -cne $boundary) { throw "Partition $($selection.Name) changed the actual hold's normalized recovery" }
            "pass partition_receiver_$($selection.Name)"
        }
    }
    if ($DelayedDisposition) {
        $delayedOutputs.Add([ordered]@{ Case=$case; SnapshotVerdict=$fullSnapshots.ArtifactEvidenceVerdict;
            SnapshotBoundaryHash=$fullSnapshots.BoundaryHash; Metadata=$boundary; MetadataHash=$boundaryHash; Quartet=(Quartet $full) })
    }
    "pass $case"
}
if ($DelayedDisposition) {
    'checkpoint-delayed-disposition-normalized=' + (ConvertTo-Json -InputObject $delayedOutputs.ToArray() -Depth 16 -Compress)
    'checkpoint-delayed-disposition-evidence-ok'
}
'checkpoint-continuity-evidence-ok'
