param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
$fixture = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$envelopes = @($fixture.projected)
$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function Import-TestFunctions([string]$path, [string[]]$names, [string]$scopeFunction = '') {
    $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
    if ($scopeFunction) {
        $scopes = @($ast.FindAll({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $scopeFunction
        }, $true))
        if ($scopes.Count -ne 1) { throw "Scope not unique: $scopeFunction" }
        $ast = $scopes[0].Body
    }
    foreach ($name in $names) {
        $definitions = @($ast.FindAll({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
        }, $true))
        if ($scopeFunction) {
            $definitions = @($definitions | Where-Object {
                $parent = $_.Parent
                while ($null -ne $parent -and $parent -isnot [Management.Automation.Language.FunctionDefinitionAst]) { $parent = $parent.Parent }
                $null -ne $parent -and $parent.Name -ceq $scopeFunction
            })
        }
        if ($definitions.Count -ne 1) { throw "Function not unique: $name" }
        . ([scriptblock]::Create($definitions[0].Extent.Text.Replace("function $name", "function script:$name")))
    }
}
Import-TestFunctions (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/ProofPublicationCancellationSnapshotEvidence.ps1') @('Copy-Value','Find-Operation','New-Effect','New-Lines')
$lines = @(New-Lines $envelopes)
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
$compactLines = @($lines | Where-Object {
    $name = Get-FileTransferCoordinatorSnapshotEventName $_
    $catalog.SnapshotAuthorityV1 -contains $name -or $catalog.GenerationStampedEffects -contains $name
})
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
if ($full.ArtifactEvidenceVerdict -cne 'pass' -or $full.__HeldPublicationProofClosures.Count -ne 1 -or
    (Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact).Verdict -cne 'pass') {
    throw "Generated held closure failed: $($full | Select-Object * -ExcludeProperty Boundaries | ConvertTo-Json -Depth 12 -Compress)"
}
$closure = $full.__HeldPublicationProofClosures[0]
$rawRoot = Find-Operation $fixture.raw[-1] 'operation_current'
$source = $rawRoot.receiveRecovery.sourceBoundary
$authority = $rawRoot.authorityId
$scope = "session_id=fixture-session; transfer_id=fixture-transfer; direction=$($first.direction); route=$($source.route); protocol_version=$($source.protocolVersion); live_route_epoch=$($source.liveRouteEpoch); route_publication_generation=$($source.routePublicationGeneration); route_commit_generation=$($source.routeCommitGeneration); runtime_generation=$($source.runtimeGeneration); terminal_generation=$($source.terminalGeneration); leg_generation=$($source.legGeneration); transport_epoch=$($source.transportEpoch); bridge_recovery_generation=$($source.bridgeRecoveryGeneration); checkpoint_request_id=$($source.checkpointRequestId); data_session_identity=fixture-data; data_session_generation=$($source.dataSessionGeneration); closure_generation=$($closure.ClosureGeneration); log_writer_id=fixture-writer"
$operation = "receive_recovery_operation_authority_id=$authority; receive_recovery_operation_generation=$($closure.OperationGeneration)"
$publication = "recovery_operation_authority_id=$authority; recovery_operation_generation=$($closure.OperationGeneration); publication_generation=2; predecessor_publication_generation=1; predecessor_frame_fingerprint=published-state; predecessor_receipt_observed_utc_ms=350"
$recheck = "event=filetransfer_receive_recovery_proof_state_publication_recheck_decided; $scope; $publication; heartbeat_generation=1; heartbeat_sequence=19; proof_kind=heartbeat_received; lane=control; observed_utc_ms=400; decision_status=Accepted; log_writer_sequence=61"
$recheckTuple = 'publication_attempt=post_delivery_recheck; recheck_heartbeat_generation=1; recheck_heartbeat_sequence=19; recheck_proof_kind=heartbeat_received; recheck_lane=control'
$held = "event=filetransfer_receive_recovery_proof_state_publication_receipt_applied; $scope; $publication; $recheckTuple; publication_outcome=Held; publication_reason=filetransfer_receive_recovery_proof_state_held_for_tuna_activation_pause; decision_status=Accepted; log_writer_sequence=72"
$invalidated = "event=filetransfer_receive_recovery_proof_state_publication_invalidated; $scope; $publication; $recheckTuple; invalidation_target=pending_command; decision_reason=coordinator_receive_recovery_transition; log_writer_sequence=82"
$proof = "event=filetransfer_receive_recovery_operation_proof_observed; $scope; $operation; closure_state=Adopted; proof_identity=proof-cancels-arm; proof_observed_utc_ms=475; log_writer_sequence=83"
$adopted = $proof.Replace('operation_proof_observed','operation_closure_adopted').Replace('log_writer_sequence=83','log_writer_sequence=84')
$diagnostics = @($recheck,$held,$invalidated,$proof,$adopted)
$targetScope = $scope.Replace('direction=inbound','direction=outbound').Replace('log_writer_id=fixture-writer','log_writer_id=target-writer')
$targetOperation = 'receive_recovery_operation_authority_id=target-authority; receive_recovery_operation_generation=1; bridge_decision_generation=1'
$episode = @(
    "event=filetransfer_receive_recovery_operation_issued; $targetScope; $targetOperation",
    "event=filetransfer_receive_recovery_operation_issued; $scope; $operation; bridge_decision_generation=$($closure.OperationGeneration)",
    "event=filetransfer_receive_recovery_operation_execution_receipt_applied; $scope; $operation; execution_outcome=Completed; closure_state=Retained; proof_identity=(none)",
    "event=filetransfer_receive_recovery_operation_closure_retained; $scope; $operation; execution_outcome=Completed; closure_state=Retained; proof_identity=(none)",
    "event=filetransfer_receive_recovery_proof_state_publication_decided; $scope; recovery_operation_authority_id=$authority; recovery_operation_generation=$($closure.OperationGeneration); publication_generation=1; decision_status=Accepted",
    "event=filetransfer_v4_state_sent; $scope; $operation; repair_publication_kind=receive_recovery_proof_state; publication_generation=1; epoch=225; v4_state_wire_fingerprint=published-state",
    "event=filetransfer_receive_recovery_proof_state_publication_receipt_applied; $scope; recovery_operation_authority_id=$authority; recovery_operation_generation=$($closure.OperationGeneration); publication_generation=1; publication_outcome=Delivered; decision_status=Accepted; v4_state_wire_fingerprint=published-state",
    "event=filetransfer_receive_recovery_operation_execution_receipt_applied; $targetScope; $targetOperation; execution_outcome=Completed; closure_state=Retained; proof_identity=(none)",
    "event=filetransfer_receive_recovery_operation_closure_retained; $targetScope; $targetOperation; execution_outcome=Completed; closure_state=Retained; proof_identity=(none)",
    'event=session_liveness_heartbeat_received; session_id=fixture-session; generation=1; sequence=19; lane=control; log_writer_id=fixture-writer',
    'event=session_liveness_proof_received; session_id=fixture-session; generation=1; sequence=19; proof_kind=heartbeat_received; lane=control; observed_utc_ms=400; log_writer_id=fixture-writer; log_writer_sequence=59'
) + $diagnostics
Import-TestFunctions (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1') @('Get-FileTransferEventField')
Import-TestFunctions (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1') @('Get-FileTransferReceiveRecoveryCompletedRetainedProofStatePublicationCorrelation')
$events = @(for ($i=0; $i -lt $episode.Count; $i++) {
    $fields = @{}
    foreach ($match in [regex]::Matches($episode[$i], '(?:^|;\s*)([a-z0-9_]+)=([^;]*)')) { $fields[$match.Groups[1].Value] = $match.Groups[2].Value.Trim() }
    [pscustomobject]@{ EventName=$fields.event; RawLine=$episode[$i]; Sequence=$i+1; LineNumber=$i+1; Fields=$fields; FilePath='fixture.log' }
})
$legacy = Get-FileTransferReceiveRecoveryCompletedRetainedProofStatePublicationCorrelation -Events $events
if ($legacy.UnresolvedCount -ne 1) { throw "Missing-delivery legacy control changed: $($legacy | ConvertTo-Json -Depth 6 -Compress)" }
foreach ($evaluation in @($full,$compact)) {
    $result = Get-FileTransferReceiveRecoveryCompletedRetainedProofStatePublicationCorrelation -Events $events -CoordinatorSnapshotEvaluation $evaluation
    if ($result.RawCount -ne 1 -or $result.DistinctCount -ne 1 -or $result.RecoveredCount -ne 1 -or $result.UnresolvedCount -ne 0) {
        throw "Retained cancellation quartet incorrect: $($result | ConvertTo-Json -Depth 6 -Compress)"
    }
}
Import-TestFunctions (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1') @(
    'Get-TunaGuiLogLineFieldValue','Test-TunaGuiLogLineHasEvent',
    'Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure') 'Get-TunaGuiFileTransferSetupFailureClassification'
if (-not (Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure -Lines $episode)) { throw 'GUI missing-delivery control changed.' }
$guiIndex = New-FileTransferHeldPublicationNaturalProofRecoveryIndex -Evaluation $compact -Lines $episode
if (Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure -Lines $episode -NaturalProofRecoveryIndex $guiIndex) { throw 'GUI missed natural proof closure.' }
foreach ($evaluation in @($full,$compact)) {
    $index = New-FileTransferHeldPublicationNaturalProofRecoveryIndex -Evaluation $evaluation -Lines $diagnostics
    if ($index.Count -ne 1 -or -not (Test-FileTransferHeldPublicationNaturalProofRecovery $index $authority $recheck)) { throw 'Exact proof closure did not recover.' }
    if (Test-FileTransferHeldPublicationNaturalProofRecovery $index 'foreign-authority' $recheck) { throw 'Wrong authority recovered.' }
}
# Diagnostic mutations cannot borrow a validated closure from another operation.
foreach ($bad in @(
    @($recheck,$held,$invalidated,$proof),
    @($recheck,$held,$proof,$adopted),
    @($recheck,$held.Replace('publication_generation=2','publication_generation=3'),$invalidated,$proof,$adopted),
    @($recheck,$held,$invalidated,$proof.Replace('proof-cancels-arm','foreign-proof'),$adopted),
    @($recheck,$held,$invalidated,$proof,$adopted.Replace('proof_observed_utc_ms=475','proof_observed_utc_ms=476')),
    @($recheck,$held,$invalidated,$proof,$adopted.Replace('data_session_identity=fixture-data','data_session_identity=foreign-data')),
    @($recheck,$held,$invalidated,$proof,$adopted,$adopted.Replace('log_writer_sequence=84','log_writer_sequence=85')),
    @($recheck,$held,$invalidated,$proof,$adopted.Replace('proof_observed_utc_ms=475;','')),
    @($recheck,$held,$invalidated,$proof,$adopted.Replace('log_writer_sequence=84','log_writer_sequence=invalid')),
    @($recheck,$held,$invalidated,$proof,$adopted.Replace('route=regular_nkn_v4_fast','route=file_tuna_v4'))
)) {
    if ((New-FileTransferHeldPublicationNaturalProofRecoveryIndex -Evaluation $full -Lines $bad).Count -ne 0) { throw 'Malformed diagnostics recovered.' }
}
if ((New-FileTransferHeldPublicationNaturalProofRecoveryIndex -Evaluation $full -Lines ($diagnostics + $diagnostics)).Count -ne 1) { throw 'Byte-identical duplicates changed recovery.' }
$missingEffect = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (New-Lines $envelopes $false)
if ($missingEffect.ArtifactEvidenceVerdict -cne 'fail' -or
    (New-FileTransferHeldPublicationNaturalProofRecoveryIndex -Evaluation $missingEffect -Lines $diagnostics).Count) { throw 'Missing effect recovered.' }
foreach ($mutation in @(
    { param($e) (Find-Operation $e 'operation_current').receiveRecovery.proofObservedUtcMs++ },
    { param($e) (Find-Operation $e 'proof_publication_pending').receiveRecovery.sourceBoundary.dataSessionGeneration++ },
    { param($e) (Find-Operation $e 'proof_publication_pending').commandFingerprint = 'foreign-command' },
    { param($e) (Find-Operation $e 'proof_publication_pending').receiptFingerprint = 'foreign-receipt' }
)) {
    $changed = Copy-Value $envelopes
    & $mutation $changed[-1]
    $evaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (New-Lines $changed)
    if ((New-FileTransferHeldPublicationNaturalProofRecoveryIndex -Evaluation $evaluation -Lines $diagnostics).Count) { throw 'Mutated snapshot closure recovered.' }
}
$open = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (New-Lines $envelopes[0..6])
if ((New-FileTransferHeldPublicationNaturalProofRecoveryIndex -Evaluation $open -Lines $diagnostics).Count) { throw 'Held without proof recovered.' }
Import-TestFunctions (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1') @(
    'New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification',
    'Get-TunaGuiFileTransferSetupFailureClassification')
$guiPath = Join-Path $OutputRoot 'gui-evidence.log'
# The whole GUI classifier requires the real route-publication prefix as well as the
# recovery-only history used by the exact closure/mutation controls above.
$guiEnvelopes = @($fixture.gui)
$first = $guiEnvelopes[0]
Import-TestFunctions (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FileTunaPressureSettlementSnapshotEvidence.ps1') @('New-Effect','New-Lines')
$byId = @{}
foreach ($envelope in $guiEnvelopes) { $byId[$envelope.transitionId] = $envelope }
$guiLines = @(foreach ($line in @(New-Lines $guiEnvelopes)) {
    $line
    if ((Get-FileTransferCoordinatorSnapshotEventName $line) -cne 'filetransfer_coordinator_transition_snapshot') { continue }
    $envelope = $byId[(Get-FileTransferCoordinatorSnapshotLineField $line transition_id)]
    $kind = switch -Exact ($envelope.decisionKind) {
        'ObserveReceiveRecoveryExecutionReceipt' { 'execution_receipt_observed' }
        'ObserveReceiveRecoveryProof' { 'receive_proof_observed' }
        'ApplyReceiveRecoveryProofStatePublicationReceipt' {
            if (@($envelope.aggregate.operations | Where-Object {
                $_.operationRole -ceq 'proof_publication_terminal' -and $_.afterState -ceq 'Delivered'
            }).Count -eq 1) { 'proof_publication_delivered' }
        }
    }
    if ($kind) {
        $role = if ($kind -ceq 'proof_publication_delivered') { 'proof_publication_terminal' } else { 'operation_current' }
        $sequence = [long](Get-FileTransferCoordinatorSnapshotLineField $line log_writer_sequence)
        New-Effect $envelope (Find-Operation $envelope $role) $kind ($sequence + 1)
    }
})
# Generate diagnostic positions from this complete fixture's final proof snapshot.
$proofSequence = 20L * $guiEnvelopes.Count
$diagnosticSequences = @(($proofSequence - 39), ($proofSequence - 18), ($proofSequence + 2), ($proofSequence + 3), ($proofSequence + 4))
$guiDiagnostics = @(for ($i = 0; $i -lt $diagnostics.Count; $i++) {
    [regex]::Replace($diagnostics[$i], 'log_writer_sequence=\d+', "log_writer_sequence=$($diagnosticSequences[$i])")
})
[IO.File]::WriteAllLines($guiPath, [string[]]($guiLines + $guiDiagnostics))
$gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'diagnostic evidence evaluation' `
    -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($guiPath)
if ($null -ne $gui) {
    $route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation (Get-FileTransferCoordinatorSnapshotEvaluation -Lines $guiLines)
    throw "GUI reopened exact closure; route findings=$($route.Findings -join ' | '): $($gui | ConvertTo-Json -Depth 8 -Compress)"
}
Write-Output 'Held publication exact proof cancellation, malformed controls, full/compact/GUI parity passed.'
