param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,
    [ValidateSet('prior','selected')][string]$ObservationScope)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$failure=''
try {
    $null=& (Join-Path $PSScriptRoot 'PublicationPositiveIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
        -RunId $RunId -ScenarioId $ScenarioId -PersistenceMode write -ObservePrimary
} catch { $failure=$_.Exception.Message }
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$facts=[IO.File]::ReadAllText((Join-Path $output 'live-primary-observation.json')) | ConvertFrom-Json
if($facts.Evaluations -ne 3 -or ($facts.EvaluationCallers -join '|') -cne '<ScriptBlock>|<ScriptBlock>|Get-FileTransferRecoveryFailureClassification' -or
    -not $facts.Prepared -or $facts.OptionalMilliseconds -gt 20000) { throw 'Original primary evaluations or input ownership changed' }
$summary=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-coordinator-snapshot-full-summary.json')) | ConvertFrom-Json
$overCap=@($summary.Boundaries | Group-Object coordinatorInstanceId,afterStateGeneration | Where-Object Count -GT 64)
if($summary.ArtifactEvidenceVerdict -cne 'pass' -or $summary.Mode -cne 'SnapshotV1Authoritative' -or
    $overCap.Count -ne 1) {
    throw 'Fixture did not preserve genuine over-cap repeated-state evidence'
}
$report=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-operator-verdict.txt'))
$line=[regex]::Match($report,'(?m)^first_proof_chain_failure_line_number=([0-9]+)\r?$').Groups[1].Value
$raw=[IO.File]::ReadAllLines((Join-Path $output 'filetransfer-retained-log-slice.log'))[[int]$line-1]
$coordinator=[regex]::Match($raw,'; publication_evidence_coordinator_id=([^;]+);').Groups[1].Value
$generation=[regex]::Match($raw,'; publication_evidence_observed_state_generation=([0-9]+);').Groups[1].Value
$sameSelectedKey=$overCap[0].Group[0].coordinatorInstanceId -ceq $coordinator -and
    [string]$overCap[0].Group[0].afterStateGeneration -ceq $generation
if(-not $coordinator -or -not $generation -or $sameSelectedKey -ne ($ObservationScope -ceq 'selected')) {
    throw 'Over-cap bucket did not have the intended relation to the original selected witness'
}
$companion=Join-Path $output 'filetransfer-local-publication-diagnostic.json'
if($ObservationScope -ceq 'prior') {
    if($failure -or -not $facts.Witness -or -not $facts.Completion -or -not (Test-Path -LiteralPath $companion -PathType Leaf)) {
        throw "Unrelated state suppressed selected publication: failure=$failure witnessOmission=$($facts.WitnessOmission)"
    }
} else {
    if($failure -notlike 'Positive publication missing: relay=True adoption=True *' -or
        $facts.Witness -or $facts.Completion -or $facts.WitnessOmission -cne 'primary_witness_observation_budget' -or
        (Test-Path -LiteralPath $companion)) { throw "Selected observation bound changed: $failure" }
    $receipt=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-retained-analysis-attempt-receipt.json')) | ConvertFrom-Json
    if($receipt.status -cne 'completed' -or $receipt.exitCode -ne 0 -or $receipt.owner -cne 'RouteAcceptance' -or
        $report -notmatch '(?m)^first_proof_chain_failure_class=file_tuna_data_path_mismatch\r?$' -or
        $report -notmatch '(?m)^first_proof_chain_failure_event=filetransfer_data_path_send_result\r?$' -or
        $report -notmatch '(?m)^verdict=FAIL_PROTOCOL_OR_INTEGRITY\r?$') { throw 'Omission changed original primary result' }
}
'observation-budget='+([ordered]@{Scope=$ObservationScope;SelectedBucketLimit=64;OriginalEvaluations=3
    OriginalSnapshotPass=$true;Companion=($ObservationScope -ceq 'prior');AuthorityEffect='none';Live=$false} | ConvertTo-Json -Compress)
