param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,
    [ValidateSet('rebatch_same','rebatch_changed','rebatch_middle')][string]$TunaPayloadMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_CONTENT_COVERAGE_SHADOW='1'
$env:NLINK_PUBLICATION_CONTENT_COVERAGE_CONTROLS='1'
$null=& (Join-Path $PSScriptRoot 'PublicationTunaPayloadIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -TunaPayloadMode $TunaPayloadMode
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$result=Get-Content -LiteralPath (Join-Path $output 'content-coverage-shadow.json') -Raw | ConvertFrom-Json
$expected=if($TunaPayloadMode -ceq 'rebatch_changed') {'unproven'} else {'pass'}
if($result.Status -cne $expected -or $result.AddedAuthorityEvaluations -ne 0 -or $result.AcceptanceChanged -or
    $result.ClassifierEnabled -or $result.Live -or $result.FullCompactParity -cne 'pass' -or
    -not $result.InputsUnchanged -or -not $result.PriorResultsUnchanged -or -not $result.CustodyControl -or
    $result.Candidate.OriginalReason -cne 'missing_tuna_resend' -or $result.Candidate.OriginalRecovered -or
    $result.WitnessOrigins.Count -lt 1 -or $result.WitnessOrigins.Count -gt 64) { throw ('Content shadow contract: '+($result | ConvertTo-Json -Depth 32 -Compress)) }
if(($expected -ceq 'pass' -and ($result.FirstMissingEdge -cne '(none)' -or $result.Coverage.ChunkIndex -ne 12 -or $result.Coverage.Bytes -ne 1024)) -or
    ($expected -ceq 'unproven' -and ($result.FirstMissingEdge -cne 'content_not_equal' -or $null -ne $result.Coverage))) { throw 'Content equality claim changed' }
if($result.CustodyControls -ne 3 -or ($expected -ceq 'pass' -and ($result.Controls.Passed -ne 35 -or
    -not $result.Controls.DetachedOnly -or $result.Controls.AddedAuthorityEvaluations -ne 0 -or $result.Controls.AcceptanceChanged))) {
    throw 'Content shadow controls missing'
}
'content-coverage='+([ordered]@{Mode=$TunaPayloadMode;Status=$result.Status;FirstMissingEdge=$result.FirstMissingEdge;
    OriginalEvaluations=3;Recovered=$false;FullCompactParity='pass';InputsUnchanged=$true;PriorResultsUnchanged=$true;
    AddedAuthorityEvaluations=0;AcceptanceChanged=$false;Live=$false} | ConvertTo-Json -Compress)
