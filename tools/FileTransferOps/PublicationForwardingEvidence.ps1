Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'PublicationUseEvidence.ps1')

function New-FileTransferPublicationForwardingIndex {
    param($SnapshotEvidence, [string]$InputPath, [string]$InputSha256,
        [string]$WriterPath, $WriterEvidence, [string]$AcceptanceRunId, [string]$ScenarioRunId,
        [string]$SnapshotInputPath, [string]$SnapshotInputSha256)

    $local = New-FileTransferPublicationUseIndex @PSBoundParameters
    $index = @{Reason='local_input_unproven'; Local=$local; Effects=@(); SnapshotPath=''; SnapshotLines=@()}
    if ($local.Reason) { return $index }
    $index.Reason = 'forwarding_evidence_unavailable'
    try {
        # Consume private rows from this same completed view, never reconstruct
        # them from diagnostic logs or invoke a Snapshot authority evaluator.
        if ($SnapshotEvidence.PSObject.Properties.Name -cnotcontains '__RoutePublicationEffects' -or
            $null -eq $SnapshotEvidence.__RoutePublicationEffects) { return $index }
        $effects = @($SnapshotEvidence.__RoutePublicationEffects)
        if ($effects.Count -gt 0) {
            $detached = ConvertTo-Json -InputObject $effects -Depth 8 -Compress | ConvertFrom-Json
            $index.Effects = @($detached)
        }
        $index.SnapshotPath = [IO.Path]::GetFullPath($SnapshotInputPath)
        $index.SnapshotLines = if ($index.SnapshotPath -ceq $local.Path) { $local.Lines }
            else { [IO.File]::ReadAllLines($index.SnapshotPath) }
        if ((Get-FileTransferEvidenceSha256 $index.SnapshotPath) -cne $SnapshotInputSha256) {
            $index.Reason = 'snapshot_input_changed'; return $index
        }
        $index.Reason = ''
    }
    catch { } # Optional evidence cannot change the caller's completed result.
    return $index
}
