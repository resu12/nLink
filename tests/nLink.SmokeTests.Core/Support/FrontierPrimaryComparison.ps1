param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading;
public sealed class PrimaryScopeCall {
    private readonly Thread thread;
    private readonly ManualResetEvent entered = new ManualResetEvent(false);
    private volatile bool done;
    private bool result;
    private Exception error;
    public PrimaryScopeCall(object scope, bool dispose) {
        thread = new Thread(() => {
            try {
                entered.Set();
                if (dispose) { ((IDisposable)scope).Dispose(); result = true; }
                else result = (bool)scope.GetType().GetProperty("IsCurrent", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(scope, null);
            } catch (Exception e) { error = e; }
            finally { done = true; }
        });
        thread.IsBackground = true; thread.Start();
    }
    public void AwaitBlocked() {
        if (!entered.WaitOne(10000) || !SpinWait.SpinUntil(() => done || (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, 10000) || done)
            throw new InvalidOperationException("Scope operation did not wait for its comparison/disposal gate");
    }
    public bool Finish() {
        if (!thread.Join(10000)) throw new InvalidOperationException("Scope operation did not finish after gate release");
        entered.Dispose();
        if (error != null) throw new InvalidOperationException("Scope operation failed", error);
        return result;
    }
}
'@
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
        $scratch=$type.GetField('current',$flags).GetValue($scope)
        if($scratch.Length -ne 64+$length) {throw 'Scope scratch length changed'}
        [Array]::Clear($scratch,0,$scratch.Length)
        if(-not $current.GetValue($scope,$null)) {throw 'Scope did not overwrite its full scratch buffer'}
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
        if(-not [object]::ReferenceEquals($scratch,$type.GetField('current',$flags).GetValue($scope))) {throw 'Scope scratch buffer was replaced'}
        if($length -eq 32768) {
            $gate=$type.GetField('sync',$flags).GetValue($scope)
            [Threading.Monitor]::Enter($gate)
            try {
                $first=[PrimaryScopeCall]::new($scope,$false);$second=[PrimaryScopeCall]::new($scope,$false)
                $first.AwaitBlocked();$second.AwaitBlocked()
                $view.Write(63+$length,[byte]($body[$length-1] -bxor 1))
            } finally {[Threading.Monitor]::Exit($gate)}
            $firstResult=$first.Finish();$secondResult=$second.Finish()
            if($firstResult -or $secondResult) {throw 'Overlapping scope checks reused a prior successful comparison'}
            $view.Write(63+$length,$body[$length-1])
            if(-not $current.GetValue($scope,$null)) {throw 'Restored scope comparison failed after overlap'}
            [Threading.Monitor]::Enter($gate)
            try {
                $retirement=[PrimaryScopeCall]::new($scope,$true);$retirement.AwaitBlocked()
                if(-not $current.GetValue($scope,$null)) {throw 'Pending disposal bypassed the held comparison gate'}
            } finally {[Threading.Monitor]::Exit($gate)}
            if(-not $retirement.Finish() -or $current.GetValue($scope,$null)) {throw 'Serialized disposal left scope current'}
            'native-primary-scope-overlap-disposal-ok'
        }
    } finally {$scope.Dispose();$view.Dispose();$map.Dispose()}
    if($current.GetValue($scope,$null)) {throw 'Disposed scope current'}
}
'native-primary-comparisons-ok'
