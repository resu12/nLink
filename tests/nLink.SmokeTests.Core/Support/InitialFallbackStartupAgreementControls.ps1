param([string]$RepoRoot, $Full, $Compact)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
function Copy-AgreementValue($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
$cases = @('missing_offer','duplicate_offer','missing_accept','duplicate_accept','missing_open','duplicate_open',
    'empty_accept','accept_kind','accept_status','accept_reason','operation_family','operation_role','operation_kind',
    'operation_key','authority_substitution','operation_generation','command_identity','command_generation',
    'command_fingerprint','receipt_identity','receipt_generation','receipt_status','receipt_validated','predecessor',
    'unapplied','missing_payload','sender_issuer','receiver_issuer','version','source_scope','target_scope','offer_scope',
    'session','transfer','direction','writer','coordinator','state_order','hash_gap','generation_gap','runtime_scope',
    'data_session_scope','extra_operation','exchange_operation','after_binding','open_status','open_reason','open_retirement')
foreach ($direction in @('inbound','outbound')) {
    foreach ($case in $cases) {
        $results = @()
        foreach ($source in @($Full,$Compact)) {
            $input = [pscustomobject]@{
                Mode=$source.Mode; CatalogVersion=$source.CatalogVersion
                ArtifactEvidenceVerdict=$source.ArtifactEvidenceVerdict; StreamComplete=$source.StreamComplete
                AuthoritativeFamilies=$source.AuthoritativeFamilies
                Boundaries=(Copy-AgreementValue $source.Boundaries)
                __RoutePublicationEffects=(Copy-AgreementValue $source.__RoutePublicationEffects)
            }
            $stream = @($input.Boundaries | Where-Object direction -CEQ $direction)
            $offer = @($stream | Where-Object decisionKind -CEQ 'PinFallbackBootstrapOffer')[0]
            $accept = @($stream | Where-Object decisionKind -CEQ 'ApplyFallbackBootstrapAccept')[0]
            $open = @($stream | Where-Object decisionKind -CEQ 'ApplyFallbackBootstrapSessionOpen')[0]
            $binding = @($stream | Where-Object decisionKind -CEQ 'BindInitialFallbackDataSession')[0]
            $startup = @($stream | Where-Object decisionReason -CEQ 'post_tuna_fallback_v6_route')[0]
            $operation = $accept.operations[0]
            $payload = $operation.routeTransition.bootstrapCorrelation
            switch ($case) {
                'missing_offer' { $input.Boundaries=@($input.Boundaries | Where-Object boundaryId -CNE $offer.boundaryId) }
                'missing_accept' { $input.Boundaries=@($input.Boundaries | Where-Object boundaryId -CNE $accept.boundaryId) }
                'missing_open' { $input.Boundaries=@($input.Boundaries | Where-Object boundaryId -CNE $open.boundaryId) }
                { $_ -in @('duplicate_offer','duplicate_accept','duplicate_open') } {
                    $original = if ($case -ceq 'duplicate_offer') { $offer } elseif ($case -ceq 'duplicate_accept') { $accept } else { $open }
                    $duplicate=Copy-AgreementValue $original; $duplicate.boundaryId+='-duplicate'; $input.Boundaries+=@($duplicate)
                }
                'empty_accept' { $accept.operations=@() }
                'accept_kind' { $accept.decisionKind='PinFallbackBootstrapOffer' }
                'accept_status' { $accept.decisionStatus='Rejected' }
                'accept_reason' { $accept.decisionReason='bootstrap_accept_duplicate' }
                'operation_family' { $operation.family='route_runtime' }
                'operation_role' { $operation.operationRole='fallback_bootstrap_exchange' }
                'operation_kind' { $operation.operationKind='FallbackBootstrapPhase' }
                'operation_key' { $operation.operationKey+='-foreign' }
                'authority_substitution' {
                    $operation.authorityId='fte1-111111111111111111111111'; $operation.commandIdentity=$operation.authorityId
                    $operation.operationKey="route_transition|fallback_bootstrap_agreement|$($operation.authorityId)|1|FallbackBootstrapAgreement"
                }
                'operation_generation' { $operation.operationGeneration++ }
                'command_identity' { $operation.commandIdentity='foreign' }
                'command_generation' { $operation.commandGeneration++ }
                'command_fingerprint' { $operation.commandFingerprint=$offer.operations[0].commandFingerprint; $payload.agreementFingerprint=$operation.commandFingerprint }
                'receipt_identity' { $operation.receiptIdentity='foreign' }
                'receipt_generation' { $operation.receiptGeneration=1 }
                'receipt_status' { $operation.receiptStatus='Accepted' }
                'receipt_validated' { $operation.receiptValidated=$true }
                'predecessor' { $operation.predecessorAuthorityId='foreign' }
                'unapplied' { $operation.stateApplied=$false }
                'missing_payload' { $operation.routeTransition.bootstrapCorrelation=$null }
                'sender_issuer' { $payload.senderIssuerFingerprint='fte1-111111111111111111111111' }
                'receiver_issuer' { $payload.receiverIssuerFingerprint='(none)' }
                'version' { $payload.version=0 }
                'source_scope' { $operation.routeTransition.sourceBoundary.runtimeGeneration++ }
                'target_scope' { $operation.routeTransition.targetBoundary.runtimeGeneration++ }
                'offer_scope' {
                    foreach ($step in @($offer,$accept,$open)) {
                        foreach ($op in $step.operations) {
                            $op.routeTransition.sourceBoundary.runtimeGeneration++
                            $op.routeTransition.targetBoundary.runtimeGeneration++
                        }
                    }
                }
                'session' { $accept.sessionId='foreign' }
                'transfer' { $accept.transferId='foreign' }
                'direction' { $accept.direction='foreign' }
                'writer' { $accept.writer='foreign' }
                'coordinator' { $accept.coordinatorInstanceId='foreign' }
                'state_order' { $operation.beforeState='Enabled' }
                'hash_gap' { $accept.beforeStateHash='gap' }
                'generation_gap' { $accept.afterStateGeneration++ }
                'runtime_scope' { $accept.afterBoundary.runtimeGeneration++ }
                'data_session_scope' { $accept.afterBoundary.dataSessionGeneration++ }
                'extra_operation' { $accept.operations+=@(Copy-AgreementValue $operation) }
                'exchange_operation' { $operation.operationRole='fallback_bootstrap_exchange'; $operation.operationKind='FallbackBootstrapExchange' }
                'after_binding' { $accept.beforeBoundary=Copy-AgreementValue $binding.afterBoundary; $accept.afterBoundary=Copy-AgreementValue $binding.afterBoundary }
                'open_status' { $open.decisionStatus='Rejected' }
                'open_reason' { $open.decisionReason='bootstrap_session_open_invalid' }
                'open_retirement' {
                    $open.operations+=@(Copy-AgreementValue (@($startup.operations | Where-Object { $_.operationRole -ceq 'runtime_owner' -and $_.afterState -ceq 'None' })[0]))
                }
            }
            $result=Get-FileTransferCoordinatorSnapshotRouteConsistency $input
            $index=New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries @(
                $input.Boundaries | ForEach-Object { [pscustomobject]@{ Boundary=$_ } })
            $initial=Get-FileTransferCoordinatorSnapshotInitialPublications -AggregateIndex $index -Effects $input.__RoutePublicationEffects
            $proofs=Get-FileTransferCoordinatorInitialFallbackStartupPublications -AggregateIndex $index `
                -InitialPublications $initial -Effects $input.__RoutePublicationEffects -CatalogVersion $input.CatalogVersion
            $newPublication=@($startup.operations | Where-Object { $_.operationRole -ceq 'route_publication' -and $_.afterState -ceq 'Published' })[0]
            $key="$($startup.coordinatorInstanceId)|$($startup.writer)|$($startup.boundaryId)|$($newPublication.operationKey)"
            if ($proofs.ContainsKey($key) -or $result.Verdict -cne 'fail' -or $null -eq $result.FirstUnresolvedBoundary) {
                throw "Invalid initial fallback agreement accepted: $direction/$case"
            }
            $results+=@($result)
        }
        if ((Compare-FileTransferCoordinatorSnapshotRouteConsistency $results[0] $results[1]).Verdict -cne 'pass') {
            throw "Agreement negative-control parity differs: $direction/$case"
        }
    }
}
[pscustomobject]@{ CasesPerDirection=$cases.Count; Directions=2; Consumers=2 }
