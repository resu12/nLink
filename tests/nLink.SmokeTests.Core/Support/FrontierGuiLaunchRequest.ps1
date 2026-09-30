param([string]$RepoRoot,[string]$Directory,[string]$OnlyCase='',[string]$Mode='launch',[string]$CoreAssembly='',[string]$FixtureAssembly='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($Mode -ceq 'join') { . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierAppRequest.ps1') }
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
if([NLink.Tools.LocalGuiWriterRequest].Assembly -ne [NLink.Tools.LocalFrontierAppRequest].Assembly) { throw 'Split native request codecs loaded' }
foreach($type in @([NLink.Tools.LocalGuiWriterRequest+FrontierLaunchOwner],[NLink.Tools.LocalGuiWriterRequest+FrontierAppLaunch],[NLink.Tools.LocalGuiWriterRequest+FrontierAppRequest])) {
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object { $null -ne $_.GetSetMethod() }).Count) { throw 'Public launch receipt construction/mutation exposed' }
}
if([NLink.Tools.LocalGuiWriterRequest+FrontierLaunchOwner]::MaximumAppLaunches -ne 2) { throw 'Unexpected launch bound' }
$cases=@('valid','wrong-run','wrong-scenario','wrong-root','wrong-request','gui-incarnation','parent-incarnation','reserved',
    'cancel-before','cancel-after','registration-after','stage-after','incomplete-seal','duplicate-app','third-app','bind-after-seal',
    'self','already-exited','null-app','nonzero','copied','owner-disposed','finalizer-coexistence','finalizer-first','seal-after-exit')
if($Mode -ceq 'join') {
    $cases=@('valid','copied','incomplete-set','third-request','request-disposed','owner-disposed','duplicate-bind','duplicate-process',
        'plain-after-request','request-after-plain','seal-before-bind','after-seal-create','nonzero','missing-attachment','abandoned-attachment',
        'swapped-endpoint','wrong-run','cancel-after','registration-after','stage-after','app-metadata','app-cancel')
}
if($OnlyCase) { $cases=@($OnlyCase) }
$results=[Collections.Generic.List[object]]::new()
foreach($case in $cases) {
    $request=[NLink.Tools.LocalGuiWriterRequest]::TryCreate([guid]::NewGuid(),'frontier-gui-run','frontier-gui-scenario',$Directory)
    if($null -eq $request) { throw 'Original GUI request unavailable' }
    $name=$request.Name
    $events=@{}
    foreach($part in @('begin','attached','release')) { $events[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$name+'.test.'+$part) }
    $process=$null; $view=$null; $map=$null; $secondary=$null
    $parentRegistration=$null; $parentMap=$null; $parentView=$null
    try {
        $start=[Diagnostics.ProcessStartInfo]::new()
        $start.FileName=(Get-Process -Id $PID).Path
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $worker=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchWorker.ps1'
        if($Mode -ceq 'join') { $worker=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiAppJoinWorker.ps1' }
        if($Mode -ceq 'files') { $worker=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiFileHandoffWorker.ps1' }
        if($Mode -ceq 'output') { $worker=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiOutputWorker.ps1' }
        if($Mode -ceq 'seal') { $worker=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiSealWorker.ps1' }
        if($Mode -ceq 'ledger-origin') { $worker=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiLedgerOrigin.ps1' }
        if($Mode -ceq 'ledger-cursor') { $worker=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiLedgerCursor.ps1' }
        $death=Join-Path $Directory 'parent-death-result.json'
        $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$worker,'-RepoRoot',$RepoRoot,'-Name',$name,'-RequestId',$request.RequestId.ToString('D'),'-Directory',$Directory,'-Case',$case,'-DeathResult',$death)
        if($Mode -ceq 'seal' -and $case -cin @('binding-endpoint','binding-relay')) {
            if($case -ceq 'binding-relay') { $secondary=[NLink.Tools.LocalGuiWriterRequest]::TryCreateRelay($request.RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory) }
            else { $secondary=[NLink.Tools.LocalGuiWriterRequest]::TryCreate($request.RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory) }
            if($null -eq $secondary) { throw 'Secondary same-scope endpoint unavailable' }
            $arguments+=@('-SecondaryName',$secondary.Name)
        }
        if($Mode -cin @('join','output')) { $arguments+=@('-CoreAssembly',$CoreAssembly) }
        if($Mode -cin @('files','seal')) { $arguments+=@('-CoreAssembly',$CoreAssembly,'-FixtureAssembly',$FixtureAssembly) }
        $start.Arguments=($arguments | ForEach-Object { '"'+$_.Replace('"','\"')+'"' }) -join ' '
        $process=[Diagnostics.Process]::Start($start)
        [void]$process.Handle
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if($case -ceq 'cursor-parent-unbound' -and $null -ne $request.TryRegisterFrontierParent()) { throw 'Unbound parent registered' }
        if(-not $request.BindWorker($process)) { throw 'Original GUI worker binding failed' }
        if($case.StartsWith('cursor-parent-',[StringComparison]::Ordinal)) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentRegistration.ps1') -Phase register
        }
        if($null -ne $secondary -and -not $secondary.BindWorker($process)) { throw 'Secondary GUI worker binding failed' }
        if($null -ne [NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncher($name,$request.RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)) { throw 'Parent attached as GUI launcher' }
        $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name); $view=$map.CreateViewAccessor()
        switch($case) {
            'gui-incarnation' { $view.Write(40L,$view.ReadInt64(40L)+1L) }
            'parent-incarnation' { $view.Write(8L,$view.ReadInt64(8L)+1L) }
            'reserved' { $view.Write(56L,1L) }
            'cancel-before' { $request.Dispose() }
        }
        [void]$events.begin.Set()
        if(-not $events.attached.WaitOne(20000)) {
            if($process.HasExited) { throw "GUI exited before attachment: $case $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())" }
            throw "GUI did not report attachment: $case"
        }
        if($case -ceq 'parent-death') {
            # No managed cancellation: the retained original native parent handle
            # must independently revoke the GUI owner and app launch evidence.
            'frontier-gui-parent-death='+(@{GuiProcessId=$process.Id;GuiCreation=$process.StartTime.ToUniversalTime().ToFileTimeUtc();Result=$death} | ConvertTo-Json -Compress)
            [Environment]::Exit(0)
        }
        switch($case) {
            'cancel-after' { $request.Dispose() }
            'registration-after' { $view.Write(64L,[byte]0) }
            'stage-after' { $view.Write(48L,2) }
        }
        [void]$events.release.Set()
        if($case.StartsWith('cursor-parent-',[StringComparison]::Ordinal)) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentRegistration.ps1') -Phase review
        }
        if(-not $process.WaitForExit(20000)) { throw "GUI fixture timed out: $case" }
        $process.WaitForExit()
        $out=$stdout.GetAwaiter().GetResult(); $err=$stderr.GetAwaiter().GetResult()
        if($case.StartsWith('markers-buffered-',[StringComparison]::Ordinal)) {
            [IO.File]::WriteAllText((Join-Path $Directory 'recovery-buffer-probe.log'),$out+[Environment]::NewLine+$err)
        }
        $expectedExitCode=if($case -cin @('cursor-parent-files-facts-ack-gui-failure','cursor-parent-files-facts-ack-commit-gui-failure','cursor-parent-rendezvous-gui-failure')) {7} else {0}
        if($process.ExitCode -ne $expectedExitCode -or $out -notmatch 'frontier-gui-worker=') { throw "Fixture failed $case`: $out $err" }
        if($null -ne $parentRegistration -and ($parentRegistration.IsCurrent -or $parentRegistration.GuiAttached)) { throw 'Parent registration survived GUI exit' }
        if($case -cin @('cursor-parent-gui-exit','cursor-parent-prepare-gui-exit')) {
            if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) {throw 'Native GUI exit retained payloads'}
            [IO.File]::AppendAllText([IO.Path]::Combine($Directory,'filetransfer-frontier-finalizer-evidence.json'),'native-exit-released')
        }
        $isCommitCase=$case.StartsWith('cursor-parent-files-facts-ack-commit-',[StringComparison]::Ordinal)
        if($isCommitCase -and $commitCase -ceq 'copied-request') {
            $foreign=$parentClone.Invoke($request,$null)
            if($null -ne $foreign.TakeAfterExit()) {throw 'Copied request took completion'}
            $foreign.Dispose()
        }
        $completion=if($isCommitCase -and $commitCase -cin @('output-gate','state-gate')) {$null} else {$request.TakeAfterExit()}
        if($case.StartsWith('cursor-parent-rendezvous-',[StringComparison]::Ordinal)) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentRendezvous.ps1') -Phase result
        } elseif($isCommitCase) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentCommitResult.ps1')
        } else {
        $expectedLegacy=$Mode -ceq 'seal' -and $case -cin @('binding-complete-valid','binding-missing-seal','binding-binding-disposed','cursor-parent-legacy-complete','cursor-parent-prepare-legacy-complete','cursor-parent-files-legacy-complete','cursor-parent-files-facts-legacy-complete','cursor-parent-files-facts-ack-legacy-complete')
        if(($null -ne $completion) -ne $expectedLegacy) { throw 'Unexpected legacy GUI completion result' }
        if($expectedLegacy -and ($completion.Outputs.Count -ne 4 -or $completion.LedgerId -cne 'binding-legacy' -or $completion.SourcePath -cne (Join-Path $Directory 'legacy-only.log'))) { throw 'Legacy completion schema changed' }
        }
        $results.Add(@{Case=$case;GuiProcessId=$process.Id;LiveProofEligible=$false;NativeWriterCustodyEstablished=$false;PublicationCompletionIssued=$false})
    } finally {
        if($case.StartsWith('cursor-parent-rendezvous-',[StringComparison]::Ordinal)) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentRendezvous.ps1') -Phase cleanup
        }
        if($case.StartsWith('cursor-parent-files-facts-ack-commit-',[StringComparison]::Ordinal)) {
            if(Get-Variable custody -ErrorAction SilentlyContinue) {if($null -ne $custody) {$custody.Dispose()}}
            if(Get-Variable commitView -ErrorAction SilentlyContinue) {if($null -ne $commitView) {$commitView.Dispose()}}
            if(Get-Variable commitMap -ErrorAction SilentlyContinue) {if($null -ne $commitMap) {$commitMap.Dispose()}}
        }
        if($null -ne $parentView) { $parentView.Dispose() }
        if($null -ne $parentMap) { $parentMap.Dispose() }
        if($null -ne $parentRegistration) { $parentRegistration.Dispose() }
        if($null -ne $view) { $view.Dispose() }
        if($null -ne $map) { $map.Dispose() }
        if($null -ne $process) { if(-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }; $process.Dispose() }
        $request.Dispose()
        if($null -ne $secondary) { $secondary.Dispose() }
        foreach($event in $events.Values) { $event.Dispose() }
    }
    $leftover=$null
    try { $leftover=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name) } catch [IO.FileNotFoundException] { }
    if($null -ne $leftover) { $leftover.Dispose(); throw 'Owned GUI endpoint was not cleaned up' }
    if($case.StartsWith('cursor-parent-',[StringComparison]::Ordinal)) {
        if($case.StartsWith('cursor-parent-rendezvous-',[StringComparison]::Ordinal)) {
            foreach($stage in @('FilesOffered','FilesPulled','FactsOffered','AcknowledgmentOffered','AcknowledgmentConfirmed','CommitPrepared')) {
                $leftoverSignal=$null
                try {$leftoverSignal=[Threading.EventWaitHandle]::OpenExisting($name+'.frontier-parent-v1.rendezvous-'+$stage)} catch [Threading.WaitHandleCannotBeOpenedException] { }
                if($null -ne $leftoverSignal) {$leftoverSignal.Dispose(); throw 'Rendezvous notification leaked'}
            }
        }
        try { $leftover=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.frontier-parent-v1') } catch [IO.FileNotFoundException] { }
        if($null -ne $leftover) { $leftover.Dispose(); throw 'Parent registration endpoint was not cleaned up' }
        if($case.StartsWith('cursor-parent-files-',[StringComparison]::Ordinal) -or $case.StartsWith('cursor-parent-rendezvous-',[StringComparison]::Ordinal)) {
            try { $leftover=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.frontier-parent-v1.files-v1') } catch [IO.FileNotFoundException] { }
            if($null -ne $leftover) { $leftover.Dispose(); throw 'Parent file endpoint was not cleaned up' }
            if($case.StartsWith('cursor-parent-files-facts-',[StringComparison]::Ordinal) -or $case.StartsWith('cursor-parent-rendezvous-',[StringComparison]::Ordinal)) {
                try { $leftover=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.frontier-parent-v1.files-v1.facts-v1') } catch [IO.FileNotFoundException] { }
                if($null -ne $leftover) { $leftover.Dispose(); throw 'Parent facts endpoint was not cleaned up' }
                try { $leftover=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.frontier-parent-v1.files-v1.ack-v1') } catch [IO.FileNotFoundException] { }
                if($null -ne $leftover) { $leftover.Dispose(); throw 'Parent acknowledgment endpoint was not cleaned up' }
                try { $leftover=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.frontier-parent-v1.files-v1.commit-v1') } catch [IO.FileNotFoundException] { }
                if($null -ne $leftover) { $leftover.Dispose(); throw 'Parent commit endpoint was not cleaned up' }
            }
        }
    }
}
'frontier-gui-launch='+(@{Cases=$results.Count;Results=@($results.ToArray());FileHandoff=$false;ProductionActivated=$false} | ConvertTo-Json -Depth 4 -Compress)
