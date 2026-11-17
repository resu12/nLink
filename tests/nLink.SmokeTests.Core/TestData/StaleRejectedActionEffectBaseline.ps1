# Frozen pre-candidate-reuse proof. Keep candidate selection independent of production edits.
function Invoke-StaleEffectBaseline {
    param([Parameter(Mandatory = $true)]$Summary)

    [object[]]$events = @()
    if ($null -ne $Summary.PSObject.Properties['AllEvents']) {
        $events = @($Summary.AllEvents)
    }
    if ($events.Count -eq 0) {
        $events = @($Summary.GlobalEvents + $Summary.TransferEvents)
    }

    $events = @($events | Sort-Object Sequence)
    $rejections = @($events | Where-Object {
            $_.EventName -eq 'filetransfer_runtime_owner_stale_update_rejected' -or
            $_.EventName -eq 'filetransfer_coordinator_leg_mutation_rejected' -or
            $_.EventName -eq 'filetransfer_runtime_unlock_callback_rejected' -or
            $_.EventName -eq 'filetransfer_runtime_unlock_precommit_probe_pending_rejected' -or
            $_.EventName -eq 'runtime_unlock_retry_rejected_after_regular_v4_receive_recovery' -or
            $_.EventName -eq 'runtime_unlock_retry_dispatch_authority_rejected' -or
            $_.EventName -eq 'filetransfer_recovery_authority_rejected' -or
            $_.EventName -eq 'filetransfer_route_publication_rejected' -or
            $_.EventName -eq 'filetransfer_route_hint_ignored_missing_publication_authority' -or
            $_.EventName -eq 'filetransfer_route_hint_ignored_stale_publication_authority' -or
            $_.EventName -eq 'filetransfer_route_hint_ignored_invalid_publication_authority'
        })
    $families = @('liveness', 'repair', 'progress', 'checkpoint', 'bridge_recovery', 'control_plane', 'route_publication')
    $familyCounts = @{}
    foreach ($family in @($families)) {
        $familyCounts[$family] = 0
    }
    $fallbackSubfamilies = @('fallback_send', 'fallback_checkpoint', 'fallback_reissue', 'fallback_tail_yield', 'fallback_pressure')
    $fallbackSubfamilyCounts = @{}
    foreach ($subfamily in @($fallbackSubfamilies)) {
        $fallbackSubfamilyCounts[$subfamily] = 0
    }

    $unknownActionCount = 0
    $forbiddenEffectCount = 0
    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    $failureEvents = New-Object System.Collections.Generic.List[object]

    foreach ($rejection in @($rejections | Sort-Object Sequence)) {
        $family = Get-FileTransferStaleRejectedProofActionFamily -Event $rejection
        $fallbackSubfamily = Get-FileTransferStaleRejectedProofFallbackSubfamily -Event $rejection
        if ($family -eq 'unknown') {
            $unknownActionCount++
            $actionKind = Get-FileTransferEventField -Event $rejection -Name 'action_kind' -Default '(none)'
            $attemptedAction = Get-FileTransferEventField -Event $rejection -Name 'attempted_action' -Default '(none)'
            $findings.Add(("unknown stale rejected authority action action_kind={0} attempted_action={1}: {2}" -f $actionKind, $attemptedAction, (Format-FileTransferEvidenceLine -Event $rejection))) | Out-Null
            $evidence.Add($rejection) | Out-Null
            $failureEvents.Add($rejection) | Out-Null
            continue
        }

        foreach ($effect in @($events | Where-Object { $_.Sequence -gt $rejection.Sequence })) {
            if (-not (Test-FileTransferStaleRejectedProofForbiddenEffect -Family $family -Event $effect -FallbackSubfamily $fallbackSubfamily)) {
                continue
            }

            if (-not (Test-FileTransferStaleRejectedProofScopeMatches -Rejection $rejection -Effect $effect)) {
                continue
            }

            $missingPublicationAuthority = Test-FileTransferPublicationMissingAuthorityRejection -Event $rejection
            if ($missingPublicationAuthority) {
                if (Test-FileTransferPublicationAuthorityAcceptanceEvent -Event $effect) {
                    continue
                }

                if (Test-FileTransferPublicationEffectHasPriorAuthorizedAcceptance -Rejection $rejection -Effect $effect -Events $events) {
                    continue
                }
            }
            elseif (-not (Test-FileTransferStaleRejectedProofAuthorityMatches -Rejection $rejection -Effect $effect)) {
                continue
            }

            $forbiddenEffectCount++
            $familyCounts[$family] = [int]$familyCounts[$family] + 1
            if (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $fallbackSubfamily) {
                $fallbackSubfamilyCounts[$fallbackSubfamily] = [int]$fallbackSubfamilyCounts[$fallbackSubfamily] + 1
            }
            $displayFamily = if (Test-FileTransferStaleRejectedProofMeaningfulValue -Value $fallbackSubfamily) { $fallbackSubfamily } else { $family }
            $findings.Add(("stale rejected {0} action was followed by matching effect: rejection={1}; effect={2}" -f $displayFamily, (Format-FileTransferEvidenceLine -Event $rejection), (Format-FileTransferEvidenceLine -Event $effect))) | Out-Null
            $evidence.Add($rejection) | Out-Null
            $evidence.Add($effect) | Out-Null
            $failureEvents.Add($effect) | Out-Null
        }
    }

    [object[]]$localDeliveries = @($events | Where-Object { Test-FileTransferFallbackLocalDeliveryWithoutPeerVisibleProof -Event $_ } | Sort-Object Sequence)
    foreach ($localDelivery in @($localDeliveries)) {
        [object[]]$laterAdmissionGrants = @($events | Where-Object {
                $_.Sequence -gt $localDelivery.Sequence -and
                $_.EventName -eq 'filetransfer_runtime_unlock_admission_granted' -and
                (Test-FileTransferStaleRejectedProofScopeMatches -Rejection $localDelivery -Effect $_)
            } | Sort-Object Sequence)

        foreach ($admissionGrant in @($laterAdmissionGrants)) {
            [object[]]$peerVisibleCompletions = @($events | Where-Object {
                    $_.Sequence -gt $localDelivery.Sequence -and
                    $_.Sequence -lt $admissionGrant.Sequence -and
                    (Test-FileTransferFallbackPeerVisibleCompletion -Event $_) -and
                    (Test-FileTransferStaleRejectedProofScopeMatches -Rejection $localDelivery -Effect $_)
                })
            if ($peerVisibleCompletions.Count -gt 0) {
                continue
            }

            $forbiddenEffectCount++
            $familyCounts['control_plane'] = [int]$familyCounts['control_plane'] + 1
            $fallbackSubfamilyCounts['fallback_send'] = [int]$fallbackSubfamilyCounts['fallback_send'] + 1
            $findings.Add(("local fallback control-plane acceptance was followed by runtime unlock admission without matching peer-visible completion: delivery={0}; admission={1}" -f (Format-FileTransferEvidenceLine -Event $localDelivery), (Format-FileTransferEvidenceLine -Event $admissionGrant))) | Out-Null
            $evidence.Add($localDelivery) | Out-Null
            $evidence.Add($admissionGrant) | Out-Null
            $failureEvents.Add($admissionGrant) | Out-Null
        }
    }

    $verdict = if ($rejections.Count -eq 0 -and $localDeliveries.Count -eq 0) { 'none' } elseif ($findings.Count -eq 0) { 'pass' } else { 'fail' }

    return [pscustomobject]@{
        Verdict = $verdict
        FailureClass = if ($verdict -eq 'fail') { 'stale_rejected_action_effect' } else { 'none' }
        RejectedActionCount = [int]$rejections.Count
        ForbiddenEffectCount = [int]$forbiddenEffectCount
        UnknownActionCount = [int]$unknownActionCount
        LivenessForbiddenEffectCount = [int]$familyCounts['liveness']
        RepairForbiddenEffectCount = [int]$familyCounts['repair']
        ProgressForbiddenEffectCount = [int]$familyCounts['progress']
        CheckpointForbiddenEffectCount = [int]$familyCounts['checkpoint']
        BridgeRecoveryForbiddenEffectCount = [int]$familyCounts['bridge_recovery']
        ControlPlaneForbiddenEffectCount = [int]$familyCounts['control_plane']
        RoutePublicationForbiddenEffectCount = [int]$familyCounts['route_publication']
        FallbackSendForbiddenEffectCount = [int]$fallbackSubfamilyCounts['fallback_send']
        FallbackCheckpointForbiddenEffectCount = [int]$fallbackSubfamilyCounts['fallback_checkpoint']
        FallbackReissueForbiddenEffectCount = [int]$fallbackSubfamilyCounts['fallback_reissue']
        FallbackTailYieldForbiddenEffectCount = [int]$fallbackSubfamilyCounts['fallback_tail_yield']
        FallbackPressureForbiddenEffectCount = [int]$fallbackSubfamilyCounts['fallback_pressure']
        Findings = $findings
        EvidenceEvents = @($evidence.ToArray() | Select-Object -First 20)
        FailureEvents = @($failureEvents.ToArray() | Sort-Object Sequence, LineNumber | Select-Object -First 20)
    }
}
