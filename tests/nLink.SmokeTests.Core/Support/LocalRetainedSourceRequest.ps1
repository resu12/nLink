param([string]$RepoRoot,[string]$Directory,[string]$Suite)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
$checks=[Collections.Generic.List[string]]::new(); $rows=[Collections.Generic.List[object]]::new()
$path=Join-Path $Directory 'input.log'
$raw='[2026-09-14T12:00:00Z] [INFO] [Fixture] event=runtime_unlock_retry_dispatch_authority_rejected; decision_reason=retry_dispatch_successor_attempt_mismatch; session_id=s; transfer_id=t; log_writer_id=writer; log_writer_sequence=3; publication_use_kind=v4_pre_pump_request; publication_evidence_version=1'
[IO.File]::WriteAllText($path,("preamble`r`n`n"+$raw+"`rtrailer"),[Text.UTF8Encoding]::new($true))
$lease=([NLink.Tools.LocalInputReadLease]::HashAndHold($path)).Lease
try {
    if($null -eq $lease -or -not $lease.IsVerified) { throw 'Parent primary hash owner unavailable' }
    $run=[guid]::NewGuid().ToString('D'); $scenario=$run+':source:1'
    if($Suite -ceq 'primitive') {
        foreach($case in @('null-lease','unverified-lease','disposed-lease','empty-attempt','owner','run','scenario','zero-lines','excess-lines')) {
            $candidate=$lease; $ownedCandidate=$null; $attempt=[guid]::NewGuid(); $owner='Wrapper'; $localRun=$run; $localScenario=$scenario; $lines=4L
            switch($case) {
                'null-lease' { $candidate=$null }
                'unverified-lease' { $ownedCandidate=[NLink.Tools.LocalInputReadLease]::OpenExpected($path,$lease.ExpectedSha256,4); $candidate=$ownedCandidate }
                'disposed-lease' { $ownedCandidate=([NLink.Tools.LocalInputReadLease]::HashAndHold($path)).Lease; $ownedCandidate.Dispose(); $candidate=$ownedCandidate }
                'empty-attempt' { $attempt=[guid]::Empty }
                'owner' { $owner='unknown' }
                'run' { $localRun='X'*1025 }
                'scenario' { $localScenario='(none)' }
                'zero-lines' { $lines=0 }
                'excess-lines' { $lines=262145 }
            }
            try {
                $unexpected=[NLink.Tools.LocalRetainedSourceRequest]::TryCreate($attempt,$owner,$localRun,$localScenario,$candidate,$lines)
                if($null -ne $unexpected) { $unexpected.Dispose(); throw "Invalid source request admitted: $case" }
            }
            finally { if($null -ne $ownedCandidate) { $ownedCandidate.Dispose() } }
            $checks.Add($case)
        }
        if([NLink.Tools.LocalRetainedSourceRequest].GetConstructors().Length -ne 0 -or
            [NLink.Tools.LocalRetainedSourceRequest+Worker].GetConstructors().Length -ne 0 -or
            [NLink.Tools.LocalRetainedSourceRequest+SourceReader].GetConstructors().Length -ne 0) { throw 'Public capability constructor exposed' }
        $checks.Add('private-capability-constructors')
        $independentPath=Join-Path $Directory 'independent.log'
        [IO.File]::WriteAllText($independentPath,'independent')
        $independentLease=([NLink.Tools.LocalInputReadLease]::HashAndHold($independentPath)).Lease
        $independentRequest=[NLink.Tools.LocalRetainedSourceRequest]::TryCreate([guid]::NewGuid(),'Wrapper',$run,$scenario,$independentLease,1)
        try {
            if($null -eq $independentRequest) { throw 'Independent source freeze missing' }
            $independentLease.Dispose(); $write=$null; $denied=$false
            try { $write=[IO.File]::OpenWrite($independentPath) } catch [IO.IOException] { $denied=$true }
            finally { if($null -ne $write) { $write.Dispose() } }
            if(-not $denied) { throw 'Disposing the original lease released the request freeze' }
        }
        finally { $independentLease.Dispose(); if($null -ne $independentRequest) { $independentRequest.Dispose() } }
        $write=[IO.File]::OpenWrite($independentPath); $write.Dispose()
        $checks.Add('independent-request-freeze-and-release')
    }
    $modes=if($Suite -ceq 'primary') { @('baseline','valid','closed','primary-report-fails') }
        else { @('valid','wrong-request','wrong-run','wrong-scenario','wrong-path','unbound','wrong-worker','worker-start','parent-start',
            'closed','gate-contended','partial','exact-count-no-eof','line-count-mismatch','cancel-before-open','cancel-after-line','parent-exit') }
    $baseline=@{}
    foreach($mode in $modes) {
        $output=Join-Path $Directory $mode; [void](New-Item -ItemType Directory -Path $output)
        $id=[guid]::NewGuid(); $count=if($mode -ceq 'line-count-mismatch') { 5L } else { 4L }
        $request=[NLink.Tools.LocalRetainedSourceRequest]::TryCreate($id,'Wrapper',$run,$scenario,$lease,$count)
        if($null -eq $request) { throw "Source request creation failed: $mode" }
        $signal='Local\nlink-source-test-'+[guid]::NewGuid().ToString('N')
        $ready=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$signal+'.ready')
        $go=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$signal+'.go')
        $step=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$signal+'.step')
        $resume=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$signal+'.resume')
        $settingsPath=Join-Path $output 'settings.json'; $resultPath=Join-Path $output 'worker-result.json'
        Write-FileTransferEvidenceAtomicJson $settingsPath @{suite=$Suite;mode=$mode;path=$path;signal=$signal;output=$output;result=$resultPath}
        $process=[Diagnostics.Process]::new(); $heldGate=$null; $parentControl=$null
        try {
            $process.StartInfo.FileName=(Get-Process -Id $PID).Path
            $worker=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalRetainedSourceWorker.ps1'
            $process.StartInfo.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$worker+'" "'+$RepoRoot+'" "'+$settingsPath+'"'
            $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
            $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
            $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST']=$request.Name
            $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST_ID']=$id.ToString('D')
            $process.StartInfo.EnvironmentVariables['NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID']=$run
            $process.StartInfo.EnvironmentVariables['NLINK_FILETRANSFER_SCENARIO_RUN_ID']=$scenario
            $process.StartInfo.EnvironmentVariables['NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT']='1'
            if($mode -ceq 'baseline') { $process.StartInfo.EnvironmentVariables.Remove('NLINK_RETAINED_SOURCE_REQUEST') }
            if(-not $process.Start()) { throw 'Test worker not started' }
            $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
            if($mode -ceq 'wrong-worker') {
                $current=[Diagnostics.Process]::GetCurrentProcess()
                try { if(-not $request.BindWorker($current)) { throw 'Wrong-worker control not bound' } } finally { $current.Dispose() }
            }
            elseif($mode -cne 'unbound') {
                if(-not $request.BindWorker($process) -or $request.BindWorker($process)) { throw 'Exact process binding not one-shot' }
            }
            if($mode -cin @('worker-start','parent-start')) {
                # Fault injection only: alter a header creation stamp, not actual process identity.
                $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($request.Name)
                $view=$map.CreateViewAccessor()
                try { $view.Write($(if($mode -ceq 'worker-start') { 40L } else { 8L }),0L) }
                finally { $view.Dispose(); $map.Dispose() }
            }
            if($mode -ceq 'parent-exit') {
                # Isolate the held-parent death check with a real test process.
                # Only this control substitutes the header's parent identity;
                # it is not a production issuer or a parent-death integration run.
                $parentControl=[Diagnostics.Process]::new()
                $parentControl.StartInfo.FileName=(Get-Process -Id $PID).Path
                $parentControl.StartInfo.Arguments='-NoProfile -Command "[Threading.Thread]::Sleep(30000)"'
                $parentControl.StartInfo.UseShellExecute=$false; $parentControl.StartInfo.CreateNoWindow=$true
                if(-not $parentControl.Start()) { throw 'Parent-death control not started' }
                $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($request.Name); $view=$map.CreateViewAccessor()
                try { $view.Write(4L,$parentControl.Id); $view.Write(8L,$parentControl.StartTime.ToUniversalTime().ToFileTimeUtc()) }
                finally { $view.Dispose(); $map.Dispose() }
            }
            if($mode -ceq 'closed') { $request.Dispose() }
            if($mode -ceq 'gate-contended') {
                $heldGate=[Threading.Mutex]::OpenExisting($request.Name+'.gate')
                if(-not $heldGate.WaitOne(0)) { throw 'Test mutex unavailable' }
            }
            if(-not $ready.WaitOne(15000)) { throw "Test worker not ready: $mode" }
            [void]$go.Set()
            if($mode -cin @('cancel-before-open','cancel-after-line','parent-exit')) {
                if(-not $step.WaitOne(15000)) { throw 'Test cancellation point not reached' }
                if($mode -ceq 'parent-exit') { $parentControl.Kill(); if(-not $parentControl.WaitForExit(5000)) { throw 'Parent-death control did not exit' } }
                else { $request.Dispose() }
                [void]$resume.Set()
            }
            if(-not $process.WaitForExit(30000)) { throw "Test worker deadline: $mode" }
            if($process.ExitCode -ne 0) { throw "Worker failed: $mode : $($stdout.Result) $($stderr.Result)" }
            $result=[IO.File]::ReadAllText($resultPath) | ConvertFrom-Json
            $attached=$mode -cin @('valid','primary-report-fails','partial','exact-count-no-eof','line-count-mismatch','cancel-before-open','cancel-after-line','parent-exit')
            $complete=$mode -cin @('valid','primary-report-fails')
            if($result.Attached -ne $attached -or $result.Complete -ne $complete -or $result.CurrentAfterClose) { throw "Wrong source ownership state: $mode" }
            if($Suite -ceq 'primitive') {
                if($attached -and ($result.SourceSha256 -cne $lease.ExpectedSha256 -or $result.SourceIdentity -cne $lease.FileIdentity -or
                    $result.RequestId -cne $id.ToString('D') -or $result.SourceLines -ne $count)) { throw 'Frozen source descriptor changed' }
                if($mode -cin @('valid','line-count-mismatch') -and -not $result.ParserEqual) { throw 'Primary parser changed events or offsets' }
                if($mode -cin @('cancel-after-line','parent-exit') -and $result.Lines -ne 4) { throw 'Cancellation aborted ordinary primary read' }
            }
            else {
                if($result.Evaluations -ne 3 -or -not $result.ReaderDisposed -or
                    ($mode -cne 'primary-report-fails' -and $result.WitnessLine -ne 3)) { throw 'Actual primary invocation/cleanup changed' }
                foreach($name in @('filetransfer-operator-verdict.txt','filetransfer-retained-compact-summary.json',
                    'filetransfer-coordinator-snapshot-full-summary.json','filetransfer-coordinator-snapshot-compact-summary.json',
                    'filetransfer-coordinator-snapshot-parity-summary.json')) {
                    $text=[IO.File]::ReadAllText((Join-Path $output $name))
                    if($name -ceq 'filetransfer-retained-compact-summary.json') {
                        $text=$text -replace '("data_path_correlation_(index|total)_ms"\s*:\s*)[0-9]+','${1}0'
                    }
                    if($mode -ceq 'baseline') { $baseline[$name]=$text }
                    elseif($text -cne $baseline[$name]) { throw "Primary report changed: $mode/$name" }
                }
            }
            $request.Dispose()
            if($process.ExitCode -ne 0) { throw 'Optional request disposed original primary process object' }
            $rows.Add([ordered]@{Mode=$mode;Attached=$result.Attached;InputReadComplete=$result.Complete})
        }
        finally {
            if($null -ne $heldGate) { $heldGate.ReleaseMutex(); $heldGate.Dispose() }
            if($null -ne $parentControl) { if(-not $parentControl.HasExited) { $parentControl.Kill(); [void]$parentControl.WaitForExit(5000) }; $parentControl.Dispose() }
            if(-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }
            $process.Dispose(); $request.Dispose(); $ready.Dispose(); $go.Dispose(); $step.Dispose(); $resume.Dispose()
        }
    }
}
finally { if($null -ne $lease) { $lease.Dispose() } }
$released=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite); $released.Dispose()
'local-retained-source-request='+(ConvertTo-Json -InputObject ([ordered]@{Suite=$Suite;AdmissionControls=$checks.ToArray();Rows=$rows.ToArray()
    AdditionalAuthorityEvaluations=0;WriterOwnershipVerified=$false;EvaluatorOwnershipVerified=$false;PublicationCompletionIssued=$false;LiveCaller=$false}) -Depth 5 -Compress)
