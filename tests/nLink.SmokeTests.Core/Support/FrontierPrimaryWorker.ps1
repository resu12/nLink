param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$SourcePath,[string]$Case)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'; $clone=[object].GetMethod('MemberwiseClone',$flags)
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
public static class NativeWorkerProbe {
 [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
 [DllImport("kernel32.dll")] static extern bool DuplicateHandle(IntPtr p,SafeFileHandle h,IntPtr t,out SafeFileHandle d,uint a,bool i,uint o);
 [DllImport("kernel32.dll")] static extern bool WriteFile(SafeFileHandle h,byte[] b,int n,out int written,IntPtr o);
 public static bool WriteDenied(SafeFileHandle h) {int written; return !WriteFile(h,new byte[]{0},1,out written,IntPtr.Zero);}
 public static string Hash(SafeFileHandle h) {
  SafeFileHandle d; if(!DuplicateHandle(GetCurrentProcess(),h,GetCurrentProcess(),out d,0x80000000,false,0)) throw new Exception();
  using(var stream=new FileStream(d,FileAccess.Read)) using(var sha=SHA256.Create()) {
   if(stream.Length!=0) using(var map=MemoryMappedFile.CreateFromFile(stream,null,0,MemoryMappedFileAccess.Read,HandleInheritability.None,true)) {
    byte[] b=new byte[32768]; long position=0;
    while(position<stream.Length) {int count=(int)Math.Min(b.Length,stream.Length-position);
     using(var view=map.CreateViewAccessor(position,count,MemoryMappedFileAccess.Read)) if(view.ReadArray(0,b,0,count)!=count) throw new Exception();
     sha.TransformBlock(b,0,count,b,0); position+=count;
    }
   }
   sha.TransformFinalBlock(new byte[0],0,0); return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
  }
 }
}
'@
$signals=@{}; $worker=$null; $owned=$null; $pins=@()
try {
    foreach($part in @('begin','attached','release')) {$signals[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.worker.'+$part)}
    if(-not $signals.begin.WaitOne(30000)) {throw 'Worker begin deadline'}
    $run='frontier-gui-run'; $path=$SourcePath; $id=$RequestId
    if($Case -ceq 'wrong-request') {$id=[guid]::NewGuid()}
    if($Case -ceq 'wrong-run') {$run+='-wrong'}
    if($Case -ceq 'wrong-path') {$path+='-wrong'}
    $worker=[NLink.Tools.LocalRetainedSourceRequest]::TryAttachFrontier($Name,$id,$path,$run,'frontier-gui-scenario')
    $rejected=$Case -cin @('unbound','self-worker','preopened','prepare-budget','repeat-prepare','missing-offer','cancel-before',
        'file-binding','file-state','file-tail','facts-body','facts-binding','facts-length','facts-padding','native-handle','partial-pull',
        'wrong-request','wrong-run','wrong-path','worker-incarnation','parent-incarnation','parent-read-after-offer','existing-publication','native-topology')
    if(($null -eq $worker) -ne $rejected) {throw "Native worker attachment mismatch: $Case"}
    if($null -ne $worker) {
        $owned=$worker.FrontierInputs
        if($null -eq $owned -or -not $worker.IsCurrent -or -not $owned.IsCurrent -or $owned.FileCount -lt 10 -or
            $owned.SourceLineCount -le 0 -or $owned.LiveProofEligible -or $owned.NativeWriterCustodyEstablished -or $owned.PublicationCompletionIssued -or
            $owned.GetType().GetConstructors().Length -or @($owned.GetType().GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) {throw 'Native worker promoted authority or lacks complete set'}
        $resources=$owned.GetType().GetField('pins',$flags).GetValue($owned)
        $pins=@($resources.GetType().GetField('Handles',$flags).GetValue($resources))
        if($pins.Count -ne $owned.FileCount) {throw 'Worker native handle set differs'}
        foreach($pin in $pins) {if(-not [NativeWorkerProbe]::WriteDenied($pin)) {throw 'Worker imported write access'}}
        if([NativeWorkerProbe]::Hash($pins[4]) -cne $worker.SourceSha256) {throw 'Worker native source is not original primary input'}
        if($null -ne $worker.TryOpenWriter() -or $null -ne $worker.TryBeginPrimary($null,[object]::new(),[string[]]@(),[string[]]@())) {throw 'Attachment promoted writer path fallback/evaluation custody'}
        if($Case -ceq 'copied-worker') {
            $copy=$clone.Invoke($worker,$null)
            if($copy.IsCurrent -or $null -ne $copy.FrontierInputs) {throw 'Copied worker current'}
            $copy.Dispose(); if(-not $worker.IsCurrent) {throw 'Copied worker retired original'}
        }
        if($Case -ceq 'copied-worker-inputs') {
            $copy=$clone.Invoke($owned,$null)
            if($copy.IsCurrent -or $copy.FileCount) {throw 'Copied worker inputs current'}
            $copy.Dispose(); if(-not $owned.IsCurrent) {throw 'Copied inputs retired original'}
        }
        if($Case -ceq 'attach-replay' -and ($null -ne [NLink.Tools.LocalRetainedSourceRequest]::TryAttachFrontier($Name,$RequestId,$SourcePath,'frontier-gui-run','frontier-gui-scenario') -or -not $worker.IsCurrent)) {throw 'Worker attachment replay affected original'}
    }
    [void]$signals.attached.Set()
    if(-not $signals.release.WaitOne(30000)) {throw 'Worker review deadline'}
    if($null -ne $worker) {
        switch($Case) {
            'worker-dispose' {$worker.Dispose()}
            'inputs-dispose' {$owned.Dispose()}
            'post-worker-pin' {$pins[0].Dispose()}
            'worker-budget' {
                $watch=$owned.GetType().GetField('work',$flags).GetValue($owned)
                $elapsed=@($watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
                $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*21))
            }
        }
        $retired=$Case -cin @('post-scope','post-facts','post-parent-pin','parent-cancel','worker-dispose','inputs-dispose','worker-budget','post-worker-pin')
        if($Case -ceq 'parent-cancel') {
            if(-not [Threading.SpinWait]::SpinUntil([Func[bool]]{@($pins | Where-Object {-not $_.IsClosed}).Count -eq 0},5000)) {throw 'Idle cancellation failed to close worker pins'}
        }
        if($owned.IsCurrent -eq $retired -or $worker.IsCurrent -eq $retired) {throw 'Worker revocation/currentness mismatch'}
        $worker.Dispose()
        if($owned.IsCurrent -or @($pins | Where-Object {-not $_.IsClosed}).Count) {throw 'Worker disposal leaked native pins'}
    }
    'frontier-primary-worker='+$Case
} finally {
    if($null -ne $worker) {$worker.Dispose()}; if($null -ne $owned) {$owned.Dispose()}
    foreach($event in $signals.Values) {$event.Dispose()}
}
