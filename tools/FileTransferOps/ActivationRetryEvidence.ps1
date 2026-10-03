Set-StrictMode -Version Latest

$script:FileTransferCoordinatorActivationRetryCatalogVersion =
    'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1-source-callback-v1-repair-owner-v1-activation-retry-v1'
$script:FileTransferCoordinatorActivationRetryCatalogHash = '6f8667e92dae781e679bbe6f59fbd4f3d6284cd893ad2f643a9e47a21d451b00'
$script:FileTransferCoordinatorActivationRetryCatalog = [ordered]@{
    version=1; family='route_transition'; payloadName='activationRetryBinding'
    transitionKind='PostTunaFallbackV6ToTunaV4'; proofBoundaryKind='FileTransferTunaActivationTransitionProofBoundary'
    payloadFields=[ordered]@{
        retryAuthorityId='string'; retryAuthorityGeneration='int64'; installGeneration='int64'
        installReceiptFingerprint='string'; pairedReleaseAuthorityId='string'; pairedReleaseGeneration='int64'
    }
    binding='exact-effective-install-consumed-release-ancestry-v1'; physicalAuthority=$false
}

function Get-FileTransferCoordinatorActivationRetryCatalog {
    $parent=Get-FileTransferCoordinatorRepairOwnerCatalog
    $json=ConvertTo-Json -InputObject $script:FileTransferCoordinatorActivationRetryCatalog -Depth 16 -Compress
    $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.UTF8Encoding]::new($false).GetBytes(
        "parentHash=$($parent.Hash)`nActivationRetryCatalog=$json"))
    if ($hash -cne $script:FileTransferCoordinatorActivationRetryCatalogHash) { throw "activation_retry_catalog_hash_invalid:$hash" }
    $result=[ordered]@{}
    foreach ($property in $parent.PSObject.Properties) { $result[$property.Name]=$property.Value }
    $result.Version=$script:FileTransferCoordinatorActivationRetryCatalogVersion
    $result.Hash=$hash; $result.ParentCatalogHash=$parent.Hash
    $result.ActivationRetryCatalog=$script:FileTransferCoordinatorActivationRetryCatalog
    [pscustomobject]$result
}

function Test-FileTransferActivationRetryOperation {
    param($Operation,$Catalog)
    $t=Get-FileTransferCoordinatorSnapshotObjectProperty $Operation 'routeTransition'
    $p=Get-FileTransferCoordinatorSnapshotObjectProperty $t 'activationRetryBinding'
    if ($null -eq $p) { return $true }
    if ($Catalog.Version -cnotin @($script:FileTransferCoordinatorActivationRetryCatalogVersion, $script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion, $script:FileTransferCoordinatorInitialDataSessionCatalogVersion, $script:FileTransferCoordinatorEarlyCheckpointProofCatalogVersion, $script:FileTransferCoordinatorFallbackRepairHandoffCatalogVersion) -or
        $Operation.family -cne 'route_transition' -or $t.transitionKind -cne 'PostTunaFallbackV6ToTunaV4' -or
        $t.proofBoundaryKind -cne 'FileTransferTunaActivationTransitionProofBoundary' -or
        -not (Test-FileTransferCheckpointRepairContinuityFields $p $script:FileTransferCoordinatorActivationRetryCatalog.payloadFields)) { return $false }
    foreach ($name in @('retryAuthorityGeneration','installGeneration','pairedReleaseGeneration')) {
        if ($p.$name -le 0) { return $false }
    }
    return $p.retryAuthorityId -cmatch '^(?:fta2-runtime_unlock_retry_dispatch-|fte1-)[0-9a-f]{24}$' -and
        $p.pairedReleaseAuthorityId -cmatch '^(?:fta2-fallback_tail_yield-|fte1-)[0-9a-f]{24}$' -and
        $p.installReceiptFingerprint -cmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$'
}

function Get-FileTransferActivationRetryEvidence {
    param([AllowEmptyCollection()][object[]]$Boundaries,$Catalog)
    $bindings=@{}; $errors=[System.Collections.Generic.List[string]]::new()
    if ($Catalog.Version -cnotin @($script:FileTransferCoordinatorActivationRetryCatalogVersion, $script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion, $script:FileTransferCoordinatorInitialDataSessionCatalogVersion, $script:FileTransferCoordinatorEarlyCheckpointProofCatalogVersion, $script:FileTransferCoordinatorFallbackRepairHandoffCatalogVersion)) {
        return [pscustomobject]@{ CatalogVersion=$Catalog.Version; Bindings=$bindings; Errors=@() }
    }
    function SameStream($a,$b) {
        foreach ($name in @('coordinatorInstanceId','writer','sessionId','transferId','direction')) {
            if ([string]$a.$name -cne [string]$b.$name) { return $false }
        }
        return $true
    }
    foreach ($start in $Boundaries) {
        if ($start.decisionKind -cne 'PrepareFileTransferRouteTransition' -or $start.decisionStatus -cne 'Prepared') { continue }
        foreach ($operation in @($start.operations | Where-Object {
            $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'current' -and
            $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Prepared' -and
            $_.routeTransition.transitionKind -ceq 'PostTunaFallbackV6ToTunaV4'
        })) {
            $key="$($start.coordinatorInstanceId)|$($operation.authorityId)|$($operation.operationGeneration)"
            $failure='snapshot_activation_retry_binding_invalid'
            try {
                $stream=@($Boundaries | Where-Object { SameStream $_ $start })
                $history=@(foreach ($b in $stream) {
                    foreach ($o in $b.operations) {
                        if ($o.family -ceq 'route_transition' -and $o.authorityId -ceq $operation.authorityId -and
                            $o.operationGeneration -eq $operation.operationGeneration -and $o.operationRole -cin @('current','terminal')) {
                            [pscustomobject]@{Boundary=$b;Operation=$o}
                        }
                    }
                })
                $responders=@($start.operations | Where-Object {
                    $_.family -ceq 'responder_probe_handoff' -and $_.operationRole -ceq 'route_transition_handoff_pending' -and
                    $_.responderProbeHandoff.linkedTransitionAuthorityId -ceq $operation.authorityId -and
                    $_.responderProbeHandoff.linkedTransitionGeneration -eq $operation.operationGeneration -and $_.receiptValidated
                })
                $p=Get-FileTransferCoordinatorSnapshotObjectProperty $operation.routeTransition 'activationRetryBinding'
                if ($responders.Count -eq 1) {
                    if (@($history | Where-Object { $null -ne (Get-FileTransferCoordinatorSnapshotObjectProperty $_.Operation.routeTransition 'activationRetryBinding') }).Count) {
                        throw 'responder_with_initiator_binding'
                    }
                    continue
                }
                if ($responders.Count -ne 0 -or $null -eq $p -or -not (Test-FileTransferActivationRetryOperation $operation $Catalog)) { throw 'binding_missing' }
                $signature=ConvertTo-Json $p -Compress
                foreach ($entry in $history) {
                    $candidate=Get-FileTransferCoordinatorSnapshotObjectProperty $entry.Operation.routeTransition 'activationRetryBinding'
                    if ($null -eq $candidate -or (ConvertTo-Json $candidate -Compress) -cne $signature -or
                        $entry.Operation.routeTransition.proofBoundaryFingerprint -cne $operation.routeTransition.proofBoundaryFingerprint) { throw 'binding_changed' }
                }
                $before=@($stream | Where-Object { $_.afterStateGeneration -le $start.beforeStateGeneration })
                $installs=@(foreach ($b in $before) {
                    if ($b.decisionKind -cne 'CommitRuntimeUnlockRetryDispatchInstall') { continue }
                    foreach ($o in $b.operations) {
                        if ($o.family -ceq 'runtime_unlock_retry_lifecycle' -and $o.operationRole -ceq 'attempt_installed' -and
                            $o.beforeState -ceq 'None' -and $o.afterState -ceq 'Installed' -and $o.receiptValidated) {
                            [pscustomobject]@{Boundary=$b;Operation=$o}
                        }
                    }
                })
                $effective=@($installs | Where-Object {
                    $_.Operation.authorityId -ceq $p.retryAuthorityId -and $_.Operation.operationGeneration -eq $p.retryAuthorityGeneration -and
                    $_.Operation.receiptGeneration -eq $p.installGeneration -and $_.Operation.receiptFingerprint -ceq $p.installReceiptFingerprint
                })
                if ($effective.Count -ne 1) { throw 'effective_install_missing_or_ambiguous' }
                $install=$effective[0]; $retry=$install.Operation.runtimeUnlockRetry
                if ($retry.releaseKind -cne 'FallbackPairedTailYield' -or $retry.releaseOutcome -cne 'Quiescent' -or
                    $retry.releaseAuthorityId -cne $p.pairedReleaseAuthorityId -or $retry.releaseGeneration -ne $p.pairedReleaseGeneration -or
                    -not (Test-FileTransferCoordinatorSnapshotRouteTransitionSourceCurrent $operation.routeTransition.sourceBoundary $retry.sourceBoundary)) { throw 'effective_install_scope' }
                foreach ($name in @('transportEpoch','bridgeRecoveryGeneration')) {
                    if ($operation.routeTransition.sourceBoundary.$name -ne $retry.sourceBoundary.$name) { throw 'effective_install_transport_scope' }
                }
                # Reject a newer issued retry, not merely a later log line. Its
                # source can be equal while its authority is unrelated or stale.
                foreach ($b in $before) {
                    if ($b.afterStateGeneration -le $install.Boundary.afterStateGeneration) { continue }
                    if (@($b.operations | Where-Object { $_.family -ceq 'runtime_unlock_retry_lifecycle' -and
                        $_.operationRole -ceq 'attempt_pending' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Authorized' -and
                        $_.authorityId -cne $p.retryAuthorityId -and $_.stateApplied }).Count) { throw 'effective_install_superseded' }
                }
                $consumptions=@(foreach ($b in $before) {
                    foreach ($o in $b.operations) {
                        if ($o.family -ceq 'fallback_tail_yield' -and $o.operationRole -ceq 'release_reservation_consumed' -and
                            $o.beforeState -ceq 'None' -and $o.afterState -ceq 'Consumed' -and $o.receiptValidated -and
                            $o.fallbackTailYield.pairedReleaseAuthorityId -ceq $p.pairedReleaseAuthorityId -and
                            $o.fallbackTailYield.pairedReleaseGeneration -eq $p.pairedReleaseGeneration) {
                            [pscustomobject]@{Boundary=$b;Operation=$o}
                        }
                    }
                })
                if ($consumptions.Count -ne 1) { throw 'consumption_missing_or_ambiguous' }
                $consumed=$consumptions[0]
                $root=@($installs | Where-Object {
                    $_.Boundary.boundaryId -ceq $consumed.Boundary.boundaryId -and
                    $_.Operation.receiptGeneration -eq $consumed.Operation.receiptGeneration -and
                    $_.Operation.receiptFingerprint -ceq $consumed.Operation.receiptFingerprint
                })
                if ($root.Count -ne 1) { throw 'consuming_install_missing' }
                $chain=[System.Collections.Generic.List[object]]::new(); $seen=@{}; $cursor=$install
                while ($true) {
                    $id=[string]$cursor.Operation.authorityId
                    if ($seen.ContainsKey($id)) { throw 'retry_ancestry_cycle' }
                    $seen[$id]=$true; $chain.Add($cursor)
                    $r=$cursor.Operation.runtimeUnlockRetry
                    if ($r.releaseAuthorityId -cne $p.pairedReleaseAuthorityId -or $r.releaseGeneration -ne $p.pairedReleaseGeneration -or
                        $r.releaseKind -cne 'FallbackPairedTailYield' -or $r.releaseOutcome -cne 'Quiescent' -or
                        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $r.sourceBoundary $retry.sourceBoundary)) { throw 'retry_ancestry_scope' }
                    if ($id -ceq $root[0].Operation.authorityId) { break }
                    $prior=@($installs | Where-Object { $_.Operation.authorityId -ceq $cursor.Operation.predecessorAuthorityId -and
                        $_.Boundary.afterStateGeneration -lt $cursor.Boundary.afterStateGeneration })
                    if ($prior.Count -ne 1) { throw 'retry_ancestry_missing' }
                    $cursor=$prior[0]
                }
                if ($bindings.ContainsKey($key)) { throw 'duplicate_preparation' }
                $bindings[$key]=[pscustomobject]@{ PreparedBoundary=$start; PreparedOperation=$operation;
                    EffectiveInstall=$install; ConsumedRelease=$consumed; InstallAncestry=@($chain.ToArray()); History=$history }
            } catch {
                $errors.Add("$failure|$($_.Exception.Message):$($start.evidenceLineIndex)")
            }
        }
    }
    [pscustomobject]@{ CatalogVersion=$Catalog.Version; Bindings=$bindings; Errors=@($errors.ToArray()) }
}
