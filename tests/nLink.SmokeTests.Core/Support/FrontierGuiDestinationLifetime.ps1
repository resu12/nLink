# Normal native loader and the actual cycle/finally callers. Headless
# role apps supply only a synthetic destination packet, not a live route proof.
$script:destinationCase=$env:NLINK_TEST_GUI_DESTINATION_CASE
$script:signals['destination.send']=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$env:NLINK_GUI_WRITER_REQUEST+'.destination.send')
$script:signals['destination.done']=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$env:NLINK_GUI_WRITER_REQUEST+'.destination.done')
$script:signals['destination.stalled']=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$env:NLINK_GUI_WRITER_REQUEST+'.destination.stalled')
$script:destinationAttempt=$null
if($script:destinationCase.StartsWith('connected-')) {
    # Invoke the original public cleanup off the PowerShell thread so a broken
    # join can be observed and its exact pipe closed by the test watchdog.
    Add-Type -TypeDefinition @'
using System;
using System.Threading.Tasks;
public static class DestinationCleanupProbe
{
    public static Task<object> End(object attempt, string ending)
    {
        return Task.Run(() => {
            if (ending == "dispose") { ((IDisposable)attempt).Dispose(); return null; }
            return attempt.GetType().GetMethod("TryCompleteDestinationAdoption")
                .Invoke(attempt, new object[] { ending == "cancel" });
        });
    }
}
'@
}
$start=@($cycleAst.Body.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.Text.Contains('.TryStartDestinationAdoption(')})
if($start.Count -ne 1 -or $start[0].Extent.StartOffset -le $hash[0].Extent.EndOffset -or
    $start[0].Extent.EndOffset -ge $send[0].Extent.StartOffset -or
    -not $start[0].Extent.Text.Contains('Get-GuiSmokeScenarioRemainingTimeoutMs -DefaultTimeoutMs $TimeoutMs')) {
    throw 'Production destination start/deadline order changed'
}
$script:destinationStart=$start[0].Extent.Text
function Get-GuiSmokeScenarioRemainingTimeoutMs {
    param($DefaultTimeoutMs)
    if($DefaultTimeoutMs -ne 100000) {throw 'Scenario budget changed'}
    if($script:destinationCase -ceq 'deadline') {return 1}
    return $DefaultTimeoutMs
}
function Test-GuiDestinationLifetime {
    param($Attempt,$Receiver)
    Set-Variable -Name Attempt,Receiver -Option Private
    $script:destinationAttempt=$Attempt
    $private:task=$Attempt.GetType().GetField('destinationWaitTask',$flags).GetValue($Attempt)
    if($null -eq $task) {throw 'Production cycle did not arm destination listener'}
    $private:clone=[object].GetMethod('MemberwiseClone',$flags)
    if($Attempt.TryStartDestinationAdoption($Receiver,100000) -or
        $clone.Invoke($Attempt,$null).TryStartDestinationAdoption($Receiver,100000) -or
        $null -ne $clone.Invoke($Attempt,$null).TryCompleteDestinationAdoption($true)) {throw 'Repeated/copied listener admitted'}
    if($script:destinationCase.StartsWith('connected-')) {
        $private:pipe=$Attempt.GetType().GetField('destinationWaitPipe',$flags).GetValue($Attempt)
        $private:pipeHandle=$pipe.SafePipeHandle
        $private:cancellation=$Attempt.GetType().GetField('destinationWaitCancellation',$flags).GetValue($Attempt)
        $private:token=$cancellation.Token
        $private:ending=$null; $bounded=$false
        try {
            [void]$script:signals['destination.send'].Set()
            if(-not $script:signals['destination.stalled'].WaitOne(15000) -or
                -not $pipe.IsConnected -or $task.Wait(250)) {throw 'Original connected destination stall missing'}
            $mode=$script:destinationCase.Split('-')[2]
            # Shorten this test's original scenario timer only after the peer is
            # blocked. The production handoff deadline remains 75,000 ms.
            if($mode -ceq 'deadline') {$cancellation.CancelAfter(100)}
            $ending=[DestinationCleanupProbe]::End($Attempt,$mode)
            $bounded=$ending.Wait(5000)
        } finally {
            if(-not $bounded) {
                $pipe.Dispose()
                if($null -ne $ending) {[void]$ending.Wait(5000)}
                [void]$task.Wait(5000)
            }
            [ordered]@{Case=$script:destinationCase;Bounded=$bounded;ForcedCleanup=(-not $bounded)} |
                ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'destination-connected.json') -Encoding UTF8
        }
        if(-not $bounded) {throw 'Connected destination cleanup exceeded 5000 ms; exact-pipe watchdog required'}
        if($null -ne $ending.GetAwaiter().GetResult() -or -not $task.IsCompleted -or
            $null -ne $task.GetAwaiter().GetResult() -or -not $token.IsCancellationRequested -or
            -not $pipeHandle.IsClosed -or $null -ne $Attempt.GetType().GetField('destination',$flags).GetValue($Attempt)) {
            throw 'Connected cancellation retained custody or pipe'
        }
        if(-not $script:signals['destination.done'].WaitOne(5000)) {throw 'Peer did not observe server closure'}
        $path=Join-Path $Directory 'synthetic-destination.bin'
        if(Test-Path -LiteralPath $path) {
            $exclusive=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            $exclusive.Dispose()
        }
    } elseif($script:destinationCase -ceq 'dispose') {
        $Attempt.Dispose()
        if(-not $task.IsCompleted -or $null -ne $task.GetAwaiter().GetResult()) {throw 'Attempt disposal did not join listener'}
    } elseif($script:destinationCase -ceq 'cancel') {
        if($null -ne $Attempt.TryCompleteDestinationAdoption($true) -or -not $task.IsCompleted) {throw 'Cancellation did not join listener'}
    } elseif($script:destinationCase -ceq 'deadline') {
        if(-not $task.Wait(5000) -or $null -ne $task.GetAwaiter().GetResult()) {throw 'Scenario deadline did not end listener'}
    } else {
        if($script:destinationCase -ceq 'slow') {
            # Real elapsed wait catches a 75-second timer accidentally starting
            # at Send. It changes no production timeouts or clock sources.
            $wait=[Threading.ManualResetEvent]::new($false)
            try {[void]$wait.WaitOne(76000)} finally {$wait.Dispose()}
            if($task.IsCompleted) {throw 'Listener spent handoff budget before receiver readiness'}
        }
        [void]$script:signals['destination.send'].Set()
        if(-not $script:signals['destination.done'].WaitOne(15000)) {throw 'Synthetic receiver handoff deadline'}
    }
    if($script:destinationCase -cin @('valid','cancel','deadline','reject','dispose')) {
        $tokenMethod=[NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDestination].GetMethod('TerminalToken',[Reflection.BindingFlags]'Static,NonPublic')
        $session=$tokenMethod.Invoke($null,@('synthetic-session'))
        $transfer=$tokenMethod.Invoke($null,@('synthetic-transfer'))
        $deadline=[Threading.CancellationTokenSource]::new(5000)
        try {$measured=$Attempt.TryMeasureDestination($session,$transfer,$deadline.Token)} finally {$deadline.Dispose()}
        if(($null -ne $measured) -ne ($script:destinationCase -ceq 'valid')) {throw 'Armed destination measurement acceptance mismatch'}
        if($null -ne $measured -and ($measured.ByteCount -ne 268435456 -or
            $measured.Sha256 -cne $script:testCanonicalSource.Sha256)) {throw 'Armed destination measured wrong bytes'}
        'armed-terminal-measurement='+$script:destinationCase
    }
}
$writer=(Get-Item Function:Write-FileTransferGuiLogLedgerEvidence).Definition
$writer=Replace-AttemptSeam $writer '$script:reportCount=@($FrontierReports | Where-Object {$null -ne $_}).Count' @'
$private:task=$script:destinationAttempt.GetType().GetField('destinationWaitTask',$flags).GetValue($script:destinationAttempt)
if(-not $task.IsCompleted) {throw 'Production finally did not join before app shutdown'}
$private:destination=$task.GetAwaiter().GetResult()
$accepted=$script:destinationCase -cin @('valid','slow')
if(($null -ne $destination) -ne $accepted) {throw 'Destination acceptance mismatch'}
if($accepted -and (-not $destination.IsCurrent -or $destination.ByteCount -ne 268435456 -or
    $destination.LiveProofEligible -or $destination.TransferIntegrityEstablished -or $destination.PublicationCompletionIssued)) {
    throw 'Destination custody missing or promoted'
}
[ordered]@{Checked=$true;Case=$script:destinationCase;Joined=$task.IsCompleted;Accepted=$accepted} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'destination-lifetime.json') -Encoding UTF8
$script:reportCount=@($FrontierReports | Where-Object {$null -ne $_}).Count
'@
Invoke-Expression ('function Write-FileTransferGuiLogLedgerEvidence {'+$writer+'}')
