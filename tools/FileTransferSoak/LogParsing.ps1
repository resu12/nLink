Set-StrictMode -Version Latest
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'FileTransferAuthorityMatching.ps1')

$script:FileTransferRetainedEventIndex = $null
$script:FileTransferRetainedEventQueryCache = @{}

function Get-FileTransferDefaultLogDir {
    $localAppData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)
    return (Join-Path $localAppData 'nLink\logs')
}

function Resolve-FileTransferLogFiles {
    param(
        [string[]]$LogPath = @(),
        [string]$LogDir = ''
    )

    $resolved = New-Object System.Collections.Generic.List[string]

    if ($null -ne $LogPath -and $LogPath.Count -gt 0) {
        foreach ($path in $LogPath) {
            if ([string]::IsNullOrWhiteSpace($path)) {
                continue
            }

            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $resolved.Add((Resolve-Path -LiteralPath $path).Path) | Out-Null
            }
        }

        return [string[]]@($resolved | Select-Object -Unique)
    }

    $effectiveLogDir = $LogDir
    if ([string]::IsNullOrWhiteSpace($effectiveLogDir)) {
        $effectiveLogDir = Get-FileTransferDefaultLogDir
    }

    if (-not (Test-Path -LiteralPath $effectiveLogDir -PathType Container)) {
        return @()
    }

    return [string[]]@(
        Get-ChildItem -LiteralPath $effectiveLogDir -File -Filter '*.log' |
            Sort-Object LastWriteTimeUtc, Name |
            ForEach-Object { $_.FullName }
    )
}

function ConvertTo-FileTransferEventTimestamp {
    param([string]$TimestampText)

    if ([string]::IsNullOrWhiteSpace($TimestampText)) {
        return $null
    }

    try {
        return [datetimeoffset]::Parse(
            $TimestampText,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal)
    }
    catch {
        return $null
    }
}

function ConvertFrom-FileTransferLogLine {
    param(
        [Parameter(Mandatory = $true)][string]$Line,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][int]$LineNumber,
        [Parameter(Mandatory = $true)][int]$Sequence
    )

    # Retained rotation slices can contain a UTF-8 BOM at the first line of an
    # appended generation.  Normalize only that marker so the ordinary log
    # envelope remains parseable without altering the retained raw line.
    $normalizedLine = $Line.TrimStart([char[]]@([char]0xFEFF))
    if ($normalizedLine.IndexOf('event=', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        return $null
    }

    $timestampText = ''
    $level = ''
    $source = ''
    $message = $normalizedLine
    $match = [regex]::Match($normalizedLine, '^\[(?<timestamp>[^\]]+)\]\s+\[(?<level>[^\]]+)\]\s+\[(?<source>[^\]]+)\]\s+(?<message>.*)$')
    if ($match.Success) {
        $timestampText = $match.Groups['timestamp'].Value
        $level = $match.Groups['level'].Value
        $source = $match.Groups['source'].Value
        $message = $match.Groups['message'].Value
    }

    $fields = @{}
    foreach ($part in ($message -split ';')) {
        $segment = $part.Trim()
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }

        $separator = $segment.IndexOf('=')
        if ($separator -le 0) {
            continue
        }

        $key = $segment.Substring(0, $separator).Trim()
        $value = $segment.Substring($separator + 1).Trim()
        if (-not [string]::IsNullOrWhiteSpace($key)) {
            $fields[$key] = $value
        }
    }

    if (-not $fields.ContainsKey('event')) {
        return $null
    }

    $transferId = ''
    if ($fields.ContainsKey('transfer_id')) {
        $transferId = [string]$fields['transfer_id']
    }

    return [pscustomobject]@{
        TimestampUtc = ConvertTo-FileTransferEventTimestamp -TimestampText $timestampText
        TimestampText = $timestampText
        Level = $level
        Source = $source
        EventName = [string]$fields['event']
        Fields = $fields
        TransferId = $transferId
        FilePath = $FilePath
        FileName = [System.IO.Path]::GetFileName($FilePath)
        LineNumber = $LineNumber
        Sequence = $Sequence
        Message = $message
        RawLine = $Line
    }
}

function Read-FileTransferLogEvents {
    param(
        [string[]]$LogFiles = @(),
        [int]$TailMinutes = 0
    )

    $events = New-Object System.Collections.Generic.List[object]
    $sequence = 0
    $cutoff = $null
    if ($TailMinutes -gt 0) {
        $cutoff = [datetimeoffset]::UtcNow.AddMinutes(-1 * $TailMinutes)
    }

    foreach ($file in @($LogFiles)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            continue
        }

        $lineNumber = 0
        foreach ($line in [System.IO.File]::ReadLines($file)) {
            $lineNumber++
            $sequence++
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $event = ConvertFrom-FileTransferLogLine -Line $line -FilePath $file -LineNumber $lineNumber -Sequence $sequence
            if ($null -eq $event) {
                continue
            }

            if ($null -ne $cutoff -and $null -ne $event.TimestampUtc -and $event.TimestampUtc -lt $cutoff) {
                continue
            }

            $events.Add($event) | Out-Null
        }
    }

    return $events.ToArray()
}

function Get-FileTransferEventScopeKey {
    param([object[]]$Events)

    if ($null -eq $Events -or $Events.Count -le 0) {
        return 'empty'
    }

    $first = $Events[0]
    $last = $Events[$Events.Count - 1]
    return ('{0}|{1}|{2}|{3}|{4}|{5}|{6}' -f
        $Events.Count,
        $first.Sequence,
        $last.Sequence,
        $first.EventName,
        $last.EventName,
        $first.TransferId,
        $last.TransferId)
}

function Reset-FileTransferRetainedEventIndex {
    $script:FileTransferRetainedEventIndex = $null
    $script:FileTransferRetainedEventQueryCache = @{}
}

function New-FileTransferRetainedEventIndex {
    param(
        [object[]]$Events = @(),
        [string[]]$LogFiles = @(),
        [bool]$IncludeFieldStatistics = $true
    )

    $eventsByName = @{}
    $eventCounts = @{}
    $firstByName = @{}
    $lastByName = @{}
    $fieldMax = @{}
    $fieldSum = @{}
    $transferIds = New-Object System.Collections.Generic.HashSet[string]
    $sessionIds = New-Object System.Collections.Generic.HashSet[string]
    $criticalMarkerNames = New-Object System.Collections.Generic.HashSet[string]
    foreach ($markerName in @(
            'filetransfer_live_route_epoch_started',
            'filetransfer_route_selected',
            'file_transfer_inbound_terminal',
            'file_transfer_outbound_terminal',
            'transfer_terminal',
            'runtime_unlock_precommit_probe_acked',
            'runtime_unlock_paired_commit_confirmed',
            'filetransfer_fallback_tail_quiescent_yield_granted',
            'filetransfer_fallback_tail_yield_proof_sent',
            'filetransfer_fallback_tail_yield_proof_received',
            'filetransfer_fallback_tail_paired_yield_granted',
            'filetransfer_fallback_tail_yield_owner_snapshot',
            'filetransfer_fallback_tail_paired_yield_rejected',
            'filetransfer_fallback_tail_owner_generation_mismatch',
            'filetransfer_fallback_tail_yield_proof_stale_ignored',
            'filetransfer_fallback_tail_terminal_proof_consumed',
            'filetransfer_tuna_gui_live_reactivation_action_decided',
            'filetransfer_tuna_gui_live_reactivation_first_transfer_terminal_before_activation',
            'runtime_unlock_filetransfer_owner_missing_before_path_lease',
            'filetransfer_control_plane_send_retired_stale_generation',
            'filetransfer_runtime_owner_stale_update_rejected',
            'filetransfer_coordinator_leg_mutation_rejected',
            'filetransfer_runtime_unlock_callback_rejected',
            'filetransfer_recovery_authority_rejected',
            'filetransfer_route_publication_rejected',
            'filetransfer_route_hint_authorized_publication_accepted',
            'filetransfer_route_hint_ignored_missing_publication_authority',
            'filetransfer_route_hint_ignored_stale_publication_authority',
            'filetransfer_route_hint_ignored_invalid_publication_authority',
            'session_liveness_timeout')) {
        $criticalMarkerNames.Add($markerName) | Out-Null
    }
    $criticalMarkerExcerpts = New-Object System.Collections.Generic.List[string]

    foreach ($event in @($Events)) {
        $name = [string]$event.EventName
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }

        if (-not $eventsByName.ContainsKey($name)) {
            $eventsByName[$name] = New-Object System.Collections.Generic.List[object]
            $eventCounts[$name] = 0
            $firstByName[$name] = $event
        }

        $eventsByName[$name].Add($event) | Out-Null
        $eventCounts[$name] = [int]$eventCounts[$name] + 1
        $lastByName[$name] = $event

        if ($IncludeFieldStatistics -and $null -ne $event.Fields) {
            foreach ($fieldName in @($event.Fields.Keys)) {
                $numericValue = 0L
                if (-not [long]::TryParse([string]$event.Fields[$fieldName], [ref]$numericValue)) {
                    continue
                }

                if (-not $fieldMax.ContainsKey($fieldName) -or $numericValue -gt [long]$fieldMax[$fieldName]) {
                    $fieldMax[$fieldName] = $numericValue
                }

                if (-not $fieldSum.ContainsKey($fieldName)) {
                    $fieldSum[$fieldName] = 0L
                }

                $currentSum = [long]$fieldSum[$fieldName]
                if ($numericValue -gt 0 -and $currentSum -gt ([long]::MaxValue - $numericValue)) {
                    $fieldSum[$fieldName] = [long]::MaxValue
                }
                elseif ($numericValue -lt 0 -and $currentSum -lt ([long]::MinValue - $numericValue)) {
                    $fieldSum[$fieldName] = [long]::MinValue
                }
                else {
                    $fieldSum[$fieldName] = $currentSum + $numericValue
                }
            }
        }

        if (-not [string]::IsNullOrWhiteSpace([string]$event.TransferId)) {
            $transferIds.Add([string]$event.TransferId) | Out-Null
        }

        $sessionId = Get-FileTransferEventField -Event $event -Name 'session_id' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($sessionId) -and
            $sessionId -ne '(none)' -and
            $sessionId -ne '(unknown)') {
            $sessionIds.Add($sessionId) | Out-Null
        }

        if ($criticalMarkerNames.Contains($name) -and $criticalMarkerExcerpts.Count -lt 200) {
            $criticalMarkerExcerpts.Add((Format-FileTransferEvidenceLine -Event $event)) | Out-Null
        }

    }

    return [pscustomobject]@{
        LogFiles = @($LogFiles)
        # Preserve the parser's canonical sequence order so compact consumers
        # can select several event kinds in one pass instead of repeatedly
        # concatenating large name buckets and sorting the result again.
        AllEvents = @($Events)
        AllEventsScopeKey = Get-FileTransferEventScopeKey -Events $Events
        EventCount = @($Events).Count
        EventsByName = $eventsByName
        EventCounts = $eventCounts
        FirstEventByName = $firstByName
        LastEventByName = $lastByName
        FieldMax = $fieldMax
        FieldSum = $fieldSum
        TransferIds = @($transferIds)
        SessionIds = @($sessionIds)
        CriticalMarkerExcerpts = @($criticalMarkerExcerpts)
    }
}

function Initialize-FileTransferRetainedEventIndex {
    param(
        [object[]]$Events = @(),
        [string[]]$LogFiles = @(),
        [bool]$IncludeFieldStatistics = $true
    )

    $script:FileTransferRetainedEventQueryCache = @{}
    $script:FileTransferRetainedEventIndex = New-FileTransferRetainedEventIndex `
        -Events $Events `
        -LogFiles $LogFiles `
        -IncludeFieldStatistics:$IncludeFieldStatistics
    return $script:FileTransferRetainedEventIndex
}

function Get-FileTransferEventField {
    param(
        $Event,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Default = ''
    )

    if ($null -eq $Event) {
        return $Default
    }

    if ($null -ne $Event.Fields -and $Event.Fields.ContainsKey($Name)) {
        return [string]$Event.Fields[$Name]
    }

    return $Default
}

function Get-FileTransferEventInt64Field {
    param(
        $Event,
        [Parameter(Mandatory = $true)][string]$Name,
        [long]$Default = 0
    )

    $text = Get-FileTransferEventField -Event $Event -Name $Name -Default ''
    $value = 0L
    if ([long]::TryParse($text, [ref]$value)) {
        return $value
    }

    return $Default
}

function Format-FileTransferEvidenceLine {
    param([Parameter(Mandatory = $true)]$Event)

    return ('{0}:{1}: {2}' -f $Event.FileName, $Event.LineNumber, $Event.Message)
}
