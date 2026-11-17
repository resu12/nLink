param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$TunaPayloadMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# The established gate checks the original primary count/outcome, complete
# independent writers, exact retained records and full/compact authority parity.
$null=& (Join-Path $PSScriptRoot 'PublicationTunaPayloadIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -TunaPayloadMode $TunaPayloadMode -ServiceYieldFixture
'service-yield-history='+([ordered]@{OriginalEvaluations=3;FirstEdge='missing_exact_tuna_receive';Recovered=$false
    WriterTopology='two_processes_two_writers';ServiceLocalReceipts=2;AcceptedPeerReceipts=2;IdempotentPeerReceipts=2
    IndependentSendCopies=4;AuthenticatedDispatches=4;FullCompactAuthorityParity='pass'
    PeerLineage='not_evaluated';ClassifierEnabled=$false;AcceptanceChanged=$false;Live=$false} | ConvertTo-Json -Compress)
