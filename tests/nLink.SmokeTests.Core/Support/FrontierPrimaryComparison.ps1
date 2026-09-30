param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$assembly=[NLink.Tools.LocalRetainedSourceRequest].Assembly
# Comparison-only construction: arbitrary transport bytes cannot decode into
# native facts, establish writer custody or grant evaluation authority.
$type=$assembly.GetType('NLink.Tools.LocalFrontierParentFileChannel')
$bytes=[int]$type.GetField('Bytes',[Reflection.BindingFlags]'Static,NonPublic').GetRawConstantValue()
$channel=[Activator]::CreateInstance($type,$true)
$map=[IO.MemoryMappedFiles.MemoryMappedFile]::CreateNew('primary-files-compare-'+[guid]::NewGuid().ToString('N'),$bytes)
$view=$map.CreateViewAccessor()
$snapshot=[byte[]]::new($bytes)
for($i=0;$i -lt $bytes;$i++) {$snapshot[$i]=[byte](1+($i%251))}
[Array]::Copy([BitConverter]::GetBytes([int]1),0,$snapshot,8,4)
$view.WriteArray(0,$snapshot,0,$bytes)
$ready=[Threading.EventWaitHandle]::new($true,[Threading.EventResetMode]::ManualReset)
$cancel=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset)
foreach($entry in @(@('map',$map),@('view',$view),@('snapshot',$snapshot),@('ready',$ready),@('cancelled',$cancel))) {
    $type.GetField($entry[0],$flags).SetValue($channel,$entry[1])
}
$current=$type.GetProperty('IsCurrent',$flags)
try {
    if(-not $current.GetValue($channel,$null)) {throw 'Original file comparison failed'}
    for($i=0;$i -lt $bytes;$i++) {
        if($i -ge 8 -and $i -lt 12) {continue}
        $prior=$view.ReadByte($i);$view.Write($i,[byte]($prior -bxor 1))
        if($current.GetValue($channel,$null)) {throw ('File byte mutation admitted: '+$i)}
        $view.Write($i,$prior)
        if(-not $current.GetValue($channel,$null)) {throw 'Restored file comparison failed'}
        $snapshot[$i]=$snapshot[$i] -bxor 1
        if($current.GetValue($channel,$null)) {throw ('Retained file byte mutation admitted: '+$i)}
        $snapshot[$i]=$snapshot[$i] -bxor 1
    }
    foreach($state in @(0,3)) {$view.Write(8,[int]$state);if($current.GetValue($channel,$null)) {throw 'Invalid file state admitted'}}
    $view.Write(8,[int]2);if(-not $current.GetValue($channel,$null)) {throw 'Valid pull state rejected'}
    $view.Write(8,[int]1);if($current.GetValue($channel,$null)) {throw 'Regressed file state admitted'}
    $view.Write(8,[int]2);[void]$cancel.Set()
    if($current.GetValue($channel,$null)) {throw 'Cancelled file channel current'}
} finally {$channel.Dispose()}
if($current.GetValue($channel,$null)) {throw 'Disposed file channel current'}

$type=$assembly.GetType('NLink.Tools.LocalFrontierPrimaryScope')
$current=$type.GetProperty('IsCurrent',$flags)
foreach($length in @(1,7,8,9,1025,32767,32768)) {
    $map=[IO.MemoryMappedFiles.MemoryMappedFile]::CreateNew('primary-scope-compare-'+[guid]::NewGuid().ToString('N'),64+$length)
    $view=$map.CreateViewAccessor();$body=[byte[]]::new($length);$header=[byte[]]::new(64)
    for($i=0;$i -lt $length;$i++) {$body[$i]=[byte](1+($i%251))}
    $view.WriteArray(0,$header,0,64);$view.WriteArray(64,$body,0,$length)
    $scope=$type.GetConstructors($flags)[0].Invoke([object[]]@($map,$body))
    try {
        $offsets=if($length -le 1025) {@(0..(63+$length))} else {@(0..63)+@(64,70,71,72,(64+$length-8),(63+$length))}
        foreach($i in $offsets) {
            $prior=$view.ReadByte($i);$view.Write($i,[byte]($prior -bxor 1))
            if($current.GetValue($scope,$null)) {throw ('Scope byte mutation admitted: '+$length+'/'+$i)}
            $view.Write($i,$prior)
            if(-not $current.GetValue($scope,$null)) {throw 'Restored scope comparison failed'}
        }
        foreach($field in @('header','body')) {
            $retained=$type.GetField($field,$flags).GetValue($scope)
            for($i=0;$i -lt [Math]::Min(1025,$retained.Length);$i++) {
                $retained[$i]=$retained[$i] -bxor 1
                if($current.GetValue($scope,$null)) {throw 'Retained scope mutation hidden'}
                $retained[$i]=$retained[$i] -bxor 1
            }
        }
    } finally {$scope.Dispose();$view.Dispose();$map.Dispose()}
    if($current.GetValue($scope,$null)) {throw 'Disposed scope current'}
}
'native-primary-comparisons-ok'
