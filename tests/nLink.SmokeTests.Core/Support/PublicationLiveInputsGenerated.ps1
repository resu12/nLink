param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# All bulk records came from an actual service/transport transfer over FakeNkn;
# the existing isolated failure fixture follows it. No historical input replay.
$result=& (Join-Path $PSScriptRoot 'PublicationPositiveIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -PersistenceMode write -LiveScaleFixture
if(-not (@($result) -match '^positive-integration=')) { throw 'Live-shaped primary/persistence gate missing' }
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$data=[IO.File]::ReadAllText((Join-Path $output 'adopted-offline-publication.json')) | ConvertFrom-Json
$observed=[IO.File]::ReadAllText((Join-Path $output 'live-primary-observation.json')) | ConvertFrom-Json
if($data.inputs.source.byteCount -lt 108501606 -or $data.inputs.source.lineCount -lt 81941 -or
    $data.inputs.writer.byteCount -lt 140661859 -or $data.inputs.writer.lineCount -lt 117572 -or
    $observed.FailureBucketRecords -lt 9180) {throw 'Generated input does not cover known live source/writer/selector shape'}
'live-input-integration='+([ordered]@{GenuineOfflineProducer=$true;PrimaryFailurePreserved=$true;Persisted=$true;Live=$false} | ConvertTo-Json -Compress)
