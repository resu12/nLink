param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$TunaPayloadMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_SERVICE_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_RECIPROCAL_YIELD_CONSUMER='1'
$env:NLINK_PUBLICATION_REJECTION_CHAIN_CONSUMER='1'
$env:NLINK_PUBLICATION_DISPATCH_WINDOW_CONSUMER='1'
$env:NLINK_PUBLICATION_DISPATCH_WINDOW_CONTROLS='1'
$null=& (Join-Path $PSScriptRoot 'PublicationTunaPayloadIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -TunaPayloadMode $TunaPayloadMode -ServiceYieldFixture
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$errorPath=Join-Path $output 'service-yield-consumer-error.txt'
if(Test-Path -LiteralPath $errorPath) { throw ([IO.File]::ReadAllText($errorPath)) }
$result=Get-Content -Raw (Join-Path $output 'service-yield-consumer.json') | ConvertFrom-Json
if($null -ne $result.PSObject.Properties['SecurityLifecycleStatus']) { throw 'Security lifecycle consumer was not default-off' }
if($result.Status -cne 'matched_original_yield_receipts' -or $result.ReciprocalStatus -cne 'matched_original_reciprocal_receipts' -or
    $result.RejectionChainStatus -cne 'matched_original_rejection_chain' -or
    $result.DispatchWindowStatus -cne 'no_matching_bound_dispatch_observed_in_capture') { throw ($result | ConvertTo-Json -Depth 12 -Compress) }
$window=$result.DispatchWindow
if($result.ClassifierEnabled -or $result.AcceptanceChanged -or $result.Live -or -not $result.InputsUnchanged -or
    $result.FullCompactParity -cne 'pass' -or $result.RejectionChainFullCompactParity -cne 'pass' -or
    $result.DispatchWindowFullCompactParity -cne 'pass' -or -not $result.DispatchWindowCustodyControl -or
    $result.WitnessOrigins.Count -gt 64 -or $window.MatchingDispatchCount -ne 0 -or $window.RuntimeRetirementProven -or
    $window.EndKind -cne 'verified_writer_capture_eof_metadata' -or $window.StartKind -cne 'publication_boundary_record' -or
    $result.WitnessOrigins -notcontains $window.LastRetainedSourceLine -or
    $window.End -le $window.LastRetainedWriterSequence -or $window.LastRetainedWriterSequence -le $result.RejectionChainCandidate.Ignored.useWriterSequence) {
    throw 'Dispatch capture contract changed'
}
# Post-run retention assertions only. The consumer still uses its original
# private inputs; these reads cannot establish a security lifetime or authority.
$lifecyclePattern='(?:^|[\s;])event=filetransfer_security_context_(?:installed|retired)(?:;|$)'
$merged=[IO.File]::ReadAllLines((Join-Path $Directory 'two-writer-source.log'))
$retained=[IO.File]::ReadAllLines((Join-Path $output 'filetransfer-retained-log-slice.log'))
$originalLifecycle=@($merged | Where-Object { $_ -cmatch $lifecyclePattern })
$retainedLifecycle=@($retained | Where-Object { $_ -cmatch $lifecyclePattern })
$facts=[IO.File]::ReadAllText((Join-Path $output 'live-primary-observation.json')) | ConvertFrom-Json
if($originalLifecycle.Count -ne 4 -or ($originalLifecycle -join "`n") -cne ($retainedLifecycle -join "`n") -or
    -not $facts.Prepared -or $facts.FullRecords -ne $retained.Length) { throw 'Original security lifecycle retention incomplete' }
foreach($role in @('sender','receiver')) {
    $path=Join-Path $Directory $(if($role -ceq 'sender') {'sender.log'} else {'nlink.log'})
    $peerLifecycle=@([IO.File]::ReadAllLines($path) | Where-Object { $_ -cmatch $lifecyclePattern })
    if($peerLifecycle.Count -ne 2 -or -not $peerLifecycle[0].Contains('event=filetransfer_security_context_installed;') -or
        -not $peerLifecycle[1].Contains('event=filetransfer_security_context_retired;')) { throw 'Original peer lifecycle fixture changed' }
}
$lifecycleOrigins=@(for($i=0;$i -lt $retained.Length;$i++) { if($retained[$i] -cmatch $lifecyclePattern) { $i+1 } })
[IO.File]::WriteAllText((Join-Path $output 'security-lifecycle-retention.json'),([ordered]@{
    Status='retained_original_diagnostics';Records=$retainedLifecycle.Count;RetainedSourceOrigins=$lifecycleOrigins;
    FullRecords=$facts.FullRecords;CompactRecords=$facts.CompactRecords;SecurityLifetimeProven=$false;
    RuntimeRetirementProven=$false;ClassifierEnabled=$false;AcceptanceChanged=$false;Live=$false
} | ConvertTo-Json -Compress))
'dispatch-window-consumer='+([ordered]@{Status=$result.DispatchWindowStatus;YieldStatus=$result.Status;RejectionStatus=$result.RejectionChainStatus;
    FullCompactParity=$result.DispatchWindowFullCompactParity;Controls=$result.DispatchStructuralControls;CustodyControl=$result.DispatchWindowCustodyControl;
    OriginalEvaluations=3;FirstEdge='missing_exact_tuna_receive';Recovered=$false;PeerLineage='not_evaluated';SecurityLifecycleRecords=$retainedLifecycle.Count;
    ClassifierEnabled=$false;AcceptanceChanged=$false;RuntimeRetirementProven=$false;Live=$false} | ConvertTo-Json -Depth 8 -Compress)
