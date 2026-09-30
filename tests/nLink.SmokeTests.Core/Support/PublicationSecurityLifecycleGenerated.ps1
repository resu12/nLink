param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$TunaPayloadMode,[switch]$OmitDispatch)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_SERVICE_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_RECIPROCAL_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_REJECTION_CHAIN_CONSUMER='1'
$env:NLINK_PUBLICATION_DISPATCH_WINDOW_CONSUMER=if($OmitDispatch) {'0'} else {'1'}
$env:NLINK_PUBLICATION_DISPATCH_WINDOW_CONTROLS='0'
$env:NLINK_PUBLICATION_SECURITY_LIFECYCLE_CONSUMER='1'
$null=& (Join-Path $PSScriptRoot 'PublicationTunaPayloadIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -TunaPayloadMode $TunaPayloadMode -ServiceYieldFixture
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
if(Test-Path -LiteralPath (Join-Path $output 'alternative-acceptance-shadow.json')) { throw 'Alternative acceptance shadow was not default-off' }
$errorPath=Join-Path $output 'service-yield-consumer-error.txt'
if(Test-Path -LiteralPath $errorPath) { throw ([IO.File]::ReadAllText($errorPath)) }
$r=Get-Content -Raw (Join-Path $output 'service-yield-consumer.json') | ConvertFrom-Json
if($r.Status -cne 'matched_original_yield_receipts' -or $r.ReciprocalStatus -cne 'matched_original_reciprocal_receipts' -or
    $r.RejectionChainStatus -cne 'matched_original_rejection_chain' -or -not $r.InputsUnchanged -or
    $r.FullCompactParity -cne 'pass' -or $r.RejectionChainFullCompactParity -cne 'pass' -or
    $r.WitnessOrigins.Count -gt 64 -or $r.ClassifierEnabled -or $r.AcceptanceChanged -or $r.Live) { throw 'Lifecycle changed earlier results' }
if($OmitDispatch) {
    if($r.SecurityLifecycleStatus -cne 'unproven' -or $r.SecurityLifecycleFirstMissingEdge -cne 'security_span_requires_dispatch_capture' -or
        $null -ne $r.PSObject.Properties['SecurityLifecycle'] -or $null -ne $r.PSObject.Properties['DispatchWindowStatus']) { throw 'Lifecycle prerequisite omission changed' }
    'security-lifecycle-omission='+([ordered]@{Status=$r.SecurityLifecycleStatus;Reason=$r.SecurityLifecycleFirstMissingEdge;
        OriginalEvaluations=3;FirstEdge='missing_exact_tuna_receive';Recovered=$false;AcceptanceChanged=$false;Live=$false} | ConvertTo-Json -Compress)
    return
}
if($r.SecurityLifecycleStatus -cne 'matched_recorded_security_context_spans') { throw ($r | ConvertTo-Json -Depth 12 -Compress) }
$s=$r.SecurityLifecycle
if($r.SecurityLifecycleFullCompactParity -cne 'pass' -or -not $r.SecurityLifecycleCustodyControl -or
    $s.Contexts.Count -ne 2 -or $s.SelectedSourceLines.Count -ne 8 -or $s.SecurityLifetimeProven -or $s.RuntimeRetirementProven -or
    $r.DispatchWindowStatus -cne 'no_matching_bound_dispatch_observed_in_capture' -or $r.DispatchWindowFullCompactParity -cne 'pass') { throw 'Lifecycle contract changed' }
foreach($context in $s.Contexts) {
    if($context.InstallSequence -ge ($context.UseSequences | Measure-Object -Minimum).Minimum -or
        $context.RetireSequence -le ($context.UseSequences | Measure-Object -Maximum).Maximum -or $context.RetireSequence -gt $context.CaptureEnd) { throw 'Lifecycle span order changed' }
    foreach($origin in @($context.InstallSourceLine,$context.RetireSourceLine)) {
        if($r.WitnessOrigins -notcontains $origin) { throw 'Lifecycle witness omitted' }
    }
}
'security-lifecycle-consumer='+([ordered]@{Status=$r.SecurityLifecycleStatus;Contexts=$s.Contexts.Count;SelectedRows=$s.SelectedSourceLines.Count;
    FullCompactParity=$r.SecurityLifecycleFullCompactParity;Controls=$r.SecurityLifecycleControls;CustodyControl=$r.SecurityLifecycleCustodyControl;
    OriginalEvaluations=3;FirstEdge='missing_exact_tuna_receive';Recovered=$false;SecurityLifetimeProven=$false;RuntimeRetirementProven=$false;
    ClassifierEnabled=$false;AcceptanceChanged=$false;Live=$false} | ConvertTo-Json -Depth 8 -Compress)
