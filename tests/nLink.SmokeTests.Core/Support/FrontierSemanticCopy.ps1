param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $RepoRoot 'tools/GuiSmokeLogLedgerStreamingProcessor.cs')
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class FrontierCopyProbe {
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool WriteFile(SafeFileHandle h, byte[] b, uint n, out uint written, IntPtr overlapped);
    public static bool WriteDenied(SafeFileHandle h) {
        uint written; return !WriteFile(h, new byte[] { 1 }, 1, out written, IntPtr.Zero) && Marshal.GetLastWin32Error() == 5;
    }
}
'@
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$clone=[object].GetMethod('MemberwiseClone',$flags)
$checks=[Collections.Generic.List[string]]::new()
function Read-CopyFixtureBytes([string]$Path) {
    $read=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $bytes=[IO.MemoryStream]::new()
    try { $read.CopyTo($bytes); return [Convert]::ToBase64String($bytes.ToArray()) }
    finally { $bytes.Dispose(); $read.Dispose() }
}
foreach($mode in @('valid','empty','multiblock','separator-boundary','unicode','collision','directory','hash','lines','origins','bytes',
    'origin-count-over','origin-count-negative','closed-input','copied-capture','copied-lease','repeat','cancel-before','cancel-mid','closed-copy','dispose')) {
    $root=[IO.Path]::Combine($Directory,$mode); [void][IO.Directory]::CreateDirectory($root)
    $text="event=copy_fixture; value=one`nevent=copy_fixture; value=two`n"
    if($mode -cin @('multiblock','cancel-mid')) { $text=(('event=copy_fixture; value='+('x'*70000))+"`n") }
    if($mode -ceq 'separator-boundary') { $text=(('event=copy_fixture;'+('x'*(32767-19)))+"`nevent=copy_fixture`n") }
    if($mode -ceq 'unicode') { $text="event=copy_fixture; value=$([char]0xe9)$([char]0x20ac)$([char]::ConvertFromUtf32(0x1f642))`n" }
    $input=[IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($text),$false)
    $lifetime=[Threading.CancellationTokenSource]::new(); $capture=$null
    $destination=[IO.Path]::Combine($root,'copy.log')
    $events=if($mode -ceq 'empty') {[string[]]@()} else {[string[]]@('copy_fixture')}
    $lines=@($text.ToCharArray() | Where-Object {$_ -eq "`n"}).Count
    try {
        $result=[NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStreamWithRetainedOutputs(
            $input,$lines,0,[IO.Path]::Combine($root,'retained'),[IO.Path]::Combine($root,'writer'),'run','scenario',[string[]]@(),
            [IO.Path]::Combine($root,'semantic'),$events,$lifetime.Token)
        $capture=$result.StreamCapture; $original=$capture.Semantic
        $lease=$capture.GetType().GetField('outputs',$flags).GetValue($capture)
        $files=$lease.GetType().GetField('files',$flags).GetValue($lease)
        $method=$capture.GetType().GetMethod('CopySemantic',$flags)
        if($mode -ceq 'collision') { [IO.File]::WriteAllText($destination,'sentinel') }
        if($mode -ceq 'directory') { [void][IO.Directory]::CreateDirectory($destination) }
        if($mode -ceq 'hash') { $original.GetType().GetProperty('Sha256').SetValue($original,('0'*64),$null) }
        if($mode -ceq 'lines') { $original.GetType().GetProperty('LineCount').SetValue($original,99L,$null) }
        if($mode -ceq 'bytes') { $original.GetType().GetProperty('ByteCount').SetValue($original,999999999L,$null) }
        if($mode -ceq 'origins') { $capture.SemanticOrigins.GetType().GetField('list',$flags).GetValue($capture.SemanticOrigins).Clear() }
        if($mode -ceq 'closed-input') { $files[2].Dispose() }
        if($mode -ceq 'cancel-before') { $lifetime.Cancel() }
        $state=[pscustomobject]@{Calls=0}
        $current=[Func[bool]]{ $state.Calls++; if($mode -ceq 'cancel-mid' -and $state.Calls -eq 4) { $lifetime.Cancel() }; return -not $lifetime.IsCancellationRequested }
        if($mode -ceq 'copied-capture') {
            if($null -ne $method.Invoke($clone.Invoke($capture,$null),[object[]]@($destination,$current)) -or -not $capture.IsCurrent) { throw 'Copied capture copied/retired original' }
        }
        if($mode -ceq 'copied-lease') {
            $copy=$clone.Invoke($lease,$null)
            if($null -ne $copy.GetType().GetMethod('CopySemantic',$flags).Invoke($copy,[object[]]@($destination,$original,[int]$capture.SemanticOrigins.Count,$current)) -or -not $capture.IsCurrent) { throw 'Copied lease copied/retired original' }
        }
        if($mode -cin @('origin-count-over','origin-count-negative')) {
            $count=if($mode -ceq 'origin-count-over') {262145} else {-1}
            $original.GetType().GetProperty('LineCount').SetValue($original,[long]$count,$null)
            $output=$lease.GetType().GetMethod('CopySemantic',$flags).Invoke($lease,[object[]]@($destination,$original,[int]$count,$current))
            if($state.Calls -ne 0 -or [IO.File]::Exists($destination)) {throw 'Semantic count rejection reached copying'}
        } else { $output=$method.Invoke($capture,[object[]]@($destination,$current)) }
        if($mode -cin @('collision','directory','hash','lines','origins','bytes','origin-count-over','origin-count-negative','closed-input','cancel-before','cancel-mid')) {
            if($null -ne $output -or $capture.IsCurrent -or @($files | Where-Object {$_.CanRead}).Count) { throw ('Invalid copy survived: '+$mode) }
            if($mode -ceq 'collision' -and [IO.File]::ReadAllText($destination) -cne 'sentinel') { throw 'Collision overwritten' }
            if($mode -ceq 'cancel-mid' -and ([IO.FileInfo]::new($destination).Length -le 0 -or [IO.FileInfo]::new($destination).Length -ge $original.ByteCount)) { throw 'Mid-copy cancellation did not exercise partial output' }
        } else {
            if($null -eq $output -or -not $capture.IsCurrent -or $output.Sha256 -cne $original.Sha256 -or $output.ByteCount -ne $original.ByteCount -or
                $output.LineCount -ne $original.LineCount -or $output.FileIdentity -ceq $original.FileIdentity) { throw ('Exact copy missing: '+$mode) }
            if((Read-CopyFixtureBytes $destination) -cne (Read-CopyFixtureBytes $original.CreationPath)) { throw 'Copied bytes differ' }
            $owns=$capture.GetType().GetMethod('OwnsSemanticCopy',$flags)
            if(-not $owns.Invoke($capture,[object[]]@($output)) -or $owns.Invoke($capture,[object[]]@($clone.Invoke($output,$null)))) { throw 'Copy accepted foreign metadata' }
            $file=$lease.GetType().GetField('semanticCopy',$flags).GetValue($lease)
            if($file.CanWrite -or -not [FrontierCopyProbe]::WriteDenied($file.SafeFileHandle)) { throw 'Copy native handle writable' }
            foreach($action in @('write','move','delete')) {
                $denied=$false
                try {
                    if($action -ceq 'write') { $probe=[IO.File]::Open($destination,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite); $probe.Dispose() }
                    elseif($action -ceq 'move') { [IO.File]::Move($destination,$destination+'.moved') }
                    else { [IO.File]::Delete($destination) }
                } catch [IO.IOException] { $denied=$true }
                if(-not $denied) { throw ('Copy permitted '+$action) }
            }
            if($mode -ceq 'repeat') { if($null -ne $method.Invoke($capture,[object[]]@(($destination+'.second'),$current))) { throw 'Repeated copy accepted' } }
            elseif($mode -ceq 'closed-copy') { $file.Dispose() }
            elseif($mode -ceq 'dispose') { $capture.Dispose() }
            else { $lifetime.Cancel(); if($file.CanRead) { throw 'Cancellation retained copy handle' } }
            if($capture.IsCurrent -or $file.CanRead) { throw 'Copy retirement failed' }
        }
        foreach($path in [IO.Directory]::GetFiles($root)) { [IO.File]::AppendAllText($path,'released') }
        $checks.Add($mode)
    } finally { if($null -ne $capture) {$capture.Dispose()}; $lifetime.Dispose(); $input.Dispose() }
}
'frontier-semantic-copy='+($checks.ToArray() | ConvertTo-Json -Compress)
