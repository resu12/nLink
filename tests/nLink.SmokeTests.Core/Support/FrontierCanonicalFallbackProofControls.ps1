param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'RouteAcceptance parse failed'}
$reader=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Read-RouteAcceptanceKeyValueArtifact'},$true))
if($reader.Count -ne 1) {throw 'Ambiguous report reader'}
Invoke-Expression $reader[0].Extent.Text
$cases=@('valid','receipt-mode','receipt-status','receipt-completed','receipt-missing','failure-incomplete','failure-other',
    'exit-nonzero','exit-unavailable','timeout','input-changed',
    'full-mode','compact-mode','full-stream','compact-stream','full-verdict','compact-verdict','parity-fail','parity-missing',
    'full-missing','compact-missing','full-malformed',
    'operator-verdict','operator-gate','operator-class','operator-runtime','operator-hard','operator-missing','operator-malformed',
    'route-pass','route-missing','route-malformed')
foreach($case in $cases) {
    $target=Join-Path $Directory $case; [void][IO.Directory]::CreateDirectory($target)
    $receipt=[pscustomobject]@{status='completed';completedProofArtifacts=$true;coordinatorSnapshotMode='SnapshotV1Authoritative';exitCode=0;exitCodeAvailable=$true;timedOut=$false;analyzerInputUnchanged=$true}
    $failure=''
    switch($case) {
        'receipt-mode' {$receipt.coordinatorSnapshotMode='LegacyV0'}
        'receipt-status' {$receipt.status='proof_incomplete'}
        'receipt-completed' {$receipt.completedProofArtifacts=$false}
        'receipt-missing' {$receipt=$null}
        'failure-incomplete' {$failure="FileTransfer-Ops retained analysis exited successfully without complete proof artifacts for $target"}
        'failure-other' {$failure='unrelated original failure'}
        'exit-nonzero' {$receipt.exitCode=1}
        'exit-unavailable' {$receipt.exitCodeAvailable=$false}
        'timeout' {$receipt.timedOut=$true}
        'input-changed' {$receipt.analyzerInputUnchanged=$false}
    }
    foreach($name in @('full','compact')) {
        $view=@{Mode='SnapshotV1Authoritative';StreamComplete=$true;ArtifactEvidenceVerdict='pass'}
        if($case -ceq ($name+'-mode')) {$view.Mode='LegacyV0'}
        if($case -ceq ($name+'-stream')) {$view.StreamComplete=$false}
        if($case -ceq ($name+'-verdict')) {$view.ArtifactEvidenceVerdict='fail'}
        if($case -cne ($name+'-missing')) {
            $json=if($case -ceq ($name+'-malformed')) {'{invalid'} else {$view | ConvertTo-Json -Compress}
            [IO.File]::WriteAllText((Join-Path $target ('filetransfer-coordinator-snapshot-'+$name+'-summary.json')),$json)
        }
    }
    if($case -cne 'parity-missing') {
        [IO.File]::WriteAllText((Join-Path $target 'filetransfer-coordinator-snapshot-parity-summary.json'),(@{Verdict=$(if($case -ceq 'parity-fail'){'fail'}else{'pass'})} | ConvertTo-Json -Compress))
    }
    $operator=[ordered]@{verdict='INCONCLUSIVE';gate_status='fail';acceptance_failure_class='incomplete_proof';runtime_correctness_verdict='incomplete_proof';hard_failure_count='0'}
    switch($case) {
        'operator-verdict' {$operator.verdict='PASS'}
        'operator-gate' {$operator.gate_status='pass'}
        'operator-class' {$operator.acceptance_failure_class='none'}
        'operator-runtime' {$operator.runtime_correctness_verdict='pass'}
        'operator-hard' {$operator.hard_failure_count='1'}
    }
    if($case -cne 'operator-missing') {
        $report=if($case -ceq 'operator-malformed') {'invalid'} else {($operator.GetEnumerator() | ForEach-Object {$_.Key+'='+$_.Value}) -join "`n"}
        [IO.File]::WriteAllText((Join-Path $target 'filetransfer-operator-verdict.txt'),$report)
    }
    if($case -cne 'route-missing') {
        $report=switch($case) {'route-pass' {'live_route_epoch_proof_verdict=pass'} 'route-malformed' {'invalid'} default {'live_route_epoch_proof_verdict=fail'}}
        [IO.File]::WriteAllText((Join-Path $target 'filetransfer-route-consistency-summary.txt'),$report)
    }
    $rejected=$false
    try {& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalFallbackProof.ps1') -Directory $target -Receipt $receipt -AnalysisFailure $failure}
    catch {$rejected=$true}
    if($rejected -ne ($case -cne 'valid')) {throw ('Canonical fallback proof outcome changed: '+$case)}
}
'canonical-fallback-proof-controls='+$cases.Count
& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalReceiptGuardControls.ps1') -RepoRoot $RepoRoot -Directory (Join-Path $Directory 'valid')
