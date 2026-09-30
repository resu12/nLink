$outputMode=$mode.Substring('output-'.Length)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'; $clone=[object].GetMethod('MemberwiseClone',$flags)
$preparation=$result.Batch.TryPrepareDiagnostic()
if($null -eq $preparation) {throw 'Output preparation unavailable'}
'frontier-diagnostic-original='+(@{Full=$preparation.FullInvocationId;Compact=$preparation.CompactInvocationId;
    Observation=$native.ObservationId;Witnesses=$result.Batch.GetWitnesses().Count;Snapshot=$preparation.DiagnosticSnapshot} | ConvertTo-Json -Compress)
if($RendezvousCase) {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticRendezvousWorker.ps1')
    return
}
if($outputMode -ceq 'copied-capabilities') {
    $copy=$clone.Invoke($preparation,$null)
    if($copy.TryOfferOutput() -or $copy.TryCommitOutput()) {throw 'Copied preparation offered/committed'}
    $copy.Dispose(); if(-not $preparation.IsCurrent) {throw 'Copied preparation retired original output'}
}
if($outputMode -ceq 'worker-budget') {
    $owned=$worker.FrontierInputs; $watch=$owned.GetType().GetField('work',$flags).GetValue($owned)
    $elapsed=@($watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
    $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*([NLink.Tools.LocalPublicationInputLimits]::EvaluationMilliseconds+1000)/1000))
}
$offered=$outputMode -cne 'no-offer' -and $preparation.TryOfferOutput()
if($offered -eq ($outputMode -cin @('missing-receiver','worker-budget','repeat-prepare','no-offer'))) {throw ('Output offer mismatch: '+$outputMode)}
if($outputMode -ceq 'repeat-offer' -and $preparation.TryOfferOutput()) {throw 'Output offer replayed'}
if($script:evaluations -ne 2 -or (@($fullView,$compactView,$proof) | ConvertTo-Json -Depth 70 -Compress) -cne $before) {throw 'Output offer changed evaluations/proof'}
[void]$signals.attached.Set(); if(-not $signals.release.WaitOne(30000)) {throw 'Output adoption deadline'}
if($outputMode -ceq 'mutation-before-commit') {$original.Recovered=$true}
$before=@($fullView,$compactView,$proof) | ConvertTo-Json -Depth 70 -Compress
$commitExpected=$outputMode -cin @('valid','copied-capabilities','repeat-take','repeat-commit','secret-tamper','worker-failure','cancel-after-take','copy-budget','pin-after-take')
if($outputMode -cnotin @('no-commit','state-only')) {
    $committed=$preparation.TryCommitOutput()
    if($committed -ne $commitExpected) {throw ('Output commit mismatch: '+$outputMode)}
    if($outputMode -ceq 'repeat-commit' -and $preparation.TryCommitOutput()) {throw 'Output commit replayed'}
}
if($script:evaluations -ne 2 -or (@($fullView,$compactView,$proof) | ConvertTo-Json -Depth 70 -Compress) -cne $before) {throw 'Output commit changed evaluations/proof'}
'frontier-primary-worker='+$Case
'frontier-diagnostic-output-worker='+$outputMode
if($outputMode -cin @('worker-failure','state-only')) {
    # Bypass normal finally only to test OS process-exit cleanup. The state-only
    # case leaves a forged state=4 without the private commitment token.
    [Console]::Out.Flush(); [Environment]::Exit($(if($outputMode -ceq 'worker-failure') {7} else {0}))
}
