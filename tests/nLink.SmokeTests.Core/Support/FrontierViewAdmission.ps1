param([string]$RepoRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
$ast=Read-AttemptAst (Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeFrontierWitness.ps1')
Invoke-Expression (Get-AttemptFunction $ast 'Require')
Invoke-Expression (Get-AttemptFunction $ast 'Snapshot')
$fixtureAst=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticState.ps1')
Invoke-Expression (Get-AttemptFunction $fixtureAst 'Fixture')
$type=[NLink.Tools.LocalRetainedSourceRequest].Assembly.GetType('NLink.Tools.LocalFrontierDiagnosticState')
$capture=$type.GetMethod('Capture',[Reflection.BindingFlags]'Static,NonPublic')
$current=$type.GetMethod('IsCurrent',[Reflection.BindingFlags]'Instance,NonPublic')
Add-Type 'public sealed class AdmissionVisits {public long Count;public void Visit(){Count++;}}'
$counter=[AdmissionVisits]::new();$visit=[Delegate]::CreateDelegate([Action],$counter,$counter.GetType().GetMethod('Visit'))
$maximum=[NLink.Tools.LocalPublicationInputLimits]::InputRecords
if($maximum -ne 262144){throw 'Original input record limit changed'}
$cases=@('1','8192','8193','8583','262144','full-over-full','compact-over-full','full-over-input','compact-over-input',
    'candidate-cap','unresolved-cap','mode','contract','verdict','stream','families-cap','missing-runtime','missing-transition',
    'catalog-version','catalog-hash','boundary-hash','failure-copy','recovered')
foreach($case in $cases){
    $x=Fixture;$count=1;$fullCount=1;$expected=''
    if([int]::TryParse($case,[ref]$count)){$fullCount=$count}else{$count=1}
    $x.Failure.Class='filetransfer_fallback_frontier_recent_progress_grace_recheck_missing'
    foreach($v in @($x.Full,$x.Compact)){
        $v.Mode='SnapshotV1Authoritative';$v.AuthoritativeFamilies=[string[]]@('route_runtime','route_transition')
        $v.Boundaries=[object[]](@($v.Boundaries[0])*$count)
    }
    $x.Index.AllEvents=[object[]](@($x.Failure.Event)*$fullCount)
    $Failure=$x.Failure;$Candidates=$x.Candidates;$RecentProof=$x.Proof
    $FullView=$x.Full;$CompactView=$x.Compact
    switch($case){
        'full-over-full' {$FullView.Boundaries=[object[]](@($FullView.Boundaries[0])*2);$expected='native_frontier_view'}
        'compact-over-full' {$CompactView.Boundaries=[object[]](@($CompactView.Boundaries[0])*2);$expected='native_frontier_view'}
        'full-over-input' {$fullCount=$maximum+1;$FullView.Boundaries=[object[]](@($FullView.Boundaries[0])*$fullCount);$expected='native_frontier_view'}
        'compact-over-input' {$fullCount=$maximum+1;$CompactView.Boundaries=[object[]](@($CompactView.Boundaries[0])*$fullCount);$expected='native_frontier_view'}
        'candidate-cap' {$Candidates=[object[]](@($Failure)*8193);$expected='native_frontier_failure_shape'}
        'unresolved-cap' {$RecentProof.UnresolvedCandidateRecords=[object[]](@($RecentProof.UnresolvedCandidateRecords[0])*8193);$expected='native_frontier_failure_shape'}
        'mode' {$CompactView.Mode='legacy';$expected='native_frontier_view'}
        'contract' {$CompactView.ContractVersion=2;$expected='native_frontier_view'}
        'verdict' {$CompactView.ArtifactEvidenceVerdict='fail';$expected='native_frontier_view'}
        'stream' {$CompactView.StreamComplete=$false;$expected='native_frontier_view'}
        'families-cap' {$CompactView.AuthoritativeFamilies=[string[]](@('route_runtime','route_transition')+@('family')*63);$expected='native_frontier_view'}
        'missing-runtime' {$CompactView.AuthoritativeFamilies=[string[]]@('route_transition');$expected='native_frontier_view'}
        'missing-transition' {$CompactView.AuthoritativeFamilies=[string[]]@('route_runtime');$expected='native_frontier_view'}
        'catalog-version' {$CompactView.CatalogVersion='foreign';$expected='native_frontier_view_parity'}
        'catalog-hash' {$CompactView.CatalogHash='foreign';$expected='native_frontier_view_parity'}
        'boundary-hash' {$CompactView.BoundaryHash='foreign';$expected='native_frontier_view_parity'}
        'failure-copy' {$Failure=$Failure.PSObject.Copy();$expected='native_frontier_failure_owner'}
        'recovered' {$RecentProof.UnresolvedCandidateRecords[0].Recovered=$true;$expected='native_frontier_unresolved_owner'}
    }
    [string[]]$Full=@('original')*$fullCount
    $before=$counter.Count;$reason='';$snapshot=$null
    try{$snapshot=Snapshot}catch{$reason=$_.Exception.Message}
    if($reason -cne $expected){throw ('View admission mismatch: case='+$case+'; expected='+$(if($expected){$expected}else{'accepted'})+'; actual='+$reason)}
    if(-not $expected){
        if(-not $snapshot -or $counter.Count-$before -ne 2 -or $FullView.Boundaries.Count -ne $count -or $CompactView.Boundaries.Count -ne $count){throw 'Admission clipped evidence or changed candidate visits'}
        # Actual native capture/currentness is covered at both sides of 8192;
        # the maximum vector above exercises the original Snapshot guard only.
        if($count -le 8583){
            $guard=$capture.Invoke($null,[object[]]@($Failure,$Candidates,$RecentProof,$x.Index,$FullView,$CompactView,$fullCount,$visit))
            if(-not $current.Invoke($guard,$null)){throw 'Native input-sized capture disagrees with admitted view'}
        }
    }
}
'frontier-view-admission=23; native-boundary-pairs=4; candidate-cap=8192; input-cap=262144; no-authority'
