param([ValidateSet('Launch','Before','Bound','After','Closed')][string]$Stage)
$private:primaryCase=$env:NLINK_TEST_PRODUCTION_PRIMARY_PAYLOAD
$private:fields=[Reflection.BindingFlags]'Instance,NonPublic'
if($Stage -ceq 'Launch') {
    $private:pair=$Preparation.Inputs.GetType().GetField('transferPair',$fields).GetValue($Preparation.Inputs)
    if(($null -ne $pair) -ne ($primaryCase -cne 'parent-rejected') -or $script:productionParent.IsCurrent) {throw 'Primary duplication or original parent retirement changed'}
    $script:primaryPayloadHandles=if($null -ne $pair) {@($pair.GetType().GetField('Files',$fields).GetValue($pair))} else {@()}
    if(@($script:primaryPayloadHandles | Where-Object IsClosed).Count) {throw 'Primary lost duplicated payload files'}
    if($primaryCase -ceq 'foreign-completion') {
        $packet=$pair.GetType().GetField('packet',$fields).GetValue($pair)
        $offset=20+[BitConverter]::ToInt32($packet,16); $packet[$offset]=$packet[$offset] -bxor 1
    }
    return
}
if($Stage -ceq 'Before') {
    . (Join-Path $RepoRoot 'tools/FileTransferOps/SecondTransferReport.ps1')
    if($null -eq $private:nativePrimaryCustody -or $null -eq $private:nativePrimaryPair -or
        $null -eq $private:nativePrimaryPayloads -or $script:evaluations -ne 3 -or $script:nativeReads -ne 1) {throw 'Original evaluator custody or pass count missing'}
    $script:productionInputs=$private:nativePrimaryCustody.GetType().GetField('inputs',$fields).GetValue($private:nativePrimaryCustody)
    $pair=$script:productionInputs.GetType().GetField('transferPair',$fields).GetValue($script:productionInputs)
    if(($null -ne $pair) -ne ($primaryCase -cnotin @('parent-rejected','foreign-completion'))) {throw 'Worker accepted wrong pair or lost original payload'}
    $script:productionWorkerHandles=if($null -ne $pair) {@($pair.GetType().GetField('Files',$fields).GetValue($pair))} else {@()}
    if($private:nativePrimaryCustody.GetType().GetField('transferBindingAttempted',$fields).GetValue($private:nativePrimaryCustody)) {throw 'Fixture consumed the production binding before reports'}
    $script:productionReports=@{}
    foreach($file in Get-ChildItem -LiteralPath $ArtifactDir -File -Filter '*summary.json') {
        $script:productionReports[$file.FullName]=Get-FileTransferSecondReportSha256 -Path $file.FullName
    }
    if($script:productionReports.Count -lt 4) {throw 'Original reports were not written before diagnostic binding'}
    $script:productionOriginalFailure=$firstCompactProofChainFailure
    if($primaryCase -ceq 'gate-not-selected') {
        if($null -ne $firstCompactProofChainFailure -and $firstCompactProofChainFailure.Class -ceq 'filetransfer_fallback_frontier_recent_progress_grace_recheck_missing') {throw 'Regular fixture unexpectedly selected fallback witness'}
    } else {
        # Controlled diagnostic-gate input only, after all original reports.
        # Do not alter the source, views, proof, candidates, or acceptance.
        $firstCompactProofChainFailure=[pscustomobject]@{Class='filetransfer_fallback_frontier_recent_progress_grace_recheck_missing'}
    }
    if($primaryCase -ceq 'pair-retired') {$pair.Dispose()}
    return
}
if($Stage -ceq 'Bound') {
    $expectedBinding=$primaryCase -ceq 'valid'
    $attempted=$private:nativePrimaryCustody.GetType().GetField('transferBindingAttempted',$fields).GetValue($private:nativePrimaryCustody)
    if(-not $attempted -or ($null -ne $private:nativePrimaryTransfer) -ne $expectedBinding) {throw 'Production diagnostic binding outcome changed'}
    $script:productionTransfer=$private:nativePrimaryTransfer
    if($expectedBinding) {
        if(-not $script:productionTransfer.IsCurrent -or $script:productionTransfer.ByteCount -ne 268435456 -or
            $script:productionTransfer.Sha256 -cne '3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699' -or
            $script:productionTransfer.LiveProofEligible -or $script:productionTransfer.TransferIntegrityEstablished -or
            $script:productionTransfer.PublicationCompletionIssued) {throw 'Production transfer identity or authority changed'}
        if($null -ne $private:nativePrimaryCustody.TryBindTransferPair($private:nativePrimaryPair,$private:nativePrimaryContext,
            $private:nativePrimaryFull,$private:nativePrimaryCompact,$fullSnapshotEvaluation,$compactSnapshotEvaluation)) {throw 'Production binding reissued pair'}
    }
    $script:productionBoundObserved=$true
    return
}
if($Stage -ceq 'After') {
    $firstCompactProofChainFailure=$script:productionOriginalFailure
    if($null -ne $script:productionPrimaryError) {throw $script:productionPrimaryError}
    $selected=$primaryCase -cne 'gate-not-selected'
    $attempted=$private:nativePrimaryCustody.GetType().GetField('transferBindingAttempted',$fields).GetValue($private:nativePrimaryCustody)
    if($attempted -ne $selected -or $script:productionBoundObserved -ne $selected) {throw 'Production diagnostic gate or binding omitted'}
    if($null -ne $script:productionTransfer -and $script:productionTransfer.IsCurrent) {throw 'Rejected witness retained transfer custody'}
    foreach($path in $script:productionReports.Keys) {if((Get-FileTransferSecondReportSha256 -Path $path) -cne $script:productionReports[$path]) {throw 'Optional binding changed original report'}}
    if($script:evaluations -ne 3 -or $script:nativeReads -ne 1) {throw 'Optional binding re-evaluated authority or reread source'}
    if($primaryCase -cne 'gate-not-selected' -and $null -ne $private:nativeWitness) {throw 'Controlled failure invented a diagnostic witness'}
    $script:productionPrimaryChecked=$true
    Write-Host ('production-primary-reports-unchanged='+$primaryCase)
    return
}
if(-not $script:productionPrimaryChecked -or @($script:productionWorkerHandles | Where-Object {-not $_.IsClosed}).Count -or
    ($null -ne $script:productionTransfer -and $script:productionTransfer.IsCurrent)) {throw 'Original primary cleanup left payload custody current'}
$script:productionPrimaryClosed=$true
Write-Host 'production-primary-files-released'
