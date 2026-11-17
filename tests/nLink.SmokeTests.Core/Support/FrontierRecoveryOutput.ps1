param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
Add-Type 'using System; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles; public static class RecoveryOutputNative { [DllImport("kernel32.dll", SetLastError=true)] public static extern bool WriteFile(SafeFileHandle file, byte[] bytes, uint count, out uint written, IntPtr overlapped); }'
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$type=[NLink.Tools.LocalGuiWriterRequest].Assembly.GetType('NLink.Tools.LocalGuiWriterRequest+FrontierRecoveryOutput')
$maximum=[long]$type.GetField('MaximumBytes',[Reflection.BindingFlags]'Static,NonPublic').GetRawConstantValue()
$checks=[Collections.Generic.List[string]]::new()
foreach($mode in @('empty','valid','maximum','over','revoked-before-seal','closed','copied')) {
    $length=if($mode -ceq 'empty') {0L} elseif($mode -ceq 'maximum') {$maximum} elseif($mode -ceq 'over') {$maximum+1} else {70003L}
    $path=Join-Path $Directory ($mode+'.bin')
    $target=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
    $capture=[Activator]::CreateInstance($type,$true)
    try {
        $target.SetLength($length); $target.Flush()
        if($mode -ceq 'revoked-before-seal') { [void]$type.GetMethod('Revoke',$flags).Invoke($capture,$null) }
        $accepted=$type.GetMethod('Seal',$flags).Invoke($capture,@($target))
        $target.Dispose()
        if($accepted -ne ($mode -cnotin @('over','revoked-before-seal'))) { throw "Wrong recovery retention bound: $mode" }
        $file=$type.GetField('file',$flags).GetValue($capture)
        if($accepted) {
            if($null -eq $file -or -not $file.CanRead -or $file.CanWrite -or $file.Length -ne $length) { throw 'Retained native file identity/access mismatch' }
            [uint32]$written=0
            if([RecoveryOutputNative]::WriteFile($file.SafeFileHandle,[byte[]]@(1),1,[ref]$written,[IntPtr]::Zero) -or
                [Runtime.InteropServices.Marshal]::GetLastWin32Error() -ne 5) { throw 'Retained native recovery handle permitted a kernel write' }
            if($mode -ceq 'copied') {
                $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($capture,$null)
                if($type.GetProperty('IsCurrent',$flags).GetValue($copy)) { throw 'Copied retention accepted' }
                [void]$type.GetMethod('Revoke',$flags).Invoke($copy,$null)
                if(-not $type.GetProperty('IsCurrent',$flags).GetValue($capture)) { throw 'Copied retention revoked original' }
            }
            if($mode -ceq 'closed') {
                $file.Dispose()
                if($type.GetProperty('IsCurrent',$flags).GetValue($capture)) { throw 'Closed native recovery file accepted' }
            }
        } elseif($null -ne $file) { throw 'Failed recovery seal retained a handle' }
    } finally { $target.Dispose(); [void]$type.GetMethod('Revoke',$flags).Invoke($capture,$null) }
    $probe=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $probe.Dispose(); $checks.Add($mode)
}
'frontier-recovery-output='+($checks -join ',')
