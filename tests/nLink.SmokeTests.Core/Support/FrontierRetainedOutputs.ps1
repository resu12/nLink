param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $RepoRoot 'tools/GuiSmokeLogLedgerStreamingProcessor.cs')
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class FrontierOutputProbe {
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool WriteFile(SafeFileHandle h, byte[] b, uint n, out uint written, IntPtr overlapped);
    public static bool WriteDenied(SafeFileHandle h) {
        uint written; return !WriteFile(h, new byte[] { 1 }, 1, out written, IntPtr.Zero) && Marshal.GetLastWin32Error() == 5;
    }
}
public sealed class FrontierOutputSource : MemoryStream {
    private int observations;
    public FrontierOutputSource(byte[] bytes) : base(bytes, false) { }
    public override long Position {
        get { if (++observations > 1) throw new IOException("post_seal_source_fault"); return base.Position; }
        set { throw new NotSupportedException(); }
    }
}
'@
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$checks=[Collections.Generic.List[string]]::new()
foreach($mode in @('valid','empty','copied-capture','copied-lease','dispose','closed-handle','cancel','source-disposed','partial-create','short','extra','pre-cancelled','post-seal-failure')) {
    $root=Join-Path $Directory $mode; [void][IO.Directory]::CreateDirectory($root)
    $paths=@('retained','writer','semantic') | ForEach-Object { Join-Path $root ($_+'.log') }
    $line='event=operational_log_writer_started; acceptance_run_id=run; scenario_run_id=scenario; log_writer_id=writer; log_writer_sequence=1; log_writer_batch_generation=1; log_writer_batch_first_sequence=1; log_writer_batch_last_sequence=1; log_writer_attempt_utc_ticks=1'
    $input=[IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($line+"`n"),$false)
    $lifetime=[Threading.CancellationTokenSource]::new(); $capture=$null
    $count=if($mode -ceq 'short') {2L} else {1L}
    $bookmark=if($mode -ceq 'empty') {1L} else {0L}
    $events=if($mode -ceq 'empty') {[string[]]@('absent')} else {[string[]]@('operational_log_writer_started')}
    if($mode -ceq 'extra') { $input.Dispose(); $input=[IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($line+"`n"+$line+"`n"),$false) }
    if($mode -ceq 'partial-create') { [IO.File]::WriteAllText($paths[1],'sentinel') }
    if($mode -ceq 'pre-cancelled') { $lifetime.Cancel() }
    if($mode -ceq 'post-seal-failure') { $input.Dispose(); $input=[FrontierOutputSource]::new([Text.Encoding]::UTF8.GetBytes($line+"`n")) }
    try {
        $rejected=$false
        try {
            $result=[NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStreamWithRetainedOutputs(
                $input,$count,$bookmark,$paths[0],$paths[1],'run','scenario',[string[]]@(),$paths[2],$events,$lifetime.Token)
            $capture=$result.StreamCapture
        } catch {
            $rejected=$true
            if($mode -cnotin @('partial-create','short','extra','pre-cancelled','post-seal-failure')) { throw }
            if($mode -ceq 'post-seal-failure' -and $_.ToString() -notmatch 'post_seal_source_fault') { throw }
        }
        if($mode -cin @('partial-create','short','extra','pre-cancelled','post-seal-failure')) {
            if(-not $rejected -or $null -ne $capture) { throw 'Incomplete outputs were retained' }
            if($mode -ceq 'partial-create' -and [IO.File]::ReadAllText($paths[1]) -cne 'sentinel') { throw 'Existing output was overwritten' }
            if($mode -ceq 'pre-cancelled' -and [IO.Directory]::GetFiles($root).Length -ne 0) { throw 'Cancelled processing created outputs' }
        } else {
            if($null -eq $capture -or -not $capture.HasRetainedOutputs -or -not $result.OwnsStreamCapture($capture)) { throw 'Original retained outputs missing' }
            $lease=$capture.GetType().GetField('outputs',$flags).GetValue($capture)
            $files=$lease.GetType().GetField('files',$flags).GetValue($lease)
            if($files.Length -ne 3) { throw 'Wrong retained output handle bound' }
            for($i=0;$i -lt 3;$i++) {
                $file=$files[$i]
                if(-not $file.CanRead -or $file.CanWrite -or -not [FrontierOutputProbe]::WriteDenied($file.SafeFileHandle)) { throw 'Retained native handle is not read-only' }
                $blocked=$false
                try { $probe=[IO.File]::Open($paths[$i],[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete); $probe.Dispose() } catch [IO.IOException] { $blocked=$true }
                if(-not $blocked) { throw 'Retained output is externally writable' }
                $blocked=$false
                try { [IO.File]::Move($paths[$i],$paths[$i]+'.moved') } catch [IO.IOException] { $blocked=$true }
                if(-not $blocked) { throw 'Retained output is replaceable' }
                $blocked=$false
                try { [IO.File]::Delete($paths[$i]) } catch [IO.IOException] { $blocked=$true }
                if(-not $blocked) { throw 'Retained output is deletable' }
                $read=[IO.File]::Open($paths[$i],[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
                $hash=[Security.Cryptography.SHA256]::Create()
                try { $digest=[BitConverter]::ToString($hash.ComputeHash($read)).Replace('-','').ToLowerInvariant() } finally { $hash.Dispose(); $read.Dispose() }
                $description=$capture.(@('Retained','Writer','Semantic')[$i])
                if($description.Sha256 -cne $digest -or $file.Length -ne $description.ByteCount) { throw 'Retained bytes differ from original write capture' }
            }
            if($mode -ceq 'copied-capture') {
                $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($capture,$null)
                $copy.Dispose(); if($copy.IsCurrent -or $copy.HasRetainedOutputs -or -not $capture.HasRetainedOutputs) { throw 'Copied capture changed original custody' }
            }
            if($mode -ceq 'copied-lease') {
                $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($lease,$null)
                $copy.Dispose(); if(-not $capture.HasRetainedOutputs) { throw 'Copied lease closed original outputs' }
            }
            if($capture.LiveProofEligible -or $capture.NativeWriterCustodyEstablished -or $capture.PublicationCompletionIssued -or $null -ne $result.NativeCapture) { throw 'Output retention enabled authority' }
            if($mode -ceq 'closed-handle') { $files[1].Dispose() }
            elseif($mode -ceq 'cancel') { $lifetime.Cancel(); if(@($files | Where-Object {$_.CanRead}).Count) { throw 'Cancellation did not synchronously close outputs' } }
            elseif($mode -ceq 'source-disposed') { $input.Dispose() }
            else { $capture.Dispose(); $capture.Dispose() }
            if($capture.IsCurrent -or $capture.HasRetainedOutputs -or @($files | Where-Object {$_.CanRead}).Count) { throw 'Retired outputs remained current' }
        }
        foreach($path in $paths) { if([IO.File]::Exists($path)) { [IO.File]::AppendAllText($path,'released'); [IO.File]::Move($path,$path+'.released') } }
        $checks.Add($mode)
    } finally { if($null -ne $capture) { $capture.Dispose() }; $lifetime.Dispose(); $input.Dispose() }
}
'frontier-retained-outputs='+($checks.ToArray() | ConvertTo-Json -Compress)
