param([string]$RepoRoot,[string]$ArtifactRoot,[string]$WorkerCase,[string]$SlotName,[string]$RequestId)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalEvidenceExportSlot.ps1')

if($WorkerCase) {
    $go=[Threading.EventWaitHandle]::OpenExisting($SlotName+'.go')
    $ready=[Threading.EventWaitHandle]::OpenExisting($SlotName+'.ready')
    $finish=[Threading.EventWaitHandle]::OpenExisting($SlotName+'.finish')
    $bytes=[IO.File]::ReadAllBytes((Join-Path $ArtifactRoot 'payload.bin'))
    [IO.File]::WriteAllText((Join-Path $ArtifactRoot 'primary.txt'),'primary-result=unchanged')
    if(-not $go.WaitOne(15000)) { throw 'Test owner did not release producer' }
    $request=[guid]$RequestId
    if($WorkerCase -ceq 'wrong-request') { $request=[guid]::NewGuid() }
    if($WorkerCase -ceq 'null') { $bytes=$null }
    $target=if($WorkerCase -ceq 'missing-owner') { $SlotName+'-missing' } else { $SlotName }
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $offered=[NLink.Tools.LocalEvidenceExportSlot]::TryOffer($target,$request,$bytes)
    $watch.Stop()
    $second=$false
    if($WorkerCase -ceq 'duplicate') { $second=[NLink.Tools.LocalEvidenceExportSlot]::TryOffer($target,$request,$bytes) }
    if($WorkerCase -cin @('magic','request-header','producer-header','length','empty-length','partial','payload','abandoned')) {
        # Direct shared-memory mutations are transport-only negative controls,
        # never a producer projection or a new authority validation.
        $rawGate=[Threading.Mutex]::OpenExisting($SlotName+'.gate')
        if(-not $rawGate.WaitOne(0)) { throw 'Test mutation gate unavailable' }
        $rawMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($SlotName)
        $rawView=$rawMap.CreateViewAccessor()
        switch($WorkerCase) {
            'magic' { $rawView.Write(0,0) }
            'request-header' { $rawView.WriteArray(16,[guid]::NewGuid().ToByteArray(),0,16) }
            'producer-header' { $rawView.Write(8,0) }
            'length' { $rawView.Write(32,262145) }
            'empty-length' { $rawView.Write(32,0) }
            'partial' { $rawView.Write(4,1) }
            'payload' { $rawView.Write(64,[byte]0) }
        }
        if($WorkerCase -cne 'abandoned') { $rawGate.ReleaseMutex(); $rawView.Dispose(); $rawMap.Dispose(); $rawGate.Dispose() }
    }
    [IO.File]::WriteAllText((Join-Path $ArtifactRoot 'offer.json'),(ConvertTo-Json -InputObject @{
        offered=$offered;second=$second;elapsedMilliseconds=$watch.ElapsedMilliseconds} -Compress))
    [void]$ready.Set()
    if($WorkerCase -ceq 'alive') { if(-not $finish.WaitOne(15000)) { throw 'Test owner did not release live worker' } }
    if($WorkerCase -ceq 'nonzero') { exit 9 }
    if($WorkerCase -ceq 'abandoned') { [Environment]::Exit(0) }
    $finish.Dispose(); $ready.Dispose(); $go.Dispose()
    exit 0
}

function Hash([byte[]]$Bytes) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
$hostName=if($PSVersionTable.PSVersion.Major -eq 5) { 'powershell' } else { 'pwsh' }
$cases=@('valid','duplicate','maximum','over-limit','empty','null','busy','cancelled','disposed','unbound','foreign-producer',
    'missing-owner','wrong-request','wrong-hash','magic','request-header','producer-header','length','empty-length','partial','payload','abandoned','alive','nonzero')
$rows=[Collections.Generic.List[object]]::new()
foreach($caseName in $cases) {
    $directory=Join-Path $ArtifactRoot $caseName
    [void](New-Item -ItemType Directory -Path $directory)
    $bytes=[Text.Encoding]::UTF8.GetBytes('prepared-opaque-evidence')
    if($caseName -cin @('maximum','over-limit')) { $bytes=[Text.Encoding]::ASCII.GetBytes(('x' * $(if($caseName -ceq 'maximum') { 262144 } else { 262145 }))) }
    if($caseName -ceq 'empty') { $bytes=[byte[]]@() }
    [IO.File]::WriteAllBytes((Join-Path $directory 'payload.bin'),$bytes)
    $hash=Hash $bytes
    $expected=if($caseName -ceq 'wrong-hash') { 'a'*64 } else { $hash }
    $request=[guid]::NewGuid()
    $slot=[NLink.Tools.LocalEvidenceExportSlot]::new($request,$expected)
    $name=$slot.Name
    $go=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$name+'.go')
    $ready=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$name+'.ready')
    $finish=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$name+'.finish')
    $process=[Diagnostics.Process]::new(); $lock=$null; $lockHeld=$false; $current=$null
    try {
        $values=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalEvidenceExportSlot.ps1'),
            '-RepoRoot',$RepoRoot,'-ArtifactRoot',$directory,'-WorkerCase',$caseName,'-SlotName',$name,'-RequestId',$request.ToString('D'))
        $process.StartInfo=[Diagnostics.ProcessStartInfo]::new()
        $process.StartInfo.FileName=$hostName
        $process.StartInfo.Arguments=($values | ForEach-Object { '"'+([string]$_).Replace('"','\"')+'"' }) -join ' '
        $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
        $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
        [void]$process.Start()
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if($caseName -ceq 'foreign-producer') { $current=[Diagnostics.Process]::GetCurrentProcess(); $bound=$slot.BindProducer($current) }
        elseif($caseName -ceq 'unbound') { $bound=$false }
        else { $bound=$slot.BindProducer($process) }
        if($caseName -cne 'unbound' -and -not $bound) { throw 'Exact producer did not bind' }
        if($bound -and $slot.BindProducer($process)) { throw 'Producer binding was replaced' }
        if($caseName -ceq 'busy') { $lock=[Threading.Mutex]::OpenExisting($name+'.gate'); $lockHeld=$lock.WaitOne(0); if(-not $lockHeld) { throw 'Test contention lock missing' } }
        if($caseName -ceq 'cancelled') { $slot.Cancel() }
        if($caseName -ceq 'disposed') { $slot.Dispose() }
        [void]$go.Set()
        if(-not $ready.WaitOne(15000)) {
            if($process.HasExited) { throw "Worker failed: $caseName : $($stderr.GetAwaiter().GetResult())" }
            throw "Worker did not reach publication: $caseName"
        }
        if($caseName -ceq 'alive') {
            if($process.HasExited -or $null -ne $slot.TakeAfterExit()) { throw 'Live producer admitted' }
            [void]$finish.Set()
        }
        if(-not $process.WaitForExit(15000)) { throw 'Bounded test producer did not exit' }
        $process.WaitForExit(); $exit=$process.ExitCode
        if($exit -ne $(if($caseName -ceq 'nonzero') { 9 } else { 0 })) { throw "Worker exit: $caseName : $($stderr.GetAwaiter().GetResult())" }
        $process.Dispose() # Match real parent cleanup before later optional reading.
        if($lockHeld) { $lock.ReleaseMutex(); $lockHeld=$false }
        $value=$slot.TakeAfterExit()
        $positive=$caseName -cin @('valid','duplicate','maximum')
        if(($null -ne $value) -ne $positive -or ($positive -and (Hash $value) -cne $hash)) { throw "Slot result drift: $caseName" }
        if($null -ne $slot.TakeAfterExit()) { throw 'Slot was consumed twice' }
        $offer=[IO.File]::ReadAllText((Join-Path $directory 'offer.json')) | ConvertFrom-Json
        if($offer.second -or ($caseName -ceq 'busy' -and ($offer.offered -or $offer.elapsedMilliseconds -gt 500)) -or
            ($positive -and -not $offer.offered)) { throw "Offer/zero-wait result drift: $caseName" }
        if([IO.File]::ReadAllText((Join-Path $directory 'primary.txt')) -cne 'primary-result=unchanged') { throw 'Primary fixture changed' }
        if($positive) { $value[0]=0; if($null -ne $slot.TakeAfterExit()) { throw 'Detached buffer mutated slot lifetime' } }
        $rows.Add([ordered]@{Case=$caseName;Offered=$offer.offered;Consumed=$positive;PrimaryMarkerUnchanged=$true})
    }
    finally {
        [void]$finish.Set()
        try { if(-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) } } catch { }
        if($lockHeld) { $lock.ReleaseMutex() }
        if($null -ne $lock) { $lock.Dispose() }
        if($null -ne $current) { $current.Dispose() }
        $process.Dispose(); $slot.Dispose(); $go.Dispose(); $ready.Dispose(); $finish.Dispose()
    }
    if([NLink.Tools.LocalEvidenceExportSlot]::TryOffer($name,$request,[byte[]]@(1))) { throw 'Disposed owner was recreated' }
}
$invalid=0
foreach($pair in @(@([guid]::Empty,('a'*64)),@([guid]::NewGuid(),''),@([guid]::NewGuid(),('A'*64)),
    @([guid]::NewGuid(),(('a'*64)+[char]10)),@([guid]::NewGuid(),(('a'*64)+[char]13+[char]10)))) {
    $rejected=$false
    try { $bad=[NLink.Tools.LocalEvidenceExportSlot]::new($pair[0],$pair[1]); $bad.Dispose() } catch { $rejected=$true }
    if(-not $rejected) { throw 'Invalid owner request/digest admitted' }
    $invalid++
}
'local-evidence-slot='+ (ConvertTo-Json -InputObject ([ordered]@{Cases=$rows.Count;InvalidOwners=$invalid;Rows=@($rows.ToArray())
    MaximumPayloadBytes=262144;ProductionCaller=$false;AuthorityProof=$false}) -Depth 6 -Compress)
