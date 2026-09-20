param([string]$Phase)
$parentCase=$case.Substring('cursor-parent-'.Length)
$parentFlags=[Reflection.BindingFlags]'Instance,NonPublic'
$parentClone=[object].GetMethod('MemberwiseClone',$parentFlags)
if($parentCase.StartsWith('rendezvous-',[StringComparison]::Ordinal)) {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentRendezvous.ps1') -Phase $Phase
    return
}
if($Phase -ceq 'register') {
    foreach($part in @('parent-attached','parent-reviewed')) {
        $events[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$name+'.test.'+$part)
    }
    if($parentCase.StartsWith('files-',[StringComparison]::Ordinal)) {
        foreach($part in @('files-offered','files-reviewed','files-retired','files-finish','facts-start','facts-offered','facts-reviewed','ack-start','ack-offered','ack-confirmed','ack-reviewed','commit-start','commit-ready','commit-published','commit-reviewed')) {
            $events[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$name+'.test.'+$part)
        }
    }
    foreach($type in @([NLink.Tools.LocalGuiWriterRequest+FrontierParentRegistration],[NLink.Tools.LocalGuiWriterRequest+FrontierParentHandoff])) {
        if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) {throw 'Parent capability fabrication exposed'}
    }
    if($parentCase -ceq 'copied-request') {
        $copy=$parentClone.Invoke($request,$null)
        if($null -ne $copy.TryRegisterFrontierParent() -or $copy.BindWorker($process) -or $null -ne $copy.TakeAfterExit()) {throw 'Copied parent request consumed original'}
        $copy.Dispose()
    }
    if($parentCase -ceq 'relay') {
        $relay=[NLink.Tools.LocalGuiWriterRequest]::TryCreateRelay($request.RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
        try {
            if(-not $relay.BindWorker($process) -or $null -ne $relay.TryRegisterFrontierParent()) {throw 'Relay parent registered'}
        } finally {$relay.Dispose()}
    } elseif($parentCase -cne 'missing') {
        $parentRegistration=$request.TryRegisterFrontierParent()
        if($parentCase -ceq 'unbound') {
            if($null -ne $parentRegistration) {throw 'Unbound registration retried'}
        } else {
            if($null -eq $parentRegistration -or -not $parentRegistration.IsCurrent -or $parentRegistration.GuiAttached -or
                $parentRegistration.LiveProofEligible -or $parentRegistration.NativeWriterCustodyEstablished -or $parentRegistration.PublicationCompletionIssued) {throw 'Original parent registration missing/authority'}
            if($parentCase -ceq 'copied-registration') {
                $copy=$parentClone.Invoke($parentRegistration,$null)
                if($copy.IsCurrent -or $copy.GuiAttached) {throw 'Copied registration current'}
                $copy.Dispose(); if(-not $parentRegistration.IsCurrent) {throw 'Copied registration retired original'}
            }
            $parentMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.frontier-parent-v1')
            $parentView=$parentMap.CreateViewAccessor(0L,128L)
            if($parentCase -ceq 'foreign-open') {
                $channelType=[NLink.Tools.LocalGuiWriterRequest].GetNestedType('ParentRegistrationChannel',[Reflection.BindingFlags]::NonPublic)
                $body=$parentRegistration.GetType().GetField('Body',$parentFlags).GetValue($parentRegistration)
                $method=$channelType.GetMethod('Attach',[Reflection.BindingFlags]'Static,NonPublic')
                $foreign=$method.Invoke($null,[object[]]@($name,[guid]::NewGuid(),$PID,$parentView.ReadInt64(56L),$process.Id,$parentView.ReadInt64(64L),$body))
                if($null -ne $foreign -or -not $parentRegistration.IsCurrent) {throw 'Rejected foreign open retired original'}
            }
            switch($parentCase) {
                'request-id' {$parentView.Write(16L,[byte]($parentView.ReadByte(16L) -bxor 1))}
                'parent-process' {$parentView.Write(48L,$PID+1)}
                'gui-process' {$parentView.Write(52L,$PID)}
                'parent-incarnation' {$parentView.Write(56L,$parentView.ReadInt64(56L)+1L)}
                'gui-incarnation' {$parentView.Write(64L,$parentView.ReadInt64(64L)+1L)}
                'digest' {$parentView.Write(72L,[byte]($parentView.ReadByte(72L) -bxor 1))}
                'reserved' {$parentView.Write(104L,1)}
                'nonce' {for($i=32;$i -lt 48;$i++) {$parentView.Write([long]$i,[byte]0)}}
                'version' {$parentView.Write(4L,2)}
                'state' {$parentView.Write(8L,2)}
            }
        }
    }
} else {
    if(-not $events['parent-attached'].WaitOne(20000)) {
        if($process.HasExited) {throw "Parent fixture exited before registration: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"}
        throw 'Parent attachment deadline'
    }
    $rejected=$parentCase -cin @('unbound','relay','missing','early','before-persistence','request-id','parent-process','gui-process','parent-incarnation','gui-incarnation','digest','reserved','nonce','version','state','budget')
    if($null -ne $parentRegistration -and $parentRegistration.GuiAttached -eq $rejected) {throw 'Parent attachment observation mismatch'}
    if(-not $rejected -and ($null -eq $parentRegistration -or -not $parentRegistration.IsCurrent)) {throw 'Parent registration lost before review'}
    switch($parentCase) {
        'parent-dispose' {$parentRegistration.Dispose()}
        'request-dispose' {$request.Dispose()}
        'repeat-parent' {if($null -ne $request.TryRegisterFrontierParent() -or $parentRegistration.IsCurrent) {throw 'Repeated parent registration retained original'}}
        'state-regression' {$parentView.Write(8L,0); if($parentRegistration.IsCurrent) {throw 'Parent state regression accepted'}}
        'legacy-body' {$view.Write(64L,[byte]($view.ReadByte(64L) -bxor 1)); if($parentRegistration.IsCurrent) {throw 'Changed legacy scope accepted'}}
    }
    [void]$events['parent-reviewed'].Set()
    if($parentCase.StartsWith('files-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentFiles.ps1')
    }
}
