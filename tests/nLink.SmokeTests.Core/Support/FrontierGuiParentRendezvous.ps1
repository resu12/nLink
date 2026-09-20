$rendezvousCase=$mode.Substring('rendezvous-'.Length)
$wall=[Diagnostics.Stopwatch]::StartNew()
$rendezvousClock=$binding.GetType().GetField('parentRendezvousClock',$flags).GetValue($binding)
$work=$owner.GetType().GetField('frontierWork',$flags).GetValue($owner)
if($rendezvousCase -ceq 'copied') {
    $copy=$clone.Invoke($binding,$null)
    if($copy.TryPrepareParentHandoffWhenReady()) {throw 'Copied binding scheduled handoff'}
    $copy.Dispose()
    if(-not $binding.IsCurrent -or $rendezvousClock.IsRunning -or $rendezvousClock.ElapsedTicks) {throw 'Copied binding consumed original rendezvous'}
}
if($rendezvousCase -ceq 'deadline-before') {
    $elapsed=@($rendezvousClock.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
    $elapsed.SetValue($rendezvousClock,[long]([Diagnostics.Stopwatch]::Frequency*21))
}
if($rendezvousCase -ceq 'repeat-waiting') {
    Add-Type -TypeDefinition @'
using System.Threading.Tasks;
public static class FrontierRendezvousRace {
    public static Task<bool> Prepare(object binding) {
        return Task.Run(() => (bool)binding.GetType().GetMethod("TryPrepareParentHandoffWhenReady").Invoke(binding, null));
    }
}
'@
    $offered=[Threading.EventWaitHandle]::OpenExisting($Name+'.frontier-parent-v1.rendezvous-FilesOffered')
    try {
        $pendingCall=[FrontierRendezvousRace]::Prepare($binding)
        if(-not $offered.WaitOne(20000)) {throw 'Concurrent GUI fixture never offered files'}
        $cancelClock=[Diagnostics.Stopwatch]::StartNew()
        if($binding.TryPrepareParentHandoffWhenReady() -or -not $pendingCall.Wait(2000) -or $pendingCall.Result -or
            $cancelClock.ElapsedMilliseconds -gt 2000) {throw 'Repeated GUI call failed to cancel lock-free wait promptly'}
        $prepared=$false
    } finally {$offered.Dispose()}
} else {$prepared=$binding.TryPrepareParentHandoffWhenReady()}
$expected=$rendezvousCase -cin @('valid','copied','delayed-parent','repeat-gui','gui-failure','early-commit','gui-abandon')
if($prepared -ne $expected) {throw "Wrong rendezvous preparation: $rendezvousCase prepared=$prepared"}
if($rendezvousClock.IsRunning -or $work.IsRunning) {throw 'Rendezvous retained running work clock'}
if($rendezvousCase -cin @('missing-parent','late-pull') -and
    ($wall.ElapsedMilliseconds -lt 19500 -or $wall.ElapsedMilliseconds -gt 25000 -or $work.ElapsedMilliseconds -ge 5000)) {throw 'Idle wait renewed deadline or consumed active work'}
if($prepared) {
    if(-not $binding.IsCurrent -or -not $evidence.IsCurrent) {throw 'Scheduling completed/retired original finalizer'}
    if($rendezvousCase -ceq 'gui-abandon') {
        'frontier-gui-worker=rendezvous-native-abandon'
        [Environment]::Exit(0)
    }
    if($rendezvousCase -ceq 'repeat-gui' -and $binding.TryPrepareParentHandoffWhenReady()) {throw 'GUI rendezvous repeated'}
    $completed=$binding.TryCompleteParentHandoff()
    if($completed -ne ($rendezvousCase -cnotin @('repeat-gui','early-commit'))) {throw 'Scheduling bypassed original completion checks'}
    if($binding.TryCompleteParentHandoff()) {throw 'Completion replay accepted'}
}
if($binding.IsCurrent -or $proof.IsCurrent -or $evidence.IsCurrent) {throw 'Rendezvous left original GUI resources current'}
if($rendezvousCase -ceq 'legacy-fallback') {
    if(-not $finalizer.Complete('binding-legacy',[IO.Path]::Combine($Directory,'legacy-only.log'),3,0,
        [string[]]@(('a'*64),('b'*64),('c'*64)),
        [string[]]@('12345678:1234567812345678','12345678:1234567812345679','12345678:1234567812345680'),
        [long[]]@(12,12,4),[long[]]@(3,3,1),[string[]]@('writer'),[long[]]@(3))) {throw 'Optional rendezvous failure consumed ordinary completion'}
}
if($rendezvousCase -ceq 'gui-failure') {
    'frontier-gui-worker=rendezvous-native-failure'
    [Environment]::Exit(7)
}
