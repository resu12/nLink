# Test-only decision logic. No I/O, authority evaluation, runtime effects or
# acceptance capability. Only the original-custody caller may bind observations.
function Get-AlternativeAcceptanceShadowDecision($Gates) {
    $required=@('scenario_scope','recorded_rejections','candidate_coverage','route_cycle',
        'first_tuna_payload','second_tuna_payload','terminal_integrity','remaining_failures')
    $unproven=@(); $failed=@()
    if($Gates -isnot [Collections.IDictionary]) { $Gates=@{} }
    if($Gates.Count -ne $required.Count -or @($Gates.Keys | Where-Object { $_ -cnotin $required }).Count -ne 0) { $unproven+='gate_schema' }
    foreach($name in $required) {
        if(-not $Gates.Contains($name) -or $Gates[$name] -isnot [string] -or $Gates[$name] -cnotin @('pass','fail','unproven')) {
            $unproven+=$name
        } elseif($Gates[$name] -ceq 'fail') { $failed+=$name }
        elseif($Gates[$name] -ceq 'unproven') { $unproven+=$name }
    }
    [ordered]@{Verdict=$(if($failed.Count -gt 0) {'would_fail'} elseif($unproven.Count -gt 0) {'unproven'} else {'would_pass'});
        FailedGates=$failed;UnprovenGates=$unproven;AcceptanceChanged=$false}
}
function Get-AlternativeRecordedRejection($Diagnostic,$Record,[int]$SourceLine) {
    $d=$Record.RejectionDiagnostic
    YRequire ($Record.Event.EventName -ceq 'filetransfer_data_path_receive_result' -and
        $Record.Event.LineNumber -eq $SourceLine -and $SourceLine -gt 0 -and
        $Record.Recovered -is [bool] -and -not $Record.Recovered -and $d.Recovered -is [bool] -and -not $d.Recovered -and
        $d.RejectionChainMatched -is [bool] -and $d.RejectionChainMatched) 'shadow_original_candidate'
    $variants=@{missing_tuna_resend='second_tuna_rejected_regular_ingress_exact_tuna_resend_missing';
        missing_exact_tuna_receive='second_tuna_rejected_regular_ingress_exact_tuna_receive_missing'}
    YRequire ($d.Reason -cin @('missing_tuna_resend','missing_exact_tuna_receive') -and
        $d.Variant -ceq $variants[$d.Reason]) 'shadow_candidate_scope'
    foreach($pair in @(@('Status','matched_original_yield_receipts'),@('ReciprocalStatus','matched_original_reciprocal_receipts'),
        @('RejectionChainStatus','matched_original_rejection_chain'),
        @('DispatchWindowStatus','no_matching_bound_dispatch_observed_in_capture'),
        @('SecurityLifecycleStatus','matched_recorded_security_context_spans'),@('FullCompactParity','pass'),
        @('RejectionChainFullCompactParity','pass'),@('DispatchWindowFullCompactParity','pass'),@('SecurityLifecycleFullCompactParity','pass'))) {
        YRequire ($Diagnostic.Contains($pair[0]) -and $Diagnostic[$pair[0]] -ceq $pair[1]) ('shadow_prerequisite:'+ $pair[0])
    }
    YRequire ($Diagnostic.InputsUnchanged -and $Diagnostic.DispatchWindowCustodyControl -and $Diagnostic.SecurityLifecycleCustodyControl -and
        -not $Diagnostic.ClassifierEnabled -and -not $Diagnostic.AcceptanceChanged -and -not $Diagnostic.Live -and
        $Diagnostic.DispatchWindow.MatchingDispatchCount -eq 0 -and -not $Diagnostic.DispatchWindow.RuntimeRetirementProven -and
        -not $Diagnostic.SecurityLifecycle.SecurityLifetimeProven -and -not $Diagnostic.SecurityLifecycle.RuntimeRetirementProven -and
        $Diagnostic.WitnessOrigins.Count -gt 0 -and $Diagnostic.WitnessOrigins.Count -le 64) 'shadow_observation_limits'
    [ordered]@{SourceLine=$SourceLine;StrictReason=$d.Reason;StrictVariant=$d.Variant;StrictRecovered=$false;
        Status='recorded_stale_route_rejection';SecurityLifetimeProven=$false;RuntimeRetirementProven=$false;GeneralStaleSafetyCertified=$false}
}
function Get-AlternativeAcceptanceShadow($Diagnostic,$Proof,$Failure) {
    $gates=[ordered]@{scenario_scope='unproven';recorded_rejections='unproven';candidate_coverage='unproven';route_cycle='unproven';
        first_tuna_payload='unproven';second_tuna_payload='unproven';terminal_integrity='unproven';remaining_failures='unproven'}
    $result=[ordered]@{Schema='nlink.alternative-acceptance-shadow/v1';Scope='original_primary_selected_candidate';
        Candidate=$null;OriginalUnresolvedCandidates=0;EvaluatedCandidates=0;Gates=$gates;
        FirstMissingEdge='shadow_original_candidate';ScenarioProofStatus='owned_scenario_gate_results_unavailable';
        ClassifierEnabled=$false;AcceptanceChanged=$false;Live=$false}
    try {
        YRequire ($null -ne $Proof -and $null -ne $Failure -and $Failure.Class -ceq 'file_tuna_data_path_mismatch') 'shadow_original_candidate'
        # EffectiveCandidateRecords contains raw companions too. Use the
        # original primary's distinct collection; do not deduplicate/reclassify.
        $all=@($Proof.CandidateRecords | Where-Object { -not $_.Recovered })
        YRequire ($Proof.UnresolvedCount -is [int] -and $Proof.UnresolvedCount -eq $all.Count) 'shadow_candidate_count'
        $result.OriginalUnresolvedCandidates=$all.Count
        $selected=YOne @($all | Where-Object { [object]::ReferenceEquals($_.Event,$Failure.Event) }) 'shadow_candidate_membership'
        $result.Candidate=Get-AlternativeRecordedRejection $Diagnostic $selected $Failure.Event.LineNumber
        $result.EvaluatedCandidates=1
        # This bounded caller owns one selected failure, not an arbitrary subset
        # capable of waiving other original candidates or unrelated failures.
        if($all.Count -eq 1) { $gates.recorded_rejections='pass'; $gates.candidate_coverage='pass' }
        $result.FirstMissingEdge=if($all.Count -eq 1) {'owned_scenario_gate_results_unavailable'} else {'shadow_candidate_coverage'}
    } catch { $result.FirstMissingEdge=$_.Exception.Message }
    # There is no issuer-bound whole-scenario gate handoff in this caller. Never
    # fill these gaps from route labels, aggregate SHA, fixture flags or JSON.
    $result.Decision=Get-AlternativeAcceptanceShadowDecision $gates
    $result
}
