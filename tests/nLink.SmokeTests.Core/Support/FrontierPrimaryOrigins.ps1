param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$SourcePath,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$mode=$Case.Substring('origin-batch-'.Length); $profile=$mode -cne 'normal'
if($profile) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchLoader.ps1') $RepoRoot}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Collections;
public sealed class BatchSourceIteration : IDisposable {
    private readonly IEnumerator cursor;
    public BatchSourceIteration(object reader) { cursor=((IEnumerable)reader.GetType().GetMethod("ReadLines").Invoke(reader,null)).GetEnumerator(); }
    public bool MoveNext() { return cursor.MoveNext(); }
    public string Current { get { return (string)cursor.Current; } }
    public void Dispose() { ((IDisposable)cursor).Dispose(); }
}
'@
$flags=[Reflection.BindingFlags]'Instance,NonPublic'; $signals=@{}; $worker=$null; $reader=$null; $cursor=$null
function New-Batch([int]$count) {
    $script:ordinals=[int[]]::new($count); $script:references=[string[]]::new($count)
    for($i=0;$i -lt $count;$i++) {$n=($count-$i-1)%$lines.Count; $script:ordinals[$i]=$n+1; $script:references[$i]=$lines[$n]}
}
function Apply-Fault {
    switch($fault) {
        'scope' {$scope=$owned.GetType().GetField('scope',$flags).GetValue($owned); $bytes=$scope.GetType().GetField('header',$flags).GetValue($scope); $bytes[0]=$bytes[0] -bxor 1}
        'facts' {$files=$owned.GetType().GetField('files',$flags).GetValue($owned); $facts=$files.GetType().GetField('Facts',$flags).GetValue($files); $view=$facts.GetType().GetField('view',$flags).GetValue($facts); $view.Write(80,[byte]($view.ReadByte(80) -bxor 1))}
        'pin' {$pins=$owned.GetType().GetField('pins',$flags).GetValue($owned); $pins.GetType().GetField('Handles',$flags).GetValue($pins)[4].Dispose()}
        'cancel' {$worker.GetType().GetField('closed',$flags).GetValue($worker).Set() | Out-Null}
        'dispose' {$owned.Dispose()}
        'budget' {$w=$owned.GetType().GetField('work',$flags).GetValue($owned); $field=@($w.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]; $field.SetValue($w,[long]([Diagnostics.Stopwatch]::Frequency*21))}
        default {throw 'Unknown fault'}
    }
}
try {
    foreach($part in @('begin','attached','release')) {$signals[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.worker.'+$part)}
    if(-not $signals.begin.WaitOne(30000)) {throw 'Batch source begin deadline'}
    $worker=[NLink.Tools.LocalRetainedSourceRequest]::TryAttachFrontier($Name,$RequestId,$SourcePath,'frontier-gui-run','frontier-gui-scenario')
    if($null -eq $worker) {throw 'Native batch worker unavailable'}
    $owned=$worker.FrontierInputs; $reader=$worker.TryOpenSource()
    if($null -eq $reader -or $null -ne $reader.GetFrontierPhysicalOrigins([int[]]@(1),[string[]]@('foreign'))) {throw 'Unread batch promoted origin'}
    $cursor=[BatchSourceIteration]::new($reader); $lines=[Collections.Generic.List[string]]::new()
    while($lines.Count -lt $worker.SourceLines) {if(-not $cursor.MoveNext()) {throw 'Source ended early'}; $lines.Add($cursor.Current)}
    if($null -ne $reader.GetFrontierPhysicalOrigins([int[]]@(1),[string[]]@($lines[0])) -or $reader.InputReadComplete) {throw 'Batch promoted origin before genuine EOF'}
    if($cursor.MoveNext() -or -not $reader.InputReadComplete) {throw 'Genuine EOF missing'}
    $expected=[int[]]@(for($i=0;$i -lt $lines.Count;$i++) {$reader.GetFrontierPhysicalOrigin($i+1,$lines[$i])})
    if(@($expected | Where-Object {$_ -le 0}).Count) {throw 'Scalar origin baseline missing'}
    'frontier-primary-source-origins='+($expected -join ',')
    $null=$signals.attached.Set(); if(-not $signals.release.WaitOne(30000)) {throw 'Batch source release deadline'}
    $hook=if($profile) {$owned.GetType().GetField('OriginBatchProbeForTest',$flags)} else {$null}
    if($mode -cin @('normal','valid')) {
        foreach($count in @(1,63,64)) {
            New-Batch $count
            $before=if($profile) {($owned.GetType().GetField('PrimaryCheckCallsForTest',$flags).GetValue($owned)).Clone()} else {$null}
            $batch=$reader.GetFrontierPhysicalOrigins($ordinals,$references)
            if($null -eq $batch -or $batch.Length -ne $count) {throw 'Valid batch missing'}
            for($i=0;$i -lt $count;$i++) {if($batch[$i] -ne $expected[$ordinals[$i]-1]) {throw 'Batch reordered or substituted origins'}}
            if($profile) {
                $after=$owned.GetType().GetField('PrimaryCheckCallsForTest',$flags).GetValue($owned)
                for($i=0;$i -lt 4;$i++) {if($after[$i]-$before[$i] -ne 2) {throw 'Batch lost full entry/exit validation or rescanned each origin'}}
            }
        }
        New-Batch 65
        if($null -ne $reader.GetFrontierPhysicalOrigins($ordinals,$references)) {throw 'Oversized batch accepted'}
        foreach($shape in @('null-ordinals','null-lines','empty','mismatch','low','high','substitution','null-line')) {
            New-Batch 3
            switch($shape) {
                'null-ordinals' {$script:ordinals=$null} 'null-lines' {$script:references=$null}
                'empty' {$script:ordinals=[int[]]@();$script:references=[string[]]@()}
                'mismatch' {$script:references=[string[]]@($lines[0])}
                'low' {$ordinals[1]=0} 'high' {$ordinals[1]=$lines.Count+1}
                'substitution' {$references[1]=[string]::new($references[1].ToCharArray())}
                'null-line' {$references[1]=$null}
            }
            if($null -ne $reader.GetFrontierPhysicalOrigins($ordinals,$references) -or -not $worker.IsCurrent) {throw ('Invalid request published or retired reader: '+$shape)}
        }
        New-Batch 3
        $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($reader,$null)
        $before=if($profile) {$owned.GetType().GetField('PrimaryOriginBatchCallsForTest',$flags).GetValue($owned)} else {0}
        if($null -ne $copy.GetFrontierPhysicalOrigins($ordinals,$references) -or -not $worker.IsCurrent) {throw 'Copied reader acquired batch custody'}
        if($profile -and $before -ne $owned.GetType().GetField('PrimaryOriginBatchCallsForTest',$flags).GetValue($owned)) {throw 'Foreign reader evaluated batch authority'}
        if($profile) {
            New-Batch 64
            $hook.SetValue($owned,[Action[string,int]]{param($stage,$index) if($stage -ceq 'tuple-read' -and $index -eq 31) {$ordinals[$index]=0; $references[$index]='foreign'}})
            $batch=$reader.GetFrontierPhysicalOrigins($ordinals,$references)
            if($null -eq $batch -or $batch.Length -ne 64 -or $batch[31] -ne $expected[32%$lines.Count]) {throw 'Validated tuple was reread from mutable request'}
            New-Batch 64
            $hook.SetValue($owned,[Action[string,int]]{param($stage,$index) if($stage -ceq 'tuple-read' -and $index -eq 31) {$references[32]='foreign'}})
            if($null -ne $reader.GetFrontierPhysicalOrigins($ordinals,$references) -or -not $worker.IsCurrent) {throw 'Future invalid tuple published a prefix or retired reader'}
            $hook.SetValue($owned,$null)
        }
        New-Batch 1
        if($null -eq $reader.GetFrontierPhysicalOrigins($ordinals,$references)) {throw 'Invalid batches poisoned original source'}
    } else {
        $phase,$fault=$mode.Split('-',2); New-Batch 64; $script:injected=0
        if($phase -ceq 'after') {
            $prior=$reader.GetFrontierPhysicalOrigins($ordinals,$references)
            if($null -eq $prior -or $prior.Length -ne 64) {throw 'Baseline batch missing'}
            Apply-Fault; $script:injected++
        } else {
            $target=switch($phase) {'before' {'entry'} 'during' {'tuple'} 'closing' {'commit'} 'verified' {'verified'}}
            $hook.SetValue($owned,[Action[string,int]]{param($stage,$index)
                if($stage -ceq $target -and ($phase -cne 'during' -or $index -eq 31)) {Apply-Fault; $script:injected++}
            })
        }
        $batch=$reader.GetFrontierPhysicalOrigins($ordinals,$references)
        if($script:injected -ne 1 -or $null -ne $batch -or $worker.IsCurrent -or $reader.InputReadComplete) {throw ('Fault published a partial batch or retained custody: '+$mode)}
        $hook.SetValue($owned,$null)
        if($null -ne $reader.GetFrontierPhysicalOrigins($ordinals,$references)) {throw 'Retired batch resumed'}
    }
    $worker.Dispose()
    if($owned.IsCurrent -or $owned.GetType().GetField('work',$flags).GetValue($owned).IsRunning) {throw 'Batch cleanup leaked native work'}
    'frontier-primary-worker='+$Case
    'frontier-primary-origin-batch='+$mode
} finally {
    if($null -ne $cursor) {$cursor.Dispose()}; if($null -ne $reader) {$reader.Dispose()}; if($null -ne $worker) {$worker.Dispose()}
    foreach($event in $signals.Values) {$event.Dispose()}
}
