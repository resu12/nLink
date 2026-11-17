param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$TunaPayloadMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_SERVICE_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_RECIPROCAL_YIELD_CONSUMER='0'
$env:NLINK_PUBLICATION_REJECTION_CHAIN_CONSUMER='1'
$null=& (Join-Path $PSScriptRoot 'PublicationTunaPayloadIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -TunaPayloadMode $TunaPayloadMode -ServiceYieldFixture
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$result=Get-Content -Raw (Join-Path $output 'service-yield-consumer.json') | ConvertFrom-Json
if($null -ne $result.PSObject.Properties['DispatchWindowStatus']) { throw 'Dispatch window consumer was not default-off' }
if($result.Status -cne 'matched_original_yield_receipts' -or $result.FirstMissingEdge -cne '(none)' -or
    $null -ne $result.PSObject.Properties['ReciprocalStatus'] -or $null -ne $result.PSObject.Properties['RejectionChainCandidate'] -or
    $result.RejectionChainStatus -cne 'unproven' -or $result.RejectionChainFirstMissingEdge -cne 'rejection_requires_reciprocal' -or
    $result.ClassifierEnabled -or $result.AcceptanceChanged -or $result.Live -or -not $result.InputsUnchanged -or
    $result.FullCompactParity -cne 'pass' -or $result.WitnessOrigins.Count -gt 64 -or $result.OmissionControls -ne 8) {
    throw 'Optional rejection failure replaced the original yield/primary result'
}
'rejection-chain-omission='+([ordered]@{Status=$result.RejectionChainStatus;Reason=$result.RejectionChainFirstMissingEdge;
    YieldStatus=$result.Status;OriginalEvaluations=3;FirstEdge='missing_exact_tuna_receive';Recovered=$false;
    PeerLineage='not_evaluated';ClassifierEnabled=$false;AcceptanceChanged=$false;Live=$false} | ConvertTo-Json -Compress)
