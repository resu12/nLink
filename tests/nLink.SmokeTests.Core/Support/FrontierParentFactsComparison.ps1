param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$type=[NLink.Tools.LocalGuiWriterRequest].Assembly.GetType('NLink.Tools.LocalFrontierParentFactsChannel')
$create=$type.GetMethod('Create',[Reflection.BindingFlags]'Static,NonPublic')
$current=$type.GetProperty('IsCurrent',$flags)
$check=$type.GetMethod('CheckCurrent',$flags)
$viewField=$type.GetField('view',$flags)
$bodyField=$type.GetField('body',$flags)
# Codec-free transport comparison only. These bytes are never decoded as facts
# or passed to native writer acceptance, parent custody, or an analyzer.
foreach($length in @(1,7,8,9,1025,32767,32768,32769,262143,262144,262145,1048577,2097152,4194304)) {
    $body=[byte[]]::new($length)
    for($i=0;$i -lt $length;$i++) {$body[$i]=[byte](1+($i%251))}
    $channel=$create.Invoke($null,[object[]]@(('facts-comparison-'+[guid]::NewGuid().ToString('N')),[byte[]]::new(32),$body,[Action]{}))
    if($null -eq $channel) {throw 'Original comparison channel missing'}
    try {
        $view=$viewField.GetValue($channel)
        if(-not $current.GetValue($channel,$null)) {throw 'Untouched comparison rejected'}
        $boundaries=@(for($chunk=262144;$chunk -lt $length;$chunk+=262144) {$chunk-1;$chunk;$chunk+1})
        $offsets=if($length -le 1025) {@(0..($length-1))} else {@((@(0,6,7,8,32766,32767,32768,262142,262143,262144,($length-1))+$boundaries) | Where-Object {$_ -ge 0 -and $_ -lt $length} | Sort-Object -Unique)}
        foreach($offset in $offsets) {
            $value=$view.ReadByte(80+$offset)
            $view.Write(80+$offset,[byte]($value -bxor 255))
            if($current.GetValue($channel,$null)) {throw ('Mapped body corruption accepted: '+$length+'/'+$offset)}
            $view.Write(80+$offset,$value)
            if(-not $current.GetValue($channel,$null)) {throw 'Restored test mapping mismatch'}
        }
        # Compare against the actual retained body, not a second cached verdict.
        $retained=$bodyField.GetValue($channel); $retained[$length-1]=$retained[$length-1] -bxor 255
        if($current.GetValue($channel,$null)) {throw 'Retained body mutation hidden'}
        $retained[$length-1]=$retained[$length-1] -bxor 255
        foreach($offset in @(@($length,4194303) | Where-Object {$_ -ge $length -and $_ -lt 4194304} | Sort-Object -Unique)) {
            $view.Write(80+$offset,[byte]1)
            if($check.Invoke($channel,[object[]]@([Action]{},$true))) {throw ('Padding corruption accepted: '+$offset)}
            $view.Write(80+$offset,[byte]0)
        }
        if($check.Invoke($channel,[object[]]@([Action]{throw 'cancelled'},$true))) {throw 'Cancellation callback ignored'}
        if($check.Invoke($channel,[object[]]@([Action]{throw 'cancelled'},$false))) {throw 'Body comparison cancellation callback ignored'}
        if($length -gt 262144) {
            $script:comparisonChecks=0
            if($check.Invoke($channel,[object[]]@([Action]{$script:comparisonChecks++;if($script:comparisonChecks -eq 2) {throw 'mid-comparison-cancel'}},$false)) -or
                $script:comparisonChecks -ne 2) {throw 'Intermediate body comparison check missing'}
            if(-not $current.GetValue($channel,$null)) {throw 'Interrupted comparison damaged retained facts'}
            $script:comparisonChecks=0
            $prior=$view.ReadByte(80+262144)
            if($check.Invoke($channel,[object[]]@([Action]{
                $script:comparisonChecks++
                if($script:comparisonChecks -eq 2) {$view.Write(80+262144,[byte]($prior -bxor 255))}
            },$false))) {throw 'Mutation at next comparison chunk was hidden'}
            $view.Write(80+262144,$prior)
        }
        if(-not $check.Invoke($channel,[object[]]@([Action]{},$true))) {throw 'Full restored mapping rejected'}
        if($length -eq 1025) {
            foreach($offset in @(0..79 | Where-Object {$_ -lt 8 -or $_ -ge 12})) {
                $prior=$view.ReadByte($offset); $view.Write($offset,[byte]($prior -bxor 255))
                if($current.GetValue($channel,$null)) {throw ('Header mutation accepted: '+$offset)}
                $view.Write($offset,$prior)
            }
            $ready=$type.GetField('ready',$flags).GetValue($channel); $null=$ready.Reset()
            if($current.GetValue($channel,$null)) {throw 'Missing ready signal accepted'}
            $null=$ready.Set()
            if(-not $type.GetMethod('MarkReceived',$flags).Invoke($channel,$null)) {throw 'Original state transition rejected'}
            $view.Write(8,[int]1)
            if($current.GetValue($channel,$null)) {throw 'Regressed state accepted'}
            $view.Write(8,[int]3)
            if($current.GetValue($channel,$null)) {throw 'Future state accepted'}
            $view.Write(8,[int]2)
            if(-not $current.GetValue($channel,$null)) {throw 'Restored received state rejected'}
            $cancelled=$type.GetField('cancelled',$flags).GetValue($channel); $null=$cancelled.Set()
            if($current.GetValue($channel,$null)) {throw 'Cancelled mapping accepted'}
        }
        if($length -eq 4194304) {
            $script:comparisonChecks=0
            if($check.Invoke($channel,[object[]]@([Action]{
                $script:comparisonChecks++;if($script:comparisonChecks -eq 2) {$view.Dispose()}
            },$false))) {throw 'Mid-comparison disposed view accepted'}
        }
        $view.Dispose()
        if($current.GetValue($channel,$null)) {throw 'Closed facts mapping remained current'}
    } finally {$channel.Dispose()}
    if($current.GetValue($channel,$null)) {throw 'Disposed comparison channel current'}
}
'native-facts-comparison-ok'
