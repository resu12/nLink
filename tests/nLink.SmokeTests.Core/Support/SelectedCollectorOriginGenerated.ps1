param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$case=$env:NLINK_TEST_MEMBERSHIP_BORROWER_CASE
if($case -cnotin @('active','result-dispose','borrower-dispose','invalid-map','unavailable')) {throw 'selected_origin_case'}
$env:NLINK_TEST_SELECTED_ORIGIN_JOIN='1';$env:NLINK_TEST_SELECTED_ORIGIN_CASE=$case
$env:NLINK_TEST_MEMBERSHIP_BORROWER_CASE=if($case -ceq 'borrower-dispose') {'dispose'} elseif($case -ceq 'unavailable') {'unavailable'} else {'active'}
$null=& (Join-Path $PSScriptRoot 'SelectedNativeWriterGenerated.ps1') @PSBoundParameters
$output=Join-Path $Directory ('selected-native-'+$PSVersionTable.PSVersion.Major)
$result=Get-Content (Join-Path $output 'selected-origin.json') -Raw|ConvertFrom-Json
$cleanup=Get-Content (Join-Path $output 'selected-origin-cleanup.json') -Raw|ConvertFrom-Json
if(-not $result.OriginalCompletionCurrent -or $result.AdditionalWriterReads -ne 0 -or $result.AcceptanceChanged -or
    -not $cleanup.RetiredAfterContext -or -not $cleanup.RetiredAfterWorker) {throw 'selected_origin_original_or_cleanup'}
$expected=if($case -ceq 'unavailable') {'unavailable'} elseif($case -ceq 'invalid-map') {'invalid_map_rejected'} else {'verified_offline_selected_collector_origin_only'}
$issued=$case -cnotin @('invalid-map','unavailable')
if($result.Status -cne $expected -or $cleanup.Issued -ne $issued) {throw 'selected_origin_outcome'}
if($issued -and ($result.SelectedIndex -ne 7 -or $result.SourceLine -ne 8 -or $result.WriterLine -ne 15 -or
    $result.ProducerLine -ne $result.RetainedLine -or $result.ProducerLine -lt 1 -or $result.JoinRecordVisits -ne 1 -or
    $result.CumulativeRecordVisits -ne 162 -or $result.WorkMilliseconds -gt 20000 -or $result.PreControls -ne 9 -or
    $result.PostControls -ne 13 -or -not $result.RepeatedJoinRejected -or -not $result.BorrowerCurrent)) {throw 'selected_origin_binding_contract'}
'selected-collector-origin='+([ordered]@{Case=$case;Status=$expected;OriginalWitnessAndCompletion=$true
    OneWriterRead=$true;OriginalEvaluations=3;OriginIssued=$issued;OriginRetired=$true;AcceptanceChanged=$false;Live=$false}|ConvertTo-Json -Compress)
