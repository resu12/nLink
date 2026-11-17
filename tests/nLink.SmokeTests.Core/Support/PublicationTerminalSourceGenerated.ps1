param([string]$RepoRoot,[string]$Directory,[AllowNull()]$NativeFiles=$null)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
Set-Variable -Name NativeFiles -Option Private -Value $NativeFiles
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$run=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID; $scenario=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
$completion=$null
$private:collectorOutputs=$null
$collectorPrimary=$env:NLINK_TEST_COLLECTOR_PRIMARY -ceq '1'
$collectorArguments=@{}
if($env:NLINK_TEST_NATIVE_COLLECTOR -ceq '1') {
    if($null -eq $NativeFiles) {throw 'Native collector needs original held files'}
    $collectorArguments.TerminalCollectorFiles=$NativeFiles
    $collectorArguments.CollectorMutation=[string]$env:NLINK_TEST_NATIVE_COLLECTOR_MUTATION
    $collectorArguments.CollectorFinalizer=$env:NLINK_TEST_COLLECTOR_FINALIZER -ceq '1'
    if($env:NLINK_TEST_COLLECTOR_OUTPUTS -ceq '1') {
        $collectorArguments.CollectorOutputs=$true
        if($collectorPrimary) {$collectorArguments.ForwardCollectorOutputs=[ref]$collectorOutputs}
        if($env:NLINK_TEST_COLLECTOR_OUTPUT_FAILURE) {$collectorArguments.SingleCase=$env:NLINK_TEST_COLLECTOR_OUTPUT_FAILURE}
    }
}
$null=& (Join-Path $PSScriptRoot 'LocalGuiWriterRequest.ps1') -RepoRoot $RepoRoot -Directory (Join-Path $Directory 'gui') `
    -AcceptedCompletion ([ref]$completion) -GeneratedSource (Join-Path $Directory 'nlink.log') -GeneratedRun $run -GeneratedScenario $scenario @collectorArguments
if($env:NLINK_TEST_NATIVE_COLLECTOR -ceq '1' -and $env:NLINK_TEST_NATIVE_COLLECTOR_MUTATION) {
    if($null -ne $completion -or -not $NativeFiles.IsCurrent) {throw 'Collector rejection changed parent custody'}
    return
}
if($env:NLINK_TEST_COLLECTOR_OUTPUTS -ceq '1' -and $env:NLINK_TEST_COLLECTOR_OUTPUT_FAILURE) {
    if($null -ne $completion -or -not $NativeFiles.IsCurrent) {throw 'Output rejection changed custody'}
    return
}
if($null -eq $completion) {throw 'Original writer completion missing'}
$output=Join-Path $Directory 'primary'; [void][IO.Directory]::CreateDirectory($output)
if($env:NLINK_TEST_PRE_VERDICT_INTEGRITY -ceq '1' -and $env:NLINK_TEST_PRE_VERDICT_CASE -cin @('true','false')) {
    # Deliberate GUI-input predicate control; never a manufactured positive fixture summary.
    [IO.File]::WriteAllText((Join-Path $output 'filetransfer-tuna-gui-summary.json'),
        ('{"integrityOk":'+$env:NLINK_TEST_PRE_VERDICT_CASE+'}'))
}
$lease=$null; $request=$null; $process=$null; $private:fileBinding=$null; $bindingChecks=$null
$primaryFiles=$env:NLINK_TEST_NATIVE_PRIMARY_FILES -ceq '1'
$mutation=if($primaryFiles) {[string]$env:NLINK_TEST_NATIVE_PRIMARY_MUTATION} else {''}
if($mutation -cnotin @('','request','descriptor','truncated','collector-identity','collector-truncated',
    'collector-origin-producer','collector-origin-range','collector-origin-count') -or
    ($mutation -like 'collector-*' -and -not $collectorPrimary)) {throw 'Invalid native-primary test mutation'}
try {
    if($collectorPrimary) {
        if($null -eq $collectorOutputs -or -not $collectorOutputs.OwnsCompletion($completion)) {throw 'Collector output owner missing'}
        $lease=$collectorOutputs.HashSourceForTest($completion)
        if($null -eq $lease -or $null -ne $collectorOutputs.HashSourceForTest($completion)) {throw 'Collector native source hash not one-shot'}
    } else {$lease=([NLink.Tools.LocalInputReadLease]::HashAndHold($completion.Outputs[3].Path)).Lease}
    $request=New-FileTransferRetainedSourceRequest -ReadLease $lease -AttemptId ([guid]::NewGuid().ToString('N')) `
        -Owner Wrapper -LineCount $completion.Outputs[3].LineCount -WriterContext $completion
    if($null -eq $request) {throw 'Original source request missing'}
    if($null -ne $NativeFiles) {
        $bindingChecks=& (Join-Path $PSScriptRoot 'TerminalRequestFileBindingControls.ps1') $request $NativeFiles $completion $lease -NativeCollectorLease:$collectorPrimary
        $private:fileBinding=$bindingChecks.Binding
    }
    if($collectorPrimary) {
        $collectorBindingChecks=& (Join-Path $PSScriptRoot 'TerminalCollectorPrimaryControls.ps1') $request $fileBinding $collectorOutputs $completion $lease
    }
    $process=[Diagnostics.Process]::new(); $process.StartInfo.FileName=(Get-Process -Id $PID).Path
    $worker=Join-Path $PSScriptRoot 'PublicationLivePrimaryWorker.ps1'
    $process.StartInfo.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$worker+'" -Mode AnalyzeRetained -LogPath "'+$completion.Outputs[3].Path+'" -ArtifactDir "'+$output+'" -TailMinutes 0 -LiveRouteProofMode RegularActivationCycle'
    $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
    $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST']=$request.Name
    $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST_ID']=$request.RequestId.ToString('D')
    $process.StartInfo.EnvironmentVariables['NLINK_PUBLICATION_TERMINAL_SOURCE_PROBE']='1'
    $process.StartInfo.EnvironmentVariables['NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT']='1'
    if($primaryFiles) {
        if($null -eq $private:fileBinding) {throw 'Native-primary parent binding missing'}
        $process.StartInfo.RedirectStandardInput=$true
        $process.StartInfo.EnvironmentVariables['NLINK_TEST_NATIVE_PRIMARY_LOG']=(Join-Path $Directory 'nlink.log')
        $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUIRED']='1'
    }
    if(-not $process.Start()) {throw 'Primary did not start'}
    $stderr=$process.StandardError.ReadToEndAsync()
    if(-not $request.BindWorker($process)) {throw 'Original primary worker binding failed'}
    if($null -ne $private:fileBinding -and -not $private:fileBinding.Owns($request,$NativeFiles,$completion,$lease)) {
        throw 'Request/file association lost after original worker binding'
    }
    if($collectorPrimary -and -not $fileBinding.OwnsCollectorOutputsForTest($collectorOutputs,$request,$completion,$lease)) {throw 'Bound worker lost collector output ownership'}
    $firstLine=''
    if($primaryFiles) {
        $copy=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($private:fileBinding,@())
        if($copy.SendToPrimaryForTest($process,'')) {throw 'Copied parent binding sent files'}
        $parent=[Diagnostics.Process]::GetCurrentProcess()
        try {if($private:fileBinding.SendToPrimaryForTest($parent,'')) {throw 'Unbound target process accepted'}} finally {$parent.Dispose()}
        if(-not $private:fileBinding.SendToPrimaryForTest($process,$mutation)) {throw 'Native-primary send failed'}
        if($private:fileBinding.SendToPrimaryForTest($process,'')) {throw 'Repeated native-primary send admitted'}
        $ack=$process.StandardOutput.ReadLineAsync()
        if(-not $ack.Wait(15000)) {throw 'Native-primary acknowledgement timed out'}
        $firstLine=$ack.Result
        if(-not $mutation -and -not $private:fileBinding.ReleaseAfterPrimaryAcknowledgementForTest($process,$firstLine)) {
            throw 'Native-primary acknowledgement rejected'
        }
    }
    $stdout=$process.StandardOutput.ReadToEndAsync()
    if(-not $process.WaitForExit(60000)) {throw 'Primary timed out'}
    [IO.File]::WriteAllText((Join-Path $output 'stdout.txt'),$firstLine+"`n"+$stdout.Result)
    [IO.File]::WriteAllText((Join-Path $output 'stderr.txt'),$stderr.Result)
    if($mutation) {
        $expected=switch($mutation) {'request' {'native_parent_request'} 'descriptor' {'native_primary_descriptor_mismatch'} 'truncated' {'native_parent_packet_truncated'}
            'collector-identity' {'collector_primary_output_identity'} 'collector-truncated' {'collector_primary_packet_truncated'}
            'collector-origin-producer' {'collector_origins_producer'} 'collector-origin-range' {'collector_origins_range'}
            'collector-origin-count' {'collector_origins_limit'}}
        $errorPath=Join-Path $output 'native-primary-adoption-error.txt'
        $counts=Get-Content (Join-Path $output 'native-primary-rejection-counters.json') -Raw | ConvertFrom-Json
        if($process.ExitCode -eq 0 -or $firstLine -like 'nlink-native-primary-adopted:*' -or
            -not [IO.File]::Exists($errorPath) -or -not [IO.File]::ReadAllText($errorPath).Contains($expected) -or
            $counts.Evaluations -ne 0 -or $counts.Adopted -or
            [IO.File]::Exists((Join-Path $output 'live-primary-observation.json')) -or
            -not $NativeFiles.IsCurrent) {throw 'Native-primary rejection control failed'}
        if($collectorPrimary -and ($fileBinding.IsCurrent -or -not $collectorOutputs.OwnsCompletion($completion) -or -not $lease.IsVerified)) {
            throw 'Rejected output handoff lost file protection or retained stale request authority'
        }
        [IO.File]::WriteAllText((Join-Path $Directory 'native-primary-rejection-result.json'),([ordered]@{
            Mutation=$mutation;Reason=$expected;Acknowledged=$false;AuthorityEvaluations=0
            ChildExited=$true;ChildExitCode=$process.ExitCode;AcceptanceChanged=$false;Live=$false
        } | ConvertTo-Json -Compress))
        return
    }
    if($process.ExitCode -ne 0) {throw ($stdout.Result + $stderr.Result)}
    if($collectorPrimary -and ($collectorOutputs.IsCurrent -or $lease.IsActive)) {throw 'Parent collector custody survived acknowledgement'}
    if($null -ne $private:fileBinding -and $private:fileBinding.IsCurrent) {throw 'Request/file association survived worker exit'}
    $probeError=Join-Path $output 'terminal-source-probe-error.txt'
    if([IO.File]::Exists($probeError)) {throw [IO.File]::ReadAllText($probeError)}
    $observed=[IO.File]::ReadAllText((Join-Path $output 'live-primary-observation.json')) | ConvertFrom-Json
    if($observed.Evaluations -ne 3 -or -not $observed.Prepared) {throw 'Original primary evaluation/custody changed'}
    $probe=[IO.File]::ReadAllText((Join-Path $output 'terminal-source-primary-observation.json')) | ConvertFrom-Json
    if($probe.RequestId -cne $request.RequestId.ToString('D') -or $probe.WriterRequestId -cne $completion.RequestId.ToString('D') -or
        $probe.SourcePath -cne $completion.Outputs[3].Path -or $probe.SourceSha256 -cne $completion.Outputs[3].Sha256 -or
        $probe.SourceIdentity -cne $lease.FileIdentity -or
        $probe.SourceBytes -ne $completion.Outputs[3].ByteCount -or $probe.SourceLines -ne $completion.Outputs[3].LineCount -or
        $probe.FullInvocationId -ceq $probe.CompactInvocationId -or $probe.WorkMilliseconds -gt 20000) {throw 'Source/primary observation binding lost'}
    # Post-run assertions only. No observation JSON is an input to the analyzer.
    'terminal-primary-source=retained-without-integrity-inference'
} finally {
    if($null -ne $process) {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}; $process.Dispose()}
    if($null -ne $request) {$request.Dispose()}; if($null -ne $lease) {$lease.Dispose()}
    if($null -ne $private:fileBinding) {$private:fileBinding.Dispose()}
    if($null -ne $collectorOutputs) {$collectorOutputs.Dispose()}
    if($collectorPrimary) {
        if($collectorOutputs.IsCurrent) {throw 'Collector output cleanup failed'}
        foreach($descriptor in $completion.Outputs) {
            $write=[IO.File]::Open($descriptor.Path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]'ReadWrite,Delete'); $write.Dispose()
            # Deferred independent fixture corroboration, not another input to
            # the analyzer or a replacement for the parent source hash.
            $hash=[NLink.Tools.LocalInputReadLease]::HashAndHold($descriptor.Path)
            try {
                if($hash.Sha256 -cne $descriptor.Sha256 -or $hash.Lease.ByteCount -ne $descriptor.ByteCount -or
                    ($null -ne $descriptor.OriginalFileIdentity -and $hash.Lease.FileIdentity -cne $descriptor.OriginalFileIdentity)) {throw 'Independent native output verification failed'}
            } finally {$hash.Lease.Dispose()}
        }
        [IO.File]::WriteAllText((Join-Path $Directory 'collector-primary-parent-result.json'),([ordered]@{
            WriterRequestId=$completion.RequestId.ToString('D');SourceRequestId=$request.RequestId.ToString('D');
            BindingControls=$collectorBindingChecks.Controls;HashPasses=1;RepeatedHashRejected=$true;
            WorkerAcknowledged=$fileBinding.WorkerAdoptionAcknowledged;FourOutputsReleased=$true;
            ParentReleasedAfterAcknowledgement=$fileBinding.WorkerAdoptionAcknowledged;RejectedAttemptReleasedInCleanup=[bool]$mutation;
            PathReopenedForPrimary=$false;AcceptanceChanged=$false
        } | ConvertTo-Json -Depth 4 -Compress))
    }
}
if($null -ne $private:fileBinding) {
    if($private:fileBinding.IsCurrent -or $NativeFiles.IsCurrent -eq $primaryFiles) {throw 'Request/file cleanup boundary changed'}
    [IO.File]::WriteAllText((Join-Path $Directory 'native-request-binding-result.json'),([ordered]@{
        Schema='nlink.test-native-request-binding/v1';Status='verified_offline_parent_request_association_only'
        NativeRequestId=$NativeFiles.RequestId.ToString('D');SourceRequestId=$request.RequestId.ToString('D')
        WriterRequestId=$completion.RequestId.ToString('D');Controls=$bindingChecks.Controls
        CurrentAfterOriginalWorkerBinding=$true;InvalidAfterOriginalWorkerExit=$true;InvalidAfterDisposal=$true
        FileOwnerStillHeldAfterRequestDisposal=(-not $primaryFiles);PrimaryFileOwnerHandoffProven=$false
        WorkerAdoptionAcknowledged=$private:fileBinding.WorkerAdoptionAcknowledged
        WriterCopyProvenanceProven=$false;WriterMembershipVerified=$false;AcceptanceChanged=$false;Live=$false
    } | ConvertTo-Json -Depth 5 -Compress))
}
