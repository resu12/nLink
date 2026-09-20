$rv=$RendezvousCase; $owned=$worker.FrontierInputs
if($rv -ceq 'copied') {
    $copy=$clone.Invoke($preparation,$null)
    if($copy.TryCommitOutputWhenAdopted()) {throw 'Copied preparation consumed diagnostic wait'}
    $copy.Dispose(); if(-not $preparation.IsCurrent) {throw 'Copied preparation retired original'}
}
if($rv -ceq 'parent-first' -and -not $signals.begin.WaitOne(30000)) {throw 'Parent-first fixture deadline'}
$noOffer=$rv -cin @('forged-offer','request-cancel','offer-timeout','worker-exit')
$offered=-not $noOffer -and $preparation.TryOfferOutput()
if($offered -ne (-not $noOffer -and -not $rv.StartsWith('collision-'))) {throw ('Rendezvous offer mismatch: '+$rv)}
[void]$signals.attached.Set()
if($rv -cin @('forged-offer','request-cancel','offer-timeout','cancel-before-commit','mutation-before-commit','worker-budget')) {
    if(-not $signals.release.WaitOne(30000)) {throw 'Rendezvous fault-injection release deadline'}
}
if($rv -ceq 'mutation-before-commit') {$original.Recovered=$true}
if($rv -ceq 'worker-budget') {
    $watch=$owned.GetType().GetField('work',$flags).GetValue($owned)
    $elapsed=@($watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
    $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*21))
}
$stateBefore=@($fullView,$compactView,$proof) | ConvertTo-Json -Depth 70 -Compress
$waitClock=[Diagnostics.Stopwatch]::StartNew()
$committed=$offered -and $preparation.TryCommitOutputWhenAdopted()
$waitClock.Stop()
$expected=$rv -cin @('valid','parent-first','offer-first','copied','repeat-wait')
if($committed -ne $expected) {throw ('Rendezvous commit mismatch: '+$rv)}
if($rv -ceq 'repeat-wait' -and ($preparation.TryCommitOutputWhenAdopted() -or -not $preparation.IsCurrent)) {throw 'Commit wait replay acquired/revoked original'}
$unchanged=(@($fullView,$compactView,$proof) | ConvertTo-Json -Depth 70 -Compress) -ceq $stateBefore
if($script:evaluations -ne 2 -or -not $unchanged) {throw 'Rendezvous changed evaluation count/proof'}
$row=@{Offered=$offered;Committed=$committed;Evaluations=$script:evaluations;Unchanged=$unchanged;Milliseconds=$waitClock.ElapsedMilliseconds}
if($rv -ceq 'adoption-timeout' -and ($waitClock.ElapsedMilliseconds -lt 19000 -or $waitClock.ElapsedMilliseconds -gt 30000)) {throw 'Adoption wait deadline changed'}
if($rv -ceq 'parent-exit') {
    $path=Join-Path $TerminalDirectory 'diagnostic-after-parent-exit.json'
    [IO.File]::WriteAllText($path+'.tmp',($row | ConvertTo-Json -Compress)); [IO.File]::Move($path+'.tmp',$path)
    [Environment]::Exit(0)
}
'frontier-rendezvous-worker='+($row | ConvertTo-Json -Compress)
'frontier-primary-worker='+$Case
