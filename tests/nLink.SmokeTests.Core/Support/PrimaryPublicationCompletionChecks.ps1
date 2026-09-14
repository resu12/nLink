param([string]$Mode,$Custody,$Evaluations,$Context,$Prepared)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
foreach($name in @('Custody','Evaluations','Context','Prepared')) {
    Set-Variable -Name $name -Option Private -Value (Get-Variable -Name $name -ValueOnly)
}
$originalNative=$Context.NativeOwner; $originalInputs=$Context.NativeInputs; $originalView=$Context.FullView
$copyNative=$null; $completion=$null
$candidateContext=$Context; $candidateCustody=$Custody; $candidatePair=$Evaluations; $candidatePrepared=$Prepared
$expected=$Mode -like 'valid-*' -or $Mode -cin @('completion-json-poison','completion-formatter-isolation',
    'completion-native-freeze-after','completion-source-freeze-after','completion-worker-freeze-after')
foreach($name in @('candidateContext','candidateCustody','candidatePair','candidatePrepared','originalNative','originalInputs','originalView')) {
    Set-Variable -Name $name -Option Private -Value (Get-Variable -Name $name -ValueOnly)
}
if($Mode -ceq 'completion-formatter-isolation') {
    $savedCanonical=(Get-Command ConvertTo-FileTransferLocalCarrierJson).ScriptBlock
    function ConvertTo-FileTransferLocalCarrierJson {
        param($Value)
        foreach($name in @('Custody','Evaluations','Context','Prepared','candidateContext','candidateCustody',
            'candidatePair','candidatePrepared','originalNative','originalInputs','originalView','localPrimaryCustody','localCompletedEvaluations')) {
            $variable=Get-Variable $name -ErrorAction SilentlyContinue
            # The original harness has an unrelated integer evaluation counter
            # in its script scope. It is not the immutable invocation-pair token.
            if($name -ceq 'Evaluations' -and $null -ne $variable -and $variable.Value -is [int]) { continue }
            if($null -ne $variable) { throw "Private issuer state leaked to codec: $name" }
        }
        & $savedCanonical $Value
    }
}
try {
    switch($Mode) {
        'completion-json-poison' {
            $Prepared.PSObject.Properties.Remove('Json')
            $Prepared | Add-Member ScriptProperty Json { throw 'Diagnostic JSON must not supply trusted completion' }
        }
        'completion-row-parity' { $Prepared.CompactRowJson='{}' }
        'completion-row-budget' { $Prepared.FullRowJson='{"padding":"'+('x'*262144)+'"}'; $Prepared.CompactRowJson=$Prepared.FullRowJson }
        'completion-payload-budget' {
            $catalogJson=ConvertTo-FileTransferLocalCarrierJson @{contractVersion=$Context.FullView.ContractVersion
                catalogVersion=$Context.FullView.CatalogVersion;catalogHash=$Context.FullView.CatalogHash;boundaryHash=$Context.FullView.BoundaryHash}
            $prefix='{"padding":"'; $suffix='"}'
            $padding=262144-$Prepared.FullRowJson.Length-$Prepared.CompactRowJson.Length-$Prepared.ProvenanceJson.Length-$catalogJson.Length-$prefix.Length-$suffix.Length
            if($padding -lt 1) { throw 'Final payload budget fixture is not admitted' }
            $Prepared.WitnessJson=$prefix+('x'*$padding)+$suffix
        }
        'completion-time-budget' {
            $Context.Work.Stop()
            $elapsed=@($Context.Work.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic') | Where-Object Name -In @('elapsed','_elapsed'))
            if($elapsed.Count -ne 1) {throw 'Completion test clock shape changed'}
            $elapsed[0].SetValue($Context.Work,[long]([Diagnostics.Stopwatch]::Frequency*([NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds+1)/1000))
        }
        'completion-native-cancel' { $Context.NativeOwner.Cancel() }
        'completion-source-cancel' { $Context.SourceReader.Dispose() }
        'completion-writer-cancel' { $Context.WriterReader.Dispose() }
        'completion-context-copy' { $candidateContext=$Context.PSObject.Copy() }
        'completion-view-copy' { $Context.FullView=$Context.FullView.PSObject.Copy() }
        'completion-pair-forged' { $candidatePair=$Evaluations | ConvertTo-Json | ConvertFrom-Json }
        'completion-custody-forged' { $candidateCustody=[pscustomobject]@{RequestId=$Evaluations.RequestId} }
        'completion-no-prepared' { $candidatePrepared=$null }
        'completion-nonascii' { $Prepared.WitnessJson='{"bad":"'+[char]0x00e9+'"}' }
    }
    if($Mode -cin @('completion-native-owner-copy','completion-native-token-copy')) {
        $copyNative=[NLink.Tools.LocalNativeInputCapture]::new($Evaluations.RequestId)
        foreach($role in @('fullCapture','fullSnapshot','compactCapture','compactSnapshot')) {
            $lines=$Context.FullLines; if($role -ceq 'compactSnapshot') { $lines=$Context.CompactLines }
            if(-not $copyNative.BeginInput($role,$lines.Length)) { throw 'Copy fixture admission failed' }
            for($i=0;$i -lt $lines.Length;$i++) { if(-not $copyNative.AppendLine($i+1,$lines[$i])) { throw 'Copy fixture budget failed' } }
            if(-not $copyNative.EndInput()) { throw 'Copy fixture incomplete' }
        }
        $Context.NativeInputs=$copyNative.Take()
        if($null -eq $Context.NativeInputs) { throw 'Copy fixture token missing' }
        if($Mode -ceq 'completion-native-owner-copy') { $Context.NativeOwner=$copyNative }
    }
    $completion=New-FileTransferPrimaryPublicationCompletion $candidateCustody $candidatePair $candidateContext $candidatePrepared
    if(($null -ne $completion) -ne $expected) { throw "Completion result drift: $Mode : $($candidateContext.PublicationCompletionOmissionReason)" }
    if($Mode -ceq 'completion-payload-budget' -and $Context.Work.ElapsedMilliseconds -ge 2000) { throw 'Deadline masked final payload size control' }
    $facts=@{Issued=$expected;DiagnosticJsonUsed=$false;AuthorityEvaluations=0}
    if($expected) {
        if(-not $Custody.OwnsPublication($completion)) { throw 'Genuine completion lost ownership' }
        $bytes=$completion.CopyPayload(); $json=[Text.Encoding]::ASCII.GetString($bytes); $body=$json | ConvertFrom-Json
        $receipt=$completion.Completion
        if((ConvertTo-FileTransferLocalCarrierJson $body) -cne $json -or
            $body.format -cne 'nlink-local-publication-forwarding-v2' -or $body.scope -cne 'local_publication_forwarding' -or
            $body.request.format -cne 'nlink-local-publication-prepared-request-v1' -or
            $body.peerLineageStatus -cne 'not_evaluated' -or $receipt.RequestId -ne $Context.SourceOwner.RequestId -or
            $receipt.WorkerProcessId -ne $PID -or $receipt.WorkerCreationFileTime -ne $Context.SourceOwner.ProcessCreationFileTime -or
            $receipt.FullInvocationId -ne $Evaluations.FullInvocationId -or $receipt.CompactInvocationId -ne $Evaluations.CompactInvocationId -or
            $receipt.PayloadBytes -ne $bytes.Length -or $receipt.PayloadSha256 -cne (Get-FileTransferLocalCarrierHash $json) -or
            $receipt.RequestSha256 -cne (Get-FileTransferLocalCarrierHash (ConvertTo-FileTransferLocalCarrierJson $body.request)) -or
            $receipt.InputDescriptorsSha256 -cne (Get-FileTransferLocalCarrierHash (ConvertTo-FileTransferLocalCarrierJson $body.inputs)) -or
            $receipt.SelectedRowsSha256 -cne (Get-FileTransferLocalCarrierHash (ConvertTo-FileTransferLocalCarrierJson $body.selection)) -or
            $receipt.CatalogSha256 -cne (Get-FileTransferLocalCarrierHash (ConvertTo-FileTransferLocalCarrierJson $body.inputs.catalog)) -or
            (ConvertTo-FileTransferLocalCarrierJson $body.selection.row) -cne $Prepared.FullRowJson -or
            (ConvertTo-FileTransferLocalCarrierJson $body.selection.witness) -cne $Prepared.WitnessJson -or
            (ConvertTo-FileTransferLocalCarrierJson $body.selection.provenance) -cne $Prepared.ProvenanceJson) { throw 'Completion canonical bytes or independent digests changed' }
        foreach($role in @('fullCapture','fullSnapshot','compactCapture','compactSnapshot')) {
            $descriptor=$body.inputs.native.$role; $input=$originalInputs.Get($role)
            if($descriptor.hashDomain -cne 'nlink-ordered-utf16le-v1' -or $descriptor.sha256 -cne $input.Sha256 -or
                $descriptor.recordCount -ne $input.RecordCount -or $descriptor.codeUnitCount -ne $input.CodeUnitCount -or
                $null -ne $descriptor.PSObject.Properties['path']) { throw 'Native input descriptor changed or received a fake path' }
        }
        if($body.inputs.source.sha256 -cne $Context.SourceOwner.SourceSha256 -or
            $body.inputs.writer.sha256 -cne $Context.SourceOwner.WriterContext.Outputs[1].Sha256 -or
            $body.inputs.source.fileIdentity -cne $Context.SourceOwner.SourceIdentity -or
            $body.inputs.writer.fileIdentity -cne $Context.SourceOwner.WriterContext.Outputs[1].OriginalFileIdentity) { throw 'Pinned file descriptors changed' }
        foreach($value in @($completion,$receipt)) {
            if($value.GetType().GetConstructors().Count -ne 0) { throw 'Public completion constructor' }
            foreach($property in $value.GetType().GetProperties()) { if($null -ne $property.GetSetMethod()) { throw 'Writable completion property' } }
        }
        $forged=$completion | ConvertTo-Json -Depth 8 | ConvertFrom-Json; $accepted=$false
        try { $accepted=$Custody.OwnsPublication($forged) } catch { }
        if($accepted) { throw 'Matching self-checksums forged completion ownership' }
        $bytes[0]=0
        if([Text.Encoding]::ASCII.GetString($completion.CopyPayload()) -cne $json) { throw 'Candidate bytes mutated the private payload' }
        # Low-level issuance is independently one-shot, even when the caller
        # repeats every genuine argument. The already issued token stays owned.
        $again=$Custody.TryPreparePublication($Evaluations,$Context,$Context.FullLines,$Context.CompactLines,
            $Context.FullView,$Context.CompactView,$originalNative,$originalInputs,$Context.WriterReader,
            $Prepared.FullRowJson,$Prepared.CompactRowJson,$Prepared.WitnessJson,$Prepared.ProvenanceJson,'{}',1000)
        if($null -ne $again -or -not $Custody.OwnsPublication($completion)) { throw 'Completion issuance replay changed ownership' }
        $facts.PayloadBytes=$receipt.PayloadBytes; $facts.CharacterVisits=$completion.CharacterVisits; $facts.ByteVisits=$completion.ByteVisits
        $facts.OptionalMilliseconds=$Context.Work.ElapsedMilliseconds
        switch($Mode) {
            'completion-native-freeze-after' { $originalNative.Cancel() }
            'completion-source-freeze-after' { $Context.SourceReader.Dispose() }
            'completion-worker-freeze-after' { $Context.SourceOwner.Dispose() }
        }
        if($Mode -like 'completion-*-freeze-after') {
            if($Custody.OwnsPublication($completion)) { throw 'Frozen completion retained ownership' }
            $facts.FreezeInvalidated=$true
        }
    }
    return $facts
} finally {
    $Context.NativeOwner=$originalNative; $Context.NativeInputs=$originalInputs; $Context.FullView=$originalView
    $Custody.Dispose()
    if($null -ne $completion -and ($Custody.OwnsPublication($completion) -or $null -ne $completion.CopyPayload())) { throw 'Completion payload survived primary cleanup' }
    if($null -ne $copyNative) { $copyNative.Dispose() }
}
