$mode=$Case.Substring('cursor-parent-'.Length)
$clone=[object].GetMethod('MemberwiseClone',$flags)
$proof=$owner.TrySealFiles()
if($null -eq $proof) {throw 'Parent registration seal missing'}
$finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
$binding=$finalizer.TryTakeFrontierBinding(); $result=$null; $evidence=$null; $handoff=$null
$parentAttached=$null; $parentReviewed=$null
if(-not $mode.StartsWith('rendezvous-',[StringComparison]::Ordinal)) {
    $parentAttached=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.parent-attached')
    $parentReviewed=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.parent-reviewed')
}
try {
    if($null -eq $binding) {throw 'Parent registration binding missing'}
    if($mode -ceq 'copied-binding') {
        $copy=$clone.Invoke($binding,$null)
        if($null -ne $copy.TryAttachParentHandoff()) {throw 'Copied binding attached parent'}
        $copy.Dispose(); if(-not $binding.IsCurrent) {throw 'Copied binding retired original'}
    }
    if($mode -cne 'early') {
        $semanticEvents=[string[]]@('operational_log_writer_started','writer_only_contribution')
        if($mode.StartsWith('files-facts-ack-commit-primary-worker-join-',[StringComparison]::Ordinal) -or
            $mode.StartsWith('files-facts-ack-commit-primary-parent-',[StringComparison]::Ordinal) -or
            $mode.StartsWith('files-facts-ack-commit-primary-prepare-Wrapper-start-rendezvous-',[StringComparison]::Ordinal) -or
            $mode.StartsWith('files-facts-ack-commit-primary-prepare-RouteAcceptance-start-rendezvous-',[StringComparison]::Ordinal) -or
            $mode.StartsWith('files-facts-ack-commit-primary-prepare-Wrapper-start-analyzer-',[StringComparison]::Ordinal) -or
            $mode.StartsWith('files-facts-ack-commit-primary-prepare-RouteAcceptance-start-analyzer-',[StringComparison]::Ordinal) -or
            $mode -cin @('files-facts-ack-commit-primary-prepare-Wrapper-worker-reports',
                'files-facts-ack-commit-primary-prepare-RouteAcceptance-worker-reports',
                'files-facts-ack-commit-primary-prepare-Wrapper-legacy-isolation',
                'files-facts-ack-commit-primary-prepare-RouteAcceptance-legacy-isolation',
                'files-facts-ack-commit-primary-prepare-Wrapper-start-auto-terminal',
                'files-facts-ack-commit-primary-prepare-RouteAcceptance-start-auto-terminal')) {
            . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferFrontierGraceHandoff.ps1')
            $catalog=Get-FileTransferCoordinatorEvidenceCatalog
            $semanticEvents=[string[]]@($semanticEvents+$catalog.SnapshotAuthorityV1+$catalog.GenerationStampedEffects+
                $catalog.NonAuthorityEvidence+(Get-FileTransferFallbackFrontierGraceCorrelationEventNames)+@('filetransfer_frontier_payload_marker') | Sort-Object -Unique)
        }
        if($mode -cin @('files-facts-ack-commit-primary-worker-witnesses-maximum','files-facts-ack-commit-primary-worker-witnesses-over-cap')) {
            $semanticEvents+=[string[]]@('frontier_witness_fixture')
        }
        $result=$binding.TryProcessObservedLedger($semanticEvents)
        if($null -eq $result -or $null -eq $binding.TryCopySemanticEvidence()) {throw 'Parent registration original inputs missing'}
        if($mode -cne 'before-persistence') {
            $evidence=$binding.TryPersistEvidence()
            if($null -eq $evidence) {throw 'Parent registration persistence missing'}
        }
    }
    if($mode.StartsWith('rendezvous-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiParentRendezvous.ps1')
        return
    }
    $work=$owner.GetType().GetField('frontierWork',$flags).GetValue($owner); $elapsedBefore=$work.ElapsedTicks
    if($mode -ceq 'budget') {
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*11))
    }
    $handoff=$binding.TryAttachParentHandoff()
    $rejected=$mode -cin @('unbound','relay','missing','early','before-persistence','request-id','parent-process','gui-process','parent-incarnation','gui-incarnation','digest','reserved','nonce','version','state','budget')
    if($rejected) {
        if($null -ne $handoff -or $binding.IsCurrent) {throw 'Invalid parent registration accepted'}
    } else {
        if($null -eq $handoff -or -not $handoff.IsCurrent -or -not $evidence.IsCurrent -or
            $handoff.LiveProofEligible -or $handoff.NativeWriterCustodyEstablished -or $handoff.PublicationCompletionIssued) {throw 'Original parent attachment missing/authority'}
        if($work.IsRunning -or $work.ElapsedTicks -le $elapsedBefore) {throw 'Parent attachment bypassed cumulative clock'}
    }
    [void]$parentAttached.Set()
    if(-not $parentReviewed.WaitOne(20000)) {throw 'Parent review deadline'}
    if($mode.StartsWith('prepare-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiParentPreparation.ps1')
        return
    }
    if($mode.StartsWith('files-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiParentFiles.ps1')
        return
    }
    if($mode -ceq 'gui-exit') {
        if(-not $handoff.IsCurrent) {throw 'GUI exit fixture lost original attachment'}
        # Bypass managed cleanup: the original parent's held process handle
        # must independently detect exit, not just a cancellation event.
        'frontier-gui-worker=native-gui-exit'
        [Environment]::Exit(0)
    }
    if(-not $rejected) {
        if($mode -ceq 'copied-handoff') {
            $copy=$clone.Invoke($handoff,$null)
            if($copy.IsCurrent) {throw 'Copied handoff current'}
            $copy.Dispose(); if(-not $handoff.IsCurrent) {throw 'Copied handoff retired original'}
        }
        if($mode -ceq 'repeat-gui') {if($null -ne $binding.TryAttachParentHandoff()) {throw 'GUI parent registration repeated'}}
        elseif($mode -ceq 'legacy-complete') {
            if(-not $finalizer.Complete('binding-legacy',[IO.Path]::Combine($Directory,'legacy-only.log'),3,0,
                [string[]]@(('a'*64),('b'*64),('c'*64)),
                [string[]]@('12345678:1234567812345678','12345678:1234567812345679','12345678:1234567812345680'),
                [long[]]@(12,12,4),[long[]]@(3,3,1),[string[]]@('writer'),[long[]]@(3))) {throw 'Registration changed legacy completion'}
        }
        elseif($mode -ceq 'complete') {if($finalizer.Complete('','',0,0,$null,$null,$null,$null,$null,$null)) {throw 'Invalid legacy completion accepted'}}
        elseif($mode -ceq 'closed-evidence') {
            $resource=$evidence.GetType().GetField('Resource',$flags).GetValue($evidence)
            $resource.GetType().GetField('file',$flags).GetValue($resource).Dispose()
        } elseif($mode -cin @('parent-dispose','request-dispose','repeat-parent','state-regression','legacy-body')) {
            if($handoff.IsCurrent) {throw 'Parent retirement did not revoke GUI attachment'}
        } else {$handoff.Dispose()}
        if($handoff.IsCurrent -or $evidence.IsCurrent) {throw 'Parent handoff retained original evidence'}
    }
    if($binding.IsCurrent -or $proof.IsCurrent -or ($null -ne $result -and $result.StreamCapture.IsCurrent)) {throw 'Parent handoff retained optional input set'}
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) {throw 'Parent registration retained payloads'}
    [IO.File]::AppendAllText($source,'parent-registration-released')
    $destination=[IO.Path]::Combine($Directory,'filetransfer-frontier-finalizer-evidence.json')
    if([IO.File]::Exists($destination)) {[IO.File]::AppendAllText($destination,'released')}
} finally {
    if($null -ne $binding) {$binding.Dispose()}
    if($null -ne $result) {$result.StreamCapture.Dispose()}
    if($null -ne $parentAttached) {$parentAttached.Dispose()}
    if($null -ne $parentReviewed) {$parentReviewed.Dispose()}
}
