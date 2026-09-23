Set-StrictMode -Version Latest

$script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion =
    'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1-source-callback-v1-repair-owner-v1-activation-retry-v1-receiver-pre-admission-v1'
$script:FileTransferCoordinatorReceiverPreAdmissionCatalogHash = '17196929ff74bd92c4866ccb8da33de05eb5b22d5436f56488f3a7fd2f4d086b'
$script:FileTransferCoordinatorReceiverPreAdmissionCatalog = [ordered]@{
    version=1; family='responder_probe_handoff'; operationKind='InitialActivationReceiverBarrier'
    payloadName='preAdmissionClose'; mode='InitialResponderBeforeProbe'
    payloadFields=[ordered]@{
        mode='string'; failureNegotiationFingerprint='string'; negotiationWithoutLaunchFingerprint='string'
        boundLaunchGeneration='int64'; failureLaunchGeneration='int64'
    }
    binding='exact-bound-semantic-ack-zero-launch-successor-no-admission-v1'; physicalAuthority=$false
}

function Get-FileTransferCoordinatorReceiverPreAdmissionCatalog {
    $parent=Get-FileTransferCoordinatorActivationRetryCatalog
    $json=ConvertTo-Json -InputObject $script:FileTransferCoordinatorReceiverPreAdmissionCatalog -Depth 16 -Compress
    $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.UTF8Encoding]::new($false).GetBytes(
        "parentHash=$($parent.Hash)`nReceiverPreAdmissionCatalog=$json"))
    if ($hash -cne $script:FileTransferCoordinatorReceiverPreAdmissionCatalogHash) { throw "receiver_pre_admission_catalog_hash_invalid:$hash" }
    $result=[ordered]@{}
    foreach ($property in $parent.PSObject.Properties) { $result[$property.Name]=$property.Value }
    $result.Version=$script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion
    $result.Hash=$hash; $result.ParentCatalogHash=$parent.Hash
    $result.ReceiverPreAdmissionCatalog=$script:FileTransferCoordinatorReceiverPreAdmissionCatalog
    [pscustomobject]$result
}

function Test-FileTransferReceiverPreAdmissionClose {
    param($Payload,$Catalog)
    $p=Get-FileTransferCoordinatorSnapshotObjectProperty $Payload 'preAdmissionClose'
    if ($null -eq $p) { return $true }
    if ($Catalog.Version -cne $script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion -or
        -not (Test-FileTransferCheckpointRepairContinuityFields $p $script:FileTransferCoordinatorReceiverPreAdmissionCatalog.payloadFields) -or
        $p.mode -cne 'InitialResponderBeforeProbe' -or $Payload.admissionFingerprint -cne '(none)' -or
        $p.boundLaunchGeneration -ne 0 -or $p.failureLaunchGeneration -lt 0 -or
        $p.negotiationWithoutLaunchFingerprint -cne $Payload.negotiationFingerprint -or
        $p.failureNegotiationFingerprint -cnotmatch '^[0-9a-f]{64}$' -or
        $p.negotiationWithoutLaunchFingerprint -cnotmatch '^[0-9a-f]{64}$') { return $false }
    return ($p.failureLaunchGeneration -eq 0) -eq ($p.failureNegotiationFingerprint -ceq $Payload.negotiationFingerprint)
}

function Test-FileTransferReceiverPreAdmissionPreparation {
    param($Operation,$Prior,$Boundary)
    return $Operation.operationRole -ceq 'receiver_barrier_current' -and
        $Operation.beforeState -ceq 'Bound' -and $Operation.afterState -ceq 'ClosePrepared' -and
        $Boundary.decisionKind -ceq 'PrepareInitialActivationReceiverBarrierClose' -and $Boundary.decisionStatus -ceq 'Accepted' -and
        $Prior.receiverBarrier.admissionFingerprint -ceq '(none)' -and $Operation.receiverBarrier.admissionFingerprint -ceq '(none)' -and
        $null -eq (Get-FileTransferCoordinatorSnapshotObjectProperty $Prior.receiverBarrier 'preAdmissionClose') -and
        $null -ne (Get-FileTransferCoordinatorSnapshotObjectProperty $Operation.receiverBarrier 'preAdmissionClose')
}
