# Codec controls use synthetic descriptor scalars, not native custody claims.
$packetType=[NLink.Tools.LocalGuiWriterRequest].Assembly.GetType('NLink.Tools.LocalFrontierParentFilePacket')
$decode=$packetType.GetMethod('Decode',[Reflection.BindingFlags]'Static,NonPublic')
$encode=$packetType.GetMethod('Encode',[Reflection.BindingFlags]'Instance,NonPublic')
$packetBytes=[byte[]]::new($fileView.ReadInt32(64L)); [void]$fileView.ReadArray(128L,$packetBytes,0,$packetBytes.Length)
function Set-PacketNumber([byte[]]$target,[int]$offset,$value) {
    $bytes=[BitConverter]::GetBytes($value); [Array]::Copy($bytes,0,$target,$offset,$bytes.Length)
}
function Assert-Packet([byte[]]$bytes,[bool]$accepted,[string]$label) {
    $value=$decode.Invoke($null,[object[]]@(,$bytes))
    if(($null -ne $value) -ne $accepted) {throw "Packet codec mismatch: $label"}
    if($accepted) {
        $encoded=$encode.Invoke($value,$null)
        if([Convert]::ToBase64String($encoded) -cne [Convert]::ToBase64String($bytes)) {throw "Packet roundtrip mismatch: $label"}
    }
}
Assert-Packet $packetBytes $true 'original'
$maximum=[byte[]]::new(2248)
[Array]::Copy($packetBytes,0,$maximum,0,584)
Set-PacketNumber $maximum 8 ([int]42)
for($i=0;$i -lt 32;$i++) {
    $offset=584+52*$i
    [Array]::Copy($packetBytes,584,$maximum,$offset,52)
    Set-PacketNumber $maximum ($offset+4) ([int][Math]::Floor($i/16))
    [Array]::Copy([guid]::NewGuid().ToByteArray(),0,$maximum,($offset+8),16)
    Set-PacketNumber $maximum ($offset+24) ([long](400000+$i))
    Set-PacketNumber $maximum ($offset+32) ([uint32]4294967295)
    Set-PacketNumber $maximum ($offset+36) ([uint32]4294967295)
    Set-PacketNumber $maximum ($offset+40) ([uint32]$i)
}
Assert-Packet $maximum $true '42-exact'
$long=[byte[]]::new(2300); [Array]::Copy($maximum,$long,$maximum.Length); Set-PacketNumber $long 8 ([int]43)
Assert-Packet $long $false '43-overflow'
foreach($fault in @('count','same-writers','empty-gui','role','slot','17-writer','handle','duplicate-handle','duplicate-file','duplicate-observation','mandatory-observation','zero-source','source-over','output-over','pipe-over','evidence-over','payload-over','empty-allowed','reserved','version','truncated')) {
    $bytes=[byte[]]$maximum.Clone()
    switch($fault) {
        'count' {Set-PacketNumber $bytes 8 ([int]::MaxValue)}
        'same-writers' {[Array]::Copy($bytes,32,$bytes,48,16)}
        'empty-gui' {[Array]::Clear($bytes,16,16)}
        'role' {Set-PacketNumber $bytes 64 ([int]1)}
        'slot' {Set-PacketNumber $bytes 588 ([int]-1)}
        '17-writer' {Set-PacketNumber $bytes (588+16*52) ([int]0)}
        'handle' {Set-PacketNumber $bytes 88 ([long]-1)}
        'duplicate-handle' {[Array]::Copy($bytes,88,$bytes,140,8)}
        'duplicate-file' {[Array]::Copy($bytes,96,$bytes,148,12)}
        'duplicate-observation' {[Array]::Copy($bytes,592,$bytes,644,16)}
        'mandatory-observation' {$bytes[72]=1}
        'zero-source' {Set-PacketNumber $bytes 108 ([long]0)}
        'source-over' {Set-PacketNumber $bytes 108 ([long]268435457)}
        'output-over' {Set-PacketNumber $bytes 160 ([long]268959745)}
        'pipe-over' {Set-PacketNumber $bytes 420 ([long]67108865)}
        'evidence-over' {Set-PacketNumber $bytes 368 ([long]65537)}
        'payload-over' {Set-PacketNumber $bytes 628 ([long]262145)}
        'empty-allowed' {Set-PacketNumber $bytes 212 ([long]0)}
        'reserved' {$bytes[12]=1}
        'version' {Set-PacketNumber $bytes 4 ([int]2)}
        'truncated' {$bytes=[byte[]]$bytes[0..2246]}
    }
    Assert-Packet $bytes ($fault -ceq 'empty-allowed') $fault
}
if(-not $pending.IsCurrent) {throw 'Codec fixtures affected original pending set'}
'frontier-parent-packet=24'
