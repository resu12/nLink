param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$type=[NLink.Tools.LocalGuiWriterRequest].Assembly.GetType('NLink.Tools.LocalFrontierFileChannel')
$create=$type.GetMethod('Create',[Reflection.BindingFlags]'Static,NonPublic')
$current=$type.GetProperty('IsCurrent',$flags)
# Transport comparison only: these bytes never become decoded manifests or
# establish native writer custody. Exercise the actual normal-loader assembly.
foreach($length in @(1,7,8,9,1025,32767,32768)) {
    $channel=$create.Invoke($null,[object[]]@(('file-comparison-'+[guid]::NewGuid().ToString('N')),
        [guid]::NewGuid(),[guid]::NewGuid(),0,1,[long]1,2,[long]2))
    if($null -eq $channel) {throw 'Original comparison channel missing'}
    try {
        $body=[byte[]]::new($length)
        for($i=0;$i -lt $length;$i++) {$body[$i]=[byte](1+($i%251))}
        if(-not $type.GetMethod('RememberSubmission',$flags).Invoke($channel,[object[]]@(,$body))) {throw 'Submission missing'}
        $type.GetMethod('PublishSubmission',$flags).Invoke($channel,$null)
        $type.GetMethod('PublishAdopted',$flags).Invoke($channel,$null)
        $view=$type.GetField('View',$flags).GetValue($channel); $view.Write(8,[int]5)
        if(-not $current.GetValue($channel,$null)) {throw 'Untouched channel rejected'}
        $offsets=if($length -le 1025) {@(0..(191+$length))} else {@(0..191)+@(192,198,199,200,(192+$length-8),(192+$length-1))}
        foreach($offset in $offsets) {
            $prior=$view.ReadByte($offset); $view.Write($offset,[byte]($prior -bxor 1))
            if($current.GetValue($channel,$null)) {throw ('Mapped corruption accepted: '+$length+'/'+$offset)}
            $view.Write($offset,$prior)
            if(-not $current.GetValue($channel,$null)) {throw 'Restored test mapping mismatch'}
        }
        foreach($field in @('body','digest')) {
            $retained=$type.GetField($field,$flags).GetValue($channel)
            foreach($offset in @(0,($retained.Length-1))) {
                $retained[$offset]=$retained[$offset] -bxor 1
                if($current.GetValue($channel,$null)) {throw ('Retained packet mutation hidden: '+$field)}
                $retained[$offset]=$retained[$offset] -bxor 1
                if(-not $current.GetValue($channel,$null)) {throw 'Restored retained packet rejected'}
            }
        }
        if($length -eq 32767) {$view.Dispose()} else {$type.GetMethod('Cancel',$flags).Invoke($channel,$null)}
        if($current.GetValue($channel,$null)) {throw 'Revoked channel current'}
    } finally {$channel.Dispose()}
    if($current.GetValue($channel,$null)) {throw 'Disposed channel current'}
}
'native-file-channel-comparison-ok'
