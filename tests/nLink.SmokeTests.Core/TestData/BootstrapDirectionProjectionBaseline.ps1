# Frozen wrapper from 046eb0b39c874df240c035fbbe52914ef027219c; only the function name is changed.
function Invoke-BootstrapDirectionProjectionBaseline {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Events,
        [Parameter(Mandatory = $true)]$SnapshotProof
    )

    [string[]]$lines = @($Events | ForEach-Object { [string]$_.RawLine })
    $directionCorrelation =
        Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $lines
    $peerFirstCorrelation =
        Get-FileTransferFallbackV6BootstrapPeerFirstCorrelation -Lines $lines
    $eventsByWriterSequence = @{}
    foreach ($event in $Events) {
        $writer = Get-FileTransferAuthorityLogField -Line ([string]$event.RawLine) -Name 'log_writer_id'
        $writerSequence = Get-FileTransferAuthorityLogField -Line ([string]$event.RawLine) -Name 'log_writer_sequence'
        if (-not [string]::IsNullOrWhiteSpace($writer) -and
            -not [string]::IsNullOrWhiteSpace($writerSequence)) {
            $eventsByWriterSequence["$writer|$writerSequence".ToLowerInvariant()] = $event
        }
    }

    [object[]]$physicalRecords = @(
        foreach ($candidate in @(
                $directionCorrelation.CandidateRecords +
                $peerFirstCorrelation.CandidateRecords)) {
            $key = "$($candidate.Writer)|$($candidate.WriterSequence)".ToLowerInvariant()
            if (-not $eventsByWriterSequence.ContainsKey($key)) {
                continue
            }
            [pscustomobject]@{
                Event = $eventsByWriterSequence[$key]
                Class = [string]$candidate.Class
                Reason = [string]$candidate.Reason
                Variant = [string]$candidate.Variant
                Recovered = [bool]$candidate.Recovered
            }
        })
    [object[]]$physicalRecovered = @($physicalRecords | Where-Object { $_.Recovered })
    [object[]]$physicalUnresolved = @($physicalRecords | Where-Object { -not $_.Recovered })
    [object[]]$candidateRecords = @($SnapshotProof.CandidateRecords) + $physicalRecords
    [object[]]$recoveredRecords = @($SnapshotProof.RecoveredCandidateRecords) + $physicalRecovered
    [object[]]$unresolvedRecords = @($SnapshotProof.UnresolvedCandidateRecords) + $physicalUnresolved

    return [pscustomobject]@{
        RawCount = [int]$SnapshotProof.RawCount +
            [int]$directionCorrelation.RawCount + [int]$peerFirstCorrelation.RawCount
        DistinctCount = [int]$SnapshotProof.DistinctCount +
            [int]$directionCorrelation.DistinctCount + [int]$peerFirstCorrelation.DistinctCount
        RecoveredCount = [int]$SnapshotProof.RecoveredCount +
            [int]$directionCorrelation.RecoveredCount + [int]$peerFirstCorrelation.RecoveredCount
        UnresolvedCount = [int]$SnapshotProof.UnresolvedCount +
            [int]$directionCorrelation.UnresolvedCount + [int]$peerFirstCorrelation.UnresolvedCount
        Verdict = if ($unresolvedRecords.Count -gt 0) { 'fail' } else { 'pass' }
        CandidateRecords = $candidateRecords
        RecoveredCandidateRecords = $recoveredRecords
        UnresolvedCandidateRecords = $unresolvedRecords
        CandidateEvents = @($candidateRecords | ForEach-Object { $_.Event })
        RecoveredCandidateEvents = @($recoveredRecords | ForEach-Object { $_.Event })
        UnresolvedCandidateEvents = @($unresolvedRecords | ForEach-Object { $_.Event })
        AuthoritativeRecords = @($SnapshotProof.AuthoritativeRecords)
    }
}
