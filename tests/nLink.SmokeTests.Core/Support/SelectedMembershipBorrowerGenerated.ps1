param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($env:NLINK_TEST_MEMBERSHIP_BORROWER_CASE -cnotin @('active','dispose','unavailable')) {throw 'borrower_case'}
# Fixture-size admission only, not another evaluator or a live scenario proof.
$sourcePath=Join-Path $Directory 'nlink.log'
$sourceLines=[IO.File]::ReadAllLines($sourcePath)
if($sourceLines.Count -lt 1 -or $sourceLines.Count -gt 4096 -or (Get-Item -LiteralPath $sourcePath).Length -gt 4MB) {throw 'borrower_fixture_size'}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
foreach($type in @([NLink.Tools.LocalRetainedSourceRequest+Worker],[NLink.Tools.LocalRetainedSourceRequest+NativeMembership])) {
    if(@($type.GetMembers([Reflection.BindingFlags]'Public,NonPublic,Instance,Static')|Where-Object {$_.Name -match 'Borrower|borrower'}).Count) {throw 'borrower_api_in_production_loader'}
}
$previous=$env:NLINK_TEST_MEMBERSHIP_BORROWER
try {
    $env:NLINK_TEST_MEMBERSHIP_BORROWER='1'
    $null=& (Join-Path $PSScriptRoot 'PublicationPositiveIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
        -RunId $RunId -ScenarioId $ScenarioId -ObservePrimary
} finally {$env:NLINK_TEST_MEMBERSHIP_BORROWER=$previous}
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major)
$errorPath=Join-Path $output 'membership-borrower-error.txt'
if(Test-Path -LiteralPath $errorPath) {throw ([IO.File]::ReadAllText($errorPath))}
$result=Get-Content -LiteralPath (Join-Path $output 'membership-borrower.json') -Raw|ConvertFrom-Json
$cleanup=Get-Content -LiteralPath (Join-Path $output 'membership-borrower-cleanup.json') -Raw|ConvertFrom-Json
$original=Get-Content -LiteralPath (Join-Path $output 'live-primary-observation.json') -Raw|ConvertFrom-Json
if($env:NLINK_TEST_MEMBERSHIP_BORROWER_CASE -ceq 'unavailable') {
    if($result.Status -cne 'unavailable' -or -not $result.OriginalCompletionCurrent -or -not $result.OwnerStillVerified -or
        $result.AdditionalWriterReads -ne 0 -or $result.OriginalEvaluations -ne 3 -or $cleanup.Issued -or
        -not $cleanup.RetiredAfterContext -or -not $cleanup.RetiredAfterWorker -or
        -not $original.Witness -or -not $original.Completion -or $original.Evaluations -ne 3) {throw 'borrower_omission_contract'}
    'selected-membership-borrower={"Status":"unavailable","OriginalWitnessAndCompletion":true,"AcceptanceChanged":false,"Live":false}'
    return
}
if($result.Status -cne 'verified_offline_selected_membership_reuse_only' -or $result.AdditionalWriterReads -ne 0 -or
    $result.CumulativeRecordVisits -gt 4096 -or $result.WorkMilliseconds -gt 20000 -or $result.OriginalEvaluations -ne 3 -or
    -not $result.OwnerStillVerified -or -not $result.OriginalCompletionCurrent -or $result.AcceptanceChanged -or
    -not $cleanup.Issued -or -not $cleanup.RetiredAfterContext -or -not $cleanup.RetiredAfterWorker -or
    $original.Evaluations -ne 3 -or -not $original.Prepared -or -not $original.Witness -or -not $original.Completion) {throw 'borrower_integration_contract'}
'selected-membership-borrower='+([ordered]@{Status=$result.Status;PreControls=$result.PreControls;PostControls=$result.PostControls
    Case=$env:NLINK_TEST_MEMBERSHIP_BORROWER_CASE;OneWriterRead=$true;OriginalEvaluations=3;OriginalWitnessAndCompletion=$true
    OwnerSurvivesBorrower=$true;BorrowerRetired=$true;ProductionApiAbsent=$true;SeededFailure=$true;AcceptanceChanged=$false;Live=$false}|ConvertTo-Json -Compress)
