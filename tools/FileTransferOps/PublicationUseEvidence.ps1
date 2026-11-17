Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LocalPublicationRows.ps1')

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'GuiSmokeLogLedger.ps1')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'FileTransferPublicationEvidence.ps1')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'FileTransferSoak/RetainedAnalysisEvidence.ps1')


function New-FileTransferPublicationUseIndex {
    param($SnapshotEvidence, [string]$InputPath, [string]$InputSha256,
        [string]$WriterPath, $WriterEvidence, [string]$AcceptanceRunId, [string]$ScenarioRunId,
        [string]$SnapshotInputPath, [string]$SnapshotInputSha256)

    # The caller supplies its already completed authoritative evaluation and
    # existing frozen-ledger result. This consumer never evaluates authority.
    $index = @{ Reason='input_evidence_invalid'; Path=''; Lines=@(); Publications=$null }
    try {
        if ([string]::IsNullOrWhiteSpace($AcceptanceRunId) -or $AcceptanceRunId -ceq '(none)' -or
            [string]::IsNullOrWhiteSpace($ScenarioRunId) -or $ScenarioRunId -ceq '(none)' -or
            $InputSha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $SnapshotInputSha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $WriterEvidence.WriterSha256 -cnotmatch '^[0-9a-f]{64}$') { return $index }
        $path = [IO.Path]::GetFullPath($InputPath)
        $snapshotPath = [IO.Path]::GetFullPath($SnapshotInputPath)
        $writerPathFull = [IO.Path]::GetFullPath($WriterPath)
        if ((Get-FileTransferEvidenceSha256 $path) -cne $InputSha256 -or
            (Get-FileTransferEvidenceSha256 $snapshotPath) -cne $SnapshotInputSha256 -or
            (Get-FileTransferEvidenceSha256 $writerPathFull) -cne $WriterEvidence.WriterSha256) { return $index }
        $lines = [IO.File]::ReadAllLines($path)
        $snapshotLines = $lines
        if ($snapshotPath -cne $path) { $snapshotLines = [IO.File]::ReadAllLines($snapshotPath) }
        $writerLines = [IO.File]::ReadAllLines($writerPathFull)
        $index.Reason = 'writer_evidence_invalid'
        if ($writerLines.Length -ne $WriterEvidence.WriterLineCount -or
            $WriterEvidence.WriterGapCount -ne 0 -or $WriterEvidence.WriterDuplicateCount -ne 0 -or
            $WriterEvidence.WriterIdentityMismatchCount -ne 0 -or $WriterEvidence.ChronologyInversionCount -ne 0 -or
            $WriterEvidence.ChronologyCollisionCount -ne 0 -or $WriterEvidence.ChronologyBatchInvalid -ne $false) { return $index }
        $owners = @{}; $members = @{}; $counts = @{}
        foreach ($writer in @($WriterEvidence.Writers)) {
            $id = [string]$writer.WriterId
            if ([string]::IsNullOrWhiteSpace($id) -or $owners.ContainsKey($id) -or
                $writer.AcceptanceRunId -cne $AcceptanceRunId -or $writer.ScenarioRunId -cne $ScenarioRunId -or
                $writer.FirstSequence -ne 1 -or $writer.LastSequence -ne $writer.ObservedCount -or
                $writer.ObservedCount -le 0) { return $index }
            $owners[$id] = $writer; $counts[$id] = 0L
        }
        if ($owners.Count -eq 0) { return $index }
        foreach ($line in $writerLines) {
            $f = ConvertFrom-GuiSmokeLogLedgerStructuredFields $line
            $id = [string]$f['log_writer_id']; $sequence = [string]$f['log_writer_sequence']
            # The existing ledger rejects duplicate writer-metadata fields.
            # Unrelated telemetry may repeat other fields; candidate and
            # Snapshot schemas are checked separately, without rewriting them.
            if (-not $owners.ContainsKey($id) -or
                $owners[$id].WriterId -cne $id -or -not (Test-FileTransferPublicationUseInteger $sequence 1)) { return $index }
            $counts[$id]++
            if ([long]$sequence -ne $counts[$id]) { return $index }
            if ($sequence -ceq '1') {
                if ($f['event'] -cne 'operational_log_writer_started' -or $f['acceptance_run_id'] -cne $AcceptanceRunId -or
                    $f['scenario_run_id'] -cne $ScenarioRunId) { return $index }
            }
            elseif ($f['event'] -ceq 'operational_log_writer_started') { return $index }
            $members["$id|$sequence"] = $line
        }
        foreach ($id in $owners.Keys) { if ($counts[$id] -ne $owners[$id].ObservedCount) { return $index } }
        $index.Reason = 'input_membership_invalid'
        # Reduced Snapshot streams have their own physical offsets and may
        # exclude use records. Bind both original inputs to the same writer log.
        $membershipLines = $lines
        if ($snapshotPath -cne $path) { $membershipLines = $lines + $snapshotLines }
        foreach ($line in $membershipLines) {
            $f = ConvertFrom-GuiSmokeLogLedgerStructuredFields $line
            $key = "$($f['log_writer_id'])|$($f['log_writer_sequence'])"
            if (-not $members.ContainsKey($key) -or
                $members[$key] -cne $line) { return $index }
        }
        # Detach the small consumer view from subsequent caller mutations.
        $index.Reason = 'snapshot_evidence_invalid'
        $view = $SnapshotEvidence | Select-Object Mode,ArtifactEvidenceVerdict,StreamComplete,AuthoritativeFamilies,Boundaries |
            ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json
        if ($view.Mode -cne 'SnapshotV1Authoritative' -or $view.ArtifactEvidenceVerdict -cne 'pass' -or
            $view.StreamComplete -ne $true -or 'route_runtime' -cnotin @($view.AuthoritativeFamilies) -or
            'route_transition' -cnotin @($view.AuthoritativeFamilies)) { return $index }
        $index.Reason = 'snapshot_input_binding_invalid'
        foreach ($boundary in @($view.Boundaries)) {
            $offset = [long]$boundary.evidenceLineIndex
            if ($offset -lt 0 -or $offset -ge $snapshotLines.Length) { return $index }
            $f = ConvertFrom-GuiSmokeLogLedgerStructuredFields $snapshotLines[$offset]
            if ($f.ContainsKey('__nlink_duplicate_structured_field') -or $f.event -cne 'filetransfer_coordinator_transition_snapshot' -or
                $f.transition_id -cne $boundary.boundaryId -or $f.snapshot_sha256 -cne $boundary.boundaryHash -or
                $f.log_writer_id -cne $boundary.writer -or $f.log_writer_sequence -cne [string]$boundary.writerSequence) { return $index }
        }
        $index.Reason = 'input_changed'
        if ((Get-FileTransferEvidenceSha256 $path) -cne $InputSha256 -or
            (Get-FileTransferEvidenceSha256 $snapshotPath) -cne $SnapshotInputSha256 -or
            (Get-FileTransferEvidenceSha256 $writerPathFull) -cne $WriterEvidence.WriterSha256) { return $index }
        $index.Path = $path; $index.Lines = $lines
        $index.Publications = New-FileTransferPublicationEvidenceIndex $view
        $index.Reason = ''
    }
    catch { } # Malformed optional evidence never changes the primary caller.
    return $index
}
