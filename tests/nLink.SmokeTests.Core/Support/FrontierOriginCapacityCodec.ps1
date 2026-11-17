# Expanded packets exercise only the codec, never original native custody.
$offsets=[Collections.Generic.Dictionary[string,int]]::new()
foreach($key in $positions.Keys) {$offsets.Add($key,$positions[$key])}
$originChecks=0
foreach($case in @(
    @{N=131072; Bookmark=0; Counts=[int[]]@(131072,0,131072)},
    @{N=131073; Bookmark=0; Counts=[int[]]@(131073,0,131072)},
    @{N=101878; Bookmark=2; Counts=[int[]]@(101876,101878,73734)},
    @{N=262144; Bookmark=0; Counts=[int[]]@(262144,0,0)},
    @{N=262144; Bookmark=262144; Counts=[int[]]@(0,262144,0)},
    @{N=8; Bookmark=8; Counts=[int[]]@(0,0,0)})) {
    $packet=[NLink.Tests.FrontierOriginCapacityProbe]::Expand($body,$native,$offsets,$case.N,$case.Bookmark,$case.Counts)
    Test-FactBytes $packet.Body ('expanded-'+$case.N) $true $packet.Native
    $decoded=$decode.Invoke($null,[object[]]@($packet.Body,$packet.Native,'frontier-gui-run','frontier-gui-scenario',[Action]{}))
    $total=($case.Counts | Measure-Object -Sum).Sum
    $instance=[Reflection.BindingFlags]'Instance,NonPublic'
    if($codec.GetField('OriginCount',$instance).GetValue($decoded) -ne $total -or
        $codec.GetField('SourceLines',$instance).GetValue($decoded) -ne $case.N -or $packet.Body.Length -gt 4194304) {throw 'Codec count/byte round trip mismatch'}
    $encoded=$codec.GetField('bytes',$instance).GetValue($decoded)
    if([Convert]::ToBase64String($encoded) -cne [Convert]::ToBase64String($packet.Body)) {throw 'Codec changed the flat v1 bytes'}
    Test-FactBytes $encoded 'round-trip' $true $packet.Native
    $compact=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($packet.Body,$packet.Positions,$true)
    if($compact.Body.Length -ge $packet.Body.Length) {throw 'Delta encoding did not reduce bounded tables'}
    Test-FactBytes $compact.Body 'v2-expanded' $true $packet.Native
    $compactDecoded=$decode.Invoke($null,[object[]]@($compact.Body,$packet.Native,'frontier-gui-run','frontier-gui-scenario',[Action]{}))
    [NLink.Tests.FrontierOriginCapacityProbe]::SameTables($decoded,$compactDecoded)
    $flatAgain=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($compact.Body,$compact.Positions,$false)
    if([Convert]::ToBase64String($flatAgain.Body) -cne [Convert]::ToBase64String($packet.Body)) {throw 'Expanded v1/v2 round trip changed facts'}
    $implicit=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($compact.Body,$compact.Positions,[int]3)
    if($implicit.Body.Length -ne $compact.Body.Length-$case.Counts[0]) {throw 'Retained-origin wire reduction changed'}
    Test-FactBytes $implicit.Body 'v3-expanded' $true $packet.Native
    $implicitDecoded=$decode.Invoke($null,[object[]]@($implicit.Body,$packet.Native,'frontier-gui-run','frontier-gui-scenario',[Action]{}))
    [NLink.Tests.FrontierOriginCapacityProbe]::SameTables($decoded,$implicitDecoded)
    $v2Again=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($implicit.Body,$implicit.Positions,$true)
    if([Convert]::ToBase64String($v2Again.Body) -cne [Convert]::ToBase64String($compact.Body)) {throw 'Expanded v2/v3 round trip changed facts'}
    $fullWriter=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($implicit.Body,$implicit.Positions,[int]4)
    $writerReduction=if($case.Counts[1] -eq $case.N) {$case.Counts[1]} else {0}
    if($fullWriter.Body.Length -ne $implicit.Body.Length-$writerReduction) {throw 'Conditional full-writer wire reduction changed'}
    Test-FactBytes $fullWriter.Body 'v4-expanded' $true $packet.Native
    $fullWriterDecoded=$decode.Invoke($null,[object[]]@($fullWriter.Body,$packet.Native,'frontier-gui-run','frontier-gui-scenario',[Action]{}))
    [NLink.Tests.FrontierOriginCapacityProbe]::SameTables($decoded,$fullWriterDecoded)
    $v3Again=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($fullWriter.Body,$fullWriter.Positions,[int]3)
    if([Convert]::ToBase64String($v3Again.Body) -cne [Convert]::ToBase64String($implicit.Body)) {throw 'Expanded v3/v4 round trip changed facts'}
    if($case.Counts[2] -gt 0) {
        $origins=$codec.GetField('semanticOrigins',$instance).GetValue($decoded)
        for($i=0;$i -lt $origins.Length;$i++) {if($origins[$i] -ne $i+1) {throw 'Semantic physical origin changed'}}
    }
    # Reapply the former decoder's actual bounded-count reader to these counts.
    $readerType=$codec.GetNestedType('Reader',[Reflection.BindingFlags]'NonPublic')
    $legacyStream=[IO.MemoryStream]::new(); $legacyWriter=[IO.BinaryWriter]::new($legacyStream)
    try {
        foreach($count in $case.Counts) {$legacyWriter.Write([int]$count)}
        $oldReader=[Activator]::CreateInstance($readerType,$instance,$null,[object[]]@(,$legacyStream.ToArray()),$null)
        $refused=$false; $seen=0
        try {
            foreach($count in $case.Counts) {
                try {$seen+=$readerType.GetMethod('Count',$instance).Invoke($oldReader,[object[]]@([int](262144-$seen)))}
                catch {if($_.Exception.GetBaseException() -isnot [IO.InvalidDataException]) {throw}; $refused=$true; break}
            }
        } finally {$oldReader.Dispose()}
        if($refused -ne ($total -gt 262144)) {throw 'Legacy count-policy compatibility changed'}
    } finally {$legacyWriter.Dispose(); $legacyStream.Dispose()}
    $originChecks++
    if($case.N -ne 101878) {continue}
    $callbackCounts=@(foreach($wire in @($packet,$compact,$implicit,$fullWriter)) {
        $script:deltaChecks=0
        if($null -eq $decode.Invoke($null,[object[]]@($wire.Body,$packet.Native,'frontier-gui-run','frontier-gui-scenario',[Action]{$script:deltaChecks++}))) {throw 'Callback count control rejected'}
        $script:deltaChecks
    })
    if(@($callbackCounts | Sort-Object -Unique).Count -ne 1) {throw 'Versioned decoder callback cadence changed'}
    foreach($wire in @($compact,$implicit,$fullWriter)) {
        foreach($stop in @(3,27,28,52,53)) {
            $script:deltaChecks=0
            $cancel=[Action]{$script:deltaChecks++;if($script:deltaChecks -eq $stop) {throw 'cancelled-delta'}}
            if($null -ne $decode.Invoke($null,[object[]]@($wire.Body,$packet.Native,'frontier-gui-run','frontier-gui-scenario',$cancel)) -or $script:deltaChecks -ne $stop) {throw 'Delta decoder changed cancellation cadence'}
        }
    }
    foreach($fault in @('negative','zero-mismatch','role-over','source-over','aggregate-over','duplicate','regression','out-of-range','retained-bookmark','count-mismatch','truncated-count','truncated-origins','cancel')) {
        $candidate=[byte[]]$packet.Body.Clone()
        switch($fault) {
            'negative' {Set-FactNumber $candidate $packet.Counts[2] ([int]-1)}
            'zero-mismatch' {Set-FactNumber $candidate $packet.Counts[2] ([int]0)}
            'role-over' {Set-FactNumber $candidate $packet.Counts[2] ([int]262145); Set-FactNumber $candidate $packet.OutputLines[2] ([long]262145); Set-FactNumber $candidate $packet.OutputLines[3] ([long]262145)}
            'source-over' {Set-FactNumber $candidate $packet.Counts[2] ([int]($case.N+1)); Set-FactNumber $candidate $packet.OutputLines[2] ([long]($case.N+1)); Set-FactNumber $candidate $packet.OutputLines[3] ([long]($case.N+1))}
            'aggregate-over' {Set-FactNumber $candidate $packet.Counts[2] ([int]786433)}
            'duplicate' {Set-FactNumber $candidate ($packet.First[2]+4) ([int]1)}
            'regression' {Set-FactNumber $candidate ($packet.First[2]+8) ([int]1)}
            'out-of-range' {Set-FactNumber $candidate $packet.First[2] ([int]($case.N+1))}
            'retained-bookmark' {Set-FactNumber $candidate $packet.First[0] ([int]2)}
            'count-mismatch' {Set-FactNumber $candidate $packet.Counts[2] ([int]73733)}
            'truncated-count' {$candidate=[byte[]]::new($packet.Counts[2]+3); [Array]::Copy($packet.Body,$candidate,$candidate.Length)}
            'truncated-origins' {$candidate=[byte[]]::new($packet.First[2]+4*73734-1); [Array]::Copy($packet.Body,$candidate,$candidate.Length)}
            'cancel' {if($null -ne $decode.Invoke($null,[object[]]@($candidate,$packet.Native,'frontier-gui-run','frontier-gui-scenario',[Action]{throw 'cancelled'}))) {throw 'Cancelled decoder admitted packet'}; $originChecks++; continue}
        }
        if($fault -ne 'cancel') {Test-FactBytes $candidate $fault $false $packet.Native; $originChecks++}
    }
}
# A partial writer table must retain its exact leading, middle or trailing gap.
foreach($writerOrigins in @([int[]]@(2,3,4,5,6,7,8),[int[]]@(1,2,3,5,6,7,8),[int[]]@(1,2,3,4,5,6,7))) {
    $packet=[NLink.Tests.FrontierOriginCapacityProbe]::Expand($body,$native,$offsets,8,8,([int[]]@(0,7,0)))
    for($i=0;$i -lt $writerOrigins.Length;$i++) {Set-FactNumber $packet.Body ($packet.First[1]+4*$i) ([int]$writerOrigins[$i])}
    $v3Gap=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($packet.Body,$packet.Positions,[int]3)
    foreach($wireVersion in @(1,2,3,4)) {
        $wire=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($packet.Body,$packet.Positions,[int]$wireVersion)
        Test-FactBytes $wire.Body 'partial-writer-gap-preserved' $true $packet.Native
        $flatGap=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($wire.Body,$wire.Positions,[int]1)
        if([Convert]::ToBase64String($flatGap.Body) -cne [Convert]::ToBase64String($packet.Body)) {throw 'Partial writer gap was synthesized or lost'}
        if($wireVersion -eq 4 -and $wire.Body.Length -ne $v3Gap.Body.Length) {throw 'Partial writer table was compacted implicitly'}
    }
    $originChecks++
}
$invalidFull=[NLink.Tests.FrontierOriginCapacityProbe]::Expand($body,$native,$offsets,8,8,([int[]]@(0,8,0)))
Set-FactNumber $invalidFull.Body $invalidFull.First[1] ([int]2)
Test-FactBytes $invalidFull.Body 'invalid-full-writer-not-normalized' $false $invalidFull.Native
$rejected=$false
try {$null=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($invalidFull.Body,$invalidFull.Positions,[int]4)}
catch {if($_.Exception.GetBaseException().Message -ne 'Nonconsecutive implicit origin') {throw}; $rejected=$true}
if(-not $rejected) {throw 'Full writer transcoder discarded an invalid supplied origin'}
$dense=[NLink.Tests.FrontierOriginCapacityProbe]::Expand($body,$native,$offsets,262144,0,([int[]]@(262144,262144,262144)))
if($dense.Body.Length -le 4194304) {throw 'Dense maximum did not exceed byte envelope'}
Test-FactBytes $dense.Body 'dense-maximum-still-byte-bounded' $false $dense.Native
$compactDense=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($dense.Body,$dense.Positions,$true)
if($compactDense.Body.Length -ge 4194304) {throw 'Dense compact control not inside wire envelope'}
Test-FactBytes $compactDense.Body 'dense-v2-still-flat-byte-bounded' $false $dense.Native
$implicitDense=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($dense.Body,$dense.Positions,[int]3)
Test-FactBytes $implicitDense.Body 'dense-v3-still-flat-byte-bounded' $false $dense.Native
$fullWriterDense=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($dense.Body,$dense.Positions,[int]4)
Test-FactBytes $fullWriterDense.Body 'dense-v4-still-flat-byte-bounded' $false $dense.Native
$sparse=[NLink.Tests.FrontierOriginCapacityProbe]::Expand($body,$native,$offsets,262144,0,([int[]]@(262144,0,0)))
$boundaryWriter=[int][Math]::Floor((4194304-$sparse.Body.Length)/4)-1
$boundary=[NLink.Tests.FrontierOriginCapacityProbe]::Expand($body,$native,$offsets,262144,0,([int[]]@(262144,$boundaryWriter,0)))
foreach($target in @(4194303,4194304,4194305)) {
    $edge=[NLink.Tests.FrontierOriginCapacityProbe]::PadPath($boundary,($target-$boundary.Body.Length))
    if($edge.Body.Length -ne $target) {throw 'Logical boundary control size mismatch'}
    Test-FactBytes $edge.Body ('v1-byte-boundary-'+$target) ($target -le 4194304) $edge.Native
    $edgeCompact=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($edge.Body,$edge.Positions,$true)
    if($edgeCompact.Body.Length -ge 4194304) {throw 'Boundary compact control not inside wire envelope'}
    Test-FactBytes $edgeCompact.Body ('v2-logical-byte-boundary-'+$target) ($target -le 4194304) $edge.Native
    $edgeImplicit=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($edge.Body,$edge.Positions,[int]3)
    Test-FactBytes $edgeImplicit.Body ('v3-logical-byte-boundary-'+$target) ($target -le 4194304) $edge.Native
    $edgeFullWriter=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($edge.Body,$edge.Positions,[int]4)
    Test-FactBytes $edgeFullWriter.Body ('v4-logical-byte-boundary-'+$target) ($target -le 4194304) $edge.Native
}
# Charge omitted full-writer origins at the same exact flat-v1 byte boundary.
$fullSparse=[NLink.Tests.FrontierOriginCapacityProbe]::Expand($body,$native,$offsets,262144,262144,([int[]]@(0,262144,0)))
$boundarySemantic=[int][Math]::Floor((4194304-$fullSparse.Body.Length)/4)-1
$fullBoundary=[NLink.Tests.FrontierOriginCapacityProbe]::Expand($body,$native,$offsets,262144,262144,([int[]]@(0,262144,$boundarySemantic)))
foreach($target in @(4194303,4194304,4194305)) {
    $edge=[NLink.Tests.FrontierOriginCapacityProbe]::PadPath($fullBoundary,($target-$fullBoundary.Body.Length))
    $wire=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($edge.Body,$edge.Positions,[int]4)
    Test-FactBytes $wire.Body ('full-writer-flat-byte-boundary-'+$target) ($target -le 4194304) $edge.Native
}
$large=[byte[]]$body.Clone();Set-FactNumber $large $positions['source-bytes'] ([long]268435456)
$wide=[NLink.Tests.FrontierOriginCapacityProbe]::Expand($large,$native,$offsets,2,0,([int[]]@(2,0,0)))
$jump=[NLink.Tests.FrontierOriginCapacityProbe]::Expand($body,$native,$offsets,262144,262144,([int[]]@(0,0,1)))
Set-FactNumber $jump.Body $jump.First[2] ([int]262144)
foreach($packet in @($wide,$jump)) {
    Test-FactBytes $packet.Body 'wide-v1-values' $true $packet.Native
    $compact=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($packet.Body,$packet.Positions,$true)
    Test-FactBytes $compact.Body 'four-byte-offset-three-byte-origin' $true $packet.Native
    $a=$decode.Invoke($null,[object[]]@($packet.Body,$packet.Native,'frontier-gui-run','frontier-gui-scenario',[Action]{}))
    $b=$decode.Invoke($null,[object[]]@($compact.Body,$packet.Native,'frontier-gui-run','frontier-gui-scenario',[Action]{}))
    [NLink.Tests.FrontierOriginCapacityProbe]::SameTables($a,$b)
    $v4Wide=[NLink.Tests.FrontierOriginCapacityProbe]::Transcode($packet.Body,$packet.Positions,[int]4)
    Test-FactBytes $v4Wide.Body 'v4-wide-values' $true $packet.Native
    $v4Decoded=$decode.Invoke($null,[object[]]@($v4Wide.Body,$packet.Native,'frontier-gui-run','frontier-gui-scenario',[Action]{}))
    [NLink.Tests.FrontierOriginCapacityProbe]::SameTables($a,$v4Decoded)
}
'frontier-full-writer-origins-v4=ok; partial_gap_cases=3; legacy_versions=1,2,3; maximum_writer_count=262144; writer_cancel_checks=52,53'
'frontier-origin-delta-capacity=18'
'frontier-origin-capacity-codec='+($originChecks+1)
