Set-StrictMode -Version Latest

function New-GuiSmokeLogLedgerState {
    param(
        [int]$MaxLines = 500000,
        [int]$MaxGenerations = 512
    )

    if ($MaxLines -le 0) {
        throw 'Gui smoke log ledger MaxLines must be positive.'
    }
    if ($MaxGenerations -le 0) {
        throw 'Gui smoke log ledger MaxGenerations must be positive.'
    }

    return [pscustomobject]@{
        Lines = New-Object System.Collections.Generic.List[string]
        Generations = @{}
        MaxLines = $MaxLines
        MaxGenerations = $MaxGenerations
        GapCount = 0
        RotationCount = 0
        SyncCount = 0
        ActiveGenerationKey = ''
        FailureCode = ''
        FailureDetail = ''
    }
}

function Reset-GuiSmokeLogLedgerState {
    param([Parameter(Mandatory = $true)]$State)

    $State.Lines.Clear()
    $State.Generations.Clear()
    $State.GapCount = 0
    $State.RotationCount = 0
    $State.SyncCount = 0
    $State.ActiveGenerationKey = ''
    $State.FailureCode = ''
    $State.FailureDetail = ''
}

function Get-GuiSmokeLogLedgerHash {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-GuiSmokeLogLedgerFirstCompleteNonEmptyLine {
    param([Parameter(Mandatory = $true)][System.IO.FileStream]$Stream)

    $length = [long]$Stream.Length
    if ($length -le 0) {
        return $null
    }

    $readLimit = [Math]::Min(16384L, $length)
    while ($readLimit -le 4194304L) {
        [void]$Stream.Seek(0L, [System.IO.SeekOrigin]::Begin)
        $buffer = New-Object byte[] ([int]$readLimit)
        $bytesRead = 0
        while ($bytesRead -lt $buffer.Length) {
            $read = $Stream.Read($buffer, $bytesRead, $buffer.Length - $bytesRead)
            if ($read -le 0) {
                break
            }
            $bytesRead += $read
        }

        $lineStart = 0
        while ($lineStart -lt $bytesRead) {
            $newlineIndex = [Array]::IndexOf($buffer, [byte]10, $lineStart, $bytesRead - $lineStart)
            if ($newlineIndex -lt 0) {
                break
            }

            $line = [System.Text.Encoding]::UTF8.GetString($buffer, $lineStart, $newlineIndex - $lineStart)
            $line = $line.TrimEnd([char[]]@([char]13))
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                return $line
            }
            $lineStart = $newlineIndex + 1
        }

        if ($readLimit -ge $length) {
            return $null
        }

        $nextReadLimit = [Math]::Min(4194304L, [Math]::Min($length, $readLimit * 4L))
        if ($nextReadLimit -le $readLimit) {
            break
        }
        $readLimit = $nextReadLimit
    }

    throw 'gui_smoke_log_cursor_identity_line_too_long: first complete non-empty line exceeded 4 MiB'
}

function Read-GuiSmokeLogLedgerFileSnapshot {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $stream = $null
    try {
        $creationTimeUtcTicks = [System.IO.File]::GetCreationTimeUtc($Path).Ticks
        $shareMode = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
        $stream = [System.IO.FileStream]::new(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            $shareMode)
        $firstLine = Get-GuiSmokeLogLedgerFirstCompleteNonEmptyLine -Stream $stream
        if ([string]::IsNullOrWhiteSpace($firstLine)) {
            return $null
        }

        $firstLineHash = Get-GuiSmokeLogLedgerHash -Value $firstLine
        return [pscustomobject]@{
            Path = [System.IO.Path]::GetFullPath($Path)
            CreationTimeUtcTicks = $creationTimeUtcTicks
            GenerationKey = ("{0}:{1}" -f $creationTimeUtcTicks, $firstLineHash)
            FirstLineHash = $firstLineHash
            Length = [long]$stream.Length
        }
    }
    catch {
        if ($_.Exception.Message.IndexOf('gui_smoke_log_cursor_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw
        }
        return $null
    }
    finally {
        if ($stream) {
            $stream.Dispose()
        }
    }
}

function Read-GuiSmokeLogLedgerFileDelta {
    param(
        [Parameter(Mandatory = $true)]$Snapshot,
        [Parameter(Mandatory = $true)][long]$ByteOffset
    )

    if (-not (Test-Path -LiteralPath $Snapshot.Path -PathType Leaf)) {
        return $null
    }

    $stream = $null
    try {
        $creationTimeUtcTicks = [System.IO.File]::GetCreationTimeUtc([string]$Snapshot.Path).Ticks
        $shareMode = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
        $stream = [System.IO.FileStream]::new(
            [string]$Snapshot.Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            $shareMode)
        $firstLine = Get-GuiSmokeLogLedgerFirstCompleteNonEmptyLine -Stream $stream
        if ([string]::IsNullOrWhiteSpace($firstLine)) {
            return $null
        }

        $firstLineHash = Get-GuiSmokeLogLedgerHash -Value $firstLine
        $generationKey = "{0}:{1}" -f $creationTimeUtcTicks, $firstLineHash
        if (-not [string]::Equals($generationKey, [string]$Snapshot.GenerationKey, [System.StringComparison]::Ordinal)) {
            return $null
        }

        $currentLength = [long]$stream.Length
        if ($currentLength -lt $ByteOffset) {
            return [pscustomobject]@{
                Truncated = $true
                CurrentLength = $currentLength
                NextByteOffset = $ByteOffset
                CompleteLineCount = 0
                Lines = @()
            }
        }
        if ($currentLength -eq $ByteOffset) {
            return [pscustomobject]@{
                Truncated = $false
                CurrentLength = $currentLength
                NextByteOffset = $ByteOffset
                CompleteLineCount = 0
                Lines = @()
            }
        }

        $remaining = $currentLength - $ByteOffset
        if ($remaining -gt [int]::MaxValue) {
            throw ("gui_smoke_log_cursor_read_window_overflow: path={0}; unread_bytes={1}" -f $Snapshot.Path, $remaining)
        }

        [void]$stream.Seek($ByteOffset, [System.IO.SeekOrigin]::Begin)
        $bytes = New-Object byte[] ([int]$remaining)
        $bytesRead = 0
        while ($bytesRead -lt $bytes.Length) {
            $read = $stream.Read($bytes, $bytesRead, $bytes.Length - $bytesRead)
            if ($read -le 0) {
                break
            }
            $bytesRead += $read
        }
        if ($bytesRead -lt $bytes.Length) {
            return [pscustomobject]@{
                Truncated = $true
                CurrentLength = [long]$stream.Length
                NextByteOffset = $ByteOffset
                CompleteLineCount = 0
                Lines = @()
            }
        }

        $completeByteCount = 0
        for ($index = $bytesRead - 1; $index -ge 0; $index--) {
            if ($bytes[$index] -eq 10) {
                $completeByteCount = $index + 1
                break
            }
        }
        if ($completeByteCount -le 0) {
            return [pscustomobject]@{
                Truncated = $false
                CurrentLength = $currentLength
                NextByteOffset = $ByteOffset
                CompleteLineCount = 0
                Lines = @()
            }
        }

        $text = [System.Text.Encoding]::UTF8.GetString($bytes, 0, $completeByteCount)
        $segments = @($text -split '\r?\n')
        $completeLineCount = [Math]::Max(0, $segments.Count - 1)
        $lines = New-Object System.Collections.Generic.List[string]
        for ($index = 0; $index -lt $completeLineCount; $index++) {
            $lines.Add([string]$segments[$index]) | Out-Null
        }

        return [pscustomobject]@{
            Truncated = $false
            CurrentLength = $currentLength
            NextByteOffset = $ByteOffset + $completeByteCount
            CompleteLineCount = $completeLineCount
            Lines = $lines.ToArray()
        }
    }
    catch {
        if ($_.Exception.Message.IndexOf('gui_smoke_log_cursor_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw
        }
        return $null
    }
    finally {
        if ($stream) {
            $stream.Dispose()
        }
    }
}

function Set-GuiSmokeLogLedgerFailure {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Detail
    )

    if ([string]::IsNullOrWhiteSpace([string]$State.FailureCode)) {
        $State.FailureCode = $Code
        $State.FailureDetail = $Detail
    }
}

function Assert-GuiSmokeLogLedgerHealthy {
    param([Parameter(Mandatory = $true)]$State)

    if (-not [string]::IsNullOrWhiteSpace([string]$State.FailureCode)) {
        throw ("{0}: {1}" -f $State.FailureCode, $State.FailureDetail)
    }
}

function Sync-GuiSmokeLogLedger {
    param(
        [Parameter(Mandatory = $true)]$State,
        [AllowEmptyCollection()][string[]]$Paths = @()
    )

    Assert-GuiSmokeLogLedgerHealthy -State $State
    $State.SyncCount++

    $snapshots = New-Object System.Collections.Generic.List[object]
    foreach ($path in @($Paths)) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $snapshot = Read-GuiSmokeLogLedgerFileSnapshot -Path $path
        if ($null -eq $snapshot) {
            continue
        }

        $snapshots.Add($snapshot) | Out-Null
    }

    $snapshotByGeneration = [ordered]@{}
    foreach ($snapshot in $snapshots.ToArray()) {
        $generationKey = [string]$snapshot.GenerationKey
        $existingSnapshot = $snapshotByGeneration[$generationKey]
        if ($null -eq $existingSnapshot -or [long]$snapshot.Length -gt [long]$existingSnapshot.Length) {
            $snapshotByGeneration[$generationKey] = $snapshot
        }
    }

    foreach ($snapshot in @($snapshotByGeneration.Values)) {

        $generationKey = [string]$snapshot.GenerationKey
        $generation = $State.Generations[$generationKey]
        if ($null -eq $generation) {
            if ($State.Generations.Count -ge $State.MaxGenerations) {
                Set-GuiSmokeLogLedgerFailure `
                    -State $State `
                    -Code 'gui_smoke_log_cursor_generation_limit' `
                    -Detail ("max_generations={0}; path={1}" -f $State.MaxGenerations, $snapshot.Path)
                Assert-GuiSmokeLogLedgerHealthy -State $State
            }

            $generation = [pscustomobject]@{
                ConsumedByteOffset = 0L
                ConsumedLineCount = 0L
                LastPath = [string]$snapshot.Path
                FirstLineHash = [string]$snapshot.FirstLineHash
            }
            $State.Generations[$generationKey] = $generation
        }
        elseif (-not [string]::Equals([string]$generation.LastPath, [string]$snapshot.Path, [System.StringComparison]::OrdinalIgnoreCase)) {
            $State.RotationCount++
            $generation.LastPath = [string]$snapshot.Path
        }

        $delta = Read-GuiSmokeLogLedgerFileDelta -Snapshot $snapshot -ByteOffset ([long]$generation.ConsumedByteOffset)
        if ($null -eq $delta) {
            $State.GapCount++
            Set-GuiSmokeLogLedgerFailure `
                -State $State `
                -Code 'gui_smoke_log_cursor_gap' `
                -Detail ("path={0}; generation={1}; reason=generation_changed_during_sync" -f $snapshot.Path, $generationKey)
            Assert-GuiSmokeLogLedgerHealthy -State $State
        }
        if ([bool]$delta.Truncated) {
            $State.GapCount++
            Set-GuiSmokeLogLedgerFailure `
                -State $State `
                -Code 'gui_smoke_log_cursor_truncated' `
                -Detail ("path={0}; generation={1}; consumed_bytes={2}; current_bytes={3}; consumed_lines={4}" -f $snapshot.Path, $generationKey, $generation.ConsumedByteOffset, $delta.CurrentLength, $generation.ConsumedLineCount)
            Assert-GuiSmokeLogLedgerHealthy -State $State
        }

        foreach ($line in @($delta.Lines)) {
            if ($State.Lines.Count -ge $State.MaxLines) {
                Set-GuiSmokeLogLedgerFailure `
                    -State $State `
                    -Code 'gui_smoke_log_cursor_overflow' `
                    -Detail ("max_lines={0}; generations={1}; path={2}" -f $State.MaxLines, $State.Generations.Count, $snapshot.Path)
                Assert-GuiSmokeLogLedgerHealthy -State $State
            }

            $State.Lines.Add([string]$line) | Out-Null
        }

        $generation.ConsumedByteOffset = [long]$delta.NextByteOffset
        $generation.ConsumedLineCount += [long]$delta.CompleteLineCount
    }

    $activeSnapshot = @($snapshots | Where-Object {
        [string]::Equals(
            [System.IO.Path]::GetFileName([string]$_.Path),
            'nlink.log',
            [System.StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -Last 1)
    if ($activeSnapshot.Count -gt 0) {
        $newActiveGenerationKey = [string]$activeSnapshot[0].GenerationKey
        if (-not [string]::IsNullOrWhiteSpace([string]$State.ActiveGenerationKey) -and
            -not [string]::Equals([string]$State.ActiveGenerationKey, $newActiveGenerationKey, [System.StringComparison]::Ordinal)) {
            $previousActiveStillRetained = @($snapshots | Where-Object {
                [string]::Equals([string]$_.GenerationKey, [string]$State.ActiveGenerationKey, [System.StringComparison]::Ordinal)
            }).Count -gt 0
            if (-not $previousActiveStillRetained) {
                $State.GapCount++
                Set-GuiSmokeLogLedgerFailure `
                    -State $State `
                    -Code 'gui_smoke_log_cursor_gap' `
                    -Detail ("previous_generation={0}; current_generation={1}; retained_generations={2}" -f $State.ActiveGenerationKey, $newActiveGenerationKey, $snapshots.Count)
                Assert-GuiSmokeLogLedgerHealthy -State $State
            }
        }

        $State.ActiveGenerationKey = $newActiveGenerationKey
    }

    Assert-GuiSmokeLogLedgerHealthy -State $State
}

function Get-GuiSmokeLogLedgerBookmark {
    param([Parameter(Mandatory = $true)]$State)

    Assert-GuiSmokeLogLedgerHealthy -State $State
    return [int]$State.Lines.Count
}

function Get-GuiSmokeLogLedgerLines {
    param([Parameter(Mandatory = $true)]$State)

    Assert-GuiSmokeLogLedgerHealthy -State $State
    return @($State.Lines.ToArray())
}

function Get-GuiSmokeLogLedgerLinesAfterBookmark {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][int]$Bookmark
    )

    Assert-GuiSmokeLogLedgerHealthy -State $State
    if ($Bookmark -lt 0 -or $Bookmark -gt $State.Lines.Count) {
        Set-GuiSmokeLogLedgerFailure `
            -State $State `
            -Code 'gui_smoke_log_cursor_invalid_bookmark' `
            -Detail ("bookmark={0}; line_count={1}" -f $Bookmark, $State.Lines.Count)
        Assert-GuiSmokeLogLedgerHealthy -State $State
    }
    if ($Bookmark -eq $State.Lines.Count) {
        return @()
    }

    return @($State.Lines.GetRange($Bookmark, $State.Lines.Count - $Bookmark).ToArray())
}

function Get-GuiSmokeLogLedgerDiagnostics {
    param([Parameter(Mandatory = $true)]$State)

    return [pscustomobject]@{
        LineCount = [int]$State.Lines.Count
        GenerationCount = [int]$State.Generations.Count
        RotationCount = [int]$State.RotationCount
        GapCount = [int]$State.GapCount
        SyncCount = [int]$State.SyncCount
        FailureCode = if ([string]::IsNullOrWhiteSpace([string]$State.FailureCode)) { '(none)' } else { [string]$State.FailureCode }
        FailureDetail = if ([string]::IsNullOrWhiteSpace([string]$State.FailureDetail)) { '(none)' } else { [string]$State.FailureDetail }
    }
}
