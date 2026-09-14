param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
$on=@(& (Join-Path $PSScriptRoot 'PublicationPositiveIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -PersistenceMode write -LiveScaleFixture)
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$baseline=Join-Path $Directory ('observer-baseline-'+$PSVersionTable.PSVersion.Major)
[void][IO.Directory]::CreateDirectory($baseline)
# Hold the explicitly controlled GUI-summary input constant as well. No analyzer
# report, private evaluation, receipt or companion is copied into the baseline.
[IO.File]::Copy((Join-Path $output 'filetransfer-tuna-gui-summary.json'),(Join-Path $baseline 'filetransfer-tuna-gui-summary.json'))
$env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT='1'
$null=& (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'PublicationLivePrimaryWorker.ps1') `
    -Mode AnalyzeRetained -LogPath (Join-Path $output 'filetransfer-retained-log-slice.log') -ArtifactDir $baseline `
    -TailMinutes 0 -LiveRouteProofMode RegularActivationCycle
if($LASTEXITCODE -ne 0) {throw 'Default-off original primary failed'}
$off=[IO.File]::ReadAllText((Join-Path $baseline 'live-primary-observation.json')) | ConvertFrom-Json
if($off.Evaluations -ne 3 -or ($off.EvaluationCallers -join '|') -cne '<ScriptBlock>|<ScriptBlock>|Get-FileTransferRecoveryFailureClassification' -or
    $off.Prepared -or $off.Witness -or $off.Completion) {throw 'Optional capture changed original evaluator calls'}
foreach($name in @('filetransfer-operator-verdict.txt','filetransfer-retained-compact-summary.json',
    'filetransfer-coordinator-snapshot-full-summary.json','filetransfer-coordinator-snapshot-compact-summary.json',
    'filetransfer-coordinator-snapshot-parity-summary.json')) {
    $expected=[IO.File]::ReadAllText((Join-Path $baseline $name));$actual=[IO.File]::ReadAllText((Join-Path $output $name))
    if($name -ceq 'filetransfer-retained-compact-summary.json') {
        $expected=$expected -replace '("data_path_correlation_(index|total)_ms"\s*:\s*)[0-9]+','${1}0'
        $actual=$actual -replace '("data_path_correlation_(index|total)_ms"\s*:\s*)[0-9]+','${1}0'
    }
    if($actual -cne $expected) {throw ('Default-off primary artifact parity: '+$name)}
}
$on
