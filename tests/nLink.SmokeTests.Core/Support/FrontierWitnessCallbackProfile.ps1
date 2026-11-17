function Add-FrontierWitnessCallbackProfileForTest {
    param([Parameter(Mandatory)][string]$Definition)
    if($Definition.Contains('# witness-profile-begin')) {throw 'Witness callback already profiled'}
    $newline="`n"; if($Definition.Contains("`r`n")) {$newline="`r`n"}
    # Insert only fixed checkpoints into the original in-memory function. No row
    # hooks, additional visits, budget changes, or evidence re-evaluation.
    $anchors=@(
        '$before=Snapshot',
        '$failurePosition=-1',
        "Require (`$failurePosition -ge 0) 'native_frontier_failure_origin'",
        '$chosen=$matches[0].Native; $receipt=$matches[0].Receipt; Scope $failureRow',
        '$entries=@{}; $last=0',
        '$markerRows=@(',
        '$boundarySnapshot=',
        '$startCount=0; $endCount=0',
        'Require ((Snapshot) -ceq $before'
    )
    # The ownership scan has multiple syntactically similar loops. Bind its
    # unique preceding statement, not a first-match search over arbitrary loops.
    for($phase=0;$phase -lt $anchors.Count;$phase++) {
        $anchor=$anchors[$phase]
        $position=$Definition.IndexOf($anchor,[StringComparison]::Ordinal)
        if($position -lt 0 -or $Definition.IndexOf($anchor,$position+$anchor.Length,[StringComparison]::Ordinal) -ge 0) {
            throw ('Witness callback profile seam changed: '+$phase)
        }
        if($phase -eq 3) {$position+=$anchor.Length}
        $block=$newline+'# witness-profile-begin'+$newline+'$witnessProfilePhaseForTest='+$phase+$newline+
            'try {$Custody.RecordWitnessCallbackForTest($witnessProfilePhaseForTest,$rows.Count,$owned.Count,$selected.Count,$false,$false)} catch {}'+$newline+
            '# witness-profile-end'+$newline
        $Definition=$Definition.Insert($position,$block)
    }
    foreach($terminal in @(
        @{Anchor='return [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+NativeWitnessSelection]::new';After=$false;Failed='$false'},
        @{Anchor='} catch {$state.Error=$_.Exception.Message; return $null}';After=$true;Failed='$true'}
    )) {
        $position=$Definition.IndexOf($terminal.Anchor,[StringComparison]::Ordinal)
        if($position -lt 0 -or $Definition.IndexOf($terminal.Anchor,$position+$terminal.Anchor.Length,[StringComparison]::Ordinal) -ge 0) {
            throw 'Witness callback terminal profile seam changed'
        }
        if($terminal.After) {$position+='} catch {$state.Error=$_.Exception.Message;'.Length}
        $block=$newline+'# witness-profile-begin'+$newline+
            'try {$Custody.RecordWitnessCallbackForTest($witnessProfilePhaseForTest,$rows.Count,$owned.Count,$selected.Count,$true,'+$terminal.Failed+')} catch {}'+$newline+
            '# witness-profile-end'+$newline
        $Definition=$Definition.Insert($position,$block)
    }
    return $Definition
}
