# Internal codec controls are not original authority or native custody.
$codec=[NLink.Tools.LocalGuiWriterRequest].Assembly.GetType('NLink.Tools.LocalFrontierParentFacts')
$decode=$codec.GetMethod('Decode',[Reflection.BindingFlags]'Static,NonPublic')
$body=[byte[]]::new($factView.ReadInt32(12L)); [void]$factView.ReadArray(80L,$body,0,$body.Length)
$native=[byte[]]::new($fileView.ReadInt32(64L)); [void]$fileView.ReadArray(128L,$native,0,$native.Length)
$reader=[IO.BinaryReader]::new([IO.MemoryStream]::new($body,$false))
$positions=@{}
function Skip-FactText([string]$label) {
    $positions[$label]=[int]$reader.BaseStream.Position
    $length=$reader.ReadInt32(); $reader.BaseStream.Position+=$length
}
function Set-FactNumber([byte[]]$target,[int]$offset,$value) {
    $encoded=[BitConverter]::GetBytes($value); [Array]::Copy($encoded,0,$target,$offset,$encoded.Length)
}
function Test-FactBytes([byte[]]$candidate,[string]$label,[bool]$accept=$false,[byte[]]$expected=$native) {
    $value=$decode.Invoke($null,[object[]]@($candidate,$expected,'frontier-gui-run','frontier-gui-scenario',[Action]{}))
    if(($null -ne $value) -ne $accept) {throw "Fact codec mismatch: $label"}
}
try {
    $reader.BaseStream.Position=8; $size=$reader.ReadInt32(); $reader.BaseStream.Position+=$size
    foreach($key in @('run','scenario','source-hash')) {Skip-FactText $key}
    $positions['source-bytes']=[int]$reader.BaseStream.Position; $reader.BaseStream.Position+=8
    $positions['source-lines']=[int]$reader.BaseStream.Position; $reader.BaseStream.Position+=8
    $positions['bookmark']=[int]$reader.BaseStream.Position; $reader.BaseStream.Position+=8
    $positions['range-count']=[int]$reader.BaseStream.Position; $reader.BaseStream.Position+=4
    for($i=0;$i -lt 2;$i++) {
        Skip-FactText "ledger-$i"; Skip-FactText "generation-$i"
        $positions["observation-$i"]=[int]$reader.BaseStream.Position; $reader.BaseStream.Position+=36
    }
    $positions['index-count']=[int]$reader.BaseStream.Position; $count=$reader.ReadInt32()
    $positions['index-first']=[int]$reader.BaseStream.Position; $reader.BaseStream.Position+=8*$count
    for($i=0;$i -lt 5;$i++) {
        foreach($key in @('path','identity','hash')) {Skip-FactText "$key-$i"}
        $positions["output-bytes-$i"]=[int]$reader.BaseStream.Position; $reader.BaseStream.Position+=16
    }
    for($i=0;$i -lt 3;$i++) {
        $positions["origin-count-$i"]=[int]$reader.BaseStream.Position; $count=$reader.ReadInt32()
        $positions["origin-first-$i"]=[int]$reader.BaseStream.Position; $reader.BaseStream.Position+=4*$count
    }
    $positions['visits']=[int]$reader.BaseStream.Position; $reader.BaseStream.Position+=64
    $count=$reader.ReadInt32()
    for($i=0;$i -lt $count;$i++) {
        foreach($key in @('writer-id','writer-run','writer-scenario')) {Skip-FactText "$key-$i"}
        $positions["writer-numbers-$i"]=[int]$reader.BaseStream.Position; $reader.BaseStream.Position+=36
    }
    $count=$reader.ReadInt32()
    for($i=0;$i -lt $count;$i++) {Skip-FactText "prefix-id-$i"; Skip-FactText "prefix-run-$i"; Skip-FactText "prefix-scenario-$i"; $reader.BaseStream.Position+=24}
    $count=$reader.ReadInt32()
    for($i=0;$i -lt $count;$i++) {Skip-FactText "batch-id-$i"; Skip-FactText "batch-hash-$i"; $reader.BaseStream.Position+=60}
    for($i=0;$i -lt 2;$i++) {$size=$reader.ReadInt32(); $positions["manifest-$i"]=[int]$reader.BaseStream.Position; $reader.BaseStream.Position+=$size}
    $positions['recovery-count']=[int]$reader.BaseStream.Position+12; $reader.BaseStream.Position+=12; $markers=$reader.ReadInt32()
    if($markers -gt 0) {
        foreach($key in @('event','id','run','scenario','hash','failure','key')) {Skip-FactText "marker-$key"}
        $positions['marker-numbers']=[int]$reader.BaseStream.Position
    }
} finally {$reader.Dispose()}
Test-FactBytes $body 'original' $true
$faults=@('version','native-body','text-length','utf8','scope','source-bytes','source-lines','bookmark','range-count','observation','index-count','index-first',
    'identity','copy-hash','output-bytes','origin-count','origin-first','visits','counter','writer-drain','manifest-slot','recovery-count','truncated','extra','over-bound','wrong-native')
if($markers -gt 0) {$faults+=@('marker-stream','marker-ticks','marker-event')}
foreach($fault in $faults) {
    $candidate=[byte[]]$body.Clone(); $expected=$native
    switch($fault) {
        'version' {Set-FactNumber $candidate 4 ([int]2)}
        'native-body' {$candidate[12+16]=$candidate[12+16] -bxor 1}
        'text-length' {Set-FactNumber $candidate $positions['run'] ([int]::MaxValue)}
        'utf8' {$candidate[$positions['run']+4]=255}
        'scope' {$candidate[$positions['run']+4]=$candidate[$positions['run']+4] -bxor 1}
        'source-bytes' {Set-FactNumber $candidate $positions['source-bytes'] ([long]1)}
        'source-lines' {Set-FactNumber $candidate $positions['source-lines'] ([long]262145)}
        'bookmark' {Set-FactNumber $candidate $positions['bookmark'] ([long]-1)}
        'range-count' {Set-FactNumber $candidate $positions['range-count'] ([int]66)}
        'observation' {Set-FactNumber $candidate $positions['observation-1'] ([int]0)}
        'index-count' {Set-FactNumber $candidate $positions['index-count'] ([int]::MaxValue)}
        'index-first' {Set-FactNumber $candidate $positions['index-first'] ([long]1)}
        'identity' {$candidate[$positions['identity-0']+4]=$candidate[$positions['identity-0']+4] -bxor 1}
        'copy-hash' {$candidate[$positions['hash-3']+4]=if($candidate[$positions['hash-3']+4] -eq 48) {49} else {48}}
        'output-bytes' {Set-FactNumber $candidate $positions['output-bytes-0'] ([long]1)}
        'origin-count' {Set-FactNumber $candidate $positions['origin-count-0'] ([int]262145)}
        'origin-first' {Set-FactNumber $candidate $positions['origin-first-0'] ([int]0)}
        'visits' {Set-FactNumber $candidate $positions['visits'] ([long]0)}
        'counter' {Set-FactNumber $candidate ($positions['visits']+8) ([long]1)}
        'writer-drain' {Set-FactNumber $candidate ($positions['writer-numbers-0']+24) ([int]2)}
        'manifest-slot' {$candidate[$positions['manifest-0']+32]=$candidate[$positions['manifest-0']+32] -bxor 1}
        'recovery-count' {Set-FactNumber $candidate $positions['recovery-count'] ([int]129)}
        'truncated' {$candidate=[byte[]]$body[0..($body.Length-2)]}
        'extra' {$candidate=[byte[]]::new($body.Length+1); [Array]::Copy($body,$candidate,$body.Length)}
        'over-bound' {$candidate=[byte[]]::new(4194305)}
        'wrong-native' {$expected=[byte[]]$native.Clone(); $expected[16]=$expected[16] -bxor 1}
        'marker-stream' {Set-FactNumber $candidate ($positions['marker-numbers']+48) ([int]3)}
        'marker-ticks' {Set-FactNumber $candidate ($positions['marker-numbers']+32) ([long]1)}
        'marker-event' {$candidate[$positions['marker-event']+4]=120}
    }
    Test-FactBytes $candidate $fault $false $expected
}
if($markers -eq 0) { & { . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierOriginCapacityCodec.ps1') } }
if(-not $facts.IsCurrent -or -not $pending.IsCurrent) {throw 'Codec controls retired original receipt'}
'frontier-parent-facts-codec='+($faults.Count+1)
