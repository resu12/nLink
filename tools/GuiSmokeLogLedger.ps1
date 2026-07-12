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
        LedgerId = [guid]::NewGuid().ToString('N')
        Lines = New-Object System.Collections.Generic.List[string]
        Generations = @{}
        ConsumedLineIdentities = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::Ordinal)
        MaxLines = $MaxLines
        MaxGenerations = $MaxGenerations
        GapCount = 0
        DuplicateIndexCount = 0
        RotationCount = 0
        TransientRescanCount = 0
        SyncCount = 0
        InitialGenerationKey = ''
        InitialGenerationSnapshots = @()
        ActiveGenerationKey = ''
        FailureCode = ''
        FailureDetail = ''
    }
}

function Reset-GuiSmokeLogLedgerState {
    param([Parameter(Mandatory = $true)]$State)

    $State.LedgerId = [guid]::NewGuid().ToString('N')
    $State.Lines.Clear()
    $State.Generations.Clear()
    $State.ConsumedLineIdentities.Clear()
    $State.GapCount = 0
    $State.DuplicateIndexCount = 0
    $State.RotationCount = 0
    $State.TransientRescanCount = 0
    $State.SyncCount = 0
    $State.InitialGenerationKey = ''
    $State.InitialGenerationSnapshots = @()
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

function Open-GuiSmokeLogLedgerFileSnapshot {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{ Status = 'Missing'; Snapshot = $null; Detail = 'path_missing' }
    }

    $stream = $null
    try {
        $shareMode = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
        $stream = [System.IO.FileStream]::new(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            $shareMode)
        $creationTimeUtcTicks = [System.IO.File]::GetCreationTimeUtc($stream.SafeFileHandle).Ticks
        $firstLine = Get-GuiSmokeLogLedgerFirstCompleteNonEmptyLine -Stream $stream
        if ([string]::IsNullOrWhiteSpace($firstLine)) {
            $stream.Dispose()
            $stream = $null
            return [pscustomobject]@{ Status = 'Empty'; Snapshot = $null; Detail = 'identity_line_pending' }
        }

        $firstLineHash = Get-GuiSmokeLogLedgerHash -Value $firstLine
        $snapshot = [pscustomobject]@{
            Path = [System.IO.Path]::GetFullPath($Path)
            CreationTimeUtcTicks = $creationTimeUtcTicks
            GenerationKey = ("{0}:{1}" -f $creationTimeUtcTicks, $firstLineHash)
            FirstLineHash = $firstLineHash
            CapturedLength = [long]$stream.Length
            IsActivePath = [string]::Equals(
                [System.IO.Path]::GetFileName($Path),
                'nlink.log',
                [System.StringComparison]::OrdinalIgnoreCase)
            Stream = $stream
        }

        $testHook = Get-Variable -Name GuiSmokeLogLedgerAfterSnapshotOpenedForTests -Scope Script -ErrorAction SilentlyContinue
        if ($null -ne $testHook -and $testHook.Value -is [scriptblock]) {
            & $testHook.Value $snapshot
        }

        $stream = $null
        return [pscustomobject]@{ Status = 'Opened'; Snapshot = $snapshot; Detail = 'opened' }
    }
    catch {
        if ($_.Exception.Message.IndexOf('gui_smoke_log_cursor_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw
        }
        return [pscustomobject]@{
            Status = 'Unavailable'
            Snapshot = $null
            Detail = $_.Exception.GetType().Name
        }
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

    $stream = [System.IO.FileStream]$Snapshot.Stream
    $capturedLength = [long]$Snapshot.CapturedLength
    try {
        $currentLength = [long]$stream.Length
        if ($currentLength -lt $capturedLength) {
            return [pscustomobject]@{
                Truncated = $true
                CurrentLength = $currentLength
                NextByteOffset = $ByteOffset
                CompleteLineCount = 0
                Lines = @()
            }
        }
        if ($currentLength -lt $ByteOffset) {
            return [pscustomobject]@{
                Truncated = $true
                CurrentLength = $currentLength
                NextByteOffset = $ByteOffset
                CompleteLineCount = 0
                Lines = @()
            }
        }
        if ($capturedLength -eq $ByteOffset) {
            return [pscustomobject]@{
                Truncated = $false
                CurrentLength = $capturedLength
                NextByteOffset = $ByteOffset
                CompleteLineCount = 0
                Lines = @()
            }
        }

        $remaining = $capturedLength - $ByteOffset
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
                CurrentLength = $capturedLength
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
            CurrentLength = $capturedLength
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
}

function Get-GuiSmokeLogLedgerScanPaths {
    param([AllowEmptyCollection()][string[]]$Paths = @())

    $resolved = New-Object System.Collections.Generic.List[string]
    $rollingDirectories = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @($Paths)) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath($path)
        if ([regex]::IsMatch([System.IO.Path]::GetFileName($fullPath), '^nlink(?:\.\d+)?\.log$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            [void]$rollingDirectories.Add([System.IO.Path]::GetDirectoryName($fullPath))
        }
        else {
            $resolved.Add($fullPath) | Out-Null
        }
    }

    foreach ($directory in $rollingDirectories) {
        if (-not [System.IO.Directory]::Exists($directory)) {
            continue
        }

        foreach ($file in @(
                Get-ChildItem -LiteralPath $directory -File -Filter 'nlink*.log' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTimeUtc, Name)) {
            $resolved.Add([string]$file.FullName) | Out-Null
        }
    }

    return @($resolved.ToArray())
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

    $maxAcquisitionAttempts = 4
    $acquisitionRetryDelayMs = 25
    $acceptedSnapshots = @()
    $acceptedHandles = @()
    $acceptedActiveGenerationKey = ''

    for ($attempt = 1; $attempt -le $maxAcquisitionAttempts; $attempt++) {
        $openedSnapshots = New-Object System.Collections.Generic.List[object]
        $snapshotByGeneration = [ordered]@{}
        $snapshotGenerationByPath = @{}
        $scanUnstable = $false
        $scanFailureDetail = ''
        try {
            foreach ($path in @(Get-GuiSmokeLogLedgerScanPaths -Paths $Paths)) {
                $openResult = Open-GuiSmokeLogLedgerFileSnapshot -Path $path
                if ([string]$openResult.Status -eq 'Opened') {
                    $snapshot = $openResult.Snapshot
                    $openedSnapshots.Add($snapshot) | Out-Null
                    $snapshotGenerationByPath[[string]$snapshot.Path] = [string]$snapshot.GenerationKey
                    $generationKey = [string]$snapshot.GenerationKey
                    $existingSnapshot = $snapshotByGeneration[$generationKey]
                    if ($null -eq $existingSnapshot -or
                        [long]$snapshot.CapturedLength -gt [long]$existingSnapshot.CapturedLength) {
                        $snapshotByGeneration[$generationKey] = $snapshot
                    }
                }
                elseif ([string]$openResult.Status -in @('Missing', 'Unavailable')) {
                    $scanUnstable = $true
                    $scanFailureDetail = "path={0}; status={1}; detail={2}" -f $path, $openResult.Status, $openResult.Detail
                }
            }

            $validationSnapshots = New-Object System.Collections.Generic.List[object]
            try {
                $validationGenerationByPath = @{}
                foreach ($path in @(Get-GuiSmokeLogLedgerScanPaths -Paths $Paths)) {
                    $validationResult = Open-GuiSmokeLogLedgerFileSnapshot -Path $path
                    if ([string]$validationResult.Status -eq 'Opened') {
                        $validationSnapshot = $validationResult.Snapshot
                        $validationSnapshots.Add($validationSnapshot) | Out-Null
                        $validationGenerationByPath[[string]$validationSnapshot.Path] = [string]$validationSnapshot.GenerationKey
                    }
                    elseif ([string]$validationResult.Status -in @('Missing', 'Unavailable')) {
                        $scanUnstable = $true
                        $scanFailureDetail = "path={0}; status={1}; detail={2}; stage=validation" -f $path, $validationResult.Status, $validationResult.Detail
                    }
                }

                $acquiredPaths = @($snapshotGenerationByPath.Keys | Sort-Object)
                $validatedPaths = @($validationGenerationByPath.Keys | Sort-Object)
                if (($acquiredPaths -join '|') -ne ($validatedPaths -join '|')) {
                    $scanUnstable = $true
                    $scanFailureDetail = "reason=path_set_changed_during_acquisition; acquired={0}; validated={1}" -f $acquiredPaths.Count, $validatedPaths.Count
                }
                else {
                    foreach ($path in $acquiredPaths) {
                        if (-not [string]::Equals(
                                [string]$snapshotGenerationByPath[$path],
                                [string]$validationGenerationByPath[$path],
                                [System.StringComparison]::Ordinal)) {
                            $scanUnstable = $true
                            $scanFailureDetail = "path={0}; reason=generation_map_changed_during_acquisition" -f $path
                            break
                        }
                    }
                }
            }
            finally {
                foreach ($validationSnapshot in @($validationSnapshots.ToArray())) {
                    try { $validationSnapshot.Stream.Dispose() } catch {}
                }
            }

            $candidateSnapshots = @($snapshotByGeneration.Values)
            $activeSnapshot = @($candidateSnapshots | Where-Object { [bool]$_.IsActivePath } | Select-Object -Last 1)
            $candidateActiveGenerationKey = if ($activeSnapshot.Count -gt 0) {
                [string]$activeSnapshot[0].GenerationKey
            }
            else {
                ''
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$State.ActiveGenerationKey) -and
                -not [string]::IsNullOrWhiteSpace($candidateActiveGenerationKey) -and
                -not [string]::Equals([string]$State.ActiveGenerationKey, $candidateActiveGenerationKey, [System.StringComparison]::Ordinal)) {
                $previousActiveStillRetained = @($candidateSnapshots | Where-Object {
                    [string]::Equals([string]$_.GenerationKey, [string]$State.ActiveGenerationKey, [System.StringComparison]::Ordinal)
                }).Count -gt 0
                if (-not $previousActiveStillRetained) {
                    $scanUnstable = $true
                    $scanFailureDetail = "previous_generation={0}; current_generation={1}; reason=active_predecessor_not_observed" -f $State.ActiveGenerationKey, $candidateActiveGenerationKey
                }
            }

            if ($scanUnstable) {
                if ($attempt -ge $maxAcquisitionAttempts) {
                    $State.GapCount++
                    Set-GuiSmokeLogLedgerFailure `
                        -State $State `
                        -Code 'gui_smoke_log_cursor_gap' `
                        -Detail ("reason=active_generation_missing_after_reconciliation; attempts={0}; {1}" -f $maxAcquisitionAttempts, $scanFailureDetail)
                    Assert-GuiSmokeLogLedgerHealthy -State $State
                }

                $State.TransientRescanCount++
                Start-Sleep -Milliseconds $acquisitionRetryDelayMs
                continue
            }

            $acceptedSnapshots = $candidateSnapshots
            $acceptedHandles = @($openedSnapshots.ToArray())
            $acceptedActiveGenerationKey = $candidateActiveGenerationKey
            $openedSnapshots = $null
            break
        }
        finally {
            if ($null -ne $openedSnapshots) {
                foreach ($snapshot in @($openedSnapshots.ToArray())) {
                    try { $snapshot.Stream.Dispose() } catch {}
                }
            }
        }
    }

    try {
        $pendingDeltas = New-Object System.Collections.Generic.List[object]
        $newGenerationCount = 0
        $newLineCount = 0
        foreach ($snapshot in @($acceptedSnapshots)) {
            $generationKey = [string]$snapshot.GenerationKey
            $generation = $State.Generations[$generationKey]
            $consumedByteOffset = if ($null -eq $generation) { 0L } else { [long]$generation.ConsumedByteOffset }
            $delta = Read-GuiSmokeLogLedgerFileDelta -Snapshot $snapshot -ByteOffset $consumedByteOffset
            if ($null -eq $delta) {
                $State.GapCount++
                Set-GuiSmokeLogLedgerFailure `
                    -State $State `
                    -Code 'gui_smoke_log_cursor_gap' `
                    -Detail ("path={0}; generation={1}; reason=bound_handle_read_failed" -f $snapshot.Path, $generationKey)
                Assert-GuiSmokeLogLedgerHealthy -State $State
            }
            if ([bool]$delta.Truncated) {
                $State.GapCount++
                Set-GuiSmokeLogLedgerFailure `
                    -State $State `
                    -Code 'gui_smoke_log_cursor_truncated' `
                    -Detail ("path={0}; generation={1}; consumed_bytes={2}; current_bytes={3}; consumed_lines={4}" -f $snapshot.Path, $generationKey, $consumedByteOffset, $delta.CurrentLength, $(if ($null -eq $generation) { 0 } else { $generation.ConsumedLineCount }))
                Assert-GuiSmokeLogLedgerHealthy -State $State
            }

            if ($null -eq $generation) {
                $newGenerationCount++
            }
            $newLineCount += @($delta.Lines).Count
            $pendingDeltas.Add([pscustomobject]@{
                Snapshot = $snapshot
                Generation = $generation
                GenerationKey = $generationKey
                Delta = $delta
            }) | Out-Null
        }

        if ($State.Generations.Count + $newGenerationCount -gt $State.MaxGenerations) {
            Set-GuiSmokeLogLedgerFailure `
                -State $State `
                -Code 'gui_smoke_log_cursor_generation_limit' `
                -Detail ("max_generations={0}; current_generations={1}; pending_generations={2}" -f $State.MaxGenerations, $State.Generations.Count, $newGenerationCount)
            Assert-GuiSmokeLogLedgerHealthy -State $State
        }
        if ($State.Lines.Count + $newLineCount -gt $State.MaxLines) {
            Set-GuiSmokeLogLedgerFailure `
                -State $State `
                -Code 'gui_smoke_log_cursor_overflow' `
                -Detail ("max_lines={0}; current_lines={1}; pending_lines={2}; generations={3}" -f $State.MaxLines, $State.Lines.Count, $newLineCount, $State.Generations.Count)
            Assert-GuiSmokeLogLedgerHealthy -State $State
        }

        $pendingLineIdentities = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::Ordinal)
        foreach ($pending in @($pendingDeltas.ToArray())) {
            $generation = $pending.Generation
            $firstLineOrdinal = if ($null -eq $generation) { 0L } else { [long]$generation.ConsumedLineCount }
            $lines = @($pending.Delta.Lines)
            for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
                $lineOrdinal = $firstLineOrdinal + [long]$lineIndex
                $lineIdentity = "{0}`n{1}" -f [string]$pending.GenerationKey, $lineOrdinal
                if ($State.ConsumedLineIdentities.Contains($lineIdentity) -or -not $pendingLineIdentities.Add($lineIdentity)) {
                    $State.DuplicateIndexCount++
                    Set-GuiSmokeLogLedgerFailure `
                        -State $State `
                        -Code 'gui_smoke_log_cursor_duplicate_index' `
                        -Detail ("generation={0}; line_ordinal={1}" -f $pending.GenerationKey, $lineOrdinal)
                    Assert-GuiSmokeLogLedgerHealthy -State $State
                }
            }
        }

        foreach ($pending in @($pendingDeltas.ToArray())) {
            $snapshot = $pending.Snapshot
            $generation = $pending.Generation
            if ($null -eq $generation) {
                $generation = [pscustomobject]@{
                    GenerationKey = [string]$pending.GenerationKey
                    CreationTimeUtcTicks = [long]$snapshot.CreationTimeUtcTicks
                    ConsumedByteOffset = 0L
                    ConsumedLineCount = 0L
                    LastPath = [string]$snapshot.Path
                    FirstLineHash = [string]$snapshot.FirstLineHash
                    IsActivePath = [bool]$snapshot.IsActivePath
                }
                $State.Generations[[string]$pending.GenerationKey] = $generation
            }
            elseif (-not [string]::Equals([string]$generation.LastPath, [string]$snapshot.Path, [System.StringComparison]::OrdinalIgnoreCase)) {
                $State.RotationCount++
                $generation.LastPath = [string]$snapshot.Path
            }
            $generation.IsActivePath = [bool]$snapshot.IsActivePath

            $firstLineOrdinal = [long]$generation.ConsumedLineCount
            $lineIndex = 0L
            foreach ($line in @($pending.Delta.Lines)) {
                $lineIdentity = "{0}`n{1}" -f [string]$pending.GenerationKey, ($firstLineOrdinal + $lineIndex)
                [void]$State.ConsumedLineIdentities.Add($lineIdentity)
                $State.Lines.Add([string]$line) | Out-Null
                $lineIndex++
            }
            $generation.ConsumedByteOffset = [long]$pending.Delta.NextByteOffset
            $generation.ConsumedLineCount += [long]$pending.Delta.CompleteLineCount
        }

        if (-not [string]::IsNullOrWhiteSpace($acceptedActiveGenerationKey)) {
            $State.ActiveGenerationKey = $acceptedActiveGenerationKey
        }

        if ([string]::IsNullOrWhiteSpace([string]$State.InitialGenerationKey) -and $State.Generations.Count -gt 0) {
            $initialGenerationKey = if (-not [string]::IsNullOrWhiteSpace($acceptedActiveGenerationKey)) {
                $acceptedActiveGenerationKey
            }
            else {
                [string](@($State.Generations.Values |
                    Sort-Object CreationTimeUtcTicks, GenerationKey |
                    Select-Object -First 1)[0].GenerationKey)
            }
            $State.InitialGenerationKey = $initialGenerationKey
            $State.InitialGenerationSnapshots = @(
                Get-GuiSmokeLogLedgerGenerationSnapshots -State $State |
                    ForEach-Object {
                        [pscustomobject]@{
                            generationKey = [string]$_.generationKey
                            creationTimeUtcTicks = [long]$_.creationTimeUtcTicks
                            firstLineHash = [string]$_.firstLineHash
                            lastPath = [string]$_.lastPath
                            isActivePath = [bool]$_.isActivePath
                            consumedByteOffset = 0L
                            consumedLineCount = 0L
                        }
                    }
            )
        }

        Assert-GuiSmokeLogLedgerHealthy -State $State
    }
    finally {
        foreach ($snapshot in @($acceptedHandles)) {
            try { $snapshot.Stream.Dispose() } catch {}
        }
    }
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

    $consumedIdentities = @($State.ConsumedLineIdentities | Sort-Object)
    $consumedIndexSha256 = Get-GuiSmokeLogLedgerHash -Value ($consumedIdentities -join [char]31)
    $consumedIndexRanges = @(
        $State.Generations.Values |
            Sort-Object CreationTimeUtcTicks, GenerationKey |
            ForEach-Object {
                $count = [long]$_.ConsumedLineCount
                [pscustomobject]@{
                    generationKey = [string]$_.GenerationKey
                    firstLineOrdinal = $(if ($count -gt 0) { 0L } else { -1L })
                    lastLineOrdinal = $(if ($count -gt 0) { $count - 1L } else { -1L })
                    count = $count
                }
            })

    return [pscustomobject]@{
        LedgerId = [string]$State.LedgerId
        LineCount = [int]$State.Lines.Count
        ConsumedIndexCount = [int]$State.ConsumedLineIdentities.Count
        ConsumedIndexSha256 = $consumedIndexSha256
        ConsumedIndexRanges = $consumedIndexRanges
        GenerationCount = [int]$State.Generations.Count
        InitialGenerationKey = [string]$State.InitialGenerationKey
        ActiveGenerationKey = [string]$State.ActiveGenerationKey
        RotationCount = [int]$State.RotationCount
        TransientRescanCount = [int]$State.TransientRescanCount
        GapCount = [int]$State.GapCount
        DuplicateIndexCount = [int]$State.DuplicateIndexCount
        SyncCount = [int]$State.SyncCount
        FailureCode = if ([string]::IsNullOrWhiteSpace([string]$State.FailureCode)) { '(none)' } else { [string]$State.FailureCode }
        FailureDetail = if ([string]::IsNullOrWhiteSpace([string]$State.FailureDetail)) { '(none)' } else { [string]$State.FailureDetail }
    }
}

function Get-GuiSmokeLogLedgerGenerationSnapshots {
    param([Parameter(Mandatory = $true)]$State)

    return @(
        $State.Generations.Values |
            Sort-Object CreationTimeUtcTicks, GenerationKey |
            ForEach-Object {
                [pscustomobject]@{
                    generationKey = [string]$_.GenerationKey
                    creationTimeUtcTicks = [long]$_.CreationTimeUtcTicks
                    firstLineHash = [string]$_.FirstLineHash
                    lastPath = [string]$_.LastPath
                    isActivePath = [bool]$_.IsActivePath
                    consumedByteOffset = [long]$_.ConsumedByteOffset
                    consumedLineCount = [long]$_.ConsumedLineCount
                }
            }
    )
}

function Get-GuiSmokeLogLedgerSnapshot {
    param(
        [Parameter(Mandatory = $true)]$State,
        [int]$Bookmark = -1
    )

    $effectiveBookmark = if ($Bookmark -ge 0) { $Bookmark } else { [int]$State.Lines.Count }
    return [pscustomobject]@{
        ledgerId = [string]$State.LedgerId
        bookmark = [int]$effectiveBookmark
        lineCount = [int]$State.Lines.Count
        initialGenerationKey = [string]$State.InitialGenerationKey
        activeGenerationKey = [string]$State.ActiveGenerationKey
        generations = @(Get-GuiSmokeLogLedgerGenerationSnapshots -State $State)
        initialGenerations = @($State.InitialGenerationSnapshots)
    }
}

function Test-GuiSmokeLogLedgerSnapshotMonotonicSubset {
    param(
        [AllowEmptyCollection()]$InitialGenerations = @(),
        [AllowEmptyCollection()]$FinalGenerations = @()
    )

    $finalByKey = @{}
    foreach ($generation in @($FinalGenerations)) {
        $key = [string]$generation.generationKey
        if (-not [string]::IsNullOrWhiteSpace($key)) {
            $finalByKey[$key] = $generation
        }
    }

    foreach ($initial in @($InitialGenerations)) {
        $key = [string]$initial.generationKey
        if ([string]::IsNullOrWhiteSpace($key) -or -not $finalByKey.ContainsKey($key)) {
            return $false
        }

        $final = $finalByKey[$key]
        if ([long]$final.consumedByteOffset -lt [long]$initial.consumedByteOffset -or
            [long]$final.consumedLineCount -lt [long]$initial.consumedLineCount) {
            return $false
        }
    }

    return $true
}

function Get-GuiSmokeLogLedgerSnapshotIntegrityReasons {
    param(
        [Parameter(Mandatory = $true)][AllowNull()]$InitialSnapshot,
        [Parameter(Mandatory = $true)][AllowNull()]$FinalSnapshot
    )

    $reasons = New-Object System.Collections.Generic.List[string]
    if ($null -eq $InitialSnapshot) {
        $reasons.Add('initial_snapshot_missing') | Out-Null
        return @($reasons.ToArray())
    }
    if ($null -eq $FinalSnapshot) {
        $reasons.Add('final_snapshot_missing') | Out-Null
        return @($reasons.ToArray())
    }

    $initialLedgerId = [string]$InitialSnapshot.ledgerId
    $finalLedgerId = [string]$FinalSnapshot.ledgerId
    if ([string]::IsNullOrWhiteSpace($initialLedgerId)) {
        $reasons.Add('initial_ledger_id_missing') | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace($finalLedgerId)) {
        $reasons.Add('final_ledger_id_missing') | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($initialLedgerId) -and
        -not [string]::IsNullOrWhiteSpace($finalLedgerId) -and
        -not [string]::Equals($initialLedgerId, $finalLedgerId, [System.StringComparison]::Ordinal)) {
        $reasons.Add('ledger_id_changed') | Out-Null
    }

    $initialGenerationKey = [string]$InitialSnapshot.activeGenerationKey
    $finalGenerationKey = [string]$FinalSnapshot.activeGenerationKey
    $initialGenerations = @($InitialSnapshot.generations)
    $finalGenerations = @($FinalSnapshot.generations)
    if ([string]::IsNullOrWhiteSpace($initialGenerationKey)) {
        $reasons.Add('initial_generation_missing') | Out-Null
    }
    if ($initialGenerations.Count -eq 0) {
        $reasons.Add('initial_generation_snapshot_missing') | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace($finalGenerationKey)) {
        $reasons.Add('final_generation_missing') | Out-Null
    }
    if ($finalGenerations.Count -eq 0) {
        $reasons.Add('final_generation_snapshot_missing') | Out-Null
    }

    $initialKeys = @($initialGenerations | ForEach-Object { [string]$_.generationKey })
    $finalKeys = @($finalGenerations | ForEach-Object { [string]$_.generationKey })
    if (-not [string]::IsNullOrWhiteSpace($initialGenerationKey) -and
        $initialKeys -notcontains $initialGenerationKey) {
        $reasons.Add('initial_active_generation_not_indexed') | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($finalGenerationKey) -and
        $finalKeys -notcontains $finalGenerationKey) {
        $reasons.Add('final_active_generation_not_indexed') | Out-Null
    }
    if ($initialGenerations.Count -gt 0 -and
        $finalGenerations.Count -gt 0 -and
        -not (Test-GuiSmokeLogLedgerSnapshotMonotonicSubset `
            -InitialGenerations $initialGenerations `
            -FinalGenerations $finalGenerations)) {
        $reasons.Add('generation_snapshot_not_monotonic') | Out-Null
    }

    return @($reasons.ToArray())
}
