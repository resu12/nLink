param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$Lane='witness',[string[]]$CaseSelection=@(),[switch]$UnicodeMetadata)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$suffix=''; if($UnicodeMetadata) { $suffix=[string][char]0x017e+[char]::ConvertFromUtf32(0x1f642)+'-' }
$output=Join-Path $Directory ('primary-'+$PSVersionTable.PSVersion.Major+'-'+$suffix+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($output)
$completion=$null
$null=& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRequest.ps1') -RepoRoot $RepoRoot -Directory (Join-Path $output 'gui') `
    -AcceptedCompletion ([ref]$completion) -GeneratedSource (Join-Path $Directory 'nlink.log') -GeneratedRun $RunId -GeneratedScenario $ScenarioId
if($null -eq $completion) { throw 'Generated GUI completion missing' }
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$RunId; $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$ScenarioId
$rows=[Collections.Generic.List[object]]::new()
$modes=@('valid-0','valid-1','valid-2','valid-3','valid-4','valid-5','valid-6','valid-7',
    'wrong-failure','wrong-witness','wrong-event-name','native-mutated','compact-mutated','full-origin','compact-origin','boundary-offset','duplicate-boundary','missing-effect',
    'effect-offset','effect-content','view-parity','view-budget','operation-budget','record-budget','time-budget',
    'native-cancel','source-cancel','writer-changed','writer-missing','no-failure',
    'event-raw-reference','event-path','event-line','event-sequence','event-sequence-compatible')
if($Lane -ceq 'completion') {
    $modes=@('valid-0','valid-1','valid-2','valid-3','valid-4','valid-5','valid-6','valid-7',
        'completion-json-poison','completion-row-parity','completion-row-budget','completion-time-budget',
        'completion-native-cancel','completion-source-cancel','completion-writer-cancel','completion-context-copy',
        'completion-view-copy','completion-native-owner-copy','completion-native-token-copy','completion-pair-forged',
        'completion-custody-forged','completion-no-prepared','completion-nonascii','completion-formatter-isolation',
        'completion-native-freeze-after','completion-source-freeze-after','completion-worker-freeze-after')
}
if($Lane -ceq 'channel') {
    $modes=@('valid-0','valid-7','channel-no-completion','channel-cancel-before','channel-native-cancel',
        'channel-source-cancel','channel-writer-cancel','channel-worker-cancel','channel-parent-cancel','channel-context-copy','channel-early-take','channel-nonzero',
        'channel-revoke-after','channel-control-partial','channel-wrong-request','channel-wrong-worker',
        'channel-wrong-creation','channel-wrong-request-hash','channel-payload-corrupt','channel-length-overflow',
        'channel-wrong-namespace','channel-publish-busy','channel-abandoned-gate')
}
if($CaseSelection.Count -gt 0) {
    $allowed=@($modes)
    if($Lane -ceq 'completion') { $allowed+=@('completion-payload-budget') }
    if($Lane -cnotin @('completion','channel') -or @($CaseSelection | Where-Object { $_ -cnotin $allowed }).Count -gt 0 -or
        @($CaseSelection | Select-Object -Unique).Count -ne $CaseSelection.Count) { throw 'Invalid focused completion case selection' }
    $modes=@($allowed | Where-Object { $_ -cin $CaseSelection })
}
foreach($mode in $modes) {
    $case=Join-Path $output $mode; [void][IO.Directory]::CreateDirectory($case)
    $lease=$null; $request=$null; $process=$null; $writerBytes=$null; $otherRequest=$null; $busyGate=$null; $ready=$null; $release=$null
    try {
        if($mode -cin @('writer-changed','writer-missing')) {
            $writerBytes=[IO.File]::ReadAllBytes($completion.Outputs[1].Path)
            if($mode -ceq 'writer-changed') { $changed=[byte[]]$writerBytes.Clone(); $changed[0]=[byte][char]'X'; [IO.File]::WriteAllBytes($completion.Outputs[1].Path,$changed) }
            else { [IO.File]::Move($completion.Outputs[1].Path,$completion.Outputs[1].Path+'.held') }
        }
        $lease=([NLink.Tools.LocalInputReadLease]::HashAndHold($completion.Outputs[3].Path)).Lease
        if($Lane -ceq 'channel') {
            $request=[NLink.Tools.LocalRetainedSourceRequest]::TryCreateWithPublication([guid]::NewGuid(),'Wrapper',$RunId,$ScenarioId,
                $lease,$completion.Outputs[3].LineCount,$completion)
            if($mode -ceq 'channel-wrong-namespace') {
                $otherRequest=[NLink.Tools.LocalRetainedSourceRequest]::TryCreateWithPublication($request.RequestId,'Wrapper',$RunId,$ScenarioId,
                    $lease,$completion.Outputs[3].LineCount,$completion)
                if($null -eq $otherRequest) { throw 'Second genuine namespace missing' }
            }
        } else {
            $request=New-FileTransferRetainedSourceRequest -ReadLease $lease -AttemptId ([guid]::NewGuid().ToString('N')) -Owner Wrapper `
                -LineCount $completion.Outputs[3].LineCount -WriterContext $completion
        }
        if($null -eq $request) { throw 'Generated retained request missing' }
        $settings=Join-Path $case 'settings.json'; $resultPath=Join-Path $case 'result.json'
        if($mode -ceq 'channel-publish-busy') {
            $busyGate=[Threading.Mutex]::OpenExisting($request.Name+'.publication.gate')
            if(-not $busyGate.WaitOne(0)) { throw 'Test gate already held' }
        }
        if($mode -ceq 'channel-early-take') {
            $ready=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$request.Name+'.test-ready')
            $release=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$request.Name+'.test-release')
        }
        [IO.File]::WriteAllText($settings,(@{mode=$mode;lane=$Lane;path=$completion.Outputs[3].Path;result=$resultPath} | ConvertTo-Json))
        $process=[Diagnostics.Process]::new(); $process.StartInfo.FileName=(Get-Process -Id $PID).Path
        $process.StartInfo.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PrimaryPublicationWitnessWorker.ps1')+'" "'+$RepoRoot+'" "'+$settings+'"'
        $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
        $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
        $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST']=$request.Name
        $process.StartInfo.EnvironmentVariables['NLINK_RETAINED_SOURCE_REQUEST_ID']=$request.RequestId.ToString('D')
        if(-not $process.Start()) { throw 'Worker launch failed' }
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if(-not $request.BindWorker($process)) { throw 'Worker binding failed' }
        if($null -ne $otherRequest -and -not $otherRequest.BindWorker($process)) { throw 'Second namespace worker binding failed' }
        if($mode -ceq 'channel-early-take') {
            if(-not $ready.WaitOne(20000) -or $process.HasExited) { throw 'Early take fixture not at live issuer seam' }
            if($null -ne $request.TakePublicationAfterExit()) { throw 'Running worker completion admitted' }
            [void]$release.Set()
        }
        if(-not $process.WaitForExit(20000)) { throw 'Worker fixture timeout' }
        $expectedExit=0; if($mode -ceq 'channel-nonzero') { $expectedExit=23 }
        if($process.ExitCode -ne $expectedExit) { throw "$mode : $($stdout.Result) $($stderr.Result)" }
        $row=[IO.File]::ReadAllText($resultPath) | ConvertFrom-Json
        if($Lane -ceq 'channel') {
            if($null -ne $busyGate) { $busyGate.ReleaseMutex(); $busyGate.Dispose(); $busyGate=$null }
            $facts=& (Join-Path $PSScriptRoot 'PrimaryPublicationChannelParentChecks.ps1') -Mode $mode -Request $request -OtherRequest $otherRequest -WorkerResult $row
            $row | Add-Member NoteProperty Channel $facts
            [IO.File]::WriteAllText((Join-Path $case 'parent.json'),($facts | ConvertTo-Json -Compress))
        }
        $rows.Add($row)
    } finally {
        if($null -ne $process) { if(-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }; $process.Dispose() }
        if($null -ne $request) { $request.Dispose() }; if($null -ne $lease) { $lease.Dispose() }
        if($null -ne $otherRequest) { $otherRequest.Dispose() }
        if($null -ne $busyGate) { $busyGate.ReleaseMutex(); $busyGate.Dispose() }
        if($null -ne $ready) { $ready.Dispose() }; if($null -ne $release) { $release.Dispose() }
        if($null -ne $writerBytes) {
            if($mode -ceq 'writer-missing') { [IO.File]::Move($completion.Outputs[1].Path+'.held',$completion.Outputs[1].Path) }
            else { [IO.File]::WriteAllBytes($completion.Outputs[1].Path,$writerBytes) }
        }
    }
}
# Stable cross-host facts; detailed timing/omission diagnostics stay in case files.
if($Lane -ceq 'channel') {
    $positive=@($rows | Where-Object { $_.Channel.Received }).Count
    'primary-publication-channel='+([ordered]@{PositiveCases=$positive;NegativeCases=$modes.Count-$positive;AuthorityEvaluationsPerWorker=2
        ActualGuiAndRetainedOwnership=$true;OriginalViewsUnchanged=$true;FailureLabels='synthetic_selection_controls'
        TrustedDigestSetter=$false;LiveExport=$false} | ConvertTo-Json -Compress)
} elseif($Lane -ceq 'completion') {
    $positive=@($rows | Where-Object { $_.Completion.Issued }).Count
    'primary-publication-completion='+([ordered]@{PositiveCases=$positive;NegativeCases=$modes.Count-$positive;AuthorityEvaluationsPerWorker=2
        ActualGuiAndRetainedOwnership=$true;OriginalViewsUnchanged=$true;FailureLabels='synthetic_selection_controls'
        DiagnosticJsonUsedForIssuance=$false;LiveExport=$false} | ConvertTo-Json -Compress)
} else {
    $positive=@($rows | Where-Object { $_.Prepared }).Count
'primary-publication-witness='+([ordered]@{PositiveCases=$positive;NegativeCases=$modes.Count-$positive;AuthorityEvaluationsPerWorker=2
    ActualGuiAndRetainedOwnership=$true;OriginalViewsUnchanged=$true;FailureLabels='synthetic_selection_controls'
    PublicationCompletionIssued=$false;LiveExport=$false} | ConvertTo-Json -Compress)
}
