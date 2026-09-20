param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$SourcePath,[string]$Case)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
$mode=$Case.Substring('evaluations-'.Length)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'; $clone=[object].GetMethod('MemberwiseClone',$flags)
$signals=@{}; $worker=$null; $reader=$null; $custody=$null; $pair=$null; $script:evaluationCalls=0
try {
    foreach($part in @('begin','attached','release')) {$signals[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.worker.'+$part)}
    if(-not $signals.begin.WaitOne(30000)) {throw 'Evaluation begin deadline'}
    $worker=[NLink.Tools.LocalRetainedSourceRequest]::TryAttachFrontier($Name,$RequestId,$SourcePath,'frontier-gui-run','frontier-gui-scenario')
    if($null -eq $worker) {throw 'Native evaluation worker unavailable'}
    $owned=$worker.FrontierInputs; $reader=$worker.TryOpenSource()
    $context=[object]::new(); $full=[string[]]@(); $compact=[string[]]@(); $fullOrdinals=[int[]]@(); $compactOrdinals=[int[]]@()
    $fullView=[pscustomobject]@{TestView='full'}; $compactView=[pscustomobject]@{TestView='compact'}
    if($mode -cne 'pre-eof') {
        $events=@(Read-FileTransferLogEvents -LogFiles @($SourcePath) -OwnedSource $reader)
        if(-not $reader.InputReadComplete -or $events.Count -ne 3) {throw 'Original parser/source association failed'}
        [string[]]$full=@($events | ForEach-Object RawLine); [int[]]$fullOrdinals=@($events | ForEach-Object LineNumber)
        [string[]]$compact=@($full[1],$full[2]); [int[]]$compactOrdinals=@(2,3)
        if($mode -ceq 'actual-evaluations') {
            . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
            $realEvaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
            function Get-FileTransferCoordinatorSnapshotEvaluation {
                param([AllowEmptyCollection()][string[]]$Lines)
                $script:evaluationCalls++
                & $realEvaluator -Lines $Lines
            }
            $catalog=Get-FileTransferCoordinatorEvidenceCatalog
            $compactEvents=@($events | Where-Object {$catalog.SnapshotAuthorityV1 -contains $_.EventName -or $catalog.GenerationStampedEffects -contains $_.EventName})
            [string[]]$compact=@($compactEvents | ForEach-Object RawLine); [int[]]$compactOrdinals=@($compactEvents | ForEach-Object LineNumber)
        }
    }
    switch($mode) {
        'wrong-ordinal' {$fullOrdinals[0]=2}
        'equal-line' {$full[0]=[string]::new($full[0].ToCharArray())}
        'compact-not-full' {$full=[string[]]@($full[0]); $fullOrdinals=[int[]]@(1); $compact=[string[]]@($compact[0]); $compactOrdinals=[int[]]@(2)}
        'origin-order' {$fullOrdinals[2]=1}
        'record-cap' {$full=[string[]]::new(262145); $fullOrdinals=[int[]]::new(262145)}
        'null-context' {$context=$null}
    }
    $initialReject=$mode -cin @('pre-eof','wrong-ordinal','equal-line','compact-not-full','origin-order','record-cap','null-context')
    $custody=$worker.TryBeginFrontierPrimary($reader,$context,$full,$fullOrdinals,$compact,$compactOrdinals)
    if(($null -eq $custody) -ne $initialReject) {throw ('Evaluation admission mismatch: '+$mode)}
    if($null -ne $custody) {
        # Caller-owned ordinal arrays are not retained as authority.
        $fullOrdinals[0]=999; if($compactOrdinals.Length) {$compactOrdinals[0]=999}
        if($mode -ceq 'valid') {
            $copiedWorker=$clone.Invoke($worker,$null); $copiedReader=$clone.Invoke($reader,$null)
            if($null -ne $copiedWorker.TryBeginFrontierPrimary($reader,$context,$full,$fullOrdinals,$compact,$compactOrdinals) -or
                $null -ne $worker.TryBeginFrontierPrimary($copiedReader,$context,$full,$fullOrdinals,$compact,$compactOrdinals)) {throw 'Copied owner/source began evaluation'}
            $copied=$clone.Invoke($custody,$null)
            if($copied.RecordFull($full,$fullView) -or $null -ne $copied.Take($context,$full,$compact,$fullView,$compactView)) {throw 'Copied custody acted'}
            $copied.Dispose(); $copiedWorker.Dispose(); $copiedReader.Dispose()
        }
        $recordRejected=$mode -cin @('mutated-full','mutated-compact','wrong-array','record-order','repeat-full','missing-view','budget','record-budget','repeat-begin','early-take')
        switch($mode) {
            'mutated-full' {$full[0]=[string]::new($full[0].ToCharArray())}
            'mutated-compact' {$compact[0]=[string]::new($compact[0].ToCharArray())}
            'budget' {
                $watch=$owned.GetType().GetField('work',$flags).GetValue($owned)
                $elapsed=@($watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
                $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*21))
            }
            'record-budget' {$owned.GetType().GetField('evaluationVisits',$flags).SetValue($owned,2097152L)}
        }
        if($mode -ceq 'record-order') {
            if($custody.RecordCompact($compact,$compactView)) {throw 'Compact recorded before full'}
        } elseif($mode -ceq 'repeat-begin') {
            # Keep the repeated request otherwise valid: the one-shot gate,
            # not the deliberately mutated caller maps above, must reject it.
            if($null -ne $worker.TryBeginFrontierPrimary($reader,$context,$full,([int[]]@(1,2,3)),$compact,([int[]]@(2,3)))) {throw 'Evaluation began twice'}
        } elseif($mode -ceq 'early-take') {
            if($null -ne $custody.Take($context,$full,$compact,$fullView,$compactView)) {throw 'Unrecorded views taken'}
        } else {
            if($mode -ceq 'actual-evaluations') {$fullView=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $full}
            $candidate=$full; if($mode -ceq 'wrong-array') {$candidate=[string[]]$full.Clone()}
            if($mode -ceq 'missing-view') {$fullView=$null}
            $beforeFull=$fullView | ConvertTo-Json -Depth 60 -Compress
            $recorded=$custody.RecordFull($candidate,$fullView)
            if($recorded -eq ($recordRejected -and $mode -cne 'repeat-full')) {throw 'Full result admission mismatch'}
            if($mode -ceq 'repeat-full') {if($custody.RecordFull($full,$fullView)) {throw 'Full result recorded twice'}}
            elseif($recorded) {
                if($mode -ceq 'actual-evaluations') {$compactView=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compact}
                $beforeCompact=$compactView | ConvertTo-Json -Depth 60 -Compress
                if(-not $custody.RecordCompact($compact,$compactView)) {throw 'Compact result missing'}
                $candidateContext=$context; $candidateView=$fullView
                if($mode -ceq 'wrong-context') {$candidateContext=[object]::new()}
                if($mode -ceq 'wrong-view') {$candidateView=$fullView.PSObject.Copy()}
                $pair=$custody.Take($candidateContext,$full,$compact,$candidateView,$compactView)
                if(($null -eq $pair) -ne ($mode -cin @('wrong-context','wrong-view'))) {throw 'Original pair admission mismatch'}
                if($null -ne $pair) {
                    if($pair.RequestId -ne $RequestId -or $pair.FullInvocationId -eq [guid]::Empty -or
                        $pair.CompactInvocationId -eq [guid]::Empty -or $pair.FullInvocationId -eq $pair.CompactInvocationId -or
                        -not $custody.Owns($pair,$context,$full,$compact,$fullView,$compactView)) {throw 'Original invocation ownership missing'}
                    $origins=@(for($i=0;$i -lt $full.Length;$i++) {$custody.GetPhysicalOrigin($pair,$full,$fullView,$i+1)})
                    if(($origins -join ',') -cne '2,74,75') {throw 'Evaluation origins used copy ordinals'}
                    'frontier-primary-source-origins='+($origins -join ',')
                    if($compact.Length -and $custody.GetPhysicalOrigin($pair,$compact,$compactView,1) -ne 74) {throw 'Compact origin differs'}
                    if($mode -ceq 'valid') {
                        $pairCopy=$clone.Invoke($pair,$null); $arrayCopy=[string[]]$full.Clone()
                        if($custody.Owns($pairCopy,$context,$full,$compact,$fullView,$compactView) -or
                            $custody.Owns($pair,[object]::new(),$full,$compact,$fullView,$compactView) -or
                            $custody.Owns($pair,$context,$arrayCopy,$compact,$fullView,$compactView) -or
                            $custody.Owns($pair,$context,$full,$compact,$fullView.PSObject.Copy(),$compactView) -or
                            $copied.Owns($pair,$context,$full,$compact,$fullView,$compactView) -or
                            $custody.GetPhysicalOrigin($pairCopy,$full,$fullView,1) -or
                            $custody.GetPhysicalOrigin($pair,$arrayCopy,$fullView,1) -or
                            $custody.GetPhysicalOrigin($pair,$full,$fullView,0) -or $custody.GetPhysicalOrigin($pair,$full,$fullView,4)) {throw 'Foreign scalar/copy acquired ownership'}
                    }
                    switch($mode) {
                        'repeat-take' {if($null -ne $custody.Take($context,$full,$compact,$fullView,$compactView)) {throw 'Pair taken twice'}}
                        'post-mutation' {$full[0]+='changed'}
                        'source-close' {$reader.Dispose()}
                        'custody-dispose' {$custody.Dispose()}
                    }
                    if(($fullView | ConvertTo-Json -Depth 60 -Compress) -cne $beforeFull -or
                        ($compactView | ConvertTo-Json -Depth 60 -Compress) -cne $beforeCompact) {throw 'Original evaluation views changed'}
                }
            }
        }
    }
    [void]$signals.attached.Set()
    if(-not $signals.release.WaitOne(30000)) {throw 'Evaluation review deadline'}
    $positive=$mode -cin @('valid','actual-evaluations')
    if($null -ne $custody -and $custody.Owns($pair,$context,$full,$compact,$fullView,$compactView) -ne $positive) {throw 'Final pair ownership mismatch'}
    if($worker.IsCurrent -ne $positive -or $owned.IsCurrent -ne $positive) {throw 'Native evaluation retirement mismatch'}
    if($owned.NativeWriterCustodyEstablished -or $owned.PublicationCompletionIssued -or $owned.LiveProofEligible -or
        $null -ne $worker.TryBeginPrimary($reader,$context,$full,$compact) -or $null -ne $worker.TryOpenWriter()) {throw 'Evaluation custody enabled legacy authority'}
    $expectedCalls=if($mode -ceq 'actual-evaluations') {2} else {0}
    if($script:evaluationCalls -ne $expectedCalls) {throw 'Ownership helper changed evaluation count'}
    $worker.Dispose()
    if($null -ne $custody -and ($custody.Owns($pair,$context,$full,$compact,$fullView,$compactView) -or
        $null -ne $custody.GetType().GetField('full',$flags).GetValue($custody))) {throw 'Evaluation references survived disposal'}
    'frontier-primary-worker='+$Case
    'frontier-primary-evaluations='+$mode
} finally {
    if($null -ne $custody) {$custody.Dispose()}; if($null -ne $reader) {$reader.Dispose()}; if($null -ne $worker) {$worker.Dispose()}
    foreach($event in $signals.Values) {$event.Dispose()}
}
