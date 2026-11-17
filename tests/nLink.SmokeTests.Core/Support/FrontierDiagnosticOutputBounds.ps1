param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$flags=[Reflection.BindingFlags]'Static,Instance,NonPublic'
$type=[NLink.Tools.LocalRetainedSourceRequest].Assembly.GetType('NLink.Tools.LocalFrontierDiagnosticChannel')
$open=$type.GetMethod('Open',$flags); $offer=$type.GetMethod('Offer',$flags); $adopt=$type.GetMethod('Adopt',$flags)
$commit=$type.GetMethod('Commit',$flags); $take=$type.GetMethod('Take',$flags); $cancel=$type.GetMethod('Cancel',$flags)
$check=[Action]{}
$validatorType=$adopt.GetParameters()[0].ParameterType
$validate=[Management.Automation.LanguagePrimitives]::ConvertTo([scriptblock]{param([byte[]]$bytes) $true},$validatorType)
foreach($case in @('maximum','overflow','empty','foreign-binding','initial-length','reserved','commit-tamper','state-only','cancel')) {
    $name='Local\frontier-output-bounds-'+[guid]::NewGuid().ToString('N')
    $binding=[byte[]]::new(32); $parent=$null; $worker=$null; $map=$null; $view=$null
    try {
        $parent=$open.Invoke($null,[object[]]@($name,$binding,$true))
        if($null -eq $parent) {throw 'Boundary parent creation failed'}
        if($case -ceq 'foreign-binding') {
            $foreign=[byte[]]::new(32); $foreign[0]=1
            if($null -ne $open.Invoke($null,[object[]]@($name,$foreign,$false))) {throw 'Foreign channel binding accepted'}
        }
        $worker=$open.Invoke($null,[object[]]@($name,$binding,$false))
        if($null -eq $worker) {throw 'Boundary worker open failed or foreign open cancelled original'}
        $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.diagnostic-v1'); $view=$map.CreateViewAccessor()
        $size=if($case -ceq 'maximum') {4194304} elseif($case -ceq 'overflow') {4194305} elseif($case -ceq 'empty') {0} else {97}
        $bytes=[byte[]]::new($size); if($size) {$bytes[$size-1]=51}
        if($case -ceq 'initial-length') {$view.Write(12L,1)}
        $offered=$offer.Invoke($worker,[object[]]@($bytes,$check))
        if($offered -eq ($case -cin @('overflow','empty','initial-length'))) {throw ('Boundary offer mismatch: '+$case)}
        if(-not $offered) {continue}
        if($case -ceq 'reserved') {$view.Write(144L,[byte]1)}
        $adopted=$adopt.Invoke($parent,[object[]]@($validate,$check))
        if($adopted -eq ($case -ceq 'reserved')) {throw ('Boundary adoption mismatch: '+$case)}
        if(-not $adopted) {continue}
        if($case -ceq 'commit-tamper') {$view.Write(80L,[byte]($view.ReadByte(80L) -bxor 1))}
        if($case -ceq 'state-only') {$view.Write(8L,4)} else {
            $committed=$commit.Invoke($worker,[object[]]@($check))
            if($committed -eq ($case -ceq 'commit-tamper')) {throw ('Boundary commit mismatch: '+$case)}
        }
        if($case -ceq 'cancel') {$cancel.Invoke($parent,$null)}
        $received=$take.Invoke($parent,[object[]]@($check))
        $expected=$case -cin @('maximum','foreign-binding')
        if(($null -ne $received) -ne $expected) {throw ('Boundary take mismatch: '+$case)}
        if($expected -and ($received.Length -ne $size -or $received[$size-1] -ne 51 -or $null -ne $take.Invoke($parent,[object[]]@($check)))) {throw 'Boundary exact bytes/one-shot mismatch'}
    } finally {
        if($null -ne $worker) {$worker.Dispose()}; if($null -ne $parent) {$parent.Dispose()}
        if($null -ne $view) {$view.Dispose()}; if($null -ne $map) {$map.Dispose()}
    }
}
$codec=[NLink.Tools.LocalRetainedSourceRequest].Assembly.GetType('NLink.Tools.LocalFrontierDiagnosticPacket')
$text=$codec.GetMethod('Text',$flags,$null,[type[]]@([IO.BinaryReader],[int]),$null)
foreach($case in @('negative','overflow','truncated','utf8','characters','valid')) {
    $stream=[IO.MemoryStream]::new(); $writer=[IO.BinaryWriter]::new($stream)
    $reader=$null
    try {
        [byte[]]$bytes=if($case -ceq 'utf8') {[byte[]]@(255)} else {[Text.Encoding]::UTF8.GetBytes('abcd')}
        $length=if($case -ceq 'negative') {-1} elseif($case -ceq 'overflow') {[int]::MaxValue} elseif($case -ceq 'truncated') {5} else {$bytes.Length}
        $writer.Write([int]$length); $writer.Write([byte[]]$bytes); $writer.Flush(); $stream.Position=0
        $reader=[IO.BinaryReader]::new($stream); $limit=if($case -ceq 'characters') {3} else {4}
        $rejected=$false; $result=$null
        try {$result=$text.Invoke($null,[object[]]@($reader,$limit))} catch {
            $cause=$_.Exception.GetBaseException()
            if($cause -isnot [InvalidOperationException] -and $cause -isnot [Text.DecoderFallbackException]) {throw}
            $rejected=$true
        }
        if($rejected -eq ($case -ceq 'valid') -or ($case -ceq 'valid' -and $result -cne 'abcd')) {throw ('Strict output UTF-8 boundary mismatch: '+$case)}
    } finally {if($null -ne $reader) {$reader.Dispose()}; $writer.Dispose(); $stream.Dispose()}
}
'frontier-diagnostic-output-bounds=verified'
