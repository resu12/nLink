param([string]$RepoRoot, $Full, $Compact)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
function Copy-Value($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
function Copy-RouteInput($Source) {
    [pscustomobject]@{
        Mode=$Source.Mode; CatalogVersion=$Source.CatalogVersion
        ArtifactEvidenceVerdict=$Source.ArtifactEvidenceVerdict; StreamComplete=$Source.StreamComplete
        AuthoritativeFamilies=$Source.AuthoritativeFamilies
        Boundaries=(Copy-Value $Source.Boundaries); __RoutePublicationEffects=(Copy-Value $Source.__RoutePublicationEffects)
    }
}
$cases = @('missing_birth','missing_publication','missing_binding','missing_binding_payload','duplicate_binding',
    'duplicate_binding_operation','foreign_binding_owner','foreign_binding_publication','unvalidated_binding',
    'binding_generation_gap','binding_hash_gap','binding_scope_change','binding_session_change','binding_transfer_change',
    'binding_direction_change','binding_writer_change','binding_coordinator_change','unwitnessed_binding',
    'missing_owner_retirement','duplicate_owner_retirement','missing_publication_retirement','duplicate_publication_retirement',
    'missing_new_owner','duplicate_new_owner','missing_new_publication','duplicate_new_publication',
    'old_owner_fingerprint','old_publication_fingerprint','owner_predecessor','publication_predecessor',
    'missing_attempt','duplicate_attempt','applied_attempt','foreign_attempt_owner','foreign_attempt_command',
    'attempt_generation','attempt_scope','attempt_fingerprint','publication_receipt','publication_receipt_status',
    'unvalidated_publication','publication_command','publication_key','publication_token',
    'missing_effect','duplicate_effect','foreign_effect_receipt','foreign_effect_owner','effect_generation','effect_scope',
    'startup_rejected','startup_reason','startup_kind','startup_hash_gap','startup_generation_gap',
    'later_publication','runtime_jump','leg_jump','terminal_change','epoch_change','checkpoint_change',
    'transport_change','bridge_change','data_session_change','data_generation_change','route_change','protocol_change',
    'stale_owner_aba','stale_publication_aba','fabricated_transition','old_profile','empty_publication_identity',
    'nonmutating_retirement','nonmutating_scope_poison','restored_scope_poison',
    'fallback_protocol4_coherent','tuna_v6_coherent')
foreach ($direction in @('inbound','outbound')) {
    foreach ($case in $cases) {
        $results = @()
        foreach ($source in @($Full,$Compact)) {
            $input = Copy-RouteInput $source
            $subject = @($input.Boundaries | Where-Object {
                $_.direction -ceq $direction -and $_.decisionReason -ceq 'post_tuna_fallback_v6_route'
            })[0]
            $binding = @($input.Boundaries | Where-Object { $_.direction -ceq $direction -and $_.decisionKind -ceq 'BindInitialFallbackDataSession' })[0]
            $publication = @($input.Boundaries | Where-Object { $_.direction -ceq $direction -and $_.decisionKind -ceq 'RoutePublication' })[0]
            $birth = @($input.Boundaries | Where-Object { $_.direction -ceq $direction -and $_.decisionKind -ceq 'StartTransfer' -and $_.beforeBoundary.runtimeGeneration -eq 0 })[0]
            $oldOwner = @($subject.operations | Where-Object { $_.operationRole -ceq 'runtime_owner' -and $_.beforeState -ceq 'Current' })[0]
            $newOwner = @($subject.operations | Where-Object { $_.operationRole -ceq 'runtime_owner' -and $_.beforeState -ceq 'None' })[0]
            $oldPub = @($subject.operations | Where-Object { $_.operationRole -ceq 'route_publication' -and $_.beforeState -ceq 'Published' })[0]
            $newPub = @($subject.operations | Where-Object { $_.operationRole -ceq 'route_publication' -and $_.beforeState -ceq 'None' })[0]
            $attempt = @($subject.operations | Where-Object operationKind -CEQ 'StartTransfer')[0]
            $effect = @($input.__RoutePublicationEffects | Where-Object TransitionId -CEQ $subject.boundaryId)[0]
            switch ($case) {
                'missing_birth' { $input.Boundaries = @($input.Boundaries | Where-Object boundaryId -CNE $birth.boundaryId) }
                'missing_publication' { $input.Boundaries = @($input.Boundaries | Where-Object boundaryId -CNE $publication.boundaryId) }
                'missing_binding' { $input.Boundaries = @($input.Boundaries | Where-Object boundaryId -CNE $binding.boundaryId) }
                'missing_binding_payload' { $binding.operations[0].initialDataSessionBinding = $null }
                'duplicate_binding' { $duplicate=Copy-Value $binding; $duplicate.boundaryId+='-duplicate'; $input.Boundaries+=@($duplicate) }
                'duplicate_binding_operation' { $binding.operations+=@(Copy-Value $binding.operations[0]) }
                'foreign_binding_owner' { $binding.operations[0].initialDataSessionBinding.runtimeOwnerFingerprint='foreign' }
                'foreign_binding_publication' { $binding.operations[0].initialDataSessionBinding.publicationFingerprint='foreign' }
                'unvalidated_binding' { $binding.operations[0].receiptValidated=$false }
                'binding_generation_gap' { $binding.afterStateGeneration++ }
                'binding_hash_gap' { $binding.beforeStateHash='gap' }
                'binding_scope_change' { $binding.afterBoundary.runtimeGeneration++ }
                'binding_session_change' { $binding.sessionId='foreign' }
                'binding_transfer_change' { $binding.transferId='foreign' }
                'binding_direction_change' { $binding.direction='foreign' }
                'binding_writer_change' { $binding.writer='foreign' }
                'binding_coordinator_change' { $binding.coordinatorInstanceId='foreign' }
                'unwitnessed_binding' { $binding.operations=@(); $binding.decisionKind='UnwitnessedAttachment' }
                'missing_owner_retirement' { $subject.operations=@($subject.operations | Where-Object { $_ -ne $oldOwner }) }
                'duplicate_owner_retirement' { $subject.operations+=@(Copy-Value $oldOwner) }
                'missing_publication_retirement' { $subject.operations=@($subject.operations | Where-Object { $_ -ne $oldPub }) }
                'duplicate_publication_retirement' { $subject.operations+=@(Copy-Value $oldPub) }
                'missing_new_owner' { $subject.operations=@($subject.operations | Where-Object { $_ -ne $newOwner }) }
                'duplicate_new_owner' { $subject.operations+=@(Copy-Value $newOwner) }
                'missing_new_publication' { $newPub.afterState='None' }
                'duplicate_new_publication' { $subject.operations+=@(Copy-Value $newPub) }
                'old_owner_fingerprint' { $oldOwner.commandFingerprint='foreign'; $oldOwner.routeRuntime.tokenFingerprint='foreign' }
                'old_publication_fingerprint' { $oldPub.commandFingerprint='foreign'; $oldPub.receiptFingerprint='foreign'; $oldPub.routeRuntime.tokenFingerprint='foreign' }
                'owner_predecessor' { $newOwner.predecessorAuthorityId=$oldOwner.authorityId }
                'publication_predecessor' { $newPub.predecessorAuthorityId=$oldPub.authorityId }
                'missing_attempt' { $subject.operations=@($subject.operations | Where-Object { $_ -ne $attempt }) }
                'duplicate_attempt' { $subject.operations+=@(Copy-Value $attempt) }
                'applied_attempt' { $attempt.stateApplied=$true }
                'foreign_attempt_owner' { $attempt.authorityId='foreign' }
                'foreign_attempt_command' { $attempt.commandIdentity='foreign' }
                'attempt_generation' { $attempt.commandGeneration++ }
                'attempt_scope' { $attempt.routeRuntime.boundary.dataSessionGeneration++ }
                'attempt_fingerprint' { $attempt.routeRuntime.tokenFingerprint='foreign' }
                'publication_receipt' { $newPub.receiptIdentity=$oldPub.receiptIdentity }
                'publication_receipt_status' { $newPub.receiptStatus='Rejected' }
                'unvalidated_publication' { $newPub.receiptValidated=$false }
                'publication_command' { $newPub.commandIdentity='foreign' }
                'publication_key' { $newPub.operationKey+='-foreign'; $effect.OperationKey=$newPub.operationKey }
                'publication_token' { $newPub.routeRuntime.tokenFingerprint='foreign' }
                'missing_effect' { $input.__RoutePublicationEffects=@($input.__RoutePublicationEffects | Where-Object TransitionId -CNE $subject.boundaryId) }
                'duplicate_effect' { $input.__RoutePublicationEffects+=@(Copy-Value $effect) }
                'foreign_effect_receipt' { $effect.ReceiptFingerprint='foreign' }
                'foreign_effect_owner' { $effect.AuthorityId='foreign' }
                'effect_generation' { $effect.StateGeneration++ }
                'effect_scope' { $effect.DataSessionGeneration=[long]$effect.DataSessionGeneration + 1L }
                'startup_rejected' { $subject.decisionStatus='Rejected' }
                'startup_reason' { $subject.decisionReason='state_refresh_received' }
                'startup_kind' { $subject.decisionKind='RoutePublication' }
                'startup_hash_gap' { $subject.beforeStateHash='gap' }
                'startup_generation_gap' { $subject.afterStateGeneration++ }
                'later_publication' { $subject.beforeBoundary.routePublicationGeneration++; $subject.afterBoundary.routePublicationGeneration++ }
                'runtime_jump' { $subject.afterBoundary.runtimeGeneration++ }
                'leg_jump' { $subject.afterBoundary.legGeneration++ }
                'terminal_change' { $subject.afterBoundary.terminalGeneration++ }
                'epoch_change' { $subject.afterBoundary.liveRouteEpoch++ }
                'checkpoint_change' { $subject.afterBoundary.checkpointGeneration++ }
                'transport_change' { $subject.afterBoundary.transportEpoch++ }
                'bridge_change' { $subject.afterBoundary.bridgeRecoveryGeneration++ }
                'data_session_change' { $subject.afterBoundary.dataSessionIdentity='foreign' }
                'data_generation_change' { $subject.afterBoundary.dataSessionGeneration++ }
                'route_change' { $subject.afterBoundary.route='file_tuna_v4' }
                'protocol_change' { $subject.afterBoundary.protocolVersion=4 }
                'stale_owner_aba' { $newOwner.authorityId=$oldOwner.authorityId }
                'stale_publication_aba' { $newPub.authorityId=$oldPub.authorityId }
                'fabricated_transition' { $fake=Copy-Value $attempt; $fake.family='route_transition'; $subject.operations+=@($fake) }
                'old_profile' { $input.CatalogVersion=$script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion }
                'empty_publication_identity' {
                    $newPub.authorityId='(none)'; $newPub.commandIdentity='(none)'; $newPub.receiptIdentity='(none)'
                    $newPub.operationKey='route_runtime|route_publication|(none)|2|RoutePublication'
                    $effect.AuthorityId='(none)'; $effect.ReceiptIdentity='(none)'; $effect.OperationKey=$newPub.operationKey
                }
                { $_ -in @('nonmutating_retirement','nonmutating_scope_poison','restored_scope_poison') } {
                    $poison=Copy-Value $binding; $poison.boundaryId+='-poison'; $poison.decisionKind='HostileObservation'
                    $poison.decisionStatus='Idempotent'; $poison.beforeBoundary=Copy-Value $binding.afterBoundary
                    $poison.beforeStateGeneration=$binding.afterStateGeneration; $poison.beforeStateHash=$binding.afterStateHash
                    $poison.operations=@()
                    if ($case -ceq 'nonmutating_retirement') { $poison.operations=@(Copy-Value $oldOwner) }
                    else { $poison.afterBoundary.dataSessionIdentity='foreign' }
                    if ($case -ceq 'restored_scope_poison') {
                        $restore=Copy-Value $poison; $restore.boundaryId+='-restore'; $restore.beforeBoundary=Copy-Value $poison.afterBoundary
                        $restore.afterBoundary=Copy-Value $binding.afterBoundary; $input.Boundaries+=@($restore)
                    }
                    $input.Boundaries+=@($poison)
                }
                { $_ -in @('fallback_protocol4_coherent','tuna_v6_coherent') } {
                    foreach ($boundary in @($input.Boundaries | Where-Object direction -CEQ $direction)) {
                        $tuples=@($boundary.beforeBoundary,$boundary.afterBoundary)
                        foreach ($op in $boundary.operations) {
                            if ($null -ne $op.routeRuntime) { $tuples+=@($op.routeRuntime.boundary) }
                            if (Test-FileTransferCoordinatorInitialDataSessionMarker $op) {
                                $tuples+=@($op.initialDataSessionBinding.sourceBoundary,$op.initialDataSessionBinding.effectiveBoundary)
                            }
                        }
                        foreach ($tuple in $tuples) {
                            if ($case -ceq 'fallback_protocol4_coherent') { $tuple.protocolVersion=4 }
                            else { $tuple.route='file_tuna_v6' }
                        }
                    }
                    foreach ($item in @($input.__RoutePublicationEffects | Where-Object CoordinatorInstanceId -CEQ $subject.coordinatorInstanceId)) {
                        if ($case -ceq 'fallback_protocol4_coherent') { $item.ProtocolVersion=4 } else { $item.Route='file_tuna_v6' }
                    }
                }
            }
            $result=Get-FileTransferCoordinatorSnapshotRouteConsistency $input
            $index=New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries @(
                $input.Boundaries | ForEach-Object { [pscustomobject]@{ Boundary=$_ } })
            $initial=Get-FileTransferCoordinatorSnapshotInitialPublications -AggregateIndex $index -Effects $input.__RoutePublicationEffects
            $epochZero=Get-FileTransferCoordinatorInitialFallbackStartupPublications -AggregateIndex $index `
                -InitialPublications $initial -Effects $input.__RoutePublicationEffects -CatalogVersion $input.CatalogVersion
            $key="$($subject.coordinatorInstanceId)|$($subject.writer)|$($subject.boundaryId)|$($newPub.operationKey)"
            if ($epochZero.ContainsKey($key)) { throw "Invalid epoch-zero successor proof accepted: $direction/$case" }
            # A missing publication must not disappear into a route-pass result.
            if ($result.Verdict -cne 'fail' -or $null -eq $result.FirstUnresolvedBoundary) {
                throw "Invalid initial startup accepted: $direction/$case"
            }
            $results+=@($result)
        }
        if ((Compare-FileTransferCoordinatorSnapshotRouteConsistency $results[0] $results[1]).Verdict -cne 'pass') {
            throw "Negative-control full/compact parity differs: $direction/$case"
        }
    }
}
[pscustomobject]@{ CasesPerDirection=$cases.Count; Directions=2; Consumers=2 }
