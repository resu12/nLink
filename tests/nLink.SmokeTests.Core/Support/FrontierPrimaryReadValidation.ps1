param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$SourcePath,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchLoader.ps1') $RepoRoot
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Collections;
public sealed class ReadValidationIteration : IDisposable {
    private readonly IEnumerator cursor;
    public ReadValidationIteration(object reader) { cursor=((IEnumerable)reader.GetType().GetMethod("ReadLines").Invoke(reader,null)).GetEnumerator(); }
    public bool MoveNext() { return cursor.MoveNext(); }
    public string Current { get { return (string)cursor.Current; } }
    public void Dispose() { ((IDisposable)cursor).Dispose(); }
}
'@
$mode=$Case.Substring('read-validation-'.Length)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'; $signals=@{}; $worker=$null; $reader=$null; $cursor=$null
function Apply-ReadFault {
    switch($fault) {
        'scope' {$scope=$owned.GetType().GetField('scope',$flags).GetValue($owned); $bytes=$scope.GetType().GetField('header',$flags).GetValue($scope); $bytes[0]=$bytes[0] -bxor 1}
        'facts' {$view.Write(80,[byte]($view.ReadByte(80) -bxor 1))}
        'pin' {$pins.GetType().GetField('Handles',$flags).GetValue($pins)[4].Dispose()}
        'cancel' {$worker.GetType().GetField('closed',$flags).GetValue($worker).Set() | Out-Null}
        'files-cancel' {$files.GetType().GetProperty('Cancelled',$flags).GetValue($files,$null).Set() | Out-Null}
        'facts-cancel' {$facts.GetType().GetProperty('Cancelled',$flags).GetValue($facts,$null).Set() | Out-Null}
        'dispose' {$owned.Dispose()}
        'budget' {$field=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]; $field.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*21))}
        default {throw 'Unknown source fault'}
    }
}
try {
    foreach($part in @('begin','attached','release')) {$signals[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.worker.'+$part)}
    if(-not $signals.begin.WaitOne(30000)) {throw 'Read validation begin deadline'}
    $worker=[NLink.Tools.LocalRetainedSourceRequest]::TryAttachFrontier($Name,$RequestId,$SourcePath,'frontier-gui-run','frontier-gui-scenario')
    if($null -eq $worker) {throw 'Original native read worker missing'}
    $owned=$worker.FrontierInputs; $reader=$worker.TryOpenSource()
    if($null -eq $reader) {throw 'Original native reader missing'}
    $files=$owned.GetType().GetField('files',$flags).GetValue($owned)
    $facts=$files.GetType().GetField('Facts',$flags).GetValue($files)
    $view=$facts.GetType().GetField('view',$flags).GetValue($facts)
    $pins=$owned.GetType().GetField('pins',$flags).GetValue($owned)
    $handles=@($pins.GetType().GetField('Handles',$flags).GetValue($pins))
    $work=$owned.GetType().GetField('work',$flags).GetValue($owned)
    $counts=$owned.GetType().GetField('PrimaryCheckCallsForTest',$flags).GetValue($owned)
    $hook=$owned.GetType().GetField('SourceReadProbeForTest',$flags)
    $phase,$fault=$mode.Split('-',2)
    $script:injected=0; $script:restored=0; $script:verified=0; $script:commits=0; $script:atEntry=0
    $expectedSuccess=$mode -cin @('valid','transient-facts')
    $hook.SetValue($owned,[Action[string]]{param($stage)
        if(-not $work.IsRunning) {throw 'Source checks escaped original work clock'}
        if($stage -ceq 'entry') {$script:atEntry=$counts[6]}
        if($stage -ceq 'commit') {$script:commits++}
        if($stage -ceq 'verified') {
            if($counts[6]-$script:atEntry -ne 1) {throw 'Line/EOF publication lost its single full validation'}
            $script:verified++
        }
        if($mode -ceq 'transient-facts') {
            if($stage -ceq 'fragment' -and $script:injected -eq 0) {Apply-ReadFault; $script:injected++}
            elseif($stage -ceq 'commit' -and $script:restored -eq 0) {Apply-ReadFault; $script:restored++}
        } elseif(-not $expectedSuccess -and $script:injected -eq 0) {
            $target=if($phase -ceq 'eof') {'commit'} else {$phase}
            if($stage -ceq $target -and ($phase -cne 'eof' -or $reader.LinesRead -eq $worker.SourceLines)) {Apply-ReadFault; $script:injected++}
        }
    })
    $cursor=[ReadValidationIteration]::new($reader); $lines=[Collections.Generic.List[string]]::new(); $failure=$null
    try {while($cursor.MoveNext()) {$lines.Add($cursor.Current)}} catch {$failure=$_}
    $hook.SetValue($owned,$null)
    if($expectedSuccess) {
        if($null -ne $failure -or -not $reader.InputReadComplete -or $lines.Count -ne $worker.SourceLines -or
            $script:verified -ne $lines.Count+1 -or $script:commits -ne $script:verified) {throw ('Native line publication/EOF changed: '+$failure)}
        if($mode -ceq 'transient-facts' -and ($script:injected -ne 1 -or $script:restored -ne 1)) {throw 'Approved transient-mutation control did not execute'}
        $references=$owned.GetType().GetField('sourceLines',$flags).GetValue($owned)
        $origins=[Collections.Generic.List[int]]::new()
        for($i=0;$i -lt $lines.Count;$i++) {
            if(-not [object]::ReferenceEquals($lines[$i],$references[$i])) {throw 'Native source string identity changed'}
            $origins.Add($reader.GetFrontierPhysicalOrigin($i+1,$lines[$i]))
        }
        if(@($origins | Where-Object {$_ -le 0}).Count) {throw 'Original physical origin missing'}
        'frontier-primary-source-origins='+($origins -join ',')
        if($owned.NativeWriterCustodyEstablished -or $owned.PublicationCompletionIssued -or $owned.LiveProofEligible) {throw 'Source validation acquired authority'}
    } else {
        $expectedLines=if($phase -ceq 'eof') {$worker.SourceLines} else {0}
        if($script:injected -ne 1 -or $null -eq $failure -or $lines.Count -ne $expectedLines -or
            $reader.LinesRead -ne $expectedLines -or $reader.InputReadComplete -or $worker.IsCurrent) {throw ('Fault published a line or completed source: '+$mode+' / '+$failure)}
        $close=$owned.GetType().GetField('PrimarySourceCloseForTest',$flags).GetValue($owned)
        if($close -ceq 'open' -or @($handles | Where-Object {-not $_.IsClosed}).Count) {throw 'Rejected line did not retire native pins'}
    }
    $null=$signals.attached.Set(); if(-not $signals.release.WaitOne(30000)) {throw 'Read validation release deadline'}
    $worker.Dispose()
    if($owned.IsCurrent -or $work.IsRunning -or @($handles | Where-Object {-not $_.IsClosed}).Count) {throw 'Read validation cleanup leaked work/pins'}
    'frontier-primary-worker='+$Case
    'frontier-primary-read-validation='+$mode
} finally {
    if($null -ne $cursor) {$cursor.Dispose()}; if($null -ne $reader) {$reader.Dispose()}; if($null -ne $worker) {$worker.Dispose()}
    foreach($event in $signals.Values) {$event.Dispose()}
}
