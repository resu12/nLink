param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$SourcePath,[string]$Case)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
$mode=$Case.Substring('payloads-'.Length)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'; $clone=[object].GetMethod('MemberwiseClone',$flags)
$signals=@{}; $worker=$null; $reader=$null; $custody=$null; $set=$null; $rows=@(); $replacedPin=$null
try {
    foreach($part in @('begin','attached','release')) {$signals[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.worker.'+$part)}
    if(-not $signals.begin.WaitOne(30000)) {throw 'Payload begin deadline'}
    $worker=[NLink.Tools.LocalRetainedSourceRequest]::TryAttachFrontier($Name,$RequestId,$SourcePath,'frontier-gui-run','frontier-gui-scenario')
    if($null -eq $worker) {throw 'Native payload worker unavailable'}
    $owned=$worker.FrontierInputs; $reader=$worker.TryOpenSource()
    $events=@(Read-FileTransferLogEvents -LogFiles @($SourcePath) -OwnedSource $reader)
    [string[]]$full=@($events | ForEach-Object RawLine); [int[]]$ordinals=@($events | ForEach-Object LineNumber)
    $compact=[string[]]@(); $context=[object]::new()
    $fullView=[pscustomobject]@{TestView='full'}; $compactView=[pscustomobject]@{TestView='compact'}
    $custody=$worker.TryBeginFrontierPrimary($reader,$context,$full,$ordinals,$compact,([int[]]@()))
    if($null -eq $custody -or -not $custody.RecordFull($full,$fullView) -or -not $custody.RecordCompact($compact,$compactView)) {throw 'Payload evaluation custody missing'}
    if($mode -ceq 'pre-take') {
        if($null -ne $custody.TryCapturePayloads($null,$context,$full,$compact,$fullView,$compactView)) {throw 'Unissued pair read payloads'}
    }
    $pair=$custody.Take($context,$full,$compact,$fullView,$compactView)
    if($null -eq $pair) {throw 'Original payload pair missing'}
    $facts=$owned.GetType().GetField('facts',$flags).GetValue($owned)
    $manifests=$facts.GetType().GetField('manifests',$flags).GetValue($facts)
    $payloadField=$manifests[0].GetType().GetField('Payloads',$flags)
    $payloadFacts=@($payloadField.GetValue($manifests[0]))+@($payloadField.GetValue($manifests[1]))
    if($payloadFacts.Count -lt 1) {throw 'Genuine payload fixture empty'}
    $pins=$owned.GetType().GetField('pins',$flags).GetValue($owned)
    $handles=$pins.GetType().GetField('Handles',$flags).GetValue($pins)
    if($mode -ceq 'foreign-identities') {
        $copied=$clone.Invoke($custody,$null); $copiedPair=$clone.Invoke($pair,$null)
        foreach($kind in @('custody','pair','context','full','compact','full-view','compact-view')) {
            $c=$custody; $p=$pair; $x=$context; $f=$full; $k=$compact; $v=$fullView; $w=$compactView
            switch($kind) {
                'custody' {$c=$copied}; 'pair' {$p=$copiedPair}; 'context' {$x=[object]::new()}
                'full' {$f=[string[]]$full.Clone()}; 'compact' {$k=[string[]]$compact.Clone()}
                'full-view' {$v=$fullView.PSObject.Copy()}; 'compact-view' {$w=$compactView.PSObject.Copy()}
            }
            if($null -ne $c.TryCapturePayloads($p,$x,$f,$k,$v,$w)) {throw ('Foreign payload admission: '+$kind)}
        }
        $copied.Dispose(); if(-not $worker.IsCurrent) {throw 'Foreign payload attempt retired original'}
    }
    switch($mode) {
        'pre-mutation' {$full[0]=[string]::new($full[0].ToCharArray())}
        'hash-mismatch' {$hash=$payloadFacts[0].GetType().GetField('Hash',$flags).GetValue($payloadFacts[0]); $hash[0]=$hash[0] -bxor 1}
        'partial-hash' {
            if($payloadFacts.Count -ne 2) {throw 'Partial payload fixture does not contain two originals'}
            $hash=$payloadFacts[1].GetType().GetField('Hash',$flags).GetValue($payloadFacts[1]); $hash[0]=$hash[0] -bxor 1
        }
        'pin-close' {$handles[10].Dispose()}
        'pin-swap' {$replacedPin=$handles[10]; $handles[10]=$handles[0]}
        'budget' {
            $watch=$owned.GetType().GetField('work',$flags).GetValue($owned)
            $elapsed=@($watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*181))
        }
        'record-budget' {$owned.GetType().GetField('evaluationVisits',$flags).SetValue($owned,[long][NLink.Tools.LocalPublicationInputLimits]::EvaluationVisits)}
    }
    $rejected=$mode -cin @('pre-mutation','hash-mismatch','partial-hash','pin-close','pin-swap','budget','record-budget')
    $set=$custody.TryCapturePayloads($pair,$context,$full,$compact,$fullView,$compactView)
    if(($null -eq $set) -ne $rejected) {throw ('Payload capture mismatch: '+$mode)}
    if($null -ne $set) {
        $rows=$set.GetPayloads()
        if($rows.Count -ne $payloadFacts.Count -or -not $set.IsCurrent -or
            $set.GetType().GetConstructors().Length -or @($rows[0].GetType().GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) {throw 'Payload set fabricated or incomplete'}
        $identities=[Collections.Generic.List[object]]::new()
        foreach($row in $rows) {
            if(-not $set.Owns($row)) {throw 'Original native payload row unowned'}
            $sha=[Security.Cryptography.SHA256]::Create()
            try {
                $bytes=[Text.UTF8Encoding]::new($false,$true).GetBytes($row.Text)
                $hash=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant()
                if($bytes.Length -ne $row.ByteCount -or $hash -cne $row.Sha256) {throw 'Payload text differs from original native bytes'}
            } finally {$sha.Dispose()}
            $identities.Add(@{Observation=$row.ObservationId.ToString('D');Writer=$row.WriterId;Hash=$row.Sha256;Bytes=$row.ByteCount;File=$row.FileIdentity})
        }
        'frontier-primary-payloads-facts='+($identities.ToArray() | ConvertTo-Json -Depth 4 -Compress)
        if($mode -ceq 'foreign-identities') {
            $setCopy=$clone.Invoke($set,$null); $rowCopy=$clone.Invoke($rows[0],$null)
            if($setCopy.IsCurrent -or $null -ne $setCopy.GetPayloads() -or $setCopy.Owns($rows[0]) -or $set.Owns($rowCopy) -or $set.Owns($null)) {throw 'Copied payload set/row acquired custody'}
            $setCopy.Dispose(); $export=$set.GetPayloads(); $export[0]=$rowCopy
            if(-not $set.Owns($rows[0]) -or $set.Owns($export[0]) -or -not $set.IsCurrent) {throw 'Exported array replaced original payload ownership'}
        }
        switch($mode) {
            'repeat' {if($null -ne $custody.TryCapturePayloads($pair,$context,$full,$compact,$fullView,$compactView)) {throw 'Payload capture replayed'}}
            'post-mutation' {$full[0]+='changed'}
            'source-dispose' {$reader.Dispose()}
            'custody-dispose' {$custody.Dispose()}
            'set-dispose' {$set.Dispose()}
            'worker-dispose' {$worker.Dispose()}
            'inputs-dispose' {$owned.Dispose()}
        }
    }
    [void]$signals.attached.Set()
    if(-not $signals.release.WaitOne(30000)) {throw 'Payload review deadline'}
    $positive=$mode -cin @('valid','two-payloads','both-writers','foreign-identities','pre-take')
    if($mode -ceq 'parent-cancel' -and -not [Threading.SpinWait]::SpinUntil([Func[bool]]{$null -eq $rows[0].Text},5000)) {throw 'Idle native cancellation did not clear cached payload text'}
    if($null -ne $set -and $set.IsCurrent -ne $positive) {throw 'Payload currentness mismatch'}
    if($worker.IsCurrent -ne $positive -or $owned.IsCurrent -ne $positive) {throw 'Payload native retirement mismatch'}
    if(-not $positive -and $null -ne $set -and ($null -ne $set.GetPayloads() -or $set.Owns($rows[0]) -or $null -ne $rows[0].Text)) {throw 'Retired payload retained ownership/text'}
    if($owned.LiveProofEligible -or $owned.PublicationCompletionIssued -or $owned.NativeWriterCustodyEstablished -or $null -ne $worker.TryOpenWriter()) {throw 'Payload bytes promoted publication authority'}
    $worker.Dispose()
    if($null -ne $set -and ($set.IsCurrent -or @($rows | Where-Object {$null -ne $_.Text}).Count)) {throw 'Payload cleanup leaked cached text'}
    'frontier-primary-worker='+$Case
    'frontier-primary-payloads='+$mode
} finally {
    if($null -ne $set) {$set.Dispose()}; if($null -ne $custody) {$custody.Dispose()}
    if($null -ne $reader) {$reader.Dispose()}; if($null -ne $worker) {$worker.Dispose()}
    if($null -ne $replacedPin) {$replacedPin.Dispose()}
    foreach($event in $signals.Values) {$event.Dispose()}
}
