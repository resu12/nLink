param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$SourcePath,[string]$Case)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
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
    $worker.Dispose()
    if(($null -ne $file -and $file.CanRead) -or $owned.IsCurrent) {throw 'Worker disposal leaked native source'}
    'frontier-primary-worker='+$Case
    'frontier-primary-source='+$mode
} finally {
    if($null -ne $cursor) {$cursor.Dispose()}; if($null -ne $reader) {$reader.Dispose()}
    if($null -ne $worker) {$worker.Dispose()}
    foreach($event in $signals.Values) {$event.Dispose()}
}
