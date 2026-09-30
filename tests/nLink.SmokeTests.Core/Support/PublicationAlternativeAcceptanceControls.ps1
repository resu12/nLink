param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PublicationServiceYieldMatcher.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PublicationAlternativeAcceptanceShadow.ps1')
$controls=0
function CheckDecision($Gates,[string]$Expected) {
    $before=ConvertTo-Json $Gates -Depth 12 -Compress
    $r=Get-AlternativeAcceptanceShadowDecision $Gates
    if($r.Verdict -cne $Expected -or $r.AcceptanceChanged -or $before -cne (ConvertTo-Json $Gates -Depth 12 -Compress)) {
        throw ('Decision control failed: expected='+$Expected+'; actual='+$r.Verdict+'; gates='+$before)
    }
    $script:controls++
}
$gates=[ordered]@{scenario_scope='pass';recorded_rejections='pass';candidate_coverage='pass';route_cycle='pass';
    first_tuna_payload='pass';second_tuna_payload='pass';terminal_integrity='pass';remaining_failures='pass'}
CheckDecision $gates 'would_pass' # Detached truth-table positive, not scenario evidence.
foreach($key in @($gates.Keys)) {
    foreach($value in @('fail','unproven','PASS',$true,'')) {
        $copy=[ordered]@{}; foreach($k in $gates.Keys) { $copy[$k]=$gates[$k] }
        $copy[$key]=$value
        CheckDecision $copy $(if($value -is [string] -and $value -ceq 'fail') {'would_fail'} else {'unproven'})
    }
    $copy=[ordered]@{}; foreach($k in $gates.Keys) { if($k -cne $key) { $copy[$k]=$gates[$k] } }
    CheckDecision $copy 'unproven'
}
CheckDecision $null 'unproven'; CheckDecision 'pass' 'unproven'
$extra=@{}; foreach($k in $gates.Keys) { $extra[$k]=$gates[$k] }; $extra.extra='pass'; CheckDecision $extra 'unproven'
$mixed=$extra.Clone(); $mixed.Remove('extra'); $mixed.route_cycle='fail'; $mixed.terminal_integrity='unproven'; CheckDecision $mixed 'would_fail'

# Detached shape controls exercise fail-closed composition only. No production
# authority or provenance is inferred from these fabricated diagnostic values.
$diagnostic=[ordered]@{Status='matched_original_yield_receipts';ReciprocalStatus='matched_original_reciprocal_receipts';
    RejectionChainStatus='matched_original_rejection_chain';DispatchWindowStatus='no_matching_bound_dispatch_observed_in_capture';
    SecurityLifecycleStatus='matched_recorded_security_context_spans';FullCompactParity='pass';RejectionChainFullCompactParity='pass';
    DispatchWindowFullCompactParity='pass';SecurityLifecycleFullCompactParity='pass';InputsUnchanged=$true;
    DispatchWindowCustodyControl=$true;SecurityLifecycleCustodyControl=$true;ClassifierEnabled=$false;AcceptanceChanged=$false;Live=$false;
    DispatchWindow=@{MatchingDispatchCount=0;RuntimeRetirementProven=$false};
    SecurityLifecycle=@{SecurityLifetimeProven=$false;RuntimeRetirementProven=$false};WitnessOrigins=@(1,2)}
$event=[pscustomobject]@{EventName='filetransfer_data_path_receive_result';LineNumber=2}
$record=[pscustomobject]@{Event=$event;Recovered=$false;RejectionDiagnostic=[pscustomobject]@{
    Recovered=$false;RejectionChainMatched=$true;Reason='missing_exact_tuna_receive';
    Variant='second_tuna_rejected_regular_ingress_exact_tuna_receive_missing'}}
$proof=[pscustomobject]@{CandidateRecords=@($record);UnresolvedCount=1;EffectiveCandidateRecords=@($record,$record)}
$failure=[pscustomobject]@{Event=$event;Class='file_tuna_data_path_mismatch'}
function CheckShadow($Diagnostic,$Proof,$Failure,[bool]$Matched,[string]$Edge) {
    $before=ConvertTo-Json @($Diagnostic,$Proof,$Failure) -Depth 16 -Compress
    $r=Get-AlternativeAcceptanceShadow $Diagnostic $Proof $Failure
    if(($null -ne $r.Candidate) -ne $Matched -or $r.FirstMissingEdge -cne $Edge -or $r.Decision.Verdict -cne 'unproven' -or
        $r.ClassifierEnabled -or $r.AcceptanceChanged -or $r.Live -or
        $before -cne (ConvertTo-Json @($Diagnostic,$Proof,$Failure) -Depth 16 -Compress)) { throw ('Shadow control failed: expected='+$Edge+'; '+($r | ConvertTo-Json -Depth 12 -Compress)) }
    if($Matched -and ($r.Candidate.StrictRecovered -or $r.Candidate.SecurityLifetimeProven -or
        $r.Candidate.RuntimeRetirementProven -or $r.Candidate.GeneralStaleSafetyCertified)) { throw 'Shadow claim widened' }
    $script:controls++
}
CheckShadow $diagnostic $proof $failure $true 'owned_scenario_gate_results_unavailable'
foreach($key in @('Status','ReciprocalStatus','RejectionChainStatus','DispatchWindowStatus','SecurityLifecycleStatus',
    'FullCompactParity','RejectionChainFullCompactParity','DispatchWindowFullCompactParity','SecurityLifecycleFullCompactParity')) {
    $copy=[ordered]@{}; foreach($k in $diagnostic.Keys) { if($k -cne $key) { $copy[$k]=$diagnostic[$k] } }
    CheckShadow $copy $proof $failure $false ('shadow_prerequisite:'+ $key)
    $copy[$key]='unproven'; CheckShadow $copy $proof $failure $false ('shadow_prerequisite:'+ $key)
}
foreach($key in @('InputsUnchanged','DispatchWindowCustodyControl','SecurityLifecycleCustodyControl','ClassifierEnabled','AcceptanceChanged','Live')) {
    $copy=[ordered]@{}; foreach($k in $diagnostic.Keys) { $copy[$k]=$diagnostic[$k] }; $copy[$key]=-not $copy[$key]
    CheckShadow $copy $proof $failure $false 'shadow_observation_limits'
}
$copy=[ordered]@{}; foreach($k in $diagnostic.Keys) { $copy[$k]=$diagnostic[$k] }
$copy.DispatchWindow=@{MatchingDispatchCount=1;RuntimeRetirementProven=$false}
CheckShadow $copy $proof $failure $false 'shadow_observation_limits'
$copy.DispatchWindow=$diagnostic.DispatchWindow; $copy.WitnessOrigins=@(1..65)
CheckShadow $copy $proof $failure $false 'shadow_observation_limits'
$copy.WitnessOrigins=@(); CheckShadow $copy $proof $failure $false 'shadow_observation_limits'
$copy.WitnessOrigins=$diagnostic.WitnessOrigins; $copy.SecurityLifecycle=@{SecurityLifetimeProven=$true;RuntimeRetirementProven=$false}
CheckShadow $copy $proof $failure $false 'shadow_observation_limits'
$record.Recovered=$true; CheckShadow $diagnostic $proof $failure $false 'shadow_candidate_count'; $record.Recovered=$false
$record.RejectionDiagnostic.Recovered=$true; CheckShadow $diagnostic $proof $failure $false 'shadow_original_candidate'; $record.RejectionDiagnostic.Recovered=$false
$record.RejectionDiagnostic.RejectionChainMatched=$false
CheckShadow $diagnostic $proof $failure $false 'shadow_original_candidate'; $record.RejectionDiagnostic.RejectionChainMatched=$true
$record.RejectionDiagnostic.Variant='unrelated'; CheckShadow $diagnostic $proof $failure $false 'shadow_candidate_scope'
$record.RejectionDiagnostic.Reason='missing_tuna_resend'; $record.RejectionDiagnostic.Variant='second_tuna_rejected_regular_ingress_exact_tuna_resend_missing'
CheckShadow $diagnostic $proof $failure $true 'owned_scenario_gate_results_unavailable'
$failure.Event=$event.PSObject.Copy(); CheckShadow $diagnostic $proof $failure $false 'shadow_candidate_membership'; $failure.Event=$event
$other=[pscustomobject]@{Event=[pscustomobject]@{EventName='filetransfer_data_path_receive_result';LineNumber=3};Recovered=$false}
$proof.CandidateRecords=@($record,$other); $proof.UnresolvedCount=2
CheckShadow $diagnostic $proof $failure $true 'shadow_candidate_coverage'
$r=Get-AlternativeAcceptanceShadow $diagnostic $proof $failure
if($r.Gates.candidate_coverage -cne 'unproven' -or $r.Gates.recorded_rejections -cne 'unproven' -or
    $r.OriginalUnresolvedCandidates -ne 2 -or $r.EvaluatedCandidates -ne 1) { throw 'Unchecked candidate was waived' }
$proof.UnresolvedCount=1; CheckShadow $diagnostic $proof $failure $false 'shadow_candidate_count'
$proof.CandidateRecords=@(); $proof.UnresolvedCount=0; CheckShadow $diagnostic $proof $failure $false 'shadow_candidate_membership'
CheckShadow $diagnostic $null $failure $false 'shadow_original_candidate'
'alternative-acceptance-controls='+([ordered]@{Passed=$controls;DetachedOnly=$true;AuthorityEvaluations=0;
    GenuineScenarioPositive=$false;AcceptanceChanged=$false} | ConvertTo-Json -Compress)
