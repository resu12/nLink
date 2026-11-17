# Exercise the production pre-Send source seam with original headless role apps.
# No UI click, network transfer, destination adoption or route verdict is run.
$sourceCase=$env:NLINK_TEST_CANONICAL_SOURCE_CASE
foreach($name in @('Get-FileTransferTunaSourceHash','Write-DeterministicFileTransferPayload')) {
    Invoke-Expression (Get-AttemptFunction $guiAst $name)
}
$originalHash=Get-AttemptFunction $guiAst 'Get-FileSha256Hex'
Invoke-Expression ($originalHash.Replace('function Get-FileSha256Hex {','function Get-OriginalSourceHash {'))
$cycleAst=@($guiAst.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $n.Name -ceq 'Invoke-FileTransferTunaHandoffFallbackCycle'},$true))[0]
$creation=@($cycleAst.Body.EndBlock.Statements | Where-Object {
    $_.Extent.Text -ceq 'Write-DeterministicFileTransferPayload -Path $AutopickPath -SizeBytes $PayloadSizeBytes -Seed $Seed -CycleIndex 0'})
$hash=@($cycleAst.Body.EndBlock.Statements | Where-Object {
    $_.Extent.Text -ceq '$expectedHash = Get-FileTransferTunaSourceHash -Path $AutopickPath -CanonicalAttempt $CanonicalAttempt -SenderRequest $SenderRequest'})
$send=@($cycleAst.Body.EndBlock.Statements | Where-Object {$_.Extent.Text -ceq 'Click-Element $sendButton'})
if($creation.Count -ne 1 -or $hash.Count -ne 1 -or $send.Count -ne 1 -or
    $creation[0].Extent.EndOffset -ge $hash[0].Extent.StartOffset -or $hash[0].Extent.EndOffset -ge $send[0].Extent.StartOffset -or
    -not $cycleAst.Extent.Text.Contains('Set-Variable -Name CanonicalAttempt,SenderRequest,ReceiverRequest -Option Private')) {
    throw 'Original protected source/pre-Send order changed'
}
$script:sourceCreation=$creation[0].Extent.Text
$script:sourceHash=$hash[0].Extent.Text
$script:sourceSend=$send[0].Extent.Text
$script:originalHashCalls=0; $script:sourceSendCount=0
$script:sourceWriter=$null; $script:testCanonicalSource=$null
function Get-FileSha256Hex {
    param($Path)
    foreach($name in @('CanonicalAttempt','SenderRequest','ReceiverRequest','source')) {
        $variable=Get-Variable $name -ErrorAction SilentlyContinue
        if($null -ne $variable -and ($variable.Value -is [NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDispatch] -or
            $variable.Value -is [NLink.Tools.LocalGuiWriterRequest+FrontierAppRequest] -or
            $variable.Value -is [NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptSource])) {throw 'Hash fallback received native capability'}
    }
    $script:originalHashCalls++
    # Release the injected competing writer after acquisition has refused it.
    if($null -ne $script:sourceWriter) {$script:sourceWriter.Dispose(); $script:sourceWriter=$null}
    return Get-OriginalSourceHash -Path $Path
}
function Assert-CanonicalSourceProtected {
    param($Path)
    $opened=$null; $denied=$false
    try {$opened=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)}
    catch [IO.IOException] {$denied=$true}
    finally {if($null -ne $opened) {$opened.Dispose()}}
    if(-not $denied) {throw 'Held source allowed write'}
    $denied=$false
    try {[IO.File]::Move($Path,$Path+'.moved')}
    catch [IO.IOException] {$denied=$true}
    if(-not $denied) {[IO.File]::Move($Path+'.moved',$Path); throw 'Held source allowed replacement'}
}
function Click-Element {
    param($Element)
    Assert-PrivateScenarioInputs
    if($Element -cne 'headless-send') {throw 'Unexpected Send boundary'}
    $script:sourceSendCount++
}
function Invoke-FileTransferTunaHandoffFallbackCycle {
    param($Context,$ArtifactDir,$AutopickPath,$PayloadSizeBytes,$Direction,$Seed,$TimeoutMs,$StartupTimeoutMs,
        $ProgressTimeoutMs,$PayerMode,$FaultMode,$RouteMode,$WalletPassword,$CanonicalAttempt,$SenderRequest,$ReceiverRequest)
    Set-Variable -Name CanonicalAttempt,SenderRequest,ReceiverRequest -Option Private
    Assert-PrivateScenarioInputs
    if($PayloadSizeBytes -ne 268435456 -or $Direction -cne 'helpee-to-helper' -or $PayerMode -cne 'helpee' -or
        $FaultMode -cne 'switch-off' -or $RouteMode -cne 'live-regular-activation-cycle' -or
        $TimeoutMs -ne 100000 -or $ProgressTimeoutMs -ne 30000) {throw 'Source consumed another projection'}
    $private:originalAttempt=$CanonicalAttempt
    $private:clone=[object].GetMethod('MemberwiseClone',$flags)
    $private:receiver=$script:canonicalRows[1][1]
    if($null -ne $CanonicalAttempt.TryHoldSource($receiver,$AutopickPath) -or
        $null -ne $CanonicalAttempt.TryHoldSource($clone.Invoke($SenderRequest,$null),$AutopickPath) -or
        $null -ne $clone.Invoke($CanonicalAttempt,$null).TryHoldSource($SenderRequest,$AutopickPath) -or
        $null -ne $CanonicalAttempt.TryHoldSource($null,$AutopickPath)) {throw 'Foreign source admitted'}
    if($sourceCase -cin @('size','default')) {$PayloadSizeBytes=65536}
    Invoke-Expression $script:sourceCreation
    if($sourceCase -ceq 'default') {$CanonicalAttempt=$null}
    try {
        if($sourceCase -ceq 'open-writer') {
            $script:sourceWriter=[IO.File]::Open($AutopickPath,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
        }
        Invoke-Expression $script:sourceHash
        if($env:NLINK_TEST_GUI_DESTINATION_CASE) {Invoke-Expression $script:destinationStart}
    } finally {if($null -ne $script:sourceWriter) {$script:sourceWriter.Dispose(); $script:sourceWriter=$null}}
    $script:testCanonicalSource=$originalAttempt.GetType().GetField('source',$flags).GetValue($originalAttempt)
    $private:heldSource=$script:testCanonicalSource
    $refused=$sourceCase -cin @('size','open-writer','default')
    if($script:originalHashCalls -ne $(if($refused) {1} else {0}) -or $expectedHash -notmatch '^[0-9a-f]{64}$') {
        throw 'Pre-Send hashing was duplicated or omitted'
    }
    if($refused) {
        if($null -ne $heldSource) {throw 'Refused/default source retained custody'}
        if($sourceCase -cne 'default' -and $null -ne $originalAttempt.TryHoldSource($SenderRequest,$AutopickPath)) {
            throw 'Refused acquisition retried'
        }
    } else {
        if($null -eq $heldSource -or -not $heldSource.IsCurrent -or $heldSource.ByteCount -ne 268435456 -or
            $heldSource.Sha256 -cne $expectedHash -or [string]::IsNullOrWhiteSpace($heldSource.FileIdentity) -or
            $heldSource.LiveProofEligible -or $heldSource.TransferIntegrityEstablished -or $heldSource.PublicationCompletionIssued -or
            $null -ne $originalAttempt.TryHoldSource($SenderRequest,$AutopickPath)) {throw 'Original source custody missing or repeated'}
        $private:copy=$clone.Invoke($heldSource,$null)
        if($copy.IsCurrent -or $null -ne $copy.Sha256 -or $copy.ByteCount -ne 0) {throw 'Copied source current'}
        $copy.Dispose()
        $clone.Invoke($originalAttempt,$null).Dispose()
        if(-not $heldSource.IsCurrent) {throw 'Copied disposal retired source'}
        Assert-CanonicalSourceProtected $AutopickPath
        if($sourceCase -ceq 'cancel') {
            $private:closed=$originalAttempt.GetType().GetField('originClosed',$flags).GetValue($originalAttempt)
            [void]$closed.Set()
            if($heldSource.IsCurrent) {throw 'Cancelled source current'}
            [void]$closed.Reset()
        } elseif($sourceCase -ceq 'closed') {
            $private:lease=$heldSource.GetType().GetField('Held',$flags).GetValue($heldSource)
            $lease.GetType().GetField('anchor',$flags).GetValue($lease).Dispose()
        } elseif($sourceCase -ceq 'dispose') {$originalAttempt.Dispose()}
        if($sourceCase -cne 'valid' -and ($heldSource.IsCurrent -or $null -ne $heldSource.Sha256 -or
            $null -ne $heldSource.FileIdentity -or $heldSource.ByteCount -ne 0 -or
            $null -ne $originalAttempt.TryHoldSource($SenderRequest,$AutopickPath))) {throw 'Retired source revived'}
    }
    $sendButton='headless-send'
    Invoke-Expression $script:sourceSend
    $script:cycleCalled=$true
    [ordered]@{Checked=$true;Case=$sourceCase;Hash=$expectedHash;SendCount=$script:sourceSendCount;
        OriginalHashCalls=$script:originalHashCalls;CustodyAcquired=(-not $refused)} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'canonical-source.json') -Encoding UTF8
    if($env:NLINK_TEST_GUI_DESTINATION_CASE) {Test-GuiDestinationLifetime -Attempt $originalAttempt -Receiver $ReceiverRequest}
}
function Complete-CanonicalSourceChecks {
    param($Attempt)
    Set-Variable -Name Attempt -Option Private
    $Attempt.Dispose()
    # Check release before any IsCurrent query could lazily retire the source.
    $path=Join-Path $Directory 'filetransfer-tuna-gui-payload.bin'
    $exclusive=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $exclusive.Dispose()
    $private:heldSource=$script:testCanonicalSource
    if($null -ne $heldSource -and ($heldSource.IsCurrent -or $null -ne $heldSource.Sha256)) {throw 'Final cleanup retained source'}
}
