param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class FrontierPersistenceProbe {
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool WriteFile(SafeFileHandle h, byte[] b, uint n, out uint written, IntPtr overlapped);
    public static bool WriteDenied(SafeFileHandle h) {
        uint written; return !WriteFile(h, new byte[] { 1 }, 1, out written, IntPtr.Zero) && Marshal.GetLastWin32Error() == 5;
    }
}
'@
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$clone=[object].GetMethod('MemberwiseClone',$flags)
$assembly=[NLink.Tools.LocalGuiWriterRequest].Assembly
$type=$assembly.GetType('NLink.Tools.LocalFrontierEvidenceFile',$true)
$constructor=$type.GetConstructors($flags)[0]; $persist=$type.GetMethod('TryPersist',$flags)
$isCurrent=$type.GetProperty('IsCurrent',$flags); $outputProperty=$type.GetProperty('Output',$flags)
function Read-PersistedFixture([string]$Path) {
    $read=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $copy=[IO.MemoryStream]::new()
    try {$read.CopyTo($copy); return ,$copy.ToArray()} finally {$copy.Dispose(); $read.Dispose()}
}
$checks=[Collections.Generic.List[string]]::new()
foreach($mode in @('valid','exact-bound','over-bound','empty','collision','directory','copied-resource','repeat','closed-file',
    'pre-cancelled','scope-false','mid-write','before-rename','after-rename','after-retain','callback-throws','dispose')) {
    $root=[IO.Path]::Combine($Directory,$mode); [void][IO.Directory]::CreateDirectory($root)
    $destination=[IO.Path]::Combine($root,'evidence.json')
    $bytes=[Text.Encoding]::UTF8.GetBytes('{"value":"original"}')
    if($mode -cin @('exact-bound','mid-write')) { $bytes=[Text.Encoding]::UTF8.GetBytes(('{}'+(' '*65534))) }
    if($mode -ceq 'over-bound') { $bytes=[byte[]]::new(65537) }
    if($mode -ceq 'empty') { $bytes=[byte[]]::new(0) }
    if($mode -ceq 'collision') { [IO.File]::WriteAllText($destination,'sentinel') }
    if($mode -ceq 'directory') { [void][IO.Directory]::CreateDirectory($destination) }
    $lifetime=[Threading.CancellationTokenSource]::new(); if($mode -ceq 'pre-cancelled') {$lifetime.Cancel()}
    $resource=$constructor.Invoke([object[]]@($lifetime.Token)); $state=[pscustomobject]@{Calls=0}
    $current=[Func[bool]]{
        $state.Calls++
        if($mode -ceq 'scope-false') {return $false}
        if($mode -ceq 'callback-throws' -and $state.Calls -eq 3) {throw 'fixture-callback-fault'}
        $cancelAt=if($mode -cin @('mid-write','before-rename')) {3} elseif($mode -ceq 'after-rename') {4} elseif($mode -ceq 'after-retain') {5} else {0}
        if($state.Calls -eq $cancelAt) {$lifetime.Cancel()}
        return -not $lifetime.IsCancellationRequested
    }
    try {
        if($mode -ceq 'copied-resource') {
            $copy=$clone.Invoke($resource,$null)
            if($persist.Invoke($copy,[object[]]@($destination,$bytes,$current))) {throw 'Copied resource persisted'}
            $copy.Dispose()
        }
        $success=$persist.Invoke($resource,[object[]]@($destination,$bytes,$current))
        if($mode -cin @('over-bound','empty','collision','directory','pre-cancelled','scope-false','mid-write','before-rename','after-rename','after-retain','callback-throws')) {
            if($success -or $isCurrent.GetValue($resource,$null)) {throw ('Invalid persistence survived: '+$mode)}
            if($mode -ceq 'collision' -and [IO.File]::ReadAllText($destination) -cne 'sentinel') {throw 'Existing evidence overwritten'}
            if($mode -cin @('mid-write','before-rename','callback-throws') -and [IO.File]::Exists($destination)) {throw 'Pre-rename failure published final path'}
            if($mode -cin @('after-rename','after-retain') -and -not [IO.File]::Exists($destination)) {throw 'Post-rename fault was not exercised'}
            if($mode -ceq 'mid-write') {
                $partial=@([IO.Directory]::GetFiles($root,'*.tmp'))
                if($partial.Count -ne 1 -or [IO.FileInfo]::new($partial[0]).Length -ne 32768) {throw 'Partial write not exercised'}
            }
        } else {
            if(-not $success -or -not $isCurrent.GetValue($resource,$null)) {throw ('Original persistence failed: '+$mode)}
            $output=$outputProperty.GetValue($resource,$null); $file=$type.GetField('file',$flags).GetValue($resource)
            $actual=Read-PersistedFixture $destination
            $hash=[Security.Cryptography.SHA256]::Create()
            try {$digest=[BitConverter]::ToString($hash.ComputeHash($actual)).Replace('-','').ToLowerInvariant()} finally {$hash.Dispose()}
            if([Convert]::ToBase64String($bytes) -cne [Convert]::ToBase64String($actual) -or $output.Sha256 -cne $digest -or
                $output.ByteCount -ne $bytes.Length -or $output.CreationPath -cne $destination -or $output.LineCount -ne 1 -or
                @([IO.Directory]::GetFiles($root,'*.tmp')).Count) {throw 'Persistence bytes/hash/rename mismatch'}
            if($file.CanWrite -or -not [FrontierPersistenceProbe]::WriteDenied($file.SafeFileHandle)) {throw 'Persisted native handle is writable'}
            foreach($operation in @('write','move','delete')) {
                $denied=$false
                try {
                    if($operation -ceq 'write') {$probe=[IO.File]::Open($destination,[IO.FileMode]::Open,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)); $probe.Dispose()}
                    elseif($operation -ceq 'move') {[IO.File]::Move($destination,($destination+'.moved'))}
                    else {[IO.File]::Delete($destination)}
                } catch [IO.IOException] {$denied=$true}
                if(-not $denied) {throw ('Persistence permitted '+$operation)}
            }
            if($mode -ceq 'repeat') {if($persist.Invoke($resource,[object[]]@(($destination+'.second'),$bytes,$current))) {throw 'Repeated persistence accepted'}}
            elseif($mode -ceq 'closed-file') {$file.Dispose()}
            elseif($mode -ceq 'dispose') {$resource.Dispose()}
            else {$lifetime.Cancel(); if($file.CanRead) {throw 'Cancellation retained evidence handle'}}
            if($isCurrent.GetValue($resource,$null) -or $file.CanRead) {throw 'Persistence resource revived'}
        }
        foreach($path in [IO.Directory]::GetFiles($root)) {[IO.File]::AppendAllText($path,'released')}
        $checks.Add($mode)
    } finally {$resource.Dispose(); $lifetime.Dispose()}
}
# Encoder limits and escaping are independent of native owner authority.
$jsonType=$assembly.GetType('NLink.Tools.FrontierEvidenceJson',$true)
foreach($mode in @('escape','character-bound','byte-bound','text-bound','invalid-utf8','culture')) {
    $json=[Activator]::CreateInstance($jsonType,$true); $failed=$false
    try {
        if($mode -ceq 'escape') {
            $value="quoted`" slash\ tab`t newline`n $([char]0xe9) $([char]::ConvertFromUtf32(0x1f642))"
            [void]$jsonType.GetMethod('Text',$flags).Invoke($json,[object[]]@($value))
            $encoded=$jsonType.GetMethod('Encode',$flags).Invoke($json,$null)
            if(([Text.Encoding]::UTF8.GetString($encoded) | ConvertFrom-Json) -cne $value) {throw 'JSON escaping mismatch'}
        } elseif($mode -ceq 'culture') {
            $old=[Threading.Thread]::CurrentThread.CurrentCulture
            try {[Threading.Thread]::CurrentThread.CurrentCulture=[Globalization.CultureInfo]::GetCultureInfo('ar-SA'); [void]$jsonType.GetMethod('Number',$flags).Invoke($json,[object[]]@(1234567L))}
            finally {[Threading.Thread]::CurrentThread.CurrentCulture=$old}
            if([Text.Encoding]::UTF8.GetString($jsonType.GetMethod('Encode',$flags).Invoke($json,$null)) -cne '1234567') {throw 'Culture-dependent number'}
        } else {
            try {
                if($mode -ceq 'character-bound') {[void]$jsonType.GetMethod('Add',$flags).Invoke($json,[object[]]@(('x'*65537)))}
                elseif($mode -ceq 'byte-bound') {[void]$jsonType.GetMethod('Add',$flags).Invoke($json,[object[]]@(([string][char]0xe9)*32769)); [void]$jsonType.GetMethod('Encode',$flags).Invoke($json,$null)}
                elseif($mode -ceq 'text-bound') {[void]$jsonType.GetMethod('Text',$flags).Invoke($json,[object[]]@(('x'*4097)))}
                else {[void]$jsonType.GetMethod('Text',$flags).Invoke($json,[object[]]@([string][char]0xd800)); [void]$jsonType.GetMethod('Encode',$flags).Invoke($json,$null)}
            } catch {
                $failure=$_.Exception.GetBaseException()
                if($mode -ceq 'invalid-utf8') {
                    if($failure -isnot [Text.EncoderFallbackException]) {throw}
                } else {
                    $expectedCode=if($mode -ceq 'character-bound') {'frontier_evidence_limit'} elseif($mode -ceq 'byte-bound') {'frontier_evidence_byte_limit'} else {'frontier_evidence_text_limit'}
                    if($failure -isnot [IO.InvalidDataException] -or $failure.Message -cne $expectedCode) {throw}
                }
                $failed=$true
            }
            if(-not $failed) {throw ('Encoder bound accepted: '+$mode)}
        }
    } catch {throw}
    $checks.Add('json-'+$mode)
}
'frontier-evidence-persistence='+($checks.ToArray() | ConvertTo-Json -Compress)
