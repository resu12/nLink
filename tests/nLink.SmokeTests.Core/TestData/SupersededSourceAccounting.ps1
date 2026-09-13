param([string]$RepoRoot,[string]$InputPath)
$ErrorActionPreference='Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/StallGraceAccounting.ps1')
function Check([bool]$Condition,[string]$Reason) { if (-not $Condition) { throw $Reason } }
function Copy-Value($Value) { [Management.Automation.PSSerializer]::Deserialize([Management.Automation.PSSerializer]::Serialize($Value,64)) }
function Parse([string[]]$Lines) {
    for ($i=0;$i -lt $Lines.Count;$i++) {
        $event=ConvertFrom-FileTransferLogLine $Lines[$i] 'accounting-test.log' ($i+1) ($i+1)
        if ($null -ne $event) { $event }
    }
}
function Counts($r) { @($r.AccountingVersion,$r.RawCount,$r.DistinctCount,$r.RecoveredCount,$r.SupersededCount,$r.UnresolvedCount) -join '/' }
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'GUI parse failure'
$definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $n.Name -ceq 'Get-TunaGuiFallbackFrontierStallGraceRecheckFailure'},$true))
Invoke-Expression $definition[0].Extent.Text
$lines=[IO.File]::ReadAllLines($InputPath)
$snapshot=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
Check ($snapshot.ArtifactEvidenceVerdict -ceq 'pass' -and $snapshot.StreamComplete) $snapshot.ArtifactEvidenceFailureReason
$events=@(Parse $lines)
$hold=@($events | Where-Object EventName -CEQ 'filetransfer_v6_frontier_request_deferred')[0]
$index=New-FileTransferStallGraceCurrentSourceIndex -Events $events -SnapshotEvidence $snapshot
$reason='';$closure=Get-FileTransferStallGraceSupersessionClosure $index $hold ([ref]$reason)
Check ($null -ne $closure) "actual join failed:$reason"
Check ($closure.RepairOutcome -ceq 'SettledStalled' -and $closure.SuccessorOutcome -ceq 'StaleRejected') 'repair outcome rewritten'
Check ($closure.LocalTarget.CoordinatorInstanceId -cne $closure.PeerTarget.CoordinatorInstanceId) 'same peer used twice'
foreach ($first in @($closure.LocalTarget.CoordinatorInstanceId,$closure.PeerTarget.CoordinatorInstanceId)) {
    $reordered=Copy-Value $snapshot
    $reordered.Boundaries=@($reordered.Boundaries | Sort-Object @{Expression={if ($_.coordinatorInstanceId -ceq $first) {0} else {1}}},afterStateGeneration)
    $sourceIndex=New-FileTransferStallGraceCurrentSourceIndex -Events $events -SnapshotEvidence $reordered
    $why='';$proof=Get-FileTransferStallGraceSupersessionClosure $sourceIndex $hold ([ref]$why)
    Check ($null -ne $proof -and $proof.LocalTarget.CommitBoundaryId -ceq $closure.LocalTarget.CommitBoundaryId -and
        $proof.PeerTarget.CommitBoundaryId -ceq $closure.PeerTarget.CommitBoundaryId) "peer drain order changed authority:$why"
}

function Parity([string]$Case,[string[]]$InputLines,[string]$Expected) {
    $inputEvents=@(Parse $InputLines)
    $direct=& $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $inputEvents -SnapshotEvidence $snapshot
    $partition=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $inputEvents -SnapshotEvidence $snapshot
    $indexed=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $inputEvents -Index $partition -SnapshotEvidence $snapshot
    # The GUI ledger supplies this coordinate before calling the shared reducer.
    $guiLines=@(for ($i=0;$i -lt $InputLines.Count;$i++) { $InputLines[$i] + '; gui_evidence_sequence=' + ($i+1) })
    $gui=Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $guiLines -IncludeEpisodeAudit -SnapshotEvidence $snapshot
    $guiFast=Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $guiLines -SnapshotEvidence $snapshot
    foreach ($r in @($direct,$indexed)) {
        Check ((Counts $r) -ceq $Expected) "$Case counts:$(Counts $r)"
        $s=@($r.CandidateRecords | Where-Object ObligationDisposition -CEQ 'Superseded')
        Check ($s.Count -eq 1 -and -not $s[0].Recovered -and $null -eq $s[0].RecoveryEvent -and
            $null -eq $s[0].PSObject.Properties['UnresolvedDiagnostic']) "$Case superseded audit"
        Check (@($r.UnresolvedCandidateRecords | Where-Object ObligationDisposition -CNE 'Unresolved').Count -eq 0) "$Case unresolved list"
    }
    $s=@($gui.EpisodeAudit | Where-Object ObligationDisposition -CEQ 'Superseded')
    Check ($gui.AccountingVersion -eq 2 -and $gui.SupersededCount -eq 1 -and $s.Count -eq 1 -and
        -not $s[0].Recovered -and -not $s[0].Excluded -and $null -eq $s[0].RecoveryEvent) "$Case GUI audit: S=$($gui.SupersededCount); audit=$(@($gui.EpisodeAudit | ForEach-Object { $_.ObligationDisposition + ':' + $_.Excluded + ':' + $_.Recovered }) -join ',')"
    Check ($gui.Unresolved -eq ($indexed.UnresolvedCount -gt 0) -and $guiFast.Unresolved -eq $gui.Unresolved) "$Case GUI gate"
    if ($gui.Unresolved) {
        $u=$indexed.UnresolvedCandidateRecords[0]
        Check ($u.Event.Fields.frontier_chunk_index -ceq '101' -and $null -eq $gui.FirstUnresolvedDiagnostic) "$Case wrong first unresolved diagnostic"
    }
    Write-Output "pass $Case"
}
Parity 'actual-full' $lines '2/1/1/0/1/0'
$names=@(Get-FileTransferFallbackFrontierGraceCorrelationEventNames)
$compact=@($lines | Where-Object { $_ -match '(?:^|; |\] )event=([^;]+)' -and $Matches[1] -cin $names })
Check ($compact.Count -lt $lines.Count) 'compact input not independent'
Parity 'actual-compact' $compact '2/1/1/0/1/0'
# A second otherwise identical, admitted wake has no matching repair chain.
# This synthetic consumer mutation is not a second actual runtime episode.
$holdLine=@($compact | Where-Object { $_ -match 'event=filetransfer_v6_frontier_request_deferred(?:;|$)' })[0]
$otherHold=$holdLine.Replace('frontier_chunk_index=100;','frontier_chunk_index=101;').Replace('wake_generation=1;','wake_generation=2;')
$used=@((Parse $compact) | ForEach-Object { [long]$_.Fields.log_writer_sequence })
$newSequence=[long]$hold.Fields.log_writer_sequence+1
while ($newSequence -in $used) { $newSequence++ }
foreach ($field in @('log_writer_sequence','log_writer_batch_generation','log_writer_batch_first_sequence','log_writer_batch_last_sequence')) {
    $otherHold=[regex]::Replace($otherHold,"$field=[0-9]+",("{0}={1}" -f $field,$newSequence))
}
$mixed=@($compact | ForEach-Object { $_; if ($_ -ceq $holdLine) { $otherHold } })
$oldWidth=$script:FileTransferFallbackFrontierGracePartitionWidth
try {
    $script:FileTransferFallbackFrontierGracePartitionWidth=1
    Parity 'mixed-superseded-unresolved' $mixed '2/2/2/0/1/1'
} finally { $script:FileTransferFallbackFrontierGracePartitionWidth=$oldWidth }

# Merge accounting must count recovery explicitly, including mixed partitions.
$reference=& $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $events -SnapshotEvidence $snapshot
$recovered=Copy-Value $reference.CandidateRecords[0]
$recovered.Event.Sequence+=100000;$recovered.Event.LineNumber+=100000
$recovered.Recovered=$true;$recovered.ObligationDisposition='Recovered';$recovered.RecoveryEvent='unit-recovery'
$unresolved=Copy-Value $recovered
$unresolved.Event.Sequence++;$unresolved.Event.LineNumber++
$unresolved.Recovered=$false;$unresolved.ObligationDisposition='Unresolved';$unresolved.RecoveryEvent=$null
$merged=Merge-FileTransferFallbackFrontierGraceCorrelationResults -Kind stall -Index ([pscustomobject]@{IndexBuildVisitCount=0;BoundedLookupVisitCount=0}) -LookupStart 0 -Results @(
    $reference,[pscustomobject]@{AccountingVersion=2;RawCount=2;DistinctCount=2;RecoveredCount=1;SupersededCount=0;UnresolvedCount=1;CandidateRecords=@($recovered,$unresolved)})
Check ((Counts $merged) -ceq '2/3/3/1/1/1' -and $merged.UnresolvedCandidateRecords.Count -eq 1) 'mixed partition merge derived recovery'

$peerStart=@($snapshot.__FallbackTailPeerProofBindings | Where-Object { $_.BindingKind -ceq 'paired_yield' -and
    $_.CoordinatorInstanceId -ceq $closure.PeerTarget.CoordinatorInstanceId })[0].TransitionSequence
$peerEnd=@($snapshot.Boundaries | Where-Object boundaryId -CEQ $closure.PeerTarget.CommitBoundaryId)[0].writerSequence
foreach ($poison in @('pause','terminal','data-replacement')) {
    $sequence=[long]$peerStart+1
    while ($sequence -in $used) { $sequence++ }
    Check ($sequence -lt [long]$peerEnd) 'peer poison fixture interval missing'
    $name=if ($poison -ceq 'pause') {'filetransfer_v6_receiver_state_sent'} elseif ($poison -ceq 'terminal') {'filetransfer_live_route_epoch_terminal'} else {'filetransfer_data_session_recreated'}
    # The synthetic negative arrives late in the consumer input but belongs to
    # the peer's earlier writer interval. It grants no positive authority.
    $poisonLine='[2026-09-13 09:00:00Z] [INFO] [UnitFixture] event={0}; log_writer_id={1}; log_writer_sequence={2}; session_id={3}; transfer_id={4}; direction={5}; transfer_paused={6}' -f
        $name,$hold.Fields.log_writer_id,$sequence,$hold.Fields.session_id,$hold.Fields.transfer_id,$closure.PeerTarget.Direction,$(if ($poison -ceq 'pause') {'1'} else {'0'})
    $peerLines=@($compact)+@($poisonLine)
    $peerEvents=@(Parse $peerLines)
    $direct=& $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $peerEvents -SnapshotEvidence $snapshot
    $partition=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $peerEvents -SnapshotEvidence $snapshot
    $indexed=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $peerEvents -Index $partition -SnapshotEvidence $snapshot
    foreach ($r in @($direct,$indexed)) { Check ($r.SupersededCount -eq 0 -and $r.RecoveredCount -eq 0) "peer $poison closed the source" }
    Check ((Counts $direct) -ceq (Counts $indexed)) "peer $poison changed admission between consumers"
    $sourceIndex=New-FileTransferStallGraceCurrentSourceIndex -Events $events -SnapshotEvidence $snapshot
    $why='';$rejected=Get-FileTransferStallGraceSupersessionClosure $sourceIndex $hold ([ref]$why) -EvidenceEvents $peerEvents
    Check ($null -eq $rejected -and $why -ceq 'peer_source_interval_poisoned') "peer $poison not vetoed:$why"
    Write-Output "pass peer-$poison"
}

# Mutate already validated DTOs only to unit-test fail-closed selection. These
# copies do not constitute valid producer evidence or acceptance artifacts.
foreach ($case in @('legacy','incomplete','peer-pair','peer-proof','retry-link','dispatch','responder-link',
    'local-commit','peer-commit','local-disposition','peer-disposition','local-publication','peer-publication',
    'checkpoint','data','duplicate','source-aba','target-aba')) {
    $copy=Copy-Value $snapshot
    $local=@($copy.Boundaries | Where-Object boundaryId -CEQ $closure.LocalTarget.CommitBoundaryId)[0]
    $peer=@($copy.Boundaries | Where-Object boundaryId -CEQ $closure.PeerTarget.CommitBoundaryId)[0]
    switch ($case) {
        'legacy' { $copy.CatalogVersion=$script:FileTransferCoordinatorRepairOwnerCatalogVersion }
        'incomplete' { $copy.StreamComplete=$false }
        'peer-pair' { $copy.__FallbackTailPeerProofBindings=@($copy.__FallbackTailPeerProofBindings | Where-Object CoordinatorInstanceId -CEQ $closure.LocalTarget.CoordinatorInstanceId) }
        'peer-proof' { foreach ($p in $copy.__FallbackTailPeerProofBindings) { if ($p.CoordinatorInstanceId -ceq $closure.PeerTarget.CoordinatorInstanceId) { $p.PeerProofFingerprint='foreign' } } }
        'retry-link' { $copy.__ActivationRetryEvidence=$null }
        'dispatch' { $copy.Boundaries=@($copy.Boundaries | Where-Object decisionKind -CNE 'ObserveRuntimeUnlockRetryDispatchSettlement') }
        'responder-link' { foreach ($b in $copy.Boundaries) { $b.operations=@($b.operations | Where-Object operationRole -CNE 'route_transition_handoff_pending') } }
        'local-commit' { $copy.Boundaries=@($copy.Boundaries | Where-Object boundaryId -CNE $local.boundaryId) }
        'peer-commit' { $copy.Boundaries=@($copy.Boundaries | Where-Object boundaryId -CNE $peer.boundaryId) }
        'local-disposition' { $copy.Boundaries=@($copy.Boundaries | Where-Object boundaryId -CNE $closure.LocalTarget.DispositionBoundaryId) }
        'peer-disposition' { $copy.Boundaries=@($copy.Boundaries | Where-Object boundaryId -CNE $closure.PeerTarget.DispositionBoundaryId) }
        'local-publication' { $local.operations=@($local.operations | Where-Object operationKey -CNE $closure.LocalTarget.PublicationOperationKey) }
        'peer-publication' { $peer.operations=@($peer.operations | Where-Object operationKey -CNE $closure.PeerTarget.PublicationOperationKey) }
        'checkpoint' { foreach ($o in $peer.operations) { if ($o.family -ceq 'route_transition') { $o.routeTransition.durableCheckpointFingerprint='(none)' } } }
        'data' { $peer.afterBoundary.dataSessionGeneration++ }
        'duplicate' { $copy.Boundaries+=Copy-Value $peer }
        'source-aba' { $b=@($copy.Boundaries | Where-Object boundaryId -CEQ $closure.PeerTarget.PreparedBoundaryId)[0]; $b.beforeBoundary.terminalGeneration++ }
        'target-aba' { $b=@($copy.Boundaries | Where-Object boundaryId -CEQ $closure.PeerTarget.DispositionBoundaryId)[0]; $b.beforeBoundary.dataSessionGeneration++ }
    }
    $candidateIndex=New-FileTransferStallGraceCurrentSourceIndex -Events $events -SnapshotEvidence $copy
    $why='';$rejected=Get-FileTransferStallGraceSupersessionClosure $candidateIndex $hold ([ref]$why)
    Check ($null -eq $rejected -and -not [string]::IsNullOrWhiteSpace($why)) "accepted negative:$case"
    Write-Output "pass negative-$case"
}

$prefix='filetransfer_fallback_frontier_stall_grace_recheck_missing_'
$v2=@{}
foreach ($entry in @(@('accounting_version','2'),@('raw_count','3'),@('distinct_count','3'),@('recovered_count','1'),@('superseded_count','1'),@('unresolved_count','1'))) {
    $v2[$prefix+$entry[0]]=$entry[1]
}
Check ((Counts (ConvertFrom-FileTransferStallGraceAccountingReport $v2)) -ceq '2/3/3/1/1/1') 'valid report rejected'
$legacy=@{};$legacy[$prefix+'distinct_count']='1';$legacy[$prefix+'unresolved_count']='1'
$read=ConvertFrom-FileTransferStallGraceAccountingReport $legacy
Check ($read.AccountingVersion -eq 1 -and $read.SupersededCount -eq 0 -and $read.UnresolvedCount -eq 1) 'legacy report rewritten'
foreach ($case in @('version','missing','negative','malformed','equation','legacy-superseded')) {
    $bad=$v2.Clone()
    switch ($case) {
        'version' { $bad[$prefix+'accounting_version']='3' }
        'missing' { $bad.Remove($prefix+'superseded_count') }
        'negative' { $bad[$prefix+'raw_count']='-1' }
        'malformed' { $bad[$prefix+'raw_count']='NaN' }
        'equation' { $bad[$prefix+'distinct_count']='2' }
        'legacy-superseded' { $bad.Remove($prefix+'accounting_version') }
    }
    $threw=$false
    try { $null=ConvertFrom-FileTransferStallGraceAccountingReport $bad } catch { $threw=$true }
    Check $threw "invalid report accepted:$case"
}
Write-Output 'superseded-source-accounting-ok'
