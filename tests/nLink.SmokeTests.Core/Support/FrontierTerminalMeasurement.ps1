param($Attempt,$Destination,[string]$ExpectedHash,[string]$Path,[string]$Fault)
Set-Variable -Name Attempt,Destination -Option Private
$ast=Read-AttemptAst (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1')
$field=@($ast.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -ceq 'Get-GuiSmokeFieldValue'})
Invoke-Expression $field[0].Extent.Text
Invoke-Expression (Get-AttemptFunction $ast 'Get-FileTransferTunaTerminalIntegrity')
function Get-GuiSmokeScenarioRemainingTimeoutMs {param($DefaultTimeoutMs) return 10000}
function Get-FileSha256Hex {throw 'Native measurement reopened a pathname'}
function Get-TerminalScopeToken([string]$Value) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try {return 'fte1-'+([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))).Replace('-','').ToLowerInvariant().Substring(0,24))}
    finally {$sha.Dispose()}
}
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$session=Get-TerminalScopeToken $Destination.GetType().GetField('session',$flags).GetValue($Destination)
$transfer=Get-TerminalScopeToken $Destination.GetType().GetField('transfer',$flags).GetValue($Destination)
$inbound=@{state='Completed';error_code='(none)';terminal_session_id=$session;terminal_transfer_id=$transfer}
$outbound=$inbound.Clone()
$cycle=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $n.Name -ceq 'Invoke-FileTransferTunaHandoffFallbackCycle'},$true))[0]
$call=@($cycle.Body.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -ceq '$terminalIntegrity'})
if($call.Count -ne 1 -or -not $call[0].Extent.Text.Contains('-CanonicalAttempt $CanonicalAttempt')) {throw 'Real terminal consumer missing'}
$private:CanonicalAttempt=$Attempt; $resolvedSavedPath=$Path; $PayloadSizeBytes=268435456L; $TimeoutMs=10000
# Exercise the actual production call, not a duplicate test hashing implementation.
Invoke-Expression $call[0].Extent.Text
if(-not $terminalIntegrity.Completed -or -not $terminalIntegrity.IntegrityOk -or
    $terminalIntegrity.ActualHash -cne $ExpectedHash -or $terminalIntegrity.SavedSize -ne 268435456) {
    throw ('Held terminal measurement failed: '+$terminalIntegrity.MeasurementError)
}
$opened=$null; $denied=$false
try {$opened=[IO.File]::OpenRead($Path)} catch [IO.IOException] {$denied=$true}
finally {if($null -ne $opened) {$opened.Dispose()}}
if(-not $denied -or -not $Destination.IsCurrent -or $Destination.LiveProofEligible -or
    $Destination.TransferIntegrityEstablished -or $Destination.PublicationCompletionIssued) {throw 'Measurement weakened custody or granted authority'}
$none=[Threading.CancellationToken]::None
$copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($Attempt,$null)
if($null -ne $copy.TryMeasureDestination($session,$transfer,$none) -or
    $null -ne $Attempt.TryMeasureDestination('fte1-000000000000000000000000',$transfer,$none) -or
    $null -ne $Attempt.TryMeasureDestination($session,'fte1-000000000000000000000000',$none) -or
    $null -ne $Attempt.TryMeasureDestination($session,$transfer,[Threading.CancellationToken]::new($true))) {throw 'Foreign or cancelled measurement succeeded'}
$outbound.terminal_transfer_id='foreign'
Invoke-Expression $call[0].Extent.Text
if(-not $terminalIntegrity.Completed -or $terminalIntegrity.IntegrityOk -or
    $terminalIntegrity.MeasurementError -cne 'terminal_destination_scope_mismatch') {throw 'Mismatched terminal pair measured'}
$outbound.terminal_transfer_id=$transfer
# Cached/advertised digest is not the byte measurement.
$hashField=$Destination.GetType().GetField('hash',$flags); $cached=$hashField.GetValue($Destination)
try {
    $hashField.SetValue($Destination,[Convert]::ToBase64String((New-Object byte[] 32)))
    Invoke-Expression $call[0].Extent.Text
    if(-not $terminalIntegrity.IntegrityOk -or $terminalIntegrity.ActualHash -cne $ExpectedHash) {throw 'Measurement trusted cached digest'}
} finally {$hashField.SetValue($Destination,$cached)}
$hook=$Destination.GetType().GetField('MeasurementChunkForTest',$flags)
$cancel=[Threading.CancellationTokenSource]::new()
try {
    $hook.SetValue($Destination,[Delegate]::CreateDelegate([Action],$cancel,'Cancel'))
    if($null -ne $Attempt.TryMeasureDestination($session,$transfer,$cancel.Token) -or
        -not $cancel.IsCancellationRequested -or -not $Destination.IsCurrent) {throw 'Mid-read cancellation passed or retired custody'}
} finally {$cancel.Dispose();$hook.SetValue($Destination,$null)}
if($Fault -ceq 'dispose') {
    $hook.SetValue($Destination,[Delegate]::CreateDelegate([Action],$Destination,'Dispose'))
    Invoke-Expression $call[0].Extent.Text
    if($Destination.IsCurrent -or $terminalIntegrity.IntegrityOk -or -not $terminalIntegrity.Completed -or
        $terminalIntegrity.MeasurementError -cne 'terminal_destination_measurement_unavailable') {throw 'Mid-read disposal passed or erased terminal'}
} elseif($Fault -ceq 'wrong-file') {
    # Same bytes, distinct native identity. Keep the original hold until cleanup.
    $other=Join-Path $Directory 'equal-byte-foreign-destination.bin'
    [IO.File]::Copy((Join-Path $Directory 'filetransfer-tuna-gui-payload.bin'),$other)
    $foreign=[IO.File]::OpenRead($other)
    $fileField=$Destination.GetType().GetField('file',$flags); $original=$fileField.GetValue($Destination)
    try {
        $fileField.SetValue($Destination,$foreign.SafeFileHandle)
        Invoke-Expression $call[0].Extent.Text
        if($terminalIntegrity.IntegrityOk -or -not $terminalIntegrity.Completed -or
            $terminalIntegrity.MeasurementError -cne 'terminal_destination_measurement_unavailable') {throw 'Equal-byte foreign file passed'}
    } finally {$original.Dispose();$foreign.Dispose()}
} elseif($Fault -cne 'valid') {throw 'Unknown measurement fault'}
'terminal-held-measurement='+$Fault
