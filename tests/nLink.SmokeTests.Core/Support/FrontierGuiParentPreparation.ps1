$preparationCase=$mode.Substring('prepare-'.Length)
$preparation=$null
$capture=$result.StreamCapture
$lease=$capture.GetType().GetField('outputs',$flags).GetValue($capture)
$originalOutputs=$lease.GetType().GetField('files',$flags).GetValue($lease)
$evidenceResource=$evidence.GetType().GetField('Resource',$flags).GetValue($evidence)
$adopted=$adopter.GetType().GetField('adopted',$flags).GetValue($adopter)
$originalManifests=@($adopted | ForEach-Object {$_.GetType().GetField('Manifest',$flags).GetValue($_)})
$payloadCount=0
foreach($manifest in $originalManifests) {$payloadCount+=$manifest.GetType().GetField('Payloads',$flags).GetValue($manifest).Length}
$type=[NLink.Tools.LocalGuiWriterRequest+FrontierParentPreparation]
if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count -or
    $binding.GetType().GetMethod('TryPrepareParentFiles').GetParameters().Length) {throw 'Preparation fabrication exposed'}
try {
    if($preparationCase -ceq 'copied-binding') {
        $foreign=$clone.Invoke($binding,$null)
        if($null -ne $foreign.TryPrepareParentFiles()) {throw 'Copied binding prepared original files'}
        $foreign.Dispose(); if(-not $binding.IsCurrent) {throw 'Copied binding retired original'}
    }
    if($preparationCase -ceq 'missing-handoff') {$binding.GetType().GetField('parentHandoff',$flags).SetValue($binding,$null)}
    if($preparationCase -ceq 'closed-source') {$adopter.GetType().GetField('sealedSource',$flags).GetValue($adopter).Dispose()}
    if($preparationCase -ceq 'closed-payload') {
        $item=@($adopted | Where-Object {$_.GetType().GetField('Files',$flags).GetValue($_).Count -gt 1})[0]
        $item.GetType().GetField('Files',$flags).GetValue($item)[1].Dispose()
    }
    if($preparationCase -ceq 'closed-output') {$originalOutputs[0].Dispose()}
    if($preparationCase -ceq 'closed-copy') {$lease.GetType().GetField('semanticCopy',$flags).GetValue($lease).Dispose()}
    if($preparationCase -ceq 'closed-evidence') {$evidenceResource.GetType().GetField('file',$flags).GetValue($evidenceResource).Dispose()}
    if($preparationCase -ceq 'closed-recovery') {
        $readers=$binding.GetType().GetField('recoveryReaders',$flags).GetValue($binding)
        $resource=$readers[0].GetType().GetField('output',$flags).GetValue($readers[0])
        $resource.GetType().GetField('file',$flags).GetValue($resource).Dispose()
    }
    if($preparationCase -ceq 'mutable-result') {$result.Writers=@(); $result.SemanticSha256='foreign'; $result.SourceLineVisitCount=999}
    if($preparationCase -ceq 'budget') {
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*11))
    }
    $elapsedBefore=$work.ElapsedTicks
    $preparation=$binding.TryPrepareParentFiles()
    if($preparationCase -cin @('missing-handoff','closed-source','closed-payload','closed-output','closed-copy','closed-evidence','closed-recovery','budget')) {
        if($null -ne $preparation -or $binding.IsCurrent) {throw 'Incomplete preparation accepted'}
    } else {
        if($null -eq $preparation -or -not $preparation.IsCurrent -or -not $preparation.FilesPrepared -or
            $preparation.FileCount -ne 10+$payloadCount -or $preparation.PayloadCount -ne $payloadCount -or
            $preparation.LiveProofEligible -or $preparation.NativeWriterCustodyEstablished -or $preparation.PublicationCompletionIssued) {throw 'Complete original preparation missing/authority'}
        if($work.IsRunning -or $work.ElapsedTicks -le $elapsedBefore) {throw 'Preparation did not share work clock'}
        if(-not [object]::ReferenceEquals($type.GetField('Capture',$flags).GetValue($preparation),$capture) -or
            -not [object]::ReferenceEquals($type.GetField('Evidence',$flags).GetValue($preparation),$evidence) -or
            -not [object]::ReferenceEquals($type.GetField('Handoff',$flags).GetValue($preparation),$handoff) -or
            -not [object]::ReferenceEquals($type.GetField('Cursor',$flags).GetValue($preparation),$binding.CursorMatch) -or
            -not [object]::ReferenceEquals($type.GetField('Markers',$flags).GetValue($preparation),$binding.RecoveryMatch.Markers)) {throw 'Preparation reconstructed original facts'}
        $pins=$type.GetField('Files',$flags).GetValue($preparation)
        $entries=@($pins.GetType().GetField('entries',$flags).GetValue($pins))
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class FrontierPreparationProbe {
    [StructLayout(LayoutKind.Sequential)]
    private struct Info {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll")] private static extern bool GetFileInformationByHandle(SafeFileHandle h, out Info info);
    public static long[] Describe(FileStream file) {
        Info info; if (!GetFileInformationByHandle(file.SafeFileHandle, out info)) throw new IOException("describe_failed");
        return new long[] { file.SafeFileHandle.DangerousGetHandle().ToInt64(), info.Volume, info.IndexHigh, info.IndexLow, file.Length };
    }
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool WriteFile(SafeFileHandle h, byte[] b, uint n, out uint written, IntPtr overlapped);
    public static bool WriteDenied(SafeFileHandle h) {
        uint written; return !WriteFile(h, new byte[] { 1 }, 1, out written, IntPtr.Zero) && Marshal.GetLastWin32Error() == 5;
    }
}
'@
        $handles=@($entries | ForEach-Object {$_.GetType().GetField('Handle',$flags).GetValue($_)})
        foreach($handle in $handles) {if($handle.IsClosed -or -not [FrontierPreparationProbe]::WriteDenied($handle)) {throw 'Prepared pin has write access'}}
        $writers=$pins.GetType().GetProperty('Writers',$flags).GetValue($pins,$null)
        foreach($writer in $writers) {
            if(@($originalManifests | Where-Object {[object]::ReferenceEquals($_,$writer)}).Count -ne 1) {throw 'Preparation replaced original writer manifest'}
        }
        if($preparationCase -ceq 'gui-exit') {
            'frontier-gui-worker=native-gui-exit-with-preparation'
            [Environment]::Exit(0)
        }
        if($preparationCase -ceq 'primitive') {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPreparedFiles.ps1')
            if(-not $preparation.IsCurrent) {throw 'Primitive controls retired original preparation'}
        }
        if($preparationCase -ceq 'copied-preparation') {
            $foreign=$clone.Invoke($preparation,$null)
            if($foreign.IsCurrent -or $foreign.FilesPrepared -or $foreign.FileCount) {throw 'Copied preparation current'}
            $foreign.Dispose(); if(-not $preparation.IsCurrent) {throw 'Copied preparation retired original'}
        }
        if($preparationCase -ceq 'repeat') {if($null -ne $binding.TryPrepareParentFiles()) {throw 'Preparation repeated'}}
        elseif($preparationCase -ceq 'closed-pin') {$handles[0].Dispose()}
        elseif($preparationCase -ceq 'evidence-dispose') {$evidence.Dispose()}
        elseif($preparationCase -ceq 'handoff-dispose') {$handoff.Dispose()}
        elseif($preparationCase -ceq 'legacy-complete') {
            if(-not $finalizer.Complete('binding-legacy',[IO.Path]::Combine($Directory,'legacy-only.log'),3,0,
                [string[]]@(('a'*64),('b'*64),('c'*64)),
                [string[]]@('12345678:1234567812345678','12345678:1234567812345679','12345678:1234567812345680'),
                [long[]]@(12,12,4),[long[]]@(3,3,1),[string[]]@('writer'),[long[]]@(3))) {throw 'Preparation changed legacy completion'}
        }
        elseif($preparationCase -ceq 'complete') {if($finalizer.Complete('','',0,0,$null,$null,$null,$null,$null,$null)) {throw 'Invalid legacy completion accepted'}}
        elseif($preparationCase -ceq 'cancel') {
            $cancel=[Threading.EventWaitHandle]::OpenExisting($Name+'.frontier-parent-v1.cancelled')
            try {[void]$cancel.Set(); if($preparation.IsCurrent) {throw 'Cancelled preparation current'}} finally {$cancel.Dispose()}
        } else {$preparation.Dispose()}
        if($preparation.IsCurrent -or $preparation.FilesPrepared -or $preparation.FileCount -or @($handles | Where-Object {-not $_.IsClosed}).Count) {throw 'Prepared pins not retired together'}
    }
    if($binding.IsCurrent -or $proof.IsCurrent -or $capture.IsCurrent) {throw 'Preparation retained original inputs'}
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) {throw 'Preparation retained private payloads'}
    [IO.File]::AppendAllText($source,'preparation-released')
    [IO.File]::AppendAllText([IO.Path]::Combine($Directory,'filetransfer-frontier-finalizer-evidence.json'),'released')
} finally {if($null -ne $preparation) {$preparation.Dispose()}}
