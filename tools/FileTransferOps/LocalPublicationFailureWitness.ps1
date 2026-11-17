Set-StrictMode -Version Latest

# Called only with the primary's already selected first failure. This is a
# detached selection, NOT a validated publication row or completion capability.
function Resolve-FileTransferPrimaryFailureWitness {
    param($Index, $Failure)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if ($null -eq $Failure -or $null -eq $Failure.Event -or
            $Index.LogFiles.Count -ne 1) { return $null }
        $event = $Failure.Event
        $path = [string]$Index.LogFiles[0]
        if ($path.Length -gt 32768 -or -not [IO.Path]::IsPathRooted($path) -or
            [IO.Path]::GetFullPath($path) -cne $path -or $event.FilePath -cne $path -or
            $event.LineNumber -isnot [int] -or $event.LineNumber -lt 1 -or
            $event.Sequence -isnot [int] -or $event.Sequence -ne $event.LineNumber -or
            $event.RawLine -isnot [string] -or $event.RawLine.Length -gt 262144 -or
            $Failure.Class -isnot [string] -or [string]::IsNullOrWhiteSpace($Failure.Class) -or
            $Failure.Class.Length -gt 256 -or $Failure.Class -ceq '(none)' -or
            $Failure.Reason -isnot [string] -or [string]::IsNullOrWhiteSpace($Failure.Reason) -or
            $Failure.Reason.Length -gt 1024 -or $event.EventName -isnot [string] -or
            $event.EventName.Length -gt 256) { return $null }
        # Use the primary's existing event-name index. No whole-source search,
        # nearest-record fallback, sorting or primary collection mutation.
        $bucket = $Index.EventsByName[$event.EventName]
        if ($null -eq $bucket -or $bucket.Count -gt 16384) { return $null }
        $matches = 0; $visits = 0
        foreach ($member in $bucket) {
            if ($watch.ElapsedMilliseconds -gt 250) { return $null }
            $visits++
            if ($member.FilePath -ceq $path -and $member.LineNumber -eq $event.LineNumber) {
                if (-not [object]::ReferenceEquals($member, $event)) { return $null }
                $matches++
            }
        }
        if ($matches -ne 1) { return $null }
        # Parse only this bounded selected line. Loading the whole GUI ledger
        # here would redefine the primary's Snapshot evaluator and its caches.
        $payload = $event.RawLine
        $prefix = [regex]::Match($payload, '^(?:\[[^\]]+\]\s+)+(?<message>.*)$')
        if ($prefix.Success) { $payload = $prefix.Groups['message'].Value }
        $fields = @{}
        foreach ($part in $payload.Split(';')) {
            if ($watch.ElapsedMilliseconds -gt 250) { return $null }
            $segment = $part.Trim(); $separator = $segment.IndexOf('=')
            if ($separator -le 0) { continue }
            $key = $segment.Substring(0, $separator).Trim()
            if (-not $key) { continue }
            if ($fields.Count -ge 256 -or $fields.ContainsKey($key)) { return $null }
            $fields[$key] = $segment.Substring($separator + 1).Trim()
        }
        $sequence = 0L
        if ($fields['event'] -cne $event.EventName -or
            $fields['publication_use_kind'] -cnotin @('v4_physical_submission','v4_pre_pump_request','receive_guard','receive_dispatch') -or
            $fields['publication_evidence_version'] -cne '1' -or
            [string]::IsNullOrWhiteSpace([string]$fields['log_writer_id']) -or
            ([string]$fields['log_writer_id']).Length -gt 128 -or
            [string]$fields['log_writer_sequence'] -cnotmatch '\A[1-9][0-9]*\z' -or
            -not [long]::TryParse([string]$fields['log_writer_sequence'], [ref]$sequence) -or
            $event.Fields['log_writer_id'] -cne $fields['log_writer_id'] -or
            $event.Fields['log_writer_sequence'] -cne $fields['log_writer_sequence']) { return $null }
        $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($event.RawLine)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
        if ($watch.ElapsedMilliseconds -gt 250) { return $null }
        return [pscustomobject]@{
            Policy = 'primary_reported_failure_witnesses_v1'
            ReportClass = $Failure.Class
            ReportReason = $Failure.Reason
            EventName = $event.EventName
            SourcePath = $path
            SourceLine = [long]$event.LineNumber
            Writer = [string]$fields['log_writer_id']
            WriterSequence = $sequence
            ContentSha256 = $digest
            IndexRecordVisits = $visits
            RawCodeUnitCount = $event.RawLine.Length
        }
    }
    catch { return $null } # Optional selection must not change primary reporting.
}
