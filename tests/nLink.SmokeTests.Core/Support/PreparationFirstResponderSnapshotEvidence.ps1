param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function New-Effect($envelope, $operation, [string]$kind, [long]$sequence) {
    $scope = switch -Exact ($operation.family) {
        'responder_probe_handoff' { $operation.responderProbeHandoff.sourceBoundary }
        'route_transition' { $operation.routeTransition.sourceBoundary }
        'route_runtime' { $operation.routeRuntime.boundary }
    }
    "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($envelope.transitionId); coordinator_state_generation=$($envelope.aggregate.afterStateGeneration); operation_family=$($operation.family); operation_key=$($operation.operationKey); operation_authority_id=$($operation.authorityId); operation_generation=$($operation.operationGeneration); effect_kind=$kind; effect_identity=$kind-$($envelope.transitionId); effect_generation=$($operation.receiptGeneration); receipt_identity=$($operation.receiptIdentity); receipt_generation=$($operation.receiptGeneration); receipt_status=$($operation.receiptStatus); receipt_fingerprint=$($operation.receiptFingerprint); route=$($scope.route); protocol_version=$($scope.protocolVersion); live_route_epoch=$($scope.liveRouteEpoch); route_publication_generation=$($scope.routePublicationGeneration); route_commit_generation=$($scope.routeCommitGeneration); runtime_generation=$($scope.runtimeGeneration); terminal_generation=$($scope.terminalGeneration); leg_generation=$($scope.legGeneration); transport_epoch=$($scope.transportEpoch); bridge_recovery_generation=$($scope.bridgeRecoveryGeneration); checkpoint_generation=$($scope.checkpointGeneration); checkpoint_request_id=$($scope.checkpointRequestId); data_session_identity=$($scope.dataSessionIdentity); data_session_generation=$($scope.dataSessionGeneration); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
}
function New-Lines([object[]]$records) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($first.aggregate.beforeStateGeneration); initial_state_hash=$($first.aggregate.beforeStateFingerprint); $coverage; authority_effect=none")
    for ($i=0; $i -lt $records.Count; $i++) {
        $envelope=$records[$i]
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Depth 64 -Compress))
        $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
        $payload=[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
        $sequence=20L*($i+1)
        $lines.Add("event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($envelope.transitionId); before_state_generation=$($envelope.aggregate.beforeStateGeneration); after_state_generation=$($envelope.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence")
        foreach ($operation in $envelope.aggregate.operations) {
            $kinds=@()
            if ($operation.family -ceq 'responder_probe_handoff') {
                $kinds=@(if ($operation.operationRole -ceq 'route_transition_handoff_pending') { 'route_transition_handoff_supplied' } else { 'convergence_observed' })
            }
            elseif ($operation.family -ceq 'route_runtime' -and $operation.operationRole -ceq 'route_publication' -and $operation.afterState -ceq 'Published') {
                $kinds=@('route_publication_forwarded')
            }
            elseif ($operation.family -ceq 'route_runtime' -and $operation.operationRole -ceq 'route_commit' -and $operation.afterState -ceq 'Committed') {
                $kinds=@('route_commit_adopted','target_runtime_leg_installed')
            }
            elseif ($operation.family -ceq 'route_transition') {
                if ($envelope.decisionKind -ceq 'BeginFileTransferRouteTransitionPhysicalDrain' -and $operation.operationRole -ceq 'current') { $kinds=@('pause_installed') }
                elseif ($envelope.decisionKind -ceq 'CommitFileTransferRouteTransition') {
                    if ($operation.operationRole -ceq 'terminal') { $kinds=@('route_commit_adopted','target_runtime_leg_installed') }
                    elseif ($operation.operationRole -ceq 'inbound') { $kinds=@('completed_drain_receipt') }
                }
                elseif ($envelope.decisionKind -ceq 'ApplyFileTransferRouteTransitionDispositionReceipt' -and $operation.operationRole -ceq 'inbound') {
                    $kinds=@('route_transition_disposition_applied','transition_pause_released','recovered_epoch_publication')
                }
            }
            foreach ($kind in $kinds) { $sequence++; $lines.Add((New-Effect $envelope $operation $kind $sequence)) }
        }
    }
    $last=$records[-1]
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($records.Count); $coverage; authority_effect=none")
    $lines.ToArray()
}
function Assert-Parity([string[]]$lines, [bool]$expectFailure) {
    $full=Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated_diagnostic')+$lines)
    $compactLines=@($lines | Where-Object {
        $name=Get-FileTransferCoordinatorSnapshotEventName -Line $_
        $catalog.SnapshotAuthorityV1 -contains $name -or $catalog.GenerationStampedEffects -contains $name
    })
    $compact=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ($full.ArtifactEvidenceVerdict -cne 'pass' -or ($null -ne $full.FirstUnresolvedBoundary) -ne $expectFailure -or
        (Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact).Verdict -cne 'pass') {
        $failures=@($full.Boundaries | Where-Object verdict -ceq 'fail' | Select-Object decisionKind,taxonomyClass,rawCount,unresolvedCount)
        throw "Preparation-first contract failed (expectedFailure=$expectFailure): validation=$($full.ArtifactEvidenceFailureReason); failures=$($failures | ConvertTo-Json -Compress)"
    }
    $path=Join-Path $OutputRoot 'gui-input.log'
    [IO.File]::WriteAllLines($path,$compactLines)
    $gui=Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    if ($expectFailure) {
        $boundary=$full.FirstUnresolvedBoundary
        if ($gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
            $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
            $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) { throw 'GUI boundary differs.' }
    }
    elseif ($null -ne $gui) { throw 'GUI reopened completed responder.' }
}
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$null,[ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
$lines=@(New-Lines $envelopes)
Assert-Parity $lines $false
# Identical physical copies coalesce; a missing exact acceptance remains open.
Assert-Parity (@($lines[0]) + $lines[1..($lines.Count-2)] + $lines[1..($lines.Count-2)] + @($lines[-1])) $false
Assert-Parity (New-Lines @($envelopes[0],$envelopes[1],$envelopes[3],$envelopes[4],$envelopes[5])) $true
'Preparation-first Coordinator/evidence/full/compact/GUI contract passed.'
