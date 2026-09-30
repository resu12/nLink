# Expanded packets exercise only the codec, never original native custody.
Add-Type -Path (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierOriginCapacityProbe.cs')
$offsets=[Collections.Generic.Dictionary[string,int]]::new()
foreach($key in $positions.Keys) {$offsets.Add($key,$positions[$key])}
$originChecks=0
foreach($case in @(
    @{N=131072; Bookmark=0; Counts=[int[]]@(131072,0,131072)},
    @{N=131073; Bookmark=0; Counts=[int[]]@(131073,0,131072)},
    @{N=101878; Bookmark=2; Counts=[int[]]@(101876,101878,73734)},
    @{N=262144; Bookmark=0; Counts=[int[]]@(262144,0,0)},
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
$dense=[NLink.Tests.FrontierOriginCapacityProbe]::Expand($body,$native,$offsets,262144,0,([int[]]@(262144,262144,262144)))
if($dense.Body.Length -le 4194304) {throw 'Dense maximum did not exceed byte envelope'}
Test-FactBytes $dense.Body 'dense-maximum-still-byte-bounded' $false $dense.Native
'frontier-origin-capacity-codec='+($originChecks+1)
