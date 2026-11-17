param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,
    [ValidateSet('independent_pairs','independent_missing_receive')][string]$TunaPayloadMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_SERVICE_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_RECIPROCAL_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_REJECTION_CHAIN_CONSUMER='1'
$env:NLINK_PUBLICATION_DISPATCH_WINDOW_CONSUMER='1'
$env:NLINK_PUBLICATION_DISPATCH_WINDOW_CONTROLS='0'
$env:NLINK_PUBLICATION_SECURITY_LIFECYCLE_CONSUMER='1'
$env:NLINK_PUBLICATION_ALTERNATIVE_ACCEPTANCE_SHADOW='1'
$env:NLINK_PUBLICATION_TUNA_PAIR_SHADOW='1'
$env:NLINK_PUBLICATION_TUNA_PAIR_CONTROLS=if($TunaPayloadMode -ceq 'independent_pairs') {'1'} else {'0'}
$env:NLINK_PUBLICATION_TUNA_PAYLOAD_OBSERVER='1'
# The independent range intentionally has the unchanged strict wrong-range
# outcome. This wrapper does not claim the canonical scenario or terminal pass.
$null=& (Join-Path $PSScriptRoot 'PublicationReceiveIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -TwoWriterFixture -ServiceIngressFixture -TunaPayloadMode wrong_range -ServiceYieldFixture
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$r=Get-Content -LiteralPath (Join-Path $output 'alternative-acceptance-shadow.json') -Raw | ConvertFrom-Json
$prior=Get-Content -LiteralPath (Join-Path $output 'service-yield-consumer.json') -Raw | ConvertFrom-Json
$facts=Get-Content -LiteralPath (Join-Path $output 'live-primary-observation.json') -Raw | ConvertFrom-Json
if($r.FirstMissingEdge -cne 'owned_scenario_gate_results_unavailable') {
    throw ($r | ConvertTo-Json -Depth 12 -Compress)
}
if($r.Decision.Verdict -cne 'unproven' -or -not $r.InputsUnchanged -or -not $r.PriorResultsUnchanged -or
    $r.AcceptanceChanged -or $r.ClassifierEnabled -or $r.Live -or $r.OriginalUnresolvedCandidates -ne 1 -or
    ($r.WitnessOrigins -join ',') -cne ($prior.WitnessOrigins -join ',') -or $r.WitnessOrigins.Count -gt 64 -or
    $facts.Evaluations -ne 3 -or $facts.IngressCandidates.Count -ne 1 -or $facts.IngressCandidates[0].Recovered -or
    $facts.IngressCandidates[0].Reason -cne 'missing_tuna_resend' -or $r.PayloadWorkElapsedMilliseconds -gt 20000) { throw 'Payload shadow changed strict/custody contract' }
foreach($name in @('scenario_scope','route_cycle','terminal_integrity','remaining_failures')) {
    if($r.Gates.$name -cne 'unproven' -or $r.Decision.UnprovenGates -cnotcontains $name) { throw 'Payload pairs supplied unrelated scenario proof' }
}
$first=$r.PayloadGates.first_tuna_payload; $second=$r.PayloadGates.second_tuna_payload
if($first.Status -cne 'pass' -or $first.Pair.StartChunk -ne 16 -or $first.Pair.Bytes -ne 2048 -or $first.Pair.Chunks -ne 2 -or
    $r.Gates.first_tuna_payload -cne 'pass') { throw ('First payload not proven: '+($first | ConvertTo-Json -Depth 10 -Compress)) }
if($TunaPayloadMode -ceq 'independent_pairs') {
    if($r.Candidate.StrictReason -cne 'missing_tuna_resend' -or $r.Candidate.StrictRecovered -or
        $r.Gates.recorded_rejections -cne 'pass' -or $r.Gates.candidate_coverage -cne 'pass' -or
        $prior.DispatchWindowStatus -cne 'no_matching_bound_dispatch_observed_in_capture' -or
        $prior.DispatchWindowFirstMissingEdge -cne '(none)' -or $prior.DispatchWindow.MatchingDispatchCount -ne 0 -or
        $r.Candidate.SecurityLifetimeProven -or $r.Candidate.RuntimeRetirementProven -or $r.Candidate.GeneralStaleSafetyCertified) {
        throw 'Disjoint recorded range did not preserve the bounded rejection contract'
    }
    if($second.Status -cne 'pass' -or $second.Pair.StartChunk -ne 20 -or $second.Pair.Bytes -ne 2048 -or $second.Pair.Chunks -ne 2 -or
        $r.Gates.second_tuna_payload -cne 'pass' -or $second.Pair.Envelope -ceq $first.Pair.Envelope -or
        $r.PayloadControls.Passed -ne 17 -or -not $r.PayloadControls.DetachedOnly) { throw ('Second payload not proven: '+($second | ConvertTo-Json -Depth 10 -Compress)) }
} else {
    if($second.Status -cne 'unproven' -or $second.FirstMissingEdge -cne 'tuna_pair_receive_missing' -or
        $null -ne $second.Pair -or $r.Gates.second_tuna_payload -cne 'unproven') { throw 'Omitted second receive was accepted' }
    if($r.Candidate.StrictReason -cne 'missing_tuna_resend' -or $r.Candidate.StrictRecovered -or
        $r.Gates.recorded_rejections -cne 'pass' -or $r.Gates.candidate_coverage -cne 'pass') { throw 'Independent first pair changed recorded rejection admission' }
}
'tuna-pair-shadow='+([ordered]@{Mode=$TunaPayloadMode;First=$first.Status;Second=$second.Status;Verdict=$r.Decision.Verdict;
    StrictReason=$facts.IngressCandidates[0].Reason;Recovered=$false;OriginalEvaluations=3;InputsUnchanged=$true;
    PriorResultsUnchanged=$true;AcceptanceChanged=$false;Live=$false} | ConvertTo-Json -Compress)
