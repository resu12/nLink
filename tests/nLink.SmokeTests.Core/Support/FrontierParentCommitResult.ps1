if(($null -ne $completion) -ne $commitLegacyExpected) {throw 'Commit path changed original legacy result'}
if(@($pulledHandles | Where-Object {-not $_.IsClosed}).Count) {throw 'Original provisional pins survived completion'}
if($clock.IsRunning) {throw 'Parent work clock did not stop after completion'}
if($null -ne $custody) {
    if($custody.IsCurrent -ne $commitShouldAccept) {throw "Custody/exit/completion agreement failed: $commitCase"}
    if($commitShouldAccept) {
        if($custody.FileCount -ne $custodyPins.Count -or $custody.SourceLineCount -le 0 -or
            @($custodyPins | Where-Object {$_.IsClosed}).Count) {throw 'Post-GUI custody lost exact native set'}
        if($commitCase -ceq 'path-binding') {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentPathBinding.ps1')
        }
        if($commitCase -ceq 'copied-custody') {
            $foreign=$parentClone.Invoke($custody,$null)
            if($foreign.IsCurrent -or $foreign.FileCount) {throw 'Copied committed custody current'}
            $foreign.Dispose(); if(-not $custody.IsCurrent) {throw 'Copied committed custody retired original'}
        }
        if($commitCase.StartsWith('primary-',[StringComparison]::Ordinal)) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryRequest.ps1')
        }
        switch($commitCase) {
            'repeat-take' {if($null -ne $request.TakeAfterExit()) {throw 'Original completion replayed'}}
            'post-accept-mutation' {$commitView.Write(80L,[byte]($commitView.ReadByte(80L) -bxor 1))}
            'post-accept-pin' {$custodyPins[0].Dispose()}
        }
        if($commitCase -cin @('repeat-take','post-accept-mutation','post-accept-pin') -and $custody.IsCurrent) {throw 'Stale committed custody retained'}
    }
    if($commitCase -cin @('output-gate','state-gate')) {$request.Dispose()}
    else {$custody.Dispose()}
    if($custody.IsCurrent -or $custody.FileCount -or @($custodyPins | Where-Object {-not $_.IsClosed}).Count) {throw 'Custody disposal leaked native files'}
}
if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) {throw 'Final parent custody leaked payloads'}
