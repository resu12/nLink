param([string]$RepoRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$type=[NLink.Tools.LocalInputReadLease].Assembly.GetType('NLink.Tools.FrontierLedger.GuiSmokeLedgerLfRead')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$ctor=$type.GetConstructor($flags,$null,[type[]]@([IO.Stream],[Action],[bool],[Action]),$null)
$read=$type.GetMethod('ReadLine',$flags)
$utf8=[Text.UTF8Encoding]::new($false,$true)
$script:cases=0
function Check-Frame([byte[]]$Bytes,[bool]$AllowFinal,[bool]$ExpectedSuccess,[string[]]$Expected) {
    $stream=[IO.MemoryStream]::new($Bytes,$false); $lines=[Collections.Generic.List[string]]::new(); $success=$true
    try {
        $reader=$ctor.Invoke(@($stream,[Action]{},$AllowFinal,$null))
        try {while($null -ne ($line=$read.Invoke($reader,$null))) {$lines.Add($line)}} catch {$success=$false}
        if($success -ne $ExpectedSuccess) {throw 'Framing success mismatch'}
        if($success) {
            if($lines.Count -ne $Expected.Count) {throw 'Framing count mismatch'}
            for($i=0;$i -lt $lines.Count;$i++) {if($lines[$i] -cne $Expected[$i]) {throw 'Framing content mismatch'}}
        }
        $script:cases++
    } finally {$stream.Dispose()}
}
Check-Frame ($utf8.GetBytes("a`nb`n")) $false $true @('a','b')
Check-Frame ($utf8.GetBytes("a`nb")) $false $false @()
Check-Frame ($utf8.GetBytes("a`r`nb")) $true $true @('a','b')
Check-Frame ($utf8.GetBytes("a`nb`n")) $true $true @('a','b')
Check-Frame ($utf8.GetBytes(([string][char]0xfeff)+"a`r`nb")) $true $true @('a','b')
Check-Frame ([byte[]]@()) $true $true @()
Check-Frame ([byte[]]@(97,10,195)) $true $false @()
Check-Frame ([byte[]]@(97,10,195,10)) $true $false @()
$maximum='a'*262144
Check-Frame ($utf8.GetBytes($maximum)) $true $true @($maximum)
Check-Frame ($utf8.GetBytes($maximum+'a')) $true $false @()
$split=('a'*32767)+[char]0xe9
Check-Frame ($utf8.GetBytes($split)) $true $true @($split)
$crlf='a'*32767
Check-Frame ($utf8.GetBytes($crlf+"`r`nb")) $true $true @($crlf,'b')
'frontier-primary-framing='+$script:cases
