param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$type=[NLink.Tools.LocalGuiWriterRequest].Assembly.GetType('NLink.Tools.LocalPayloadParentChannel',$true)
$static=[Reflection.BindingFlags]'Static,NonPublic'; $instance=[Reflection.BindingFlags]'Instance,NonPublic'
$create=$type.GetMethod('Create',$static); $open=$type.GetMethod('Open',$static)
$current=$type.GetProperty('Current',$instance); $pull=$type.GetMethod('MarkPulled',$instance); $commit=$type.GetMethod('Commit',$instance)
$binding=[byte[]]::new(32); $binding[0]=9; $packet=[byte[]]::new(256); $packet[127]=17
if($null -ne $create.Invoke($null,@('invalid-payload-parent',$binding,[byte[]]::new(8193)))) {throw 'Oversized payload control packet accepted'}
foreach($fault in @('valid','binding','reserved','body','tail','regression','cancel')) {
    $endpoint='Local\nlink-payload-parent-test-'+[guid]::NewGuid().ToString('N')
    $sender=$null; $receiver=$null
    try {
        $sender=$create.Invoke($null,@($endpoint,$binding,$packet))
        if($null -eq $sender -or -not $current.GetValue($sender,$null) -or $commit.Invoke($sender,$null)) {throw 'Offer/early commit changed'}
        if($null -ne $create.Invoke($null,@($endpoint,$binding,$packet)) -or -not $current.GetValue($sender,$null)) {throw 'Collision replaced or canceled original offer'}
        $candidate=[byte[]]$binding.Clone(); if($fault -ceq 'binding') {$candidate[0]++}
        if($fault -cin @('reserved','body','tail')) {
            $view=$type.GetField('view',$instance).GetValue($sender)
            $offset=switch($fault) {'reserved'{80} 'body'{96} 'tail'{352}}
            $view.Write([long]$offset,[byte]1)
        }
        $receiver=$open.Invoke($null,@($endpoint,$candidate))
        if($fault -cin @('binding','reserved','body','tail')) {
            if($null -ne $receiver) {throw ('Invalid packet admitted: '+$fault)}
            continue
        }
        if($null -eq $receiver -or -not $pull.Invoke($receiver,$null) -or $pull.Invoke($receiver,$null) -or
            $null -ne $open.Invoke($null,@($endpoint,$binding)) -or -not $commit.Invoke($sender,$null) -or $commit.Invoke($sender,$null)) {throw 'Ack/commit one-shot changed'}
        if(-not $current.GetValue($receiver,$null)) {throw 'Committed packet lost'}
        if($fault -ceq 'regression') {
            $type.GetField('view',$instance).GetValue($sender).Write([long]8,[int]2)
            if($current.GetValue($receiver,$null)) {throw 'State regression accepted'}
        } elseif($fault -ceq 'cancel') {
            $sender.Dispose()
            if($current.GetValue($receiver,$null)) {throw 'Canceled packet remained current'}
        } else {
            $type.GetMethod('Release',$instance).Invoke($sender,$null)
            if(-not $current.GetValue($receiver,$null)) {throw 'Committed sender release canceled parent'}
        }
    } finally {if($null -ne $receiver){$receiver.Dispose()};if($null -ne $sender){$sender.Dispose()}}
}
'payload-parent-channel-ok'
