$script:GuiSmokeLegacyV0EvidenceCatalog = $null
$script:GuiSmokeLiveDiagnosticEvidenceEventNames = @(
    'filetransfer_frontier_publication_receipt_closed',
    'filetransfer_fallback_v6_proof_bootstrap_trigger_bound',
    'filetransfer_fallback_v6_proof_bootstrap_peer_first_wake_decided',
    'filetransfer_fallback_v6_proof_bootstrap_pending_write_deferred',
    'filetransfer_fallback_v6_proof_bootstrap_pending_write_recheck_decided',
    'runtime_unlock_retry_physical_send_scope_cancellation_requested',
    'runtime_unlock_retry_physical_send_drain_decided'
)

function Get-GuiSmokeLegacyV0EvidenceCatalog {
    if ($null -ne $script:GuiSmokeLegacyV0EvidenceCatalog) {
        return $script:GuiSmokeLegacyV0EvidenceCatalog
    }

    $path = Join-Path $PSScriptRoot 'FileTransferOps\LegacyV0EvidenceCatalog.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "legacy_v0_evidence_catalog_missing:$path"
    }

    $catalogMetadata = (Get-FileTransferCoordinatorEvidenceCatalog).LegacyV0
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $actualHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
    if ($actualHash -cne [string]$catalogMetadata.Hash) {
        throw "legacy_v0_evidence_catalog_hash_invalid:$actualHash"
    }
    $catalog = (
        [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)) |
        ConvertFrom-Json
    if ([string]$catalog.version -cne [string]$catalogMetadata.Version) {
        throw "legacy_v0_evidence_catalog_version_unknown:$($catalog.version)"
    }

    $events = @($catalog.events | ForEach-Object { [string]$_ })
    $registered = @(
        $catalog.correlators |
            ForEach-Object { @($_.dependencies) } |
            ForEach-Object { ([string]$_).ToLowerInvariant() } |
            Sort-Object -Unique)
    if ($events.Count -eq 0 -or
        @($events | Sort-Object -Unique).Count -ne $events.Count -or
        ($events -join "`n") -cne (@($events | Sort-Object) -join "`n") -or
        ($events -join "`n") -cne ($registered -join "`n")) {
        throw 'legacy_v0_evidence_catalog_registry_invalid'
    }

    $script:GuiSmokeLegacyV0EvidenceCatalog = $catalog
    return $catalog
}

function Get-GuiSmokeLegacyV0SemanticEvidenceEventNames {
    $catalog = Get-GuiSmokeLegacyV0EvidenceCatalog
    return [string[]]@($catalog.events)
}

function Get-GuiSmokeSemanticEvidenceEventNames {
    $names = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    # LegacyV0 remains isolated behind this compatibility catalog. Snapshot V1
    # events are declared explicitly and never discovered from analyzer source.
    foreach ($name in @(Get-GuiSmokeLegacyV0SemanticEvidenceEventNames)) {
        [void]$names.Add([string]$name)
    }
    $coordinatorCatalog = Get-FileTransferCoordinatorEvidenceCatalog
    foreach ($name in @(
            $coordinatorCatalog.SnapshotAuthorityV1 +
            $coordinatorCatalog.GenerationStampedEffects +
            $coordinatorCatalog.NonAuthorityEvidence)) {
        [void]$names.Add([string]$name)
    }
    foreach ($name in $script:GuiSmokeLiveDiagnosticEvidenceEventNames) {
        [void]$names.Add([string]$name)
    }
    return [string[]]@($names | Sort-Object)
}
