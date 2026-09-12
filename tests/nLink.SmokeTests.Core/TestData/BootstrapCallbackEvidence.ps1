param([string]$RepoRoot, [string]$InputPath, [switch]$ModeledSuccessor)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog=Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion
$parent=Get-FileTransferCoordinatorBootstrapInitiationCatalog
if ($parent.Hash -cne '40a8177eae51664f06bc2bc7aaa4dc2fb62983fc2262c3c2f99d030401efd7db' -or
    $catalog.ParentCatalogHash -cne $parent.Hash -or $catalog.Hash -cne '0ad879a93ec4d4639ecffd0fecf1c64d2aa32dc594bd39c680b8ca30c3c492dd') { throw 'Catalog lineage changed' }
$marker=[pscustomobject]@{operationRole='fallback_bootstrap_source_callback';receiptValidated=$true}
if ((Test-FileTransferCoordinatorSnapshotEffectScope $marker ([pscustomobject]@{})) -or
    (Test-FileTransferCoordinatorSnapshotEffectReceipt $marker ([pscustomobject]@{}))) { throw 'Source hold evidence granted physical authority' }
$captured=[IO.File]::ReadAllLines($InputPath)
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'GUI parser error' }
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification',
    'Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name}, $true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    Invoke-Expression $definitions[0].Extent.Text
}
function Set-Field([string]$Line, [string]$Name, [string]$Value) {
    [regex]::Replace($Line, "(^|; )$([regex]::Escape($Name))=[^;]*", {param($m) $m.Groups[1].Value + $Name + '=' + $Value})
}
function Replace-Envelope([string]$Line, $Envelope) {
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Envelope -Depth 64 -Compress))
    $line=Set-Field $Line 'snapshot_sha256' (Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes)
    Set-Field $line 'snapshot_payload' ([Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_'))
}
$normalized=[Collections.Generic.List[string]]::new()
foreach ($case in @('exact','missing_admission','missing_capture','missing_consumption','held_terminal','wrong_callback',
    'wrong_hold','wrong_provider','duplicate_callback','extra_field','old_profile','identical_duplicate')) {
    $lines=[string[]]$captured.Clone(); $mutated=$case -ceq 'exact'
    if ($case -ceq 'old_profile') {
        for ($i=0; $i -lt $lines.Count; $i++) { $lines[$i]=Set-Field (Set-Field $lines[$i] 'snapshot_catalog_version' $parent.Version) 'snapshot_catalog_hash' $parent.Hash }
        $mutated=$true
    }
    if ($case -ceq 'identical_duplicate') {
        $duplicate=@($lines | Where-Object { (Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_transition_snapshot' })[0]
        $lines=@(foreach ($line in $lines) { $line; if ($line -ceq $duplicate) { $line } }); $mutated=$true
    }
    for ($i=0; $i -lt $lines.Count -and -not $mutated; $i++) {
        if ((Get-FileTransferCoordinatorSnapshotEventName $lines[$i]) -cne 'filetransfer_coordinator_transition_snapshot') { continue }
        $envelope=(ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $lines[$i] 'snapshot_payload')).Value
        foreach ($op in @($envelope.aggregate.operations)) {
            if (-not (Test-FileTransferCoordinatorBootstrapCallbackMarker $op)) { continue }
            $p=$op.routeTransition.bootstrapCallbacks; $changed=$false
            if (($case -ceq 'missing_admission' -and $p.stage -ceq 'Admitted') -or
                ($case -ceq 'missing_consumption' -and $p.stage -ceq 'Consumed')) {
                $envelope.aggregate.operations=@($envelope.aggregate.operations | Where-Object { $_ -ne $op }); $changed=$true
            }
            if ($case -ceq 'missing_capture' -and $op.afterState -ceq 'Applied') { $p.capturedCallbacks=@(); $changed=$true }
            if ($p.stage -ceq 'Consumed') {
                switch ($case) {
                    'held_terminal' { $p.allowedEffect='terminal'; $changed=$true }
                    'wrong_callback' { $p.callbackFingerprint='fte1-' + ('a' * 24); $changed=$true }
                    'wrong_hold' { $p.holdCommandFingerprint='fte1-' + ('a' * 24); $changed=$true }
                    'wrong_provider' { $p.providerGeneration++; $changed=$true }
                    'duplicate_callback' { $envelope.aggregate.operations+=@($op); $changed=$true }
                    'extra_field' { $p | Add-Member NoteProperty physicalAuthority $true; $changed=$true }
                }
            }
            if ($changed) { $lines[$i]=Replace-Envelope $lines[$i] $envelope; $mutated=$true; break }
        }
    }
    if (-not $mutated) { throw "No mutation target: $case" }
    $full=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
    # Each consumer reparses its own serialized records; no decoded operation objects are shared.
    $records=@(for ($i=0; $i -lt $lines.Count; $i++) { [pscustomobject]@{Line=[string]$lines[$i];LineIndex=$i} })
    $compact=Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
    $positive=$case -cin @('exact','identical_duplicate')
    if ($positive) {
        if ($ModeledSuccessor) {
            # Real Core history has no modeled physical effect records. It must remain unresolved for that reason only.
            if ($full.ArtifactEvidenceVerdict -cne 'fail' -or [string]::IsNullOrEmpty($full.ArtifactEvidenceFailureReason) -or
                @($full.ArtifactEvidenceFailureReason -split ';' | Where-Object { $_ -cnotmatch '^snapshot_scope_invalidation_required_effect_missing:\d+$' }).Count) {
                throw "$case modeled successor failed outside its absent physical effects: $($full.ArtifactEvidenceFailureReason)"
            }
            if (@(Get-FileTransferBootstrapCallbackHistoryErrors @($full.Boundaries)).Count) { throw "$case late callback binding rejected" }
        } elseif ($full.ArtifactEvidenceVerdict -cne 'pass') { throw "$case callback history invalid: $($full.ArtifactEvidenceFailureReason)" }
    } elseif ($full.ArtifactEvidenceVerdict -cne 'fail' -or $full.ArtifactEvidenceFailureReason -cnotmatch 'snapshot_bootstrap_callback') {
        throw "$case failed outside callback boundary: $($full.ArtifactEvidenceFailureReason)"
    }
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        (ConvertTo-Json @($full.Boundaries) -Depth 64 -Compress) -cne (ConvertTo-Json @($compact.Boundaries) -Depth 64 -Compress)) { throw "$case full/compact parity failed" }
    $guiPath=Join-Path ([IO.Path]::GetDirectoryName($InputPath)) ($case + '-gui.log')
    [IO.File]::WriteAllLines($guiPath, $lines)
    $gui=Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($guiPath)
    $boundary=$full.FirstUnresolvedBoundary
    if ($positive -and $full.ArtifactEvidenceVerdict -ceq 'pass') {
        $route=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
        if ($route.Verdict -cne 'fail') { throw 'Modeled source-hold history was mistaken for physical route acceptance' }
        $boundary=$route.FirstUnresolvedBoundary
    }
    if ($null -eq $gui -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash) { throw "$case GUI parity failed" }
    $reasons=[string[]]($full.ArtifactEvidenceFailureReason -split ';'); [Array]::Sort($reasons, [StringComparer]::Ordinal)
    $normalized.Add("$case|$($full.ArtifactEvidenceVerdict)|$($full.BoundaryHash)|$($reasons -join ';')")
}
'bootstrap-callback-normalized=' + ($normalized -join '/')
