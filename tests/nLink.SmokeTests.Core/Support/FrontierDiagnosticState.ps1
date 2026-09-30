param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
Add-Type 'public sealed class DiagnosticHostileEquals { public static int Calls; public override bool Equals(object other) { Calls++; return true; } public override int GetHashCode() { return 0; } }'
$type=[NLink.Tools.LocalRetainedSourceRequest].Assembly.GetType('NLink.Tools.LocalFrontierDiagnosticState')
$capture=$type.GetMethod('Capture',[Reflection.BindingFlags]'Static,NonPublic')
$current=$type.GetMethod('IsCurrent',[Reflection.BindingFlags]'Instance,NonPublic')
$visit=[Action]{}
function Fixture {
    $event=[pscustomobject]@{EventName='fixture';LineNumber=1;Sequence=1;FilePath='source';RawLine='original'}
    $failure=[pscustomobject]@{Class='fixture';Reason='reason';Variant='variant';Event=$event}
    $proof=[pscustomobject]@{UnresolvedCount=1;RecoveredCount=0;UnresolvedCandidateRecords=[object[]]@([pscustomobject]@{Event=$event;Reason='reason';Variant='variant';Recovered=$false})}
    $index=[pscustomobject]@{AllEvents=[object[]]@($event);LogFiles=[string[]]@('source')}
    $boundary=[pscustomobject]@{coordinatorInstanceId='coordinator';boundaryId='boundary';evidenceLineIndex=0;verdict='pass';decisionKind='kind';decisionStatus='status';boundaryHash='hash';writer='writer';writerSequence=1L}
    $view=[pscustomobject]@{Mode='mode';ContractVersion=1;CatalogVersion='version';CatalogHash='hash';BoundaryHash='hash';ArtifactEvidenceVerdict='pass';StreamComplete=$true;AuthoritativeFamilies=[string[]]@('family');Boundaries=[object[]]@($boundary)}
    return @{Failure=$failure;Candidates=[object[]]@($failure);Proof=$proof;Index=$index;Full=$view;Compact=$view.PSObject.Copy()}
}
function Capture($x,[int]$FullCount=1) { $capture.Invoke($null,[object[]]@($x.Failure,$x.Candidates,$x.Proof,$x.Index,$x.Full,$x.Compact,$FullCount,$visit)) }
foreach($case in @('valid','hostile-equals','scalar-type','script-property','array-cap','field-cap','array-rank')) {
    $x=Fixture; $reason=$null; $script:getters=0
    switch($case) {
        'script-property' {$x.Failure.PSObject.Properties.Remove('Reason'); $x.Failure | Add-Member ScriptProperty Reason {$script:getters++;'reason'}; $reason='frontier_diagnostic_note:Reason'}
        'array-cap' {$x.Candidates=[object[]](@($x.Failure)*8193); $reason='frontier_diagnostic_collection'}
        'field-cap' {$x.Full.Boundaries=[object[]](@($x.Full.Boundaries[0])*8192); $reason='frontier_diagnostic_state_capacity'}
        'array-rank' {$x.Index.AllEvents=[Array]::CreateInstance([object],1,1); $reason='frontier_diagnostic_collection'}
    }
    if($null -ne $reason) {
        $caught=$null; try {[void](Capture $x)} catch {$caught=$_.Exception; while($null -ne $caught.InnerException){$caught=$caught.InnerException}}
        if($null -eq $caught -or $caught.Message -cne $reason) {throw ('Wrong guard rejection: '+$case+' '+$caught)}
        if($script:getters -ne 0) {throw 'Capture executed script property'}
        continue
    }
    $guard=Capture $x
    if(-not $current.Invoke($guard,$null)) {throw 'Original guard not current'}
    if($case -ceq 'hostile-equals') {$x.Failure.Reason=[DiagnosticHostileEquals]::new()}
    if($case -ceq 'scalar-type') {$x.Failure.Reason=1L}
    if([bool]$current.Invoke($guard,$null) -ne ($case -ceq 'valid')) {throw ('Guard mutation mismatch: '+$case)}
    if([DiagnosticHostileEquals]::Calls -ne 0) {throw 'Guard invoked replacement Equals'}
}
# The approved event-record capacity is separate from metadata cells. Every
# original event and property must still be captured and checked.
foreach($count in @(21825,21826,65536,73885)) {
    $x=Fixture; $x.Index.AllEvents=[object[]](@($x.Failure.Event)*$count)
    $guard=$null; $reason=''
    try {$guard=Capture $x $count} catch {
        $caught=$_.Exception; while($null -ne $caught.InnerException) {$caught=$caught.InnerException}; $reason=$caught.Message
    }
    if($null -eq $guard -or -not $current.Invoke($guard,$null)) {throw ('Input-sized event state rejected: '+$count+' '+$reason)}
}
foreach($fault in @('array-copy','event-copy','raw-copy','raw-property','path-value','path-property','order','path-type','raw-script','path-script')) {
    $x=Fixture; $second=$x.Failure.Event.PSObject.Copy()
    $x.Index.AllEvents=[object[]]@($x.Failure.Event,$second); $guard=Capture $x 2
    $script:getters=0
    if(-not $current.Invoke($guard,$null)) {throw 'Original event-state guard unavailable'}
    switch($fault) {
        'array-copy' {$x.Index.AllEvents=$x.Index.AllEvents.Clone()}
        'event-copy' {$x.Index.AllEvents[1]=$second.PSObject.Copy()}
        'raw-copy' {$second.RawLine=[string]::new($second.RawLine.ToCharArray())}
        'raw-property' {$value=$second.RawLine; $second.PSObject.Properties.Remove('RawLine'); $second.PSObject.Properties.Add([Management.Automation.PSNoteProperty]::new('RawLine',$value))}
        'path-value' {$second.FilePath='other'}
        'path-property' {$value=$second.FilePath; $second.PSObject.Properties.Remove('FilePath'); $second.PSObject.Properties.Add([Management.Automation.PSNoteProperty]::new('FilePath',$value))}
        'order' {$x.Index.AllEvents[0]=$second; $x.Index.AllEvents[1]=$x.Failure.Event}
        'path-type' {$second.FilePath=[DiagnosticHostileEquals]::new()}
        'raw-script' {$second.PSObject.Properties.Remove('RawLine'); $second | Add-Member ScriptProperty RawLine {$script:getters++;'original'}}
        'path-script' {$second.PSObject.Properties.Remove('FilePath'); $second | Add-Member ScriptProperty FilePath {$script:getters++;'source'}}
    }
    if($current.Invoke($guard,$null)) {throw ('Event identity/property mutation accepted: '+$fault)}
    if($script:getters -ne 0 -or [DiagnosticHostileEquals]::Calls -ne 0) {throw 'Event validation executed replacement code'}
}
'frontier-diagnostic-state-capacity=4; event-mutations=10'
'frontier-diagnostic-state=verified'
