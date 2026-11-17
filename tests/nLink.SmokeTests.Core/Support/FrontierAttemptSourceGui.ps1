# Extend only the explicit writer fixture. No live dispatch or Send is enabled.
$projectionCheck=(Get-Item Function:Invoke-FileTransferTunaHandoffFallbackCycle).Definition
Invoke-Expression ('function Assert-SourceCycleProjection {'+$projectionCheck+'}')
Invoke-Expression (Get-AttemptFunction $guiAst 'Write-DeterministicFileTransferPayload')
$cycleAst=@($guiAst.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $n.Name -ceq 'Invoke-FileTransferTunaHandoffFallbackCycle'},$true))[0]
$creation=@($cycleAst.Body.EndBlock.Statements | Where-Object {
    $_.Extent.Text -ceq 'Write-DeterministicFileTransferPayload -Path $AutopickPath -SizeBytes $PayloadSizeBytes -Seed $Seed -CycleIndex 0'})
$hash=@($cycleAst.Body.EndBlock.Statements | Where-Object {$_.Extent.Text -ceq '$expectedHash = Get-FileTransferTunaSourceHash -Path $AutopickPath -CanonicalAttempt $CanonicalAttempt -SenderRequest $SenderRequest'})
$send=@($cycleAst.Body.EndBlock.Statements | Where-Object {$_.Extent.Text -ceq 'Click-Element $sendButton'})
if($creation.Count -ne 1 -or $hash.Count -ne 1 -or $send.Count -ne 1 -or
    $creation[0].Extent.EndOffset -ge $hash[0].Extent.StartOffset -or $hash[0].Extent.EndOffset -ge $send[0].Extent.StartOffset) {
    throw 'Original payload/pre-Send hash seam changed'
}
$script:sourceCreation=$creation[0].Extent.Text
$script:sourceHash=$hash[0].Extent.Text.Replace('Get-FileTransferTunaSourceHash -Path $AutopickPath -CanonicalAttempt $CanonicalAttempt -SenderRequest $SenderRequest','$Source.Value.Sha256')
function Assert-SourceProtected {
    param([string]$Path)
    $opened=$null; $denied=$false
    try {$opened=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)}
    catch [IO.IOException] {$denied=$true}
    finally {if($null -ne $opened) {$opened.Dispose()}}
    if(-not $denied) {throw 'Held source allowed a writer'}
    $denied=$false
    try {[IO.File]::Move($Path,$Path+'.moved')}
    catch [IO.IOException] {$denied=$true}
    if(-not $denied) {[IO.File]::Move($Path+'.moved',$Path); throw 'Held source allowed rename/replacement'}
}
function Invoke-FileTransferTunaHandoffFallbackCycle {
    param($Context,$ArtifactDir,$AutopickPath,$PayloadSizeBytes,$Direction,$Seed,$TimeoutMs,$StartupTimeoutMs,
        $ProgressTimeoutMs,$PayerMode,$FaultMode,$RouteMode,$WalletPassword,$Attempt,$SenderRequest,$ReceiverRequest,[ref]$Source)
    Set-Variable -Name Attempt,SenderRequest,ReceiverRequest,Source -Option Private
    Assert-SourceCycleProjection -PayloadSizeBytes $PayloadSizeBytes -Direction $Direction -PayerMode $PayerMode `
        -FaultMode $FaultMode -RouteMode $RouteMode -TimeoutMs $TimeoutMs -ProgressTimeoutMs $ProgressTimeoutMs
    if([IO.Path]::GetFullPath($AutopickPath) -cne [IO.Path]::GetFullPath((Join-Path $Directory 'filetransfer-tuna-gui-payload.bin'))) {
        throw 'Source creation escaped fixture'
    }
    $clone=[object].GetMethod('MemberwiseClone',$flags)
    if($null -ne $Attempt.TryHoldSourceForTest($ReceiverRequest,$AutopickPath) -or
        $null -ne $Attempt.TryHoldSourceForTest($clone.Invoke($SenderRequest,$null),$AutopickPath) -or
        $null -ne $clone.Invoke($Attempt,$null).TryHoldSourceForTest($SenderRequest,$AutopickPath)) {throw 'Foreign source request admitted'}
    # Wrong-length acquisition is rejected before hashing and spends only the
    # original source attempt. Its fixture does not create a large payload.
    if($attemptCase -ceq 'writer-source-size') {$PayloadSizeBytes=65536}
    Invoke-Expression $script:sourceCreation
    $writer=$null
    try {
        if($attemptCase -ceq 'writer-source-open-writer') {
            $writer=[IO.File]::Open($AutopickPath,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
        }
        $Source.Value=$Attempt.TryHoldSourceForTest($SenderRequest,$AutopickPath)
    } finally {if($null -ne $writer) {$writer.Dispose()}}
    if($attemptCase -cin @('writer-source-size','writer-source-open-writer')) {
        if($null -ne $Source.Value -or $null -ne $Attempt.TryHoldSourceForTest($SenderRequest,$AutopickPath)) {
            throw 'Rejected source acquisition retried or retained a handle'
        }
        $exclusive=[IO.File]::Open($AutopickPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $exclusive.Dispose()
        return
    }
    if($null -eq $Source.Value -or -not $Source.Value.IsCurrent -or $Source.Value.ByteCount -ne 268435456 -or
        $null -ne $Attempt.TryHoldSourceForTest($SenderRequest,$AutopickPath) -or $Source.Value.TryBindClosedWriters($null)) {
        throw 'Original pre-Send source hold missing/repeated'
    }
    Invoke-Expression $script:sourceHash
    if($expectedHash -notmatch '^[0-9a-f]{64}$' -or [string]::IsNullOrWhiteSpace($Source.Value.FileIdentity)) {throw 'Held source measurement missing'}
    $copied=$clone.Invoke($Source.Value,$null)
    if($copied.IsCurrent -or $null -ne $copied.Sha256) {throw 'Copied source current'}
    $copied.Dispose()
    if(-not $Source.Value.IsCurrent) {throw 'Copied source disposal retired original'}
    Assert-SourceProtected $AutopickPath
    # Observation-only output, never read to reconstruct source custody.
    [IO.File]::WriteAllText((Join-Path $Directory 'source-measurement.txt'),
        ($Source.Value.ByteCount.ToString()+"`n"+$Source.Value.Sha256+"`n"+$Source.Value.FileIdentity))
    if($attemptCase.StartsWith('writer-source-read-')) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptSenderReadGui.ps1')
    }
}
$writerScenario=Replace-AttemptSeam $writerScenario '$private:frontierHelpeeRequest = $null' `
    ('$private:attemptSource = $null'+"`n"+'$private:frontierHelpeeRequest = $null')
$writerScenario=Replace-AttemptSeam $writerScenario '-WalletPassword $walletPassword' `
    '-WalletPassword $walletPassword -Attempt $attemptTicket -Source ([ref]$attemptSource)'
$writerScenario=Replace-AttemptSeam $writerScenario 'finally {if($null -ne $FrontierOwner) {$FrontierOwner.Dispose()}}' `
    'finally {if($null -ne $attemptSource) {$attemptSource.Dispose()}; if($null -ne $FrontierOwner) {$FrontierOwner.Dispose()}}'
