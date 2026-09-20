param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$SourcePath,[string]$Case)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$mode=$Case.Substring('witnesses-'.Length)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'; $clone=[object].GetMethod('MemberwiseClone',$flags)
$signals=@{}; $worker=$null; $reader=$null; $custody=$null; $set=$null; $batch=$null; $rows=@(); $script:evaluations=0
$realEvaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param([AllowEmptyCollection()][string[]]$Lines)
    $script:evaluations++; & $realEvaluator -Lines $Lines
}
try {
    foreach($part in @('begin','attached','release')) {$signals[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.worker.'+$part)}
    if(-not $signals.begin.WaitOne(30000)) {throw 'Witness begin deadline'}
    $worker=[NLink.Tools.LocalRetainedSourceRequest]::TryAttachFrontier($Name,$RequestId,$SourcePath,'frontier-gui-run','frontier-gui-scenario')
    if($null -eq $worker) {throw 'Native witness worker unavailable'}
    $owned=$worker.FrontierInputs; $reader=$worker.TryOpenSource()
    $events=@(Read-FileTransferLogEvents -LogFiles @($SourcePath) -OwnedSource $reader)
    [string[]]$full=@($events | ForEach-Object RawLine); [int[]]$ordinals=@($events | ForEach-Object LineNumber)
    # This selection fixture deliberately includes a non-empty compact subset;
    # it is not a claim that these rows satisfy the real failure's proof chain.
    [string[]]$compact=@($full[1],$full[-1]); [int[]]$compactOrdinals=@($ordinals[1],$ordinals[-1])
    $context=[object]::new()
    $custody=$worker.TryBeginFrontierPrimary($reader,$context,$full,$ordinals,$compact,$compactOrdinals)
    $fullView=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $full
    $compactView=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compact
    $before=@($fullView,$compactView) | ConvertTo-Json -Depth 60 -Compress
    if($null -eq $custody -or -not $custody.RecordFull($full,$fullView) -or -not $custody.RecordCompact($compact,$compactView)) {throw 'Witness evaluation custody missing'}
    $pair=$custody.Take($context,$full,$compact,$fullView,$compactView)
    $set=$custody.TryCapturePayloads($pair,$context,$full,$compact,$fullView,$compactView)
    if($null -eq $set) {throw 'Witness original payloads missing'}
    $payload=$set.GetPayloads()[0]
    [int[]]$selection=@(1,2,$full.Length)
    if($mode -ceq 'foreign-identities') {
        foreach($kind in @('custody','pair','context','full','compact','full-view','compact-view','payloads','payload','null-payloads','null-payload')) {
            $c=$custody; $p=$pair; $x=$context; $f=$full; $k=$compact; $v=$fullView; $w=$compactView; $s=$set; $y=$payload
            switch($kind) {
                'custody' {$c=$clone.Invoke($custody,$null)}; 'pair' {$p=$clone.Invoke($pair,$null)}; 'context' {$x=[object]::new()}
                'full' {$f=[string[]]$full.Clone()}; 'compact' {$k=[string[]]$compact.Clone()}
                'full-view' {$v=$fullView.PSObject.Copy()}; 'compact-view' {$w=$compactView.PSObject.Copy()}
                'payloads' {$s=$clone.Invoke($set,$null)}; 'payload' {$y=$clone.Invoke($payload,$null)}
                'null-payloads' {$s=$null}; 'null-payload' {$y=$null}
            }
            if($null -ne $c.TryCaptureWitnesses($p,$x,$f,$k,$v,$w,$s,$y,$selection)) {throw ('Foreign witness admission: '+$kind)}
        }
        if(-not $worker.IsCurrent) {throw 'Foreign witness attempt retired original'}
    }
    switch($mode) {
        'maximum' {$selection=[int[]](1..64)}; 'over-cap' {$selection=[int[]](1..65)}
        'empty' {$selection=[int[]]@()}; 'null' {$selection=$null}; 'duplicate' {$selection=[int[]]@(1,1)}
        'order' {$selection=[int[]]@(2,1)}; 'zero' {$selection=[int[]]@(0)}; 'out-of-range' {$selection=[int[]]@($full.Length+1)}
        'pre-mutation' {$full[0]=[string]::new($full[0].ToCharArray())}
        'record-budget' {$owned.GetType().GetField('evaluationVisits',$flags).SetValue($owned,2097152L)}
        'budget' {
            $watch=$owned.GetType().GetField('work',$flags).GetValue($owned)
            $elapsed=@($watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*21))
        }
    }
    if($mode -cin @('range-start','range-end')) {
        # Deliberate private-fact fault injection, not a public mutation path.
        $facts=$owned.GetType().GetField('facts',$flags).GetValue($owned)
        $starts=$facts.GetType().GetField('lineStarts',$flags).GetValue($facts)
        $manifests=$facts.GetType().GetField('manifests',$flags).GetValue($facts)
        $fact=$manifests[0].GetType().GetField('Payloads',$flags).GetValue($manifests[0])[0]
        $range=$fact.GetType().GetField('Completion',$flags).GetValue($fact)
        $offset=$range.GetType().GetField('Offset',$flags).GetValue($range)
        if($mode -ceq 'range-end') {$offset+=$range.GetType().GetField('Length',$flags).GetValue($range)+2L}
        $index=[Array]::IndexOf($starts,[long]$offset); if($index -lt 0) {throw 'Range fault fixture missing exact edge'}
        $starts[$index]++
    }
    $rejected=$mode -cin @('over-cap','empty','null','duplicate','order','zero','out-of-range','pre-mutation','record-budget','budget','range-start','range-end')
    $batch=$custody.TryCaptureWitnesses($pair,$context,$full,$compact,$fullView,$compactView,$set,$payload,$selection)
    if(($null -eq $batch) -ne $rejected) {
        $facts=$owned.GetType().GetField('facts',$flags).GetValue($owned)
        $starts=$facts.GetType().GetField('lineStarts',$flags).GetValue($facts)
        $manifests=$facts.GetType().GetField('manifests',$flags).GetValue($facts)
        $fact=$manifests[0].GetType().GetField('Payloads',$flags).GetValue($manifests[0])[0]
        foreach($name in @('Completion','Marker')) {
            $range=$fact.GetType().GetField($name,$flags).GetValue($fact); $type=$range.GetType()
            $offset=$type.GetField('Offset',$flags).GetValue($range); $length=$type.GetField('Length',$flags).GetValue($range)
            $first=$type.GetField('First',$flags).GetValue($range); $last=$type.GetField('Last',$flags).GetValue($range)
            Write-Output "witness-range $name offset=$offset length=$length first=$first last=$last startIndex=$([Array]::IndexOf($starts,[long]$offset)) endIndex=$([Array]::IndexOf($starts,[long]($offset+$length+2)))"
        }
        throw ('Witness capture mismatch: '+$mode)
    }
    if($null -ne $batch) {
        $rows=$batch.GetWitnesses()
        if($rows.Count -ne $selection.Length -or -not [object]::ReferenceEquals($batch.Payload,$payload) -or
            $batch.GetType().GetConstructors().Length -or $batch.LiveProofEligible -or $batch.NativeWriterCustodyEstablished -or $batch.PublicationCompletionIssued) {throw 'Witness batch incomplete or authoritative'}
        $diagnostics=[Collections.Generic.List[object]]::new()
        foreach($row in $rows) {
            $index=$row.FullIndex-1; $compactIndex=[Array]::IndexOf($compact,$full[$index])+1
            if(-not $batch.Owns($row) -or -not [object]::ReferenceEquals($row.Text,$full[$index]) -or
                $row.CompactIndex -ne $compactIndex -or @($row.GetType().GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) {throw 'Witness original row mismatch'}
            $sha=[Security.Cryptography.SHA256]::Create()
            try {$hash=[BitConverter]::ToString($sha.ComputeHash([Text.UTF8Encoding]::new($false,$true).GetBytes($row.Text))).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
            if($row.LineSha256 -cne $hash) {throw 'Witness exact decoded line hash mismatch'}
            $diagnostics.Add(@{Full=$row.FullIndex;Line=$row.SourceLine;Offset=$row.ByteOffset;Bytes=$row.RawByteCount})
        }
        'frontier-primary-witnesses-facts='+(@{Rows=$diagnostics.ToArray();Completion=$batch.Completion;Marker=$batch.Marker} | ConvertTo-Json -Depth 6 -Compress)
        $selection[0]=999
        if($batch.GetWitnesses()[0].FullIndex -ne 1) {throw 'Caller selection mutated captured witness'}
        if($mode -ceq 'foreign-identities') {
            $copy=$clone.Invoke($batch,$null); $rowCopy=$clone.Invoke($rows[0],$null)
            if($copy.IsCurrent -or $null -ne $copy.GetWitnesses() -or $copy.Owns($rows[0]) -or $batch.Owns($rowCopy) -or $batch.Owns($null)) {throw 'Copied witnesses acquired custody'}
            $copy.Dispose(); $export=$batch.GetWitnesses(); $export[0]=$rowCopy
            if(-not $batch.Owns($rows[0]) -or $batch.Owns($export[0])) {throw 'Exported array changed witness ownership'}
        }
        switch($mode) {
            'prepare-without-join' {if($null -ne $batch.TryPrepareDiagnostic()) {throw 'Physical witnesses bypassed original semantic join'}}
            'repeat' {if($null -ne $custody.TryCaptureWitnesses($pair,$context,$full,$compact,$fullView,$compactView,$set,$payload,([int[]]@(1,2,$full.Length)))) {throw 'Witness capture replayed'}}
            'post-mutation' {$compact[0]+='changed'}; 'batch-dispose' {$batch.Dispose()}; 'payload-dispose' {$set.Dispose()}; 'source-dispose' {$reader.Dispose()}
        }
    }
    [void]$signals.attached.Set()
    if(-not $signals.release.WaitOne(30000)) {throw 'Witness review deadline'}
    $positive=$mode -cin @('valid','maximum','foreign-identities')
    if($mode -ceq 'parent-cancel' -and -not [Threading.SpinWait]::SpinUntil([Func[bool]]{$null -eq $rows[0].Text},5000)) {throw 'Idle cancellation retained witness text'}
    if($null -ne $batch -and $batch.IsCurrent -ne $positive) {throw 'Witness currentness mismatch'}
    if($worker.IsCurrent -ne $positive -or $owned.IsCurrent -ne $positive) {throw 'Witness native retirement mismatch'}
    if(-not $positive -and $null -ne $batch -and ($null -ne $batch.GetWitnesses() -or $batch.Owns($rows[0]) -or $null -ne $rows[0].Text)) {throw 'Retired witnesses retained ownership/text'}
    $after=@($fullView,$compactView) | ConvertTo-Json -Depth 60 -Compress
    if($before -cne $after -or $script:evaluations -ne 2 -or $null -ne $worker.TryOpenWriter()) {throw 'Witness selection changed original evaluations or opened legacy writer'}
    $worker.Dispose()
    if($null -ne $batch -and ($batch.IsCurrent -or @($rows | Where-Object {$null -ne $_.Text}).Count)) {throw 'Witness cleanup leaked cached text'}
    'frontier-primary-worker='+$Case
    'frontier-primary-witnesses='+$mode
} finally {
    if($null -ne $batch) {$batch.Dispose()}; if($null -ne $set) {$set.Dispose()}; if($null -ne $custody) {$custody.Dispose()}
    if($null -ne $reader) {$reader.Dispose()}; if($null -ne $worker) {$worker.Dispose()}
    foreach($event in $signals.Values) {$event.Dispose()}
}
