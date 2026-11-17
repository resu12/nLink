# Frozen test-only pre-candidate reducer, captured before the reporting optimization.
function Get-FileTransferFallbackTailSnapshotLocalYieldMissingCorrelationBaseline {
    param(
        [object[]]$Events,
        [Parameter(Mandatory = $true)]$SnapshotEvaluation
    )

    $empty = {
        [pscustomobject]@{
            RawCount = 0
            DistinctCount = 0
            RecoveredCount = 0
            UnresolvedCount = 0
            Verdict = 'pass'
            UnresolvedCandidateRecords = @()
        }
    }
    if ([string]$SnapshotEvaluation.Mode -cne 'SnapshotV1Authoritative' -or
        [string]$SnapshotEvaluation.ArtifactEvidenceVerdict -cne 'pass' -or
        -not [bool]$SnapshotEvaluation.StreamComplete -or
        @($SnapshotEvaluation.AuthoritativeFamilies) -notcontains 'fallback_tail_yield') {
        return & $empty
    }

    [object[]]$orderedEvents = @($Events | Sort-Object Sequence, LineNumber)
    [object[]]$entries = @(
        foreach ($boundary in @($SnapshotEvaluation.Boundaries)) {
            if ([string]$boundary.verdict -cne 'pass') {
                continue
            }
            foreach ($operation in @($boundary.operations)) {
                if ([string]$operation.family -ceq 'fallback_tail_yield') {
                    [pscustomobject]@{ Boundary = $boundary; Operation = $operation }
                }
            }
        })
    [object[]]$bindings = if (
        $null -ne $SnapshotEvaluation.PSObject.Properties[
            '__FallbackTailPeerProofBindings']) {
        @($SnapshotEvaluation.__FallbackTailPeerProofBindings)
    }
    else {
        @()
    }
    [object[]]$starts = @($bindings | Where-Object {
            [string]$_.BindingKind -ceq 'pending_peer_proof' -and
            [string]$_.OperationRole -ceq 'peer_proof_pending_initial' -and
            [string]$_.BeforeState -ceq 'None' -and
            [string]$_.AfterState -ceq 'Pending' -and
            [string]$_.PairingState -ceq 'Pending' -and
            [long]$_.TransitionSequence -gt 0 -and
            [long]$_.StateGeneration -gt 0 -and
            [long]$_.OperationGeneration -gt 0 -and
            [long]$_.OperationGeneration -eq [long]$_.PeerProofYieldGeneration -and
            [string]$_.PeerProofAuthorityId -match
                '^fta2-fallback_tail_yield-[0-9a-f]{24}$' -and
            (ConvertTo-FileTransferCoordinatorEvidenceToken `
                ([string]$_.PeerProofAuthorityId)) -ceq
                [string]$_.OperationAuthorityId -and
            $null -ne $_.PeerProofReceipt -and
            $null -ne $_.PeerProofEffect -and
            [long]$_.PeerProofEffect.WriterSequence -gt
                [long]$_.TransitionSequence
        })

    $incidents = [System.Collections.Generic.List[object]]::new()
    foreach ($start in $starts) {
        $startSequence = [long]$start.TransitionSequence
        [object[]]$retirements = @($entries | Where-Object {
                [string]$_.Boundary.coordinatorInstanceId -ceq
                    [string]$start.CoordinatorInstanceId -and
                [string]$_.Boundary.writer -ceq [string]$start.Writer -and
                [string]$_.Boundary.sessionId -ceq [string]$start.SessionId -and
                [string]$_.Boundary.transferId -ceq [string]$start.TransferId -and
                [string]$_.Boundary.direction -ceq [string]$start.Direction -and
                [long]$_.Boundary.writerSequence -gt $startSequence -and
                [string]$_.Operation.operationKey -ceq
                    [string]$start.OperationKey -and
                [string]$_.Operation.authorityId -ceq
                    [string]$start.OperationAuthorityId -and
                [long]$_.Operation.operationGeneration -eq
                    [long]$start.OperationGeneration -and
                [string]$_.Operation.beforeState -ceq 'Pending' -and
                [string]$_.Operation.afterState -ceq 'None' -and
                (Test-FileTransferFallbackTailSnapshotLineageBoundary `
                    -PendingBinding $start `
                    -Boundary $_.Boundary)
            } | Sort-Object { [long]$_.Boundary.writerSequence })
        $firstRetirement = @($retirements | Select-Object -First 1)
        $retirementBound = if ($firstRetirement.Count -eq 1) {
            [long]$firstRetirement[0].Boundary.writerSequence
        }
        else {
            [long]::MaxValue
        }

        [object[]]$receipts = @($orderedEvents | Where-Object {
                $writerSequence = Get-FileTransferFallbackTailEventWriterSequence `
                    -Event $_
                $writerSequence -gt $startSequence -and
                $writerSequence -lt $retirementBound -and
                (Test-FileTransferFallbackTailSnapshotReceiptMatch `
                    -Binding $start `
                    -Event $_)
            })
        if ($receipts.Count -ne 1) {
            continue
        }
        $receipt = $receipts[0]
        [object[]]$receiptOwners = @($starts | Where-Object {
                Test-FileTransferFallbackTailSnapshotReceiptMatch `
                    -Binding $_ `
                    -Event $receipt
            })
        if ($receiptOwners.Count -ne 1 -or
            [string]$receiptOwners[0].TransitionId -cne
                [string]$start.TransitionId -or
            [string]$receiptOwners[0].OperationKey -cne
                [string]$start.OperationKey) {
            continue
        }
        $receiptSequence = Get-FileTransferFallbackTailEventWriterSequence -Event $receipt

        [object[]]$preReceiptPoisonEvents = @($orderedEvents | Where-Object {
                $writerSequence = Get-FileTransferFallbackTailEventWriterSequence `
                    -Event $_
                $writerSequence -gt $startSequence -and
                $writerSequence -lt $receiptSequence -and
                (([string]$_.EventName -in @(
                            'filetransfer_runtime_terminalized',
                            'filetransfer_terminalized_by_peer_down',
                            'filetransfer_terminalized_by_peer_session_end',
                            'filetransfer_terminalized_by_peer_disconnect',
                            'filetransfer_terminalized_by_session_end',
                            'filetransfer_session_replaced',
                            'filetransfer_data_session_recreated',
                            'filetransfer_data_session_removed',
                            'file_transfer_inbound_terminal',
                            'file_transfer_outbound_terminal',
                            'transfer_terminal') -and
                        (Test-FileTransferFallbackTailLocalYieldPoisonScopeMatch `
                            -Pending $receipt `
                            -Event $_)) -or
                    (Test-FileTransferFallbackTailLocalYieldScopeMutation `
                        -Pending $receipt `
                        -Event $_))
            })
        if ($preReceiptPoisonEvents.Count -gt 0) {
            continue
        }

        $retirement = if ($retirements.Count -eq 1) { $retirements[0] } else { $null }
        [object[]]$peerFirstYieldEntries = @()
        if ($null -ne $retirement) {
            $peerFirstYieldEntries = @($entries | Where-Object {
                    [string]$_.Boundary.boundaryId -ceq
                        [string]$retirement.Boundary.boundaryId -and
                    [long]$_.Boundary.writerSequence -eq
                        [long]$retirement.Boundary.writerSequence -and
                    (Test-FileTransferFallbackTailSnapshotPeerFirstYieldCarry `
                        -PendingBinding $start `
                        -PendingRetirement $retirement `
                        -CurrentEntry $_)
                })
        }
        $peerFirstYield = if ($peerFirstYieldEntries.Count -eq 1) {
            $peerFirstYieldEntries[0]
        }
        else {
            $null
        }

        [object[]]$recoveryBindings = @()
        if ($null -ne $peerFirstYield) {
            $recoveryBindings = @($bindings | Where-Object {
                    [string]$_.BindingKind -ceq 'paired_yield' -and
                    [long]$_.TransitionSequence -gt
                        [long]$retirement.Boundary.writerSequence -and
                    [string]$_.OperationKey -ceq
                        [string]$peerFirstYield.Operation.operationKey -and
                    (Test-FileTransferFallbackTailSnapshotPairingRecovery `
                        -PendingBinding $start `
                        -PairedBinding $_)
                })
        }
        $recovery = if ($recoveryBindings.Count -eq 1) {
            $recoveryBindings[0]
        }
        else {
            $null
        }
        $retirementSequence = if ($null -ne $retirement) {
            [long]$retirement.Boundary.writerSequence
        }
        else {
            [long]::MaxValue
        }
        if ($null -ne $recovery) {
            $recoverySequence = [long]$recovery.TransitionSequence
            [object[]]$lineageBoundaries = @(
                $SnapshotEvaluation.Boundaries | Where-Object {
                    [long]$_.writerSequence -gt $startSequence -and
                    [long]$_.writerSequence -le $recoverySequence -and
                    [string]$_.writer -ceq [string]$start.Writer -and
                    [string]$_.coordinatorInstanceId -ceq
                        [string]$start.CoordinatorInstanceId
                })
            [object[]]$pendingHistory = @($entries | Where-Object {
                    [string]$_.Boundary.coordinatorInstanceId -ceq
                        [string]$start.CoordinatorInstanceId -and
                    [string]$_.Boundary.writer -ceq [string]$start.Writer -and
                    [long]$_.Boundary.writerSequence -ge $startSequence -and
                    [long]$_.Boundary.writerSequence -le $retirementSequence -and
                    [string]$_.Operation.operationKey -ceq
                        [string]$start.OperationKey
                })
            [object[]]$currentHistory = @($entries | Where-Object {
                    [string]$_.Boundary.coordinatorInstanceId -ceq
                        [string]$start.CoordinatorInstanceId -and
                    [string]$_.Boundary.writer -ceq [string]$start.Writer -and
                    [long]$_.Boundary.writerSequence -ge $retirementSequence -and
                    [long]$_.Boundary.writerSequence -le $recoverySequence -and
                    [string]$_.Operation.operationKey -ceq
                        [string]$peerFirstYield.Operation.operationKey
                })
            $lineageValid = $lineageBoundaries.Count -gt 0 -and
                @($lineageBoundaries | Where-Object {
                        -not (Test-FileTransferFallbackTailSnapshotLineageBoundary `
                            -PendingBinding $start `
                            -Boundary $_)
                    }).Count -eq 0 -and
                @($pendingHistory | Where-Object {
                        $sequence = [long]$_.Boundary.writerSequence
                        ($sequence -eq $startSequence -and
                            [string]$_.Operation.beforeState -ceq 'None' -and
                            [string]$_.Operation.afterState -ceq 'Pending') -or
                        ($sequence -eq $retirementSequence -and
                            [string]$_.Operation.beforeState -ceq 'Pending' -and
                            [string]$_.Operation.afterState -ceq 'None') -or
                        ($sequence -gt $startSequence -and
                            $sequence -lt $retirementSequence -and
                            [string]$_.Operation.beforeState -ceq 'Pending' -and
                            [string]$_.Operation.afterState -ceq 'Pending')
                    }).Count -eq $pendingHistory.Count -and
                @($pendingHistory | Where-Object {
                        [long]$_.Boundary.writerSequence -eq $startSequence
                    }).Count -eq 1 -and
                @($pendingHistory | Where-Object {
                        [long]$_.Boundary.writerSequence -eq $retirementSequence
                    }).Count -eq 1 -and
                @($currentHistory | Where-Object {
                        $sequence = [long]$_.Boundary.writerSequence
                        ($sequence -eq $retirementSequence -and
                            [string]$_.Operation.beforeState -ceq 'None' -and
                            [string]$_.Operation.afterState -ceq 'PeerProofObserved') -or
                        ($sequence -eq $recoverySequence -and
                            [string]$_.Operation.beforeState -ceq 'PeerProofObserved' -and
                            [string]$_.Operation.afterState -ceq 'Paired') -or
                        ($sequence -gt $retirementSequence -and
                            $sequence -lt $recoverySequence -and
                            [string]$_.Operation.beforeState -ceq 'PeerProofObserved' -and
                            [string]$_.Operation.afterState -ceq 'PeerProofObserved')
                    }).Count -eq $currentHistory.Count -and
                @($currentHistory | Where-Object {
                        [long]$_.Boundary.writerSequence -eq $retirementSequence
                    }).Count -eq 1 -and
                @($currentHistory | Where-Object {
                        [long]$_.Boundary.writerSequence -eq $recoverySequence
                    }).Count -eq 1
            if (-not $lineageValid) {
                $recovery = $null
            }
        }
        $poisonUpperBound = if ($null -ne $recovery) {
            [long]$recovery.TransitionSequence
        }
        else {
            $retirementSequence
        }
        [object[]]$poisonEvents = @($orderedEvents | Where-Object {
                $writerSequence = Get-FileTransferFallbackTailEventWriterSequence `
                    -Event $_
                $writerSequence -gt $receiptSequence -and
                $writerSequence -lt $poisonUpperBound -and
                (([string]$_.EventName -in @(
                            'filetransfer_runtime_terminalized',
                            'filetransfer_terminalized_by_peer_down',
                            'filetransfer_terminalized_by_peer_session_end',
                            'filetransfer_terminalized_by_peer_disconnect',
                            'filetransfer_terminalized_by_session_end',
                            'filetransfer_session_replaced',
                            'filetransfer_data_session_recreated',
                            'filetransfer_data_session_removed',
                            'file_transfer_inbound_terminal',
                            'file_transfer_outbound_terminal',
                            'transfer_terminal') -and
                        (Test-FileTransferFallbackTailLocalYieldPoisonScopeMatch `
                            -Pending $receipt `
                            -Event $_)) -or
                    (Test-FileTransferFallbackTailLocalYieldScopeMutation `
                        -Pending $receipt `
                        -Event $_))
            } | Sort-Object {
                Get-FileTransferFallbackTailEventWriterSequence -Event $_
            })
        $poisonSequence = if ($poisonEvents.Count -gt 0) {
            Get-FileTransferFallbackTailEventWriterSequence -Event $poisonEvents[0]
        }
        else {
            [long]::MaxValue
        }
        $endSequence = [Math]::Min($retirementSequence, $poisonSequence)
        [object[]]$blocked = @($orderedEvents | Where-Object {
                [string]$_.EventName -ceq 'filetransfer_fallback_tail_local_yield_blocked' -and
                (Get-FileTransferEventField -Event $_ -Name 'source' -Default '') -ceq
                    'fallback_tail_peer_yield' -and
                (Get-FileTransferEventField -Event $_ -Name 'decision_reason' -Default '') -ceq
                    'fallback_tail_local_owner_work_pending' -and
                (Get-FileTransferFallbackTailEventWriterSequence -Event $_) -gt $receiptSequence -and
                (Get-FileTransferFallbackTailEventWriterSequence -Event $_) -lt $endSequence -and
                (Test-FileTransferFallbackTailRawScopeEqual -Left $receipt -Right $_) -and
                (Test-FileTransferFallbackTailSnapshotScopeEventMatch `
                    -Binding $start `
                    -Event $_)
            })
        if ($blocked.Count -gt 0) {
            $firstBlockedSequence = Get-FileTransferFallbackTailEventWriterSequence `
                -Event ($blocked | Sort-Object Sequence, LineNumber | Select-Object -First 1)
            $incidents.Add([pscustomobject]@{
                    Start = $start
                    BlockedEvents = $blocked
                    Ambiguous = $false
                    Recovered = $null -ne $recovery -and
                        [long]$recovery.TransitionSequence -gt $firstBlockedSequence -and
                        [long]$recovery.TransitionSequence -lt $poisonSequence
                }) | Out-Null
        }
    }

    $blockedOwners = [System.Collections.Generic.Dictionary[string, int]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($incident in $incidents) {
        foreach ($blockedEvent in @($incident.BlockedEvents)) {
            $blockedKey = '{0}|{1}' -f
                (Get-FileTransferEventField `
                    -Event $blockedEvent `
                    -Name 'log_writer_id' `
                    -Default ''),
                (Get-FileTransferFallbackTailEventWriterSequence `
                    -Event $blockedEvent)
            $blockedOwners[$blockedKey] = if ($blockedOwners.ContainsKey($blockedKey)) {
                $blockedOwners[$blockedKey] + 1
            }
            else {
                1
            }
        }
    }
    foreach ($incident in $incidents) {
        $incident.Ambiguous = @($incident.BlockedEvents | Where-Object {
                $blockedKey = '{0}|{1}' -f
                    (Get-FileTransferEventField `
                        -Event $_ `
                        -Name 'log_writer_id' `
                        -Default ''),
                    (Get-FileTransferFallbackTailEventWriterSequence -Event $_)
                $blockedOwners[$blockedKey] -ne 1
            }).Count -gt 0
        if ($incident.Ambiguous) {
            $incident.Recovered = $false
        }
    }

    [object[]]$unresolved = @($incidents | Where-Object { -not $_.Recovered })
    [object[]]$candidateRecords = @(
        foreach ($incident in $unresolved) {
            $first = @($incident.BlockedEvents | Sort-Object Sequence, LineNumber | Select-Object -First 1)
            if ($first.Count -eq 1) {
                [pscustomobject]@{
                    Class = 'fallback_tail_local_yield_missing'
                    Event = $first[0]
                    Reason = 'fallback_tail_local_owner_work_pending'
                    # The generic owner-work observation does not identify which
                    # service execution or transport obligation is holding it.
                    Variant = 'peer_first_local_quiescent_proof_pairing_missing'
                    RawCopyCount = [int]$incident.BlockedEvents.Count
                }
            }
        })
    $rawCount = [int](@($incidents | ForEach-Object {
                $_.BlockedEvents.Count
            } | Measure-Object -Sum).Sum)
    return [pscustomobject]@{
        RawCount = $rawCount
        DistinctCount = [int]$incidents.Count
        RecoveredCount = [int]@($incidents | Where-Object { $_.Recovered }).Count
        UnresolvedCount = [int]$unresolved.Count
        Verdict = if ($unresolved.Count -gt 0) { 'fail' } else { 'pass' }
        UnresolvedCandidateRecords = $candidateRecords
    }
}
