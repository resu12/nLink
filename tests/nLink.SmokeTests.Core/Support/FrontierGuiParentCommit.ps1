$commitCase=$ackCase.Substring('commit-'.Length)
$commitEvents=@{}
foreach($part in @('commit-start','commit-ready','commit-published','commit-reviewed')) {$commitEvents[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.'+$part)}
try {
    [void]$commitEvents['commit-start'].Set()
    if(-not $commitEvents['commit-ready'].WaitOne(20000)) {throw 'Parent commit preparation deadline'}
    if($binding.GetType().GetMethod('TryCompleteParentHandoff').GetParameters().Length) {throw 'Completion scalar import exposed'}
    if($commitCase -ceq 'copied-binding') {
        $foreign=$clone.Invoke($binding,$null)
        if($foreign.TryCompleteParentHandoff()) {throw 'Copied binding completed handoff'}
        $foreign.Dispose(); if(-not $binding.IsCurrent) {throw 'Copied binding retired original'}
    }
    if($commitCase -ceq 'gui-budget') {
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*6))
    }
    if($commitCase -ceq 'cancel-before') {$adopter.Dispose()}
    if($commitCase -ceq 'missing-commit') {$binding.Dispose(); $completed=$false}
    else {$completed=$binding.TryCompleteParentHandoff()}
    $expected=$commitCase -cnotin @('repeat-prepare','parent-budget','gui-budget','missing-prepare','cancel-before','failed-completion','missing-commit','commit-binding','commit-state')
    if($completed -ne $expected) {throw "GUI commit result mismatch: $commitCase completed=$completed elapsed=$($work.ElapsedMilliseconds)"}
    if($commitCase -ceq 'repeat-gui' -and $binding.TryCompleteParentHandoff()) {throw 'GUI completion replayed'}
    if($binding.IsCurrent -or $submission.IsCurrent) {throw 'Completion kept provisional GUI resources'}
    [void]$commitEvents['commit-published'].Set()
    if(-not $commitEvents['commit-reviewed'].WaitOne(20000)) {throw 'Parent commit review deadline'}
    if($commitCase -ceq 'gui-failure') {'frontier-gui-worker=committed-but-failed-native-exit'; [Environment]::Exit(7)}
} finally {foreach($event in $commitEvents.Values) {$event.Dispose()}}
