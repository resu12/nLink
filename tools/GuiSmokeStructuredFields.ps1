Set-StrictMode -Version Latest

function ConvertFrom-GuiSmokeLogLedgerStructuredFields {
    param([AllowNull()][string]$Line)

    $fields = @{}
    $duplicateFieldObserved = $false
    $duplicateWriterEvidenceFieldObserved = $false
    if ([string]::IsNullOrWhiteSpace($Line)) {
        return $fields
    }

    $payload = $Line
    $prefixMatch = [regex]::Match($Line, '^(?:\[[^\]]+\]\s+)+(?<message>.*)$')
    if ($prefixMatch.Success) {
        $payload = $prefixMatch.Groups['message'].Value
    }
    foreach ($part in ($payload -split ';')) {
        $segment = $part.Trim()
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }
        $separator = $segment.IndexOf('=')
        if ($separator -le 0) {
            continue
        }
        $name = $segment.Substring(0, $separator).Trim()
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            if ($fields.ContainsKey($name)) {
                $duplicateFieldObserved = $true
                if (@(
                        'event',
                        'acceptance_run_id',
                        'scenario_run_id',
                        'log_writer_id',
                        'log_writer_sequence',
                        'log_writer_batch_generation',
                        'log_writer_batch_first_sequence',
                        'log_writer_batch_last_sequence',
                        'log_writer_attempt_utc_ticks'
                    ) -contains $name) {
                    $duplicateWriterEvidenceFieldObserved = $true
                }
                continue
            }
            $fields[$name] = $segment.Substring($separator + 1).Trim()
        }
    }
    if ($duplicateFieldObserved) {
        $fields['__nlink_duplicate_structured_field'] = $true
    }
    if ($duplicateWriterEvidenceFieldObserved) {
        $fields['__nlink_duplicate_writer_evidence_field'] = $true
    }

    return $fields
}
