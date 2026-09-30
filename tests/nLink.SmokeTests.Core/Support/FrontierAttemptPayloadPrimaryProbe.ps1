param([ValidateSet('Launch','Bind','Closed')][string]$Stage)
$private:primaryFault=$env:NLINK_TEST_PAYLOAD_PRIMARY_CASE
$private:validPair=$primaryFault -cin @('valid','canonical-source','canonical-fallback','canonical-tail-late-publication','evaluation-extended','evaluation-channel-retired')
$private:fields=[Reflection.BindingFlags]'Instance,NonPublic'
$private:clone=[object].GetMethod('MemberwiseClone',$fields)
if($Stage -ceq 'Launch') {
    $private:files=$Preparation.Inputs.GetType().GetField('transferPair',$fields).GetValue($Preparation.Inputs)
    if($null -eq $files) {throw 'Actual pair did not precede native relay retirement'}
    $script:primaryPayloadHandles=$files.GetType().GetField('Files',$fields).GetValue($files)
    if($script:payloadParent.IsCurrent -or @($script:primaryPayloadHandles | Where-Object IsClosed).Count) {throw 'Original parent retirement or primary duplication changed'}
    if($primaryFault -ceq 'foreign-completion') {
        $private:packet=$files.GetType().GetField('packet',$fields).GetValue($files)
        $private:offset=20+[BitConverter]::ToInt32($packet,16); $packet[$offset]=$packet[$offset] -bxor 1
    }
    return
}
if($Stage -ceq 'Bind') {
    $script:primaryPayloadChecked=$false
    if($null -eq $nativePrimaryCustody -or $null -eq $nativePrimaryPair -or $null -eq $nativePrimaryPayloads) {throw 'Original native evaluator/payload catalog missing'}
    Write-Host ('attempt-payload-primary-inputs; full_lines='+$nativePrimaryFull.Length+'; diagnostic_receipts='+@($nativePrimaryPayloads.GetPayloads()).Count)
    if($primaryFault -ceq 'canonical-source') {
        foreach($view in @($fullSnapshotEvaluation,$compactSnapshotEvaluation)) {
            if($view.Mode -cne 'SnapshotV1Authoritative' -or -not $view.StreamComplete -or $view.ArtifactEvidenceVerdict -cne 'pass') {throw 'Canonical native source lost coordinator evidence'}
        }
        if($nativePrimaryFull.Length -le 2 -or @($nativePrimaryPayloads.GetPayloads()).Count -ne 0) {throw 'Canonical source was substituted or gained a diagnostic receipt'}
    }
    $private:inputs=$nativePrimaryCustody.GetType().GetField('inputs',$fields).GetValue($nativePrimaryCustody)
    $private:files=$inputs.GetType().GetField('transferPair',$fields).GetValue($inputs)
    $script:workerPayloadHandles=if($null -ne $files) {@($files.GetType().GetField('Files',$fields).GetValue($files))} else {@()}
    if(($null -ne $files) -ne ($primaryFault -cne 'foreign-completion')) {throw 'Worker accepted wrong completion or lost native transfer pair'}
    $private:arguments=@($nativePrimaryPair,$nativePrimaryContext,$nativePrimaryFull,$nativePrimaryCompact,$fullSnapshotEvaluation,$compactSnapshotEvaluation)
    # The canonical tail observes the production post-report binding. It must
    # not consume that one-shot operation from this test-only pre-report probe.
    if($primaryFault -ceq 'canonical-tail-late-publication') {return}
    if($primaryFault.StartsWith('evaluation-')) {
        $private:work=$inputs.GetType().GetField('work',$fields).GetValue($inputs)
        $private:elapsed=@($work.GetType().GetFields($fields) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        if($work.IsRunning) {throw 'Pair regression requires the stopped original clock'}
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*21))
        if(-not $nativePrimaryCustody.Owns($arguments[0],$arguments[1],$arguments[2],$arguments[3],$arguments[4],$arguments[5])) {throw 'Evaluation custody expired before pair binding at 21 seconds'}
        $private:visits=$inputs.GetType().GetField('evaluationVisits',$fields)
        $private:beforeForeign=$visits.GetValue($inputs)
    }
    if($null -ne $clone.Invoke($nativePrimaryCustody,$null).TryBindTransferPair($arguments[0],$arguments[1],$arguments[2],$arguments[3],$arguments[4],$arguments[5]) -or
        $null -ne $nativePrimaryCustody.TryBindTransferPair($clone.Invoke($nativePrimaryPair,$null),$arguments[1],$arguments[2],$arguments[3],$arguments[4],$arguments[5]) -or
        $null -ne $nativePrimaryCustody.TryBindTransferPair($arguments[0],$arguments[1],[string[]]$arguments[2].Clone(),$arguments[3],$arguments[4],$arguments[5]) -or
        $null -ne $nativePrimaryCustody.TryBindTransferPair($arguments[0],[object]::new(),$arguments[2],$arguments[3],$arguments[4],$arguments[5])) {throw 'Copied or foreign evaluation bound actual transfer pair'}
    if($primaryFault -ceq 'pair-retired') {$files.Dispose()}
    elseif($primaryFault -ceq 'owner-retired') {$inputs.Dispose()}
    if($primaryFault.StartsWith('evaluation-')) {
        if($work.ElapsedMilliseconds -lt 21000 -or $work.IsRunning -or $visits.GetValue($inputs) -ne $beforeForeign) {throw 'Foreign calls reset or charged the original pair budget'}
        if($primaryFault -ceq 'evaluation-expired') {$elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*61))}
    }
    $script:transferEvaluation=$nativePrimaryCustody.TryBindTransferPair($arguments[0],$arguments[1],$arguments[2],$arguments[3],$arguments[4],$arguments[5])
    $private:actualPair=$null -ne $script:transferEvaluation
    $private:bindingDiagnostic='attempt-payload-primary-binding; case='+$primaryFault+'; expected='+$validPair+'; actual='+$actualPair
    [Console]::Error.WriteLine($bindingDiagnostic)
    if($primaryFault.StartsWith('evaluation-')) {
        [Console]::Error.WriteLine('pair-evaluation-budget; case='+$primaryFault+'; elapsed_ms='+$work.ElapsedMilliseconds+
            '; rejected='+$inputs.GetType().GetField('PrimarySourceRejectionForTest',$fields).GetValue($inputs))
    }
    if($primaryFault -ceq 'canonical-fallback') {Write-FrontierPrimaryCheckpoint 'binding' $bindingDiagnostic}
    if($actualPair -ne $validPair) {throw 'Actual transfer evaluation association outcome changed'}
    if($validPair) {
        if(-not $script:transferEvaluation.IsCurrent -or $script:transferEvaluation.ByteCount -ne 268435456 -or
            $script:transferEvaluation.Sha256 -cne '3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699' -or
            $clone.Invoke($script:transferEvaluation,$null).IsCurrent -or
            $script:transferEvaluation.LiveProofEligible -or $script:transferEvaluation.TransferIntegrityEstablished -or
            $script:transferEvaluation.PublicationCompletionIssued -or
            $null -ne $nativePrimaryCustody.TryBindTransferPair($arguments[0],$arguments[1],$arguments[2],$arguments[3],$arguments[4],$arguments[5])) {throw 'Bound pair count/hash/copy/repeat/authority changed'}
    }
    if($primaryFault.StartsWith('evaluation-')) {
        if($primaryFault -ceq 'evaluation-channel-retired') {
            $private:channel=$inputs.GetType().GetField('transferChannel',$fields).GetValue($inputs)
            $channel.Dispose()
            if($script:transferEvaluation.IsCurrent -or $script:transferEvaluation.ByteCount -ne 0 -or
                $null -ne $script:transferEvaluation.Sha256) {throw 'Retired pair channel remained current under evaluation allowance'}
        } elseif($primaryFault -ceq 'evaluation-expired') {
            if($work.ElapsedMilliseconds -lt 61000 -or
                $inputs.GetType().GetField('PrimarySourceRejectionForTest',$fields).GetValue($inputs) -cne 'resource:budget') {throw 'Expired pair did not retain its original budget rejection'}
            if($null -ne $nativePrimaryCustody.TryBindTransferPair($arguments[0],$arguments[1],$arguments[2],$arguments[3],$arguments[4],$arguments[5]) -or
                $visits.GetValue($inputs) -ne $beforeForeign) {throw 'Expired pair reopened or refilled its visits'}
        }
        if($work.ElapsedMilliseconds -lt 21000 -or $work.IsRunning) {throw 'Pair binding changed the original cumulative clock'}
        Write-Host ('attempt-payload-evaluation-budget='+$primaryFault)
    }
    $script:primaryPayloadBound=$true
    return
}
if(-not $script:primaryPayloadBound) {
    $private:cleanupDiagnostic='attempt-payload-primary-cleanup; bind_complete=False; open_handles=not_checked; evaluation_current=not_checked'
    [Console]::Error.WriteLine($cleanupDiagnostic)
    if($primaryFault -ceq 'canonical-fallback') {Write-FrontierPrimaryCheckpoint 'cleanup' $cleanupDiagnostic}
    throw 'Original payload binding did not complete before retirement'
}
$private:openHandles=@($script:workerPayloadHandles | Where-Object {-not $_.IsClosed}).Count
if($openHandles) {
    $private:cleanupDiagnostic='attempt-payload-primary-cleanup; bind_complete=True; open_handles='+$openHandles+'; evaluation_current=not_checked'
    [Console]::Error.WriteLine($cleanupDiagnostic)
    if($primaryFault -ceq 'canonical-fallback') {Write-FrontierPrimaryCheckpoint 'cleanup' $cleanupDiagnostic}
    throw 'Original evaluation retirement left actual payload handles open'
}
$private:evaluationCurrent=$false; $private:evaluationState='absent'
if($null -ne $script:transferEvaluation) {
    $private:evaluationCurrent=$script:transferEvaluation.IsCurrent
    $private:evaluationState=[string]$evaluationCurrent
}
$private:cleanupDiagnostic='attempt-payload-primary-cleanup; bind_complete=True; open_handles=0; evaluation_current='+$evaluationState
[Console]::Error.WriteLine($cleanupDiagnostic)
if($primaryFault -ceq 'canonical-fallback') {Write-FrontierPrimaryCheckpoint 'cleanup' $cleanupDiagnostic}
if($evaluationCurrent) {throw 'Original evaluation remained current after retirement'}
if($primaryFault -ceq 'canonical-source') {
    $private:selected=if($null -eq $firstCompactProofChainFailure) {'(none)'} else {$firstCompactProofChainFailure.Class}
    if($selected -ceq 'filetransfer_fallback_frontier_recent_progress_grace_recheck_missing') {throw 'Regular-only source unexpectedly selected a fallback witness'}
    Write-Host ('attempt-payload-canonical-source; selected_failure='+$selected+'; diagnostic_witness=unavailable')
}
$script:primaryPayloadChecked=$true
