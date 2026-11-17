param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$checks=[Collections.Generic.List[string]]::new()
function Source([string]$Name,[byte[]]$Bytes) {
    $path=Join-Path $Directory $Name
    [IO.File]::WriteAllBytes($path,$Bytes)
    return $path
}
function Drain($Lease) {
    $rows=[Collections.Generic.List[string]]::new()
    try { foreach($line in $Lease.VerifyAndReadLines()) { $rows.Add($line) } } catch { }
    return ,$rows.ToArray()
}
function Iterator($Lease) {
    return ,([Collections.IEnumerable].GetMethod('GetEnumerator').Invoke($Lease.VerifyAndReadLines(),@()))
}
$move=[Collections.IEnumerator].GetMethod('MoveNext')
$dispose=[IDisposable].GetMethod('Dispose')
$path=Source 'input.log' ([Text.Encoding]::UTF8.GetBytes("A`r`nB`nC`rD"))
$baseline=Get-FileTransferEvidenceSha256 $path
$lease=$null
$observed=Get-FileTransferEvidenceSha256 $path -ReadLease ([ref]$lease)
try {
    if($null -eq $lease -or -not $lease.IsVerified -or $observed -cne $baseline -or
        $lease.ExpectedSha256 -cne $baseline -or $lease.ByteCount -ne ([IO.FileInfo]$path).Length) { throw 'Hash-pass ownership unavailable' }
    if((Get-FileTransferEvidenceSha256 $path) -cne $baseline -or (Get-FileTransferEvidenceLineCount $path) -ne 4 -or
        ([IO.File]::ReadAllLines($path) -join '|') -cne 'A|B|C|D') { throw 'Read lease changed ordinary readers' }
    $checks.Add('same-hash-and-default-readers'); $checks.Add('exact-file-object-descriptor')
    $stream=$lease.TryOpenVerifiedRead()
    if($null -eq $stream -or $stream.CanWrite) { throw 'Verified read missing or writable' }
    $stream.Dispose()
    foreach($operation in @('write','rename','delete')) {
        $denied=$false; $stream=$null
        try {
            switch($operation) {
                'write' { $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite) }
                'rename' { [IO.File]::Move($path,(Join-Path $Directory 'unexpected-rename.log')) }
                'delete' { [IO.File]::Delete($path) }
            }
        }
        catch [IO.IOException] { $denied=$true }
        finally { if($null -ne $stream) { $stream.Dispose() } }
        if(-not $denied) { throw "Lease admitted $operation" }
        $checks.Add('denied-'+$operation)
    }
    $other=$null; $same=Get-FileTransferEvidenceSha256 $path -ReadLease ([ref]$other)
    try { if($null -eq $other -or $other.FileIdentity -cne $lease.FileIdentity -or $same -cne $baseline) { throw 'Compatible owners did not share exact identity' } }
    finally { if($null -ne $other) { $other.Dispose() } }
    $checks.Add('multiple-read-only-owners')
    $rejected=$false
    try { $null=Get-FileTransferEvidenceSha256 $path -ReadLease ([ref]$lease) } catch { $rejected=$true }
    if(-not $rejected -or -not $lease.IsVerified) { throw 'Assigned owner was overwritten' }
    $checks.Add('assigned-owner-not-overwritten')
}
finally { if($null -ne $lease) { $lease.Dispose(); $lease.Dispose() } }
if($lease.IsActive -or $lease.IsVerified -or $null -ne $lease.TryOpenVerifiedRead()) { throw 'Disposed source ownership revived' }
$stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite); $stream.Dispose()
$checks.Add('dispose-invalidates-and-releases')
foreach($case in @('valid','wrong-hash','too-few-lines','too-many-lines','partial','exact-count-without-eof','cancel','time-budget')) {
    $hash=if($case -ceq 'wrong-hash') { '0'*64 } else { $baseline }
    $count=if($case -ceq 'too-few-lines') { 3L } elseif($case -ceq 'too-many-lines') { 5L } else { 4L }
    $candidate=[NLink.Tools.LocalInputReadLease]::OpenExpected($path,$hash,$count)
    try {
        if($null -eq $candidate -or $candidate.IsVerified -or $null -ne $candidate.TryOpenVerifiedRead()) { throw 'Candidate hash was treated as verified' }
        if($case -cin @('partial','exact-count-without-eof','cancel','time-budget')) {
            $iterator=Iterator $candidate
            try {
                if(-not $move.Invoke($iterator,@())) { throw 'Verification did not begin' }
                if($case -ceq 'exact-count-without-eof') {
                    for($i=1; $i -lt 4; $i++) { if(-not $move.Invoke($iterator,@())) { throw 'Expected source records unavailable' } }
                }
                if($case -ceq 'cancel') { $candidate.Dispose() }
                if($case -ceq 'time-budget') {
                    # Exhaust the real clock deterministically without a five-second sleep.
                    $clock=$candidate.GetType().GetField('verificationClock',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($candidate)
                    $clock.Stop()
                    $elapsed=@($clock.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic') | Where-Object Name -In @('elapsed','_elapsed'))
                    if($elapsed.Count -ne 1) {throw 'Verification test clock shape changed'}
                    $elapsed[0].SetValue($clock,[long]([Diagnostics.Stopwatch]::Frequency*([NLink.Tools.LocalInputReadLease]::MaximumVerificationMilliseconds+1)/1000))
                }
                if($case -cin @('cancel','time-budget')) { try { $null=$move.Invoke($iterator,@()) } catch { } }
            }
            finally { $null=$dispose.Invoke($iterator,@()) }
        }
        else { $rows=Drain $candidate }
        if(($case -ceq 'valid') -ne $candidate.IsVerified) { throw "Wrong verification state: $case" }
        if($case -ceq 'valid' -and (($rows -join '|') -cne 'A|B|C|D' -or $candidate.VerifiedLineCount -ne 4)) { throw 'Verified line framing changed' }
        if($case -ceq 'time-budget' -and $candidate.OmissionReason -cne 'input_verification_time_limit') { throw 'Elapsed verification budget not enforced' }
        if($case -cin @('partial','exact-count-without-eof','wrong-hash')) {
            $null=Drain $candidate
            if($candidate.IsVerified) { throw 'Failed verification was revived by replay' }
        }
        $checks.Add('candidate-'+$case)
    }
    finally { if($null -ne $candidate) { $candidate.Dispose() } }
}
foreach($hash in @('',('A'*64),($baseline+"`n"))) {
    if($null -ne [NLink.Tools.LocalInputReadLease]::OpenExpected($path,$hash,4)) { throw 'Invalid expected digest accepted' }
}
foreach($count in @(0L,-1L,262145L)) {
    if($null -ne [NLink.Tools.LocalInputReadLease]::OpenExpected($path,$baseline,$count)) { throw 'Invalid expected count accepted' }
}
$checks.Add('strict-digest-and-count-shapes')
foreach($case in @('oversize-line','invalid-utf8','bom-and-empty-lines')) {
    $bytes=switch($case) {
        'oversize-line' { [Text.Encoding]::UTF8.GetBytes('X'*262145) }
        'invalid-utf8' { [byte[]]@(255,255) }
        'bom-and-empty-lines' { [byte[]]@([byte[]]@(239,187,191)+[Text.Encoding]::UTF8.GetBytes("`r`nZ`n")) }
    }
    $special=Source ($case+'.log') $bytes
    $hash=Get-FileTransferEvidenceSha256 $special
    $candidate=[NLink.Tools.LocalInputReadLease]::OpenExpected($special,$hash,$(if($case -ceq 'bom-and-empty-lines'){2L}else{1L}))
    try {
        $rows=Drain $candidate
        if(($case -ceq 'bom-and-empty-lines') -ne $candidate.IsVerified) { throw "Invalid line verification: $case" }
        if($case -ceq 'bom-and-empty-lines' -and ($rows.Length -ne 2 -or $rows[0] -cne '' -or $rows[1] -cne 'Z')) { throw 'BOM/blank-line identity changed' }
        if($candidate.VerificationCodeUnitVisits -gt 262144) { throw 'Line limit was enforced after unbounded construction' }
        $checks.Add($case)
    }
    finally { if($null -ne $candidate) { $candidate.Dispose() } }
}
$empty=Source 'empty.log' ([byte[]]@())
$emptyLease=$null; $emptyHash=Get-FileTransferEvidenceSha256 $empty -ReadLease ([ref]$emptyLease)
try { if($null -eq $emptyLease -or -not $emptyLease.IsVerified -or $emptyHash -cne (Get-FileTransferEvidenceSha256 $empty)) { throw 'Empty source hash changed' } }
finally { if($null -ne $emptyLease) { $emptyLease.Dispose() } }
$missing=$null
if((Get-FileTransferEvidenceSha256 (Join-Path $Directory 'missing.log') -ReadLease ([ref]$missing)) -cne '' -or $null -ne $missing) { throw 'Missing-source behavior changed' }
if([NLink.Tools.LocalInputReadLease].GetConstructors().Length -ne 0) { throw 'Public file-identity constructor exposed' }
$checks.Add('empty-and-missing-source-semantics'); $checks.Add('no-public-identity-construction')
$hashCode=(Get-Command Get-FileTransferEvidenceSha256).ScriptBlock.ToString()
$loader=". (Join-Path (Split-Path -Parent `$PSScriptRoot) 'FileTransferOps/LocalInputReadLease.ps1')"
if(($hashCode.Split([string[]]@($loader),[StringSplitOptions]::None)).Length -ne 2) { throw 'Optional loader seam changed' }
$fallback=[scriptblock]::Create($hashCode.Replace($loader,"throw 'injected optional loader failure'"))
$absent=$null; $fallbackHash=& $fallback -Path $path -ReadLease ([ref]$absent)
if($fallbackHash -cne $baseline -or $null -ne $absent) { throw 'Optional module failure changed the original hash result' }
$checks.Add('optional-loader-failure-preserves-primary-hash')
[uri]$incompatible=$null
$incompatibleHash=Get-FileTransferEvidenceSha256 $path -ReadLease ([ref]$incompatible)
if($null -ne $incompatible -or $incompatibleHash -cne $baseline) { throw 'Typed output omission changed the primary hash' }
$released=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite); $released.Dispose()
$checks.Add('incompatible-output-reference-releases-lease')
'local-input-read-lease='+(ConvertTo-Json -InputObject ([ordered]@{Checks=$checks.ToArray();DefaultReadersCompatible=$true
    ParentWorkerBindingVerified=$false;EvaluatorOwnershipVerified=$false;PublicationCompletionIssued=$false;ProductionCaller=$false}) -Depth 4 -Compress)
