param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$assembly=[NLink.Tools.LocalRetainedSourceRequest].Assembly
$captureType=$assembly.GetType('NLink.Tools.LocalGuiWriterRequest+FrontierOutputCapture')
$outputType=[NLink.Tools.LocalRetainedSourceRequest+FrontierPrimaryOutput]
foreach($case in @('empty','ascii','utf8-bom','utf16-bom','invalid-utf8','split-unicode','trailing-newline')) {
    $capture=$null
    try {
        [byte[]]$bytes=switch($case) {
            'empty' {[byte[]]@()}
            'ascii' {[Text.Encoding]::UTF8.GetBytes('plain')}
            'utf8-bom' {[byte[]](@(239,187,191)+[Text.Encoding]::UTF8.GetBytes('caf'+[char]0xe9))}
            'utf16-bom' {[byte[]](@(255,254)+[Text.Encoding]::Unicode.GetBytes('line'+[char]0x20ac))}
            'invalid-utf8' {[byte[]]@(255,97,195)}
            'split-unicode' {[Text.Encoding]::UTF8.GetBytes(('x'*32767)+[char]::ConvertFromUtf32(0x1f642)+[char]0xe9)}
            'trailing-newline' {[Text.Encoding]::UTF8.GetBytes("line`r`n")}
        }
        if($null -eq $bytes) {$bytes=[byte[]]::new(0)}
        $prefix=Join-Path $Directory $case
        $capture=$captureType.GetConstructors($flags)[0].Invoke([object[]]@($prefix.PSObject.BaseObject))
        $target=$captureType.GetField('stdoutTarget',$flags).GetValue($capture)
        $target.Write($bytes,0,$bytes.Length); $target.Flush()
        $retained=$captureType.GetField('RecoveryOutputs',$flags).GetValue($capture)[0]
        if(-not $retained.GetType().GetMethod('Seal',$flags).Invoke($retained,[object[]]@($target))) {throw 'Encoding fixture seal failed'}
        $owner=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($outputType)
        $outputType.GetField('capture',$flags).SetValue($owner,$capture)
        $outputType.GetField('outputEncodings',$flags).SetValue($owner,[Text.Encoding[]]@([Text.Encoding]::UTF8,[Text.Encoding]::UTF8))
        $actual=$outputType.GetMethod('ReportHash',$flags).Invoke($owner,[object[]]@(0,[Action]{}))
        $memory=[IO.MemoryStream]::new($bytes,$false); $reader=[IO.StreamReader]::new($memory,[Text.Encoding]::UTF8,$true)
        try {$text=$reader.ReadToEnd()} finally {$reader.Dispose()}
        $path=$prefix+'-expected.txt'; Set-Content -LiteralPath $path -Value $text -Encoding UTF8
        $file=[IO.File]::OpenRead($path); $sha=[Security.Cryptography.SHA256]::Create()
        try {$expected=[BitConverter]::ToString($sha.ComputeHash($file)).Replace('-','').ToLowerInvariant()}
        finally {$sha.Dispose(); $file.Dispose()}
        if($actual -cne $expected) {throw ('Terminal encoding mismatch: '+$case)}
    } finally {if($null -ne $capture) {$captureType.GetMethod('Revoke',$flags).Invoke($capture,$null)}}
}
'frontier-terminal-encoding=7'
