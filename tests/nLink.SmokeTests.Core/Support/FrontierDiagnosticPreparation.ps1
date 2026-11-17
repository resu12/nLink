$prepareMode=$mode.Substring('prepare-'.Length)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'; $clone=[object].GetMethod('MemberwiseClone',$flags)
$batch=$result.Batch
if($prepareMode -ceq 'copied-batch') {
    $copy=$clone.Invoke($batch,$null)
    if($null -ne $copy.TryPrepareDiagnostic()) {throw 'Copied batch prepared output'}
    $copy.Dispose(); if(-not $batch.IsCurrent) {throw 'Copied batch retired original'}
}
switch($prepareMode) {
    'pre-failure' {$failure.Reason='changed-after-join'}
    'pre-view' {$compactView.CatalogHash='0'*64}
    'record-budget' {$worker.FrontierInputs.GetType().GetField('evaluationVisits',$flags).SetValue($worker.FrontierInputs,[long][NLink.Tools.LocalPublicationInputLimits]::EvaluationVisits)}
    'budget' {
        $owned=$worker.FrontierInputs; $watch=$owned.GetType().GetField('work',$flags).GetValue($owned)
        $elapsed=@($watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*181))
    }
}
$preparation=$batch.TryPrepareDiagnostic()
$reject=$prepareMode -cin @('pre-failure','pre-view','budget','record-budget')
if(($null -eq $preparation) -ne $reject) {throw ('Preparation admission mismatch: '+$prepareMode)}
if($null -ne $preparation) {
    $descriptors=$preparation.GetInputDescriptors()
    if($descriptors.Count -ne $worker.FrontierInputs.FileCount -or $descriptors.Count -ne 11 -or
        $descriptors[4].FileIdentity -cne $worker.SourceIdentity -or $descriptors[4].ByteCount -ne $worker.SourceBytes -or
        $descriptors[10].ObservationId -ne $native.ObservationId -or $descriptors[10].ByteCount -ne $native.ByteCount -or
        $preparation.RequestId -ne $pair.RequestId -or $preparation.FullInvocationId -ne $pair.FullInvocationId -or
        $preparation.CompactInvocationId -ne $pair.CompactInvocationId -or $preparation.WorkerProcessId -ne $PID -or
        $preparation.WorkerCreationFileTime -ne $worker.ProcessCreationFileTime -or
        $preparation.SourceSha256 -cne $worker.SourceSha256 -or $preparation.DiagnosticSnapshot -cne $batch.DiagnosticSnapshot -or
        $preparation.LiveProofEligible -or $preparation.NativeWriterCustodyEstablished -or $preparation.PublicationCompletionIssued -or
        $preparation.GetType().GetConstructors().Length) {throw 'Preparation native descriptor/identity mismatch'}
    foreach($hash in @($preparation.ScopeSha256,$preparation.OfferSha256,$preparation.FactsReceiptSha256)) {
        if($hash -cnotmatch '\A[a-f0-9]{64}\z') {throw 'Preparation binding hash missing'}
    }
    for($i=0;$i -lt 10;$i++) {if($descriptors[$i].Role -ne $i -or $descriptors[$i].WriterSlot -ne -1) {throw 'Descriptor roles changed'}}
    if($descriptors[10].Role -ne 10 -or $descriptors[10].WriterSlot -ne 0 -or
        $descriptors[0].GetType().GetProperty('FileIdentity').GetSetMethod()) {throw 'Descriptor is mutable or payload slot changed'}
    switch($prepareMode) {
        'copied-preparation' {
            $copy=$clone.Invoke($preparation,$null)
            if($copy.IsCurrent -or $null -ne $copy.GetInputDescriptors()) {throw 'Copied preparation acquired original'}
            $copy.Dispose(); if(-not $preparation.IsCurrent) {throw 'Copied preparation retired original'}
        }
        'descriptor-copy' {$descriptors[0]=$null; if($null -eq $preparation.GetInputDescriptors()[0]) {throw 'Export replaced private descriptors'}}
        'post-failure' {$failure.Variant='changed-after-preparation'}
        'post-view' {$fullView.AuthoritativeFamilies[0]='changed'}
        'post-proof' {$original.Recovered=$true}
        'post-completion' {$completion.boundaryHash='0'*64}
        'post-index' {$failure.Event.RawLine=[string]::new($failure.Event.RawLine.ToCharArray())}
        'post-candidate' {$candidates[0]=$failure.PSObject.Copy()}
        'script-property' {
            $script:diagnosticGetterCalls=0
            $failure.PSObject.Properties.Remove('Reason')
            $failure | Add-Member -MemberType ScriptProperty -Name Reason -Value {$script:diagnosticGetterCalls++; 'unexpected'}
        }
        'repeat' {if($null -ne $batch.TryPrepareDiagnostic()) {throw 'Preparation replay accepted'}}
        'dispose' {$preparation.Dispose()}
    }
}
$prepareCurrent=$prepareMode -cin @('valid','copied-batch','copied-preparation','descriptor-copy')
# Snapshot after deliberate fixture edits, before native revalidation. The
# native check must neither evaluate authority nor mutate the original report.
$before=@($fullView,$compactView,$proof) | ConvertTo-Json -Depth 70 -Compress
if($null -ne $preparation -and $prepareMode -cne 'parent-cancel' -and $preparation.IsCurrent -ne $prepareCurrent) {throw 'Preparation accepted changed original state'}
if($prepareMode -ceq 'script-property' -and $script:diagnosticGetterCalls -ne 0) {throw 'Native state guard executed a script property'}
