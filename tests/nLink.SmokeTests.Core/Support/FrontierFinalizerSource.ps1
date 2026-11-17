# Reuses real Core app-lifetime children from the cleanup/adoption fixture.
# Execute the original whole finalizer; stop at its legacy snapshot boundary
# after inspecting native preparation, and verify its outer finally cleanup.
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='cleanup-adoption-run'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='cleanup-adoption-scope'
foreach($name in @('Write-FileTransferGuiLogLedgerEvidence','Wait-GuiSmokeProcessOutputCaptureDrain')) {
    $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if($definition.Count -ne 1) {throw 'Original finalizer function ambiguous'}
    Invoke-Expression $definition[0].Extent.Text
}
$script:sourceOwner=$owner
$script:sourceAdoption=$adoption
$script:sourceBinding=$null; $script:sourceCapture=$null; $script:sourceMatch=$null
$script:sourceEvents=[Collections.Generic.List[string]]::new()
$script:sourceProbeFailure=''; $script:sourceProbed=$false
$script:GuiSmokeProcessOutputFiles=@($apps | ForEach-Object {
    Join-Path $Directory ($_.Id.ToString()+'.stdout.log')
    Join-Path $Directory ($_.Id.ToString()+'.stderr.log')
})
$script:GuiSmokeAppLogLedger=[pscustomobject]@{LedgerId='untrusted-legacy-ledger'}
$script:sourcePath=Join-Path $Directory 'live-operational-log/nlink.log'
function Assert-FinalizerPrivacy {
    foreach($name in @('owner','requests','FrontierOwner','FrontierReports','localWriterIssuer',
        'localFrontierBinding','localFrontierResult','localFrontierEvidence','frontierPrepared','selectedOwner','originalReports')) {
        if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) {throw "Finalizer capability leaked: $name"}
    }
}
function Get-GuiSmokeSemanticEvidenceEventNames {
    Assert-FinalizerPrivacy
    $script:sourceEvents.Add('events')
    if($FinalizerCase -ceq 'event-selection-failure') {throw 'fixture event selection failure'}
    return @('filetransfer_frontier_publication_receipt_closed')
}
function Update-FileTransferGuiEvidenceFinalizationStage {
    param($Stage)
    Assert-FinalizerPrivacy
    $script:sourceEvents.Add($Stage)
    if($Stage -ceq 'process_output_drained') {
        if($script:sourceOwner.GetType().GetField('fileSealAttempted',$flags).GetValue($script:sourceOwner)) {throw 'Native source prepared before output drain'}
        if($FinalizerCase -ceq 'partial-tail') {[IO.File]::AppendAllText($script:sourcePath,'unterminated',[Text.UTF8Encoding]::new($false))}
        if($FinalizerCase -ceq 'prefix-rewrite') {
            $write=[IO.File]::Open($script:sourcePath,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
            try {$write.WriteByte([byte][char]'X')} finally {$write.Dispose()}
        }
    }
}
function Sync-GuiSmokeAppLogLedger {
    param([switch]$IgnoreScenarioDeadline)
    Assert-FinalizerPrivacy
    if($script:sourceProbed) {throw 'Legacy sync retried'}
    $script:sourceProbed=$true; $script:sourceEvents.Add('legacy-sync')
    try {
        $reader=$script:sourceAdoption.GetType().GetField('sealedReader',$flags).GetValue($script:sourceAdoption)
        $expectPrepared=$FinalizerCase -cin @('writer-only','positive','cancel-after')
        if($expectPrepared) {
            if($null -eq $reader -or -not $reader.IsCurrent -or -not $reader.ReachedEnd) {
                $seal=$script:sourceOwner.GetType().GetField('fileSealAttempted',$flags).GetValue($script:sourceOwner)
                throw "Original source not prepared at legacy boundary: seal=$seal; reader=$($null -ne $reader); events=$($script:sourceEvents -join '|'); drain=$processOutputDrainFailure"
            }
            $script:sourceBinding=$reader.GetType().GetField('binding',$flags).GetValue($reader)
            $script:sourceMatch=$script:sourceBinding.CursorMatch
            $result=$script:sourceMatch.GetType().GetField('Result',$flags).GetValue($script:sourceMatch)
            $script:sourceCapture=$result.StreamCapture
            $finalizer=$script:sourceBinding.GetType().GetField('finalizer',$flags).GetValue($script:sourceBinding)
            if(-not $script:sourceMatch.IsCurrent -or -not $script:sourceMatch.MatchesResult($result) -or
                -not $script:sourceCapture.IsCurrent -or -not $script:sourceCapture.MatchesOriginalSource($reader) -or
                $script:sourceMatch.Final.Bookmark -ne $script:sourceMatch.Initial.Bookmark -or
                $script:sourceMatch.Initial.Bookmark -eq 77 -or $script:sourceMatch.Final.LineCount -le 0 -or
                $script:sourceMatch.Final.CommittedBytes -ne $reader.Length -or $reader.Position -ne $reader.Length -or
                $finalizer.GetType().GetField('endpointName',$flags).GetValue($finalizer) -cne $env:NLINK_GUI_WRITER_REQUEST) {throw 'Native cursor/source/finalizer join changed'}
            $work=$script:sourceOwner.GetType().GetField('frontierWork',$flags).GetValue($script:sourceOwner)
            if($work.IsRunning -or -not $script:sourceOwner.AllAppOutputsDrainedSuccessfully -or
                -not $script:sourceOwner.AllAppRequestsCompletedSuccessfully -or -not $script:sourceOwner.AllFileHandoffsAcknowledged -or
                $script:sourceBinding.LiveProofEligible -or $script:sourceBinding.NativeWriterCustodyEstablished -or
                $script:sourceBinding.PublicationCompletionIssued) {throw 'Preparation changed budget, completion or authority'}
            if($FinalizerCase -ceq 'cancel-after') {
                $script:sourceOwner.Dispose()
                if($script:sourceBinding.IsCurrent -or $script:sourceCapture.IsCurrent -or $script:sourceMatch.IsCurrent) {throw 'Cancelled preparation survived'}
            }
        } elseif($FinalizerCase -cin @('default','copied-owner')) {
            if($null -ne $reader -or -not $script:sourceOwner.IsCurrent -or
                $script:sourceOwner.GetType().GetField('fileSealAttempted',$flags).GetValue($script:sourceOwner)) {throw 'Default/copy consumed original native preparation'}
        } elseif($script:sourceOwner.IsCurrent) {throw 'Failed preparation retained optional owner'}
    } catch {$script:sourceProbeFailure=$_.Exception.Message; throw}
}
function Get-GuiSmokeLogLedgerSnapshot {
    param($State,$Bookmark)
    Assert-FinalizerPrivacy
    if($Bookmark -ne 77 -or $State.LedgerId -cne 'untrusted-legacy-ledger') {throw 'Ordinary scalar inputs changed'}
    throw 'fixture legacy snapshot failure'
}
$private:selectedOwner=$owner
if($FinalizerCase -ceq 'default') {$selectedOwner=$null}
if($FinalizerCase -ceq 'copied-owner') {$selectedOwner=[object].GetMethod('MemberwiseClone',$flags).Invoke($owner,$null)}
if($FinalizerCase -ceq 'missing-issuer') {Remove-Item Env:NLINK_GUI_WRITER_REQUEST -ErrorAction SilentlyContinue}
$private:originalReports=$reports
$failure=''
try {
    Write-FileTransferGuiLogLedgerEvidence -ArtifactDir $Directory -Bookmark 77 -InitialSnapshot ([pscustomobject]@{writers=@()}) `
        -FrontierOwner $selectedOwner -FrontierReports $originalReports -ProcessOutputDrainAction {
            param($FrontierOwner)
            Set-Variable -Name FrontierOwner -Option Private
            # In default/copy cases arrange genuine cleanup independently, to
            # isolate that finalizer selection never consumes the original seal.
            $private:cleanupOwner=if($FinalizerCase -cin @('default','copied-owner')) {$script:sourceOwner} else {$FrontierOwner}
            Cleanup-ProcessesWithOperationalLogDrain -Processes $targets -ExePath fixture-only -FrontierOwner $cleanupOwner
            if($FinalizerCase -ceq 'drain-failure') {throw 'fixture drain failure'}
            return $apps
        }
} catch {$failure=$_.Exception.Message}
if(-not $script:sourceProbed -or $script:sourceProbeFailure -or $failure -cne 'fixture legacy snapshot failure') {
    throw "Finalizer boundary failed: $failure; $script:sourceProbeFailure"
}
foreach($app in $apps) {if(-not $app.HasExited -or $app.ExitCode -ne 0) {throw 'Finalizer lost ordinary child cleanup'}}
foreach($report in $reports) {foreach($task in @($report.Stdout,$report.Stderr)) {if(-not $task.Wait(5000)) {throw 'Finalizer lost report EOF'}; [void]$task.GetAwaiter().GetResult()}}
if($null -ne $script:sourceBinding -and ($script:sourceBinding.IsCurrent -or $script:sourceCapture.IsCurrent -or $script:sourceMatch.IsCurrent)) {
    throw 'Finalizer exception retained native source preparation'
}
if($FinalizerCase -cne 'missing-issuer' -and $script:sourceOwner.IsCurrent) {throw 'Original finalizer disposal did not retire original owner'}
$order=$script:sourceEvents -join '|'
if($FinalizerCase -cin @('writer-only','positive','cancel-after') -and $order -cne 'process_cleanup_started|process_output_drained|events|legacy-sync') {throw "Native preparation order changed: $order"}
$expectEvidence=$FinalizerCase -cin @('writer-only','positive','cancel-after')
if((Test-Path -LiteralPath (Join-Path $Directory 'filetransfer-frontier-finalizer-evidence.json')) -ne $expectEvidence) {throw 'Native finalizer evidence presence changed'}
'frontier-finalizer-source='+$FinalizerCase
