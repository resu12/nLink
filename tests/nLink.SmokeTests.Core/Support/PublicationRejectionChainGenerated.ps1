param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$TunaPayloadMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_SERVICE_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_RECIPROCAL_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_REJECTION_CHAIN_CONSUMER='1'
$null=& (Join-Path $PSScriptRoot 'PublicationTunaPayloadIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -TunaPayloadMode $TunaPayloadMode -ServiceYieldFixture
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$errorPath=Join-Path $output 'service-yield-consumer-error.txt'
if(Test-Path -LiteralPath $errorPath) { throw ([IO.File]::ReadAllText($errorPath)) }
$result=Get-Content -Raw (Join-Path $output 'service-yield-consumer.json') | ConvertFrom-Json
if($null -ne $result.PSObject.Properties['DispatchWindowStatus']) { throw 'Dispatch window consumer was not default-off' }
if($result.Status -cne 'matched_original_yield_receipts' -or
    $result.ReciprocalStatus -cne 'matched_original_reciprocal_receipts' -or
    $result.RejectionChainStatus -cne 'matched_original_rejection_chain') { throw ($result | ConvertTo-Json -Depth 8 -Compress) }
if($result.ClassifierEnabled -or $result.AcceptanceChanged -or $result.Live -or -not $result.InputsUnchanged -or
    $result.FullCompactParity -cne 'pass' -or $result.RejectionChainFullCompactParity -cne 'pass' -or
    $result.WitnessOrigins.Count -gt 64 -or $result.OmissionControls -ne 8) { throw 'Rejection chain consumer contract changed' }
$controls=& (Join-Path $PSScriptRoot 'PublicationRejectionChainControls.ps1') -OutputDirectory $output -Candidate $result.RejectionChainCandidate
[IO.File]::WriteAllText((Join-Path $output 'rejection-chain-controls.json'),($controls | ConvertTo-Json -Depth 6 -Compress))
'rejection-chain-consumer='+([ordered]@{Status=$result.RejectionChainStatus;YieldStatus=$result.Status;ReciprocalStatus=$result.ReciprocalStatus;
    FullCompactParity=$result.RejectionChainFullCompactParity;NegativeControls=$controls.NegativeControls;
    SparseIgnoreAccepted=$controls.SparseIgnoreAccepted;ParityControl=$controls.ParityControl;SharedWitnessLimitControl=$controls.SharedWitnessLimitControl;
    OmissionControls=$result.OmissionControls;OriginalEvaluations=3;FirstEdge='missing_exact_tuna_receive';Recovered=$false;
    PeerLineage='not_evaluated';ClassifierEnabled=$false;AcceptanceChanged=$false;Live=$false} | ConvertTo-Json -Depth 6 -Compress)
