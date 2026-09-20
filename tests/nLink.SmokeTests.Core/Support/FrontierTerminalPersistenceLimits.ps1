param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$type=[NLink.Tools.LocalRetainedSourceRequest].Assembly.GetType('NLink.Tools.LocalFrontierEvidenceFile',$true)
$constructor=$type.GetConstructors($flags)[0]
$persist=$type.GetMethod('TryPersistDiagnostic',$flags)
foreach($case in @('above-finalizer-cap','exact-bound','over-bound','empty','cancel-during-write')) {
    $length=if($case -ceq 'above-finalizer-cap') {65537} elseif($case -ceq 'over-bound') {6MB+1} elseif($case -ceq 'empty') {0} else {6MB}
    $bytes=[byte[]]::new($length)
    $path=[IO.Path]::Combine($Directory,$case+'.json')
    $lifetime=[Threading.CancellationTokenSource]::new()
    $resource=$constructor.Invoke([object[]]@($lifetime.Token))
    $state=[pscustomobject]@{Calls=0}
    $current=[Func[bool]]{
        $state.Calls++
        if($case -ceq 'cancel-during-write' -and $state.Calls -eq 3) {$lifetime.Cancel()}
        return -not $lifetime.IsCancellationRequested
    }
    try {
        $success=$persist.Invoke($resource,[object[]]@($path,$bytes,$current))
        $expected=$case -cin @('above-finalizer-cap','exact-bound')
        if($success -ne $expected -or $type.GetProperty('IsCurrent',$flags).GetValue($resource,$null) -ne $expected) {throw ('Diagnostic file bound mismatch: '+$case)}
        if($expected) {
            $output=$type.GetProperty('Output',$flags).GetValue($resource,$null)
            if($output.ByteCount -ne $length) {throw 'Diagnostic file was truncated'}
            $lifetime.Cancel()
            if($type.GetProperty('IsCurrent',$flags).GetValue($resource,$null)) {throw 'Cancelled diagnostic file remained current'}
        } elseif([IO.File]::Exists($path)) {throw 'Rejected diagnostic write published destination'}
    } finally {$resource.Dispose(); $lifetime.Dispose()}
}
'frontier-terminal-persistence-limits=5'
