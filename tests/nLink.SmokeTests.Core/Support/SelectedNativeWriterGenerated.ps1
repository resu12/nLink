param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[switch]$ExpectMissingSecurityCapture,[switch]$ExpectWriterBudgetOmission,[AllowNull()]$NativeFiles=$null)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($env:NLINK_TEST_MEMBERSHIP_BORROWER_CASE -cnotin @('active','dispose','unavailable')) {throw 'selected_native_case'}
$env:NLINK_TEST_SELECTED_NATIVE_WRITER='1'; $env:NLINK_TEST_NATIVE_WITNESS_ADAPTER='1'
$env:NLINK_TEST_MEMBERSHIP_BORROWER='1'; $env:NLINK_TEST_COLLECTOR_PRIMARY='1'
foreach($flag in @('NLINK_TEST_NATIVE_PRIMARY_FILES','NLINK_TEST_COLLECTOR_MEMBERSHIP','NLINK_TEST_TERMINAL_PAYLOAD_JOIN',
    'NLINK_TEST_PRE_VERDICT_INTEGRITY','NLINK_PUBLICATION_TERMINAL_SOURCE_PROBE')) {[Environment]::SetEnvironmentVariable($flag,'0','Process')}
$handoff=$env:NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF -ceq '1'
if($handoff -ne ($null -ne $NativeFiles) -or ($handoff -and ($ExpectMissingSecurityCapture -or $ExpectWriterBudgetOmission))) {throw 'authenticated_payload_parent_scope'}
if(-not $handoff) {& (Join-Path $PSScriptRoot 'TerminalRequestFileBinding.ps1') $RepoRoot}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$output=Join-Path $Directory ('selected-native-'+$PSVersionTable.PSVersion.Major)
[void][IO.Directory]::CreateDirectory($output)
$log=$null; $outputs=$null; $lease=$null; $request=$null; $process=$null; $completion=$null
try {
    $log=if($handoff) {$NativeFiles} else {[NLink.Tests.HeldFileNativeParent+SeededCollectorLogForTest]::Open((Join-Path $Directory 'nlink.log'),$RunId,$ScenarioId)}
    if($null -eq $log) {throw 'selected_native_log_missing'}
    $null=& (Join-Path $PSScriptRoot 'LocalGuiWriterRequest.ps1') -RepoRoot $RepoRoot -Directory (Join-Path $output 'gui') `
        -AcceptedCompletion ([ref]$completion) -GeneratedSource (Join-Path $Directory 'nlink.log') -GeneratedRun $RunId -GeneratedScenario $ScenarioId `
        -TerminalCollectorFiles $log -CollectorFinalizer -CollectorOutputs -ForwardCollectorOutputs ([ref]$outputs)
    if($null -eq $outputs -or -not $outputs.OwnsCompletion($completion)) {throw 'selected_native_outputs_missing'}
    $lease=$outputs.HashSourceForTest($completion)
    if($null -eq $lease -or $null -ne $outputs.HashSourceForTest($completion)) {throw 'selected_native_source_hash'}
    $request=New-FileTransferRetainedSourceRequest -ReadLease $lease -AttemptId ([guid]::NewGuid().ToString('N')) `
        -Owner Wrapper -LineCount $completion.Outputs[3].LineCount -WriterContext $completion
    if($null -eq $request) {throw 'selected_native_request_missing'}
    $clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
    $copiedRequest=$clone.Invoke($request,@())
    if($handoff) {
        if($null -ne $copiedRequest.TryBindTerminalFilesForTest($log,$completion,$lease)) {throw 'authenticated_payload_copied_request'}
        $binding=$request.TryBindTerminalFilesForTest($log,$completion,$lease)
        if($null -eq $binding -or -not $binding.BindCollectorOutputsForTest($outputs) -or
            $null -ne $request.TryBindTerminalFilesForTest($log,$completion,$lease) -or $binding.BindCollectorOutputsForTest($outputs)) {throw 'authenticated_payload_binding'}
    } else {
        if($null -ne $copiedRequest.TryBindSeededCollectorForTest($log,$outputs,$completion,$lease)) {throw 'selected_native_copied_request'}
        $binding=$request.TryBindSeededCollectorForTest($log,$outputs,$completion,$lease)
        if($null -eq $binding -or -not $binding.IsCurrent -or $null -ne $request.TryBindSeededCollectorForTest($log,$outputs,$completion,$lease)) {throw 'selected_native_request_binding'}
    }
    function Send-Binding($candidate,$target) {
        if($handoff) {return $candidate.SendToPrimaryForTest($target,'')}
        return $candidate.SendToPrimaryForTest($target)
    }
    $process=[Diagnostics.Process]::new(); $process.StartInfo.FileName=(Get-Process -Id $PID).Path
    $worker=Join-Path $PSScriptRoot 'PublicationLivePrimaryWorker.ps1'
    $process.StartInfo.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$worker+'" -Mode AnalyzeRetained -LogPath "'+$completion.Outputs[3].Path+'" -ArtifactDir "'+$output+'" -TailMinutes 0 -LiveRouteProofMode RegularActivationCycle'
    $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true; $process.StartInfo.RedirectStandardInput=$true
    $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST']=$request.Name
    $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST_ID']=$request.RequestId.ToString('D')
    $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUIRED']='1'
    $process.StartInfo.EnvironmentVariables['NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT']='1'
    if($handoff) {$process.StartInfo.EnvironmentVariables['NLINK_TEST_NATIVE_PRIMARY_LOG']=(Join-Path $Directory 'nlink.log')}
    if(-not $process.Start()) {throw 'selected_native_primary_start'}
    $stderr=$process.StandardError.ReadToEndAsync()
    if(-not $request.BindWorker($process)) {throw 'selected_native_worker_bind'}
    $copy=$clone.Invoke($binding,@())
    if(Send-Binding $copy $process) {throw 'selected_native_copied_binding'}
    $parent=[Diagnostics.Process]::GetCurrentProcess()
    try {if(Send-Binding $binding $parent) {throw 'selected_native_unbound_process'}} finally {$parent.Dispose()}
    if(-not (Send-Binding $binding $process) -or (Send-Binding $binding $process)) {throw 'selected_native_send_once'}
    $ack=$process.StandardOutput.ReadLineAsync()
    if(-not $ack.Wait(15000)) {throw 'selected_native_ack_timeout'}
    $released=if($handoff) {$binding.ReleaseAfterPrimaryAcknowledgementForTest($process,$ack.Result)} else {$binding.ReleaseAfterAcknowledgementForTest($process,$ack.Result)}
    if(-not $released) {throw 'selected_native_ack_rejected'}
    if($outputs.IsCurrent -or $binding.IsCurrent -or $lease.IsVerified -or $log.IsCurrent -eq $handoff) {throw 'selected_native_parent_release'}
    $stdout=$process.StandardOutput.ReadToEndAsync()
    if(-not $process.WaitForExit(60000)) {throw 'selected_native_primary_timeout'}
    [IO.File]::WriteAllText((Join-Path $output 'stdout.txt'),$ack.Result+"`n"+$stdout.Result)
    [IO.File]::WriteAllText((Join-Path $output 'stderr.txt'),$stderr.Result)
    if($process.ExitCode -ne 0) {throw ('selected_native_primary_exit: '+$stderr.Result)}
    foreach($name in @('membership-borrower-error.txt','collector-primary-original-error.txt','selected-native-adoption-error.txt')) {
        $path=Join-Path $output $name; if(Test-Path -LiteralPath $path) {throw ([IO.File]::ReadAllText($path))}
    }
    $original=Get-Content (Join-Path $output 'live-primary-observation.json') -Raw|ConvertFrom-Json
    if($ExpectMissingSecurityCapture -or $ExpectWriterBudgetOmission) {
        $expectedOmission=if($ExpectWriterBudgetOmission) {'primary_witness_writer_unavailable'} else {'carrier_forwarding_unproven'}
        $reservedVisits=8L*$original.FullRecords+4L*$original.CompactRecords+2L*$completion.Outputs[1].LineCount
        if($ExpectWriterBudgetOmission -and $reservedVisits -le 4096) {throw 'selected_native_writer_budget_not_exceeded'}
        $borrower=Get-Content (Join-Path $output 'membership-borrower.json') -Raw|ConvertFrom-Json
        $cleanup=Get-Content (Join-Path $output 'membership-borrower-cleanup.json') -Raw|ConvertFrom-Json
        $nativeCleanup=Get-Content (Join-Path $output 'selected-native-cleanup.json') -Raw|ConvertFrom-Json
        if(-not $original.Prepared -or $original.Witness -or $original.Completion -or $original.Evaluations -ne 3 -or
            $original.WitnessOmission -cne $expectedOmission -or $borrower.Status -cne 'unavailable' -or
            $cleanup.Issued -or -not $cleanup.RetiredAfterContext -or -not $cleanup.RetiredAfterWorker -or
            -not $nativeCleanup.FourOutputsDisposed -or (Test-Path (Join-Path $output 'selected-native-witness.json'))) {throw 'selected_native_expected_omission_missing'}
        'selected-native-writer='+(@{WitnessOmission=$expectedOmission;TerminalConsumerEnabled=$false}|ConvertTo-Json -Compress)
        return
    }
    $native=Get-Content (Join-Path $output 'selected-native-witness.json') -Raw|ConvertFrom-Json
    $borrower=Get-Content (Join-Path $output 'membership-borrower.json') -Raw|ConvertFrom-Json
    $cleanup=Get-Content (Join-Path $output 'membership-borrower-cleanup.json') -Raw|ConvertFrom-Json
    $nativeCleanup=Get-Content (Join-Path $output 'selected-native-cleanup.json') -Raw|ConvertFrom-Json
    $summary=Get-Content (Join-Path $output 'filetransfer-retained-compact-summary.json') -Raw|ConvertFrom-Json
    if($summary.verdict -cne 'FAIL_PROTOCOL_OR_INTEGRITY' -or $summary.gate_status -cne 'fail' -or
        $summary.first_proof_chain_failure_class -cne 'file_tuna_data_path_mismatch') {throw 'selected_native_changed_original_verdict'}
    if(-not $original.Witness -or -not $original.Completion -or $original.Evaluations -ne 3 -or $native.SelectedIndex -lt 1 -or
        -not $native.SameMembershipMap -or $native.AdditionalWriterReads -ne 0 -or $native.NoTerminalOwner -eq $handoff -or
        $borrower.AdditionalWriterReads -ne 0 -or -not $borrower.OriginalCompletionCurrent -or -not $borrower.OwnerStillVerified -or
        -not $cleanup.RetiredAfterContext -or -not $cleanup.RetiredAfterWorker -or -not $nativeCleanup.FourOutputsDisposed) {throw 'selected_native_original_contract'}
    $unavailable=$env:NLINK_TEST_MEMBERSHIP_BORROWER_CASE -ceq 'unavailable'
    if($unavailable) {
        if($borrower.Status -cne 'unavailable' -or $cleanup.Issued) {throw 'selected_native_omission'}
    } elseif($borrower.Status -cne 'verified_offline_selected_membership_reuse_only' -or -not $cleanup.Issued -or
        $borrower.CumulativeRecordVisits -gt 4096 -or $borrower.WorkMilliseconds -gt 20000) {throw 'selected_native_borrower_contract'}
    if($handoff) {
        $payload=Get-Content (Join-Path $output 'selected-payload-owner.json') -Raw|ConvertFrom-Json
        $expected=if($env:NLINK_TEST_PAYLOAD_OWNER_CASE -ceq 'scope') {'foreign_scope_rejected'} else {'verified_offline_selected_payload_owner_only'}
        if($payload.Status -cne $expected -or $payload.TerminalIntegrityEvaluated -or
            $payload.AdditionalWriterReads -ne 0 -or $payload.AdditionalPayloadHashes -ne 0 -or
            -not $nativeCleanup.PayloadFilesDisposed -or -not $nativeCleanup.PayloadOwnerRetired) {throw 'authenticated_payload_post_report'}
        if($env:NLINK_TEST_SELECTED_TERMINAL_CASE) {
            $terminal=Get-Content (Join-Path $output 'selected-terminal-join.json') -Raw|ConvertFrom-Json
            $expected=$env:NLINK_TEST_SELECTED_TERMINAL_CASE -cin @('active','dispose')
            if($terminal.Joined -ne $expected -or -not $terminal.RepeatedJoinRejected -or
                -not $nativeCleanup.SelectedTerminalsRetired -or $terminal.CumulativeRecordVisits -ne $borrower.CumulativeRecordVisits -or
                $terminal.CumulativeCodeUnits -ne $borrower.CumulativeCodeUnits -or $terminal.AcceptanceChanged -or
                $terminal.AdditionalWriterReads -ne 0 -or $terminal.AdditionalPayloadHashes -ne 0) {throw 'selected_terminal_post_report'}
        }
    }
    foreach($file in $completion.Outputs) {
        $write=[IO.File]::Open($file.Path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]'ReadWrite,Delete'); $write.Dispose()
    }
    'selected-native-writer='+([ordered]@{Case=$env:NLINK_TEST_MEMBERSHIP_BORROWER_CASE;OriginalWitnessAndCompletion=$true
        OneWriterRead=$true;SameMembershipMap=$true;OriginalEvaluations=3;NoTerminalOwner=(-not $handoff);OutputsReleased=$true
        SeededFailure=$true;AcceptanceChanged=$false;Live=$false}|ConvertTo-Json -Compress)
} finally {
    if($null -ne $process) {
        try {if(-not $process.HasExited) {$process.Kill();[void]$process.WaitForExit(5000)}}
        catch [InvalidOperationException] { } # No process exists if Start failed.
        $process.Dispose()
    }
    if($null -ne $request) {$request.Dispose()};if($null -ne $lease) {$lease.Dispose()}
    if($null -ne $outputs) {$outputs.Dispose()};if($null -ne $log) {$log.Dispose()}
}
