# Fixed test-only inventory of the original compact verdict inputs.
$script:TerminalIntegrityCountFactors=@(
    'runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteUnresolvedCount',
    'regularV4FrontierRepairSettlementDispositionProof.UnresolvedCount',
    'regularV4FrontierRepairPressureRecoveryHandoffProof.UnresolvedCount',
    'runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.UnresolvedCount',
    'runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.UnresolvedCount',
    'runtimeUnlockRetryDispatchPostGrantHoldProof.UnresolvedCount',
    'fileTransferReceiveRecoveryOperationProof.UnresolvedCount',
    'fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.UnresolvedCount',
    'fileTransferReceiveRecoveryCompletedProvenSuccessorProof.UnresolvedCount',
    'fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.UnresolvedCount',
    'fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.UnresolvedCount',
    'fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.UnresolvedCount',
    'fallbackTailFailedYieldQuiescentSuccessorProof.UnresolvedCount',
    'runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.UnresolvedCount',
    'runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedUnresolvedCount',
    'runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessUnresolvedCount',
    'bridgeRecoveryExecutionPayloadHoldProof.UnresolvedCount',
    'fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.UnresolvedCount',
    'fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.UnresolvedCount',
    'fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.UnresolvedCount',
    'bridgeRecoveryExecutionSubmissionBarrierDrainProof.UnresolvedCount',
    'bridgeRecoveryExecutionStartupPayloadIsolationProof.UnresolvedCount',
    'runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.UnresolvedCount',
    'runtimeUnlockInitialOfferRetryLifecycleHandoffProof.UnresolvedCount',
    'runtimeUnlockResponderPeerProbeHandoffProof.UnresolvedCount',
    'runtimeUnlockInitialActivationAnswerPeerDeliveryProof.UnresolvedCount',
    'runtimeUnlockInitialOfferReceiveRecoveryHoldProof.UnresolvedCount',
    'runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.UnresolvedCount',
    'runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.UnresolvedCount',
    'runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.UnresolvedCount',
    'runtimeUnlockRetryFallbackTailUnblockRecheckProof.UnresolvedCount',
    'runtimeUnlockRetryCurrentListenerAdoptionProof.UnresolvedCount',
    'runtimeUnlockRegularV4RecoverySettlementProof.HandoffBypassCount',
    'runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.UnresolvedCount',
    'runtimeUnlockPostTunaFallbackTransportProofHandoffProof.UnresolvedCount',
    'fallbackTailTransportEpochReauthorizationProof.UnresolvedCount',
    'regularV4FrontierTailRepairStatePublicationProof.UnresolvedCount',
    'regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.UnresolvedCount',
    'fileTransferRouteTransitionDrainingTransportGraceProof.UnresolvedCount',
    'fileTransferFallbackV6ProofBootstrapProof.UnresolvedCount',
    'fileTransferRouteTransitionProof.UnresolvedCount',
    'runtimeUnlockRegularV4RouteCommitSendDrainProof.UnresolvedCount'
)
function Get-TerminalIntegrityShadowState($ReadStatus,$Gui,[bool]$Owned) {
    if($ReadStatus -ceq 'absent') {if($Owned){return 'owned_integrity_proven'}; return 'owned_integrity_unproven'}
    if($ReadStatus -cne 'read' -or $null -eq $Gui) {return 'invalid_input'}
    $property=$Gui.PSObject.Properties['integrityOk']
    if($null -eq $property -or $property.Value -isnot [bool]) {return 'invalid_input'}
    if(-not $property.Value) {if($Owned){return 'conflict'}; return 'explicit_failure'}
    if($Owned){return 'owned_integrity_proven'}
    return 'owned_integrity_unproven'
}
function Get-TerminalPreVerdictInventory($Frame,[long]$AvailableVisits=4096,[long]$AvailableUnits=1073741824) {
    # Outputs are detached diagnostics. None is accepted as a capability.
    if($AvailableVisits -lt 58 -or $AvailableVisits -gt 4096 -or $AvailableUnits -lt 0 -or $AvailableUnits -gt 1073741824) {throw 'integrity_inventory_remaining_budget'}
    $factors=[ordered]@{
        GuiCompleted=$Frame.GuiCompleted;GuiIntegrityOk=$Frame.GuiIntegrityOk
        HardFailureCount=$Frame.HardFailureCount;ActiveFileTunaV6Count=$Frame.ActiveFileTunaV6Count
        RouteConsistency=$Frame.RouteConsistency.Verdict;LiveRouteProof=$Frame.LiveRouteProof.Verdict
        LiveRouteProofMode=$Frame.LiveRouteProofMode;PreTerminalLivenessTimeoutCount=$Frame.PreTerminalLivenessTimeoutCount
        WarningCapPresent=$null -ne $Frame.WarningCap;WarningKindCount=$Frame.WarningKinds.Count
        CandidateCount=$Frame.Candidates.Count;SnapshotMode=$Frame.Snapshot.Mode
        ArtifactVerdict=$Frame.Snapshot.ArtifactEvidenceVerdict;ParityVerdict=$Frame.Parity.Verdict
        FallbackTailAuthoritative=$Frame.FallbackTailAuthoritative;ReceiveRecoverySelected=$Frame.ReceiveRecoverySelected
    }
    if($Frame.Counts.Count -ne $script:TerminalIntegrityCountFactors.Count) {throw 'integrity_inventory_count_fields'}
    foreach($key in $script:TerminalIntegrityCountFactors) {
        if(-not $Frame.Counts.Contains($key) -or $null -eq $Frame.Counts[$key]) {throw 'integrity_inventory_missing_factor'}
        $factors[$key]=[long]$Frame.Counts[$key]
    }
    $sets=@($Frame.CandidateArray,@($Frame.Mismatch.CandidateRecords),@($Frame.Mismatch.EffectiveCandidateRecords))
    $records=[long]$Frame.CandidateArray.Count+@($Frame.Mismatch.CandidateRecords).Count+@($Frame.Mismatch.EffectiveCandidateRecords).Count
    $warnings=@($Frame.WarningKinds)
    $exceeded=@(if($null -ne $Frame.WarningCap){$Frame.WarningCap.ExceededKinds})
    $visits=[long]$factors.Count+6L*$records+$warnings.Count+$exceeded.Count
    if($visits -gt $AvailableVisits -or $Frame.Candidates.Count -ne $Frame.CandidateArray.Count) {throw 'integrity_inventory_record_limit'}
    $units=0L
    foreach($key in @($factors.Keys)) {
        if($null -eq $factors[$key]) {throw 'integrity_inventory_missing_factor'}
        $value=[string]$factors[$key]
        if($value.Length -gt 4096) {throw 'integrity_inventory_scalar_limit'}
        $cost=4L*($key.Length+$value.Length)
        if($cost -gt $AvailableUnits-$units) {throw 'integrity_inventory_code_unit_limit'}
        $units+=$cost
    }
    $groups=[Collections.Generic.List[object]]::new(); $selectedIndex=-1
    for($set=0;$set -lt 3;$set++) {
        $rows=[Collections.Generic.List[object]]::new()
        for($i=0;$i -lt $sets[$set].Count;$i++) {
            $item=$sets[$set][$i]
            if($set -eq 0) {
                if(-not [object]::ReferenceEquals($item,$Frame.Candidates[$i])) {throw 'integrity_inventory_candidate_identity'}
                if([object]::ReferenceEquals($item,$Frame.FirstFailure)) {
                    if($selectedIndex -ge 0) {throw 'integrity_inventory_duplicate_selection'}
                    $selectedIndex=$i
                }
            }
            $row=[ordered]@{}
            foreach($name in @('Class','Reason','Variant','Recovered')) {
                $p=$item.PSObject.Properties[$name]; $row[$name]=if($null -eq $p){$null}else{$p.Value}
            }
            foreach($name in @('EventName','Sequence','LineNumber')) {
                $p=$item.Event.PSObject.Properties[$name]; $row[$name]=if($null -eq $p){$null}else{$p.Value}
            }
            $diagnostic=$item.PSObject.Properties['RejectionDiagnostic']
            $row.RejectionReason=if($null -ne $diagnostic -and $null -ne $diagnostic.Value){$diagnostic.Value.Reason}else{$null}
            foreach($name in @($row.Keys)) {
                if($null -ne $row[$name]) {
                    $value=[string]$row[$name]
                    if($value.Length -gt 4096) {throw 'integrity_inventory_scalar_limit'}
                    $cost=4L*($name.Length+$value.Length)
                    if($cost -gt $AvailableUnits-$units) {throw 'integrity_inventory_code_unit_limit'}
                    $units+=$cost
                }
            }
            $rows.Add([pscustomobject]$row)
        }
        $groups.Add($rows.ToArray())
    }
    if($null -ne $Frame.FirstFailure -and $selectedIndex -lt 0) {throw 'integrity_inventory_selection_not_member'}
    foreach($value in @($warnings)+@($exceeded)) {
        if($null -eq $value -or ([string]$value).Length -gt 4096) {throw 'integrity_inventory_warning_limit'}
        $cost=4L*([string]$value).Length
        if($cost -gt $AvailableUnits-$units) {throw 'integrity_inventory_code_unit_limit'}
        $units+=$cost
    }
    [pscustomobject]@{Factors=$factors;WarningKinds=$warnings;WarningCapExceededKinds=$exceeded;
        Candidates=$groups[0];MismatchCandidates=$groups[1];EffectiveMismatchCandidates=$groups[2];
        FirstSelectedIndex=$selectedIndex;RecordVisits=$visits;CodeUnitVisits=$units}
}
