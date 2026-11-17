param([string]$RepoRoot, [string]$InputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$captured = [IO.File]::ReadAllLines($InputPath)
$baseline = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $captured
if ($baseline.ArtifactEvidenceVerdict -cne 'pass') { throw "Agreement baseline invalid: $($baseline.ArtifactEvidenceFailureReason)" }
$inbound = $baseline.Boundaries[0].direction -ceq 'inbound'
$tokens=$null; $parseErrors=$null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'GUI parser error' }
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification',
        'Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name}, $true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    Invoke-Expression $definitions[0].Extent.Text
}
function Set-Field([string]$Line, [string]$Name, [string]$Value) {
    [regex]::Replace($Line, "(^|; )$([regex]::Escape($Name))=[^;]*", {param($m) $m.Groups[1].Value + $Name + '=' + $Value})
}
function Replace-Envelope([string]$Line, $Envelope) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Envelope -Depth 64 -Compress))
    $line = Set-Field $Line 'decision_kind' $Envelope.decisionKind
    $line = Set-Field $line 'snapshot_sha256' (Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes)
    Set-Field $line 'snapshot_payload' ([Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_'))
}
$cases = @('exact','offer_wrong_event','offer_wrong_state','offer_version','offer_receiver','accept_wrong_event',
    'accept_sender','accept_identity','accept_source','missing_offer','hidden_accept')
if ($inbound) { $cases += @('open_wrong_event','open_receiver','open_version','open_identity','open_source') }
$normalized = [Collections.Generic.List[string]]::new()
foreach ($case in $cases) {
    $lines = [string[]]$captured.Clone(); $mutated = $case -ceq 'exact'
    for ($i=0; -not $mutated -and $i -lt $lines.Count; $i++) {
        if ((Get-FileTransferCoordinatorSnapshotEventName $lines[$i]) -cne 'filetransfer_coordinator_transition_snapshot') { continue }
        $envelope = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $lines[$i] 'snapshot_payload')).Value
        $agreements = @($envelope.aggregate.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_agreement')
        if ($agreements.Count -ne 1) { continue }
        $operation = $agreements[0]; $payload = $operation.routeTransition.bootstrapCorrelation
        $offer = $envelope.decisionKind -ceq 'PinFallbackBootstrapOffer'
        $accept = $envelope.decisionKind -ceq 'ApplyFallbackBootstrapAccept'
        $open = $envelope.decisionKind -ceq 'ApplyFallbackBootstrapSessionOpen'
        if ($case -ceq 'missing_offer' -and $offer) { $envelope.aggregate.operations = @(); $mutated = $true }
        elseif ($case.StartsWith('offer_') -and $offer) {
            switch ($case) {
                'offer_wrong_event' { $envelope.decisionKind = 'InvalidateFallbackBootstrapExchange' }
                'offer_wrong_state' { $operation.afterState = 'Selected' }
                'offer_version' { $payload.version = 1 }
                'offer_receiver' { $payload.receiverIssuerFingerprint = 'fte1-' + ('a' * 24) }
            }
            $mutated = $true
        } elseif (($case.StartsWith('accept_') -or $case -ceq 'hidden_accept') -and $accept) {
            switch ($case) {
                'accept_wrong_event' { $envelope.decisionKind = 'InvalidateFallbackBootstrapExchange' }
                'accept_sender' { $payload.senderIssuerFingerprint = 'fte1-' + ('a' * 24) }
                'accept_identity' {
                    $operation.authorityId = 'fte1-' + ('a' * 24); $operation.commandIdentity = $operation.authorityId
                    $operation.operationKey = "route_transition|fallback_bootstrap_agreement|$($operation.authorityId)|1|FallbackBootstrapAgreement"
                }
                'accept_source' { $operation.routeTransition.sourceBoundary.dataSessionGeneration++; $operation.routeTransition.targetBoundary.dataSessionGeneration++ }
                'hidden_accept' { $operation.beforeState = $operation.afterState }
            }
            $mutated = $true
        } elseif ($case.StartsWith('open_') -and $open) {
            switch ($case) {
                'open_wrong_event' { $envelope.decisionKind = 'InvalidateFallbackBootstrapExchange' }
                'open_receiver' { $payload.receiverIssuerFingerprint = 'fte1-' + ('a' * 24) }
                'open_version' { $payload.version = 1 - $payload.version }
                'open_identity' {
                    $operation.authorityId = 'fte1-' + ('a' * 24); $operation.commandIdentity = $operation.authorityId
                    $operation.operationKey = "route_transition|fallback_bootstrap_agreement|$($operation.authorityId)|1|FallbackBootstrapAgreement"
                }
                'open_source' { $operation.routeTransition.sourceBoundary.dataSessionGeneration++; $operation.routeTransition.targetBoundary.dataSessionGeneration++ }
            }
            $mutated = $true
        }
        if ($mutated) { $lines[$i] = Replace-Envelope $lines[$i] $envelope }
    }
    if (-not $mutated) { throw "No target for $case" }
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
    $records = @(for ($i=0; $i -lt $lines.Count; $i++) { [pscustomobject]@{Line=$lines[$i];LineIndex=$i} })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
    $expected = if ($case -ceq 'exact') { 'pass' } else { 'fail' }
    if ($full.ArtifactEvidenceVerdict -cne $expected -or $compact.ArtifactEvidenceVerdict -cne $expected -or
        ($case -cne 'exact' -and $full.ArtifactEvidenceFailureReason -cnotmatch 'snapshot_bootstrap_correlation_')) {
        throw "$case expected $expected : $($full.ArtifactEvidenceFailureReason)"
    }
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        (ConvertTo-Json @($full.Boundaries) -Depth 64 -Compress) -cne (ConvertTo-Json @($compact.Boundaries) -Depth 64 -Compress)) {
        throw "$case full/compact mismatch"
    }
    $guiPath = Join-Path ([IO.Path]::GetDirectoryName($InputPath)) ($case + '-gui.log')
    [IO.File]::WriteAllLines($guiPath, $lines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($guiPath)
    $boundary = $full.FirstUnresolvedBoundary
    if ($case -ceq 'exact') {
        $route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
        if (($route.Findings -join ';') -cne 'snapshot authoritative route stream contains no route publications') { throw 'Agreement-only fixture granted route acceptance' }
        $boundary = $route.FirstUnresolvedBoundary
    }
    if ($null -eq $gui -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash) {
        throw "$case GUI boundary mismatch"
    }
    $normalized.Add("$case|$($full.ArtifactEvidenceVerdict)|$($full.BoundaryHash)|$($full.ArtifactEvidenceFailureReason)")
}
# Inspect unchanged carriers and forged repeat legacy validation directly at the history boundary.
$agreementEntries = @($baseline.Boundaries | ForEach-Object {
    $boundary = $_
    foreach ($operation in @($_.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_agreement')) {
        [pscustomobject]@{Boundary=$boundary;Operation=$operation}
    }
})
$last = $agreementEntries[-1]
$carry = ConvertFrom-Json (ConvertTo-Json $last.Boundary -Depth 64 -Compress)
$carry.evidenceLineIndex = 1000
$carry.decisionKind = 'InvalidateFallbackBootstrapExchange'
$carry.operations = @($carry.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_agreement')
$carry.operations[0].beforeState = $carry.operations[0].afterState
if (@(Get-FileTransferBootstrapCorrelationHistoryErrors (@($baseline.Boundaries) + @($carry))).Count -ne 0) { throw 'Unchanged agreement carrier rejected' }
if ($inbound -and $last.Operation.routeTransition.bootstrapCorrelation.version -eq 0) {
    $carry.decisionKind = 'ApplyFallbackBootstrapSessionOpen'
    $carry.operations[0].commandFingerprint = 'fte1-' + ('a' * 24)
    $carry.operations[0].routeTransition.bootstrapCorrelation.agreementFingerprint = $carry.operations[0].commandFingerprint
    if (@(Get-FileTransferBootstrapCorrelationHistoryErrors (@($baseline.Boundaries) + @($carry)) | Where-Object {
            $_ -cmatch '^snapshot_bootstrap_correlation_agreement_mutated:' }).Count -ne 1) { throw 'Repeated legacy SessionOpen mutation accepted' }
}
Write-Output ('bootstrap-agreement-normalized=' + ($normalized -join '/'))
