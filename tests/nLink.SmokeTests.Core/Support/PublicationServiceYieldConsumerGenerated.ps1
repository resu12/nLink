param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$TunaPayloadMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_SERVICE_YIELD_CONSUMER='1'
$null=& (Join-Path $PSScriptRoot 'PublicationServiceYieldGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory -RunId $RunId -ScenarioId $ScenarioId -TunaPayloadMode $TunaPayloadMode
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$errorPath=Join-Path $output 'service-yield-consumer-error.txt'
if(Test-Path -LiteralPath $errorPath) { throw ([IO.File]::ReadAllText($errorPath)) }
$result=Get-Content -Raw (Join-Path $output 'service-yield-consumer.json') | ConvertFrom-Json
if($null -ne $result.PSObject.Properties['RejectionChainStatus']) { throw 'Rejection chain consumer was not default-off' }
if($result.Status -cne 'matched_original_yield_receipts') { throw ($result | ConvertTo-Json -Depth 8 -Compress) }
if($result.ClassifierEnabled -or $result.AcceptanceChanged -or $result.Live -or -not $result.InputsUnchanged -or
    $result.FullCompactParity -cne 'pass' -or $result.DiagnosticRecords -ne 14 -or $result.WitnessOrigins.Count -gt 64 -or $result.OmissionControls -ne 8) { throw 'Service yield consumer contract changed' }
$controls=& (Join-Path $PSScriptRoot 'PublicationServiceYieldMatcherControls.ps1') -OutputDirectory $output -Candidate $result.Candidate
[IO.File]::WriteAllText((Join-Path $output 'service-yield-matcher-controls.json'),($controls | ConvertTo-Json -Compress))
'service-yield-consumer='+([ordered]@{Status=$result.Status;FullCompactParity=$result.FullCompactParity;DiagnosticRecords=$result.DiagnosticRecords;
    NegativeControls=$controls.NegativeControls;LocalSecurityEpisodeControls=$controls.LocalSecurityEpisodeControls;DetachedPositiveControls=$controls.DetachedPositiveControls;
    OmissionControls=$result.OmissionControls;OriginalEvaluations=3;FirstEdge='missing_exact_tuna_receive';Recovered=$false;
    PeerLineage='not_evaluated';ClassifierEnabled=$false;AcceptanceChanged=$false;Live=$false} | ConvertTo-Json -Compress)
