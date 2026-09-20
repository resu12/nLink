$prepareOwner=if($primaryCase.StartsWith('prepare-Wrapper-')) {'Wrapper'} else {'RouteAcceptance'}
$prepareCase=$primaryCase.Substring(('prepare-'+$prepareOwner+'-').Length)
$sourcePreparation=$null; $priorHash=$null; $legacyLock=$null
$attempt=[guid]::NewGuid(); $source=$completion.Outputs[3].Path
try {
    if($prepareCase -cin @('valid','foreign-arguments') -and [IO.File]::Exists($source)) {throw 'Native-only fixture unexpectedly has a legacy analyzer file'}
    if($prepareCase -ceq 'legacy-isolation') {
        # Conflicting legacy bytes remain exclusively locked through the native
        # hash/count, primary worker and terminal receipt/persistence path.
        $legacyLock=[IO.FileStream]::new($source,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $sentinel=[Text.Encoding]::UTF8.GetBytes('legacy-label-must-not-be-opened')
        $legacyLock.Write($sentinel,0,$sentinel.Length); $legacyLock.Flush()
    }
    [PrimaryPositionProbe]::Set($custodyPins[4],29L)
    if($prepareCase -ceq 'foreign-arguments') {
        $copy=$parentClone.Invoke($custody,$null)
        if($null -ne $copy.TryPreparePrimarySource($attempt,$prepareOwner,'frontier-gui-run','frontier-gui-scenario',$source,$completion)) {throw 'Copied custody prepared source'}
        $copy.Dispose()
        $copyCompletion=$parentClone.Invoke($completion,$null)
        foreach($args in @(
            @($attempt,$prepareOwner,'frontier-gui-run','frontier-gui-scenario',$source,$copyCompletion),
            @([guid]::Empty,$prepareOwner,'frontier-gui-run','frontier-gui-scenario',$source,$completion),
            @($attempt,'invalid-owner','frontier-gui-run','frontier-gui-scenario',$source,$completion),
            @($attempt,$prepareOwner,'foreign-run','frontier-gui-scenario',$source,$completion),
            @($attempt,$prepareOwner,'frontier-gui-run','foreign-scenario',$source,$completion),
            @($attempt,$prepareOwner,'frontier-gui-run','frontier-gui-scenario',$completion.Outputs[0].Path,$completion),
            @($attempt,$prepareOwner,'frontier-gui-run','frontier-gui-scenario',$source.ToUpperInvariant(),$completion),
            @($attempt,$prepareOwner,'frontier-gui-run','frontier-gui-scenario',$null,$completion))) {
            if($null -ne $custody.TryPreparePrimarySource($args[0],$args[1],$args[2],$args[3],$args[4],$args[5]) -or
                -not $custody.IsCurrent -or $custody.GetType().GetField('primaryHashAttempted',$parentFlags).GetValue($custody)) {throw 'Foreign arguments consumed/retired original source'}
        }
    }
    if($prepareCase -ceq 'already-hashed') {$priorHash=$custody.TryHashPrimarySource()}
    if($prepareCase -ceq 'disposed-parent') {$request.Dispose()}
    if($prepareCase -ceq 'pin-failure') {$custodyPins[4].Dispose()}
    if($prepareCase -ceq 'budget') {
        $elapsed=@($clock.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($clock,[long]([Diagnostics.Stopwatch]::Frequency*6))
    }
    $sourcePreparation=$custody.TryPreparePrimarySource($attempt,$prepareOwner,'frontier-gui-run','frontier-gui-scenario',$source,$completion)
    if($prepareCase -ceq 'already-hashed') {
        if($null -ne $sourcePreparation -or -not $custody.IsCurrent -or $null -eq $priorHash -or -not $priorHash.Lease.IsVerified -or
            $priorHash.Lease.GetType().GetField('frontierCountAttempted',$parentFlags).GetValue($priorHash.Lease) -ne 0) {throw 'Preparation consumed an earlier caller hash'}
        $lines=$priorHash.Lease.CountFrontierSourceLines($source)
        $primary=[NLink.Tools.LocalRetainedSourceRequest]::TryCreateWithWriter($attempt,$prepareOwner,'frontier-gui-run','frontier-gui-scenario',$priorHash.Lease,$lines,$completion)
        $inputs=$custody.TryTransferToPrimary($primary)
        if($null -eq $inputs -or -not $inputs.IsCurrent) {throw 'Prior hash caller could not finish its original operation'}
        'frontier-primary-preparation='+$prepareOwner+'/'+$prepareCase; return
    }
    if($prepareCase -cin @('disposed-parent','pin-failure','budget')) {
        if($null -ne $sourcePreparation -or $custody.IsCurrent -or @($custodyPins | Where-Object {-not $_.IsClosed}).Count) {throw 'Failed preparation leaked custody'}
        'frontier-primary-preparation='+$prepareOwner+'/'+$prepareCase; return
    }
    if($null -eq $sourcePreparation -or -not $sourcePreparation.IsCurrent -or $sourcePreparation.Request.RequestId -ne $attempt -or
        $sourcePreparation.InputSha256 -cne $completion.Outputs[3].Sha256 -or $sourcePreparation.InputLineCount -ne $completion.Outputs[3].LineCount -or
        $sourcePreparation.NativeWriterCustodyEstablished -or $sourcePreparation.LiveProofEligible -or $sourcePreparation.PublicationCompletionIssued -or
        $custody.IsCurrent -or @($custodyPins | Where-Object {-not $_.IsClosed}).Count -or
        $sourcePreparation.GetType().GetConstructors().Length -or @($sourcePreparation.GetType().GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) {throw 'Prepared original source missing or promoted authority'}
    $primary=$sourcePreparation.Request; $inputs=$sourcePreparation.Inputs
    if($prepareCase.StartsWith('start-')) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryStart.ps1')
        return
    }
    if($prepareCase -ceq 'replay' -and ($null -ne $custody.TryPreparePrimarySource($attempt,$prepareOwner,'frontier-gui-run','frontier-gui-scenario',$source,$completion) -or
        -not $sourcePreparation.IsCurrent)) {throw 'Preparation replay acquired/retired successor'}
    if($prepareCase -ceq 'copied-result') {
        $copy=$parentClone.Invoke($sourcePreparation,$null)
        if($copy.IsCurrent -or $null -ne $copy.Request -or $null -ne $copy.Inputs -or $null -ne $copy.InputSha256 -or $copy.InputLineCount -ne -1) {throw 'Copied preparation exposed resources'}
        $copy.Dispose(); if(-not $sourcePreparation.IsCurrent) {throw 'Copied preparation retired original'}
    }
    if($prepareCase -ceq 'independent-lifetime') {$request.Dispose(); if(-not $sourcePreparation.IsCurrent) {throw 'Preparation depended on old GUI request'}}
    . (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
    $read=$primary.TryOpenTerminalSource($source)
    if($null -eq $read) {throw 'Prepared source unavailable for original receipt hash'}
    try {$afterHash=Get-FileTransferEvidenceSha256 -Path $source -OwnedSource $read} finally {$read.Dispose()}
    $read=$primary.TryOpenTerminalSource($source)
    if($null -eq $read) {throw 'Prepared source unavailable for original receipt count'}
    try {$afterLines=Get-FileTransferEvidenceLineCount -Path $source -OwnedSource $read} finally {$read.Dispose()}
    if($afterHash -cne $sourcePreparation.InputSha256 -or $afterLines -ne $sourcePreparation.InputLineCount -or -not $sourcePreparation.IsCurrent) {throw 'Prepared source differs from original source passes'}
    $resources=$inputs.GetType().GetField('pins',$parentFlags).GetValue($inputs)
    $primaryPins=@($resources.GetType().GetField('Handles',$parentFlags).GetValue($resources))
    foreach($pin in $primaryPins) {if(-not [ParentPinProbe]::WriteDenied($pin)) {throw 'Preparation retained write access'}}
    if([PrimaryPositionProbe]::Position($primaryPins[4]) -ne 29L) {throw 'Preparation moved shared native source position'}
    if($prepareCase -cin @('worker-reports','legacy-isolation')) {
        $primaryCase='worker-join-pipes-terminal-reports-persist-valid'
        $open=$inputs.GetType().GetMethod('TryOpenRead',$parentFlags)
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryWorkerParent.ps1')
    }
    if($null -ne $legacyLock) {
        $legacyLock.Position=0; $unchanged=[byte[]]::new($sentinel.Length)
        if($legacyLock.Length -ne $sentinel.Length -or $legacyLock.Read($unchanged,0,$unchanged.Length) -ne $sentinel.Length -or
            [Convert]::ToBase64String($unchanged) -cne [Convert]::ToBase64String($sentinel)) {throw 'Native primary changed legacy analyzer bytes'}
    }
    $sourcePreparation.Dispose()
    if($sourcePreparation.IsCurrent -or $null -ne $sourcePreparation.Request -or $null -ne $sourcePreparation.Inputs -or $sourcePreparation.InputLineCount -ne -1 -or
        $inputs.IsCurrent -or @($primaryPins | Where-Object {-not $_.IsClosed}).Count) {throw 'Preparation cleanup leaked resources'}
    'frontier-primary-preparation='+$prepareOwner+'/'+$prepareCase
} finally {
    if($null -ne $sourcePreparation) {$sourcePreparation.Dispose()}
    if($null -ne $priorHash) {$priorHash.Lease.Dispose()}
    if($null -ne $legacyLock) {$legacyLock.Dispose()}
}
