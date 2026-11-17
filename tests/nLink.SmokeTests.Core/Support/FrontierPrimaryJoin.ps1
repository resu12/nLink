param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$SourcePath,[string]$Case,[string]$TerminalDirectory='',[string]$RendezvousCase='')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($Case.StartsWith('join-pipes-',[StringComparison]::Ordinal)) {$ProgressPreference='SilentlyContinue'}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeFrontierWitness.ps1')
$signals=@{}; $worker=$null; $reader=$null; $custody=$null; $set=$null; $script:evaluations=0
$mode=$Case.Substring('join-'.Length)
$preparing=$mode.StartsWith('prepare-',[StringComparison]::Ordinal); $preparation=$null; $prepareCurrent=$false
$outputting=$mode.StartsWith('output-',[StringComparison]::Ordinal)
$piping=$mode.StartsWith('pipes-',[StringComparison]::Ordinal)
$realEvaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param([AllowEmptyCollection()][string[]]$Lines)
    $script:evaluations++; & $realEvaluator -Lines $Lines
}
try {
    foreach($part in @('begin','attached','release')) {$signals[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.worker.'+$part)}
    if([string]$env:NLINK_RETAINED_FRONTIER_START_V1 -ceq '1') {
        $worker=Open-FileTransferRetainedSourceOwner -Name $Name -RequestId $RequestId -Path $SourcePath -Run 'frontier-gui-run' -Scenario 'frontier-gui-scenario' -FrontierReady
    } else {
        if(-not $signals.begin.WaitOne(30000)) {throw 'Join begin deadline'}
        $worker=[NLink.Tools.LocalRetainedSourceRequest]::TryAttachFrontier($Name,$RequestId,$SourcePath,'frontier-gui-run','frontier-gui-scenario')
    }
    if($null -eq $worker) {throw 'Native join worker unavailable'}
    $reader=$worker.TryOpenSource(); $events=@(Read-FileTransferLogEvents -LogFiles @($SourcePath) -OwnedSource $reader)
    [string[]]$full=@($events | ForEach-Object RawLine); [int[]]$ordinals=@($events | ForEach-Object LineNumber)
    $catalog=Get-FileTransferCoordinatorEvidenceCatalog
    $compactEvents=@($events | Where-Object {$catalog.SnapshotAuthorityV1 -contains $_.EventName -or $catalog.GenerationStampedEffects -contains $_.EventName})
    [string[]]$compact=@($compactEvents | ForEach-Object RawLine); [int[]]$compactOrdinals=@($compactEvents | ForEach-Object LineNumber)
    $context=[object]::new(); $custody=$worker.TryBeginFrontierPrimary($reader,$context,$full,$ordinals,$compact,$compactOrdinals)
    $fullView=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $full
    $compactView=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compact
    if($null -eq $custody -or -not $custody.RecordFull($full,$fullView) -or -not $custody.RecordCompact($compact,$compactView)) {throw 'Join evaluation custody missing'}
    $pair=$custody.Take($context,$full,$compact,$fullView,$compactView)
    $set=$custody.TryCapturePayloads($pair,$context,$full,$compact,$fullView,$compactView)
    if($null -eq $set) {throw 'Join native payload missing'}
    $proof=Get-FileTransferFallbackFrontierRecentProgressGraceRecheckCorrelation -Events $events
    if($proof.UnresolvedCount -ne 1 -or $proof.RecoveredCount -ne 0) {throw ('Original native failure missing: '+($proof | ConvertTo-Json -Depth 4 -Compress))}
    $original=$proof.UnresolvedCandidateRecords[0]
    $failure=[pscustomobject]@{Class='filetransfer_fallback_frontier_recent_progress_grace_recheck_missing';Event=$original.Event;Reason=$original.Reason;Variant=$original.Variant}
    $candidates=@($failure); $index=[pscustomobject]@{AllEvents=$events;LogFiles=@($SourcePath)}
    $native=$set.GetPayloads()[0]; $receipt=$native.Text | ConvertFrom-Json
    if($mode -cin @('scope-two-transfers','scope-duplicate-receipt')) {
        $payloads=@($set.GetPayloads())
        if($payloads.Count -ne 2 -or $payloads[0].WriterId -cne $payloads[1].WriterId) {throw 'Original same-writer transfers missing'}
        $matches=@($payloads | Where-Object {($_.Text | ConvertFrom-Json).Source.TransferId -ceq $failure.Event.Fields.frontier_scope_transfer_id})
        if($matches.Count -ne 1) {throw 'Original transfer partition missing'}
        $native=$matches[0]; $receipt=$native.Text | ConvertFrom-Json
        if($mode -ceq 'scope-duplicate-receipt') {
            # Negative-only captured receipt fault; never manufacture a witness.
            $foreign=@($payloads | Where-Object {-not [object]::ReferenceEquals($_,$native)})[0]
            $other=$foreign.Text | ConvertFrom-Json
            $other.Source.SessionId=$receipt.Source.SessionId; $other.Source.TransferId=$receipt.Source.TransferId
            [string]$duplicateJson=$other | ConvertTo-Json -Depth 20 -Compress
            $foreign.GetType().GetProperty('Text').GetSetMethod($true).Invoke($foreign,[object[]]@($duplicateJson)) | Out-Null
        }
    }
    $scopeFault=$mode.StartsWith('scope-fault-',[StringComparison]::Ordinal)
    $dataSession=$mode.StartsWith('data-session-',[StringComparison]::Ordinal)
    $watchdog=$mode.StartsWith('watchdog-',[StringComparison]::Ordinal) -or $scopeFault -or $dataSession
    if($mode -ceq 'scope-session-redacted') {
        if($failure.Event.Fields.session_id -cne '[redacted]' -or
            $failure.Event.Fields.frontier_scope_session_id -cne $receipt.Source.SessionId -or
            $failure.Event.Fields.frontier_scope_transfer_id -cne $receipt.Source.TransferId) {throw 'Original projected/redacted identity missing'}
    }
    if($watchdog) {
        $root=@($receipt.Lifecycle | Where-Object Kind -ceq 'StallCaptured')[0].Binding
        $stalls=@($events | Where-Object {$_.EventName -ceq 'filetransfer_v6_frontier_request_deferred' -and
            $_.Fields.wake_generation -ceq [string]$root.WakeGeneration -and $_.Fields.frontier_chunk_index -ceq [string]$root.FrontierChunkIndex})
        if($stalls.Count -ne 1 -or $stalls[0].Fields.reason -cne 'frontier_stall_repair_due' -or
            $receipt.Outcome -cne 'PublishedWithinObservationBound' -or
            @($receipt.Lifecycle | Where-Object Kind -ceq 'StallSuccessor').Count -ne [int]($mode -ceq 'watchdog-successor')) {
            throw 'Genuine watchdog root/late-publication fixture missing'
        }
        if($mode -ceq 'watchdog-rearm' -and @($receipt.Lifecycle | Where-Object Kind -ceq 'RecentArmed').Count -lt 3) {throw 'Genuine recent rearm missing'}
    }
    if($dataSession) {
        $recent=@($events | Where-Object {$_.EventName -ceq 'filetransfer_v6_frontier_recent_progress_grace_deferred' -and
            $_.Fields.frontier_chunk_index -ceq [string]$root.FrontierChunkIndex})
        if($failure.Event.Fields.transfer_id -cne '[redacted]' -or
            $recent.Count -ne @($receipt.Lifecycle | Where-Object Kind -ceq 'RecentArmed').Count -or $recent.Count -eq 0 -or
            @($recent | Where-Object {$_.Fields.data_session_identity -cne '[redacted]:1' -or
                $_.Fields.frontier_scope_data_session_identity -cne $receipt.Source.DataSessionIdentity -or
                $_.Fields.data_session_generation -cne [string]$receipt.Source.DataSessionGeneration}).Count -ne 0) {throw 'Original redacted data-session projection missing'}
        $unrelated=@($events | Where-Object {$_.EventName -ceq 'writer_only_contribution' -and
            $_.Fields.log_writer_id -cne $failure.Event.Fields.log_writer_id -and
            [regex]::Matches($_.RawLine,'(?i)(?:^|;)\s*data_session_identity\s*=').Count -eq 2})
        if($unrelated.Count -ne 1) {throw 'Original unrelated duplicate fixture missing'}
    }
    $completion=@($compactView.Boundaries | Where-Object {$_.boundaryId -ceq $receipt.Completion.TransitionId})[0]
    switch($mode) {
        'failure-copy' {$failure=$failure.PSObject.Copy()}
        'failure-reason' {$failure.Reason='unrelated'}
        'failure-line' {$failure.Event.LineNumber++; $failure.Event.Sequence++}
        'candidate-duplicate' {$candidates=@($failure,$failure)}
        'recovered' {$original.Recovered=$true}
        'view-verdict' {$compactView.ArtifactEvidenceVerdict='fail'}
        'completion-hash' {$completion.boundaryHash='0'*64}
        'completion-projection' {$completion.evidenceLineIndex++}
        'index-line-copy' {$index.AllEvents[0].RawLine=[string]::new($full[0].ToCharArray())}
        'receipt-profile' {$receipt.ClockDomain='offline_injected_v1'}
        'receipt-root' {$receipt.RootGeneration++}
        'receipt-repair' {$receipt.Repair.ContinuityReceiptIdentity='foreign'}
        'receipt-frame' {$receipt.Frame.BytesCommitted++}
        'receipt-lifecycle' {$receipt.Lifecycle[1].Sequence++}
        'receipt-completion' {$receipt.Completion.SnapshotSha256='0'*64}
        'receipt-observation' {$receipt.ObservationId=[guid]::NewGuid().ToString('D')}
    }
    if($mode.StartsWith('receipt-',[StringComparison]::Ordinal)) {
        # Deliberate private captured-text fault injection to exercise semantic
        # checks; this is not a caller-import or public payload mutation path.
        [string]$faultJson=$receipt | ConvertTo-Json -Depth 20 -Compress
        $native.GetType().GetProperty('Text').GetSetMethod($true).Invoke($native,[object[]]@($faultJson)) | Out-Null
    }
    $before=@($fullView,$compactView,$proof) | ConvertTo-Json -Depth 70 -Compress
    if($dataSession -and $mode -cne 'data-session-valid') {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDataSessionScopeFaults.ps1')
    } elseif($scopeFault) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierEventScopeFaults.ps1')
    } elseif($watchdog -and $mode -cnotin @('watchdog-valid','watchdog-rearm','watchdog-successor','data-session-valid')) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierWatchdogJoinFaults.ps1')
    }
    $arguments=@{Custody=$custody;Evaluations=$pair;Context=$context;Full=$full;Compact=$compact;FullView=$fullView;CompactView=$compactView;
        Payloads=$set;Index=$index;Failure=$failure;Candidates=$candidates;RecentProof=$proof}
    if($mode -ceq 'foreign-context') {
        $arguments.Context=[object]::new(); $foreign=Resolve-FileTransferNativeFrontierWitness @arguments
        if($null -ne $foreign -or -not $set.IsCurrent) {throw 'Foreign context acquired/retired original diagnostic'}
        $arguments.Context=$context
    }
    $reason=''; $result=$null
    if($mode -ceq 'snapshot-overflow') {
        $resolve=[Func[Action,NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+NativeWitnessSelection]]{
            param([Action]$visit)
            $visit.Invoke(); [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+NativeWitnessSelection]::new($native,[int[]]@(1),('x'*16385))
        }
        if($null -ne $custody.TryResolveWitnesses($pair,$context,$full,$compact,$fullView,$compactView,$set,$failure,$candidates,$proof,$index,$resolve)) {throw 'Oversized original snapshot accepted'}
    } else {$result=Resolve-FileTransferNativeFrontierWitness @arguments -OmissionReason ([ref]$reason)}
    $accepted=$preparing -or $outputting -or $piping -or $mode -cin @('valid','foreign-context','repeat','post-mutation','parent-cancel','watchdog-valid','watchdog-rearm','scope-session-redacted','scope-two-transfers','data-session-valid')
    if(($null -ne $result) -ne $accepted) {throw ('Native join mismatch: '+$mode+' '+$reason)}
    if($mode -ceq 'scope-duplicate-receipt' -and $reason -cne 'native_frontier_receipt_ambiguity') {throw ('Wrong duplicate receipt rejection: '+$reason)}
    if($watchdog -and -not $accepted) {
        if($mode -ceq 'watchdog-successor') {if($reason -cne 'native_frontier_watchdog_root') {throw ('Wrong successor rejection: '+$reason)}}
        elseif($script:watchdogFaultApplied -ne (1+[int]($mode -ceq 'watchdog-reordered-root')) -or $reason -cne $script:watchdogExpected) {throw ('Wrong watchdog rejection: '+$mode+' '+$reason+' hits='+$script:watchdogFaultApplied)}
    }
    if($mode -ceq 'witness-overflow' -and $reason -cne 'native_frontier_witness_capacity') {throw ('Wrong witness overflow edge: '+$reason)}
    if($null -ne $result) {
        if($result.Status -cne 'matched_native_frontier_diagnostic' -or $result.Batch.GetWitnesses().Count -gt 64 -or
            $result.OriginalSnapshot -cne $result.Batch.DiagnosticSnapshot -or $result.Batch.GetType().GetProperty('DiagnosticSnapshot').GetSetMethod() -or
            $result.SamePairMatched -or $result.LiveProofEligible -or $result.PublicationCompletionIssued -or $result.NativeWriterCustodyEstablished) {throw 'Native diagnostic snapshot/custody mismatch'}
        'native-join='+(@{Witnesses=$result.Batch.GetWitnesses().Count;Evaluations=$script:evaluations;Outcome=$result.DiagnosticOutcome;FailureIndex=$result.FailureFullIndex} | ConvertTo-Json -Compress)
        if($mode -ceq 'repeat' -and $null -ne (Resolve-FileTransferNativeFrontierWitness @arguments)) {throw 'Native diagnostic replayed'}
        if($mode -ceq 'post-mutation') {$full[0]+='changed'}
        if($preparing) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticPreparation.ps1')}
        if($outputting) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticOutputWorker.ps1')
            return
        }
        if($piping) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryOutputWorker.ps1')
            # Exit from the entry script, not its dot-sourced helper, so the
            # native process receives the intended code and finally still runs.
            if($mode -ceq 'pipes-nonzero-exit') {exit 7}
            return
        }
    }
    if($script:evaluations -ne 2 -or (@($fullView,$compactView,$proof) | ConvertTo-Json -Depth 70 -Compress) -cne $before) {throw 'Native join changed primary evaluation/proof'}
    [void]$signals.attached.Set(); if(-not $signals.release.WaitOne(30000)) {throw 'Join release deadline'}
    if($mode -cin @('parent-cancel','prepare-parent-cancel') -and -not [Threading.SpinWait]::SpinUntil([Func[bool]]{$null -eq $result.Batch.DiagnosticSnapshot},5000)) {throw 'Idle cancellation retained native diagnostic snapshot'}
    $current=if($preparing) {$prepareCurrent} else {$mode -cin @('valid','foreign-context','watchdog-valid','watchdog-rearm','scope-session-redacted','scope-two-transfers','data-session-valid')}
    if($null -ne $preparation -and $preparation.IsCurrent -ne $current) {throw 'Preparation lifetime mismatch'}
    if($null -ne $preparation -and -not $current -and $null -ne $preparation.DiagnosticSnapshot) {throw 'Retired preparation retained snapshot'}
    if($null -ne $result -and $result.Batch.IsCurrent -ne $current) {throw 'Native diagnostic lifetime mismatch'}
    if($set.IsCurrent -ne $current -or $worker.IsCurrent -ne $current) {throw 'Join native set lifetime mismatch'}
    if($null -ne $result -and -not $current -and $null -ne $result.Batch.DiagnosticSnapshot) {throw 'Retired native diagnostic retained snapshot'}
    'frontier-primary-worker='+$Case
    'frontier-primary-join='+$Case.Substring('join-'.Length)
    if($preparing) {'frontier-diagnostic-preparation='+$mode.Substring('prepare-'.Length)}
} finally {
    if($null -ne $set) {$set.Dispose()}; if($null -ne $custody) {$custody.Dispose()}
    if($null -ne $reader) {$reader.Dispose()}; if($null -ne $worker) {$worker.Dispose()}
    foreach($event in $signals.Values) {$event.Dispose()}
}
