Set-StrictMode -Version Latest

if ($null -eq ('NLink.Tools.GuiSmokeLogLedgerStreamingProcessor' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'GuiSmokeLogLedgerStreamingProcessor.cs')
}

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
        Writers = @{}
        UnknownWritersAfterBookmark = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::Ordinal)
        ConsumedLineIdentities = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::Ordinal)
        MaxLines = $MaxLines
        MaxGenerations = $MaxGenerations
        GapCount = 0
        DuplicateIndexCount = 0
        WriterGapCount = 0L
        WriterDuplicateCount = 0L
        WriterIdentityMismatchCount = 0L
        WriterEvidenceBookmark = -1
        RotationCount = 0
        TransientRescanCount = 0
        SyncCount = 0
        InitialGenerationKey = ''
        InitialGenerationSnapshots = @()
        ActiveGenerationKey = ''
        FailureCode = ''
        FailureDetail = ''
        StorageMode = 'RollingMemory'
        AppendOnlyPath = ''
        AppendOnlyMaximumBytes = 0L
        AppendOnlyConsumedByteOffset = 0L
        AppendOnlyLineStartOffsets = New-Object System.Collections.Generic.List[long]
        AppendOnlyGenerationKey = ''
        LastSuccessfulSyncStartedUtc = $null
        LastSuccessfulSyncCompletedUtc = $null
        LastSuccessfulSyncElapsedMilliseconds = 0L
        AppendOnlyBufferReadCount = 0L
        AppendOnlyNewlineSearchCount = 0L
        AppendOnlyIndexedByteCount = 0L
    }
}

function Reset-GuiSmokeLogLedgerState {
    param([Parameter(Mandatory = $true)]$State)

    $State.LedgerId = [guid]::NewGuid().ToString('N')
    $State.Lines.Clear()
    $State.Generations.Clear()
    $State.Writers.Clear()
    $State.UnknownWritersAfterBookmark.Clear()
    $State.ConsumedLineIdentities.Clear()
    $State.GapCount = 0
    $State.DuplicateIndexCount = 0
    $State.WriterGapCount = 0L
    $State.WriterDuplicateCount = 0L
    $State.WriterIdentityMismatchCount = 0L
    $State.WriterEvidenceBookmark = -1
    $State.RotationCount = 0
    $State.TransientRescanCount = 0
    $State.SyncCount = 0
    $State.InitialGenerationKey = ''
    $State.InitialGenerationSnapshots = @()
    $State.ActiveGenerationKey = ''
    $State.FailureCode = ''
    $State.FailureDetail = ''
    $State.StorageMode = 'RollingMemory'
    $State.AppendOnlyPath = ''
    $State.AppendOnlyMaximumBytes = 0L
    $State.AppendOnlyConsumedByteOffset = 0L
    $State.AppendOnlyLineStartOffsets.Clear()
    $State.AppendOnlyGenerationKey = ''
    $State.LastSuccessfulSyncStartedUtc = $null
    $State.LastSuccessfulSyncCompletedUtc = $null
    $State.LastSuccessfulSyncElapsedMilliseconds = 0L
    $State.AppendOnlyBufferReadCount = 0L
    $State.AppendOnlyNewlineSearchCount = 0L
    $State.AppendOnlyIndexedByteCount = 0L
}

function Enable-GuiSmokeAppendOnlyLogLedgerState {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$Path,
        [long]$MaximumBytes = 4L * 1024L * 1024L * 1024L
    )

    if ($MaximumBytes -le 0) {
        throw 'Gui smoke append-only ledger maximum bytes must be positive.'
    }

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if ([IO.File]::Exists($resolvedPath)) {
        throw "gui_smoke_append_only_log_preexisting:$resolvedPath"
    }
    $directory = [IO.Path]::GetDirectoryName($resolvedPath)
    if ([string]::IsNullOrWhiteSpace($directory)) {
        throw "gui_smoke_append_only_log_parent_missing:$resolvedPath"
    }

    [void][IO.Directory]::CreateDirectory($directory)
    $stream = [IO.File]::Open($resolvedPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    $stream.Flush($true)
    $stream.Dispose()

    $generationKey = 'append-only:' + (Get-GuiSmokeLogLedgerHash -Value $resolvedPath.ToLowerInvariant())
    $creationTicks = [IO.File]::GetCreationTimeUtc($resolvedPath).Ticks
    $State.StorageMode = 'AppendOnlyFile'
    $State.AppendOnlyPath = $resolvedPath
    $State.AppendOnlyMaximumBytes = $MaximumBytes
    $State.AppendOnlyConsumedByteOffset = 0L
    $State.AppendOnlyLineStartOffsets.Clear()
    $State.AppendOnlyGenerationKey = $generationKey
    $State.InitialGenerationKey = $generationKey
    $State.ActiveGenerationKey = $generationKey
    $generation = [pscustomobject]@{
        GenerationKey = $generationKey
        CreationTimeUtcTicks = [long]$creationTicks
        ConsumedByteOffset = 0L
        ConsumedLineCount = 0L
        LastPath = $resolvedPath
        FirstLineHash = ''
        IsActivePath = $true
    }
    $State.Generations[$generationKey] = $generation
    $State.InitialGenerationSnapshots = @([pscustomobject]@{
        generationKey = $generationKey
        creationTimeUtcTicks = [long]$creationTicks
        firstLineHash = ''
        lastPath = $resolvedPath
        isActivePath = $true
        consumedByteOffset = 0L
        consumedLineCount = 0L
    })
}

function Get-GuiSmokeLogLedgerLineCount {
    param([Parameter(Mandatory = $true)]$State)

    if ([string]$State.StorageMode -eq 'AppendOnlyFile') {
        return [int]$State.AppendOnlyLineStartOffsets.Count
    }
    return [int]$State.Lines.Count
}

function Set-GuiSmokeLogLedgerWriterEvidenceBookmark {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][int]$Bookmark
    )

    $lineCount = Get-GuiSmokeLogLedgerLineCount -State $State
    if ($Bookmark -lt 0 -or $Bookmark -gt $lineCount) {
        throw ("gui_smoke_log_writer_invalid_bookmark: bookmark={0}; line_count={1}" -f $Bookmark, $lineCount)
    }
    if ([int]$State.WriterEvidenceBookmark -ge 0 -and [int]$State.WriterEvidenceBookmark -ne $Bookmark) {
        throw ("gui_smoke_log_writer_bookmark_changed: initial={0}; replacement={1}" -f $State.WriterEvidenceBookmark, $Bookmark)
    }

    $State.WriterEvidenceBookmark = $Bookmark
}

function Get-GuiSmokeLogLedgerStructuredField {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $match = [regex]::Match(
        $Line,
        ('(?:^|[;\s]){0}=(?<value>[^;\r\n]*)' -f [regex]::Escape($Name)),
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        return ''
    }

    return [string]$match.Groups['value'].Value.Trim()
}

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

function Add-GuiSmokeLogLedgerWriterEvidence {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line
    )

    $writerId = Get-GuiSmokeLogLedgerStructuredField -Line $Line -Name 'log_writer_id'
    $writerSequenceText = Get-GuiSmokeLogLedgerStructuredField -Line $Line -Name 'log_writer_sequence'
    if ([string]::IsNullOrWhiteSpace($writerId) -and [string]::IsNullOrWhiteSpace($writerSequenceText)) {
        if ([int]$State.WriterEvidenceBookmark -ge 0 -and
            (Get-GuiSmokeLogLedgerLineCount -State $State) -ge [int]$State.WriterEvidenceBookmark) {
            $State.WriterIdentityMismatchCount++
        }
        return
    }
    if ([string]::IsNullOrWhiteSpace($writerId) -or [string]::IsNullOrWhiteSpace($writerSequenceText)) {
        $State.WriterIdentityMismatchCount++
        return
    }

    $writerSequence = 0L
    if (-not [long]::TryParse(
            $writerSequenceText,
            [System.Globalization.NumberStyles]::Integer,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$writerSequence) -or
        $writerSequence -le 0) {
        $State.WriterIdentityMismatchCount++
        return
    }

    $eventName = Get-GuiSmokeLogLedgerStructuredField -Line $Line -Name 'event'
    $isStart = [string]::Equals($eventName, 'operational_log_writer_started', [System.StringComparison]::OrdinalIgnoreCase)
    $writer = $State.Writers[$writerId]
    if ($null -eq $writer) {
        if (-not $isStart) {
            if ([int]$State.WriterEvidenceBookmark -ge 0 -and
                (Get-GuiSmokeLogLedgerLineCount -State $State) -ge [int]$State.WriterEvidenceBookmark -and
                $State.UnknownWritersAfterBookmark.Add($writerId)) {
                $State.WriterIdentityMismatchCount++
            }
            return
        }

        $acceptanceRunId = Get-GuiSmokeLogLedgerStructuredField -Line $Line -Name 'acceptance_run_id'
        $scenarioRunId = Get-GuiSmokeLogLedgerStructuredField -Line $Line -Name 'scenario_run_id'
        $expectedAcceptanceRunId = [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
        $expectedScenarioRunId = [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
        if ([string]::IsNullOrWhiteSpace($expectedAcceptanceRunId) -or
            [string]::IsNullOrWhiteSpace($expectedScenarioRunId) -or
            -not [string]::Equals($acceptanceRunId, $expectedAcceptanceRunId, [System.StringComparison]::Ordinal) -or
            -not [string]::Equals($scenarioRunId, $expectedScenarioRunId, [System.StringComparison]::Ordinal)) {
            if ([int]$State.WriterEvidenceBookmark -ge 0 -and
                (Get-GuiSmokeLogLedgerLineCount -State $State) -ge [int]$State.WriterEvidenceBookmark) {
                $State.WriterIdentityMismatchCount++
            }
            return
        }

        $writer = [pscustomobject]@{
            WriterId = $writerId
            AcceptanceRunId = $acceptanceRunId
            ScenarioRunId = $scenarioRunId
            FirstSequence = $writerSequence
            LastSequence = $writerSequence
            ObservedCount = 1L
            StartObserved = $true
        }
        $State.Writers[$writerId] = $writer
        if ($writerSequence -gt 1) {
            $State.WriterGapCount += $writerSequence - 1L
        }
        if ($writerSequence -ne 1L) {
            $State.WriterIdentityMismatchCount++
        }
        return
    }

    if ($writerSequence -eq ([long]$writer.LastSequence + 1L)) {
        $writer.LastSequence = $writerSequence
        $writer.ObservedCount++
    }
    elseif ($writerSequence -gt ([long]$writer.LastSequence + 1L)) {
        $State.WriterGapCount += $writerSequence - [long]$writer.LastSequence - 1L
        $writer.LastSequence = $writerSequence
        $writer.ObservedCount++
    }
    else {
        $State.WriterDuplicateCount++
    }

    if (-not $isStart) {
        return
    }

    $acceptanceRunId = Get-GuiSmokeLogLedgerStructuredField -Line $Line -Name 'acceptance_run_id'
    $scenarioRunId = Get-GuiSmokeLogLedgerStructuredField -Line $Line -Name 'scenario_run_id'
    if ($writerSequence -ne 1L -or
        [string]::IsNullOrWhiteSpace($acceptanceRunId) -or
        [string]::IsNullOrWhiteSpace($scenarioRunId) -or
        [string]::Equals($acceptanceRunId, '(none)', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($scenarioRunId, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
        $State.WriterIdentityMismatchCount++
        return
    }

    if (-not [string]::Equals([string]$writer.AcceptanceRunId, $acceptanceRunId, [System.StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$writer.ScenarioRunId, $scenarioRunId, [System.StringComparison]::Ordinal)) {
        $State.WriterIdentityMismatchCount++
    }
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
        $artifactDirectory = [string]$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR
        if (-not [string]::IsNullOrWhiteSpace($artifactDirectory)) {
            try {
                $failurePath = Join-Path ([IO.Path]::GetFullPath($artifactDirectory)) 'filetransfer-gui-log-cursor-failure.json'
                $temporaryPath = '{0}.{1}.tmp' -f $failurePath, [guid]::NewGuid().ToString('N')
                $failure = [ordered]@{
                    schemaVersion = 1
                    event = 'filetransfer_gui_log_cursor_failure'
                    failureCode = $Code
                    failureDetail = $Detail
                    storageMode = [string]$State.StorageMode
                    sourcePath = [string]$State.AppendOnlyPath
                    activeGenerationKey = [string]$State.ActiveGenerationKey
                    consumedByteOffset = [long]$State.AppendOnlyConsumedByteOffset
                    consumedLineCount = [long](Get-GuiSmokeLogLedgerLineCount -State $State)
                    lastSuccessfulSyncStartedUtc = $State.LastSuccessfulSyncStartedUtc
                    lastSuccessfulSyncCompletedUtc = $State.LastSuccessfulSyncCompletedUtc
                    lastSuccessfulSyncElapsedMilliseconds = [long]$State.LastSuccessfulSyncElapsedMilliseconds
                    syncCount = [int]$State.SyncCount
                    observedUtc = [DateTimeOffset]::UtcNow.ToString('O')
                }
                [IO.File]::WriteAllText($temporaryPath, ($failure | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
                if ([IO.File]::Exists($failurePath)) { [IO.File]::Delete($failurePath) }
                [IO.File]::Move($temporaryPath, $failurePath)
            }
            catch {
                # Cursor failure remains authoritative even when its optional
                # diagnostic record cannot be persisted.
            }
        }
    }
}

function Assert-GuiSmokeLogLedgerHealthy {
    param([Parameter(Mandatory = $true)]$State)

    if (-not [string]::IsNullOrWhiteSpace([string]$State.FailureCode)) {
        throw ("{0}: {1}" -f $State.FailureCode, $State.FailureDetail)
    }
}

function Invoke-GuiSmokeLogLedgerCancellationProbe {
    param([AllowNull()][scriptblock]$CancellationProbe = $null)

    if ($null -ne $CancellationProbe) {
        [void](& $CancellationProbe)
    }
}

function Sync-GuiSmokeAppendOnlyLogLedger {
    param(
        [Parameter(Mandatory = $true)]$State,
        [AllowNull()][scriptblock]$CancellationProbe = $null
    )

    $startedUtc = [DateTimeOffset]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $State.LastSuccessfulSyncStartedUtc = $startedUtc
    $stream = $null
    try {
        Invoke-GuiSmokeLogLedgerCancellationProbe -CancellationProbe $CancellationProbe
        $path = [string]$State.AppendOnlyPath
        if ([string]::IsNullOrWhiteSpace($path) -or -not [IO.File]::Exists($path)) {
            Set-GuiSmokeLogLedgerFailure -State $State -Code 'gui_smoke_log_cursor_gap' -Detail ("path={0}; reason=append_only_source_missing" -f $path)
            Assert-GuiSmokeLogLedgerHealthy -State $State
        }

        $generation = $State.Generations[[string]$State.AppendOnlyGenerationKey]
        $creationTicks = [IO.File]::GetCreationTimeUtc($path).Ticks
        if ($null -eq $generation -or [long]$generation.CreationTimeUtcTicks -ne [long]$creationTicks) {
            Set-GuiSmokeLogLedgerFailure -State $State -Code 'gui_smoke_log_cursor_gap' -Detail ("path={0}; reason=append_only_source_replaced; expected_creation_ticks={1}; actual_creation_ticks={2}" -f $path, $(if ($null -eq $generation) { 0L } else { [long]$generation.CreationTimeUtcTicks }), $creationTicks)
            Assert-GuiSmokeLogLedgerHealthy -State $State
        }

        $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
        $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share, 65536, [IO.FileOptions]::SequentialScan)
        $capturedLength = [long]$stream.Length
        $consumed = [long]$State.AppendOnlyConsumedByteOffset
        if ($capturedLength -lt $consumed) {
            Set-GuiSmokeLogLedgerFailure -State $State -Code 'gui_smoke_log_cursor_truncated' -Detail ("path={0}; consumed_bytes={1}; current_bytes={2}; reason=append_only_source_truncated" -f $path, $consumed, $capturedLength)
            Assert-GuiSmokeLogLedgerHealthy -State $State
        }
        if ($capturedLength -gt [long]$State.AppendOnlyMaximumBytes) {
            Set-GuiSmokeLogLedgerFailure -State $State -Code 'gui_smoke_log_cursor_overflow' -Detail ("path={0}; max_bytes={1}; current_bytes={2}; reason=append_only_source_limit" -f $path, $State.AppendOnlyMaximumBytes, $capturedLength)
            Assert-GuiSmokeLogLedgerHealthy -State $State
        }

        $stream.Position = $consumed
        $buffer = [byte[]]::new(65536)
        $absolutePosition = $consumed
        $lineStart = $consumed
        $committedOffset = $consumed
        $bufferReadCount = 0L
        $newlineSearchCount = 0L
        $indexedByteCount = 0L
        while ($absolutePosition -lt $capturedLength) {
            Invoke-GuiSmokeLogLedgerCancellationProbe -CancellationProbe $CancellationProbe
            $requested = [int][Math]::Min([long]$buffer.Length, $capturedLength - $absolutePosition)
            $read = $stream.Read($buffer, 0, $requested)
            if ($read -le 0) {
                break
            }
            $bufferReadCount++
            $indexedByteCount += [long]$read
            $searchStart = 0
            while ($searchStart -lt $read) {
                $newlineSearchCount++
                $index = [Array]::IndexOf($buffer, [byte]10, $searchStart, $read - $searchStart)
                if ($index -lt 0) { break }
                $State.AppendOnlyLineStartOffsets.Add([long]$lineStart) | Out-Null
                $committedOffset = $absolutePosition + [long]$index + 1L
                $lineStart = $committedOffset
                $searchStart = $index + 1
            }
            $absolutePosition += [long]$read
        }

        $State.AppendOnlyConsumedByteOffset = $committedOffset
        $generation.ConsumedByteOffset = $committedOffset
        $generation.ConsumedLineCount = [long]$State.AppendOnlyLineStartOffsets.Count
        $State.SyncCount++
        $State.LastSuccessfulSyncCompletedUtc = [DateTimeOffset]::UtcNow
        $State.LastSuccessfulSyncElapsedMilliseconds = [long]$stopwatch.ElapsedMilliseconds
        $State.AppendOnlyBufferReadCount += $bufferReadCount
        $State.AppendOnlyNewlineSearchCount += $newlineSearchCount
        $State.AppendOnlyIndexedByteCount += $indexedByteCount
    }
    finally {
        if ($stream) {
            $stream.Dispose()
        }
        $stopwatch.Stop()
    }
}

function Sync-GuiSmokeLogLedger {
    param(
        [Parameter(Mandatory = $true)]$State,
        [AllowEmptyCollection()][string[]]$Paths = @(),
        [AllowNull()][scriptblock]$CancellationProbe = $null
    )

    if ([string]$State.StorageMode -eq 'AppendOnlyFile') {
        Sync-GuiSmokeAppendOnlyLogLedger -State $State -CancellationProbe $CancellationProbe
        return
    }

    Invoke-GuiSmokeLogLedgerCancellationProbe -CancellationProbe $CancellationProbe
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
                Invoke-GuiSmokeLogLedgerCancellationProbe -CancellationProbe $CancellationProbe
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
                    Invoke-GuiSmokeLogLedgerCancellationProbe -CancellationProbe $CancellationProbe
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

            Invoke-GuiSmokeLogLedgerCancellationProbe -CancellationProbe $CancellationProbe
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
            Invoke-GuiSmokeLogLedgerCancellationProbe -CancellationProbe $CancellationProbe
            $generationKey = [string]$snapshot.GenerationKey
            $generation = $State.Generations[$generationKey]
            $consumedByteOffset = if ($null -eq $generation) { 0L } else { [long]$generation.ConsumedByteOffset }
            $delta = Read-GuiSmokeLogLedgerFileDelta -Snapshot $snapshot -ByteOffset $consumedByteOffset
            Invoke-GuiSmokeLogLedgerCancellationProbe -CancellationProbe $CancellationProbe
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
                if (($lineIndex % 256) -eq 0) {
                    Invoke-GuiSmokeLogLedgerCancellationProbe -CancellationProbe $CancellationProbe
                }
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

        # Cancellation is safe through this point because the accepted deltas
        # have only been validated. Once the commit begins, finish it
        # atomically so byte offsets, line identities, and writer evidence
        # cannot diverge.
        Invoke-GuiSmokeLogLedgerCancellationProbe -CancellationProbe $CancellationProbe
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
                Add-GuiSmokeLogLedgerWriterEvidence -State $State -Line ([string]$line)
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
        Invoke-GuiSmokeLogLedgerCancellationProbe -CancellationProbe $CancellationProbe
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
    return Get-GuiSmokeLogLedgerLineCount -State $State
}

function Get-GuiSmokeLogLedgerLines {
    param([Parameter(Mandatory = $true)]$State)

    Assert-GuiSmokeLogLedgerHealthy -State $State
    $lineCount = Get-GuiSmokeLogLedgerLineCount -State $State
    if ([string]$State.StorageMode -eq 'AppendOnlyFile') {
        $lines = New-Object System.Collections.Generic.List[string]
        [void](Invoke-GuiSmokeLogLedgerRangeVisitor -State $State -StartIndex 0 -EndIndex $lineCount -Visitor {
            param([string]$line, [int]$lineIndex)
            $lines.Add($line) | Out-Null
            return $null
        })
        return @($lines.ToArray())
    }
    return @($State.Lines.ToArray())
}

function Get-GuiSmokeLogLedgerLinesAfterBookmark {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][int]$Bookmark
    )

    Assert-GuiSmokeLogLedgerHealthy -State $State
    $lineCount = Get-GuiSmokeLogLedgerLineCount -State $State
    if ($Bookmark -lt 0 -or $Bookmark -gt $lineCount) {
        Set-GuiSmokeLogLedgerFailure `
            -State $State `
            -Code 'gui_smoke_log_cursor_invalid_bookmark' `
            -Detail ("bookmark={0}; line_count={1}" -f $Bookmark, $lineCount)
        Assert-GuiSmokeLogLedgerHealthy -State $State
    }
    if ($Bookmark -eq $lineCount) {
        return @()
    }

    if ([string]$State.StorageMode -eq 'AppendOnlyFile') {
        $lines = New-Object System.Collections.Generic.List[string]
        [void](Invoke-GuiSmokeLogLedgerRangeVisitor -State $State -StartIndex $Bookmark -EndIndex $lineCount -Visitor {
            param([string]$line, [int]$lineIndex)
            $lines.Add($line) | Out-Null
            return $null
        })
        return @($lines.ToArray())
    }

    return @($State.Lines.GetRange($Bookmark, $lineCount - $Bookmark).ToArray())
}

function Invoke-GuiSmokeLogLedgerRangeVisitor {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][int]$StartIndex,
        [Parameter(Mandatory = $true)][int]$EndIndex,
        [Parameter(Mandatory = $true)][scriptblock]$Visitor
    )

    Assert-GuiSmokeLogLedgerHealthy -State $State
    $lineCount = Get-GuiSmokeLogLedgerLineCount -State $State
    if ($StartIndex -lt 0 -or
        $EndIndex -lt $StartIndex -or
        $EndIndex -gt $lineCount) {
        Set-GuiSmokeLogLedgerFailure `
            -State $State `
            -Code 'gui_smoke_log_cursor_invalid_range' `
            -Detail ("start_index={0}; end_index={1}; line_count={2}" -f $StartIndex, $EndIndex, $lineCount)
        Assert-GuiSmokeLogLedgerHealthy -State $State
    }

    if ([string]$State.StorageMode -eq 'AppendOnlyFile') {
        if ($StartIndex -eq $EndIndex) {
            return $null
        }
        $stream = $null
        $reader = $null
        try {
            $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
            $stream = [IO.FileStream]::new([string]$State.AppendOnlyPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share, 65536, [IO.FileOptions]::SequentialScan)
            $stream.Position = [long]$State.AppendOnlyLineStartOffsets[$StartIndex]
            $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $true, 65536, $true)
            for ($lineIndex = $StartIndex; $lineIndex -lt $EndIndex; $lineIndex++) {
                $line = $reader.ReadLine()
                if ($null -eq $line) {
                    Set-GuiSmokeLogLedgerFailure -State $State -Code 'gui_smoke_log_cursor_gap' -Detail ("path={0}; line_index={1}; reason=append_only_range_short_read" -f $State.AppendOnlyPath, $lineIndex)
                    Assert-GuiSmokeLogLedgerHealthy -State $State
                }
                $result = & $Visitor ([string]$line) $lineIndex
                if ($null -ne $result) {
                    return $result
                }
            }
            return $null
        }
        finally {
            if ($reader) { $reader.Dispose() }
            if ($stream) { $stream.Dispose() }
        }
    }

    for ($lineIndex = $StartIndex; $lineIndex -lt $EndIndex; $lineIndex++) {
        $result = & $Visitor ([string]$State.Lines[$lineIndex]) $lineIndex
        if ($null -ne $result) {
            return $result
        }
    }

    return $null
}

function Write-GuiSmokeAppendOnlyLogLedgerRangeCapture {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][int]$StartIndex,
        [Parameter(Mandatory = $true)][int]$EndIndex,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $resolvedDestinationPath = [IO.Path]::GetFullPath($DestinationPath)
    if ([IO.File]::Exists($resolvedDestinationPath)) {
        throw "Gui smoke log ledger capture already exists: $resolvedDestinationPath"
    }
    $destinationDirectory = [IO.Path]::GetDirectoryName($resolvedDestinationPath)
    [void][IO.Directory]::CreateDirectory($destinationDirectory)
    $tempPath = Join-Path $destinationDirectory (".{0}.{1}.tmp" -f [IO.Path]::GetFileName($resolvedDestinationPath), [guid]::NewGuid().ToString('N'))
    $source = $null
    $destination = $null
    $sha256 = $null
    try {
        $startOffset = if ($StartIndex -lt $State.AppendOnlyLineStartOffsets.Count) { [long]$State.AppendOnlyLineStartOffsets[$StartIndex] } else { [long]$State.AppendOnlyConsumedByteOffset }
        $endOffset = if ($EndIndex -lt $State.AppendOnlyLineStartOffsets.Count) { [long]$State.AppendOnlyLineStartOffsets[$EndIndex] } else { [long]$State.AppendOnlyConsumedByteOffset }
        $remaining = $endOffset - $startOffset
        if ($remaining -lt 0) { throw 'gui_smoke_append_only_capture_invalid_offsets' }
        $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
        $source = [IO.FileStream]::new([string]$State.AppendOnlyPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share, 65536, [IO.FileOptions]::SequentialScan)
        $source.Position = $startOffset
        $destination = [IO.FileStream]::new($tempPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 65536, [IO.FileOptions]::SequentialScan)
        $sha256 = [Security.Cryptography.SHA256]::Create()
        $buffer = [byte[]]::new(65536)
        $byteCount = 0L
        while ($remaining -gt 0) {
            $requested = [int][Math]::Min([long]$buffer.Length, $remaining)
            $read = $source.Read($buffer, 0, $requested)
            if ($read -le 0) { throw 'gui_smoke_append_only_capture_short_read' }
            $destination.Write($buffer, 0, $read)
            [void]$sha256.TransformBlock($buffer, 0, $read, $buffer, 0)
            $remaining -= [long]$read
            $byteCount += [long]$read
        }
        [void]$sha256.TransformFinalBlock([byte[]]::new(0), 0, 0)
        $destination.Flush($true)
        $destination.Dispose(); $destination = $null
        $source.Dispose(); $source = $null
        [IO.File]::Move($tempPath, $resolvedDestinationPath)
        $tempPath = ''
        return [pscustomobject]@{
            Path = $resolvedDestinationPath
            Sha256 = (([BitConverter]::ToString($sha256.Hash)) -replace '-', '').ToLowerInvariant()
            LineCount = [long]($EndIndex - $StartIndex)
            ByteCount = $byteCount
        }
    }
    finally {
        if ($source) { $source.Dispose() }
        if ($destination) { $destination.Dispose() }
        if ($sha256) { $sha256.Dispose() }
        if (-not [string]::IsNullOrWhiteSpace($tempPath) -and [IO.File]::Exists($tempPath)) { [IO.File]::Delete($tempPath) }
    }
}

function Write-GuiSmokeLogLedgerRangeCapture {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][int]$StartIndex,
        [Parameter(Mandatory = $true)][int]$EndIndex,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    if ([string]$State.StorageMode -eq 'AppendOnlyFile') {
        return Write-GuiSmokeAppendOnlyLogLedgerRangeCapture -State $State -StartIndex $StartIndex -EndIndex $EndIndex -DestinationPath $DestinationPath
    }

    $resolvedDestinationPath = [IO.Path]::GetFullPath($DestinationPath)
    if ([IO.File]::Exists($resolvedDestinationPath)) {
        throw "Gui smoke log ledger capture already exists: $resolvedDestinationPath"
    }

    $destinationDirectory = [IO.Path]::GetDirectoryName($resolvedDestinationPath)
    if ([string]::IsNullOrWhiteSpace($destinationDirectory)) {
        throw "Gui smoke log ledger capture destination has no parent directory: $resolvedDestinationPath"
    }
    [void][IO.Directory]::CreateDirectory($destinationDirectory)

    $tempPath = Join-Path `
        $destinationDirectory `
        (".{0}.{1}.tmp" -f [IO.Path]::GetFileName($resolvedDestinationPath), [guid]::NewGuid().ToString('N'))
    $stream = $null
    $sha256 = $null
    $captureState = [pscustomobject]@{
        LineCount = 0L
        ByteCount = 0L
    }
    try {
        $stream = [IO.FileStream]::new(
            $tempPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            65536,
            [IO.FileOptions]::SequentialScan)
        $sha256 = [Security.Cryptography.SHA256]::Create()
        $encoding = [Text.UTF8Encoding]::new($false)
        $separatorBytes = $encoding.GetBytes([Environment]::NewLine)

        [void](Invoke-GuiSmokeLogLedgerRangeVisitor `
            -State $State `
            -StartIndex $StartIndex `
            -EndIndex $EndIndex `
            -Visitor {
                param([string]$line, [int]$lineIndex)

                if ($lineIndex -gt $StartIndex) {
                    $stream.Write($separatorBytes, 0, $separatorBytes.Length)
                    [void]$sha256.TransformBlock(
                        $separatorBytes,
                        0,
                        $separatorBytes.Length,
                        $separatorBytes,
                        0)
                    $captureState.ByteCount += [long]$separatorBytes.Length
                }

                $lineBytes = $encoding.GetBytes($line)
                if ($lineBytes.Length -gt 0) {
                    $stream.Write($lineBytes, 0, $lineBytes.Length)
                    [void]$sha256.TransformBlock($lineBytes, 0, $lineBytes.Length, $lineBytes, 0)
                    $captureState.ByteCount += [long]$lineBytes.Length
                }
                [void]($captureState.LineCount++)
                return $null
            })

        [void]$sha256.TransformFinalBlock([byte[]]::new(0), 0, 0)
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null

        [IO.File]::Move($tempPath, $resolvedDestinationPath)
        $tempPath = ''
        return [pscustomobject]@{
            Path = $resolvedDestinationPath
            Sha256 = (([BitConverter]::ToString($sha256.Hash)) -replace '-', '').ToLowerInvariant()
            LineCount = [long]$captureState.LineCount
            ByteCount = [long]$captureState.ByteCount
        }
    }
    finally {
        if ($stream) {
            $stream.Dispose()
        }
        if ($sha256) {
            $sha256.Dispose()
        }
        if (-not [string]::IsNullOrWhiteSpace($tempPath) -and [IO.File]::Exists($tempPath)) {
            [IO.File]::Delete($tempPath)
        }
    }
}

function Copy-GuiSmokeFileAtomicCreateNew {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [switch]$BindSourceLength,
        [switch]$AllowSourceWrite
    )

    $resolvedSourcePath = [IO.Path]::GetFullPath($SourcePath)
    $resolvedDestinationPath = [IO.Path]::GetFullPath($DestinationPath)
    if (-not [IO.File]::Exists($resolvedSourcePath)) {
        throw "Gui smoke source file does not exist: $resolvedSourcePath"
    }
    if ([IO.File]::Exists($resolvedDestinationPath)) {
        throw "Gui smoke destination file already exists: $resolvedDestinationPath"
    }

    $destinationDirectory = [IO.Path]::GetDirectoryName($resolvedDestinationPath)
    if ([string]::IsNullOrWhiteSpace($destinationDirectory)) {
        throw "Gui smoke destination has no parent directory: $resolvedDestinationPath"
    }
    [void][IO.Directory]::CreateDirectory($destinationDirectory)

    $tempPath = Join-Path `
        $destinationDirectory `
        (".{0}.{1}.tmp" -f [IO.Path]::GetFileName($resolvedDestinationPath), [guid]::NewGuid().ToString('N'))
    $sourceStream = $null
    $destinationStream = $null
    $sha256 = $null
    $byteCount = 0L
    $lineFeedCount = 0L
    $lastByte = -1
    try {
        $sourceShare = if ($AllowSourceWrite) {
            [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
        }
        else {
            [IO.FileShare]::Read
        }
        $sourceStream = [IO.FileStream]::new(
            $resolvedSourcePath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            $sourceShare,
            65536,
            [IO.FileOptions]::SequentialScan)
        $capturedLength = if ($BindSourceLength) { [long]$sourceStream.Length } else { -1L }
        $destinationStream = [IO.FileStream]::new(
            $tempPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            65536,
            [IO.FileOptions]::SequentialScan)
        $sha256 = [Security.Cryptography.SHA256]::Create()
        $buffer = [byte[]]::new(65536)
        while ($true) {
            $requested = if ($BindSourceLength) {
                [int][Math]::Min([long]$buffer.Length, $capturedLength - $byteCount)
            }
            else {
                $buffer.Length
            }
            if ($requested -le 0) {
                break
            }
            $read = $sourceStream.Read($buffer, 0, $requested)
            if ($read -le 0) {
                if ($BindSourceLength -and $byteCount -ne $capturedLength) {
                    throw "writer_output_snapshot_short_read:expected=$capturedLength;actual=$byteCount"
                }
                break
            }
            $destinationStream.Write($buffer, 0, $read)
            [void]$sha256.TransformBlock($buffer, 0, $read, $buffer, 0)
            $byteCount += [long]$read
            $lastByte = [int]$buffer[$read - 1]
            $searchIndex = 0
            while ($searchIndex -lt $read) {
                $lineFeedIndex = [Array]::IndexOf(
                    $buffer,
                    [byte]10,
                    $searchIndex,
                    $read - $searchIndex)
                if ($lineFeedIndex -lt 0) {
                    break
                }

                [void]($lineFeedCount++)
                $searchIndex = $lineFeedIndex + 1
            }
        }
        if ($BindSourceLength -and $byteCount -ne $capturedLength) {
            throw "writer_output_snapshot_short_read:expected=$capturedLength;actual=$byteCount"
        }

        [void]$sha256.TransformFinalBlock([byte[]]::new(0), 0, 0)
        $destinationStream.Flush($true)
        $sourceStream.Dispose()
        $sourceStream = $null
        $destinationStream.Dispose()
        $destinationStream = $null

        [IO.File]::Move($tempPath, $resolvedDestinationPath)
        $tempPath = ''
        return [pscustomobject]@{
            Path = $resolvedDestinationPath
            Sha256 = (([BitConverter]::ToString($sha256.Hash)) -replace '-', '').ToLowerInvariant()
            LineCount = $(if ($byteCount -le 0) {
                    0L
                }
                elseif ($lastByte -eq 10) {
                    $lineFeedCount
                }
                else {
                    $lineFeedCount + 1L
                })
            ByteCount = $byteCount
        }
    }
    finally {
        if ($sourceStream) {
            $sourceStream.Dispose()
        }
        if ($destinationStream) {
            $destinationStream.Dispose()
        }
        if ($sha256) {
            $sha256.Dispose()
        }
        if (-not [string]::IsNullOrWhiteSpace($tempPath) -and [IO.File]::Exists($tempPath)) {
            [IO.File]::Delete($tempPath)
        }
    }
}

function Get-GuiSmokeLogLedgerDiagnostics {
    param([Parameter(Mandatory = $true)]$State)

    $lineCount = Get-GuiSmokeLogLedgerLineCount -State $State
    $consumedIdentities = if ([string]$State.StorageMode -eq 'AppendOnlyFile') { @() } else { @($State.ConsumedLineIdentities | Sort-Object) }
    $consumedIndexSha256 = if ([string]$State.StorageMode -eq 'AppendOnlyFile') {
        Get-GuiSmokeLogLedgerHash -Value ("{0}|0|{1}" -f $State.AppendOnlyGenerationKey, $lineCount)
    }
    else {
        Get-GuiSmokeLogLedgerHash -Value ($consumedIdentities -join [char]31)
    }
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
        LineCount = [int]$lineCount
        ConsumedIndexCount = [int]$lineCount
        ConsumedIndexSha256 = $consumedIndexSha256
        ConsumedIndexRanges = $consumedIndexRanges
        GenerationCount = [int]$State.Generations.Count
        InitialGenerationKey = [string]$State.InitialGenerationKey
        ActiveGenerationKey = [string]$State.ActiveGenerationKey
        RotationCount = [int]$State.RotationCount
        TransientRescanCount = [int]$State.TransientRescanCount
        GapCount = [int]$State.GapCount
        DuplicateIndexCount = [int]$State.DuplicateIndexCount
        WriterGapCount = [long]$State.WriterGapCount
        WriterDuplicateCount = [long]$State.WriterDuplicateCount
        WriterIdentityMismatchCount = [long]$State.WriterIdentityMismatchCount
        SyncCount = [int]$State.SyncCount
        StorageMode = [string]$State.StorageMode
        SourcePath = [string]$State.AppendOnlyPath
        LastSuccessfulSyncStartedUtc = $State.LastSuccessfulSyncStartedUtc
        LastSuccessfulSyncCompletedUtc = $State.LastSuccessfulSyncCompletedUtc
        LastSuccessfulSyncElapsedMilliseconds = [long]$State.LastSuccessfulSyncElapsedMilliseconds
        AppendOnlyBufferReadCount = [long]$State.AppendOnlyBufferReadCount
        AppendOnlyNewlineSearchCount = [long]$State.AppendOnlyNewlineSearchCount
        AppendOnlyIndexedByteCount = [long]$State.AppendOnlyIndexedByteCount
        FailureCode = if ([string]::IsNullOrWhiteSpace([string]$State.FailureCode)) { '(none)' } else { [string]$State.FailureCode }
        FailureDetail = if ([string]::IsNullOrWhiteSpace([string]$State.FailureDetail)) { '(none)' } else { [string]$State.FailureDetail }
    }
}

function Get-GuiSmokeLogLedgerWriterSnapshots {
    param([Parameter(Mandatory = $true)]$State)

    return @(
        $State.Writers.Values |
            Sort-Object WriterId |
            ForEach-Object {
                [pscustomobject]@{
                    writerId = [string]$_.WriterId
                    acceptanceRunId = [string]$_.AcceptanceRunId
                    scenarioRunId = [string]$_.ScenarioRunId
                    firstSequence = [long]$_.FirstSequence
                    lastSequence = [long]$_.LastSequence
                    observedCount = [long]$_.ObservedCount
                }
            }
    )
}

function Update-GuiSmokeLogLedgerWriterEvidenceFromFrozenSource {
    param([Parameter(Mandatory = $true)]$State)

    if ([string]$State.StorageMode -ne 'AppendOnlyFile') {
        return
    }

    $bookmark = [int]$State.WriterEvidenceBookmark
    $lineCount = Get-GuiSmokeLogLedgerLineCount -State $State
    $State.Writers.Clear()
    $State.UnknownWritersAfterBookmark.Clear()
    $State.WriterGapCount = 0L
    $State.WriterDuplicateCount = 0L
    $State.WriterIdentityMismatchCount = 0L
    $State.WriterEvidenceBookmark = -1
    if ($bookmark -gt 0) {
        [void](Invoke-GuiSmokeLogLedgerRangeVisitor -State $State -StartIndex 0 -EndIndex $bookmark -Visitor {
            param([string]$line, [int]$lineIndex)
            Add-GuiSmokeLogLedgerWriterEvidence -State $State -Line $line
            return $null
        })
    }
    $State.WriterEvidenceBookmark = $bookmark
    if ($bookmark -lt $lineCount) {
        [void](Invoke-GuiSmokeLogLedgerRangeVisitor -State $State -StartIndex ([Math]::Max(0, $bookmark)) -EndIndex $lineCount -Visitor {
            param([string]$line, [int]$lineIndex)
            Add-GuiSmokeLogLedgerWriterEvidence -State $State -Line $line
            return $null
        })
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

    $lineCount = Get-GuiSmokeLogLedgerLineCount -State $State
    $effectiveBookmark = if ($Bookmark -ge 0) { $Bookmark } else { [int]$lineCount }
    return [pscustomobject]@{
        ledgerId = [string]$State.LedgerId
        bookmark = [int]$effectiveBookmark
        lineCount = [int]$lineCount
        initialGenerationKey = [string]$State.InitialGenerationKey
        activeGenerationKey = [string]$State.ActiveGenerationKey
        generations = @(Get-GuiSmokeLogLedgerGenerationSnapshots -State $State)
        initialGenerations = @($State.InitialGenerationSnapshots)
        writers = @(Get-GuiSmokeLogLedgerWriterSnapshots -State $State)
    }
}

function Test-GuiSmokeLogLedgerWriterSnapshotMonotonicSubset {
    param(
        [AllowEmptyCollection()]$InitialWriters = @(),
        [AllowEmptyCollection()]$FinalWriters = @()
    )

    $finalById = @{}
    foreach ($writer in @($FinalWriters)) {
        $writerId = [string]$writer.writerId
        if (-not [string]::IsNullOrWhiteSpace($writerId)) {
            $finalById[$writerId] = $writer
        }
    }

    foreach ($initial in @($InitialWriters)) {
        $writerId = [string]$initial.writerId
        if ([string]::IsNullOrWhiteSpace($writerId) -or -not $finalById.ContainsKey($writerId)) {
            return $false
        }

        $final = $finalById[$writerId]
        if (-not [string]::Equals([string]$initial.acceptanceRunId, [string]$final.acceptanceRunId, [System.StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$initial.scenarioRunId, [string]$final.scenarioRunId, [System.StringComparison]::Ordinal) -or
            [long]$initial.firstSequence -ne [long]$final.firstSequence -or
            [long]$final.lastSequence -lt [long]$initial.lastSequence -or
            [long]$final.observedCount -lt [long]$initial.observedCount) {
            return $false
        }
    }

    return $true
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

    $initialWriters = @()
    if ($null -ne $InitialSnapshot.PSObject.Properties['writers']) {
        $initialWriters = @($InitialSnapshot.writers)
    }
    $finalWriters = @()
    if ($null -ne $FinalSnapshot.PSObject.Properties['writers']) {
        $finalWriters = @($FinalSnapshot.writers)
    }
    if ($finalWriters.Count -eq 0) {
        $reasons.Add('final_writer_snapshot_missing') | Out-Null
    }
    if ($initialWriters.Count -gt 0 -and
        $finalWriters.Count -gt 0 -and
        -not (Test-GuiSmokeLogLedgerWriterSnapshotMonotonicSubset `
            -InitialWriters $initialWriters `
            -FinalWriters $finalWriters)) {
        $reasons.Add('writer_snapshot_not_monotonic') | Out-Null
    }

    $writerIds = @($finalWriters | ForEach-Object { [string]$_.writerId })
    if (@($writerIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -or
        @($writerIds | Select-Object -Unique).Count -ne $writerIds.Count) {
        $reasons.Add('writer_identity_invalid') | Out-Null
    }
    foreach ($writer in $finalWriters) {
        if ([long]$writer.firstSequence -ne 1L -or
            [long]$writer.lastSequence -lt [long]$writer.firstSequence -or
            [long]$writer.observedCount -ne ([long]$writer.lastSequence - [long]$writer.firstSequence + 1L) -or
            [string]::IsNullOrWhiteSpace([string]$writer.acceptanceRunId) -or
            [string]::IsNullOrWhiteSpace([string]$writer.scenarioRunId)) {
            $reasons.Add('writer_snapshot_invalid') | Out-Null
            break
        }
    }

    return @($reasons.ToArray())
}
