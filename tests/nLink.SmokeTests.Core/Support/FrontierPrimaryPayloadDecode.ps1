param([string]$RepoRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$decode=[NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs].GetMethod('DecodePayload',[Reflection.BindingFlags]'Static,NonPublic')
$sha=[Security.Cryptography.SHA256]::Create(); $utf8=[Text.UTF8Encoding]::new($false,$true); $checks=0
try {
    foreach($case in @('unicode','maximum','empty','over-cap','wrong-hash','short-hash','invalid-utf8','truncated-utf8','overlong-utf8','bom-preserved')) {
        $text='payload-'+[char]0x17e; $bytes=$utf8.GetBytes($text)
        switch($case) {
            'maximum' {$text='x'*262144; $bytes=$utf8.GetBytes($text)}
            'empty' {$bytes=[byte[]]@()}
            'over-cap' {$bytes=[byte[]]::new(262145)}
            'invalid-utf8' {$bytes=[byte[]]@(0xc3,0x28)}
            'truncated-utf8' {$bytes=[byte[]]@(0xe2,0x82)}
            'overlong-utf8' {$bytes=[byte[]]@(0xc0,0xaf)}
            'bom-preserved' {$text=[string][char]0xfeff+'payload'; $bytes=$utf8.GetBytes($text)}
        }
        $hash=$sha.ComputeHash($bytes)
        if($case -ceq 'wrong-hash') {$hash[0]=$hash[0] -bxor 1}
        if($case -ceq 'short-hash') {$hash=[byte[]]::new(31)}
        $result=$null
        try {$result=$decode.Invoke($null,@($bytes,$hash))} catch {
            $problem=$_.Exception
            while($null -ne $problem -and $problem -isnot [Text.DecoderFallbackException]) {$problem=$problem.InnerException}
            if($null -eq $problem) {throw}
        }
        $accepted=$case -cin @('unicode','maximum','bom-preserved')
        if(($null -ne $result) -ne $accepted -or ($accepted -and $result -cne $text)) {throw ('Payload primitive mismatch: '+$case)}
        $checks++
    }
    'frontier-primary-payload-decode='+$checks
} finally {$sha.Dispose()}
