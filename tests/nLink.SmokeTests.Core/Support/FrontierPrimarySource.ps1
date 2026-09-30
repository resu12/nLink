param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$SourcePath,[string]$Case,[switch]$SourceDiagnostics)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($SourceDiagnostics) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchLoader.ps1') $RepoRoot}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Collections;
public sealed class SourceIteration : IDisposable {
    private readonly IEnumerator cursor;
    public SourceIteration(object reader) { cursor=((IEnumerable)reader.GetType().GetMethod("ReadLines").Invoke(reader,null)).GetEnumerator(); }
    public bool MoveNext() { return cursor.MoveNext(); }
    public string Current { get { return (string)cursor.Current; } }
    public void Dispose() { ((IDisposable)cursor).Dispose(); }
}
'@
$mode=$Case.Substring('source-'.Length)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'; $clone=[object].GetMethod('MemberwiseClone',$flags)
$signals=@{}; $worker=$null; $owned=$null; $reader=$null; $cursor=$null; $file=$null
function Expire-SourceWork {
    $watch=$owned.GetType().GetField('work',$flags).GetValue($owned)
    $elapsed=@($watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
    $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*21))
}
function Require-ReadFailure($Enumerator) {
    $failed=$false
    try {[void]$Enumerator.MoveNext()} catch {$failed=$true}
    if(-not $failed) {throw 'Rejected source read did not fail'}
}
try {
    foreach($part in @('begin','attached','release')) {$signals[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.worker.'+$part)}
    if(-not $signals.begin.WaitOne(30000)) {throw 'Source begin deadline'}
    $worker=[NLink.Tools.LocalRetainedSourceRequest]::TryAttachFrontier($Name,$RequestId,$SourcePath,'frontier-gui-run','frontier-gui-scenario')
    if($null -eq $worker) {throw 'Native source worker unavailable'}
    $owned=$worker.FrontierInputs
    if($mode -ceq 'copied-worker') {
        $copy=$clone.Invoke($worker,$null)
        if($null -ne $copy.TryOpenSource()) {throw 'Copied worker opened native source'}
        $copy.Dispose(); if(-not $worker.IsCurrent) {throw 'Copied worker retired original source'}
    }
    if($mode -ceq 'wrong-count') {$worker.GetType().GetProperty('SourceLines').SetValue($worker,[long]($worker.SourceLines+1),$null)}
    $reader=$worker.TryOpenSource()
    if(($null -eq $reader) -ne ($mode -ceq 'wrong-count')) {throw 'Native source open mismatch'}
    $retired=$mode -cin @('wrong-count','repeat-open','repeat-read','partial-read','reader-dispose','inputs-dispose','worker-dispose',
        'pin-close','post-scope','post-facts','parent-cancel','post-parent-pin','read-budget','origin-budget')
    $post=$mode -cin @('post-scope','post-facts','parent-cancel','post-parent-pin')
    if($null -ne $reader) {
        if($reader.InputReadComplete -or $worker.OwnsCompletedSource($reader) -or $reader.GetFrontierPhysicalOrigin(1,'foreign')) {throw 'Unread source promoted completion/origin'}
        $mapped=$owned.GetType().GetField('nativeStream',$flags).GetValue($owned)
        $file=$mapped.GetType().GetField('stream',$flags).GetValue($mapped)
        if(-not $file.CanRead -or $file.CanWrite -or $mapped.CanSeek -or $mapped.CanWrite) {throw 'Source exposed writable/shared-position stream'}
        if($mode -ceq 'copied-reader') {
            $copy=$clone.Invoke($reader,$null); $foreign=[SourceIteration]::new($copy)
            try {Require-ReadFailure $foreign} finally {$foreign.Dispose()}
            $copy.Dispose(); if($copy.InputReadComplete -or -not $worker.IsCurrent) {throw 'Copied reader affected original'}
        }
        $cursor=[SourceIteration]::new($reader)
        $lines=[Collections.Generic.List[string]]::new()
        if($mode -ceq 'repeat-open') {
            if($null -ne $worker.TryOpenSource()) {throw 'Native source opened twice'}
            Require-ReadFailure $cursor
        } elseif($mode -ceq 'read-budget') {
            if($SourceDiagnostics -and -not $cursor.MoveNext()) {throw 'Diagnostic source did not reach its first line'}
            Expire-SourceWork; Require-ReadFailure $cursor
        } else {
            if(-not $cursor.MoveNext()) {throw 'Native source has no lines'}
            $lines.Add($cursor.Current)
            if($reader.GetFrontierPhysicalOrigin(1,$lines[0]) -ne 0 -or $reader.InputReadComplete) {throw 'Partial read promoted physical origin'}
            if($post) { } elseif($mode -ceq 'partial-read') {$cursor.Dispose()} else {
                if($mode -ceq 'pre-eof') {
                    while($lines.Count -lt $worker.SourceLines) {
                        if(-not $cursor.MoveNext()) {throw 'Source ended before its declared final record'}
                        $lines.Add($cursor.Current)
                    }
                    if($reader.InputReadComplete -or $reader.GetFrontierPhysicalOrigin($lines.Count,$lines[$lines.Count-1])) {throw 'Last record promoted completion before EOF observation'}
                }
                while($cursor.MoveNext()) {$lines.Add($cursor.Current)}
                if(-not $reader.InputReadComplete -or -not $worker.OwnsCompletedSource($reader) -or $reader.LinesRead -ne $worker.SourceLines) {throw 'Native source EOF incomplete'}
                if($mode -ceq 'copied-worker' -and $copy.OwnsCompletedSource($reader)) {throw 'Copied worker inherited original completed source ownership'}
                $origins=[Collections.Generic.List[int]]::new(); $previous=0
                for($i=0;$i -lt $lines.Count;$i++) {
                    $origin=$reader.GetFrontierPhysicalOrigin($i+1,$lines[$i])
                    if($origin -le $previous -or $origin -gt $owned.SourceLineCount) {throw 'Native physical origin is not original ordered ledger position'}
                    $origins.Add($origin); $previous=$origin
                }
                'frontier-primary-source-origins='+($origins -join ',')
                $sha=[Security.Cryptography.SHA256]::Create()
                try {
                    $bytes=[Text.UTF8Encoding]::new($false,$true).GetBytes(($lines -join [Environment]::NewLine))
                    $hash=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant()
                    if($hash -cne $worker.SourceSha256 -or $bytes.Length -ne $worker.SourceBytes) {throw 'Native source lines differ from original bytes'}
                } finally {$sha.Dispose()}
                $full=[string[]]$lines.ToArray(); $compact=[string[]]@(); $work=[Diagnostics.Stopwatch]::StartNew()
                $capture=[NLink.Tools.LocalNativeInputCapture]::new($worker.RequestId)
                try {
                    if($null -ne $worker.CaptureNativeInputs($reader,$capture,$full,$compact,$work) -or
                        $worker.ValidateInputReferences($reader,$full,$full,$compact,$compact,$work) -or
                        $null -ne $worker.TryBeginPrimary($reader,[object]::new(),$full,$compact) -or $null -ne $worker.TryOpenWriter()) {throw 'Source EOF promoted legacy input/evaluation/writer authority'}
                } finally {$capture.Dispose(); $work.Stop()}
                switch($mode) {
                    'origin-substitution' {
                        $equal=[string]::new($lines[0].ToCharArray())
                        if([object]::ReferenceEquals($equal,$lines[0]) -or $reader.GetFrontierPhysicalOrigin(1,$equal)) {throw 'Equal replacement line acquired origin'}
                    }
                    'origin-index' {if($reader.GetFrontierPhysicalOrigin(0,$lines[0]) -or $reader.GetFrontierPhysicalOrigin($lines.Count+1,$lines[0])) {throw 'Out-of-range source origin accepted'}}
                    'copied-reader' {if($copy.GetFrontierPhysicalOrigin(1,$lines[0])) {throw 'Copied reader acquired completed origin'}}
                    'repeat-read' {$again=[SourceIteration]::new($reader); try {Require-ReadFailure $again} finally {$again.Dispose()}}
                    'reader-dispose' {$reader.Dispose()}
                    'inputs-dispose' {$owned.Dispose()}
                    'worker-dispose' {$worker.Dispose()}
                    'pin-close' {
                        $pins=$owned.GetType().GetField('pins',$flags).GetValue($owned)
                        $handles=$pins.GetType().GetField('Handles',$flags).GetValue($pins); $handles[4].Dispose()
                    }
                    'origin-budget' {Expire-SourceWork; if($reader.GetFrontierPhysicalOrigin(1,$lines[0])) {throw 'Expired origin remained available'}}
                }
            }
        }
    }
    [void]$signals.attached.Set()
    if(-not $signals.release.WaitOne(30000)) {throw 'Source review deadline'}
    if($post) {
        if($mode -ceq 'parent-cancel' -and -not [Threading.SpinWait]::SpinUntil([Func[bool]]{-not $file.CanRead},5000)) {throw 'Idle source cancellation did not close mapping stream'}
        Require-ReadFailure $cursor
    }
    if($retired) {
        if($worker.IsCurrent -or $owned.IsCurrent -or ($null -ne $reader -and $reader.InputReadComplete)) {throw 'Retired source stayed current'}
        if($null -ne $file -and $file.CanRead) {throw 'Retired source stream leaked'}
    } elseif(-not $worker.IsCurrent -or -not $reader.InputReadComplete) {throw 'Current native source retired unexpectedly'}
    if($SourceDiagnostics) {
        $rejection=$owned.GetType().GetField('PrimarySourceRejectionForTest',$flags).GetValue($owned)
        $snapshot=$owned.GetType().GetField('PrimarySourceSnapshotForTest',$flags).GetValue($owned)
        $close=$owned.GetType().GetField('PrimarySourceCloseForTest',$flags).GetValue($owned)
        $closeSnapshot=$owned.GetType().GetField('PrimarySourceCloseSnapshotForTest',$flags).GetValue($owned)
        $expected=switch($mode) {'valid' {'none'} 'origin-substitution' {'none'} 'origin-index' {'none'} 'copied-reader' {'none'} 'origin-budget' {'resource:budget'} 'read-budget' {'read:budget'} 'post-scope' {'read:scope'} 'post-facts' {'resource:files'} 'pin-close' {'resource:pins'} 'parent-cancel' {'read:closed'} default {throw 'Unknown diagnostic control'}}
        if($rejection -cne $expected) {throw "First source rejection changed: $mode / $rejection / $snapshot / $close"}
        if(-not $retired) {if($close -cne 'open') {throw 'Valid diagnostic source closed early'}}
        else {
            if($closeSnapshot -notmatch '; lines=[1-9][0-9]*;' -or $close -ceq 'open') {throw "Source diagnostic lost pre-close progress: $rejection / $close / $closeSnapshot"}
            if($mode -ceq 'parent-cancel' -and ($close -notmatch '^watch:' -or $snapshot -notmatch '; lines=0;')) {throw 'Cancellation diagnostic invented retained lines'}
            if($mode -ceq 'read-budget' -and $snapshot -notmatch '^work_ms=2[1-9][0-9]{3}; lines=1;') {throw 'Source budget observation changed'}
        }
        $worker.Dispose(); $owned.Dispose()
        if($owned.GetType().GetField('PrimarySourceRejectionForTest',$flags).GetValue($owned) -cne $rejection -or
            $owned.GetType().GetField('PrimarySourceSnapshotForTest',$flags).GetValue($owned) -cne $snapshot -or
            ($retired -and $owned.GetType().GetField('PrimarySourceCloseSnapshotForTest',$flags).GetValue($owned) -cne $closeSnapshot) -or
            ($retired -and $owned.GetType().GetField('PrimarySourceCloseForTest',$flags).GetValue($owned) -cne $close)) {throw 'Cleanup replaced the first source diagnostic'}
        'primary-source-diagnostic; case='+$mode+'; rejected='+$rejection+'; close='+$close+'; '+$snapshot
        'primary-source-close-diagnostic; '+$closeSnapshot
        $calls=$owned.GetType().GetField('PrimaryCheckCallsForTest',$flags).GetValue($owned)
        $ticks=$owned.GetType().GetField('PrimaryCheckTicksForTest',$flags).GetValue($owned)
        $readCalls=$owned.GetType().GetField('PrimaryReadCallsForTest',$flags).GetValue($owned)
        $readTicks=$owned.GetType().GetField('PrimaryReadWorkTicksForTest',$flags).GetValue($owned)
        $work=$owned.GetType().GetField('work',$flags).GetValue($owned)
        if($calls.Length -ne 12 -or $ticks.Length -ne 12 -or $readCalls -ne $reader.LinesRead+1 -or $readTicks -le 0 -or $work.IsRunning) {throw 'Source work profile lost original read/clock bounds'}
        for($stage=0;$stage -lt 3;$stage++) {
            $i=$stage*4
            if($calls[$i] -lt $calls[$i+1] -or $calls[$i+1] -lt $calls[$i+2] -or $calls[$i+2] -lt $calls[$i+3]) {throw 'Profiler repeated a short-circuited predicate'}
        }
        if(@($ticks | Where-Object {$_ -lt 0}).Count -or $calls[4] -le 0 -or $calls[7] -le 0 -or $calls[11] -le 0) {throw 'Profiler lost active/inactive native checks'}
        $originCalls=$owned.GetType().GetField('PrimaryOriginCallsForTest',$flags).GetValue($owned)
        $originAccepted=$owned.GetType().GetField('PrimaryOriginAcceptedForTest',$flags).GetValue($owned)
        $originTicks=$owned.GetType().GetField('PrimaryOriginWorkTicksForTest',$flags).GetValue($owned)
        $originChecks=$owned.GetType().GetField('PrimaryOriginCheckCallsForTest',$flags).GetValue($owned)
        $originCheckTicks=$owned.GetType().GetField('PrimaryOriginCheckTicksForTest',$flags).GetValue($owned)
        $expectedOrigins=if($mode -ceq 'read-budget') {1} elseif($post) {2} elseif($mode -cin @('origin-budget','origin-substitution')) {6} elseif($mode -ceq 'origin-index') {7} else {5}
        $expectedAccepted=if($post -or $mode -ceq 'read-budget') {0} else {3}
        if($originCalls -ne $expectedOrigins -or $originAccepted -ne $expectedAccepted -or $originTicks -le 0 -or
            @($originCheckTicks | Where-Object {$_ -lt 0}).Count -or $originChecks[0] -gt $originCalls) {throw 'Origin phase counts changed original evaluations'}
        for($j=0;$j -lt 4;$j++) {if($originChecks[$j] -gt $calls[$j] -or ($j -gt 0 -and $originChecks[$j] -gt $originChecks[$j-1])) {throw 'Origin phase repeated or moved checks'}}
        if($mode -ceq 'origin-budget' -and ($close -cne 'SourceOrigin' -or $snapshot -notmatch '; ended=True$')) {throw 'Origin budget diagnostic lost completed source'}
        $before=($calls -join ',')+' / '+($ticks -join ',')+' / '+$work.ElapsedTicks
        $originBefore=($originChecks -join ',')+' / '+($originCheckTicks -join ',')+' / '+$originCalls+' / '+$originAccepted+' / '+$originTicks
        $profile=$owned.GetType().GetMethod('GetPrimaryWorkProfileForTest',$flags).Invoke($owned,$null)
        if($before -cne (($calls -join ',')+' / '+($ticks -join ',')+' / '+$work.ElapsedTicks)) {throw 'Profile formatting evaluated authority or changed clocks'}
        if($originBefore -cne (($originChecks -join ',')+' / '+($originCheckTicks -join ',')+' / '+
            $owned.GetType().GetField('PrimaryOriginCallsForTest',$flags).GetValue($owned)+' / '+
            $owned.GetType().GetField('PrimaryOriginAcceptedForTest',$flags).GetValue($owned)+' / '+
            $owned.GetType().GetField('PrimaryOriginWorkTicksForTest',$flags).GetValue($owned))) {throw 'Origin formatting changed work counters'}
        $profile
    }
    $worker.Dispose()
    if(($null -ne $file -and $file.CanRead) -or $owned.IsCurrent) {throw 'Worker disposal leaked native source'}
    'frontier-primary-worker='+$Case
    'frontier-primary-source='+$mode
} finally {
    if($null -ne $cursor) {$cursor.Dispose()}; if($null -ne $reader) {$reader.Dispose()}
    if($null -ne $worker) {$worker.Dispose()}
    foreach($event in $signals.Values) {$event.Dispose()}
}
