param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$TunaPayloadMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_SERVICE_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_RECIPROCAL_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_REJECTION_CHAIN_CONSUMER='0'
$env:NLINK_PUBLICATION_DISPATCH_WINDOW_CONSUMER='1'
$null=& (Join-Path $PSScriptRoot 'PublicationTunaPayloadIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -TunaPayloadMode $TunaPayloadMode -ServiceYieldFixture
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$result=Get-Content -Raw (Join-Path $output 'service-yield-consumer.json') | ConvertFrom-Json
if($result.Status -cne 'matched_original_yield_receipts' -or $result.ReciprocalStatus -cne 'matched_original_reciprocal_receipts' -or
    $null -ne $result.PSObject.Properties['RejectionChainStatus'] -or $null -ne $result.PSObject.Properties['DispatchWindow'] -or
    $result.DispatchWindowStatus -cne 'unproven' -or $result.DispatchWindowFirstMissingEdge -cne 'dispatch_requires_rejection_chain' -or
    $result.ClassifierEnabled -or $result.AcceptanceChanged -or $result.Live -or -not $result.InputsUnchanged -or
    $result.FullCompactParity -cne 'pass' -or $result.WitnessOrigins.Count -gt 64) { throw 'Optional dispatch failure replaced the original results' }
'dispatch-window-omission='+([ordered]@{Status=$result.DispatchWindowStatus;Reason=$result.DispatchWindowFirstMissingEdge;
    YieldStatus=$result.Status;ReciprocalStatus=$result.ReciprocalStatus;OriginalEvaluations=3;FirstEdge='missing_exact_tuna_receive';
    Recovered=$false;ClassifierEnabled=$false;AcceptanceChanged=$false;Live=$false} | ConvertTo-Json -Compress)
