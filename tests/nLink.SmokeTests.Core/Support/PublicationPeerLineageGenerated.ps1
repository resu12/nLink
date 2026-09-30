param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$TunaPayloadMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_PEER_LINEAGE_PROTOTYPE='1'
$null=& (Join-Path $PSScriptRoot 'PublicationTunaPayloadIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -TunaPayloadMode $TunaPayloadMode
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$errorPath=Join-Path $output 'peer-lineage-prototype-error.txt'
if(Test-Path -LiteralPath $errorPath) { throw ([IO.File]::ReadAllText($errorPath)) }
$result=[IO.File]::ReadAllText((Join-Path $output 'peer-lineage-prototype.json')) | ConvertFrom-Json
if($result.Status -cne 'candidate_links_only' -or $result.PeerLineage -cne 'unproven' -or
    $result.FirstUnprovenEdge -cne 'authenticated_yield_to_coordinator_receipt' -or
    $result.ClassifierEnabled -or $result.AcceptanceChanged -or -not $result.InputsUnchanged -or
    $result.NegativeControls.Count -ne 12 -or $result.FullCompactParity -cne 'pass') { throw 'Prototype boundary changed' }
# Original primary and wrapper assertions above still require the unresolved
# missing_exact_tuna_receive result. The prototype cannot recover that failure.
'peer-lineage-prototype='+([ordered]@{Status=$result.Status;PeerLineage=$result.PeerLineage
    FirstUnprovenEdge=$result.FirstUnprovenEdge;NegativeControls=$result.NegativeControls
    FullCompactParity=$result.FullCompactParity;OriginalEvaluations=3;AcceptanceChanged=$false
    ClassifierEnabled=$false;Live=$false} | ConvertTo-Json -Compress)
