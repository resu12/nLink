param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$Directory,[string]$Case,[string]$DeathResult='',
    [string]$CoreAssembly,[string]$FixtureAssembly,[switch]$App,[string]$SignalName,[int]$Ordinal=0,[string]$SecondaryName='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($App) {
    $events=@{}
    foreach($part in @('begin','prepared','ack','done','exit','held-ready')) { $events[$part]=[Threading.EventWaitHandle]::OpenExisting($SignalName+'.'+$part) }
    $lease=$null; $fixture=$null
    try {
        if(-not $events.begin.WaitOne(20000)) { throw 'Seal app begin deadline' }
        $env:NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH=Join-Path $Directory 'shared.log'
        if(($Case.StartsWith('ledger-',[StringComparison]::Ordinal) -and $Case -cnotin @('ledger-wrong-source','ledger-after-launch')) -or $Case.StartsWith('cursor-',[StringComparison]::Ordinal)) {
            $env:NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH=Join-Path $Directory 'live-operational-log/nlink.log'
        }
        $env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR=$Directory
        $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='frontier-gui-run'
        $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='frontier-gui-scenario'
        [void][Reflection.Assembly]::LoadFrom($CoreAssembly)
        [void][Reflection.Assembly]::LoadFrom($FixtureAssembly)
        $lease=[NLink.Tools.LocalFrontierAppRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario')
        if($null -eq $lease) { throw 'Seal original app lease missing' }
        if($Case -ceq 'cursor-join-prefix' -and $Ordinal -eq 0) {
            [NLink.SmokeTests.FrontierGenuineSubmissionFixture]::WriteStartupPrefix()
            [void]$events['held-ready'].Set()
            $prefixRelease=[Threading.EventWaitHandle]::OpenExisting($SignalName+'.held-release')
            try { if(-not $prefixRelease.WaitOne(15000)) { throw 'Cursor prefix release deadline' } } finally { $prefixRelease.Dispose() }
        }
        $variant=if($Case -cin @('both_positive','two_payloads')) {$Case} else {'valid'}
        if($Case -cin @('cursor-parent-prepare-both-positive','cursor-parent-files-both-positive')) {$variant='both_positive'}
        if($Case -cin @('cursor-parent-prepare-two-payloads','cursor-parent-files-two-payloads','cursor-parent-files-facts-two-payloads')) {$variant='two_payloads'}
        if($Case -cin @('cursor-parent-files-facts-ack-commit-primary-worker-payloads-two-payloads','cursor-parent-files-facts-ack-commit-primary-worker-payloads-partial-hash')) {$variant='two_payloads'}
        if($Case -ceq 'cursor-parent-files-facts-ack-commit-primary-worker-payloads-both-writers') {$variant='both_positive'}
        if($Case -cin @('cursor-parent-files-facts-ack-commit-primary-worker-witnesses-maximum','cursor-parent-files-facts-ack-commit-primary-worker-witnesses-over-cap')) {$variant='witness_rows'}
        if($Case.StartsWith('cursor-parent-files-facts-ack-commit-primary-worker-join-',[StringComparison]::Ordinal) -or
            $Case.StartsWith('cursor-parent-files-facts-ack-commit-primary-prepare-Wrapper-start-rendezvous-',[StringComparison]::Ordinal) -or
            $Case.StartsWith('cursor-parent-files-facts-ack-commit-primary-prepare-RouteAcceptance-start-rendezvous-',[StringComparison]::Ordinal) -or
            $Case.StartsWith('cursor-parent-files-facts-ack-commit-primary-parent-',[StringComparison]::Ordinal) -or
            $Case.StartsWith('cursor-parent-files-facts-ack-commit-primary-prepare-Wrapper-start-analyzer-',[StringComparison]::Ordinal) -or
            $Case.StartsWith('cursor-parent-files-facts-ack-commit-primary-prepare-RouteAcceptance-start-analyzer-',[StringComparison]::Ordinal) -or
            $Case -cin @('cursor-parent-files-facts-ack-commit-primary-prepare-Wrapper-worker-reports',
                'cursor-parent-files-facts-ack-commit-primary-prepare-RouteAcceptance-worker-reports',
                'cursor-parent-files-facts-ack-commit-primary-prepare-Wrapper-legacy-isolation',
                'cursor-parent-files-facts-ack-commit-primary-prepare-RouteAcceptance-legacy-isolation',
                'cursor-parent-files-facts-ack-commit-primary-prepare-Wrapper-start-auto-terminal',
                'cursor-parent-files-facts-ack-commit-primary-prepare-RouteAcceptance-start-auto-terminal')) {$variant='primary_failure'}
        if($Case -ceq 'cursor-parent-files-facts-ack-commit-primary-worker-join-witness-overflow') {$variant='primary_overflow'}
        $fixture=[NLink.SmokeTests.FrontierGenuineSubmissionFixture]::Create($lease,$variant,$Ordinal)
        [void]$fixture.PrepareAsync().GetAwaiter().GetResult()
        if(-not $fixture.Submit()) { throw 'Seal genuine submission failed' }
        if($Case.StartsWith('markers-',[StringComparison]::Ordinal)) { $fixture.WriteRecoveryMarkers($Case.Substring('markers-'.Length)) }
        if($Case -ceq 'cursor-parent-files-facts-markers') {$fixture.WriteRecoveryMarkers('valid')}
        [void]$events.prepared.Set()
        if(-not $events.ack.WaitOne(15000) -or -not $fixture.TakeAck()) { throw 'Seal app acknowledgment failed' }
        if($Case -cne 'missing-completion' -or $Ordinal -ne 1) {
            if(-not $lease.CompleteAttachment()) { throw 'Seal app completion failed' }
        }
        if($Case.StartsWith('recovery-',[StringComparison]::Ordinal)) {
            if($Case -cne 'recovery-empty') {
                $raw=[byte[]]::new(70003)
                for($i=0;$i -lt $raw.Length;$i++) { $raw[$i]=[byte]($i % 256) }
                $out=[Console]::OpenStandardOutput(); $err=[Console]::OpenStandardError()
                $out.Write($raw,0,$raw.Length); $out.Flush()
                $marker=[Text.Encoding]::UTF8.GetBytes("event=operational_log_write_deferred; log_writer_id=fixture-$Ordinal; failure_code=raw-marker-custody-$([char]0x17e)`r`n")
                $err.Write($marker,0,$marker.Length); $err.Write($raw,0,$raw.Length); $err.Flush()
            }
        } else { [Console]::WriteLine('original-app-output'); [Console]::Error.WriteLine('original-app-error') }
        if($Case -ceq 'markers-truncated-utf8' -and $Ordinal -eq 0) {
            $errorOutput=[Console]::OpenStandardError(); $tail=[byte[]]@(0xe2,0x82)
            $errorOutput.Write($tail,0,$tail.Length); $errorOutput.Flush()
        }
        if($Ordinal -eq 1 -and $Case -cin @('held-stdout','held-stderr')) {
            Add-Type 'using System; using System.Runtime.InteropServices; public static class SealOutputNative { [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int kind); [DllImport("kernel32.dll")] public static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags); }'
            foreach($kind in @(-11,-12)) {
                $inherit=if(($Case -ceq 'held-stdout' -and $kind -eq -11) -or ($Case -ceq 'held-stderr' -and $kind -eq -12)) {1} else {0}
                if(-not [SealOutputNative]::SetHandleInformation([SealOutputNative]::GetStdHandle($kind),1,$inherit)) { throw 'Seal pipe inheritance failed' }
            }
            $start=[Diagnostics.ProcessStartInfo]::new()
            $start.FileName=(Get-Command pwsh).Source; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
            $args=@('-NoProfile','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiOutputWorker.ps1'),'-HoldPipe','-SignalName',$SignalName,'-Case',$Case)
            $start.Arguments=($args | ForEach-Object {'"'+$_+'"'}) -join ' '
            $holder=[Diagnostics.Process]::Start($start)
            try { if(-not $events['held-ready'].WaitOne(15000)) { throw 'Seal pipe holder deadline' } } finally { $holder.Dispose() }
        }
        [void]$events.done.Set()
        if(-not $events.exit.WaitOne(20000)) { throw 'Seal app exit deadline' }
        if($Case -ceq 'nonzero' -and $Ordinal -eq 1) { exit 7 }
    } finally {
        if($null -ne $fixture) { $fixture.Dispose() }
        if($null -ne $lease) { $lease.Dispose() }
        foreach($event in $events.Values) { $event.Dispose() }
    }
    exit 0
}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$begin=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.begin')
$attached=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.attached')
$release=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.release')
$owner=$null; $finalizer=$null; $heldWriter=$null
$apps=[Collections.Generic.List[object]]::new()
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
function Wait-SealCheck([scriptblock]$Check,[string]$Message) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while(-not (& $Check)) { if($clock.ElapsedMilliseconds -ge 12000) { throw $Message }; Start-Sleep -Milliseconds 10 }
}
function Finish-SealApps {
    foreach($entry in $apps) { [void]$entry.Events.exit.Set() }
    foreach($entry in $apps) {
        if(-not $entry.Process.WaitForExit(10000)) { throw 'Seal app did not exit' }
        $expected=if($Case -ceq 'nonzero' -and $entry.Ordinal -eq 1) {7} else {0}
        if($entry.Process.ExitCode -ne $expected) { throw "Seal app exited $($entry.Process.ExitCode)" }
    }
}
try {
    if(-not $begin.WaitOne(20000)) { throw 'Seal GUI bind deadline' }
    $owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncher($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
    if($null -eq $owner) { throw 'Seal original GUI unavailable' }
    $ledgerOrigin=$null
    if(($Case.StartsWith('ledger-',[StringComparison]::Ordinal) -and $Case -cne 'ledger-after-launch') -or $Case.StartsWith('cursor-',[StringComparison]::Ordinal)) {
        $ledgerOrigin=$owner.TryCreateLedgerOrigin()
        if($null -eq $ledgerOrigin -or -not $ledgerOrigin.IsCurrent) { throw 'Early original ledger creation missing' }
        if($Case -ceq 'ledger-shared-budget') {
            $creationWork=$owner.GetType().GetField('frontierWork',$flags).GetValue($owner)
            $elapsed=@($creationWork.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($creationWork,[long]([Diagnostics.Stopwatch]::Frequency*2))
        }
    }
    $type=[NLink.Tools.LocalGuiWriterRequest+FrontierSealedFiles]
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) { throw 'Public seal fabrication exposed' }
    $requests=@($owner.TryCreateAppRequest(),$owner.TryCreateAppRequest())
    for($i=0;$i -lt 2;$i++) {
        $signal=$Name+'.seal.'+$i; $events=@{}
        foreach($part in @('begin','prepared','ack','done','exit','held-ready','held-release')) {
            $events[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$signal+'.'+$part)
        }
        $process=[Diagnostics.Process]::new()
        $process.StartInfo.FileName=(Get-Command pwsh).Source; $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
        $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
        $args=@('-NoProfile','-File',$PSCommandPath,'-App','-RepoRoot',$RepoRoot,'-Name',$requests[$i].Name,'-RequestId',$requests[$i].RequestId.ToString('D'),
            '-Directory',$Directory,'-CoreAssembly',$CoreAssembly,'-FixtureAssembly',$FixtureAssembly,'-SignalName',$signal,'-Ordinal',"$i",'-Case',$Case)
        $process.StartInfo.Arguments=($args | ForEach-Object {'"'+$_+'"'}) -join ' '
        $entry=@{Process=$process;Events=$events;Binding=$null;Ordinal=$i;Out=$null;Err=$null}; $apps.Add($entry)
        if($Case -ceq 'missing-output' -and $i -eq 1) {
            [void]$process.Start(); $entry.Out=$process.StandardOutput.ReadToEndAsync(); $entry.Err=$process.StandardError.ReadToEndAsync()
            $entry.Binding=$requests[$i].BindApp($process)
        } else { $entry.Binding=$requests[$i].StartAppWithOutput($process) }
        if($null -eq $entry.Binding) { throw 'Seal original output launch failed' }
        if($i -eq 0 -and $Case.StartsWith('cursor-',[StringComparison]::Ordinal)) {
            $initialObservation=$null
            if($Case -ceq 'cursor-join-prefix') {
                [void]$entry.Events.begin.Set()
                if(-not $entry.Events['held-ready'].WaitOne(15000)) { throw 'Original writer prefix deadline' }
            } elseif($Case -cnotin @('cursor-empty-initial','cursor-join-empty')) { [IO.File]::AppendAllText($ledgerOrigin.SourcePath,"cursor startup fixture`r`n",[Text.UTF8Encoding]::new($false)) }
            if($Case -cnotin @('cursor-missing-initial','cursor-join-missing-initial')) {
                $initialObservation=$ledgerOrigin.TryCaptureInitialObservation()
                $expectedBookmark=if($Case -ceq 'cursor-join-prefix') {2} else {[int]($Case -cnotin @('cursor-empty-initial','cursor-join-empty'))}
                if($null -eq $initialObservation -or -not $initialObservation.IsCurrent -or $initialObservation.Bookmark -ne $expectedBookmark) { throw 'Cursor initial phase observation missing' }
            }
        }
        if($Case -ceq 'capture-fault' -and $i -eq 1) {
            $capture=$entry.Binding.GetType().GetField('Output',$flags).GetValue($entry.Binding)
            $capture.GetType().GetField('stdoutTarget',$flags).GetValue($capture).Dispose()
        }
    }
    if(-not $owner.SealLaunches() -or -not $owner.TryEnableFileHandoff()) { throw 'Seal original channels unavailable' }
    if($Case -ceq 'cursor-join-prefix') { [void]$apps[0].Events['held-release'].Set() }
    # First writer drains before the second starts writing the same source.
    for($i=0;$i -lt 2;$i++) {
        $entry=$apps[$i]; [void]$entry.Events.begin.Set()
        if(-not $entry.Events.prepared.WaitOne(15000)) { throw 'Seal genuine preparation deadline' }
        if(-not $requests[$i].TryAdoptFiles()) { throw 'Seal original GUI adoption failed' }
        [void]$entry.Events.ack.Set()
        if(-not $entry.Events.done.WaitOne(15000)) { throw 'Seal original app completion deadline' }
        if($Case.StartsWith('cursor-',[StringComparison]::Ordinal) -and -not $ledgerOrigin.TrySynchronize()) { throw 'Cursor live adopted synchronization failed' }
    }
    if(-not $owner.AllFileHandoffsAcknowledged) { throw 'Seal original acknowledgments missing' }
    [void]$attached.Set()
    if(-not $release.WaitOne(20000)) { throw 'Seal GUI release deadline' }
    if($Case -ceq 'early') {
        if($null -ne $owner.TrySealFiles() -or $owner.IsCurrent -or @($apps | Where-Object {$_.Process.HasExited}).Count) { throw 'Live app accepted as closed writer or stopped by failure' }
    }
    Finish-SealApps
    foreach($entry in $apps) {
        $capture=$entry.Binding.GetType().GetField('Output',$flags).GetValue($entry.Binding)
        if($null -eq $capture) { continue }
        $outTask=$capture.GetType().GetField('stdout',$flags).GetValue($capture)
        $errTask=$capture.GetType().GetField('stderr',$flags).GetValue($capture)
        if($entry.Ordinal -eq 1 -and $Case -cin @('held-stdout','held-stderr')) {
            $closed=if($Case -ceq 'held-stdout') {$errTask} else {$outTask}
            Wait-SealCheck {$closed.IsCompleted} 'Non-inherited output did not drain'
            if($owner.AllAppOutputsDrainedSuccessfully -or -not $owner.AllAppRequestsCompletedSuccessfully) { throw 'Seal missing EOF control invalid' }
        } else { Wait-SealCheck {$outTask.IsCompleted -and $errTask.IsCompleted} 'Seal original output did not finish' }
    }
    $source=Join-Path $Directory 'shared.log'
    if(($Case.StartsWith('ledger-',[StringComparison]::Ordinal) -and $Case -cnotin @('ledger-wrong-source','ledger-after-launch')) -or $Case.StartsWith('cursor-',[StringComparison]::Ordinal)) { $source=$ledgerOrigin.SourcePath }
    $adopter=$owner.GetType().GetField('fileAdoption',$flags).GetValue($owner)
    if($Case -ceq 'mutated') {
        $items=$adopter.GetType().GetField('adopted',$flags).GetValue($adopter)
        $manifest=$items[0].GetType().GetField('Manifest',$flags).GetValue($items[0])
        $range=$manifest.GetType().GetField('Drain',$flags).GetValue($manifest)
        $offset=$range.GetType().GetField('Offset',$flags).GetValue($range)
        $write=[IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        try { $write.Position=$offset; $byte=$write.ReadByte(); $write.Position=$offset; $write.WriteByte([byte]($byte -bxor 1)) } finally { $write.Dispose() }
    }
    if($Case -ceq 'rotated') { [IO.File]::Move($source,$source+'.original'); [IO.File]::WriteAllText($source,'replacement'); $source+='.original' }
    if($Case -ceq 'budget') {
        $work=$adopter.GetType().GetField('work',$flags).GetValue($adopter)
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*6))
    }
    if($Case -ceq 'copied') {
        $ownerCopy=[object].GetMethod('MemberwiseClone',$flags).Invoke($owner,$null)
        if($null -ne $ownerCopy.TrySealFiles()) { throw 'Copied owner sealed files' }
        $ownerCopy.Dispose()
        if(-not $owner.IsCurrent) { throw 'Copied owner revoked original before seal' }
    }
    $bytes=[IO.File]::ReadAllBytes($source); $sha=[Security.Cryptography.SHA256]::Create()
    try { $expectedHash=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
    if($Case -ceq 'writer-open') { $heldWriter=[IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete) }
    if($Case.StartsWith('matching-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiWriterMatch.ps1')
        'frontier-gui-worker=original-writer-match-verified'
        return
    }
    if($Case.StartsWith('ledger-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiLedgerOriginSeal.ps1')
        'frontier-gui-worker=original-ledger-origin-seal-verified'
        return
    }
    if($Case.StartsWith('cursor-join-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiCursorMatch.ps1')
        'frontier-gui-worker=original-cursor-match-verified'
        return
    }
    if($Case.StartsWith('cursor-parent-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiParentRegistration.ps1')
        'frontier-gui-worker=original-parent-registration-verified'
        return
    }
    if($Case.StartsWith('cursor-persist-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiEvidencePersistence.ps1')
        'frontier-gui-worker=original-evidence-persistence-verified'
        return
    }
    if($Case.StartsWith('cursor-copy-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiSemanticCopy.ps1')
        'frontier-gui-worker=original-semantic-copy-verified'
        return
    }
    if($Case.StartsWith('cursor-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiLedgerCursorFinal.ps1')
        'frontier-gui-worker=original-ledger-cursor-final-verified'
        return
    }
    if($Case.StartsWith('markers-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiRecoveryMatch.ps1')
        'frontier-gui-worker=original-recovery-match-verified'
        return
    }
    if($Case.StartsWith('recovery-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiRecoveryRead.ps1')
        'frontier-gui-worker=original-recovery-inputs-verified'
        return
    }
    if($Case.StartsWith('processing-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiLedgerProcessing.ps1')
        'frontier-gui-worker=original-ledger-processing-verified'
        return
    }
    if($Case.StartsWith('source-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiSourceRead.ps1')
        'frontier-gui-worker=original-finalizer-source-verified'
        return
    }
    if($Case.StartsWith('binding-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiFinalizerBinding.ps1')
        'frontier-gui-worker=original-finalizer-binding-verified'
        return
    }
    $proof=$owner.TrySealFiles()
    $positive=$Case -cin @('valid','both_positive','two_payloads','copied','rotated','repeat')
    if(($null -ne $proof) -ne $positive) { throw "Wrong original final seal: $Case" }
    if($positive) {
        $ranges=if($Case -cin @('both_positive','two_payloads')) {6} else {4}
        if(-not $proof.IsCurrent -or $proof.SourceBytes -ne $bytes.Length -or $proof.SourceSha256 -cne $expectedHash -or $proof.VerifiedRangeCount -ne $ranges) { throw 'Seal content/range evidence mismatch' }
        if($proof.LiveProofEligible -or $proof.NativeWriterCustodyEstablished -or $proof.PublicationCompletionIssued) { throw 'Seal enabled publication' }
        $blocked=$false
        try { $probe=[IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete); $probe.Dispose() } catch [IO.IOException] { $blocked=$true }
        if(-not $blocked) { throw 'Final source remained writable' }
        if($Case -ceq 'copied') {
            $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($proof,$null)
            if($copy.IsCurrent) { throw 'Copied final seal accepted' }
            $copy.Dispose(); if(-not $proof.IsCurrent) { throw 'Copied final seal retired original' }
        }
        if($Case -ceq 'repeat') {
            if($null -ne $owner.TrySealFiles() -or $proof.IsCurrent -or $owner.IsCurrent) { throw 'Repeated seal retained original result' }
        } else { $proof.Dispose(); if($proof.IsCurrent -or $owner.IsCurrent) { throw 'Seal disposal did not revoke original' } }
    } elseif($owner.IsCurrent -or $null -ne $owner.TrySealFiles()) { throw 'Failed seal retained optional result' }
    foreach($entry in $apps) { [void]$entry.Events['held-release'].Set() }
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Final seal cleanup retained payload handles' }
    if($null -ne $heldWriter) { $heldWriter.Dispose(); $heldWriter=$null }
    [IO.File]::AppendAllText($source,'released')
    if($Case -cne 'cancel-after') {
        $finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
        if($null -eq $finalizer) { throw 'Final seal consumed legacy finalizer' }
    }
    'frontier-gui-worker=original-final-source-seal-verified'
} finally {
    if($null -ne $heldWriter) { $heldWriter.Dispose() }
    if($null -ne $owner) { $owner.Dispose() }
    foreach($entry in $apps) {
        foreach($part in @('begin','ack','exit','held-release')) { [void]$entry.Events[$part].Set() }
        if(-not $entry.Process.HasExited -and -not $entry.Process.WaitForExit(5000)) { $entry.Process.Kill(); [void]$entry.Process.WaitForExit(5000) }
        $entry.Process.Dispose(); foreach($event in $entry.Events.Values) { $event.Dispose() }
    }
    if($null -ne $finalizer) { $finalizer.Dispose() }
    $begin.Dispose(); $attached.Dispose(); $release.Dispose()
}
