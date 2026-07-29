Set-StrictMode -Version Latest

function Get-RouteAcceptanceEvidencePropertyValue {
    param(
        [AllowNull()]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowNull()]$DefaultValue = $null
    )

    if ($null -eq $Object) {
        return $DefaultValue
    }

    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            return $Object[$Name]
        }
        return $DefaultValue
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $DefaultValue
    }

    return $property.Value
}

function Test-RouteAcceptanceEvidencePropertyPresent {
    param(
        [AllowNull()]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) {
        return $false
    }

    if ($Object -is [System.Collections.IDictionary]) {
        return $Object.Contains($Name)
    }

    return $null -ne $Object.PSObject.Properties[$Name]
}

function ConvertTo-RouteAcceptanceEvidenceScenarioName {
    param([Parameter(Mandatory = $true)][string]$ScenarioName)

    return ($ScenarioName.Trim().ToLowerInvariant() -replace '[^a-z0-9_.-]', '_')
}

function Get-RouteAcceptanceEvidenceScenarioRunId {
    param(
        [Parameter(Mandatory = $true)][string]$AcceptanceRunId,
        [Parameter(Mandatory = $true)][string]$ScenarioName,
        [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$Attempt
    )

    return '{0}:{1}:{2}' -f $AcceptanceRunId, (ConvertTo-RouteAcceptanceEvidenceScenarioName -ScenarioName $ScenarioName), $Attempt
}

function ConvertTo-RouteAcceptanceEvidenceBoolean {
    param(
        [AllowNull()]$Value,
        [bool]$DefaultValue = $false
    )

    if ($null -eq $Value) {
        return $DefaultValue
    }
    if ($Value -is [bool]) {
        return [bool]$Value
    }

    $text = ([string]$Value).Trim()
    if ($text -match '^(1|true|yes|on|pass)$') {
        return $true
    }
    if ($text -match '^(0|false|no|off|fail)$') {
        return $false
    }
    return $DefaultValue
}

function ConvertTo-RouteAcceptanceEvidenceInt64 {
    param(
        [AllowNull()]$Value,
        [long]$DefaultValue = 0
    )

    if ($null -eq $Value) {
        return $DefaultValue
    }

    $parsed = 0L
    if ([long]::TryParse(
            ([string]$Value).Trim(),
            [System.Globalization.NumberStyles]::Integer,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        return $parsed
    }
    return $DefaultValue
}

function Get-RouteAcceptanceEvidenceSha256Bytes {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($Bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-RouteAcceptanceEvidenceTextSha256 {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)

    return Get-RouteAcceptanceEvidenceSha256Bytes -Bytes ([System.Text.Encoding]::UTF8.GetBytes($Value))
}

function Get-RouteAcceptanceEvidenceSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ("Route acceptance evidence file is missing: {0}" -f $Path)
    }

    $stream = [System.IO.File]::Open(
        [System.IO.Path]::GetFullPath($Path),
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Test-RouteAcceptanceEvidenceSha256Value {
    param([AllowNull()]$Value)

    return -not [string]::IsNullOrWhiteSpace([string]$Value) -and ([string]$Value) -match '^[0-9a-fA-F]{64}$'
}

function Test-RouteAcceptanceEvidenceGitCommitValue {
    param([AllowNull()]$Value)

    return -not [string]::IsNullOrWhiteSpace([string]$Value) -and ([string]$Value) -match '^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$'
}

function Get-RouteAcceptanceEvidenceGitState {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $resolvedRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
    $headOutput = @(& git -C $resolvedRoot rev-parse --verify HEAD 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw ("Unable to resolve Git HEAD for route acceptance evidence: {0}" -f ($headOutput -join '; '))
    }

    $statusOutput = @(& git -C $resolvedRoot status --porcelain=v1 --untracked-files=all 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw ("Unable to resolve Git status for route acceptance evidence: {0}" -f ($statusOutput -join '; '))
    }

    $normalizedLines = @(
        $statusOutput |
            ForEach-Object { ([string]$_).TrimEnd() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object
    )
    $normalizedStatus = $normalizedLines -join "`n"

    return [pscustomobject][ordered]@{
        gitCommitSha = ([string]$headOutput[0]).Trim().ToLowerInvariant()
        dirty = $normalizedLines.Count -gt 0
        normalizedStatus = $normalizedStatus
        normalizedStatusSha256 = Get-RouteAcceptanceEvidenceTextSha256 -Value $normalizedStatus
    }
}

function Get-RouteAcceptanceEvidenceRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $root = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd([char[]]@([char]'\', [char]'/'))
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = $root + [System.IO.Path]::DirectorySeparatorChar
    if ($fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
    }
    return $fullPath.Replace('\', '/')
}

function Get-RouteAcceptanceEvidenceFileSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $exists = Test-Path -LiteralPath $fullPath -PathType Leaf
    return [pscustomobject][ordered]@{
        relativePath = Get-RouteAcceptanceEvidenceRelativePath -RepoRoot $RepoRoot -Path $fullPath
        path = $fullPath
        exists = $exists
        sha256 = if ($exists) { Get-RouteAcceptanceEvidenceSha256 -Path $fullPath } else { '' }
    }
}

function Get-RouteAcceptanceEvidenceHarnessAggregateSha256 {
    param([Parameter(Mandatory = $true)][object[]]$Files)

    $lines = @(
        $Files |
            Sort-Object { [string](Get-RouteAcceptanceEvidencePropertyValue -Object $_ -Name 'relativePath' -DefaultValue '') } |
            ForEach-Object {
                $relativePath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $_ -Name 'relativePath' -DefaultValue '')
                $exists = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $_ -Name 'exists' -DefaultValue $false)
                $sha256 = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $_ -Name 'sha256' -DefaultValue '')
                "{0}`0{1}`0{2}" -f $relativePath, ([int]$exists), $sha256.ToLowerInvariant()
            }
    )
    return Get-RouteAcceptanceEvidenceTextSha256 -Value ($lines -join "`n")
}

function New-RouteAcceptanceEvidenceSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$PortableExecutablePath,
        [Parameter(Mandatory = $true)][string[]]$HarnessFiles,
        [Parameter(Mandatory = $true)][string]$BridgeDir,
        [AllowNull()]$GitState = $null
    )

    $resolvedRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
    if ($null -eq $GitState) {
        $GitState = Get-RouteAcceptanceEvidenceGitState -RepoRoot $resolvedRoot
    }

    $runtime = Get-RouteAcceptanceEvidenceFileSnapshot -RepoRoot $resolvedRoot -Path $PortableExecutablePath
    $harnessSnapshots = @(
        $HarnessFiles |
            ForEach-Object { Get-RouteAcceptanceEvidenceFileSnapshot -RepoRoot $resolvedRoot -Path $_ } |
            Sort-Object relativePath
    )

    $resolvedBridgeDir = [System.IO.Path]::GetFullPath($BridgeDir)
    $indexPath = Join-Path $resolvedBridgeDir 'index.js'
    $manifestPath = Join-Path $resolvedBridgeDir 'bridge-manifest.json'
    $bridgeIndex = Get-RouteAcceptanceEvidenceFileSnapshot -RepoRoot $resolvedRoot -Path $indexPath
    $bridgeManifest = Get-RouteAcceptanceEvidenceFileSnapshot -RepoRoot $resolvedRoot -Path $manifestPath
    $declaredIndexSha256 = ''
    if ($bridgeManifest.exists) {
        try {
            $manifestObject = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $declaredIndexSha256 = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $manifestObject -Name 'bridgeScriptSha256' -DefaultValue '')).Trim().ToLowerInvariant()
        }
        catch {
            $declaredIndexSha256 = ''
        }
    }

    return [pscustomobject][ordered]@{
        capturedUtc = (Get-Date).ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
        source = [pscustomobject][ordered]@{
            gitCommitSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $GitState -Name 'gitCommitSha' -DefaultValue '')).Trim().ToLowerInvariant()
            dirty = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $GitState -Name 'dirty' -DefaultValue $true) -DefaultValue $true
            normalizedStatusSha256 = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $GitState -Name 'normalizedStatusSha256' -DefaultValue '')).Trim().ToLowerInvariant()
        }
        runtime = $runtime
        harness = [pscustomobject][ordered]@{
            files = $harnessSnapshots
            aggregateSha256 = Get-RouteAcceptanceEvidenceHarnessAggregateSha256 -Files $harnessSnapshots
        }
        bridge = [pscustomobject][ordered]@{
            bundlePath = $resolvedBridgeDir
            indexPath = $bridgeIndex.path
            manifestPath = $bridgeManifest.path
            indexExists = $bridgeIndex.exists
            manifestExists = $bridgeManifest.exists
            sha256 = $bridgeIndex.sha256
            manifestSha256 = $bridgeManifest.sha256
            declaredIndexSha256 = $declaredIndexSha256
            declaredHashMatches = (Test-RouteAcceptanceEvidenceSha256Value -Value $declaredIndexSha256) -and [string]::Equals($bridgeIndex.sha256, $declaredIndexSha256, [System.StringComparison]::OrdinalIgnoreCase)
        }
    }
}

function Resolve-RouteAcceptanceEvidenceObject {
    param(
        [AllowNull()]$Value,
        [string]$Kind = 'evidence'
    )

    if ($null -eq $Value) {
        return [pscustomobject]@{ Object = $null; Path = ''; Sha256 = ''; Error = ("{0}_missing" -f $Kind) }
    }
    if ($Value -is [string]) {
        $path = [string]$Value
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            return [pscustomobject]@{ Object = $null; Path = $path; Sha256 = ''; Error = ("{0}_missing" -f $Kind) }
        }
        try {
            return [pscustomobject]@{
                Object = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                Path = [System.IO.Path]::GetFullPath($path)
                Sha256 = Get-RouteAcceptanceEvidenceSha256 -Path $path
                Error = ''
            }
        }
        catch {
            return [pscustomobject]@{ Object = $null; Path = $path; Sha256 = ''; Error = ("{0}_malformed" -f $Kind) }
        }
    }
    return [pscustomobject]@{ Object = $Value; Path = ''; Sha256 = ''; Error = '' }
}

function Get-RouteAcceptanceEvidenceGenerationKey {
    param([AllowNull()]$Generation)

    foreach ($name in @('generationKey', 'GenerationKey', 'key', 'Key')) {
        $value = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $Generation -Name $name -DefaultValue '')
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim()
        }
    }
    return ''
}

function Get-RouteAcceptanceEvidenceWriterId {
    param([Parameter(Mandatory = $true)][AllowNull()]$Writer)

    foreach ($name in @('writerId', 'WriterId')) {
        $value = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $Writer -Name $name -DefaultValue '')
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim()
        }
    }
    return ''
}

function Get-RouteAcceptanceEvidenceLineCount {
    param([Parameter(Mandatory = $true)][string]$Path)

    $reader = [System.IO.File]::OpenText([System.IO.Path]::GetFullPath($Path))
    $count = 0L
    try {
        while ($null -ne $reader.ReadLine()) {
            $count++
        }
    }
    finally {
        $reader.Dispose()
    }
    return $count
}

function Get-RouteAcceptanceOperationalLogWriteFailureMarkerCount {
    param([Parameter(Mandatory = $true)][string]$Path)

    $reader = [System.IO.File]::OpenText([System.IO.Path]::GetFullPath($Path))
    $count = 0L
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            if ([string]$line -match '(?i)(?:^|[;\s])event=operational_log_write_failed(?:;|$)') {
                $count++
            }
        }
    }
    finally {
        $reader.Dispose()
    }
    return $count
}

function Get-RouteAcceptanceEvidenceStructuredField {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $match = [regex]::Match(
        $Line,
        ('(?:^|[;\s]){0}=(?<value>[^;\r\n]*)' -f [regex]::Escape($Name)),
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) { return '' }
    return [string]$match.Groups['value'].Value.Trim()
}

function ConvertFrom-RouteAcceptanceEvidenceStructuredFields {
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
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }
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
    if ($duplicateFieldObserved) {
        $fields['__nlink_duplicate_structured_field'] = $true
    }
    if ($duplicateWriterEvidenceFieldObserved) {
        $fields['__nlink_duplicate_writer_evidence_field'] = $true
    }

    return $fields
}

function Get-RouteAcceptanceOperationalLogMarkerCounts {
    param([Parameter(Mandatory = $true)][string]$Path)

    $counts = [ordered]@{ deferred = 0L; recovered = 0L; failed = 0L; invalid = 0L }
    foreach ($line in [System.IO.File]::ReadLines([System.IO.Path]::GetFullPath($Path))) {
        $fields = ConvertFrom-RouteAcceptanceEvidenceStructuredFields -Line ([string]$line)
        $eventName = [string]$fields['event']
        if ($fields.ContainsKey('__nlink_duplicate_structured_field') -and
            [string]$line -match '(?i)(?:^|[;\s])event=operational_log_write_(?:deferred|recovered|failed)(?:;|$)') {
            $counts.invalid++
            continue
        }
        switch ($eventName) {
            'operational_log_write_deferred' { $counts.deferred++; break }
            'operational_log_write_recovered' { $counts.recovered++; break }
            'operational_log_write_failed' { $counts.failed++; break }
        }
    }
    return [pscustomobject]$counts
}

function Get-RouteAcceptanceEvidenceWriterChronology {
    param([Parameter(Mandatory = $true)][string]$Path)

    $writerBatchesById = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $batchSequenceKeys = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $batchOrdinal = 0L
    $invalid = $false
    foreach ($line in [System.IO.File]::ReadLines([System.IO.Path]::GetFullPath($Path))) {
        $fields = ConvertFrom-RouteAcceptanceEvidenceStructuredFields -Line ([string]$line)
        $writerId = [string]$fields['log_writer_id']
        $sequence = ConvertTo-RouteAcceptanceEvidenceInt64 -Value $fields['log_writer_sequence']
        $batchGeneration = ConvertTo-RouteAcceptanceEvidenceInt64 -Value $fields['log_writer_batch_generation']
        $batchFirstSequence = ConvertTo-RouteAcceptanceEvidenceInt64 -Value $fields['log_writer_batch_first_sequence']
        $batchLastSequence = ConvertTo-RouteAcceptanceEvidenceInt64 -Value $fields['log_writer_batch_last_sequence']
        $attemptUtcTicks = ConvertTo-RouteAcceptanceEvidenceInt64 -Value $fields['log_writer_attempt_utc_ticks']
        if ($fields.ContainsKey('__nlink_duplicate_writer_evidence_field') -or
            [string]::IsNullOrWhiteSpace($writerId) -or
            $sequence -le 0 -or
            $batchGeneration -le 0 -or
            $batchFirstSequence -le 0 -or
            $batchLastSequence -lt $batchFirstSequence -or
            $attemptUtcTicks -le 0) {
            $invalid = $true
            continue
        }

        $writerBatches = $null
        if (-not $writerBatchesById.TryGetValue($writerId, [ref]$writerBatches)) {
            $writerBatches = [System.Collections.Generic.Dictionary[long, object]]::new()
            $writerBatchesById.Add($writerId, $writerBatches)
        }

        $batch = $null
        if (-not $writerBatches.TryGetValue($batchGeneration, [ref]$batch)) {
            $batchOrdinal++
            $batch = [pscustomobject]@{
                WriterId = $writerId
                BatchGeneration = $batchGeneration
                BatchFirstSequence = $batchFirstSequence
                BatchLastSequence = $batchLastSequence
                AttemptUtcTicks = $attemptUtcTicks
                FirstObservedOrdinal = $batchOrdinal
                MinimumSequence = $sequence
                MaximumSequence = $sequence
                RecordCount = 0L
                WriterIdMutated = $false
                BatchFirstSequenceMutated = $false
                BatchLastSequenceMutated = $false
                AttemptUtcTicksMutated = $false
                DuplicateSequence = $false
            }
            $writerBatches.Add($batchGeneration, $batch)
        }
        else {
            if (-not [string]::Equals(
                    [string]$batch.WriterId,
                    $writerId,
                    [System.StringComparison]::Ordinal)) {
                $batch.WriterIdMutated = $true
            }
            if ([long]$batch.BatchFirstSequence -ne $batchFirstSequence) {
                $batch.BatchFirstSequenceMutated = $true
            }
            if ([long]$batch.BatchLastSequence -ne $batchLastSequence) {
                $batch.BatchLastSequenceMutated = $true
            }
            if ([long]$batch.AttemptUtcTicks -ne $attemptUtcTicks) {
                $batch.AttemptUtcTicksMutated = $true
            }
            if ($sequence -lt [long]$batch.MinimumSequence) {
                $batch.MinimumSequence = $sequence
            }
            if ($sequence -gt [long]$batch.MaximumSequence) {
                $batch.MaximumSequence = $sequence
            }
        }

        $batch.RecordCount = [long]$batch.RecordCount + 1L
        $batchKey = '{0}:{1}:{2}' -f $writerId.Length, $writerId, $batchGeneration
        $batchSequenceKey = '{0}{1}{2}' -f $batchKey, [char]31, $sequence
        if (-not $batchSequenceKeys.Add($batchSequenceKey)) {
            $batch.DuplicateSequence = $true
        }
    }

    $inversionCount = 0L
    $collisionCount = 0L
    foreach ($writerEntry in $writerBatchesById.GetEnumerator()) {
        $validBatches = New-Object System.Collections.Generic.List[object]
        foreach ($batch in $writerEntry.Value.Values) {
            $batchValid = -not [bool]$batch.WriterIdMutated -and
                -not [bool]$batch.BatchFirstSequenceMutated -and
                -not [bool]$batch.BatchLastSequenceMutated -and
                -not [bool]$batch.AttemptUtcTicksMutated -and
                -not [bool]$batch.DuplicateSequence -and
                [long]$batch.AttemptUtcTicks -gt 0 -and
                [long]$batch.MinimumSequence -eq [long]$batch.BatchFirstSequence -and
                [long]$batch.MaximumSequence -eq [long]$batch.BatchLastSequence -and
                [long]$batch.RecordCount -eq ([long]$batch.BatchLastSequence - [long]$batch.BatchFirstSequence + 1L)
            if (-not $batchValid) {
                $invalid = $true
                continue
            }

            $validBatches.Add([pscustomobject]@{
                    firstSequence = [long]$batch.BatchFirstSequence
                    attemptUtcTicks = [long]$batch.AttemptUtcTicks
                    firstObservedOrdinal = [long]$batch.FirstObservedOrdinal
                }) | Out-Null
        }

        $orderedBatches = @($validBatches.ToArray() | Sort-Object firstSequence, firstObservedOrdinal)
        for ($index = 1; $index -lt $orderedBatches.Count; $index++) {
            $previousTicks = [long]$orderedBatches[$index - 1].attemptUtcTicks
            $currentTicks = [long]$orderedBatches[$index].attemptUtcTicks
            if ($currentTicks -lt $previousTicks) {
                $inversionCount++
            }
            elseif ($currentTicks -eq $previousTicks) {
                $collisionCount++
            }
        }
    }

    return [pscustomobject][ordered]@{
        inversionCount = $inversionCount
        collisionCount = $collisionCount
        batchEvidenceValid = -not $invalid
    }
}

function Get-RouteAcceptanceCursorEvidence {
    param(
        [Parameter(Mandatory = $true)][AllowNull()]$LogLedger,
        [Parameter(Mandatory = $true)][AllowNull()]$AnalyzerDiagnostics,
        [Parameter(Mandatory = $true)][string]$AcceptanceRunId,
        [Parameter(Mandatory = $true)][string]$ScenarioRunId
    )

    $reasons = New-Object System.Collections.Generic.List[string]
    $ledgerResult = Resolve-RouteAcceptanceEvidenceObject -Value $LogLedger -Kind 'cursor_ledger'
    $diagnosticsResult = Resolve-RouteAcceptanceEvidenceObject -Value $AnalyzerDiagnostics -Kind 'analyzer_diagnostics'
    if (-not [string]::IsNullOrWhiteSpace($ledgerResult.Error)) {
        $reasons.Add($ledgerResult.Error) | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($diagnosticsResult.Error)) {
        $reasons.Add($diagnosticsResult.Error) | Out-Null
    }

    $ledger = $ledgerResult.Object
    $diagnostics = $diagnosticsResult.Object
    if ((ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'schemaVersion' -DefaultValue 0)) -ne 1) {
        $reasons.Add('cursor_ledger_schema_invalid') | Out-Null
    }
    if ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'event' -DefaultValue '') -ne 'filetransfer_gui_log_ledger') {
        $reasons.Add('cursor_ledger_event_invalid') | Out-Null
    }
    $reportedCursorVerdict = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'cursorIntegrityVerdict' -DefaultValue '')
    $reportedCursorReasons = @(Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'cursorIntegrityReasons' -DefaultValue @())
    if ([string]::IsNullOrWhiteSpace($reportedCursorVerdict)) {
        $reasons.Add('cursor_reported_integrity_verdict_missing') | Out-Null
    }
    elseif (-not [string]::Equals($reportedCursorVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($reportedCursorReasons.Count -eq 0) {
            $reasons.Add('cursor_reported_integrity_failed') | Out-Null
        }
        else {
            foreach ($reportedCursorReason in $reportedCursorReasons) {
                $reasons.Add(('cursor_reported:{0}' -f [string]$reportedCursorReason)) | Out-Null
            }
        }
    }
    $reportedAcceptanceRunId = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'acceptanceRunId' -DefaultValue '')
    $reportedScenarioRunId = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'scenarioRunId' -DefaultValue '')
    if ([string]::IsNullOrWhiteSpace($reportedAcceptanceRunId) -or
        -not [string]::Equals($reportedAcceptanceRunId, $AcceptanceRunId, [System.StringComparison]::Ordinal)) {
        $reasons.Add('cursor_acceptance_run_id_mismatch') | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace($reportedScenarioRunId) -or
        -not [string]::Equals($reportedScenarioRunId, $ScenarioRunId, [System.StringComparison]::Ordinal)) {
        $reasons.Add('cursor_scenario_run_id_mismatch') | Out-Null
    }
    $ledgerId = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'ledgerId' -DefaultValue '')
    if ([string]::IsNullOrWhiteSpace($ledgerId)) {
        $reasons.Add('cursor_ledger_id_missing') | Out-Null
    }

    $initial = Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'initial' -DefaultValue $null
    $final = Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'final' -DefaultValue $null
    if ($null -eq $initial) {
        $reasons.Add('cursor_initial_snapshot_missing') | Out-Null
    }
    if ($null -eq $final) {
        $reasons.Add('cursor_final_snapshot_missing') | Out-Null
    }
    foreach ($snapshotRequirement in @(
            [pscustomobject]@{ Snapshot = $initial; Prefix = 'initial' },
            [pscustomobject]@{ Snapshot = $final; Prefix = 'final' })) {
        foreach ($fieldName in @('bookmark', 'lineCount', 'activeGenerationKey', 'generations', 'writers')) {
            if (-not (Test-RouteAcceptanceEvidencePropertyPresent -Object $snapshotRequirement.Snapshot -Name $fieldName)) {
                $reasons.Add(('cursor_{0}_{1}_missing' -f $snapshotRequirement.Prefix, $fieldName.ToLowerInvariant())) | Out-Null
            }
        }
    }


    $writerEvidenceVersion = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerEvidenceVersion' -DefaultValue 0)
    if ($writerEvidenceVersion -ne 3) {
        $reasons.Add('cursor_writer_evidence_version_invalid') | Out-Null
    }
    $reportedWriterVerdict = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerIntegrityVerdict' -DefaultValue '')
    $reportedWriterReasons = @(Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerIntegrityReasons' -DefaultValue @())
    if ([string]::IsNullOrWhiteSpace($reportedWriterVerdict)) {
        $reasons.Add('cursor_writer_integrity_verdict_missing') | Out-Null
    }
    elseif (-not [string]::Equals($reportedWriterVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($reportedWriterReasons.Count -eq 0) {
            $reasons.Add('cursor_writer_integrity_failed') | Out-Null
        }
        else {
            foreach ($writerReason in $reportedWriterReasons) {
                $reasons.Add(('cursor_writer_reported:{0}' -f [string]$writerReason)) | Out-Null
            }
        }
    }

    $initialWriters = @(Get-RouteAcceptanceEvidencePropertyValue -Object $initial -Name 'writers' -DefaultValue @())
    $finalWriters = @(Get-RouteAcceptanceEvidencePropertyValue -Object $final -Name 'writers' -DefaultValue @())
    if ($finalWriters.Count -eq 0) {
        $reasons.Add('cursor_final_writer_snapshot_missing') | Out-Null
    }
    $initialWriterIds = @($initialWriters | ForEach-Object { Get-RouteAcceptanceEvidenceWriterId -Writer $_ })
    $finalWriterIds = @($finalWriters | ForEach-Object { Get-RouteAcceptanceEvidenceWriterId -Writer $_ })
    if (@($initialWriterIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -or
        @($finalWriterIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        $reasons.Add('cursor_writer_id_missing') | Out-Null
    }
    if (@($initialWriterIds | Select-Object -Unique).Count -ne $initialWriterIds.Count -or
        @($finalWriterIds | Select-Object -Unique).Count -ne $finalWriterIds.Count) {
        $reasons.Add('cursor_writer_id_duplicate') | Out-Null
    }
    if (($initialWriterIds -join "`n") -ne (@($initialWriterIds | Sort-Object) -join "`n") -or
        ($finalWriterIds -join "`n") -ne (@($finalWriterIds | Sort-Object) -join "`n")) {
        $reasons.Add('cursor_writer_order_invalid') | Out-Null
    }

    $finalWriterById = @{}
    foreach ($writer in $finalWriters) {
        $writerId = Get-RouteAcceptanceEvidenceWriterId -Writer $writer
        if ([string]::IsNullOrWhiteSpace($writerId)) {
            continue
        }
        $finalWriterById[$writerId] = $writer
        $writerAcceptanceRunId = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $writer -Name 'acceptanceRunId' -DefaultValue '')
        $writerScenarioRunId = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $writer -Name 'scenarioRunId' -DefaultValue '')
        if (-not [string]::Equals($writerAcceptanceRunId, $AcceptanceRunId, [System.StringComparison]::Ordinal)) {
            $reasons.Add('cursor_writer_acceptance_run_id_mismatch') | Out-Null
        }
        if (-not [string]::Equals($writerScenarioRunId, $ScenarioRunId, [System.StringComparison]::Ordinal)) {
            $reasons.Add('cursor_writer_scenario_run_id_mismatch') | Out-Null
        }
        $firstSequence = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $writer -Name 'firstSequence' -DefaultValue 0)
        $lastSequence = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $writer -Name 'lastSequence' -DefaultValue 0)
        $observedCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $writer -Name 'observedCount' -DefaultValue 0)
        if ($firstSequence -ne 1 -or $lastSequence -lt $firstSequence -or $observedCount -ne ($lastSequence - $firstSequence + 1)) {
            $reasons.Add('cursor_writer_snapshot_invalid') | Out-Null
        }
    }
    foreach ($initialWriter in $initialWriters) {
        $writerId = Get-RouteAcceptanceEvidenceWriterId -Writer $initialWriter
        if ([string]::IsNullOrWhiteSpace($writerId) -or -not $finalWriterById.ContainsKey($writerId)) {
            $reasons.Add('cursor_writer_snapshot_not_monotonic') | Out-Null
            continue
        }
        $finalWriter = $finalWriterById[$writerId]
        if (-not [string]::Equals(
                [string](Get-RouteAcceptanceEvidencePropertyValue -Object $initialWriter -Name 'acceptanceRunId' -DefaultValue ''),
                [string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalWriter -Name 'acceptanceRunId' -DefaultValue ''),
                [System.StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [string](Get-RouteAcceptanceEvidencePropertyValue -Object $initialWriter -Name 'scenarioRunId' -DefaultValue ''),
                [string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalWriter -Name 'scenarioRunId' -DefaultValue ''),
                [System.StringComparison]::Ordinal) -or
            (ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $initialWriter -Name 'firstSequence' -DefaultValue 0)) -ne
                (ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $finalWriter -Name 'firstSequence' -DefaultValue 0)) -or
            (ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $finalWriter -Name 'lastSequence' -DefaultValue 0)) -lt
                (ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $initialWriter -Name 'lastSequence' -DefaultValue 0)) -or
            (ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $finalWriter -Name 'observedCount' -DefaultValue 0)) -lt
                (ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $initialWriter -Name 'observedCount' -DefaultValue 0))) {
            $reasons.Add('cursor_writer_snapshot_not_monotonic') | Out-Null
        }
    }

    $initialLedgerId = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $initial -Name 'ledgerId' -DefaultValue $ledgerId)
    $finalLedgerId = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $final -Name 'ledgerId' -DefaultValue $ledgerId)
    if (-not [string]::IsNullOrWhiteSpace($ledgerId) -and
        (-not [string]::Equals($ledgerId, $initialLedgerId, [System.StringComparison]::Ordinal) -or
         -not [string]::Equals($ledgerId, $finalLedgerId, [System.StringComparison]::Ordinal))) {
        $reasons.Add('cursor_ledger_id_mismatch') | Out-Null
    }

    $initialGenerationKey = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $initial -Name 'activeGenerationKey' -DefaultValue '')
    $finalGenerationKey = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $final -Name 'activeGenerationKey' -DefaultValue '')
    if ([string]::IsNullOrWhiteSpace($initialGenerationKey)) {
        $reasons.Add('cursor_initial_generation_missing') | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace($finalGenerationKey)) {
        $reasons.Add('cursor_final_generation_missing') | Out-Null
    }

    $initialGenerations = @(Get-RouteAcceptanceEvidencePropertyValue -Object $initial -Name 'generations' -DefaultValue @())
    $finalGenerations = @(Get-RouteAcceptanceEvidencePropertyValue -Object $final -Name 'generations' -DefaultValue @())
    $initialKeys = @($initialGenerations | ForEach-Object { Get-RouteAcceptanceEvidenceGenerationKey -Generation $_ })
    $finalKeys = @($finalGenerations | ForEach-Object { Get-RouteAcceptanceEvidenceGenerationKey -Generation $_ })
    if (@($initialKeys | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -or
        @($finalKeys | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        $reasons.Add('cursor_generation_key_missing') | Out-Null
    }
    if (@($initialKeys | Select-Object -Unique).Count -ne $initialKeys.Count -or
        @($finalKeys | Select-Object -Unique).Count -ne $finalKeys.Count) {
        $reasons.Add('cursor_duplicate_generation') | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($initialGenerationKey) -and $initialKeys -notcontains $initialGenerationKey) {
        $reasons.Add('cursor_initial_generation_not_indexed') | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($finalGenerationKey) -and $finalKeys -notcontains $finalGenerationKey) {
        $reasons.Add('cursor_final_generation_not_indexed') | Out-Null
    }
    if (($initialKeys -join "`n") -ne (@($initialKeys | Sort-Object) -join "`n") -or
        ($finalKeys -join "`n") -ne (@($finalKeys | Sort-Object) -join "`n")) {
        $reasons.Add('cursor_generation_order_invalid') | Out-Null
    }
    foreach ($initialKey in $initialKeys) {
        if (-not [string]::IsNullOrWhiteSpace($initialKey) -and $finalKeys -notcontains $initialKey) {
            $reasons.Add('cursor_generation_not_monotonic') | Out-Null
            break
        }

        $initialGeneration = @($initialGenerations | Where-Object { (Get-RouteAcceptanceEvidenceGenerationKey -Generation $_) -eq $initialKey } | Select-Object -First 1)
        $finalGeneration = @($finalGenerations | Where-Object { (Get-RouteAcceptanceEvidenceGenerationKey -Generation $_) -eq $initialKey } | Select-Object -First 1)
        if ($initialGeneration.Count -gt 0 -and $finalGeneration.Count -gt 0) {
            foreach ($generation in @($initialGeneration[0], $finalGeneration[0])) {
                if (-not (Test-RouteAcceptanceEvidencePropertyPresent -Object $generation -Name 'consumedByteOffset') -or
                    -not (Test-RouteAcceptanceEvidencePropertyPresent -Object $generation -Name 'consumedLineCount')) {
                    $reasons.Add('cursor_generation_consumed_fields_missing') | Out-Null
                    break
                }
            }
            $initialByteOffset = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $initialGeneration[0] -Name 'consumedByteOffset' -DefaultValue 0)
            $finalByteOffset = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $finalGeneration[0] -Name 'consumedByteOffset' -DefaultValue 0)
            $initialConsumedLines = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $initialGeneration[0] -Name 'consumedLineCount' -DefaultValue 0)
            $finalConsumedLines = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $finalGeneration[0] -Name 'consumedLineCount' -DefaultValue 0)
            if ($initialByteOffset -lt 0 -or $finalByteOffset -lt 0 -or
                $initialConsumedLines -lt 0 -or $finalConsumedLines -lt 0 -or
                $finalByteOffset -lt $initialByteOffset -or $finalConsumedLines -lt $initialConsumedLines) {
                $reasons.Add('cursor_generation_not_monotonic') | Out-Null
                break
            }
        }
    }

    foreach ($finalGeneration in $finalGenerations) {
        if (-not (Test-RouteAcceptanceEvidencePropertyPresent -Object $finalGeneration -Name 'consumedByteOffset') -or
            -not (Test-RouteAcceptanceEvidencePropertyPresent -Object $finalGeneration -Name 'consumedLineCount')) {
            $reasons.Add('cursor_generation_consumed_fields_missing') | Out-Null
            break
        }
        if ((ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $finalGeneration -Name 'consumedByteOffset' -DefaultValue -1) -DefaultValue -1) -lt 0 -or
            (ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $finalGeneration -Name 'consumedLineCount' -DefaultValue -1) -DefaultValue -1) -lt 0) {
            $reasons.Add('cursor_generation_consumed_fields_invalid') | Out-Null
            break
        }
    }

    foreach ($requiredLedgerField in @(
            'rotationCount',
            'transientRescanCount',
            'gapCount',
            'duplicateIndexCount',
            'consumedIndexCount',
            'consumedIndexSha256',
            'consumedIndexRanges',
            'syncCount',
            'failureCode',
            'failureDetail',
            'writerEvidenceVersion',
            'writerGapCount',
            'writerDuplicateCount',
            'writerIdentityMismatchCount',
            'writerWriteFailureCount',
            'writerDeferredBatchCount',
            'writerDeferredLineCount',
            'writerRecoveredBatchCount',
            'writerRecoveredLineCount',
            'writerUnresolvedDeferredBatchCount',
            'writerUnresolvedDeferredLineCount',
            'writerTerminalFailureCount',
            'writerRecoveryMismatchCount',
            'writerChronologyInversionCount',
            'writerChronologyCollisionCount',
            'writerDrainMissingCount',
            'writerRecoveryVerdict',
            'writerChronologyVerdict',
            'writerIntegrityVerdict',
            'writerIntegrityReasons',
            'writerFailureMarkerFiles',
            'writerMarkerFiles',
            'writerDeferredBatches',
            'writerDrains',
            'writerEvidenceSlice')) {
        if (-not (Test-RouteAcceptanceEvidencePropertyPresent -Object $ledger -Name $requiredLedgerField)) {
            $missingFieldReason = switch ($requiredLedgerField) {
                'gapCount' { 'cursor_gap_count_missing'; break }
                'duplicateIndexCount' { 'cursor_duplicate_index_count_missing'; break }
                'consumedIndexCount' { 'cursor_consumed_index_count_missing'; break }
                'consumedIndexSha256' { 'cursor_consumed_index_sha256_missing'; break }
                'consumedIndexRanges' { 'cursor_consumed_index_ranges_missing'; break }
                'rotationCount' { 'cursor_rotation_count_missing'; break }
                'transientRescanCount' { 'cursor_transient_rescan_count_missing'; break }
                'syncCount' { 'cursor_sync_count_missing'; break }
                'failureCode' { 'cursor_failure_code_missing'; break }
                'failureDetail' { 'cursor_failure_detail_missing'; break }
                'writerEvidenceVersion' { 'cursor_writer_evidence_version_missing'; break }
                'writerGapCount' { 'cursor_writer_gap_count_missing'; break }
                'writerDuplicateCount' { 'cursor_writer_duplicate_count_missing'; break }
                'writerIdentityMismatchCount' { 'cursor_writer_identity_mismatch_count_missing'; break }
                'writerWriteFailureCount' { 'cursor_writer_write_failure_count_missing'; break }
                'writerIntegrityVerdict' { 'cursor_writer_integrity_verdict_missing'; break }
                'writerIntegrityReasons' { 'cursor_writer_integrity_reasons_missing'; break }
                'writerFailureMarkerFiles' { 'cursor_writer_failure_marker_files_missing'; break }
                default { 'cursor_ledger_field_missing' }
            }
            $reasons.Add($missingFieldReason) | Out-Null
        }
    }
    $gapCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'gapCount' -DefaultValue -1) -DefaultValue -1
    $duplicateIndexCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'duplicateIndexCount' -DefaultValue -1) -DefaultValue -1
    if ($gapCount -ne 0) {
        $reasons.Add('cursor_rotation_gap') | Out-Null
    }
    if ($duplicateIndexCount -ne 0) {
        $reasons.Add('cursor_duplicate_index') | Out-Null
    }
    $writerGapCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerGapCount' -DefaultValue -1) -DefaultValue -1
    $writerDuplicateCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerDuplicateCount' -DefaultValue -1) -DefaultValue -1
    $writerIdentityMismatchCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerIdentityMismatchCount' -DefaultValue -1) -DefaultValue -1
    $writerWriteFailureCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerWriteFailureCount' -DefaultValue -1) -DefaultValue -1
    $writerDeferredBatchCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerDeferredBatchCount' -DefaultValue -1) -DefaultValue -1
    $writerDeferredLineCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerDeferredLineCount' -DefaultValue -1) -DefaultValue -1
    $writerRecoveredBatchCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerRecoveredBatchCount' -DefaultValue -1) -DefaultValue -1
    $writerRecoveredLineCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerRecoveredLineCount' -DefaultValue -1) -DefaultValue -1
    $writerUnresolvedDeferredBatchCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerUnresolvedDeferredBatchCount' -DefaultValue -1) -DefaultValue -1
    $writerUnresolvedDeferredLineCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerUnresolvedDeferredLineCount' -DefaultValue -1) -DefaultValue -1
    $writerTerminalFailureCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerTerminalFailureCount' -DefaultValue -1) -DefaultValue -1
    $writerRecoveryMismatchCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerRecoveryMismatchCount' -DefaultValue -1) -DefaultValue -1
    $writerChronologyInversionCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerChronologyInversionCount' -DefaultValue -1) -DefaultValue -1
    $writerChronologyCollisionCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerChronologyCollisionCount' -DefaultValue -1) -DefaultValue -1
    $writerDrainMissingCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerDrainMissingCount' -DefaultValue -1) -DefaultValue -1
    if ($writerGapCount -ne 0) {
        $reasons.Add('cursor_writer_gap') | Out-Null
    }
    if ($writerDuplicateCount -ne 0) {
        $reasons.Add('cursor_writer_duplicate') | Out-Null
    }
    if ($writerIdentityMismatchCount -ne 0) {
        $reasons.Add('cursor_writer_identity_mismatch') | Out-Null
    }
    if ($writerWriteFailureCount -ne 0) {
        $reasons.Add('cursor_writer_write_failure') | Out-Null
    }
    if ($writerDeferredBatchCount -lt 0 -or $writerDeferredLineCount -lt 0 -or
        $writerRecoveredBatchCount -lt 0 -or $writerRecoveredLineCount -lt 0) {
        $reasons.Add('cursor_writer_recovery_counts_invalid') | Out-Null
    }
    if ($writerDeferredBatchCount -ne $writerRecoveredBatchCount -or
        $writerDeferredLineCount -ne $writerRecoveredLineCount -or
        $writerUnresolvedDeferredBatchCount -ne 0 -or
        $writerUnresolvedDeferredLineCount -ne 0) {
        $reasons.Add('cursor_writer_deferred_unresolved') | Out-Null
    }
    if ($writerTerminalFailureCount -ne 0) { $reasons.Add('cursor_writer_terminal_failure') | Out-Null }
    if ($writerRecoveryMismatchCount -ne 0) { $reasons.Add('cursor_writer_recovery_mismatch') | Out-Null }
    if ($writerChronologyInversionCount -ne 0) { $reasons.Add('cursor_writer_chronology_inversion') | Out-Null }
    if ($writerChronologyCollisionCount -ne 0) { $reasons.Add('cursor_writer_chronology_collision') | Out-Null }
    if ($writerDrainMissingCount -ne 0) { $reasons.Add('cursor_writer_drain_missing') | Out-Null }
    if (-not [string]::Equals([string](Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerRecoveryVerdict' -DefaultValue ''), 'pass', [System.StringComparison]::OrdinalIgnoreCase)) {
        $reasons.Add('cursor_writer_recovery_verdict_failed') | Out-Null
    }
    if (-not [string]::Equals([string](Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerChronologyVerdict' -DefaultValue ''), 'pass', [System.StringComparison]::OrdinalIgnoreCase)) {
        $reasons.Add('cursor_writer_chronology_verdict_failed') | Out-Null
    }

    $writerFailureMarkerFiles = @(Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerFailureMarkerFiles' -DefaultValue @())
    if ($writerFailureMarkerFiles.Count -eq 0) {
        $reasons.Add('cursor_writer_failure_marker_files_missing') | Out-Null
    }
    $markerCountTotal = 0L
    foreach ($markerFile in $writerFailureMarkerFiles) {
        $markerPath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $markerFile -Name 'path' -DefaultValue '')
        if (-not [string]::IsNullOrWhiteSpace($markerPath) -and -not [System.IO.Path]::IsPathRooted($markerPath) -and -not [string]::IsNullOrWhiteSpace($ledgerResult.Path)) {
            $markerPath = Join-Path ([System.IO.Path]::GetDirectoryName($ledgerResult.Path)) $markerPath
        }
        $markerSha256 = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $markerFile -Name 'sha256' -DefaultValue '')).Trim().ToLowerInvariant()
        $markerCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $markerFile -Name 'markerCount' -DefaultValue -1) -DefaultValue -1
        if ([string]::IsNullOrWhiteSpace($markerPath) -or -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            $reasons.Add('cursor_writer_failure_marker_file_missing') | Out-Null
        }
        else {
            if (-not (Test-RouteAcceptanceEvidenceSha256Value -Value $markerSha256) -or
                -not [string]::Equals((Get-RouteAcceptanceEvidenceSha256 -Path $markerPath), $markerSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                $reasons.Add('cursor_writer_failure_marker_sha256_mismatch') | Out-Null
            }
            if ($markerCount -lt 0 -or (Get-RouteAcceptanceOperationalLogWriteFailureMarkerCount -Path $markerPath) -ne $markerCount) {
                $reasons.Add('cursor_writer_failure_marker_count_mismatch') | Out-Null
            }
        }
        if ($markerCount -ge 0) {
            $markerCountTotal += $markerCount
        }
    }
    if ($writerWriteFailureCount -lt 0 -or $markerCountTotal -ne $writerWriteFailureCount) {
        $reasons.Add('cursor_writer_write_failure_count_mismatch') | Out-Null
    }

    $writerMarkerFiles = @(Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerMarkerFiles' -DefaultValue @())
    $markerDeferredTotal = 0L
    $markerRecoveredTotal = 0L
    $markerTerminalTotal = 0L
    foreach ($markerFile in $writerMarkerFiles) {
        $markerPath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $markerFile -Name 'path' -DefaultValue '')
        if (-not [string]::IsNullOrWhiteSpace($markerPath) -and -not [System.IO.Path]::IsPathRooted($markerPath) -and -not [string]::IsNullOrWhiteSpace($ledgerResult.Path)) {
            $markerPath = Join-Path ([System.IO.Path]::GetDirectoryName($ledgerResult.Path)) $markerPath
        }
        if ([string]::IsNullOrWhiteSpace($markerPath) -or -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            $reasons.Add('cursor_writer_marker_file_missing') | Out-Null
            continue
        }
        $markerSha256 = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $markerFile -Name 'sha256' -DefaultValue '')
        if (-not (Test-RouteAcceptanceEvidenceSha256Value -Value $markerSha256) -or
            -not [string]::Equals((Get-RouteAcceptanceEvidenceSha256 -Path $markerPath), $markerSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            $reasons.Add('cursor_writer_marker_sha256_mismatch') | Out-Null
        }
        $actualMarkerCounts = Get-RouteAcceptanceOperationalLogMarkerCounts -Path $markerPath
        if ([long]$actualMarkerCounts.invalid -ne 0) {
            $reasons.Add('cursor_writer_marker_structured_field_duplicate') | Out-Null
        }
        foreach ($markerKind in @(
                [pscustomobject]@{ Name = 'deferredMarkerCount'; Actual = [long]$actualMarkerCounts.deferred },
                [pscustomobject]@{ Name = 'recoveredMarkerCount'; Actual = [long]$actualMarkerCounts.recovered },
                [pscustomobject]@{ Name = 'terminalFailureMarkerCount'; Actual = [long]$actualMarkerCounts.failed })) {
            $reportedCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $markerFile -Name $markerKind.Name -DefaultValue -1) -DefaultValue -1
            if ($reportedCount -ne $markerKind.Actual) {
                $reasons.Add('cursor_writer_marker_count_mismatch') | Out-Null
            }
        }
        $markerDeferredTotal += [long]$actualMarkerCounts.deferred
        $markerRecoveredTotal += [long]$actualMarkerCounts.recovered
        $markerTerminalTotal += [long]$actualMarkerCounts.failed
    }
    if ($markerDeferredTotal -ne $writerDeferredBatchCount -or
        $markerRecoveredTotal -ne $writerRecoveredBatchCount -or
        $markerTerminalTotal -ne $writerTerminalFailureCount) {
        $reasons.Add('cursor_writer_marker_total_mismatch') | Out-Null
    }

    $writerEvidenceSlice = Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerEvidenceSlice' -DefaultValue $null
    $writerEvidenceSlicePath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $writerEvidenceSlice -Name 'path' -DefaultValue '')
    if (-not [string]::IsNullOrWhiteSpace($writerEvidenceSlicePath) -and -not [System.IO.Path]::IsPathRooted($writerEvidenceSlicePath) -and -not [string]::IsNullOrWhiteSpace($ledgerResult.Path)) {
        $writerEvidenceSlicePath = Join-Path ([System.IO.Path]::GetDirectoryName($ledgerResult.Path)) $writerEvidenceSlicePath
    }
    if ([string]::IsNullOrWhiteSpace($writerEvidenceSlicePath) -or -not (Test-Path -LiteralPath $writerEvidenceSlicePath -PathType Leaf)) {
        $reasons.Add('cursor_writer_evidence_slice_missing') | Out-Null
    }
    else {
        $writerEvidenceSliceSha256 = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $writerEvidenceSlice -Name 'sha256' -DefaultValue '')
        $writerEvidenceSliceLineCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $writerEvidenceSlice -Name 'lineCount' -DefaultValue -1) -DefaultValue -1
        if (-not (Test-RouteAcceptanceEvidenceSha256Value -Value $writerEvidenceSliceSha256) -or
            -not [string]::Equals((Get-RouteAcceptanceEvidenceSha256 -Path $writerEvidenceSlicePath), $writerEvidenceSliceSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            $reasons.Add('cursor_writer_evidence_slice_sha256_mismatch') | Out-Null
        }
        if ((Get-RouteAcceptanceEvidenceLineCount -Path $writerEvidenceSlicePath) -ne $writerEvidenceSliceLineCount) {
            $reasons.Add('cursor_writer_evidence_slice_line_count_mismatch') | Out-Null
        }
        if ($writerEvidenceVersion -eq 3) {
            $recomputedWriterChronology = Get-RouteAcceptanceEvidenceWriterChronology -Path $writerEvidenceSlicePath
            if (-not $recomputedWriterChronology.batchEvidenceValid) {
                $reasons.Add('cursor_writer_chronology_batch_invalid') | Out-Null
            }
            if ($writerChronologyInversionCount -ne [long]$recomputedWriterChronology.inversionCount -or
                $writerChronologyCollisionCount -ne [long]$recomputedWriterChronology.collisionCount) {
                $reasons.Add('cursor_writer_chronology_count_mismatch') | Out-Null
            }
        }
    }

    $writerDrains = @(Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerDrains' -DefaultValue @())
    if ($writerDrains.Count -ne $finalWriters.Count -or @($writerDrains | Where-Object { -not (ConvertTo-RouteAcceptanceEvidenceBoolean -Value $_.valid) }).Count -ne 0) {
        $reasons.Add('cursor_writer_drain_invalid') | Out-Null
    }
    $consumedIndexCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'consumedIndexCount' -DefaultValue -1) -DefaultValue -1
    $finalLineCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $final -Name 'lineCount' -DefaultValue -1) -DefaultValue -1
    if ($consumedIndexCount -lt 0 -or $finalLineCount -lt 0 -or $consumedIndexCount -ne $finalLineCount) {
        $reasons.Add('cursor_consumed_index_count_mismatch') | Out-Null
    }
    $consumedIndexSha256 = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'consumedIndexSha256' -DefaultValue '')).Trim().ToLowerInvariant()
    if (-not (Test-RouteAcceptanceEvidenceSha256Value -Value $consumedIndexSha256)) {
        $reasons.Add('cursor_consumed_index_sha256_invalid') | Out-Null
    }
    $consumedIndexRanges = @(Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'consumedIndexRanges' -DefaultValue @())
    $consumedIndexRangeTotal = 0L
    $consumedIndexRangesByGeneration = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    $reconstructedConsumedIdentities = New-Object System.Collections.Generic.List[string]
    foreach ($range in $consumedIndexRanges) {
        $rangeGenerationKey = Get-RouteAcceptanceEvidenceGenerationKey -Generation $range
        $rangeCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $range -Name 'count' -DefaultValue -1) -DefaultValue -1
        $firstOrdinal = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $range -Name 'firstLineOrdinal' -DefaultValue -2) -DefaultValue -2
        $lastOrdinal = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $range -Name 'lastLineOrdinal' -DefaultValue -2) -DefaultValue -2
        if (-not [string]::IsNullOrWhiteSpace($rangeGenerationKey)) {
            if ($consumedIndexRangesByGeneration.ContainsKey($rangeGenerationKey)) {
                $reasons.Add('cursor_consumed_index_range_duplicate_generation') | Out-Null
            }
            else {
                $consumedIndexRangesByGeneration[$rangeGenerationKey] = $range
            }
        }
        if ([string]::IsNullOrWhiteSpace($rangeGenerationKey) -or $finalKeys -notcontains $rangeGenerationKey -or
            $rangeCount -lt 0 -or
            ($rangeCount -eq 0 -and ($firstOrdinal -ne -1 -or $lastOrdinal -ne -1)) -or
            ($rangeCount -gt 0 -and ($firstOrdinal -ne 0 -or $lastOrdinal -ne ($rangeCount - 1)))) {
            $reasons.Add('cursor_consumed_index_range_invalid') | Out-Null
            continue
        }
        $matchingFinalGeneration = @(
            $finalGenerations |
                Where-Object { (Get-RouteAcceptanceEvidenceGenerationKey -Generation $_) -eq $rangeGenerationKey } |
                Select-Object -First 1)
        $generationConsumedLineCount = if ($matchingFinalGeneration.Count -eq 1) {
            ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $matchingFinalGeneration[0] -Name 'consumedLineCount' -DefaultValue -1) -DefaultValue -1
        }
        else {
            -1L
        }
        if ($rangeCount -ne $generationConsumedLineCount) {
            $reasons.Add('cursor_consumed_index_range_generation_count_mismatch') | Out-Null
        }
        if ($rangeCount -le $consumedIndexCount -and $rangeCount -le $finalLineCount) {
            for ($lineOrdinal = 0L; $lineOrdinal -lt $rangeCount; $lineOrdinal++) {
                $reconstructedConsumedIdentities.Add(("{0}`n{1}" -f $rangeGenerationKey, $lineOrdinal)) | Out-Null
            }
        }
        $consumedIndexRangeTotal += $rangeCount
    }
    foreach ($finalKey in $finalKeys) {
        if (-not [string]::IsNullOrWhiteSpace($finalKey) -and -not $consumedIndexRangesByGeneration.ContainsKey($finalKey)) {
            $reasons.Add('cursor_consumed_index_range_generation_missing') | Out-Null
        }
    }
    if ($consumedIndexRanges.Count -ne $finalGenerations.Count -or $consumedIndexRangeTotal -ne $consumedIndexCount) {
        $reasons.Add('cursor_consumed_index_range_count_mismatch') | Out-Null
    }
    $recomputedConsumedIndexSha256 = Get-RouteAcceptanceEvidenceTextSha256 -Value ((@($reconstructedConsumedIdentities.ToArray() | Sort-Object)) -join [char]31)
    if (-not [string]::Equals($consumedIndexSha256, $recomputedConsumedIndexSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        $reasons.Add('cursor_consumed_index_sha256_mismatch') | Out-Null
    }
    $rotationCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'rotationCount' -DefaultValue -1) -DefaultValue -1
    $transientRescanCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'transientRescanCount' -DefaultValue -1) -DefaultValue -1
    $syncCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'syncCount' -DefaultValue -1) -DefaultValue -1
    if ($rotationCount -lt 0 -or $transientRescanCount -lt 0 -or $syncCount -le 0) {
        $reasons.Add('cursor_ledger_counters_invalid') | Out-Null
    }
    $failureCode = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'failureCode' -DefaultValue '')
    if (-not [string]::IsNullOrWhiteSpace($failureCode) -and $failureCode -notmatch '^(\(none\)|none|null)$') {
        $reasons.Add('cursor_ledger_failed') | Out-Null
    }

    $retainedSlice = Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'retainedSlice' -DefaultValue $null
    $retainedPath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $retainedSlice -Name 'path' -DefaultValue '')
    if (-not [string]::IsNullOrWhiteSpace($retainedPath) -and -not [System.IO.Path]::IsPathRooted($retainedPath) -and -not [string]::IsNullOrWhiteSpace($ledgerResult.Path)) {
        $retainedPath = Join-Path ([System.IO.Path]::GetDirectoryName($ledgerResult.Path)) $retainedPath
    }
    $retainedSha256 = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $retainedSlice -Name 'sha256' -DefaultValue '')).Trim().ToLowerInvariant()
    $retainedLineCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $retainedSlice -Name 'lineCount' -DefaultValue -1) -DefaultValue -1
    if ([string]::IsNullOrWhiteSpace($retainedPath) -or -not (Test-Path -LiteralPath $retainedPath -PathType Leaf)) {
        $reasons.Add('cursor_retained_slice_missing') | Out-Null
    }
    else {
        if (-not (Test-RouteAcceptanceEvidenceSha256Value -Value $retainedSha256) -or
            -not [string]::Equals((Get-RouteAcceptanceEvidenceSha256 -Path $retainedPath), $retainedSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            $reasons.Add('cursor_retained_slice_sha256_mismatch') | Out-Null
        }
        if ($retainedLineCount -lt 0 -or (Get-RouteAcceptanceEvidenceLineCount -Path $retainedPath) -ne $retainedLineCount) {
            $reasons.Add('cursor_retained_slice_line_count_mismatch') | Out-Null
        }
        $initialBookmark = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $initial -Name 'bookmark' -DefaultValue -1) -DefaultValue -1
        if ($initialBookmark -ge 0 -and $finalLineCount -ge 0 -and $retainedLineCount -ne [Math]::Max(0L, $finalLineCount - $initialBookmark)) {
            $reasons.Add('cursor_retained_slice_delta_mismatch') | Out-Null
        }
    }

    $eventCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $diagnostics -Name 'event_count' -DefaultValue -1) -DefaultValue -1
    $indexedEventCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $diagnostics -Name 'indexed_event_count' -DefaultValue -1) -DefaultValue -1
    $logFileCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $diagnostics -Name 'log_file_count' -DefaultValue -1) -DefaultValue -1
    if ($eventCount -lt 0 -or $indexedEventCount -lt 0 -or $eventCount -ne $indexedEventCount) {
        $reasons.Add('cursor_event_index_count_mismatch') | Out-Null
    }
    if ($logFileCount -ne 1) {
        $reasons.Add('cursor_log_file_count_mismatch') | Out-Null
    }

    $uniqueReasons = @($reasons | Select-Object -Unique)
    $writerValidationReasons = @(
        $uniqueReasons |
            Where-Object {
                [string]$_ -match '^cursor_(?:writer_|initial_writer|final_writer)'
            }
    )
    $writerValidationVerdict = if ($writerValidationReasons.Count -eq 0) { 'PASS' } else { 'FAIL' }
    return [pscustomobject][ordered]@{
        verdict = if ($uniqueReasons.Count -eq 0) { 'PASS' } else { 'FAIL' }
        reasons = $uniqueReasons
        producerCursorIntegrityVerdict = $reportedCursorVerdict
        producerCursorIntegrityReasons = @($reportedCursorReasons)
        acceptanceRunId = $reportedAcceptanceRunId
        scenarioRunId = $reportedScenarioRunId
        ledgerId = $ledgerId
        initialGenerationKey = $initialGenerationKey
        finalGenerationKey = $finalGenerationKey
        rotationCount = $rotationCount
        transientRescanCount = $transientRescanCount
        gapCount = $gapCount
        duplicateIndexCount = $duplicateIndexCount
        writerEvidenceVersion = $writerEvidenceVersion
        writerGapCount = $writerGapCount
        writerDuplicateCount = $writerDuplicateCount
        writerIdentityMismatchCount = $writerIdentityMismatchCount
        writerWriteFailureCount = $writerWriteFailureCount
        writerDeferredBatchCount = $writerDeferredBatchCount
        writerDeferredLineCount = $writerDeferredLineCount
        writerRecoveredBatchCount = $writerRecoveredBatchCount
        writerRecoveredLineCount = $writerRecoveredLineCount
        writerUnresolvedDeferredBatchCount = $writerUnresolvedDeferredBatchCount
        writerUnresolvedDeferredLineCount = $writerUnresolvedDeferredLineCount
        writerTerminalFailureCount = $writerTerminalFailureCount
        writerRecoveryMismatchCount = $writerRecoveryMismatchCount
        writerChronologyInversionCount = $writerChronologyInversionCount
        writerChronologyCollisionCount = $writerChronologyCollisionCount
        writerDrainMissingCount = $writerDrainMissingCount
        writerRecoveryVerdict = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerRecoveryVerdict' -DefaultValue '')
        writerChronologyVerdict = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $ledger -Name 'writerChronologyVerdict' -DefaultValue '')
        producerWriterIntegrityVerdict = $reportedWriterVerdict
        producerWriterIntegrityReasons = @($reportedWriterReasons)
        writerIntegrityVerdict = $writerValidationVerdict
        writerIntegrityReasons = $writerValidationReasons
        consumedIndexCount = $consumedIndexCount
        indexedEventCount = $indexedEventCount
        eventCount = $eventCount
        logFileCount = $logFileCount
        ledgerPath = $ledgerResult.Path
        ledgerSha256 = $ledgerResult.Sha256
        diagnosticsPath = $diagnosticsResult.Path
        diagnosticsSha256 = $diagnosticsResult.Sha256
        retainedSlicePath = if ([string]::IsNullOrWhiteSpace($retainedPath)) { '' } else { [System.IO.Path]::GetFullPath($retainedPath) }
        retainedSliceSha256 = $retainedSha256
        retainedSliceLineCount = $retainedLineCount
        finalLineCount = $finalLineCount
    }
}

function Get-RouteAcceptanceScenarioEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$AcceptanceRunId,
        [Parameter(Mandatory = $true)][string]$ScenarioName,
        [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$Attempt,
        [Parameter(Mandatory = $true)][string]$ScenarioRunId,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][bool]$ArtifactFinalized,
        [Parameter(Mandatory = $true)][string]$LogLedgerPath,
        [Parameter(Mandatory = $true)][string]$AnalyzerDiagnosticsPath,
        [Parameter(Mandatory = $true)][string]$AnalyzerInputPath
    )

    $cursor = Get-RouteAcceptanceCursorEvidence `
        -LogLedger $LogLedgerPath `
        -AnalyzerDiagnostics $AnalyzerDiagnosticsPath `
        -AcceptanceRunId $AcceptanceRunId `
        -ScenarioRunId $ScenarioRunId
    $resolvedAnalyzerInputPath = [System.IO.Path]::GetFullPath($AnalyzerInputPath)
    $analyzerInputExists = Test-Path -LiteralPath $resolvedAnalyzerInputPath -PathType Leaf
    return [pscustomobject][ordered]@{
        scenarioName = $ScenarioName
        attempt = $Attempt
        scenarioRunId = $ScenarioRunId
        artifactDir = [System.IO.Path]::GetFullPath($ArtifactDir)
        artifactFinalized = $ArtifactFinalized
        logLedger = [pscustomobject][ordered]@{
            acceptanceRunId = $cursor.acceptanceRunId
            scenarioRunId = $cursor.scenarioRunId
            path = $cursor.ledgerPath
            sha256 = $cursor.ledgerSha256
            ledgerId = $cursor.ledgerId
            initialGenerationKey = $cursor.initialGenerationKey
            finalGenerationKey = $cursor.finalGenerationKey
            rotationCount = $cursor.rotationCount
            transientRescanCount = $cursor.transientRescanCount
            gapCount = $cursor.gapCount
            duplicateIndexCount = $cursor.duplicateIndexCount
            writerEvidenceVersion = $cursor.writerEvidenceVersion
            writerGapCount = $cursor.writerGapCount
            writerDuplicateCount = $cursor.writerDuplicateCount
            writerIdentityMismatchCount = $cursor.writerIdentityMismatchCount
            writerWriteFailureCount = $cursor.writerWriteFailureCount
            writerDeferredBatchCount = $cursor.writerDeferredBatchCount
            writerDeferredLineCount = $cursor.writerDeferredLineCount
            writerRecoveredBatchCount = $cursor.writerRecoveredBatchCount
            writerRecoveredLineCount = $cursor.writerRecoveredLineCount
            writerUnresolvedDeferredBatchCount = $cursor.writerUnresolvedDeferredBatchCount
            writerUnresolvedDeferredLineCount = $cursor.writerUnresolvedDeferredLineCount
            writerTerminalFailureCount = $cursor.writerTerminalFailureCount
            writerRecoveryMismatchCount = $cursor.writerRecoveryMismatchCount
            writerChronologyInversionCount = $cursor.writerChronologyInversionCount
            writerChronologyCollisionCount = $cursor.writerChronologyCollisionCount
            writerDrainMissingCount = $cursor.writerDrainMissingCount
            writerRecoveryVerdict = $cursor.writerRecoveryVerdict
            writerChronologyVerdict = $cursor.writerChronologyVerdict
            writerIntegrityVerdict = $cursor.writerIntegrityVerdict
            writerIntegrityReasons = @($cursor.writerIntegrityReasons)
            producerWriterIntegrityVerdict = $cursor.producerWriterIntegrityVerdict
            producerWriterIntegrityReasons = @($cursor.producerWriterIntegrityReasons)
            consumedIndexCount = $cursor.consumedIndexCount
            finalLineCount = $cursor.finalLineCount
            indexedEventCount = $cursor.indexedEventCount
            cursorIntegrityVerdict = $cursor.verdict
            cursorIntegrityReasons = @($cursor.reasons)
            producerCursorIntegrityVerdict = $cursor.producerCursorIntegrityVerdict
            producerCursorIntegrityReasons = @($cursor.producerCursorIntegrityReasons)
        }
        analyzerInput = [pscustomobject][ordered]@{
            path = $resolvedAnalyzerInputPath
            exists = $analyzerInputExists
            sha256 = if ($analyzerInputExists) { Get-RouteAcceptanceEvidenceSha256 -Path $resolvedAnalyzerInputPath } else { '' }
            lineCount = if ($analyzerInputExists) { Get-RouteAcceptanceEvidenceLineCount -Path $resolvedAnalyzerInputPath } else { -1 }
            diagnosticsPath = $cursor.diagnosticsPath
            diagnosticsSha256 = $cursor.diagnosticsSha256
            eventCount = $cursor.eventCount
            indexedEventCount = $cursor.indexedEventCount
            logFileCount = $cursor.logFileCount
            retainedSlicePath = $cursor.retainedSlicePath
            retainedSliceSha256 = $cursor.retainedSliceSha256
            retainedSliceLineCount = $cursor.retainedSliceLineCount
        }
    }
}

function Merge-RouteAcceptanceEvidenceSnapshots {
    param(
        [Parameter(Mandatory = $true)]$InitialSnapshot,
        [Parameter(Mandatory = $true)]$FinalSnapshot
    )

    $initialSource = Get-RouteAcceptanceEvidencePropertyValue -Object $InitialSnapshot -Name 'source' -DefaultValue $null
    $finalSource = Get-RouteAcceptanceEvidencePropertyValue -Object $FinalSnapshot -Name 'source' -DefaultValue $null
    $initialHead = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $initialSource -Name 'gitCommitSha' -DefaultValue '')).ToLowerInvariant()
    $finalHead = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalSource -Name 'gitCommitSha' -DefaultValue '')).ToLowerInvariant()

    $initialRuntime = Get-RouteAcceptanceEvidencePropertyValue -Object $InitialSnapshot -Name 'runtime' -DefaultValue $null
    $finalRuntime = Get-RouteAcceptanceEvidencePropertyValue -Object $FinalSnapshot -Name 'runtime' -DefaultValue $null
    $initialRuntimeSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $initialRuntime -Name 'sha256' -DefaultValue '')).ToLowerInvariant()
    $finalRuntimeSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalRuntime -Name 'sha256' -DefaultValue '')).ToLowerInvariant()

    $initialHarness = Get-RouteAcceptanceEvidencePropertyValue -Object $InitialSnapshot -Name 'harness' -DefaultValue $null
    $finalHarness = Get-RouteAcceptanceEvidencePropertyValue -Object $FinalSnapshot -Name 'harness' -DefaultValue $null
    $initialHarnessFiles = @(Get-RouteAcceptanceEvidencePropertyValue -Object $initialHarness -Name 'files' -DefaultValue @())
    $finalHarnessFiles = @(Get-RouteAcceptanceEvidencePropertyValue -Object $finalHarness -Name 'files' -DefaultValue @())
    $mergedHarnessFiles = New-Object System.Collections.Generic.List[object]
    $allHarnessPaths = @(
        @($initialHarnessFiles + $finalHarnessFiles) |
            ForEach-Object { [string](Get-RouteAcceptanceEvidencePropertyValue -Object $_ -Name 'relativePath' -DefaultValue '') } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )
    foreach ($relativePath in $allHarnessPaths) {
        $initialFile = @($initialHarnessFiles | Where-Object { [string](Get-RouteAcceptanceEvidencePropertyValue -Object $_ -Name 'relativePath' -DefaultValue '') -eq $relativePath } | Select-Object -First 1)
        $finalFile = @($finalHarnessFiles | Where-Object { [string](Get-RouteAcceptanceEvidencePropertyValue -Object $_ -Name 'relativePath' -DefaultValue '') -eq $relativePath } | Select-Object -First 1)
        $initialFileObject = if ($initialFile.Count -eq 0) { $null } else { $initialFile[0] }
        $finalFileObject = if ($finalFile.Count -eq 0) { $null } else { $finalFile[0] }
        $initialSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $initialFileObject -Name 'sha256' -DefaultValue '')).ToLowerInvariant()
        $finalSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalFileObject -Name 'sha256' -DefaultValue '')).ToLowerInvariant()
        $mergedHarnessFiles.Add([pscustomobject][ordered]@{
            relativePath = $relativePath
            path = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalFileObject -Name 'path' -DefaultValue '')
            initialSha256 = $initialSha
            finalSha256 = $finalSha
            hashStable = (Test-RouteAcceptanceEvidenceSha256Value -Value $initialSha) -and [string]::Equals($initialSha, $finalSha, [System.StringComparison]::OrdinalIgnoreCase)
        }) | Out-Null
    }

    $initialHarnessAggregate = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $initialHarness -Name 'aggregateSha256' -DefaultValue '')).ToLowerInvariant()
    $finalHarnessAggregate = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalHarness -Name 'aggregateSha256' -DefaultValue '')).ToLowerInvariant()
    $initialBridge = Get-RouteAcceptanceEvidencePropertyValue -Object $InitialSnapshot -Name 'bridge' -DefaultValue $null
    $finalBridge = Get-RouteAcceptanceEvidencePropertyValue -Object $FinalSnapshot -Name 'bridge' -DefaultValue $null
    $initialBridgeSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $initialBridge -Name 'sha256' -DefaultValue '')).ToLowerInvariant()
    $finalBridgeSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalBridge -Name 'sha256' -DefaultValue '')).ToLowerInvariant()
    $finalDeclaredHash = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalBridge -Name 'declaredIndexSha256' -DefaultValue '')).ToLowerInvariant()

    return [pscustomobject][ordered]@{
        source = [pscustomobject][ordered]@{
            initialGitCommitSha = $initialHead
            finalGitCommitSha = $finalHead
            gitCommitSha = if ([string]::Equals($initialHead, $finalHead, [System.StringComparison]::OrdinalIgnoreCase)) { $finalHead } else { '' }
            headStable = (Test-RouteAcceptanceEvidenceGitCommitValue -Value $initialHead) -and [string]::Equals($initialHead, $finalHead, [System.StringComparison]::OrdinalIgnoreCase)
            dirtyAtStart = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $initialSource -Name 'dirty' -DefaultValue $true) -DefaultValue $true
            dirtyAtFinal = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $finalSource -Name 'dirty' -DefaultValue $true) -DefaultValue $true
            dirty = (ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $initialSource -Name 'dirty' -DefaultValue $true) -DefaultValue $true) -or (ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $finalSource -Name 'dirty' -DefaultValue $true) -DefaultValue $true)
            normalizedStatusSha256 = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalSource -Name 'normalizedStatusSha256' -DefaultValue '')).ToLowerInvariant()
        }
        runtime = [pscustomobject][ordered]@{
            portableExecutablePath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalRuntime -Name 'path' -DefaultValue '')
            initialSha256 = $initialRuntimeSha
            finalSha256 = $finalRuntimeSha
            sha256 = if ([string]::Equals($initialRuntimeSha, $finalRuntimeSha, [System.StringComparison]::OrdinalIgnoreCase)) { $finalRuntimeSha } else { '' }
            hashStable = (Test-RouteAcceptanceEvidenceSha256Value -Value $initialRuntimeSha) -and [string]::Equals($initialRuntimeSha, $finalRuntimeSha, [System.StringComparison]::OrdinalIgnoreCase)
        }
        harness = [pscustomobject][ordered]@{
            files = @($mergedHarnessFiles.ToArray())
            initialAggregateSha256 = $initialHarnessAggregate
            finalAggregateSha256 = $finalHarnessAggregate
            aggregateSha256 = if ([string]::Equals($initialHarnessAggregate, $finalHarnessAggregate, [System.StringComparison]::OrdinalIgnoreCase)) { $finalHarnessAggregate } else { '' }
            hashStable = (Test-RouteAcceptanceEvidenceSha256Value -Value $initialHarnessAggregate) -and [string]::Equals($initialHarnessAggregate, $finalHarnessAggregate, [System.StringComparison]::OrdinalIgnoreCase) -and @($mergedHarnessFiles | Where-Object { -not $_.hashStable }).Count -eq 0
        }
        bridge = [pscustomobject][ordered]@{
            bundlePath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalBridge -Name 'bundlePath' -DefaultValue '')
            indexPath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalBridge -Name 'indexPath' -DefaultValue '')
            manifestPath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $finalBridge -Name 'manifestPath' -DefaultValue '')
            initialSha256 = $initialBridgeSha
            finalSha256 = $finalBridgeSha
            sha256 = if ([string]::Equals($initialBridgeSha, $finalBridgeSha, [System.StringComparison]::OrdinalIgnoreCase)) { $finalBridgeSha } else { '' }
            hashStable = (Test-RouteAcceptanceEvidenceSha256Value -Value $initialBridgeSha) -and [string]::Equals($initialBridgeSha, $finalBridgeSha, [System.StringComparison]::OrdinalIgnoreCase)
            declaredIndexSha256 = $finalDeclaredHash
            declaredHashMatches = (ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $initialBridge -Name 'declaredHashMatches' -DefaultValue $false)) -and (ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $finalBridge -Name 'declaredHashMatches' -DefaultValue $false)) -and [string]::Equals($finalBridgeSha, $finalDeclaredHash, [System.StringComparison]::OrdinalIgnoreCase)
        }
    }
}

function Get-RouteAcceptanceEvidenceMergedHarnessAggregateSha256 {
    param(
        [Parameter(Mandatory = $true)][object[]]$Files,
        [Parameter(Mandatory = $true)][ValidateSet('initialSha256', 'finalSha256')][string]$HashProperty
    )

    $snapshots = @(
        foreach ($file in $Files) {
            [pscustomobject]@{
                relativePath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $file -Name 'relativePath' -DefaultValue '')
                exists = $true
                sha256 = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $file -Name $HashProperty -DefaultValue '')
            }
        }
    )
    return Get-RouteAcceptanceEvidenceHarnessAggregateSha256 -Files $snapshots
}

function Get-RouteAcceptanceEvidenceManifestValidation {
    param([Parameter(Mandatory = $true)]$Manifest)

    $structuralReasons = New-Object System.Collections.Generic.List[string]
    $provenanceReasons = New-Object System.Collections.Generic.List[string]
    $cursorReasons = New-Object System.Collections.Generic.List[string]
    if ((ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $Manifest -Name 'schemaVersion' -DefaultValue 0)) -ne 1) {
        $structuralReasons.Add('manifest_schema_unsupported') | Out-Null
    }
    if ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $Manifest -Name 'event' -DefaultValue '') -ne 'filetransfer_route_acceptance_run_manifest') {
        $structuralReasons.Add('manifest_event_invalid') | Out-Null
    }
    $runId = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $Manifest -Name 'runId' -DefaultValue '')).Trim()
    $parsedRunId = [guid]::Empty
    if ([string]::IsNullOrWhiteSpace($runId)) {
        $structuralReasons.Add('manifest_run_id_missing') | Out-Null
    }
    elseif (-not [guid]::TryParse($runId, [ref]$parsedRunId)) {
        $structuralReasons.Add('manifest_run_id_invalid') | Out-Null
    }
    $executionMode = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $Manifest -Name 'executionMode' -DefaultValue '')
    if ($executionMode -notin @('live', 'fake', 'evidence_dry_run')) {
        $structuralReasons.Add('manifest_execution_mode_invalid') | Out-Null
    }

    $source = Get-RouteAcceptanceEvidencePropertyValue -Object $Manifest -Name 'source' -DefaultValue $null
    $initialHead = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $source -Name 'initialGitCommitSha' -DefaultValue '')).Trim().ToLowerInvariant()
    $finalHead = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $source -Name 'finalGitCommitSha' -DefaultValue '')).Trim().ToLowerInvariant()
    $claimedHead = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $source -Name 'gitCommitSha' -DefaultValue '')).Trim().ToLowerInvariant()
    $expectedHeadStable = (Test-RouteAcceptanceEvidenceGitCommitValue -Value $initialHead) -and
        (Test-RouteAcceptanceEvidenceGitCommitValue -Value $finalHead) -and
        [string]::Equals($initialHead, $finalHead, [System.StringComparison]::OrdinalIgnoreCase)
    $claimedHeadStable = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $source -Name 'headStable' -DefaultValue $false)
    $expectedClaimedHead = if ($expectedHeadStable) { $finalHead } else { '' }
    if ($claimedHeadStable -ne $expectedHeadStable -or
        -not [string]::Equals($claimedHead, $expectedClaimedHead, [System.StringComparison]::OrdinalIgnoreCase)) {
        $provenanceReasons.Add('source_head_consistency_invalid') | Out-Null
    }
    if (-not $expectedHeadStable) {
        $provenanceReasons.Add('source_head_changed') | Out-Null
    }
    $dirtyAtStart = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $source -Name 'dirtyAtStart' -DefaultValue $true) -DefaultValue $true
    $dirtyAtFinal = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $source -Name 'dirtyAtFinal' -DefaultValue $true) -DefaultValue $true
    $expectedDirty = $dirtyAtStart -or $dirtyAtFinal
    $claimedDirty = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $source -Name 'dirty' -DefaultValue $true) -DefaultValue $true
    if ($claimedDirty -ne $expectedDirty) {
        $provenanceReasons.Add('source_dirty_consistency_invalid') | Out-Null
    }
    if ($expectedDirty) {
        $provenanceReasons.Add('source_dirty') | Out-Null
    }
    if (-not (Test-RouteAcceptanceEvidenceGitCommitValue -Value $claimedHead)) {
        $provenanceReasons.Add('source_commit_sha_missing') | Out-Null
    }
    if (-not (Test-RouteAcceptanceEvidenceSha256Value -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $source -Name 'normalizedStatusSha256' -DefaultValue ''))) {
        $provenanceReasons.Add('source_status_sha256_missing') | Out-Null
    }

    $runtime = Get-RouteAcceptanceEvidencePropertyValue -Object $Manifest -Name 'runtime' -DefaultValue $null
    $initialRuntimeSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $runtime -Name 'initialSha256' -DefaultValue '')).Trim().ToLowerInvariant()
    $finalRuntimeSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $runtime -Name 'finalSha256' -DefaultValue '')).Trim().ToLowerInvariant()
    $claimedRuntimeSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $runtime -Name 'sha256' -DefaultValue '')).Trim().ToLowerInvariant()
    $expectedRuntimeStable = (Test-RouteAcceptanceEvidenceSha256Value -Value $initialRuntimeSha) -and
        (Test-RouteAcceptanceEvidenceSha256Value -Value $finalRuntimeSha) -and
        [string]::Equals($initialRuntimeSha, $finalRuntimeSha, [System.StringComparison]::OrdinalIgnoreCase)
    $claimedRuntimeStable = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $runtime -Name 'hashStable' -DefaultValue $false)
    $expectedRuntimeSha = if ($expectedRuntimeStable) { $finalRuntimeSha } else { '' }
    if ($claimedRuntimeStable -ne $expectedRuntimeStable -or
        -not [string]::Equals($claimedRuntimeSha, $expectedRuntimeSha, [System.StringComparison]::OrdinalIgnoreCase)) {
        $provenanceReasons.Add('runtime_provenance_inconsistent') | Out-Null
    }
    if (-not $expectedRuntimeStable) {
        $provenanceReasons.Add('runtime_sha256_changed') | Out-Null
    }
    if (-not (Test-RouteAcceptanceEvidenceSha256Value -Value $claimedRuntimeSha)) {
        $provenanceReasons.Add('runtime_sha256_missing') | Out-Null
    }
    $runtimePath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $runtime -Name 'portableExecutablePath' -DefaultValue '')
    if ([string]::IsNullOrWhiteSpace($runtimePath)) {
        $provenanceReasons.Add('runtime_path_missing') | Out-Null
    }
    elseif (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
        $provenanceReasons.Add('runtime_file_missing') | Out-Null
    }
    elseif (-not [string]::Equals((Get-RouteAcceptanceEvidenceSha256 -Path $runtimePath), $finalRuntimeSha, [System.StringComparison]::OrdinalIgnoreCase)) {
        $provenanceReasons.Add('runtime_current_sha256_mismatch') | Out-Null
    }

    $harness = Get-RouteAcceptanceEvidencePropertyValue -Object $Manifest -Name 'harness' -DefaultValue $null
    $harnessFiles = @(Get-RouteAcceptanceEvidencePropertyValue -Object $harness -Name 'files' -DefaultValue @())
    $requiredHarnessPaths = @(
        'tools/Run-FileTransferRouteAcceptance.ps1',
        'tools/FileTransferRouteAcceptanceEvidence.ps1',
        'tools/Run-FileTransferTunaGuiSmoke.ps1',
        'tools/GuiSmoke-Windows.ps1',
        'tools/GuiSmokeLogLedger.ps1'
    )
    $harnessPaths = @($harnessFiles | ForEach-Object { ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $_ -Name 'relativePath' -DefaultValue '')).Replace('\', '/') })
    $normalizedHarnessPaths = @($harnessPaths | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object)
    $normalizedRequiredHarnessPaths = @($requiredHarnessPaths | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object)
    if ($harnessFiles.Count -ne 5 -or
        @($normalizedHarnessPaths | Select-Object -Unique).Count -ne 5 -or
        ($normalizedHarnessPaths -join "`n") -ne ($normalizedRequiredHarnessPaths -join "`n")) {
        $provenanceReasons.Add('harness_required_files_mismatch') | Out-Null
    }

    $allHarnessFilesStable = $true
    foreach ($harnessFile in $harnessFiles) {
        $initialFileSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $harnessFile -Name 'initialSha256' -DefaultValue '')).Trim().ToLowerInvariant()
        $finalFileSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $harnessFile -Name 'finalSha256' -DefaultValue '')).Trim().ToLowerInvariant()
        $expectedFileStable = (Test-RouteAcceptanceEvidenceSha256Value -Value $initialFileSha) -and
            (Test-RouteAcceptanceEvidenceSha256Value -Value $finalFileSha) -and
            [string]::Equals($initialFileSha, $finalFileSha, [System.StringComparison]::OrdinalIgnoreCase)
        $claimedFileStable = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $harnessFile -Name 'hashStable' -DefaultValue $false)
        if ($claimedFileStable -ne $expectedFileStable) {
            $provenanceReasons.Add('harness_file_provenance_inconsistent') | Out-Null
        }
        if (-not $expectedFileStable) {
            $allHarnessFilesStable = $false
        }
        $harnessFilePath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $harnessFile -Name 'path' -DefaultValue '')
        if ([string]::IsNullOrWhiteSpace($harnessFilePath) -or -not (Test-Path -LiteralPath $harnessFilePath -PathType Leaf)) {
            $provenanceReasons.Add('harness_file_missing') | Out-Null
        }
        elseif (-not [string]::Equals((Get-RouteAcceptanceEvidenceSha256 -Path $harnessFilePath), $finalFileSha, [System.StringComparison]::OrdinalIgnoreCase)) {
            $provenanceReasons.Add('harness_current_sha256_mismatch') | Out-Null
        }
    }

    $initialHarnessAggregate = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $harness -Name 'initialAggregateSha256' -DefaultValue '')).Trim().ToLowerInvariant()
    $finalHarnessAggregate = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $harness -Name 'finalAggregateSha256' -DefaultValue '')).Trim().ToLowerInvariant()
    $claimedHarnessAggregate = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $harness -Name 'aggregateSha256' -DefaultValue '')).Trim().ToLowerInvariant()
    $recomputedInitialHarnessAggregate = Get-RouteAcceptanceEvidenceMergedHarnessAggregateSha256 -Files $harnessFiles -HashProperty initialSha256
    $recomputedFinalHarnessAggregate = Get-RouteAcceptanceEvidenceMergedHarnessAggregateSha256 -Files $harnessFiles -HashProperty finalSha256
    $initialHarnessAggregateMatches = (Test-RouteAcceptanceEvidenceSha256Value -Value $initialHarnessAggregate) -and
        [string]::Equals($initialHarnessAggregate, $recomputedInitialHarnessAggregate, [System.StringComparison]::OrdinalIgnoreCase)
    $finalHarnessAggregateMatches = (Test-RouteAcceptanceEvidenceSha256Value -Value $finalHarnessAggregate) -and
        [string]::Equals($finalHarnessAggregate, $recomputedFinalHarnessAggregate, [System.StringComparison]::OrdinalIgnoreCase)
    if (-not $initialHarnessAggregateMatches -or -not $finalHarnessAggregateMatches) {
        $provenanceReasons.Add('harness_aggregate_sha256_mismatch') | Out-Null
    }
    $expectedHarnessStable = $allHarnessFilesStable -and $initialHarnessAggregateMatches -and
        $finalHarnessAggregateMatches -and
        [string]::Equals($initialHarnessAggregate, $finalHarnessAggregate, [System.StringComparison]::OrdinalIgnoreCase)
    $claimedHarnessStable = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $harness -Name 'hashStable' -DefaultValue $false)
    $expectedHarnessAggregate = if ($expectedHarnessStable) { $finalHarnessAggregate } else { '' }
    if ($claimedHarnessStable -ne $expectedHarnessStable -or
        -not [string]::Equals($claimedHarnessAggregate, $expectedHarnessAggregate, [System.StringComparison]::OrdinalIgnoreCase)) {
        $provenanceReasons.Add('harness_provenance_inconsistent') | Out-Null
    }
    if (-not $expectedHarnessStable) {
        $provenanceReasons.Add('harness_sha256_changed') | Out-Null
    }
    if (-not (Test-RouteAcceptanceEvidenceSha256Value -Value $claimedHarnessAggregate)) {
        $provenanceReasons.Add('harness_sha256_missing') | Out-Null
    }

    $bridge = Get-RouteAcceptanceEvidencePropertyValue -Object $Manifest -Name 'bridge' -DefaultValue $null
    $initialBridgeSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $bridge -Name 'initialSha256' -DefaultValue '')).Trim().ToLowerInvariant()
    $finalBridgeSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $bridge -Name 'finalSha256' -DefaultValue '')).Trim().ToLowerInvariant()
    $claimedBridgeSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $bridge -Name 'sha256' -DefaultValue '')).Trim().ToLowerInvariant()
    $expectedBridgeStable = (Test-RouteAcceptanceEvidenceSha256Value -Value $initialBridgeSha) -and
        (Test-RouteAcceptanceEvidenceSha256Value -Value $finalBridgeSha) -and
        [string]::Equals($initialBridgeSha, $finalBridgeSha, [System.StringComparison]::OrdinalIgnoreCase)
    $claimedBridgeStable = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $bridge -Name 'hashStable' -DefaultValue $false)
    $expectedBridgeSha = if ($expectedBridgeStable) { $finalBridgeSha } else { '' }
    if ($claimedBridgeStable -ne $expectedBridgeStable -or
        -not [string]::Equals($claimedBridgeSha, $expectedBridgeSha, [System.StringComparison]::OrdinalIgnoreCase)) {
        $provenanceReasons.Add('bridge_provenance_inconsistent') | Out-Null
    }
    if (-not $expectedBridgeStable) {
        $provenanceReasons.Add('bridge_sha256_changed') | Out-Null
    }
    if (-not (Test-RouteAcceptanceEvidenceSha256Value -Value $claimedBridgeSha)) {
        $provenanceReasons.Add('bridge_sha256_missing') | Out-Null
    }
    $declaredBridgeSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $bridge -Name 'declaredIndexSha256' -DefaultValue '')).Trim().ToLowerInvariant()
    $expectedDeclaredBridgeMatch = $expectedBridgeStable -and
        (Test-RouteAcceptanceEvidenceSha256Value -Value $declaredBridgeSha) -and
        [string]::Equals($finalBridgeSha, $declaredBridgeSha, [System.StringComparison]::OrdinalIgnoreCase)
    $claimedDeclaredBridgeMatch = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $bridge -Name 'declaredHashMatches' -DefaultValue $false)
    if ($claimedDeclaredBridgeMatch -ne $expectedDeclaredBridgeMatch) {
        $provenanceReasons.Add('bridge_declared_hash_consistency_invalid') | Out-Null
    }
    if (-not $expectedDeclaredBridgeMatch) {
        $provenanceReasons.Add('bridge_manifest_sha256_mismatch') | Out-Null
    }
    $bridgeIndexPath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $bridge -Name 'indexPath' -DefaultValue '')
    if ([string]::IsNullOrWhiteSpace($bridgeIndexPath) -or -not (Test-Path -LiteralPath $bridgeIndexPath -PathType Leaf)) {
        $provenanceReasons.Add('bridge_index_missing') | Out-Null
    }
    elseif (-not [string]::Equals((Get-RouteAcceptanceEvidenceSha256 -Path $bridgeIndexPath), $finalBridgeSha, [System.StringComparison]::OrdinalIgnoreCase)) {
        $provenanceReasons.Add('bridge_current_sha256_mismatch') | Out-Null
    }
    $bridgeManifestPath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $bridge -Name 'manifestPath' -DefaultValue '')
    if ([string]::IsNullOrWhiteSpace($bridgeManifestPath) -or -not (Test-Path -LiteralPath $bridgeManifestPath -PathType Leaf)) {
        $provenanceReasons.Add('bridge_manifest_missing') | Out-Null
    }
    else {
        try {
            $currentBridgeManifest = Get-Content -LiteralPath $bridgeManifestPath -Raw | ConvertFrom-Json
            $currentDeclaredBridgeSha = ([string](Get-RouteAcceptanceEvidencePropertyValue -Object $currentBridgeManifest -Name 'bridgeScriptSha256' -DefaultValue '')).Trim().ToLowerInvariant()
            if (-not [string]::Equals($currentDeclaredBridgeSha, $declaredBridgeSha, [System.StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals($currentDeclaredBridgeSha, $finalBridgeSha, [System.StringComparison]::OrdinalIgnoreCase)) {
                $provenanceReasons.Add('bridge_manifest_current_declaration_mismatch') | Out-Null
            }
        }
        catch {
            $provenanceReasons.Add('bridge_manifest_malformed') | Out-Null
        }
    }

    $scenarioNames = @((Get-RouteAcceptanceEvidencePropertyValue -Object $Manifest -Name 'scenarioNames' -DefaultValue @()) | ForEach-Object { [string]$_ })
    $scenarioAttempts = @(Get-RouteAcceptanceEvidencePropertyValue -Object $Manifest -Name 'scenarioAttempts' -DefaultValue @())
    if ($scenarioAttempts.Count -eq 0) {
        $cursorReasons.Add('scenario_attempts_missing') | Out-Null
    }
    $seenScenarioRunIds = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::Ordinal)
    foreach ($scenarioAttempt in $scenarioAttempts) {
        $scenarioName = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $scenarioAttempt -Name 'scenarioName' -DefaultValue '')
        $attemptNumber = [int](ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $scenarioAttempt -Name 'attempt' -DefaultValue 0))
        $scenarioRunId = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $scenarioAttempt -Name 'scenarioRunId' -DefaultValue '')
        if ([string]::IsNullOrWhiteSpace($scenarioName) -or $scenarioNames -notcontains $scenarioName) {
            $cursorReasons.Add('scenario_name_mismatch') | Out-Null
        }
        if ($attemptNumber -le 0) {
            $cursorReasons.Add('scenario_attempt_invalid') | Out-Null
        }
        else {
            $expectedScenarioRunId = Get-RouteAcceptanceEvidenceScenarioRunId -AcceptanceRunId $runId -ScenarioName $scenarioName -Attempt $attemptNumber
            if (-not [string]::Equals($scenarioRunId, $expectedScenarioRunId, [System.StringComparison]::Ordinal)) {
                $cursorReasons.Add('scenario_run_id_mismatch') | Out-Null
            }
        }
        if ([string]::IsNullOrWhiteSpace($scenarioRunId) -or -not $seenScenarioRunIds.Add($scenarioRunId)) {
            $cursorReasons.Add('scenario_run_id_duplicate_or_missing') | Out-Null
        }
        if (-not (ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $scenarioAttempt -Name 'artifactFinalized' -DefaultValue $false))) {
            $cursorReasons.Add('scenario_artifact_not_finalized') | Out-Null
        }
        $logLedger = Get-RouteAcceptanceEvidencePropertyValue -Object $scenarioAttempt -Name 'logLedger' -DefaultValue $null
        if (-not [string]::Equals([string](Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'acceptanceRunId' -DefaultValue ''), $runId, [System.StringComparison]::Ordinal)) {
            $cursorReasons.Add('cursor_acceptance_run_id_mismatch') | Out-Null
        }
        if (-not [string]::Equals([string](Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'scenarioRunId' -DefaultValue ''), $scenarioRunId, [System.StringComparison]::Ordinal)) {
            $cursorReasons.Add('cursor_scenario_run_id_mismatch') | Out-Null
        }
        if (-not [string]::Equals([string](Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'producerCursorIntegrityVerdict' -DefaultValue ''), 'pass', [System.StringComparison]::OrdinalIgnoreCase)) {
            $cursorReasons.Add('cursor_reported_integrity_failed') | Out-Null
        }
        if (-not [string]::Equals([string](Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'cursorIntegrityVerdict' -DefaultValue ''), 'PASS', [System.StringComparison]::OrdinalIgnoreCase)) {
            $reportedReasons = @(Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'cursorIntegrityReasons' -DefaultValue @())
            if ($reportedReasons.Count -eq 0) {
                $cursorReasons.Add('cursor_integrity_failed') | Out-Null
            }
            else {
                foreach ($reportedReason in $reportedReasons) {
                    $cursorReasons.Add([string]$reportedReason) | Out-Null
                }
            }
        }
        foreach ($requiredCursorField in @(
                'gapCount',
                'duplicateIndexCount',
                'writerEvidenceVersion',
                'writerGapCount',
                'writerDuplicateCount',
                'writerIdentityMismatchCount',
                'writerWriteFailureCount',
                'writerDeferredBatchCount',
                'writerRecoveredBatchCount',
                'writerUnresolvedDeferredBatchCount',
                'writerTerminalFailureCount',
                'writerRecoveryMismatchCount',
                'writerChronologyInversionCount',
                'writerChronologyCollisionCount',
                'writerDrainMissingCount',
                'writerRecoveryVerdict',
                'writerChronologyVerdict',
                'writerIntegrityVerdict',
                'consumedIndexCount',
                'finalLineCount')) {
            if (-not (Test-RouteAcceptanceEvidencePropertyPresent -Object $logLedger -Name $requiredCursorField)) {
                $cursorReasons.Add(('cursor_{0}_missing' -f $requiredCursorField.ToLowerInvariant())) | Out-Null
            }
        }
        $gapCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'gapCount' -DefaultValue -1) -DefaultValue -1
        $duplicateIndexCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'duplicateIndexCount' -DefaultValue -1) -DefaultValue -1
        $writerEvidenceVersion = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerEvidenceVersion' -DefaultValue 0)
        $writerGapCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerGapCount' -DefaultValue -1) -DefaultValue -1
        $writerDuplicateCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerDuplicateCount' -DefaultValue -1) -DefaultValue -1
        $writerIdentityMismatchCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerIdentityMismatchCount' -DefaultValue -1) -DefaultValue -1
        $writerWriteFailureCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerWriteFailureCount' -DefaultValue -1) -DefaultValue -1
        $writerUnresolvedDeferredBatchCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerUnresolvedDeferredBatchCount' -DefaultValue -1) -DefaultValue -1
        $writerTerminalFailureCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerTerminalFailureCount' -DefaultValue -1) -DefaultValue -1
        $writerRecoveryMismatchCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerRecoveryMismatchCount' -DefaultValue -1) -DefaultValue -1
        $writerChronologyInversionCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerChronologyInversionCount' -DefaultValue -1) -DefaultValue -1
        $writerChronologyCollisionCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerChronologyCollisionCount' -DefaultValue -1) -DefaultValue -1
        $writerDrainMissingCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerDrainMissingCount' -DefaultValue -1) -DefaultValue -1
        $consumedIndexCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'consumedIndexCount' -DefaultValue -1) -DefaultValue -1
        $finalLineCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'finalLineCount' -DefaultValue -1) -DefaultValue -1
        if ($gapCount -ne 0) {
            $cursorReasons.Add('cursor_rotation_gap') | Out-Null
        }
        if ($duplicateIndexCount -ne 0) {
            $cursorReasons.Add('cursor_duplicate_index') | Out-Null
        }
        if ($writerEvidenceVersion -ne 3) {
            $cursorReasons.Add('cursor_writer_evidence_version_invalid') | Out-Null
        }
        if ($writerGapCount -ne 0) {
            $cursorReasons.Add('cursor_writer_gap') | Out-Null
        }
        if ($writerDuplicateCount -ne 0) {
            $cursorReasons.Add('cursor_writer_duplicate') | Out-Null
        }
        if ($writerIdentityMismatchCount -ne 0) {
            $cursorReasons.Add('cursor_writer_identity_mismatch') | Out-Null
        }
        if ($writerWriteFailureCount -ne 0) {
            $cursorReasons.Add('cursor_writer_write_failure') | Out-Null
        }
        if ($writerUnresolvedDeferredBatchCount -ne 0) { $cursorReasons.Add('cursor_writer_deferred_unresolved') | Out-Null }
        if ($writerTerminalFailureCount -ne 0) { $cursorReasons.Add('cursor_writer_terminal_failure') | Out-Null }
        if ($writerRecoveryMismatchCount -ne 0) { $cursorReasons.Add('cursor_writer_recovery_mismatch') | Out-Null }
        if ($writerChronologyInversionCount -ne 0) { $cursorReasons.Add('cursor_writer_chronology_inversion') | Out-Null }
        if ($writerChronologyCollisionCount -ne 0) { $cursorReasons.Add('cursor_writer_chronology_collision') | Out-Null }
        if ($writerDrainMissingCount -ne 0) { $cursorReasons.Add('cursor_writer_drain_missing') | Out-Null }
        if (-not [string]::Equals([string](Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerRecoveryVerdict' -DefaultValue ''), 'pass', [System.StringComparison]::OrdinalIgnoreCase)) {
            $cursorReasons.Add('cursor_writer_recovery_verdict_failed') | Out-Null
        }
        if (-not [string]::Equals([string](Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerChronologyVerdict' -DefaultValue ''), 'pass', [System.StringComparison]::OrdinalIgnoreCase)) {
            $cursorReasons.Add('cursor_writer_chronology_verdict_failed') | Out-Null
        }
        if (-not [string]::Equals([string](Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'writerIntegrityVerdict' -DefaultValue ''), 'pass', [System.StringComparison]::OrdinalIgnoreCase)) {
            $cursorReasons.Add('cursor_writer_integrity_failed') | Out-Null
        }
        if ($consumedIndexCount -lt 0 -or $finalLineCount -lt 0 -or $consumedIndexCount -ne $finalLineCount) {
            $cursorReasons.Add('cursor_consumed_index_count_mismatch') | Out-Null
        }
        if (-not (Test-RouteAcceptanceEvidenceSha256Value -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'sha256' -DefaultValue ''))) {
            $cursorReasons.Add('cursor_ledger_sha256_missing') | Out-Null
        }
        $analyzerInput = Get-RouteAcceptanceEvidencePropertyValue -Object $scenarioAttempt -Name 'analyzerInput' -DefaultValue $null
        if (-not (Test-RouteAcceptanceEvidenceSha256Value -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $analyzerInput -Name 'sha256' -DefaultValue ''))) {
            $cursorReasons.Add('analyzer_input_sha256_missing') | Out-Null
        }
        $eventCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $analyzerInput -Name 'eventCount' -DefaultValue -1) -DefaultValue -1
        $indexedEventCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $analyzerInput -Name 'indexedEventCount' -DefaultValue -1) -DefaultValue -1
        $ledgerIndexedEventCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'indexedEventCount' -DefaultValue -1) -DefaultValue -1
        if ($eventCount -lt 0 -or $indexedEventCount -lt 0 -or $eventCount -ne $indexedEventCount -or $ledgerIndexedEventCount -ne $indexedEventCount) {
            $cursorReasons.Add('cursor_event_index_count_mismatch') | Out-Null
        }
        if ((ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $analyzerInput -Name 'logFileCount' -DefaultValue -1) -DefaultValue -1) -ne 1) {
            $cursorReasons.Add('cursor_log_file_count_mismatch') | Out-Null
        }

        $ledgerPath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'path' -DefaultValue '')
        $diagnosticsPath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $analyzerInput -Name 'diagnosticsPath' -DefaultValue '')
        if ([string]::IsNullOrWhiteSpace($ledgerPath) -or -not (Test-Path -LiteralPath $ledgerPath -PathType Leaf)) {
            $cursorReasons.Add('cursor_ledger_missing') | Out-Null
        }
        elseif ([string]::IsNullOrWhiteSpace($diagnosticsPath) -or -not (Test-Path -LiteralPath $diagnosticsPath -PathType Leaf)) {
            $cursorReasons.Add('analyzer_diagnostics_missing') | Out-Null
        }
        else {
            $currentCursor = Get-RouteAcceptanceCursorEvidence `
                -LogLedger $ledgerPath `
                -AnalyzerDiagnostics $diagnosticsPath `
                -AcceptanceRunId $runId `
                -ScenarioRunId $scenarioRunId
            if ($currentCursor.verdict -ne 'PASS') {
                foreach ($currentCursorReason in @($currentCursor.reasons)) {
                    $cursorReasons.Add([string]$currentCursorReason) | Out-Null
                }
            }
            if ($writerChronologyInversionCount -ne [long]$currentCursor.writerChronologyInversionCount -or
                $writerChronologyCollisionCount -ne [long]$currentCursor.writerChronologyCollisionCount) {
                $cursorReasons.Add('cursor_writer_chronology_count_mismatch') | Out-Null
            }
            if (-not [string]::Equals([string](Get-RouteAcceptanceEvidencePropertyValue -Object $logLedger -Name 'sha256' -DefaultValue ''), $currentCursor.ledgerSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                $cursorReasons.Add('cursor_ledger_current_sha256_mismatch') | Out-Null
            }
            if (-not [string]::Equals([string](Get-RouteAcceptanceEvidencePropertyValue -Object $analyzerInput -Name 'diagnosticsSha256' -DefaultValue ''), $currentCursor.diagnosticsSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                $cursorReasons.Add('analyzer_diagnostics_current_sha256_mismatch') | Out-Null
            }
        }

        $analyzerInputPath = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $analyzerInput -Name 'path' -DefaultValue '')
        if ([string]::IsNullOrWhiteSpace($analyzerInputPath) -or -not (Test-Path -LiteralPath $analyzerInputPath -PathType Leaf)) {
            $cursorReasons.Add('analyzer_input_missing') | Out-Null
        }
        else {
            if (-not [string]::Equals((Get-RouteAcceptanceEvidenceSha256 -Path $analyzerInputPath), [string](Get-RouteAcceptanceEvidencePropertyValue -Object $analyzerInput -Name 'sha256' -DefaultValue ''), [System.StringComparison]::OrdinalIgnoreCase)) {
                $cursorReasons.Add('analyzer_input_current_sha256_mismatch') | Out-Null
            }
            $recordedAnalyzerLineCount = ConvertTo-RouteAcceptanceEvidenceInt64 -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $analyzerInput -Name 'lineCount' -DefaultValue -1) -DefaultValue -1
            if ($recordedAnalyzerLineCount -lt 0 -or (Get-RouteAcceptanceEvidenceLineCount -Path $analyzerInputPath) -ne $recordedAnalyzerLineCount) {
                $cursorReasons.Add('analyzer_input_current_line_count_mismatch') | Out-Null
            }
        }
    }

    $structural = @($structuralReasons | Select-Object -Unique)
    $provenance = @($provenanceReasons | Select-Object -Unique)
    $cursor = @($cursorReasons | Select-Object -Unique)
    $allReasons = @($structural + $provenance + $cursor)
    $verdict = if ($allReasons.Count -eq 0) { 'PASS' } else { 'FAIL' }
    $proofReasons = New-Object System.Collections.Generic.List[string]
    foreach ($reason in $allReasons) {
        $proofReasons.Add($reason) | Out-Null
    }
    if ($executionMode -ne 'live') {
        $proofReasons.Add('execution_mode_not_live') | Out-Null
    }

    return [pscustomobject][ordered]@{
        verdict = $verdict
        provenanceVerdict = if (($structural.Count + $provenance.Count) -eq 0) { 'PASS' } else { 'FAIL' }
        cursorIntegrityVerdict = if ($cursor.Count -eq 0) { 'PASS' } else { 'FAIL' }
        currentRuntimeProof = $verdict -eq 'PASS' -and $executionMode -eq 'live'
        reasons = @($proofReasons | Select-Object -Unique)
        structuralReasons = $structural
        provenanceReasons = $provenance
        cursorIntegrityReasons = $cursor
    }
}

function New-RouteAcceptanceEvidenceManifest {
    param(
        [Parameter(Mandatory = $true)][string]$RunId,
        [Parameter(Mandatory = $true)][string]$MatrixMode,
        [Parameter(Mandatory = $true)][ValidateSet('live', 'fake', 'evidence_dry_run')][string]$ExecutionMode,
        [Parameter(Mandatory = $true)][string]$ArtifactRoot,
        [Parameter(Mandatory = $true)][datetime]$StartedUtc,
        [Parameter(Mandatory = $true)][datetime]$FinalizedUtc,
        [Parameter(Mandatory = $true)][string[]]$ScenarioNames,
        [Parameter(Mandatory = $true)]$InitialSnapshot,
        [Parameter(Mandatory = $true)]$FinalSnapshot,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$ScenarioAttempts
    )

    $merged = Merge-RouteAcceptanceEvidenceSnapshots -InitialSnapshot $InitialSnapshot -FinalSnapshot $FinalSnapshot
    $manifest = [pscustomobject][ordered]@{
        schemaVersion = 1
        event = 'filetransfer_route_acceptance_run_manifest'
        runId = $RunId
        matrixMode = $MatrixMode
        executionMode = $ExecutionMode
        artifactRoot = [System.IO.Path]::GetFullPath($ArtifactRoot)
        startedUtc = $StartedUtc.ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
        finalizedUtc = $FinalizedUtc.ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
        scenarioNames = @($ScenarioNames)
        source = $merged.source
        runtime = $merged.runtime
        harness = $merged.harness
        bridge = $merged.bridge
        scenarioAttempts = @($ScenarioAttempts)
        validation = $null
    }
    $manifest.validation = Get-RouteAcceptanceEvidenceManifestValidation -Manifest $manifest
    return $manifest
}

function Test-RouteAcceptanceEvidenceManifest {
    param([Parameter(Mandatory = $true)]$Manifest)

    $actual = Get-RouteAcceptanceEvidenceManifestValidation -Manifest $Manifest
    $embedded = Get-RouteAcceptanceEvidencePropertyValue -Object $Manifest -Name 'validation' -DefaultValue $null
    $embeddedMismatch = $false
    if ($null -eq $embedded) {
        $embeddedMismatch = $true
    }
    else {
        $embeddedVerdict = [string](Get-RouteAcceptanceEvidencePropertyValue -Object $embedded -Name 'verdict' -DefaultValue '')
        $embeddedCurrentProof = ConvertTo-RouteAcceptanceEvidenceBoolean -Value (Get-RouteAcceptanceEvidencePropertyValue -Object $embedded -Name 'currentRuntimeProof' -DefaultValue $false)
        $embeddedReasons = @((Get-RouteAcceptanceEvidencePropertyValue -Object $embedded -Name 'reasons' -DefaultValue @()) | ForEach-Object { [string]$_ } | Sort-Object -Unique)
        $actualReasons = @($actual.reasons | ForEach-Object { [string]$_ } | Sort-Object -Unique)
        if ($embeddedVerdict -ne $actual.verdict -or $embeddedCurrentProof -ne $actual.currentRuntimeProof -or ($embeddedReasons -join "`n") -ne ($actualReasons -join "`n")) {
            $embeddedMismatch = $true
        }
    }

    if (-not $embeddedMismatch) {
        return $actual
    }

    $reasons = @($actual.reasons + 'manifest_embedded_validation_mismatch' | Select-Object -Unique)
    return [pscustomobject][ordered]@{
        verdict = 'FAIL'
        provenanceVerdict = 'FAIL'
        cursorIntegrityVerdict = $actual.cursorIntegrityVerdict
        currentRuntimeProof = $false
        reasons = $reasons
        structuralReasons = @($actual.structuralReasons + 'manifest_embedded_validation_mismatch' | Select-Object -Unique)
        provenanceReasons = @($actual.provenanceReasons)
        cursorIntegrityReasons = @($actual.cursorIntegrityReasons)
    }
}

function Write-RouteAcceptanceEvidenceManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Manifest
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $directory = [System.IO.Path]::GetDirectoryName($fullPath)
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $validation = Test-RouteAcceptanceEvidenceManifest -Manifest $Manifest
    $json = $Manifest | ConvertTo-Json -Depth 32
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = [System.IO.FileStream]::new(
        $fullPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }

    $reopened = Test-RouteAcceptanceEvidenceManifestFile -Path $fullPath
    return [pscustomobject][ordered]@{
        path = $fullPath
        sha256 = Get-RouteAcceptanceEvidenceSha256 -Path $fullPath
        validation = $reopened
        preWriteValidation = $validation
    }
}

function Test-RouteAcceptanceEvidenceManifestFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject][ordered]@{
            verdict = 'FAIL'
            provenanceVerdict = 'FAIL'
            cursorIntegrityVerdict = 'FAIL'
            currentRuntimeProof = $false
            reasons = @('manifest_missing')
            structuralReasons = @('manifest_missing')
            provenanceReasons = @()
            cursorIntegrityReasons = @()
        }
    }

    try {
        $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        return Test-RouteAcceptanceEvidenceManifest -Manifest $manifest
    }
    catch {
        return [pscustomobject][ordered]@{
            verdict = 'FAIL'
            provenanceVerdict = 'FAIL'
            cursorIntegrityVerdict = 'FAIL'
            currentRuntimeProof = $false
            reasons = @('manifest_malformed')
            structuralReasons = @('manifest_malformed')
            provenanceReasons = @()
            cursorIntegrityReasons = @()
        }
    }
}
