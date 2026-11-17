. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierLedgerProcessing.ps1')
$mode=$Case.Substring('ledger-'.Length)
$binding=$null; $capture=$null
try {
    if($mode -ceq 'after-launch') {
        if($null -ne $owner.TryCreateLedgerOrigin() -or $owner.IsCurrent) { throw 'Late ledger creation accepted' }
    } else {
        $native=$ledgerOrigin.GetType().GetField('file',$flags).GetValue($ledgerOrigin)
        $creationWork=$owner.GetType().GetField('frontierWork',$flags).GetValue($owner)
        $adoptionWork=$adopter.GetType().GetField('work',$flags).GetValue($adopter)
        if(-not [object]::ReferenceEquals($creationWork,$adoptionWork) -or $creationWork.IsRunning) { throw 'Creation and adoption budgets split' }
        if($mode -ceq 'shared-budget' -and $creationWork.ElapsedMilliseconds -lt 2000) { throw 'Creation budget was reset' }
        if($mode -ceq 'wrong-source') {
            # Same exact bytes and run scope are not the actual native file.
            [IO.File]::WriteAllBytes($ledgerOrigin.SourcePath,[IO.File]::ReadAllBytes($source))
        }
        $lineCount=[IO.File]::ReadAllLines($source).Length
        $proof=$owner.TrySealFiles()
        if($mode -ceq 'wrong-source') {
            if($null -ne $proof -or $owner.IsCurrent -or $ledgerOrigin.IsCurrent -or $native.CanRead) { throw 'Same-content foreign source matched creation' }
        } else {
            if($null -eq $proof -or -not $proof.IsCurrent -or -not $ledgerOrigin.IsCurrent -or $native.Length -ne $proof.SourceBytes -or $native.Position -ne 0) { throw 'Creation handle failed to coexist with exact source seal' }
            $finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
            if($null -eq $finalizer) { throw 'Ledger origin finalizer missing' }
            $binding=$finalizer.TryTakeFrontierBinding()
            if($null -eq $binding -or -not [object]::ReferenceEquals($binding.LedgerOrigin,$ledgerOrigin)) { throw 'Finalizer lost exact creation owner' }
            if($mode -ceq 'copied-binding') {
                $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($binding,$null)
                if($null -ne $copy.LedgerOrigin) { throw 'Copied finalizer binding acquired ledger origin' }
                $copy.Dispose(); if(-not $ledgerOrigin.IsCurrent) { throw 'Copied binding retired ledger origin' }
            }
            $result=Invoke-FileTransferFrontierLedgerProcessing -Binding $binding -FrozenLineCount $lineCount -Bookmark 0
            if($null -eq $result -or $null -eq $binding.WriterMatch -or $null -eq $binding.RecoveryMatch -or -not $ledgerOrigin.IsCurrent -or
                $ledgerOrigin.CursorHistoryValidated -or $ledgerOrigin.LiveProofEligible -or $ledgerOrigin.NativeWriterCustodyEstablished -or $ledgerOrigin.PublicationCompletionIssued) { throw 'Created source lost existing gates or enabled authority' }
            $capture=$result.StreamCapture
            if($mode -ceq 'closed-after-seal') { $native.Dispose() }
            else { $binding.Dispose() }
            if($ledgerOrigin.IsCurrent -or $binding.IsCurrent -or $null -ne $binding.LedgerOrigin -or $capture.IsCurrent -or $proof.IsCurrent -or $native.CanRead) { throw 'Original ledger retirement retained finalizer resources' }
        }
        [IO.File]::AppendAllText($ledgerOrigin.SourcePath,'creation-released')
    }
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Ledger origin retirement retained payloads' }
    [IO.File]::AppendAllText($source,'ledger-origin-released')
} finally {
    if($null -ne $capture) { $capture.Dispose() }
    if($null -ne $binding) { $binding.Dispose() }
}
