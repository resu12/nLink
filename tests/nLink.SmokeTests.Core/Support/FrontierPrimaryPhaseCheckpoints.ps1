function Add-FrontierPrimaryPhaseCheckpoints([string]$Definition) {
    if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -cne 'canonical-tail-late-publication') {return $Definition}
    # Project only the fixture's in-memory copy. Every original statement remains unchanged.
    $newline=if($Definition.Contains("`r`n")) {"`r`n"} else {"`n"}
    $seams=@(
        @{line='    $snapshotCatalog = Get-FileTransferCoordinatorEvidenceCatalog';before=@('compact-enter')},
        @{line='    if ($null -ne $LocalSourceOwner -and $null -ne $LocalSourceReader) {';before=@('arrays-ready')},
        @{line='                $private:nativePrimaryCustody = $LocalSourceOwner.TryBeginFrontierPrimary($LocalSourceReader,';before=@('origins-ready','registration-start')},
        @{line='                    $primaryCompactSnapshotLines, $nativeCompactOrdinals)';after=@('registration-end')},
        @{line='    $fullSnapshotEvaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $primaryFullSnapshotLines';before=@('full-start');after=@('full-end')},
        @{line='    $compactSnapshotEvaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $primaryCompactSnapshotLines';before=@('compact-start');after=@('compact-end')},
        @{line='    # Only an admitted bounded private context retains these array references.';before=@('payloads-ready')},
        @{line='    $snapshotEvaluation = $compactSnapshotEvaluation';before=@('parity-ready')},
        @{line='    $fileTransferReceiveRecoveryOperationLegacyProof =';before=@('pre-correlator')}
    )
    foreach($seam in $seams) {
        $replacement=$seam.line
        if($seam.ContainsKey('before')) {
            $prefix=@(foreach($stage in $seam.before) {"Write-FrontierPrimaryCheckpoint '$stage' 'original phase boundary' # fixture-primary-phase"})
            $replacement=($prefix -join $newline)+$newline+$replacement
        }
        if($seam.ContainsKey('after')) {
            $suffix=@(foreach($stage in $seam.after) {"Write-FrontierPrimaryCheckpoint '$stage' 'original phase boundary' # fixture-primary-phase"})
            $replacement+=$newline+($suffix -join $newline)
        }
        $Definition=Replace-AttemptSeam $Definition $seam.line $replacement
    }
    return $Definition
}
