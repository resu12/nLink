param([string]$RepoRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
$ast=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticState.ps1')
Invoke-Expression (Get-AttemptFunction $ast 'Fixture')
Add-Type @'
public sealed class BoundaryVisitCounter { public long Count; public void Visit() { Count++; } }
public sealed class BoundaryHostileEquals {
    public static int Calls;
    public override bool Equals(object other) { Calls++; return true; }
    public override int GetHashCode() { return 0; }
}
'@
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$type=[NLink.Tools.LocalRetainedSourceRequest].Assembly.GetType('NLink.Tools.LocalFrontierDiagnosticState')
$capture=$type.GetMethod('Capture',[Reflection.BindingFlags]'Static,NonPublic')
$current=$type.GetMethod('IsCurrent',$flags)
$maximum=[NLink.Tools.LocalPublicationInputLimits]::InputRecords
function Capture($x,[int]$Count,$counter) {
    $visit=[Delegate]::CreateDelegate([Action],$counter,$counter.GetType().GetMethod('Visit'))
    $capture.Invoke($null,[object[]]@($x.Failure,$x.Candidates,$x.Proof,$x.Index,$x.Full,$x.Compact,$Count,$visit))
}
function Reason([scriptblock]$Action) {
    try {[void](& $Action);return ''} catch {
        $caught=$_.Exception;while($null -ne $caught.InnerException){$caught=$caught.InnerException};return $caught.Message
    }
}
foreach($count in @(8587,8192,1)) {
    $x=Fixture;$x.Index.AllEvents=[object[]](@($x.Failure.Event)*$count)
    $rows=[object[]](@($x.Full.Boundaries[0])*$count)
    $x.Full.Boundaries=$rows;$x.Compact.Boundaries=$rows.Clone()
    $counter=[BoundaryVisitCounter]::new();$guard=$null;$script:boundaryGuard=$null
    $reason=Reason {$script:boundaryGuard=Capture $x $count $counter};$guard=$script:boundaryGuard
    if($reason -or $null -eq $guard){throw "Boundary capture rejected valid input-sized stream: $count`: $reason"}
    if($type.GetField('cells',$flags).GetValue($guard) -ne 41 -or $counter.Count -ne 41+23L*$count){throw 'Boundary capture changed metadata or visit accounting'}
    $before=$counter.Count
    if(-not $current.Invoke($guard,$null) -or $counter.Count-$before -ne 49+23L*$count){throw 'Boundary currentness omitted charged checks'}
}
foreach($fault in @('over-input','over-full','rank','lower-bound','null-row','script-field','scalar','text','candidate-cap')) {
    $x=Fixture;$count=1;$script:getters=0;$expected='frontier_diagnostic_collection'
    switch($fault) {
        'over-input' {$x.Full.Boundaries=[object[]](@($x.Full.Boundaries[0])*($maximum+1))}
        'over-full' {$x.Full.Boundaries=[object[]]@($x.Full.Boundaries[0],$x.Full.Boundaries[0])}
        'rank' {$x.Full.Boundaries=[Array]::CreateInstance([object],1,1)}
        'lower-bound' {$x.Full.Boundaries=[Array]::CreateInstance([object],[int[]]@(1),[int[]]@(1))}
        'null-row' {$x.Full.Boundaries[0]=$null;$expected='frontier_diagnostic_owner'}
        'script-field' {$row=$x.Full.Boundaries[0];$row.PSObject.Properties.Remove('boundaryHash');$row|Add-Member ScriptProperty boundaryHash {$script:getters++;'hash'};$expected='frontier_diagnostic_note:boundaryHash'}
        'scalar' {$x.Full.Boundaries[0].boundaryHash=[BoundaryHostileEquals]::new();$expected='frontier_diagnostic_scalar'}
        'text' {$x.Full.Boundaries[0].boundaryHash='x'*262145;$expected='frontier_diagnostic_text_capacity'}
        'candidate-cap' {$x.Candidates=[object[]](@($x.Failure)*8193)}
    }
    $counter=[BoundaryVisitCounter]::new()
    if((Reason {Capture $x $count $counter}) -cne $expected -or $script:getters -or [BoundaryHostileEquals]::Calls){throw ('Boundary admission guard changed: '+$fault)}
}
$names=@('coordinatorInstanceId','boundaryId','evidenceLineIndex','verdict','decisionKind','decisionStatus','boundaryHash','writer','writerSequence')
foreach($view in @('Full','Compact')) {
    foreach($fault in @('array-copy','row-copy','order')+$names) {
        $x=Fixture;$row=$x.Full.Boundaries[0];$second=$row.PSObject.Copy();$second.boundaryId='second'
        $x.Index.AllEvents=[object[]]@($x.Failure.Event,$x.Failure.Event)
        $x.Full.Boundaries=[object[]]@($row,$second);$x.Compact.Boundaries=[object[]]@($row,$second)
        $counter=[BoundaryVisitCounter]::new();$guard=Capture $x 2 $counter
        switch($fault) {
            'array-copy' {$x[$view].Boundaries=$x[$view].Boundaries.Clone()}
            'row-copy' {$x[$view].Boundaries[1]=$second.PSObject.Copy()}
            'order' {$x[$view].Boundaries[0]=$second;$x[$view].Boundaries[1]=$row}
            default {$property=$second.PSObject.Properties[$fault];$value=$property.Value;$second.PSObject.Properties.Remove($fault);$second.PSObject.Properties.Add([Management.Automation.PSNoteProperty]::new($fault,$value))}
        }
        if($current.Invoke($guard,$null)){throw "Boundary identity/property mutation accepted: $view/$fault"}
    }
}
foreach($fault in @('value','type','script','hostile-equals')) {
    $x=Fixture;$counter=[BoundaryVisitCounter]::new();$guard=Capture $x 1 $counter;$script:getters=0
    $row=$x.Full.Boundaries[0]
    switch($fault) {
        'value' {$row.boundaryHash='changed'}
        'type' {$row.writerSequence='1'}
        'script' {$row.PSObject.Properties.Remove('boundaryHash');$row|Add-Member ScriptProperty boundaryHash {$script:getters++;'hash'}}
        'hostile-equals' {$row.boundaryHash=[BoundaryHostileEquals]::new()}
    }
    if($current.Invoke($guard,$null) -or $script:getters -or [BoundaryHostileEquals]::Calls){throw ('Boundary validation executed or accepted replacement: '+$fault)}
}
'frontier-diagnostic-boundary-state=verified'
