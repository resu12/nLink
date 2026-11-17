# Exact peer-first fixture preserved from FileTransferOpsScriptSurfaceTests.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Windows PowerShell 5.1 required' }
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1'), [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors -join ';') }
foreach ($definition in @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -like '*FileTransferFallbackTail*'
    }, $true))) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/SnapshotLocalYieldCandidateBaseline.ps1')

$script:WriterSequencePredicate = (Get-Command Get-FileTransferFallbackTailEventWriterSequence).ScriptBlock
$script:ReceiptPredicate = (Get-Command Test-FileTransferFallbackTailSnapshotReceiptMatch).ScriptBlock
$script:MutationPredicate = (Get-Command Test-FileTransferFallbackTailLocalYieldScopeMutation).ScriptBlock
$script:PoisonPredicate = (Get-Command Test-FileTransferFallbackTailLocalYieldPoisonScopeMatch).ScriptBlock
$script:RawScopePredicate = (Get-Command Test-FileTransferFallbackTailRawScopeEqual).ScriptBlock
$script:Visits = @{}
function Record-Visit([string]$Kind, $Event) {
    $script:Visits[$Kind] = [long]$script:Visits[$Kind] + 1L
    if ([string]$Event.EventName -ceq 'unrelated') {
        $script:Visits['unrelated'] = [long]$script:Visits['unrelated'] + 1L
    }
}
function Get-FileTransferFallbackTailEventWriterSequence {
    param($Event)
    Record-Visit 'sequence' $Event
    & $script:WriterSequencePredicate -Event $Event
}
function Test-FileTransferFallbackTailSnapshotReceiptMatch {
    param($Binding, $Event)
    Record-Visit 'receipt' $Event
    & $script:ReceiptPredicate -Binding $Binding -Event $Event
}
function Test-FileTransferFallbackTailLocalYieldScopeMutation {
    param($Pending, $Event)
    Record-Visit 'mutation' $Event
    & $script:MutationPredicate -Pending $Pending -Event $Event
}
function Test-FileTransferFallbackTailLocalYieldPoisonScopeMatch {
    param($Pending, $Event)
    Record-Visit 'poison' $Event
    & $script:PoisonPredicate -Pending $Pending -Event $Event
}
function Test-FileTransferFallbackTailRawScopeEqual {
    param($Left, $Right)
    Record-Visit 'blocked' $Right
    & $script:RawScopePredicate -Left $Left -Right $Right
}
function Copy-Value($value) {
    $value | ConvertTo-Json -Depth 24 | ConvertFrom-Json
}
$rawSession = 'session-raw'
$rawTransfer = 'transfer-raw'
$rawPeerAuthority = 'fta2-fallback_tail_yield-0123456789abcdef01234567'
$rawOwner = "$rawSession|$rawTransfer|post_tuna_fallback_v6|6|2|3|1|1|(none)"
$rawPair = "$rawSession|$rawTransfer|post_tuna_fallback_v6|6|2|1|(none)"
$sessionToken = ConvertTo-FileTransferCoordinatorEvidenceToken $rawSession
$transferToken = ConvertTo-FileTransferCoordinatorEvidenceToken $rawTransfer
$peerAuthorityToken = ConvertTo-FileTransferCoordinatorEvidenceToken $rawPeerAuthority
$ownerToken = ConvertTo-FileTransferCoordinatorEvidenceToken $rawOwner
$pairToken = ConvertTo-FileTransferCoordinatorEvidenceToken $rawPair
$source = [pscustomobject][ordered]@{
    route='post_tuna_fallback_v6'; protocolVersion=6L; liveRouteEpoch=2L
    routePublicationGeneration=4L; routeCommitGeneration=2L
    runtimeGeneration=3L; terminalGeneration=0L; legGeneration=3L
    transportEpoch=1L; bridgeRecoveryGeneration=1L
    checkpointGeneration=0L; checkpointRequestId='(none)'
    dataSessionIdentity='data-session'; dataSessionGeneration=1L
}
$aggregate = Copy-Value $source
$aggregate.transportEpoch = 0L
$aggregate.bridgeRecoveryGeneration = 0L
function New-PeerReceipt([string]$reason) {
    [pscustomobject][ordered]@{
        stage='peer_proof'; commandIdentity='peer-command'; commandGeneration=1L
        commandFingerprint='peer-command-fingerprint'; receiptIdentity='peer-receipt'
        receiptGeneration=1L; receiptStatus='Observed'; receiptReason=$reason
        receiptFingerprint='peer-receipt-fingerprint'; receiptValidated=$true
    }
}
$pendingReceipt = New-PeerReceipt 'fallback_tail_peer_proof_retained'
$observedReceipt = New-PeerReceipt 'fallback_tail_peer_proof_observed'
$peerEffect = [pscustomobject][ordered]@{
    LineIndex=20L; Writer='writer'; WriterSequence=20L
    CoordinatorInstanceId='coordinator'; TransitionId='pending'; StateGeneration=1L
    Family='fallback_tail_yield'; OperationKey='pending-key'
    AuthorityId=$peerAuthorityToken; OperationGeneration=1L
    EffectKind='peer_proof_observed'; EffectIdentity='peer-effect'
    ReceiptIdentity='peer-receipt'; ReceiptGeneration=1L
    ReceiptStatus='Observed'; ReceiptFingerprint='peer-receipt-fingerprint'
}
$pendingBinding = [pscustomobject][ordered]@{
    BindingKind='pending_peer_proof'; CoordinatorInstanceId='coordinator'; Writer='writer'
    TransitionId='pending'; TransitionSequence=10L; StateGeneration=1L
    SessionId=$sessionToken; TransferId=$transferToken; Direction='outbound'
    OperationKey='pending-key'; OperationAuthorityId=$peerAuthorityToken
    OperationGeneration=1L; OperationRole='peer_proof_pending_initial'
    BeforeState='None'; AfterState='Pending'; OwnerKey=$ownerToken; PairKey=$pairToken
    PeerProofAuthorityId=$rawPeerAuthority; PeerProofGeneration=1L
    PeerProofYieldGeneration=1L; PeerProofIdentity='peer-proof'
    PeerProofFingerprint='peer-proof-fingerprint'; PairingState='Pending'
    PairingGeneration=0L; LocalProofIdentity='(none)'
    LocalProofFingerprint='(none)'; PairedReleaseAuthorityId='(none)'
    PairedReleaseGeneration=0L; PairedReleaseOutcome='(none)'
    AggregateBoundary=$aggregate; SourceBoundary=$source
    EffectiveBoundary=$source; TargetBoundary=$source
    PeerProofReceipt=$pendingReceipt; PeerProofEffect=$peerEffect
    LocalProofReceipt=$null; LocalProofEffects=@()
    PairedReleaseReceipt=$null; PairedReleaseEffect=$null
}
function New-YieldOperation(
    [string]$role,
    [string]$key,
    [string]$authority,
    [long]$generation,
    [string]$before,
    [string]$after,
    $typed = $null) {
    [pscustomobject][ordered]@{
        family='fallback_tail_yield'; operationRole=$role
        operationKind=if ($role -eq 'peer_proof_pending_initial') { 'PeerProof' } else { 'Quiescent' }
        operationKey=$key; authorityId=$authority
        operationGeneration=$generation; beforeState=$before; afterState=$after
        stateApplied=$true; fallbackTailYield=$typed
    }
}
function New-Boundary([string]$id, [long]$sequence, [long]$generation, [object[]]$operations) {
    [pscustomobject][ordered]@{
        boundaryId=$id; coordinatorInstanceId='coordinator'; writer='writer'
        writerSequence=$sequence; sessionId=$sessionToken; transferId=$transferToken
        direction='outbound'; beforeStateGeneration=($generation - 1L)
        afterStateGeneration=$generation; beforeBoundary=$aggregate; afterBoundary=$aggregate
        operations=$operations; verdict='pass'
    }
}
$pendingOperation = New-YieldOperation `
    'peer_proof_pending_initial' 'pending-key' $peerAuthorityToken 1L 'None' 'Pending'
$pendingBoundary = New-Boundary 'pending' 10L 1L @($pendingOperation)
$currentAuthority = ConvertTo-FileTransferCoordinatorEvidenceToken `
    'fta2-fallback_tail_yield-aaaaaaaaaaaaaaaaaaaaaaaa'
$currentKey = "fallback_tail_yield|yield_current|$currentAuthority|2|Quiescent"
$carriedTyped = [pscustomobject][ordered]@{
    role='yield_current'; pairingState='PeerProofObserved'; ownerKey=$ownerToken
    pairKey=$pairToken; peerProofAuthorityId=$rawPeerAuthority
    peerProofYieldGeneration=1L; peerProofIdentity='peer-proof'
    peerProofFingerprint='peer-proof-fingerprint'; localProofIdentity='(none)'
    pairedReleaseAuthorityId='(none)'; sourceBoundary=$source
    receipts=@($observedReceipt)
}
$retiredOperation = New-YieldOperation `
    'peer_proof_pending_initial' 'pending-key' $peerAuthorityToken 1L 'Pending' 'None'
$carriedOperation = New-YieldOperation `
    'yield_current' $currentKey $currentAuthority 2L 'None' 'PeerProofObserved' $carriedTyped
$carryBoundary = New-Boundary 'carry' 50L 2L @($retiredOperation,$carriedOperation)
$pairedOperation = New-YieldOperation `
    'yield_current' $currentKey $currentAuthority 2L 'PeerProofObserved' 'Paired'
$pairedBoundary = New-Boundary 'paired' 60L 3L @($pairedOperation)
$pairedBinding = [pscustomobject][ordered]@{
    BindingKind='paired_yield'; CoordinatorInstanceId='coordinator'; Writer='writer'
    TransitionId='paired'; TransitionSequence=60L; StateGeneration=3L
    SessionId=$sessionToken; TransferId=$transferToken; Direction='outbound'
    OperationKey=$currentKey; OperationAuthorityId=$currentAuthority
    OperationGeneration=2L; OperationRole='yield_current'
    BeforeState='PeerProofObserved'; AfterState='Paired'; OwnerKey=$ownerToken
    PairKey=$pairToken; PeerProofAuthorityId=$rawPeerAuthority
    PeerProofGeneration=1L; PeerProofYieldGeneration=1L
    PeerProofIdentity='peer-proof'; PeerProofFingerprint='peer-proof-fingerprint'
    PairingState='Paired'; PairingGeneration=1L
    LocalProofIdentity='local-proof'; LocalProofFingerprint='local-proof-fingerprint'
    PairedReleaseAuthorityId='release'; PairedReleaseGeneration=1L
    PairedReleaseOutcome='Quiescent'; AggregateBoundary=$aggregate
    SourceBoundary=$source
    EffectiveBoundary=$source; TargetBoundary=$source
    PeerProofReceipt=$observedReceipt; PeerProofEffect=$peerEffect
    LocalProofReceipt=[pscustomobject]@{ receiptIdentity='local-receipt' }
    LocalProofEffects=@(
        [pscustomobject]@{ WriterSequence=61L; EffectKind='local_proof_sent' },
        [pscustomobject]@{ WriterSequence=62L; EffectKind='local_proof_applied' })
    PairedReleaseReceipt=[pscustomobject]@{ receiptIdentity='release-receipt' }
    PairedReleaseEffect=[pscustomobject]@{ WriterSequence=63L }
}
function New-Evaluation([object[]]$boundaries, [object[]]$bindings) {
    [pscustomobject]@{
        Mode='SnapshotV1Authoritative'; ArtifactEvidenceVerdict='pass'
        StreamComplete=$true; AuthoritativeFamilies=@('fallback_tail_yield')
        Boundaries=$boundaries; __FallbackTailPeerProofBindings=$bindings
    }
}
function New-Event([string]$name, [long]$sequence, [hashtable]$extra = @{}) {
    $fields=[ordered]@{
        log_writer_id='writer'; log_writer_sequence=[string]$sequence
        session_id=$rawSession; transfer_id=$rawTransfer; direction='outbound'
        route='post_tuna_fallback_v6'; protocol_version='6'; live_route_epoch='2'
        leg_generation='3'; transport_epoch='1'; bridge_recovery_generation='1'
        checkpoint_request_id='(none)'
        data_session_identity='data-session'; data_session_generation='1'
    }
    foreach ($key in $extra.Keys) { $fields[$key]=[string]$extra[$key] }
    [pscustomobject]@{
        EventName=$name; Sequence=$sequence; LineNumber=$sequence
        FileName='synthetic.log'; Message=$name; Fields=$fields
    }
}
$receipt = New-Event 'filetransfer_fallback_tail_yield_proof_received' 30L @{
    authority_schema=2; authority_id=$rawPeerAuthority
    predecessor_authority_id='(none)'; yield_generation=1
    peer_direction='inbound'; owner_key=$rawOwner; pair_key=$rawPair
    raw_pair_key=$rawPair; canonical_pair_key=$rawPair; pair_key_format='canonical_7'
    peer_live_route_epoch=2; peer_leg_generation=3; peer_transport_epoch=1
    peer_bridge_recovery_generation=1; peer_checkpoint_request_id='(none)'
}
$blocked = @(40L..43L | ForEach-Object {
        New-Event 'filetransfer_fallback_tail_local_yield_blocked' $_ @{
            source='fallback_tail_peer_yield'
            decision_reason='fallback_tail_local_owner_work_pending'
        }
    })
$unrelated = New-Event 'unrelated' 35L

function Copy-Event($Event) {
    $copy = $Event | Select-Object *
    $fields = [ordered]@{}
    foreach ($key in $Event.Fields.Keys) { $fields[$key] = $Event.Fields[$key] }
    $copy.Fields = $fields
    return $copy
}
function New-Scenario([string]$Writer = 'writer', [bool]$Recovered = $true) {
    $boundaries = if ($Recovered) { @($pendingBoundary,$carryBoundary,$pairedBoundary) } else { @($pendingBoundary) }
    $bindings = if ($Recovered) { @($pendingBinding,$pairedBinding) } else { @($pendingBinding) }
    $evaluation = Copy-Value (New-Evaluation $boundaries $bindings)
    foreach ($boundary in $evaluation.Boundaries) { $boundary.writer = $Writer }
    foreach ($binding in $evaluation.__FallbackTailPeerProofBindings) {
        $binding.Writer = $Writer
        $binding.PeerProofEffect.Writer = $Writer
    }
    $events = @(@($receipt) + $blocked | ForEach-Object {
        $copy = Copy-Event $_
        $copy.Fields.log_writer_id = $Writer
        $copy
    })
    [pscustomobject]@{ Evaluation=$evaluation; Events=$events }
}
function Compare-Proofs($Scenario, [string]$Name = 'case') {
    [object[]]$events = @($Scenario.Events)
    $references = [object[]]@($events)
    $before = ConvertTo-Json -InputObject $Scenario -Depth 32 -Compress
    $script:Visits = @{}
    $expected = Get-FileTransferFallbackTailSnapshotLocalYieldMissingCorrelationBaseline `
        -Events $events -SnapshotEvaluation $Scenario.Evaluation
    $script:BaselineVisits = $script:Visits
    $script:Visits = @{}
    $actual = Get-FileTransferFallbackTailSnapshotLocalYieldMissingCorrelation `
        -Events $events -SnapshotEvaluation $Scenario.Evaluation
    $script:ActualVisits = $script:Visits
    if ((ConvertTo-Json -InputObject $expected -Depth 32 -Compress) -cne
        (ConvertTo-Json -InputObject $actual -Depth 32 -Compress)) {
        throw "$Name complete proof changed. Expected: $($expected | ConvertTo-Json -Depth 8 -Compress); actual: $($actual | ConvertTo-Json -Depth 8 -Compress)"
    }
    for ($i=0; $i -lt $expected.UnresolvedCandidateRecords.Count; $i++) {
        $expectedEvent = $expected.UnresolvedCandidateRecords[$i].Event
        $actualEvent = $actual.UnresolvedCandidateRecords[$i].Event
        if (-not [object]::ReferenceEquals($expectedEvent, $actualEvent)) { throw "$Name candidate reference/order changed at $i" }
        $sourceFound = $false
        foreach ($event in $events) {
            if ([object]::ReferenceEquals($event, $actualEvent)) { $sourceFound = $true; break }
        }
        if (-not $sourceFound) { throw "$Name candidate does not reference an original event" }
    }
    if ($events.Count -ne $references.Count) { throw "$Name changed input count" }
    for ($i=0; $i -lt $events.Count; $i++) {
        if (-not [object]::ReferenceEquals($events[$i], $references[$i])) { throw "$Name changed source reference/order at $i" }
    }
    if ((ConvertTo-Json -InputObject $Scenario -Depth 32 -Compress) -cne $before) { throw "$Name mutated input" }
    return $actual
}
function Assert-Quartet($Proof, [int]$Raw, [int]$Distinct, [int]$Recovered, [int]$Unresolved) {
    if ($Proof.RawCount -ne $Raw -or $Proof.DistinctCount -ne $Distinct -or
        $Proof.RecoveredCount -ne $Recovered -or $Proof.UnresolvedCount -ne $Unresolved) {
        throw "Unexpected quartet: $($Proof | ConvertTo-Json -Depth 8 -Compress)"
    }
}
function Add-Poison($Scenario, [string]$Name, [long]$WriterSequence, [hashtable]$Fields = @{}) {
    $event = New-Event $Name $WriterSequence $Fields
    $event.Fields.log_writer_id = $Scenario.Events[0].Fields.log_writer_id
    $Scenario.Events += $event
    return $event
}
