param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$TunaPayloadMode,[switch]$OmitSecurity)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_SERVICE_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_RECIPROCAL_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_REJECTION_CHAIN_CONSUMER='1'
$env:NLINK_PUBLICATION_DISPATCH_WINDOW_CONSUMER='1'
$env:NLINK_PUBLICATION_DISPATCH_WINDOW_CONTROLS='0'
$env:NLINK_PUBLICATION_SECURITY_LIFECYCLE_CONSUMER=if($OmitSecurity) {'0'} else {'1'}
$env:NLINK_PUBLICATION_ALTERNATIVE_ACCEPTANCE_SHADOW='1'
$null=& (Join-Path $PSScriptRoot 'PublicationTunaPayloadIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -TunaPayloadMode $TunaPayloadMode -ServiceYieldFixture
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$errorPath=Join-Path $output 'service-yield-consumer-error.txt'
if(Test-Path -LiteralPath $errorPath) { throw ([IO.File]::ReadAllText($errorPath)) }
$r=Get-Content -LiteralPath (Join-Path $output 'alternative-acceptance-shadow.json') -Raw | ConvertFrom-Json
$prior=Get-Content -LiteralPath (Join-Path $output 'service-yield-consumer.json') -Raw | ConvertFrom-Json
if($r.Schema -cne 'nlink.alternative-acceptance-shadow/v1' -or $r.Decision.Verdict -cne 'unproven' -or
    -not $r.InputsUnchanged -or -not $r.PriorResultsUnchanged -or $r.ClassifierEnabled -or $r.AcceptanceChanged -or $r.Live -or
    $r.OriginalUnresolvedCandidates -ne 1 -or ($r.WitnessOrigins -join ',') -cne ($prior.WitnessOrigins -join ',') -or
    $r.WitnessOrigins.Count -gt 64 -or $prior.Status -cne 'matched_original_yield_receipts' -or
    $prior.RejectionChainStatus -cne 'matched_original_rejection_chain' -or
    $prior.DispatchWindowStatus -cne 'no_matching_bound_dispatch_observed_in_capture') { throw ($r | ConvertTo-Json -Depth 12 -Compress) }
foreach($name in @('scenario_scope','route_cycle','first_tuna_payload','second_tuna_payload','terminal_integrity','remaining_failures')) {
    if($r.Gates.$name -cne 'unproven' -or $r.Decision.UnprovenGates -cnotcontains $name) { throw 'Unavailable scenario proof was inferred' }
}
if($OmitSecurity) {
    if($null -ne $r.Candidate -or $r.EvaluatedCandidates -ne 0 -or $r.Gates.recorded_rejections -cne 'unproven' -or
        $r.FirstMissingEdge -cne 'shadow_prerequisite:SecurityLifecycleStatus') { throw 'Omitted security evidence was accepted' }
} else {
    if($r.Candidate.Status -cne 'recorded_stale_route_rejection' -or $r.Candidate.StrictRecovered -or
        $r.Candidate.StrictReason -cne 'missing_exact_tuna_receive' -or $r.EvaluatedCandidates -ne 1 -or
        $r.Gates.recorded_rejections -cne 'pass' -or $r.Gates.candidate_coverage -cne 'pass' -or
        $r.Candidate.SecurityLifetimeProven -or $r.Candidate.RuntimeRetirementProven -or $r.Candidate.GeneralStaleSafetyCertified -or
        $r.FirstMissingEdge -cne 'owned_scenario_gate_results_unavailable') { throw 'Recorded-rejection shadow contract changed' }
}
'alternative-acceptance-shadow='+([ordered]@{PrerequisiteOmitted=[bool]$OmitSecurity;Verdict=$r.Decision.Verdict;
    CandidateStatus=$(if($null -eq $r.Candidate) {'unproven'} else {$r.Candidate.Status});FirstMissingEdge=$r.FirstMissingEdge;
    EvaluatedCandidates=$r.EvaluatedCandidates;OriginalEvaluations=3;StrictReason='missing_exact_tuna_receive';StrictRecovered=$false;
    InputsUnchanged=$r.InputsUnchanged;PriorResultsUnchanged=$r.PriorResultsUnchanged;AcceptanceChanged=$false;Live=$false} | ConvertTo-Json -Compress)
