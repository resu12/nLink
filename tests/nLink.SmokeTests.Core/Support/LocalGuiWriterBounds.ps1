param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
# Primitive controls bind the current real process, not a GUI finalizer. They
# test capability admission/one-shot mechanics and never claim parent adoption.
$request=[NLink.Tools.LocalGuiWriterRequest]::TryCreate([guid]::NewGuid(),'run','scenario',$Directory)
$current=[Diagnostics.Process]::GetCurrentProcess(); $owner=$null
try {
    if($null -eq $request -or -not $request.BindWorker($current) -or $request.BindWorker($current)) { throw 'Primitive binding failed' }
    $owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($request.Name,$request.RequestId,'run','scenario',$Directory)
    if($null -eq $owner -or $null -ne [NLink.Tools.LocalGuiWriterRequest]::TryAttach($request.Name,$request.RequestId,'run','scenario',$Directory)) { throw 'Primitive attach not one-shot' }
    $ledger='ledger'; $path=Join-Path $Directory 'not-opened.log'; $bookmark=0L
    [string[]]$hashes=@(('a'*64),('b'*64),('c'*64)); [string[]]$identities=@('12345678:1234567812345678','12345678:1234567812345679','12345678:1234567812345680')
    [long[]]$bytes=@(12,12,4); [long[]]$lines=@(3,3,1); [string[]]$writers=@('writer'); [long[]]$last=@(3)
    switch($Case) {
        '64-writers' { $writers=[string[]]@(1..64 | ForEach-Object { "writer-$_" }); $last=[long[]]@(1..64 | ForEach-Object {3}) }
        '65-writers' { $writers=[string[]]@(1..65 | ForEach-Object { "writer-$_" }); $last=[long[]]@(1..65 | ForEach-Object {3}) }
        'empty-writers' { $writers=[string[]]@(); $last=[long[]]@() }
        'duplicate-writer' { $writers=@('writer','writer'); $last=@(3,3) }
        'metadata' { $writers=[string[]]@(1..20 | ForEach-Object { ('x'*999)+$_ }); $last=[long[]]@(1..20 | ForEach-Object {3}) }
        'writer-length' { $writers=@(('x'*1025)) }
        'negative-bytes' { $bytes[1]=-1 }
        'negative-lines' { $lines[1]=-1 }
        'hash' { $hashes[1]='caller-declared' }
        'identity' { $identities[1]='not-an-identity' }
        'bookmark' { $bookmark=4 }
        'source-relative' { $path='relative.log' }
        'disposed' { $owner.Dispose() }
        'cancelled' { $closed=[Threading.EventWaitHandle]::OpenExisting($request.Name+'.closed'); try { [void]$closed.Set() } finally { $closed.Dispose() } }
        'ledger-length' { $ledger='x'*1025 }
    }
    $completed=$owner.Complete($ledger,$path,3,$bookmark,$hashes,$identities,$bytes,$lines,$writers,$last)
    $expected=$Case -cin @('valid','64-writers')
    if($completed -ne $expected) { throw "Wrong bounded completion result: $Case" }
    if($owner.Complete($ledger,$path,3,$bookmark,$hashes,$identities,$bytes,$lines,$writers,$last)) { throw 'Repeated completion succeeded' }
    if($null -ne $request.TakeAfterExit()) { throw 'Current live process adopted' }
    if(Test-Path -LiteralPath (Join-Path $Directory 'not-opened.log')) { throw 'Capability unexpectedly created a file' }
} finally { if($null -ne $owner) { $owner.Dispose() }; $request.Dispose(); $current.Dispose() }
'gui-writer-request='+(@{Case=$Case; Prepared=$expected; ParentAdopted=$false; FileIo=$false} | ConvertTo-Json -Compress)
