param([string]$RepoRoot,[string]$ArtifactRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeInputCapture.ps1')
$roles=@('fullCapture','fullSnapshot','compactCapture','compactSnapshot')
function Fill($Owner,[string[]]$Lines) {
    foreach($role in $roles) {
        if(-not $Owner.BeginInput($role,$Lines.Length)) { throw "Begin failed: $($Owner.OmissionReason)" }
        for($i=0; $i -lt $Lines.Length; $i++) { if(-not $Owner.AppendLine($i+1,$Lines[$i])) { throw "Append failed: $($Owner.OmissionReason)" } }
        if(-not $Owner.EndInput()) { throw "End failed: $($Owner.OmissionReason)" }
    }
    return $Owner.Take()
}
$vectors=@(
    @{Name='empty';Lines=[string[]]@();Hash='e0d5da8ece8aa85f55de0491cfa4a8a0b0024f40b01d5474a499cc244679e670'},
    @{Name='blank';Lines=[string[]]@('');Hash='17e6c97aa6bc619bdfc959ad84158419aee8404a32d0bc7ff0df90189d31b644'},
    @{Name='pair';Lines=[string[]]@('A','B');Hash='53b9fc58d0134ef0a2f9ec7563a61bd21a11401c87e0432cb5589b268cbc2f54'},
    @{Name='embedded-lf';Lines=[string[]]@("A`nB");Hash='cf7ab64f2259cc6c4ba0684b4c53db8341eef207144fc23643a1707aaa3dc56a'},
    @{Name='surrogate';Lines=[string[]]@([string][char]0xd800);Hash='33a66b724245f06e5f2531f74081467d22b6a34a4195b763d3ce851406c3706f'}
)
$rows=[Collections.Generic.List[object]]::new()
foreach($vector in $vectors) {
    $request=[guid]::NewGuid(); $owner=[NLink.Tools.LocalNativeInputCapture]::new($request)
    try {
        $result=Fill $owner $vector.Lines
        if($null -eq $result -or $result.RequestId -ne $request -or -not $owner.Owns($result)) { throw 'Owner binding missing' }
        foreach($role in $roles) {
            $input=$result.Get($role)
            if($input.Role -cne $role -or $input.RecordCount -ne $vector.Lines.Count -or $input.Sha256 -cne $vector.Hash) { throw 'Pinned native input changed' }
        }
        if($null -ne $result.Get('writer') -or $null -ne $result.Get('FullCapture') -or $null -ne $owner.Take()) { throw 'Role or one-shot contract changed' }
        $expectedUnits=0L; foreach($line in $vector.Lines) { $expectedUnits+=$line.Length }
        $prefixLength=[Text.Encoding]::ASCII.GetByteCount([NLink.Tools.LocalNativeInputCapture]::Domain+[char]0)
        $expectedBytes=4*($prefixLength+8+8*$vector.Lines.Length+2*$expectedUnits)
        if($owner.RecordVisits -ne 4*$vector.Lines.Length -or $owner.CodeUnitVisits -ne 4*$expectedUnits -or
            $owner.FramedByteVisits -ne $expectedBytes) { throw 'Work accounting changed' }
        if($vector.Lines.Length) { $vector.Lines[0]='changed-after-capture' }
        if($result.Get('fullCapture').Sha256 -cne $vector.Hash) { throw 'Detached digest changed with source array' }
        $other=[NLink.Tools.LocalNativeInputCapture]::new($request)
        try { $otherResult=Fill $other ([string[]]@()); if($owner.Owns($otherResult) -or $other.Owns($result)) { throw 'Equal request IDs borrowed ownership' } }
        finally { $other.Dispose() }
        $owner.Cancel()
        if($owner.Owns($result) -or $null -ne $owner.Take() -or $owner.BeginInput('fullCapture',0)) { throw 'Cancelled owner revived' }
        $rows.Add([ordered]@{Case=$vector.Name;Sha256=$vector.Hash;RecordVisits=$owner.RecordVisits;CodeUnitVisits=$owner.CodeUnitVisits;FramedByteVisits=$owner.FramedByteVisits})
    }
    finally { $owner.Dispose() }
}
$controls=@(
    @{Name='unknown-role';Action={param($o) $o.BeginInput('writer',0)}},
    @{Name='role-case';Action={param($o) $o.BeginInput('FullCapture',0)}},
    @{Name='negative-count';Action={param($o) $o.BeginInput('fullCapture',-1)}},
    @{Name='record-limit';Action={param($o) $o.BeginInput('fullCapture',262145)}},
    @{Name='append-without-begin';Action={param($o) $o.AppendLine(1,'A')}},
    @{Name='end-without-begin';Action={param($o) $o.EndInput()}},
    @{Name='nested-begin';Action={param($o) [void]$o.BeginInput('fullCapture',0); $o.BeginInput('fullSnapshot',0)}},
    @{Name='duplicate-role';Action={param($o) [void]$o.BeginInput('fullCapture',0); [void]$o.EndInput(); $o.BeginInput('fullCapture',0)}},
    @{Name='ordinal';Action={param($o) [void]$o.BeginInput('fullCapture',1); $o.AppendLine(2,'A')}},
    @{Name='extra-line';Action={param($o) [void]$o.BeginInput('fullCapture',0); $o.AppendLine(1,'A')}},
    @{Name='missing-line';Action={param($o) [void]$o.BeginInput('fullCapture',1); $o.EndInput()}},
    @{Name='null-line';Action={param($o) [void]$o.BeginInput('fullCapture',1); $o.GetType().GetMethod('AppendLine').Invoke($o,[object[]]@(1,$null))}},
    @{Name='line-limit';Action={param($o) [void]$o.BeginInput('fullCapture',1); $o.AppendLine(1,('x'*262145))}},
    @{Name='cancel-before-start';Action={param($o) $o.Cancel(); $o.BeginInput('fullCapture',0)}},
    @{Name='cancel-in-progress';Action={param($o) [void]$o.BeginInput('fullCapture',1); $o.Cancel(); $o.AppendLine(1,'A')}},
    @{Name='dispose-before-start';Action={param($o) $o.Dispose(); $o.BeginInput('fullCapture',0)}},
    @{Name='active-budget';Action={param($o)
        # Controlled clock-state fault injection, not a measured throughput claim.
        $o.GetType().GetField('activeTicks',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($o,
            [long]([Diagnostics.Stopwatch]::Frequency*([NLink.Tools.LocalNativeInputCapture]::MaximumActiveMilliseconds+1)/1000))
        $o.BeginInput('fullCapture',0)
    }}
)
$negative=0
foreach($control in $controls) {
    $owner=[NLink.Tools.LocalNativeInputCapture]::new([guid]::NewGuid())
    try {
        if((& $control.Action $owner) -ne $false -or $null -ne $owner.Take() -or $owner.BeginInput('fullSnapshot',0)) { throw "Negative admitted: $($control.Name)" }
        $negative++
    }
    finally { $owner.Dispose() }
}
$owner=[NLink.Tools.LocalNativeInputCapture]::new([guid]::NewGuid())
try {
    if($null -ne $owner.Take() -or $owner.BeginInput('fullCapture',0)) { throw 'Missing roles admitted or retried' }
    $negative++
}
finally { $owner.Dispose() }
$owner=[NLink.Tools.LocalNativeInputCapture]::new([guid]::NewGuid())
try {
    # Total-work exhaustion is bounded even if the active-time ceiling wins first.
    $count=[int]([NLink.Tools.LocalNativeInputCapture]::MaximumCodeUnits/[NLink.Tools.LocalNativeInputCapture]::MaximumLineCodeUnits)+1
    [void]$owner.BeginInput('fullCapture',$count)
    $line='x'*262144; $rejected=$false
    for($i=1;$i -le $count;$i++) { if(-not $owner.AppendLine($i,$line)) { $rejected=$true; break } }
    if(-not $rejected -or $owner.CodeUnitVisits -gt [NLink.Tools.LocalNativeInputCapture]::MaximumCodeUnits -or
        $owner.RecordVisits -gt [NLink.Tools.LocalNativeInputCapture]::MaximumRecords -or $null -ne $owner.Take()) { throw 'Total work was unbounded' }
    $negative++
}
finally { $owner.Dispose() }
$invalidRequest=$false
try { $bad=[NLink.Tools.LocalNativeInputCapture]::new([guid]::Empty); $bad.Dispose() } catch { $invalidRequest=$true }
if(-not $invalidRequest -or [NLink.Tools.LocalNativeInputCapture+NativeInputs].GetConstructors().Count -ne 0 -or
    [NLink.Tools.LocalNativeInputCapture+NativeInput].GetConstructors().Count -ne 0) { throw 'Unissued input token publicly constructible' }
$negative++
'local-native-input-capture='+(ConvertTo-Json -InputObject ([ordered]@{Vectors=$rows.ToArray();NegativeControls=$negative
    MaximumRecords=[NLink.Tools.LocalNativeInputCapture]::MaximumRecords;MaximumCodeUnits=[NLink.Tools.LocalNativeInputCapture]::MaximumCodeUnits
    MaximumLineCodeUnits=[NLink.Tools.LocalNativeInputCapture]::MaximumLineCodeUnits;MaximumActiveMilliseconds=[NLink.Tools.LocalNativeInputCapture]::MaximumActiveMilliseconds
    BufferBytes=[NLink.Tools.LocalNativeInputCapture]::BufferBytes;AuthorityProof=$false;PublicationCompletionIssued=$false;ProductionCaller=$false
}) -Depth 6 -Compress)
