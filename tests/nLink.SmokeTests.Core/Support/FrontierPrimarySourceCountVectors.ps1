param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
# Primitive decoder parity only. This reflection fixture issues no request,
# transferred custody, evaluation, diagnostic output or terminal capability.
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$hashMethod=[NLink.Tools.LocalInputReadLease].GetMethod('HashFrontier',[Reflection.BindingFlags]'Static,NonPublic')
foreach($case in @('empty','ascii','utf8-bom','utf16-bom','invalid-utf8','split-unicode','trailing-newline','mixed-newlines','blank-lines','unterminated')) {
    [byte[]]$bytes=switch($case) {
        'empty' {[byte[]]@()}
        'ascii' {[Text.Encoding]::UTF8.GetBytes('plain')}
        'utf8-bom' {[byte[]](@(239,187,191)+[Text.Encoding]::UTF8.GetBytes("caf$([char]0xe9)`nnext"))}
        'utf16-bom' {[byte[]](@(255,254)+[Text.Encoding]::Unicode.GetBytes("line$([char]0x20ac)`r`nnext"))}
        'invalid-utf8' {[byte[]]@(97,10,255,97,195)}
        'split-unicode' {[Text.Encoding]::UTF8.GetBytes(('x'*32767)+[char]::ConvertFromUtf32(0x1f642)+"`n"+[char]0xe9)}
        'trailing-newline' {[Text.Encoding]::UTF8.GetBytes("line`r`n")}
        'mixed-newlines' {[Text.Encoding]::UTF8.GetBytes("one`rtwo`nthree`r`nfour")}
        'blank-lines' {[Text.Encoding]::UTF8.GetBytes("`r`n`n`r")}
        'unterminated' {[Text.Encoding]::UTF8.GetBytes("one`ntwo")}
    }
    if($null -eq $bytes) {$bytes=[byte[]]::new(0)}
    $path=Join-Path $Directory ($case+'.log'); [IO.File]::WriteAllBytes($path,$bytes)
    $expected=0L; $expectedFailed=$false
    # The unchanged pathname implementation is the independent baseline.
    try {$expected=Get-FileTransferEvidenceLineCount -Path $path} catch {$expectedFailed=$true}
    $pin=$null; $hashed=$null
    try {
        $pin=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
        $sha=[Security.Cryptography.SHA256]::Create()
        try {$digest=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
        $output=[NLink.Tools.LocalGuiWriterRequest+Output].GetConstructors($flags)[0].Invoke([object[]]@($path.PSObject.BaseObject,$digest.PSObject.BaseObject,$null,[long]$bytes.Length,99999L))
        $hashed=$hashMethod.Invoke($null,[object[]]@($pin.SafeFileHandle,$output,[Func[bool]]{$true}))
        if($null -eq $hashed -or $null -eq $hashed.Lease) {throw 'Native decoder primitive hash failed'}
        $pin.Position=7L; $actual=0L; $actualFailed=$false
        try {$actual=Get-FileTransferEvidenceLineCount -Path $path -ReadLease $hashed.Lease} catch {$actualFailed=$true}
        if($actualFailed -ne $expectedFailed -or (-not $actualFailed -and $actual -ne $expected) -or $pin.Position -ne 7L) {throw ('Count decoder parity: '+$case)}
        if(-not $hashed.Lease.IsVerified) {throw 'Count changed original source hash lease'}
    } finally {if($null -ne $hashed) {$hashed.Lease.Dispose()}; if($null -ne $pin) {$pin.Dispose()}}
    $released=[IO.File]::OpenWrite($path); $released.Dispose()
}
foreach($args in @(@{ReadLease=[pscustomobject]@{IsVerified=$true}},@{ReadLease=[pscustomobject]@{};OwnedSource=[pscustomobject]@{}})) {
    $rejected=$false
    try {$null=Get-FileTransferEvidenceLineCount -Path $path @args} catch {$rejected=$true}
    if(-not $rejected) {throw 'Count accepted forged/conflicting sources'}
}
'frontier-primary-count-vectors=10'
