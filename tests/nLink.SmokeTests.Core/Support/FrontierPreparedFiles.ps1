# Transport/resource controls only. These reconstructed helper objects never
# become original finalizer capabilities or alter the enclosing GUI preparation.
$pinType=$pins.GetType()
$pinConstructor=$pinType.GetConstructors($flags)[0]
$pinAdd=$pinType.GetMethod('Add',$flags); $pinSeal=$pinType.GetMethod('Seal',$flags)
$pinCurrent=$pinType.GetProperty('IsCurrent',$flags)
$pinEntries=$pinType.GetField('entries',$flags)
function Add-PreparedFixture($Target,$Entry,[int]$Role=-1,[long]$Bytes=-1,[int]$Slot=-2,$Payload=$null) {
    $t=$Entry.GetType()
    $roleValue=$t.GetField('Role',$flags).GetValue($Entry)
    $slotValue=$t.GetField('Slot',$flags).GetValue($Entry)
    $payloadValue=$t.GetField('Payload',$flags).GetValue($Entry)
    $info=$t.GetField('Identity',$flags).GetValue($Entry)
    $length=([long]$info.SizeHigh -shl 32) -bor [long]$info.SizeLow
    if($Role -ge 0) {$roleValue=$Role}; if($Bytes -ge 0) {$length=$Bytes}; if($Slot -ge -1) {$slotValue=$Slot}; if($null -ne $Payload) {$payloadValue=$Payload}
    return $pinAdd.Invoke($Target,[object[]]@([int]$roleValue,$t.GetField('Handle',$flags).GetValue($Entry),[long]$length,[int]$slotValue,$payloadValue))
}
$primitiveCases=@('valid','copied','missing-role','missing-payload','foreign-payload','swapped-writers','wrong-slot','duplicate-role','duplicate-file',
    'length','over-bound','pre-cancel','mid-cancel','closed-pin','repeat-seal','after-seal')
$primitiveChecks=0
foreach($primitiveCase in $primitiveCases) {
    $lifetime=[Threading.CancellationTokenSource]::new()
    if($primitiveCase -ceq 'pre-cancel') {$lifetime.Cancel()}
    $candidate=$pinConstructor.Invoke([object[]]@($lifetime.Token))
    $foreign=$null
    try {
        if($primitiveCase -ceq 'copied') {
            $foreign=$clone.Invoke($candidate,$null)
            if((Add-PreparedFixture $foreign $entries[0]) -or $pinSeal.Invoke($foreign,[object[]]@($writers[0],$writers[1])) -or $pinCurrent.GetValue($foreign,$null)) {throw 'Copied pins acquired original state'}
            $foreign.Dispose()
        }
        $added=0; $expectedFailure=$false
        foreach($entry in $entries) {
            $role=$entry.GetType().GetField('Role',$flags).GetValue($entry)
            if($primitiveCase -ceq 'missing-role' -and $role -eq 9) {continue}
            if($primitiveCase -ceq 'missing-payload' -and $role -eq 10) {continue}
            $ok=$false
            if($primitiveCase -ceq 'pre-cancel') {$expectedFailure=$true; $ok=Add-PreparedFixture $candidate $entry}
            elseif($primitiveCase -ceq 'mid-cancel' -and $added -eq 2) {$lifetime.Cancel(); $expectedFailure=$true; $ok=Add-PreparedFixture $candidate $entry}
            elseif($primitiveCase -ceq 'duplicate-role' -and $role -eq 1) {$expectedFailure=$true; $ok=Add-PreparedFixture $candidate $entry -Role 0}
            elseif($primitiveCase -ceq 'duplicate-file' -and $role -eq 1) {$expectedFailure=$true; $ok=Add-PreparedFixture $candidate $entries[0] -Role 1}
            elseif($primitiveCase -ceq 'length' -and $added -eq 2) {$expectedFailure=$true; $ok=Add-PreparedFixture $candidate $entry -Bytes 0}
            elseif($primitiveCase -ceq 'over-bound' -and $added -eq 2) {$expectedFailure=$true; $ok=Add-PreparedFixture $candidate $entry -Bytes 268959745}
            elseif($primitiveCase -ceq 'foreign-payload' -and $role -eq 10) {
                $payload=$entry.GetType().GetField('Payload',$flags).GetValue($entry)
                $ok=Add-PreparedFixture $candidate $entry -Payload ($clone.Invoke($payload,$null))
            } elseif($primitiveCase -ceq 'wrong-slot' -and $role -eq 10) {
                $slot=$entry.GetType().GetField('Slot',$flags).GetValue($entry)
                $ok=Add-PreparedFixture $candidate $entry -Slot (1-$slot)
            } else {$ok=Add-PreparedFixture $candidate $entry}
            if($expectedFailure) {if($ok) {throw ('Invalid pin accepted: '+$primitiveCase)}; break}
            if(-not $ok) {throw ('Original pin refused: '+$primitiveCase)}
            $added++
        }
        $candidateEntries=@($pinEntries.GetValue($candidate))
        $candidateHandles=@($candidateEntries | ForEach-Object {$_.GetType().GetField('Handle',$flags).GetValue($_)})
        if(-not $expectedFailure) {
            $a=$writers[0]; $b=$writers[1]
            if($primitiveCase -ceq 'swapped-writers') {$a=$writers[1]; $b=$writers[0]}
            $sealed=$pinSeal.Invoke($candidate,[object[]]@($a,$b))
            $shouldSeal=$primitiveCase -cnotin @('missing-role','missing-payload','foreign-payload','swapped-writers','wrong-slot')
            if($sealed -ne $shouldSeal) {throw ('Pin completeness result wrong: '+$primitiveCase)}
            if($shouldSeal) {
                if(-not $pinCurrent.GetValue($candidate,$null)) {throw 'Sealed pins not current'}
                foreach($handle in $candidateHandles) {if(-not [FrontierPreparationProbe]::WriteDenied($handle)) {throw 'Auxiliary pin writable'}}
                if($primitiveCase -ceq 'closed-pin') {$candidateHandles[0].Dispose()}
                elseif($primitiveCase -ceq 'repeat-seal') {if($pinSeal.Invoke($candidate,[object[]]@($a,$b))) {throw 'Pin set sealed twice'}}
                elseif($primitiveCase -ceq 'after-seal') {if((Add-PreparedFixture $candidate $entries[0])) {throw 'Sealed pins grew'}}
                else {$candidate.Dispose()}
            }
        }
        if($pinCurrent.GetValue($candidate,$null) -or @($candidateHandles | Where-Object {-not $_.IsClosed}).Count) {throw ('Partial pins leaked: '+$primitiveCase)}
        $primitiveChecks++
    } finally {$candidate.Dispose(); $lifetime.Dispose()}
}
# Exact capacity uses actual unique native files and synthetic transport facts.
# It does not claim 32 genuine producer observations or finalizer approval.
$manifestType=$writers[0].GetType()
$payloadType=$manifestType.GetNestedType('Payload',[Reflection.BindingFlags]::NonPublic)
$fileType=$manifestType.GetNestedType('FileEntry',[Reflection.BindingFlags]::NonPublic)
$manifestConstructor=$manifestType.GetConstructors($flags)[0]
$payloadConstructor=$payloadType.GetConstructors($flags)[0]
$fileConstructor=$fileType.GetConstructors($flags)[0]
$prototype=@($entries | Where-Object {$_.GetType().GetField('Role',$flags).GetValue($_) -eq 10})[0]
$payloadPrototype=$prototype.GetType().GetField('Payload',$flags).GetValue($prototype)
$lifetime=[Threading.CancellationTokenSource]::new()
$candidate=$pinConstructor.Invoke([object[]]@($lifetime.Token))
$created=[Collections.Generic.List[IO.FileStream]]::new(); $capacityWriters=@()
try {
    foreach($entry in $entries) {
        if($entry.GetType().GetField('Role',$flags).GetValue($entry) -ne 10 -and -not (Add-PreparedFixture $candidate $entry)) {throw 'Capacity base pin failed'}
    }
    for($slot=0;$slot -lt 2;$slot++) {
        $payloads=[Array]::CreateInstance($payloadType,16)
        for($i=0;$i -lt 16;$i++) {
            $path=[IO.Path]::Combine($Directory,('preparation-capacity-'+$slot+'-'+$i+'.bin'))
            $file=[IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
            $created.Add($file); $file.WriteByte([byte]($i+1)); $file.Flush($true)
            $description=[FrontierPreparationProbe]::Describe($file)
            $descriptor=$fileConstructor.Invoke([object[]]@($description[0],[uint32]$description[1],[uint32]$description[2],[uint32]$description[3],$description[4]))
            $payload=$payloadConstructor.Invoke([object[]]@([guid]::NewGuid(),$descriptor,[byte[]]::new(32),
                $payloadType.GetField('Completion',$flags).GetValue($payloadPrototype),$payloadType.GetField('Marker',$flags).GetValue($payloadPrototype)))
            $payloads.SetValue($payload,$i)
            if(-not $pinAdd.Invoke($candidate,[object[]]@(10,$file.SafeFileHandle,1L,$slot,$payload))) {throw 'Capacity payload pin failed'}
        }
        $w=$writers[$slot]
        $args=@('GuiRequest','AppRequest','AppId','AppCreation','Writer','Source','Drain') | ForEach-Object {$manifestType.GetField($_,$flags).GetValue($w)}
        $args+=[object[]]@(,$payloads)
        $capacityWriters+=@($manifestConstructor.Invoke([object[]]$args))
    }
    if(-not $pinSeal.Invoke($candidate,[object[]]@($capacityWriters[0],$capacityWriters[1])) -or -not $pinCurrent.GetValue($candidate,$null)) {throw 'Exact 42-file capacity failed'}
    $capacityEntries=@($pinEntries.GetValue($candidate)); if($capacityEntries.Count -ne 42) {throw 'Capacity not exercised'}
    foreach($entry in $capacityEntries) {if(-not [FrontierPreparationProbe]::WriteDenied($entry.GetType().GetField('Handle',$flags).GetValue($entry))) {throw 'Capacity pin writable'}}
    $primitiveChecks++
    $overflow=$pinConstructor.Invoke([object[]]@($lifetime.Token))
    try {
        foreach($entry in $capacityEntries) {if(-not (Add-PreparedFixture $overflow $entry)) {throw 'Overflow fixture did not reach 42 pins'}}
        $file=[IO.FileStream]::new([IO.Path]::Combine($Directory,'preparation-capacity-extra.bin'),[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
        $created.Add($file); $file.WriteByte(1); $file.Flush($true)
        $description=[FrontierPreparationProbe]::Describe($file)
        $descriptor=$fileConstructor.Invoke([object[]]@($description[0],[uint32]$description[1],[uint32]$description[2],[uint32]$description[3],$description[4]))
        $payload=$payloadConstructor.Invoke([object[]]@([guid]::NewGuid(),$descriptor,[byte[]]::new(32),
            $payloadType.GetField('Completion',$flags).GetValue($payloadPrototype),$payloadType.GetField('Marker',$flags).GetValue($payloadPrototype)))
        if($pinAdd.Invoke($overflow,[object[]]@(10,$file.SafeFileHandle,1L,0,$payload))) {throw 'Distinct 43rd native pin accepted'}
        foreach($entry in $pinEntries.GetValue($overflow)) {if(-not $entry.GetType().GetField('Handle',$flags).GetValue($entry).IsClosed) {throw 'Overflow leaked partial set'}}
        if(-not $pinCurrent.GetValue($candidate,$null)) {throw 'Overflow affected separate original set'}
        $primitiveChecks++
    } finally {$overflow.Dispose()}
    $candidate.Dispose()
    foreach($entry in $capacityEntries) {if(-not $entry.GetType().GetField('Handle',$flags).GetValue($entry).IsClosed) {throw 'Capacity cleanup leaked pin'}}
} finally {$candidate.Dispose(); $lifetime.Dispose(); foreach($file in $created) {$file.Dispose()}}
('frontier-preparation-primitive='+$primitiveChecks)
