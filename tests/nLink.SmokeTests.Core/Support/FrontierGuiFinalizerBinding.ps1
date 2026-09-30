# Dot-sourced only by the genuine parent/GUI/two-app seal fixture, after both
# real apps exited and their original output pumps drained. Scalar legacy
# completion controls below test the codec, not finalizer validation authority.
$bindingCase=$Case.Substring('binding-'.Length)
$type=[NLink.Tools.LocalGuiWriterRequest+FrontierFinalizerBinding]
if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) { throw 'Binding fabrication exposed' }
$method=[NLink.Tools.LocalGuiWriterRequest+Finalizer].GetMethod('TryTakeFrontierBinding')
if($method.GetParameters().Length) { throw 'Binding accepts caller evidence' }
function Complete-BindingLegacy($Issuer,[bool]$Valid) {
    $ledger=if($Valid) {'binding-legacy'} else {''}
    return $Issuer.Complete($ledger,(Join-Path $Directory 'legacy-only.log'),3,0,
        [string[]]@(('a'*64),('b'*64),('c'*64)),
        [string[]]@('12345678:1234567812345678','12345678:1234567812345679','12345678:1234567812345680'),
        [long[]]@(12,12,4),[long[]]@(3,3,1),[string[]]@('writer'),[long[]]@(3))
}
function Attach-BindingFinalizer {
    return [NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
}
$proof=$null; $binding=$null; $relay=$null
try {
    if($bindingCase -cin @('finalizer-first','missing-seal','disposed-before')) {
        $finalizer=Attach-BindingFinalizer
        if($null -eq $finalizer) { throw 'Early original finalizer unavailable' }
        if($bindingCase -ceq 'missing-seal') {
            if($null -ne $finalizer.TryTakeFrontierBinding() -or $owner.IsCurrent -or $null -ne $owner.TrySealFiles()) { throw 'Missing seal was accepted or revived' }
            if(-not (Complete-BindingLegacy $finalizer $true)) { throw 'Optional failure prevented legacy completion' }
        } elseif($bindingCase -ceq 'disposed-before') {
            $finalizer.Dispose()
            if($null -ne $finalizer.TryTakeFrontierBinding() -or $owner.IsCurrent) { throw 'Disposed finalizer retained optional owner' }
        }
    }
    if($bindingCase -cnotin @('missing-seal','disposed-before')) {
        $proof=$owner.TrySealFiles()
        if($null -eq $proof -or -not $proof.IsCurrent) { throw 'Binding original source seal unavailable' }
        if($bindingCase -ceq 'endpoint') {
            $finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($SecondaryName,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
        } elseif($bindingCase -ceq 'relay') {
            $relay=[NLink.Tools.LocalGuiWriterRequest]::TryAttachRelay($SecondaryName,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
            if($null -eq $relay) { throw 'Original relay unavailable' }
            $finalizer=$relay.GetType().GetField('owner',$flags).GetValue($relay)
        } elseif($null -eq $finalizer) { $finalizer=Attach-BindingFinalizer }
        if($null -eq $finalizer) { throw 'Original finalizer unavailable' }
        if($bindingCase -ceq 'copied-finalizer') {
            $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($finalizer,$null)
            if($null -ne $copy.TryTakeFrontierBinding() -or (Complete-BindingLegacy $copy $true)) { throw 'Copied finalizer consumed capability' }
            $copy.Dispose()
            if(-not $proof.IsCurrent) { throw 'Copied finalizer retired original set' }
        }
        if($bindingCase -ceq 'complete-before') {
            if(Complete-BindingLegacy $finalizer $false) { throw 'Invalid completion accepted' }
        }
        $binding=$finalizer.TryTakeFrontierBinding()
        if($bindingCase -cin @('endpoint','relay')) {
            if($null -ne $binding -or -not $proof.IsCurrent) { throw 'Foreign endpoint bound or revoked original files' }
            $finalizer.Dispose()
            if(-not $proof.IsCurrent) { throw 'Foreign finalizer disposal revoked original files' }
            $proof.Dispose()
        } elseif($bindingCase -ceq 'complete-before') {
            if($null -ne $binding -or $proof.IsCurrent) { throw 'Prior completion attempt retained binding' }
        } else {
            if($null -eq $binding -or -not $binding.IsCurrent) { throw 'Original finalizer binding unavailable' }
            if($binding.LiveProofEligible -or $binding.NativeWriterCustodyEstablished -or $binding.PublicationCompletionIssued) { throw 'Ownership binding enabled publication' }
            if($bindingCase -ceq 'copied-finalizer') {
                $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($finalizer,$null)
                if($null -ne $copy.TryTakeFrontierBinding() -or (Complete-BindingLegacy $copy $true)) { throw 'Post-binding finalizer copy consumed capability' }
                $copy.Dispose()
                if(-not $binding.IsCurrent) { throw 'Post-binding finalizer copy retired original' }
            }
            if($bindingCase -ceq 'copied-binding') {
                $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($binding,$null)
                if($copy.IsCurrent) { throw 'Copied binding was current' }
                $copy.Dispose()
                if(-not $binding.IsCurrent) { throw 'Copied binding revoked original' }
            }
            switch($bindingCase) {
                'repeat' { if($null -ne $finalizer.TryTakeFrontierBinding()) { throw 'Repeated binding succeeded' } }
                'owner-disposed' { $owner.Dispose() }
                'seal-disposed' { $proof.Dispose() }
                'binding-disposed' { $binding.Dispose(); if(-not (Complete-BindingLegacy $finalizer $true)) { throw 'Binding disposal prevented legacy completion' } }
                'complete-valid' { if(-not (Complete-BindingLegacy $finalizer $true)) { throw 'Original legacy completion failed' } }
                'complete-invalid' { if(Complete-BindingLegacy $finalizer $false) { throw 'Invalid legacy completion succeeded' } }
                'budget' {
                    $work=$adopter.GetType().GetField('work',$flags).GetValue($adopter)
                    $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
                    $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*11))
                }
                'cancel' {
                    $cancel=[Threading.EventWaitHandle]::OpenExisting($Name+'.closed')
                    try {
                        [void]$cancel.Set()
                        if($binding.IsCurrent) { throw 'Cancelled binding remained current' }
                        [void]$cancel.Reset()
                    } finally { $cancel.Dispose() }
                }
            }
            if($bindingCase -cin @('reserved','stage','registration','gui-incarnation','parent-incarnation')) {
                $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($Name); $view=$map.CreateViewAccessor()
                try {
                    $offset=switch($bindingCase) {'reserved' {56L}; 'stage' {48L}; 'registration' {64L}; 'gui-incarnation' {40L}; 'parent-incarnation' {8L}}
                    $old=$view.ReadInt64($offset); $view.Write($offset,($old+1L))
                    if($binding.IsCurrent) { throw 'Stale native scope retained binding' }
                    $view.Write($offset,$old)
                } finally { $view.Dispose(); $map.Dispose() }
            }
            if($bindingCase -cin @('valid','finalizer-first','copied-finalizer','copied-binding')) {
                if(-not $binding.IsCurrent) { throw 'Valid binding lost original ownership' }
                $finalizer.Dispose()
            }
            if($binding.IsCurrent -or $proof.IsCurrent -or $owner.IsCurrent -or $null -ne $finalizer.TryTakeFrontierBinding()) { throw 'Retired binding or original set revived' }
        }
    }
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Binding cleanup retained payload files' }
    [IO.File]::AppendAllText($source,'binding-released')
    if(Test-Path -LiteralPath (Join-Path $Directory 'legacy-only.log')) { throw 'Ownership binding opened a legacy source path' }
} finally {
    if($null -ne $binding) { $binding.Dispose() }
    if($null -ne $relay) { $relay.Dispose() }
}
