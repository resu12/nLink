Set-StrictMode -Version Latest

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

    if ($Line.IndexOf('event=', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        return $null
    }

    $timestampText = ''
    $level = ''
    $source = ''
    $message = $Line
    $match = [regex]::Match($Line, '^\[(?<timestamp>[^\]]+)\]\s+\[(?<level>[^\]]+)\]\s+\[(?<source>[^\]]+)\]\s+(?<message>.*)$')
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

function Get-FileTransferEventField {
    param(
        [Parameter(Mandatory = $true)]$Event,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Default = ''
    )

    if ($null -ne $Event.Fields -and $Event.Fields.ContainsKey($Name)) {
        return [string]$Event.Fields[$Name]
    }

    return $Default
}

function Get-FileTransferEventInt64Field {
    param(
        [Parameter(Mandatory = $true)]$Event,
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
