param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$TunaPayloadMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_SERVICE_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_RECIPROCAL_YIELD_CONSUMER='1'
$null=& (Join-Path $PSScriptRoot 'PublicationTunaPayloadIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -TunaPayloadMode $TunaPayloadMode -ServiceYieldFixture
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$errorPath=Join-Path $output 'service-yield-consumer-error.txt'
if(Test-Path -LiteralPath $errorPath) { throw ([IO.File]::ReadAllText($errorPath)) }
$result=Get-Content -Raw (Join-Path $output 'service-yield-consumer.json') | ConvertFrom-Json
if($result.Status -cne 'matched_original_yield_receipts') { throw ($result | ConvertTo-Json -Depth 8 -Compress) }
$single=$env:NLINK_PUBLICATION_YIELD_CASE -cin @('control_only','bulk_only')
$slot=if($env:NLINK_PUBLICATION_YIELD_CASE -cin @('bulk_only','receiver_local_first')) {'bulk'} else {'control'}
if($result.ReciprocalStatus -cne 'matched_original_reciprocal_receipts' -or $result.ClassifierEnabled -or $result.AcceptanceChanged -or
    $result.Live -or -not $result.InputsUnchanged -or $result.FullCompactParity -cne 'pass' -or $result.WitnessOrigins.Count -gt 64 -or
    $result.OmissionControls -ne 8 -or $result.DiagnosticRecords -ne $(if($single) {10} else {14}) -or
    $result.Candidate.Copy -cne $slot -or $result.ReciprocalCandidate.Copy -cne $slot) { throw 'Reciprocal consumer contract changed' }
$full=Get-Content -Raw (Join-Path $output 'filetransfer-coordinator-snapshot-full-summary.json') | ConvertFrom-Json
$local=@($full.Boundaries | Where-Object boundaryId -CEQ $result.ReciprocalCandidate.LocalBoundary)
$peer=@($full.Boundaries | Where-Object boundaryId -CEQ $result.Candidate.PeerBoundary)
if($local.Count -ne 1 -or $peer.Count -ne 1 -or
    (($local[0].afterStateGeneration -lt $peer[0].afterStateGeneration) -ne ($env:NLINK_PUBLICATION_YIELD_CASE -ceq 'receiver_local_first'))) {
    throw 'Genuine receiver receipt order changed'
}
$controls=& (Join-Path $PSScriptRoot 'PublicationReciprocalYieldControls.ps1') -OutputDirectory $output -Candidate $result.Candidate -Reciprocal $result.ReciprocalCandidate
[IO.File]::WriteAllText((Join-Path $output 'reciprocal-yield-controls.json'),($controls | ConvertTo-Json -Depth 6 -Compress))
'reciprocal-yield-consumer='+([ordered]@{Status=$result.Status;ReciprocalStatus=$result.ReciprocalStatus;Case=$env:NLINK_PUBLICATION_YIELD_CASE;
    Copy=$slot;DiagnosticRecords=$result.DiagnosticRecords;FullCompactParity=$result.FullCompactParity;NegativeControls=$controls.NegativeControls;
    OmissionControls=$result.OmissionControls;OriginalEvaluations=3;FirstEdge='missing_exact_tuna_receive';Recovered=$false;
    PeerLineage='not_evaluated';ClassifierEnabled=$false;AcceptanceChanged=$false;Live=$false} | ConvertTo-Json -Compress)
