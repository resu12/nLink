Set-StrictMode -Version Latest

# Explicit diagnostic/test bundle only. Do not load this into a live retained
# analysis: LogParsing initializes its script-scoped index and query caches.
# The production GUI classifier neither imports nor invokes this adapter.
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'FileTransferSoak\LogParsing.ps1')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'FileTransferSoak\DataPathRejectedIngressEvidence.ps1')

function Get-TunaGuiRejectedIngressShadowEventNames {
    return @(
        'filetransfer_data_path_send_result',
        'filetransfer_data_path_receive_result',
        'filetransfer_tuna_data_path_mismatch',
        'filetransfer_data_frame_ignored',
        'filetransfer_route_transitioned',
        'file_transfer_outbound_terminal',
        'file_transfer_inbound_terminal',
        'transfer_terminal',
        'filetransfer_runtime_terminalized'
    )
}

function Get-TunaGuiRejectedIngressShadowDiagnostics {
    param(
        [AllowEmptyCollection()][string[]]$Lines = @(),
        [AllowNull()]$PrimaryGuiDecision = $null
    )

    $eventNames = Get-TunaGuiRejectedIngressShadowEventNames
    $events = New-Object System.Collections.Generic.List[object]
    for ($lineIndex = 0; $lineIndex -lt $Lines.Count; $lineIndex++) {
        $line = $Lines[$lineIndex]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $event = ConvertFrom-FileTransferLogLine -Line $line -FilePath 'gui-shadow.log' `
            -LineNumber ($lineIndex + 1) -Sequence ($lineIndex + 1)
        if ($null -eq $event -or $event.EventName -notin $eventNames) { continue }

        # The GUI selector appends the original evidence location before
        # partitioning. Keep it, rather than assigning a compact ordinal.
        $originalSequence = Get-FileTransferEventInt64Field -Event $event `
            -Name 'gui_evidence_sequence' -Default ($lineIndex + 1)
        if ($originalSequence -gt 0) {
            $event.LineNumber = $originalSequence
            $event.Sequence = $originalSequence
        }
        $events.Add($event) | Out-Null
    }

    $index = New-FileTransferFileTunaV4DataPathCorrelationIndex -Events $events.ToArray()
    $diagnostics = New-Object System.Collections.Generic.List[object]
    $seenReceives = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($event in $index.OrderedEvents) {
        if ($event.EventName -ne 'filetransfer_data_path_receive_result' -or
            -not (Test-FileTransferFileTunaV4DataPathActualEvent -Event $event) -or
            ((Get-FileTransferEventField -Event $event -Name 'actual_transport' -Default '') -ne 'regular_nkn' -and
             (Get-FileTransferEventField -Event $event -Name 'delivery_transport' -Default '') -ne 'regular_nkn')) {
            continue
        }

        # Coalesce only identical receive records, excluding appended location
        # metadata. Conflicting content stays in the index and is never hidden.
        $record = [regex]::Replace($event.RawLine, ';\s*gui_evidence_sequence=\d+\s*$', '')
        if (-not $seenReceives.Add($record)) { continue }
        $shared = Get-FileTransferFileTunaV4DataPathMismatchRejectedFailClosedResult `
            -Mismatch $event -Events $index.OrderedEvents -DataPathIndex $index
        $diagnostics.Add([pscustomobject][ordered]@{
            LineNumber = [long]$event.LineNumber
            WriterId = Get-FileTransferEventField -Event $event -Name 'log_writer_id' -Default ''
            WriterSequence = Get-FileTransferEventInt64Field -Event $event -Name 'log_writer_sequence' -Default 0
            Recovered = [bool]$shared.Recovered
            RejectionChainMatched = [bool]$shared.RejectionChainMatched
            ReceiveDisposition = [string]$shared.ReceiveDisposition
            Reason = [string]$shared.Reason
            Variant = [string]$shared.Variant
        }) | Out-Null
    }

    # No arbitration or GUI authority is granted by this diagnostic result.
    return [pscustomobject]@{
        PrimaryGuiDecision = $PrimaryGuiDecision
        SharedDiagnostics = $diagnostics.ToArray()
        RelevantEventCount = $events.Count
        IndexBuildVisitCount = $index.Statistics.build_visit_count
        BoundedLookupVisitCount = $index.Statistics.bounded_lookup_visit_count
    }
}
