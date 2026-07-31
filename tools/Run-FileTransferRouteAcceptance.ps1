param(
    [string]$ExePath = ".\artifacts\portable\nLink\win-x64\nLink.exe",
    [string]$WalletPath = ".\artifacts\tuna-poc\wallet-test-nkn.json",
    [string]$WalletPassword = "",
    [string]$SidecarPath = ".\artifacts\portable\nLink\win-x64\tuna\win-x64\nlink-tuna-sidecar.exe",
    [string]$Runtime = "win-x64",
    [string]$ArtifactRoot = "artifacts\filetransfer-route-acceptance",
    [ValidateSet("legacy", "phase4-ab-acceptance", "phase5-analyzer-gui-acceptance")]
    [string]$MatrixMode = "legacy",
    [string]$BaselineManifestPath = "artifacts\filetransfer-route-ab\baseline-lock-v0.7.0-20260524\baseline-manifest.json",
    [double]$GoodputRegressionTolerancePercent = 10D,
    [int]$GoodputOnlyRerunLimit = 1,
    [int]$SetupOnlyRerunLimit = 1,
    [int]$TimeoutSeconds = 900,
    [int]$ProgressTimeoutSeconds = 180,
    [int]$FallbackMaxAttempts = 2,
    [bool]$AllowExternalTransportWarnings = $true,
    [string[]]$ScenarioName = @(),
    [switch]$EvidenceDryRun,
    [string]$EvidenceDryRunFixtureRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'FileTransferRouteAcceptanceEvidence.ps1')

$script:RegularNknGoodputFloorBytesPerSecond = 1500000D
$script:TunaGoodputFloorBytesPerSecond = 4000000D
$script:RunResults = New-Object System.Collections.Generic.List[object]
$script:RouteAcceptanceRunId = ''
$script:RouteAcceptanceExecutionMode = 'live'
$script:RouteAcceptanceEvidence = $null
$script:RouteAcceptanceManifestPath = ''
$script:RouteAcceptanceManifestSha256 = ''
$script:RouteAcceptanceScenarioAttempts = New-Object System.Collections.Generic.List[object]
$script:RouteAcceptanceStartedUtc = [DateTime]::UtcNow
$script:RouteAcceptanceInitialGitState = $null
$script:RouteAcceptanceInitialEvidenceSnapshot = $null
$script:RouteAcceptanceEvidenceRuntimePath = ''
$script:RouteAcceptanceEvidenceBridgeDir = ''
$script:RouteAcceptanceEvidenceHarnessFiles = @()
$script:RouteAcceptanceEvidenceDryRunFixtureRoot = ''
$script:RouteAcceptanceEvidenceCompletionAttempted = $false
$script:RouteAcceptanceEvidenceScenarioNames = @()

function Resolve-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Test-RouteAcceptanceEnvEnabled {
    param([Parameter(Mandatory = $true)][string]$Name)

    $value = [string][System.Environment]::GetEnvironmentVariable($Name)
    return -not [string]::IsNullOrWhiteSpace($value) -and $value -match '^(1|true|yes|on)$'
}

function Get-RouteAcceptanceEnvValue {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$DefaultValue = ''
    )

    $value = [string][System.Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $DefaultValue
    }

    return $value
}

function Invoke-RouteAcceptanceScenarioIdentityScope {
    param(
        [Parameter(Mandatory = $true)][string]$ScenarioName,
        [Parameter(Mandatory = $true)][int]$AttemptIndex,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $oldRunId = $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
    $oldScenarioRunId = $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
    $normalizedScenario = ($ScenarioName.Trim().ToLowerInvariant() -replace '[^a-z0-9_.-]', '_')
    $scenarioRunId = '{0}:{1}:{2}' -f $script:RouteAcceptanceRunId, $normalizedScenario, $AttemptIndex
    try {
        $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = $script:RouteAcceptanceRunId
        $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = $scenarioRunId
        & $Action
    }
    finally {
        if ($null -eq $oldRunId) {
            Remove-Item Env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = $oldRunId
        }

        if ($null -eq $oldScenarioRunId) {
            Remove-Item Env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = $oldScenarioRunId
        }
    }
}

function Add-RouteAcceptanceScenarioAttemptEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$ScenarioName,
        [Parameter(Mandatory = $true)][int]$AttemptIndex,
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    $normalizedScenario = ($ScenarioName.Trim().ToLowerInvariant() -replace '[^a-z0-9_.-]', '_')
    $script:RouteAcceptanceScenarioAttempts.Add([pscustomobject]@{
        ScenarioName = $ScenarioName
        Attempt = $AttemptIndex
        ScenarioRunId = ('{0}:{1}:{2}' -f $script:RouteAcceptanceRunId, $normalizedScenario, $AttemptIndex)
        ArtifactDir = $ArtifactDir
    }) | Out-Null
}

function Get-RouteAcceptanceEvidenceDryRunFixtureRunId {
    param([Parameter(Mandatory = $true)][string]$FixtureRoot)

    $ledger = Get-ChildItem -LiteralPath $FixtureRoot -Recurse -File -Filter 'filetransfer-gui-log-ledger.json' -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        Select-Object -First 1
    if ($null -eq $ledger) {
        return ''
    }

    try {
        $ledgerObject = Get-Content -LiteralPath $ledger.FullName -Raw | ConvertFrom-Json
        $runId = ([string]$ledgerObject.acceptanceRunId).Trim()
        $parsed = [guid]::Empty
        if ([guid]::TryParse($runId, [ref]$parsed) -and $parsed -ne [guid]::Empty) {
            return $runId
        }
    }
    catch {
        return ''
    }

    return ''
}

function Copy-RouteAcceptanceEvidenceDryRunScenarioFixture {
    param(
        [Parameter(Mandatory = $true)][string]$ScenarioName,
        [Parameter(Mandatory = $true)][int]$AttemptIndex,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if ([string]::IsNullOrWhiteSpace($script:RouteAcceptanceEvidenceDryRunFixtureRoot)) {
        throw 'Evidence dry-run fixture root is missing.'
    }

    $fixtureName = if ($AttemptIndex -le 1) { $ScenarioName } else { '{0}-rerun-{1}' -f $ScenarioName, ($AttemptIndex - 1) }
    $source = Join-Path $script:RouteAcceptanceEvidenceDryRunFixtureRoot $fixtureName
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw ("Evidence dry-run scenario fixture is missing: {0}" -f $source)
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in @(Get-ChildItem -LiteralPath $source -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function Get-RouteAcceptanceEvidenceHarnessFiles {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    return @(
        (Join-Path $RepoRoot 'tools\Run-FileTransferRouteAcceptance.ps1'),
        (Join-Path $RepoRoot 'tools\FileTransferRouteAcceptanceEvidence.ps1'),
        (Join-Path $RepoRoot 'tools\Run-FileTransferTunaGuiSmoke.ps1'),
        (Join-Path $RepoRoot 'tools\GuiSmoke-Windows.ps1'),
        (Join-Path $RepoRoot 'tools\GuiSmokeLogLedger.ps1')
    )
}

function New-RouteAcceptanceFakeEvidenceRuntime {
    param([Parameter(Mandatory = $true)][string]$RunRoot)

    $fixtureRoot = Join-Path $RunRoot 'evidence-fixture'
    $bridgeDir = Join-Path $fixtureRoot 'bridge\win-x64'
    New-Item -ItemType Directory -Force -Path $bridgeDir | Out-Null

    $runtimePath = Join-Path $fixtureRoot 'nLink.fake.exe'
    [System.IO.File]::WriteAllText($runtimePath, 'filetransfer-route-acceptance-fake-runtime-v1', [System.Text.UTF8Encoding]::new($false))
    $bridgeIndexPath = Join-Path $bridgeDir 'index.js'
    [System.IO.File]::WriteAllText($bridgeIndexPath, '// filetransfer route acceptance fake bridge v1', [System.Text.UTF8Encoding]::new($false))
    $bridgeHash = Get-RouteAcceptanceEvidenceSha256 -Path $bridgeIndexPath
    [ordered]@{
        manifestVersion = 1
        runtime = 'win-x64'
        bridgeScriptSha256 = $bridgeHash
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $bridgeDir 'bridge-manifest.json') -Encoding UTF8

    return [pscustomobject]@{
        RuntimePath = [System.IO.Path]::GetFullPath($runtimePath)
        BridgeDir = [System.IO.Path]::GetFullPath($bridgeDir)
    }
}

function Write-RouteAcceptanceFakeCursorEvidence {
    param(
        [Parameter(Mandatory = $true)]$Attempt
    )

    $artifactDir = [string]$Attempt.ArtifactDir
    $ledgerPath = Join-Path $artifactDir 'filetransfer-gui-log-ledger.json'
    if (Test-Path -LiteralPath $ledgerPath -PathType Leaf) {
        return
    }

    $retainedPath = Join-Path $artifactDir 'filetransfer-retained-log-slice.log'
    $diagnosticsPath = Join-Path $artifactDir 'filetransfer-retained-analysis-stage-diagnostics.json'
    if (-not (Test-Path -LiteralPath $retainedPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $diagnosticsPath -PathType Leaf)) {
        return
    }

    $ledgerSlicePath = Join-Path $artifactDir 'filetransfer-gui-log-ledger-slice.log'
    [System.IO.File]::Copy($retainedPath, $ledgerSlicePath, $false)
    $diagnostics = Get-Content -LiteralPath $diagnosticsPath -Raw | ConvertFrom-Json
    $lineCount = [int](Get-RouteAcceptanceEvidenceLineCount -Path $ledgerSlicePath)
    $byteCount = [long](Get-Item -LiteralPath $ledgerSlicePath).Length
    $generationKey = '0000000000000000001:fake-ledger-generation'
    $ledgerId = [guid]::NewGuid().ToString('D')
    $consumedIdentities = if ($lineCount -gt 0) {
        @(
            for ($lineOrdinal = 0; $lineOrdinal -lt $lineCount; $lineOrdinal++) {
                "{0}`n{1}" -f $generationKey, $lineOrdinal
            }
        ) | Sort-Object
    }
    else {
        @()
    }
    $consumedIndexSha256 = Get-RouteAcceptanceEvidenceTextSha256 -Value ($consumedIdentities -join [char]31)
    $writerFailureMarkerPath = Join-Path $artifactDir 'filetransfer-gui-log-writer-failure-markers.log'
    [System.IO.File]::WriteAllText($writerFailureMarkerPath, '', [System.Text.UTF8Encoding]::new($false))
    $writerEvidenceSlicePath = Join-Path $artifactDir 'filetransfer-gui-log-writer-evidence-slice.log'
    $attemptTicks = [DateTimeOffset]::UtcNow.UtcTicks
    $writerEvidenceLines = @(
        "[2000-01-01 00:00:00Z] [INFO] [OperationalLog] event=operational_log_writer_started; acceptance_run_id=$($script:RouteAcceptanceRunId); scenario_run_id=$([string]$Attempt.ScenarioRunId); log_writer_id=fake-writer; log_writer_sequence=1; log_writer_batch_generation=1; log_writer_batch_first_sequence=1; log_writer_batch_last_sequence=1; log_writer_attempt_utc_ticks=$attemptTicks",
        "[2000-01-01 00:00:00Z] [INFO] [OperationalLog] event=operational_log_writer_drained; acceptance_run_id=$($script:RouteAcceptanceRunId); scenario_run_id=$([string]$Attempt.ScenarioRunId); last_payload_sequence=1; log_writer_id=fake-writer; log_writer_sequence=2; log_writer_batch_generation=2; log_writer_batch_first_sequence=2; log_writer_batch_last_sequence=2; log_writer_attempt_utc_ticks=$($attemptTicks + 1L)"
    )
    [System.IO.File]::WriteAllText($writerEvidenceSlicePath, ($writerEvidenceLines -join [Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))
    $fakeWriter = [ordered]@{
        writerId = 'fake-writer'
        acceptanceRunId = $script:RouteAcceptanceRunId
        scenarioRunId = [string]$Attempt.ScenarioRunId
        firstSequence = 1
        lastSequence = 2
        observedCount = 2
    }
    $ledger = [ordered]@{
        schemaVersion = 1
        event = 'filetransfer_gui_log_ledger'
        acceptanceRunId = $script:RouteAcceptanceRunId
        scenarioRunId = [string]$Attempt.ScenarioRunId
        ledgerId = $ledgerId
        initial = [ordered]@{
            ledgerId = $ledgerId
            bookmark = 0
            lineCount = 0
            activeGenerationKey = $generationKey
            generations = @([ordered]@{ generationKey = $generationKey; lineCount = 0; consumedLineCount = 0; consumedByteOffset = 0 })
            writers = @()
        }
        final = [ordered]@{
            ledgerId = $ledgerId
            bookmark = 0
            lineCount = $lineCount
            activeGenerationKey = $generationKey
            generations = @([ordered]@{ generationKey = $generationKey; lineCount = $lineCount; consumedLineCount = $lineCount; consumedByteOffset = $byteCount })
            writers = @($fakeWriter)
        }
        rotationCount = 0
        transientRescanCount = 0
        gapCount = 0
        duplicateIndexCount = 0
        writerEvidenceVersion = 3
        writerGapCount = 0
        writerDuplicateCount = 0
        writerIdentityMismatchCount = 0
        writerWriteFailureCount = 0
        writerDeferredBatchCount = 0
        writerDeferredLineCount = 0
        writerRecoveredBatchCount = 0
        writerRecoveredLineCount = 0
        writerUnresolvedDeferredBatchCount = 0
        writerUnresolvedDeferredLineCount = 0
        writerTerminalFailureCount = 0
        writerRecoveryMismatchCount = 0
        writerChronologyInversionCount = 0
        writerChronologyCollisionCount = 0
        writerDrainMissingCount = 0
        writerRecoveryVerdict = 'pass'
        writerRecoveryReasons = @()
        writerChronologyVerdict = 'pass'
        writerChronologyReasons = @()
        writerIntegrityVerdict = 'pass'
        writerIntegrityReasons = @()
        writerFailureMarkerFiles = @([ordered]@{
            path = [System.IO.Path]::GetFullPath($writerFailureMarkerPath)
            sha256 = Get-RouteAcceptanceEvidenceSha256 -Path $writerFailureMarkerPath
            markerCount = 0
            deferredMarkerCount = 0
            recoveredMarkerCount = 0
            terminalFailureMarkerCount = 0
        })
        writerMarkerFiles = @([ordered]@{
            path = [System.IO.Path]::GetFullPath($writerFailureMarkerPath)
            sha256 = Get-RouteAcceptanceEvidenceSha256 -Path $writerFailureMarkerPath
            markerCount = 0
            deferredMarkerCount = 0
            recoveredMarkerCount = 0
            terminalFailureMarkerCount = 0
        })
        writerDeferredBatches = @()
        writerDrains = @([ordered]@{ writerId = 'fake-writer'; drainSequence = 2; finalSequence = 2; valid = $true })
        writerEvidenceSlice = [ordered]@{
            path = [System.IO.Path]::GetFullPath($writerEvidenceSlicePath)
            sha256 = Get-RouteAcceptanceEvidenceSha256 -Path $writerEvidenceSlicePath
            lineCount = 2
        }
        consumedIndexCount = $lineCount
        consumedIndexSha256 = $consumedIndexSha256
        consumedIndexRanges = @([ordered]@{
            generationKey = $generationKey
            firstLineOrdinal = $(if ($lineCount -gt 0) { 0 } else { -1 })
            lastLineOrdinal = $(if ($lineCount -gt 0) { $lineCount - 1 } else { -1 })
            count = $lineCount
        })
        syncCount = 1
        failureCode = '(none)'
        failureDetail = '(none)'
        retainedSlice = [ordered]@{
            path = [System.IO.Path]::GetFullPath($ledgerSlicePath)
            sha256 = Get-RouteAcceptanceEvidenceSha256 -Path $ledgerSlicePath
            lineCount = $lineCount
        }
        cursorIntegrityVerdict = 'pass'
        cursorIntegrityReasons = @()
    }

    $json = $ledger | ConvertTo-Json -Depth 12
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = $null
    try {
        $stream = [System.IO.FileStream]::new(
            $ledgerPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Test-RouteAcceptanceIncompleteProofRuntimeCandidate {
    param([AllowEmptyString()][string]$Candidate)

    return $Candidate -in @(
        'regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved',
        'regular_v4_frontier_repair_successor_pressure_install_missing',
        'regular_v4_frontier_repair_settlement_disposition_unverifiable',
        'regular_v4_frontier_repair_pressure_authority_lifecycle_split',
        'regular_v4_frontier_repair_pressure_recovery_handoff_missing',
        'regular_v4_frontier_repair_projection_reauthorization_missing_after_recovery',
        'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete',
        'runtime_unlock_precommit_probe_start_handoff_missing',
        'runtime_unlock_precommit_fallback_recovery_handoff_suppressed')
}

function Test-RouteAcceptanceConclusiveRouteRuntimeCandidate {
    param(
        [AllowEmptyString()][string]$Candidate,
        [AllowEmptyString()][string]$SemanticFailureDomain
    )

    return $Candidate -in @(
            'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing',
            'regular_v4_frontier_tail_repair_state_publication_missing') -and
        [string]::Equals(
            $SemanticFailureDomain,
            'route_runtime',
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Set-RouteAcceptanceResultEvidence {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [AllowEmptyCollection()][object[]]$ScenarioEvidence = @()
    )

    $evidence = Get-RouteAcceptanceEvidenceSummaryState
    $Result.manifestPresent = [bool]$evidence.ManifestPresent
    $Result.manifestPath = [string]$evidence.ManifestPath
    $Result.manifestSha256 = [string]$evidence.ManifestSha256
    $Result.manifestValidationVerdict = [string]$evidence.ManifestValidationVerdict
    $Result.provenanceVerdict = [string]$evidence.ProvenanceVerdict
    $Result.provenanceFailureReasons = @($evidence.ProvenanceFailureReasons)

    $resultArtifactDir = [System.IO.Path]::GetFullPath([string]$Result.artifactDir)
    $selectedAttempt = @($ScenarioEvidence | Where-Object {
        [string]::Equals(
            [System.IO.Path]::GetFullPath([string]$_.artifactDir),
            $resultArtifactDir,
            [System.StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1)
    if ($selectedAttempt.Count -gt 0) {
        $Result.cursorIntegrityVerdict = ([string]$selectedAttempt[0].logLedger.cursorIntegrityVerdict).ToLowerInvariant()
        $Result.writerEvidenceVersion = [long]$selectedAttempt[0].logLedger.writerEvidenceVersion
        $Result.writerIntegrityVerdict = ([string]$selectedAttempt[0].logLedger.writerIntegrityVerdict).ToLowerInvariant()
        $Result.writerGapCount = [long]$selectedAttempt[0].logLedger.writerGapCount
        $Result.writerDuplicateCount = [long]$selectedAttempt[0].logLedger.writerDuplicateCount
        $Result.writerIdentityMismatchCount = [long]$selectedAttempt[0].logLedger.writerIdentityMismatchCount
        $Result.writerWriteFailureCount = [long]$selectedAttempt[0].logLedger.writerWriteFailureCount
        $Result.writerDeferredBatchCount = [long]$selectedAttempt[0].logLedger.writerDeferredBatchCount
        $Result.writerRecoveredBatchCount = [long]$selectedAttempt[0].logLedger.writerRecoveredBatchCount
        $Result.writerUnresolvedDeferredBatchCount = [long]$selectedAttempt[0].logLedger.writerUnresolvedDeferredBatchCount
        $Result.writerTerminalFailureCount = [long]$selectedAttempt[0].logLedger.writerTerminalFailureCount
        $Result.writerRecoveryMismatchCount = [long]$selectedAttempt[0].logLedger.writerRecoveryMismatchCount
        $Result.writerChronologyInversionCount = [long]$selectedAttempt[0].logLedger.writerChronologyInversionCount
        $Result.writerChronologyCollisionCount = [long]$selectedAttempt[0].logLedger.writerChronologyCollisionCount
        $Result.writerDrainMissingCount = [long]$selectedAttempt[0].logLedger.writerDrainMissingCount
        $Result.writerRecoveryVerdict = ([string]$selectedAttempt[0].logLedger.writerRecoveryVerdict).ToLowerInvariant()
        $Result.writerChronologyVerdict = ([string]$selectedAttempt[0].logLedger.writerChronologyVerdict).ToLowerInvariant()
        $cursorReasons = @($selectedAttempt[0].logLedger.cursorIntegrityReasons | ForEach-Object { [string]$_ })
    }
    else {
        $Result.cursorIntegrityVerdict = 'missing'
        $Result.writerEvidenceVersion = 0L
        $Result.writerIntegrityVerdict = 'missing'
        $Result.writerGapCount = 0L
        $Result.writerDuplicateCount = 0L
        $Result.writerIdentityMismatchCount = 0L
        $Result.writerWriteFailureCount = 0L
        $Result.writerDeferredBatchCount = 0L
        $Result.writerRecoveredBatchCount = 0L
        $Result.writerUnresolvedDeferredBatchCount = 0L
        $Result.writerTerminalFailureCount = 0L
        $Result.writerRecoveryMismatchCount = 0L
        $Result.writerChronologyInversionCount = 0L
        $Result.writerChronologyCollisionCount = 0L
        $Result.writerDrainMissingCount = 0L
        $Result.writerRecoveryVerdict = 'missing'
        $Result.writerChronologyVerdict = 'missing'
        $cursorReasons = @('cursor_evidence_missing')
    }

    $proofReasons = @(
        @($evidence.CurrentRuntimeProofReasons) +
        @($cursorReasons) |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
            ForEach-Object { [string]$_ } |
            Select-Object -Unique
    )
    $Result.currentRuntimeProof = [bool]$evidence.CurrentRuntimeProof -and
        [string]::Equals([string]$Result.cursorIntegrityVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)
    $Result.currentRuntimeProofReasons = if ($Result.currentRuntimeProof) { @() } else { $proofReasons }

    $retainedPath = Join-Path $resultArtifactDir 'filetransfer-retained-log-slice.log'
    $summaryPresent = (Test-Path -LiteralPath (Join-Path $resultArtifactDir 'filetransfer-tuna-gui-summary.json') -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $resultArtifactDir 'filetransfer-live-nkn-summary.json') -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $resultArtifactDir 'filetransfer-live-nkn-summary.txt') -PathType Leaf)
    $Result.transferEvidenceStatus = if ([bool]$Result.completed) {
        'complete'
    }
    elseif ((Test-Path -LiteralPath $retainedPath -PathType Leaf) -or $summaryPresent) {
        'incomplete'
    }
    else {
        'not_started'
    }

    $conclusiveRouteRuntimeCandidate = Test-RouteAcceptanceConclusiveRouteRuntimeCandidate `
        -Candidate ([string]$Result.runtimeFailureCandidate) `
        -SemanticFailureDomain ([string]$Result.semanticFailureDomain)
    if (-not [bool]$Result.currentRuntimeProof -and
        -not ($script:RouteAcceptanceExecutionMode -eq 'fake' -and $conclusiveRouteRuntimeCandidate)) {
        $Result.runtimeCorrectnessVerdict = 'incomplete_proof'
        $Result.evidenceClassification = 'incomplete_proof'
    }
    elseif ($conclusiveRouteRuntimeCandidate) {
        $Result.runtimeCorrectnessVerdict = 'fail'
        $Result.evidenceClassification = 'runtime_failure'
    }
    elseif ((Test-RouteAcceptanceIncompleteProofRuntimeCandidate -Candidate ([string]$Result.runtimeFailureCandidate)) -or
        -not [string]::Equals([string]$Result.transferEvidenceStatus, 'complete', [System.StringComparison]::OrdinalIgnoreCase)) {
        $Result.runtimeCorrectnessVerdict = 'incomplete_proof'
        $Result.evidenceClassification = 'incomplete_proof'
    }
    elseif ((-not [string]::IsNullOrWhiteSpace([string]$Result.runtimeFailureCandidate) -and
            [string]$Result.runtimeFailureCandidate -notin @('(none)', 'none')) -or
        @($Result.failures | Where-Object { -not (Test-Phase4PerformanceFailureLine -Line ([string]$_)) }).Count -gt 0) {
        $Result.runtimeCorrectnessVerdict = 'fail'
        $Result.evidenceClassification = 'runtime_failure'
    }
    else {
        $Result.runtimeCorrectnessVerdict = 'pass'
        $Result.evidenceClassification = 'current_runtime_proof'
    }
}

function Complete-RouteAcceptanceRunEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)][string[]]$ScenarioNames
    )

    if ($script:RouteAcceptanceEvidenceCompletionAttempted) {
        return
    }

    $script:RouteAcceptanceEvidenceCompletionAttempted = $true
    $manifestPath = Join-Path $RunRoot 'route-acceptance-run-manifest.json'
    $scenarioEvidence = @()
    try {
        if ($null -eq $script:RouteAcceptanceInitialEvidenceSnapshot) {
            throw 'initial_evidence_snapshot_missing'
        }

        if ($script:RouteAcceptanceExecutionMode -eq 'fake') {
            foreach ($attempt in @($script:RouteAcceptanceScenarioAttempts.ToArray())) {
                Write-RouteAcceptanceFakeCursorEvidence -Attempt $attempt
            }
        }

        $scenarioEvidence = @(
            foreach ($attempt in @($script:RouteAcceptanceScenarioAttempts.ToArray())) {
                $artifactDir = [string]$attempt.ArtifactDir
                $ledgerPath = Join-Path $artifactDir 'filetransfer-gui-log-ledger.json'
                $diagnosticsPath = Join-Path $artifactDir 'filetransfer-retained-analysis-stage-diagnostics.json'
                $analyzerInputPath = Join-Path $artifactDir 'filetransfer-retained-log-slice.log'
                $evidenceFinalizationTimeoutPath = Join-Path $artifactDir 'filetransfer-gui-evidence-finalization-timeout.json'
                $artifactFinalized = (Test-Path -LiteralPath $ledgerPath -PathType Leaf) -and
                    (Test-Path -LiteralPath $diagnosticsPath -PathType Leaf) -and
                    (Test-Path -LiteralPath $analyzerInputPath -PathType Leaf) -and
                    -not (Test-Path -LiteralPath $evidenceFinalizationTimeoutPath -PathType Leaf)
                Get-RouteAcceptanceScenarioEvidence `
                    -ScenarioName ([string]$attempt.ScenarioName) `
                    -Attempt ([int]$attempt.Attempt) `
                    -AcceptanceRunId $script:RouteAcceptanceRunId `
                    -ScenarioRunId ([string]$attempt.ScenarioRunId) `
                    -ArtifactDir $artifactDir `
                    -ArtifactFinalized:$artifactFinalized `
                    -LogLedgerPath $ledgerPath `
                    -AnalyzerDiagnosticsPath $diagnosticsPath `
                    -AnalyzerInputPath $analyzerInputPath
            }
        )

        $finalGitState = Get-RouteAcceptanceEvidenceGitState -RepoRoot $RepoRoot
        $finalSnapshot = New-RouteAcceptanceEvidenceSnapshot `
            -RepoRoot $RepoRoot `
            -PortableExecutablePath $script:RouteAcceptanceEvidenceRuntimePath `
            -HarnessFiles $script:RouteAcceptanceEvidenceHarnessFiles `
            -BridgeDir $script:RouteAcceptanceEvidenceBridgeDir `
            -GitState $finalGitState
        $manifest = New-RouteAcceptanceEvidenceManifest `
            -RunId $script:RouteAcceptanceRunId `
            -MatrixMode $MatrixMode `
            -ExecutionMode $script:RouteAcceptanceExecutionMode `
            -ArtifactRoot $RunRoot `
            -StartedUtc $script:RouteAcceptanceStartedUtc `
            -FinalizedUtc ([DateTime]::UtcNow) `
            -ScenarioNames $ScenarioNames `
            -InitialSnapshot $script:RouteAcceptanceInitialEvidenceSnapshot `
            -FinalSnapshot $finalSnapshot `
            -ScenarioAttempts $scenarioEvidence
        $writeResult = Write-RouteAcceptanceEvidenceManifest -Path $manifestPath -Manifest $manifest
        $validation = Test-RouteAcceptanceEvidenceManifestFile -Path $manifestPath
        $script:RouteAcceptanceManifestPath = [System.IO.Path]::GetFullPath($manifestPath)
        $script:RouteAcceptanceManifestSha256 = [string]$writeResult.sha256
        $script:RouteAcceptanceEvidence = [pscustomobject]@{
            ManifestPresent = $true
            ManifestPath = $script:RouteAcceptanceManifestPath
            ManifestSha256 = $script:RouteAcceptanceManifestSha256
            ManifestValidationVerdict = ([string]$validation.verdict).ToLowerInvariant()
            ProvenanceVerdict = ([string]$validation.provenanceVerdict).ToLowerInvariant()
            ProvenanceFailureReasons = @($validation.structuralReasons + $validation.provenanceReasons | Select-Object -Unique)
            CurrentRuntimeProof = [bool]$validation.currentRuntimeProof
            CurrentRuntimeProofReasons = @($validation.reasons | Select-Object -Unique)
        }
    }
    catch {
        $failureReason = (($_ | Out-String).Trim() -replace '[\r\n]+', ' ')
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -and
            $null -ne $script:RouteAcceptanceInitialEvidenceSnapshot) {
            try {
                $failureManifest = New-RouteAcceptanceEvidenceManifest `
                    -RunId $script:RouteAcceptanceRunId `
                    -MatrixMode $MatrixMode `
                    -ExecutionMode $script:RouteAcceptanceExecutionMode `
                    -ArtifactRoot $RunRoot `
                    -StartedUtc $script:RouteAcceptanceStartedUtc `
                    -FinalizedUtc ([DateTime]::UtcNow) `
                    -ScenarioNames $ScenarioNames `
                    -InitialSnapshot $script:RouteAcceptanceInitialEvidenceSnapshot `
                    -FinalSnapshot $script:RouteAcceptanceInitialEvidenceSnapshot `
                    -ScenarioAttempts @($scenarioEvidence)
                Write-RouteAcceptanceEvidenceManifest -Path $manifestPath -Manifest $failureManifest | Out-Null
            }
            catch {
                $fallbackFailure = (($_ | Out-String).Trim() -replace '[\r\n]+', ' ')
                $failureReason = "{0}; fail_closed_manifest_write_failed={1}" -f $failureReason, $fallbackFailure
            }
        }

        $manifestPresent = Test-Path -LiteralPath $manifestPath -PathType Leaf
        $manifestSha = if ($manifestPresent) { Get-RouteAcceptanceEvidenceSha256 -Path $manifestPath } else { '' }
        $script:RouteAcceptanceEvidence = [pscustomobject]@{
            ManifestPresent = $manifestPresent
            ManifestPath = if ($manifestPresent) { [System.IO.Path]::GetFullPath($manifestPath) } else { '' }
            ManifestSha256 = $manifestSha
            ManifestValidationVerdict = 'fail'
            ProvenanceVerdict = 'fail'
            ProvenanceFailureReasons = @('manifest_finalize_failed')
            CurrentRuntimeProof = $false
            CurrentRuntimeProofReasons = @('manifest_finalize_failed', $failureReason)
        }
        Write-Warning ("Route-acceptance evidence finalization failed closed: {0}" -f $failureReason)
    }

    foreach ($result in @($script:RunResults.ToArray())) {
        Set-RouteAcceptanceResultEvidence -Result $result -ScenarioEvidence $scenarioEvidence
    }
}

function Get-RouteAcceptanceAppProcessesForExePath {
    param([string]$ResolvedExePath)

    if ([string]::IsNullOrWhiteSpace($ResolvedExePath)) {
        return @()
    }

    $expectedPath = ''
    try {
        $expectedPath = [System.IO.Path]::GetFullPath($ResolvedExePath)
    }
    catch {
        return @()
    }

    $expectedProcessName = [System.IO.Path]::GetFileNameWithoutExtension($expectedPath)
    if ([string]::IsNullOrWhiteSpace($expectedProcessName)) {
        return @()
    }

    $matches = New-Object System.Collections.Generic.List[object]
    foreach ($process in @(Get-Process -Name $expectedProcessName -ErrorAction SilentlyContinue)) {
        $actualPath = ''
        try {
            $actualPath = [string]$process.Path
        }
        catch {
            $actualPath = ''
        }

        if ([string]::IsNullOrWhiteSpace($actualPath)) {
            try {
                $cim = Get-CimInstance Win32_Process -Filter ("ProcessId = {0}" -f [int]$process.Id) -ErrorAction SilentlyContinue
                if ($null -ne $cim) {
                    $actualPath = [string]$cim.ExecutablePath
                }
            }
            catch {
                $actualPath = ''
            }
        }

        if ([string]::IsNullOrWhiteSpace($actualPath)) {
            continue
        }

        $actualFullPath = ''
        try {
            $actualFullPath = [System.IO.Path]::GetFullPath($actualPath)
        }
        catch {
            continue
        }

        if ([string]::Equals($actualFullPath, $expectedPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            $matches.Add([pscustomobject]@{
                Id = [int]$process.Id
                ProcessName = [string]$process.ProcessName
                Path = $actualFullPath
                MainWindowTitle = [string]$process.MainWindowTitle
                StartTime = try { $process.StartTime.ToUniversalTime().ToString('o') } catch { '' }
            }) | Out-Null
        }
    }

    return @($matches.ToArray())
}

function Write-RouteAcceptanceProcessCleanupArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$Reason,
        [AllowEmptyCollection()][object[]]$Before,
        [AllowEmptyCollection()][object[]]$After
    )

    if ([string]::IsNullOrWhiteSpace($ArtifactDir)) {
        return
    }

    try {
        New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
        $safeReason = ([string]$Reason) -replace '[^A-Za-z0-9_.-]', '_'
        if ([string]::IsNullOrWhiteSpace($safeReason)) {
            $safeReason = 'unknown'
        }

        $summary = [ordered]@{
            event = 'route_acceptance_app_process_cleanup'
            reason = $Reason
            beforeCount = @($Before).Count
            afterCount = @($After).Count
            cleanedAny = @($Before).Count -gt 0
            remainingAny = @($After).Count -gt 0
            before = @($Before)
            after = @($After)
        }

        $jsonPath = Join-Path $ArtifactDir ("process-cleanup-{0}.json" -f $safeReason)
        $txtPath = Join-Path $ArtifactDir ("process-cleanup-{0}.txt" -f $safeReason)
        $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add(("reason={0}" -f $Reason)) | Out-Null
        $lines.Add(("before_count={0}" -f @($Before).Count)) | Out-Null
        $lines.Add(("after_count={0}" -f @($After).Count)) | Out-Null
        foreach ($item in @($Before)) {
            $lines.Add(("before pid={0} name={1} title={2} path={3}" -f $item.Id, $item.ProcessName, $item.MainWindowTitle, $item.Path)) | Out-Null
        }
        foreach ($item in @($After)) {
            $lines.Add(("after pid={0} name={1} title={2} path={3}" -f $item.Id, $item.ProcessName, $item.MainWindowTitle, $item.Path)) | Out-Null
        }
        $lines | Set-Content -LiteralPath $txtPath -Encoding UTF8
    }
    catch {
        Write-Warning ("Failed to write process cleanup artifacts: {0}" -f $_.Exception.Message)
    }
}

function Assert-RouteAcceptanceCleanAppProcessState {
    param(
        [Parameter(Mandatory = $true)][string]$ResolvedExePath,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    if ([string]::IsNullOrWhiteSpace($ResolvedExePath)) {
        return
    }

    $before = @(Get-RouteAcceptanceAppProcessesForExePath -ResolvedExePath $ResolvedExePath)
    if ($before.Count -le 0) {
        return
    }

    foreach ($process in @($before)) {
        try {
            $live = Get-Process -Id ([int]$process.Id) -ErrorAction SilentlyContinue
            if ($null -ne $live -and -not $live.HasExited) {
                [void]$live.CloseMainWindow()
            }
        }
        catch {
        }
    }

    Start-Sleep -Seconds 3
    $afterClose = @(Get-RouteAcceptanceAppProcessesForExePath -ResolvedExePath $ResolvedExePath)
    foreach ($process in @($afterClose)) {
        try {
            Stop-Process -Id ([int]$process.Id) -Force -ErrorAction SilentlyContinue
        }
        catch {
        }
    }

    Start-Sleep -Seconds 1
    $after = @(Get-RouteAcceptanceAppProcessesForExePath -ResolvedExePath $ResolvedExePath)
    Write-RouteAcceptanceProcessCleanupArtifacts -ArtifactDir $ArtifactDir -Reason $Reason -Before $before -After $after

    if ($after.Count -gt 0) {
        throw ("nLink app process cleanup failed before/after route acceptance step '{0}'; remaining_count={1}; artifact_dir={2}" -f $Reason, $after.Count, $ArtifactDir)
    }
}

function Assert-RouteAcceptanceBridgeBundleRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$BridgeDir
    )

    $indexJs = Join-Path $BridgeDir 'index.js'
    $nodeExe = Join-Path $BridgeDir 'node.exe'
    $manifest = Join-Path $BridgeDir 'bridge-manifest.json'
    $dependencies = Join-Path $BridgeDir 'bridge-dependencies.json'
    $packageJson = Join-Path $BridgeDir 'package.json'
    $packageLock = Join-Path $BridgeDir 'package-lock.json'

    foreach ($requiredPath in @($indexJs, $nodeExe, $manifest, $dependencies, $packageJson, $packageLock)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw ("NKN bridge runtime is incomplete; missing '{0}'." -f $requiredPath)
        }
    }
}

function Write-RouteAcceptanceBridgeRuntimeArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$Reason,
        [Parameter(Mandatory = $true)][string]$ExeDir,
        [Parameter(Mandatory = $true)][string]$BridgeDir,
        [string]$SourceBridgeDir = '',
        [string]$Failure = ''
    )

    try {
        New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
        $safeReason = ([string]$Reason) -replace '[^A-Za-z0-9_.-]', '_'
        if ([string]::IsNullOrWhiteSpace($safeReason)) {
            $safeReason = 'unknown'
        }

        $bridgeIndex = Join-Path $BridgeDir 'index.js'
        $bridgeNode = Join-Path $BridgeDir 'node.exe'
        $summary = [ordered]@{
            event = 'route_acceptance_bridge_runtime_staging'
            reason = $Reason
            exeDir = $ExeDir
            bridgeDir = $BridgeDir
            sourceBridgeDir = $SourceBridgeDir
            bridgeIndexExists = Test-Path -LiteralPath $bridgeIndex -PathType Leaf
            nodeExists = Test-Path -LiteralPath $bridgeNode -PathType Leaf
            failure = $Failure
        }

        $jsonPath = Join-Path $ArtifactDir ("bridge-runtime-{0}.json" -f $safeReason)
        $txtPath = Join-Path $ArtifactDir ("bridge-runtime-{0}.txt" -f $safeReason)
        $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

        $lines = @(
            ("reason={0}" -f $Reason),
            ("exe_dir={0}" -f $ExeDir),
            ("bridge_dir={0}" -f $BridgeDir),
            ("source_bridge_dir={0}" -f $SourceBridgeDir),
            ("bridge_index_exists={0}" -f $summary.bridgeIndexExists),
            ("node_exists={0}" -f $summary.nodeExists),
            ("failure={0}" -f $Failure)
        )
        $lines | Set-Content -LiteralPath $txtPath -Encoding UTF8
    }
    catch {
        Write-Warning ("Failed to write bridge runtime staging artifact: {0}" -f $_.Exception.Message)
    }
}

function Ensure-RouteAcceptancePackagedBridgeRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ResolvedExePath,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [string]$Runtime = 'win-x64'
    )

    $exePath = (Resolve-Path -LiteralPath $ResolvedExePath).Path
    $exeDir = [System.IO.Path]::GetDirectoryName($exePath)
    $bridgeDir = Join-Path (Join-Path $exeDir 'bridge') $Runtime

    try {
        Assert-RouteAcceptanceBridgeBundleRuntime -BridgeDir $bridgeDir
        Write-RouteAcceptanceBridgeRuntimeArtifact -ArtifactDir $ArtifactDir -Reason 'already_packaged' -ExeDir $exeDir -BridgeDir $bridgeDir
        return $bridgeDir
    }
    catch {
        $candidateBridgeDirs = @(
            (Join-Path (Join-Path $RepoRoot 'artifacts\bridge') $Runtime),
            (Join-Path (Join-Path $RepoRoot 'artifacts\portable\nLink\win-x64\bridge') $Runtime)
        )

        foreach ($candidateBridgeDir in $candidateBridgeDirs) {
            try {
                Assert-RouteAcceptanceBridgeBundleRuntime -BridgeDir $candidateBridgeDir

                $bridgeRoot = Join-Path $exeDir 'bridge'
                if (Test-Path -LiteralPath $bridgeRoot) {
                    Remove-Item -Recurse -Force -LiteralPath $bridgeRoot
                }

                New-Item -ItemType Directory -Force -Path $bridgeDir | Out-Null
                Get-ChildItem -LiteralPath $candidateBridgeDir -Force | Copy-Item -Recurse -Force -Destination $bridgeDir
                Assert-RouteAcceptanceBridgeBundleRuntime -BridgeDir $bridgeDir
                Write-RouteAcceptanceBridgeRuntimeArtifact -ArtifactDir $ArtifactDir -Reason 'staged_from_repo_artifact' -ExeDir $exeDir -BridgeDir $bridgeDir -SourceBridgeDir $candidateBridgeDir
                Write-Host ("[FileTransfer Acceptance] Staged NKN bridge runtime into app folder: {0}" -f $bridgeDir) -ForegroundColor DarkCyan
                return $bridgeDir
            }
            catch {
                continue
            }
        }

        $failure = $_.Exception.Message
        Write-RouteAcceptanceBridgeRuntimeArtifact -ArtifactDir $ArtifactDir -Reason 'missing_bridge_runtime' -ExeDir $exeDir -BridgeDir $bridgeDir -Failure $failure
        throw ("Route acceptance app is missing the packaged NKN bridge runtime for {0}. Build portable/installer first or stage artifacts\\bridge\\{0}; app_dir={1}; last_error={2}" -f $Runtime, $exeDir, $failure)
    }
}

function ConvertTo-RouteAcceptanceDouble {
    param(
        [AllowNull()]$Value,
        [double]$DefaultValue = 0
    )

    if ($null -eq $Value) {
        return $DefaultValue
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $DefaultValue
    }

    $parsed = 0.0
    if ([double]::TryParse($text, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }

    return $DefaultValue
}

function ConvertTo-RouteAcceptanceInt {
    param(
        [AllowNull()]$Value,
        [int]$DefaultValue = 0
    )

    if ($null -eq $Value) {
        return $DefaultValue
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $DefaultValue
    }

    $parsed = 0
    if ([int]::TryParse($text, [System.Globalization.NumberStyles]::Integer, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }

    return $DefaultValue
}

function ConvertTo-RouteAcceptanceBool {
    param([AllowNull()]$Value)

    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [bool]) {
        return [bool]$Value
    }

    $text = ([string]$Value).Trim()
    return $text -match '^(1|true|yes|on)$'
}

function Get-JsonPropertyValue {
    param(
        [AllowNull()]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowNull()]$DefaultValue = $null
    )

    if ($null -eq $Object) {
        return $DefaultValue
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $DefaultValue
    }

    return $property.Value
}

function Resolve-RouteAcceptancePath {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function New-RouteAcceptanceTimestampedRoot {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$RequestedRoot
    )

    $root = Resolve-RouteAcceptancePath -RepoRoot $RepoRoot -Path $RequestedRoot
    $repoArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'artifacts'))
    if (-not $root.StartsWith($repoArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "ArtifactRoot must resolve under repo artifacts/: $root"
    }

    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $timestamp = Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TIMESTAMP'
    if ([string]::IsNullOrWhiteSpace($timestamp)) {
        $timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    }

    $candidate = Join-Path $root $timestamp
    $index = 1
    while (Test-Path -LiteralPath $candidate) {
        $candidate = Join-Path $root ("{0}-{1}" -f $timestamp, $index)
        $index++
    }

    New-Item -ItemType Directory -Force -Path $candidate | Out-Null
    return $candidate
}

function New-RouteAcceptanceRunResult {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$ExpectedRoute,
        [Parameter(Mandatory = $true)][int]$ExpectedProtocol
    )

    return [pscustomobject]@{
        name = $Name
        artifactDir = $ArtifactDir
        expectedRoute = $ExpectedRoute
        expectedProtocol = $ExpectedProtocol
        route = '(unknown)'
        finalRoute = '(unknown)'
        protocol = '(unknown)'
        runtimeProfile = '(unknown)'
        bridgeRecoveryPolicy = '(unknown)'
        routeConsistencyVerdict = '(missing)'
        selectedRouteSequence = @()
        selectedRouteChanges = @()
        liveRouteEpochRouteChanges = @()
        completed = $false
        integrityOk = $false
        shaOk = $false
        goodputBytesPerSecond = 0D
        baselineGoodputBytesPerSecond = 0D
        goodputRegressionFloorBytesPerSecond = 0D
        goodputRegressionPercent = 0D
        bridgeBulkSendFailureCount = 0
        operatorVerdict = '(missing)'
        operatorAcceptedWithWarnings = $false
        hardFailureCount = 0
        warningCount = 0
        warningKinds = @()
        warningCapExceededKinds = '(none)'
        environmentalClassification = '(none)'
        manifestPresent = $false
        manifestPath = ''
        manifestSha256 = ''
        manifestValidationVerdict = 'missing'
        provenanceVerdict = 'missing'
        provenanceFailureReasons = @()
        cursorIntegrityVerdict = 'missing'
        writerEvidenceVersion = 0L
        writerIntegrityVerdict = 'missing'
        writerGapCount = 0L
        writerDuplicateCount = 0L
        writerIdentityMismatchCount = 0L
        writerWriteFailureCount = 0L
        writerDeferredBatchCount = 0L
        writerRecoveredBatchCount = 0L
        writerUnresolvedDeferredBatchCount = 0L
        writerTerminalFailureCount = 0L
        writerRecoveryMismatchCount = 0L
        writerChronologyInversionCount = 0L
        writerChronologyCollisionCount = 0L
        writerDrainMissingCount = 0L
        writerRecoveryVerdict = 'missing'
        writerChronologyVerdict = 'missing'
        currentRuntimeProof = $false
        currentRuntimeProofReasons = @('manifest_missing')
        transferEvidenceStatus = 'not_started'
        runtimeCorrectnessVerdict = 'incomplete_proof'
        runtimeFailureCandidate = '(none)'
        runtimeUnlockRetryDispatchAuthorityGrantedRawCount = 0
        runtimeUnlockRetryDispatchAuthorityGrantedCount = 0
        runtimeUnlockRetryDispatchPostGrantHoldCount = 0
        runtimeUnlockRetryDispatchPostGrantHoldRawCount = 0
        runtimeUnlockRetryDispatchPostGrantHoldDistinctCount = 0
        runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount = 0
        runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount = 0
        runtimeUnlockRetryDispatchPostGrantHoldVerdict = 'none'
        runtimeUnlockPreCommitProbeStartHandoffMissingRawCount = 0
        runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount = 0
        runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount = 0
        runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount = 0
        runtimeUnlockPreCommitProbeStartHandoffVerdict = 'none'
        runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount = 0
        runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount = 0
        runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount = 0
        runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount = 0
        runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict = 'none'
        runtimeUnlockRetryRegularV4RecoverySettlementMissingRawCount = 0
        runtimeUnlockRetryRegularV4RecoverySettlementMissingDistinctCount = 0
        runtimeUnlockRetryRegularV4RecoverySettlementMissingRecoveredCount = 0
        runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount = 0
        runtimeUnlockRetryRegularV4RecoverySettlementVerdict = 'none'
        runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount = 0
        runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount = 0
        runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount = 0
        runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount = 0
        runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict = 'none'
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount = 0
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount = 0
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount = 0
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount = 0
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict = 'none'
        regularV4FrontierRepairMissingRangeNotPreservedRawCount = 0
        regularV4FrontierRepairMissingRangeNotPreservedDistinctCount = 0
        regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount = 0
        regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount = 0
        regularV4FrontierRepairMissingRangePreservationVerdict = 'none'
        regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount = 0
        regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount = 0
        regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount = 0
        regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount = 0
        regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict = 'none'
        regularV4FrontierRepairSuccessorPressureInstallMissingRawCount = 0
        regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount = 0
        regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount = 0
        regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount = 0
        regularV4FrontierRepairSuccessorPressureInstallVerdict = 'none'
        regularV4FrontierRepairSettlementDispositionUnverifiableRawCount = 0
        regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount = 0
        regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount = 0
        regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount = 0
        regularV4FrontierRepairSettlementDispositionVerdict = 'none'
        regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount = 0
        regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount = 0
        regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount = 0
        regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount = 0
        regularV4FrontierRepairPressureAuthorityLifecycleSplitVerdict = 'none'
        regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount = 0
        regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount = 0
        regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount = 0
        regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount = 0
        regularV4FrontierRepairPressureRecoveryHandoffVerdict = 'none'
        regularV4FrontierRepairProjectionReauthorizationMissingRawCount = 0
        regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount = 0
        regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount = 0
        regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount = 0
        regularV4FrontierRepairProjectionReauthorizationVerdict = 'none'
        regularV4FrontierTailRepairStatePublicationMissingRawCount = 0
        regularV4FrontierTailRepairStatePublicationMissingDistinctCount = 0
        regularV4FrontierTailRepairStatePublicationMissingRecoveredCount = 0
        regularV4FrontierTailRepairStatePublicationMissingUnresolvedCount = 0
        regularV4FrontierTailRepairStatePublicationVerdict = 'none'
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount = 0
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount = 0
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount = 0
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount = 0
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict = 'none'
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRawCount = 0
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount = 0
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRecoveredCount = 0
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount = 0
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffVerdict = 'none'
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount = 0
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount = 0
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount = 0
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount = 0
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationVerdict = 'none'
        fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount = 0
        fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount = 0
        fileTransferBridgeRecoveryExecutionPayloadHoldMissingRecoveredCount = 0
        fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRawCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRecoveredCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount = 0
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRawCount = 0
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount = 0
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRecoveredCount = 0
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount = 0
        fileTransferBridgeRecoveryExecutionPayloadHoldVerdict = 'none'
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount = 0
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount = 0
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRecoveredCount = 0
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount = 0
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainVerdict = 'none'
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount = 0
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount = 0
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationRecoveredCount = 0
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount = 0
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationVerdict = 'none'
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRawCount = 0
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedDistinctCount = 0
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRecoveredCount = 0
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedUnresolvedCount = 0
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldPreservationVerdict = 'none'
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRawCount = 0
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingDistinctCount = 0
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRecoveredCount = 0
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingUnresolvedCount = 0
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffVerdict = 'none'
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckRequestedCount = 0
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDecidedCount = 0
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDiscardedCount = 0
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount = 0
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingDistinctCount = 0
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRecoveredCount = 0
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount = 0
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict = 'none'
        runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRawCount = 0
        runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedDistinctCount = 0
        runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRecoveredCount = 0
        runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedUnresolvedCount = 0
        runtimeUnlockRetryDispatchZeroOfferSuccessorVerdict = 'none'
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount = 0
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount = 0
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount = 0
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount = 0
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict = 'none'
        firstProofChainFailureClass = '(none)'
        firstProofChainFailureEvent = '(none)'
        firstProofChainFailureReason = '(none)'
        firstProofChainFailureSequence = 0L
        firstProofChainFailureSource = '(none)'
        firstProofChainFailureWriterSequence = 0L
        semanticFailureDomain = '(none)'
        evidenceClassification = 'incomplete_proof'
        measurementContaminated = $false
        measurementContaminationReasons = @()
        attemptCount = 1
        retryUsed = $false
        selectedAttempt = 1
        firstFailureReason = ''
        rerunArtifactDir = ''
        rerunFailureReason = ''
        setupFailurePhase = ''
        setupFailureReason = ''
        guiReportedFailurePhase = ''
        guiReportedFailureReason = ''
        guiReportedFailureError = ''
        scenarioIsolationPreflightCount = 0
        scenarioIsolationCleanWindowPassed = 0
        scenarioIsolationFailureReason = ''
        scenarioIsolationArtifactDirs = @()
        controlledRestartAnalysis = $null
        liveRouteEpochProofVerdict = '(missing)'
        fallbackLegAuthorityProofVerdict = '(missing)'
        cleanFallbackLegProofVerdict = '(missing)'
        bridgeLivenessIntegrationVerdict = '(missing)'
        controlPlaneIsolationVerdict = '(missing)'
        fallbackTailBridgeRecoveryGenerationPairingVerdict = 'none'
        fallbackTailBridgeRecoveryGenerationMismatchRawCount = 0
        fallbackTailBridgeRecoveryGenerationMismatchDistinctCount = 0
        fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount = 0
        fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount = 0
        fallbackTailTransportEpochReauthorizationVerdict = 'none'
        fallbackTailTransportEpochReauthorizationMissingRawCount = 0
        fallbackTailTransportEpochReauthorizationMissingDistinctCount = 0
        fallbackTailTransportEpochReauthorizationMissingRecoveredCount = 0
        fallbackTailTransportEpochReauthorizationMissingUnresolvedCount = 0
        runtimeUnlockRegularV4RouteCommitSendDrainVerdict = 'none'
        runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount = 0
        runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount = 0
        runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount = 0
        runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount = 0
        fileTransferReceiveRecoveryOperationVerdict = 'none'
        fileTransferReceiveRecoveryOperationRawCount = 0
        fileTransferReceiveRecoveryOperationDistinctCount = 0
        fileTransferReceiveRecoveryOperationRecoveredCount = 0
        fileTransferReceiveRecoveryOperationUnresolvedCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorVerdict = 'none'
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount = 0
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount = 0
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount = 0
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount = 0
        fileTransferReceiveRecoveryCompletedProvenSuccessorVerdict = 'none'
        regularV4FrontierRepairStalledRawCount = 0
        regularV4FrontierRepairStalledDistinctCount = 0
        regularV4FrontierRepairStalledRecoveredCount = 0
        regularV4FrontierRepairStalledUnresolvedCount = 0
        regularV4FrontierRepairStalledVerdict = 'none'
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRawCount = 0
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount = 0
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRecoveredCount = 0
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount = 0
        runtimeUnlockInitialOfferRetryLifecycleHandoffVerdict = 'none'
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRawCount = 0
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount = 0
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRecoveredCount = 0
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount = 0
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedVerdict = 'none'
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryVerdict = 'none'
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredVerdict = 'none'
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRawCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRecoveredCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredVerdict = 'none'
        sessionLivenessTimeoutCount = 0
        bridgeLivenessStaleDeferralCount = 0
        bridgeLivenessTimeoutDuringValidRecoveryCount = 0
        fallbackReceiverStateDeferredCount = 0
        fallbackReceiverStateCoalescedCount = 0
        fallbackReceiverStateChurnWarningCount = 0
        fallbackReceiverStateChurnRawEventCount = 0
        fallbackReceiverStateChurnEfficiencyClass = 'none'
        failures = New-Object System.Collections.Generic.List[string]
    }
}

function Get-RouteAcceptanceEvidenceSummaryState {
    if ($null -ne $script:RouteAcceptanceEvidence) {
        return $script:RouteAcceptanceEvidence
    }

    return [pscustomobject]@{
        ManifestPresent = $false
        ManifestPath = ''
        ManifestSha256 = ''
        ManifestValidationVerdict = 'missing'
        ProvenanceVerdict = 'missing'
        ProvenanceFailureReasons = @('manifest_missing')
        CurrentRuntimeProof = $false
        CurrentRuntimeProofReasons = @('manifest_missing')
    }
}

function Add-RouteAcceptanceFailure {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $Result.failures.Add($Message) | Out-Null
}

function Assert-RouteAcceptanceRegularNknTransportEvidence {
    param([Parameter(Mandatory = $true)]$Result)

    $retainedLogPath = Join-Path $Result.artifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $retainedLogPath -PathType Leaf)) {
        Add-RouteAcceptanceFailure -Result $Result -Message 'regular NKN retained log missing; cannot prove NKN transport'
        return
    }

    $retainedText = Get-Content -LiteralPath $retainedLogPath -Raw
    if ($retainedText -match '(?i)(Active transport selected:\s*DevLocal|transport=devlocal|address=devlocal\.|target=devlocal\.|helper_identity=devlocal\.)') {
        Add-RouteAcceptanceFailure -Result $Result -Message 'regular NKN artifact used DevLocal transport; rerun with NLINK_TRANSPORT=NKN'
    }

    if ($retainedText -notmatch '(?i)(Active transport selected:\s*NKN|NLINK_TRANSPORT=NKN|transport=nkn|transport_profile=nkn|transport_profile=conservative_nkn_startup)') {
        Add-RouteAcceptanceFailure -Result $Result -Message 'regular NKN artifact lacks positive NKN transport evidence'
    }
}

function Remove-RouteAcceptanceFailuresMatching {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string[]]$Patterns
    )

    for ($index = $Result.failures.Count - 1; $index -ge 0; $index--) {
        $failure = [string]$Result.failures[$index]
        foreach ($pattern in @($Patterns)) {
            if (-not [string]::IsNullOrWhiteSpace($pattern) -and
                $failure.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $Result.failures.RemoveAt($index)
                break
            }
        }
    }
}

function Read-RouteAcceptanceKeyValueArtifact {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $values
    }

    foreach ($line in @(Get-Content -LiteralPath $Path)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $index = $line.IndexOf('=')
        if ($index -le 0) {
            continue
        }

        $key = $line.Substring(0, $index).Trim()
        $value = $line.Substring($index + 1).Trim()
        if (-not [string]::IsNullOrWhiteSpace($key)) {
            $values[$key] = $value
        }
    }

    return $values
}

function Get-RouteAcceptanceReportValue {
    param(
        [Parameter(Mandatory = $true)]$Report,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$DefaultValue = ''
    )

    if ($Report.ContainsKey($Name)) {
        return [string]$Report[$Name]
    }

    return $DefaultValue
}

function Test-RouteAcceptanceZombieTerminalState {
    param([string]$TerminalStates)

    if ([string]::IsNullOrWhiteSpace($TerminalStates)) {
        return $false
    }

    return $TerminalStates.IndexOf('Sending', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $TerminalStates.IndexOf('Receiving', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-RouteAcceptanceTerminalErrorState {
    param([string]$TerminalErrors)

    if ([string]::IsNullOrWhiteSpace($TerminalErrors)) {
        return $false
    }

    $tokens = @($TerminalErrors.Split([char]',') | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($tokens.Count -eq 0) {
        return $false
    }

    foreach ($token in $tokens) {
        if ($token -ne '(none)') {
            return $true
        }
    }

    return $false
}

function Split-RouteAcceptanceTokenList {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -eq '(none)') {
        return @()
    }

    return @(
        $Value.Split([char]',', [System.StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '(none)' }
    )
}

function Join-RouteAcceptanceTokenList {
    param([AllowNull()][object[]]$Values)

    $tokens = @($Values | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '(none)' })
    if ($tokens.Count -eq 0) {
        return '(none)'
    }

    return ($tokens -join ',')
}

function Get-RouteAcceptanceKindCount {
    param(
        [AllowNull()][string]$Value,
        [Parameter(Mandatory = $true)][string]$Kind
    )

    foreach ($token in @(Split-RouteAcceptanceTokenList -Value $Value)) {
        $text = [string]$token
        $index = $text.LastIndexOf(':')
        if ($index -le 0) {
            continue
        }

        $tokenKind = $text.Substring(0, $index)
        if (-not [string]::Equals($tokenKind, $Kind, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        return ConvertTo-RouteAcceptanceInt -Value $text.Substring($index + 1)
    }

    return 0
}

function ConvertTo-RouteAcceptanceSummaryStringArray {
    param(
        [AllowNull()]$Values,
        [int]$Limit = 64
    )

    $tokens = @($Values | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($tokens.Count -le $Limit) {
        return @($tokens)
    }

    $truncated = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $Limit; $i++) {
        $truncated.Add($tokens[$i]) | Out-Null
    }
    $truncated.Add(("...truncated:{0}" -f ($tokens.Count - $Limit))) | Out-Null
    return $truncated.ToArray()
}

function ConvertTo-RouteAcceptanceEnvNameToken {
    param([Parameter(Mandatory = $true)][string]$Value)

    return (($Value.ToUpperInvariant() -replace '[^A-Z0-9]+', '_').Trim('_'))
}

function Get-RouteAcceptanceScenarioEnvValue {
    param(
        [Parameter(Mandatory = $true)][string]$ScenarioName,
        [Parameter(Mandatory = $true)][string]$Suffix,
        [string]$DefaultValue = ''
    )

    $token = ConvertTo-RouteAcceptanceEnvNameToken -Value $ScenarioName
    return Get-RouteAcceptanceEnvValue -Name ("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_{0}_{1}" -f $token, $Suffix) -DefaultValue $DefaultValue
}

function Test-RouteAcceptanceScenarioEnvEnabled {
    param(
        [Parameter(Mandatory = $true)][string]$ScenarioName,
        [Parameter(Mandatory = $true)][string]$Suffix
    )

    $token = ConvertTo-RouteAcceptanceEnvNameToken -Value $ScenarioName
    return Test-RouteAcceptanceEnvEnabled -Name ("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_{0}_{1}" -f $token, $Suffix)
}

function Get-RouteAcceptanceRouteChanges {
    param([AllowNull()][object[]]$Routes)

    $changes = New-Object System.Collections.Generic.List[string]
    foreach ($route in @($Routes)) {
        $routeText = [string]$route
        if ([string]::IsNullOrWhiteSpace($routeText) -or $routeText -eq '(none)' -or $routeText -eq '(unknown)') {
            continue
        }

        if ($changes.Count -eq 0 -or
            -not [string]::Equals($changes[$changes.Count - 1], $routeText, [System.StringComparison]::OrdinalIgnoreCase)) {
            $changes.Add($routeText) | Out-Null
        }
    }

    return $changes.ToArray()
}

function Read-RouteAcceptanceBaselineManifest {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $resolvedPath = Resolve-RouteAcceptancePath -RepoRoot $RepoRoot -Path $Path
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Baseline manifest missing: $resolvedPath"
    }

    $manifest = Get-Content -LiteralPath $resolvedPath -Raw | ConvertFrom-Json
    $baselines = @{}
    foreach ($scenario in @($manifest.scenarios)) {
        $name = [string](Get-JsonPropertyValue -Object $scenario -Name 'scenario' -DefaultValue '')
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $baselines[$name] = $scenario
        }
    }

    return [pscustomobject]@{
        Path = $resolvedPath
        Manifest = $manifest
        Scenarios = $baselines
    }
}

function Test-RouteAcceptanceAllowedOperatorWarning {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedRoute,
        [Parameter(Mandatory = $true)][string]$Verdict,
        [Parameter(Mandatory = $true)][string[]]$WarningKinds
    )

    $kinds = @($WarningKinds)
    if (-not $AllowExternalTransportWarnings -or $Verdict -ne 'WARN_EXTERNAL_TRANSPORT') {
        return $false
    }

    if ($kinds.Count -eq 0) {
        return $false
    }

    $allowed = @(switch ($ExpectedRoute) {
        'file_tuna_v4' { @('external_transport_churn', 'fallback_frontier_repair_churn', 'fallback_receiver_state_churn') }
        'post_tuna_fallback_v6' { @('external_transport_churn', 'fallback_v6_send_timeout_churn', 'fallback_frontier_repair_churn', 'fallback_receiver_state_churn', 'recovered_post_tuna_fallback_bridge_clear', 'recovered_runtime_unlock_bridge_clear', 'recovered_regular_v4_bridge_clear') }
        'regular_nkn_v4_fast' { @('external_transport_churn', 'recovered_regular_v4_bridge_clear', 'regular_v4_frontier_repair_churn') }
        default { @() }
    })

    if ($allowed.Count -eq 0) {
        return $false
    }

    foreach ($kind in @($kinds)) {
        if (-not ($allowed -contains $kind)) {
            return $false
        }
    }

    return $true
}

function Test-Phase4RegularNknExternalTransportVariance {
    param([Parameter(Mandatory = $true)]$Result)

    if ($Result.finalRoute -ne 'regular_nkn_v4_fast' -or
        $Result.operatorVerdict -ne 'WARN_EXTERNAL_TRANSPORT' -or
        $Result.hardFailureCount -ne 0 -or
        $Result.warningCapExceededKinds -ne '(none)') {
        return $false
    }

    $kinds = @($Result.warningKinds | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '(none)' })
    if ($kinds.Count -eq 0) {
        return $false
    }

    foreach ($kind in @($kinds)) {
        if ($kind -ne 'external_transport_churn' -and
            $kind -ne 'recovered_regular_v4_bridge_clear' -and
            $kind -ne 'regular_v4_frontier_repair_churn') {
            return $false
        }
    }

    return $true
}

function Test-Phase5RegularNknCompletedExternalTransportVariance {
    param([Parameter(Mandatory = $true)]$Result)

    if ($Result.finalRoute -ne 'regular_nkn_v4_fast' -or
        [string]$Result.protocol -ne '4' -or
        $Result.routeConsistencyVerdict -ne 'pass' -or
        -not $Result.completed -or
        -not $Result.shaOk -or
        $Result.bridgeBulkSendFailureCount -ne 0 -or
        $Result.bridgeLivenessIntegrationVerdict -eq 'fail' -or
        $Result.controlPlaneIsolationVerdict -eq 'fail') {
        return $false
    }

    if ($Result.operatorVerdict -ne 'WARN_EXTERNAL_TRANSPORT' -and
        $Result.operatorVerdict -ne 'FAIL_EXTERNAL_TRANSPORT_CHURN') {
        return $false
    }

    $warningCapExceeded = [string]$Result.warningCapExceededKinds
    if ($warningCapExceeded -ne '(none)' -and $warningCapExceeded -ne 'external_transport_churn') {
        return $false
    }

    $kinds = @($Result.warningKinds | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '(none)' })
    if ($kinds.Count -eq 0) {
        return $false
    }

    foreach ($kind in @($kinds)) {
        if ($kind -ne 'external_transport_churn' -and
            $kind -ne 'recovered_regular_v4_bridge_clear' -and
            $kind -ne 'regular_v4_frontier_repair_churn') {
            return $false
        }
    }

    return $true
}

function Set-Phase5RegularNknCompletedExternalTransportVariance {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)]$Result
    )

    if ([string]$Scenario.Kind -ne 'regular') {
        return $false
    }

    if (-not (Test-Phase5RegularNknCompletedExternalTransportVariance -Result $Result)) {
        return $false
    }

    Remove-RouteAcceptanceFailuresMatching -Result $Result -Patterns @(
        'operator hard failures observed',
        'warning cap exceeded: external_transport_churn',
        'regular NKN warning-free acceptance failed'
    )
    $Result.environmentalClassification = 'live_transport_external_churn_completed_clean'
    return $true
}

function Set-Phase5PostTerminalGuiCleanupWarning {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)]$Result
    )

    if ([string]$Scenario.Kind -ne 'tuna') {
        return $false
    }

    if ([string]::IsNullOrWhiteSpace([string]$Result.setupFailurePhase) -and
        [string]::IsNullOrWhiteSpace([string]$Result.setupFailureReason)) {
        return $false
    }

    if (-not $Result.completed -or
        -not $Result.shaOk -or
        $Result.routeConsistencyVerdict -ne 'pass' -or
        [string]$Result.finalRoute -ne [string]$Result.expectedRoute -or
        [string]$Result.protocol -ne [string]$Result.expectedProtocol) {
        return $false
    }

    if ($Result.liveRouteEpochProofVerdict -eq 'fail' -or
        $Result.fallbackLegAuthorityProofVerdict -eq 'fail' -or
        $Result.cleanFallbackLegProofVerdict -eq 'fail' -or
        $Result.bridgeLivenessIntegrationVerdict -eq 'fail' -or
        $Result.controlPlaneIsolationVerdict -eq 'fail') {
        return $false
    }

    $setupFailurePhase = [string]$Result.setupFailurePhase
    $setupFailureReason = [string]$Result.setupFailureReason
    $failures = @($Result.failures | ForEach-Object { [string]$_ })
    $hasConnectedHeaderCleanupFailure = $setupFailurePhase.IndexOf('connected_header', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $setupFailureReason.IndexOf('connected_header', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $setupFailurePhase.IndexOf('post_transfer_connected_header', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $setupFailureReason.IndexOf('post_transfer_connected_header', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        @(($failures | Where-Object {
            $_.IndexOf('connected header', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $_.IndexOf('post_transfer_connected_header', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        } | Select-Object -First 1)).Count -gt 0
    if (-not $hasConnectedHeaderCleanupFailure) {
        return $false
    }

    Remove-RouteAcceptanceFailuresMatching -Result $Result -Patterns @(
        'Tuna GUI setup failure:',
        'scenario execution failed:'
    )
    $Result.setupFailurePhase = ''
    $Result.setupFailureReason = ''
    if ([string]$Result.environmentalClassification -eq '(none)') {
        $Result.environmentalClassification = 'post_terminal_gui_cleanup'
    }
    return $true
}

function Test-Phase5CompletedLiveFallbackWarningVariance {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)]$Result
    )

    if ([string]$Scenario.Kind -ne 'tuna' -or @($Scenario.ExpectedRouteChanges).Count -le 1) {
        return $false
    }

    if (-not $Result.completed -or
        -not $Result.shaOk -or
        $Result.routeConsistencyVerdict -ne 'pass' -or
        $Result.liveRouteEpochProofVerdict -eq 'fail' -or
        $Result.fallbackLegAuthorityProofVerdict -eq 'fail' -or
        $Result.bridgeLivenessIntegrationVerdict -eq 'fail' -or
        $Result.controlPlaneIsolationVerdict -eq 'fail' -or
        $Result.route.IndexOf('file_tuna_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Result.route.IndexOf('diagnostic_regular_nkn_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $false
    }

    if ($Result.operatorVerdict -ne 'FAIL_EXTERNAL_TRANSPORT_CHURN' -and
        $Result.operatorVerdict -ne 'WARN_EXTERNAL_TRANSPORT') {
        return $false
    }

    $exceeded = @(Split-RouteAcceptanceTokenList -Value ([string]$Result.warningCapExceededKinds))
    if ($exceeded.Count -eq 0 -or ($exceeded.Count -eq 1 -and $exceeded[0] -eq '(none)')) {
        return $false
    }

    $allowedExceeded = @(
        'external_transport_churn',
        'fallback_frontier_repair_churn',
        'fallback_receiver_state_churn'
    )
    foreach ($kind in @($exceeded)) {
        if (-not ($allowedExceeded -contains [string]$kind)) {
            return $false
        }
    }

    return $true
}

function Set-Phase5CompletedLiveFallbackWarningVariance {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)]$Result
    )

    if (-not (Test-Phase5CompletedLiveFallbackWarningVariance -Scenario $Scenario -Result $Result)) {
        return $false
    }

    Remove-RouteAcceptanceFailuresMatching -Result $Result -Patterns @(
        'operator hard failures observed',
        'warning cap exceeded: external_transport_churn',
        'warning cap exceeded: fallback_frontier_repair_churn',
        'warning cap exceeded: fallback_receiver_state_churn',
        'operator verdict is not accepted'
    )
    $Result.environmentalClassification = 'live_transport_recovered_fallback_churn_completed_clean'
    return $true
}

function Test-Phase4RegularNknProgressTimeoutRecoveryStormVariance {
    param([Parameter(Mandatory = $true)]$Result)

    if ($Result.finalRoute -ne 'regular_nkn_v4_fast' -or
        $Result.warningCapExceededKinds -ne '(none)' -or
        $Result.bridgeLivenessIntegrationVerdict -eq 'fail') {
        return $false
    }

    $summaryPath = Join-Path $Result.artifactDir 'filetransfer-live-nkn-summary.txt'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        return $false
    }

    $summary = Read-RouteAcceptanceKeyValueArtifact -Path $summaryPath
    $protocol = Get-RouteAcceptanceReportValue -Report $summary -Name 'data_protocol_version' -DefaultValue '(missing)'
    $verdict = Get-RouteAcceptanceReportValue -Report $summary -Name 'verdict' -DefaultValue '(missing)'
    $cyclesObserved = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'cycles_observed' -DefaultValue '0')
    $bridgeBulkQueueClearCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'bridge_bulk_queue_clear_count' -DefaultValue '0')
    $v4FeedbackBothFailedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'v4_feedback_both_failed_count' -DefaultValue '0')
    $progressTimeoutCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'gui_progress_timeout_count' -DefaultValue '0')
    $terminalMissingAfterProgressTimeout = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'terminal_missing_after_progress_timeout' -DefaultValue '0')
    if ($protocol -ne '4') {
        return $false
    }

    $progressTimeoutStorm = ($verdict -eq 'INCONCLUSIVE_PROGRESS_TIMEOUT' -or $progressTimeoutCount -gt 0) -and
        $terminalMissingAfterProgressTimeout -ne 0
    $startupRecoveryStorm = $verdict -eq 'FAIL_PROTOCOL_OR_INTEGRITY' -and
        $progressTimeoutCount -eq 0 -and
        $terminalMissingAfterProgressTimeout -eq 0 -and
        $cyclesObserved -eq 0 -and
        $bridgeBulkQueueClearCount -gt 0 -and
        $v4FeedbackBothFailedCount -eq 0
    if (-not $progressTimeoutStorm -and -not $startupRecoveryStorm) {
        return $false
    }

    $kinds = @($Result.warningKinds | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '(none)' })
    if ($Result.operatorVerdict -eq 'INCONCLUSIVE_PROGRESS_TIMEOUT' -and
        $Result.hardFailureCount -eq 0 -and
        ($kinds -contains 'regular_v4_transport_recovery_storm')) {
        return $true
    }

    if ($Result.operatorVerdict -eq 'INCONCLUSIVE' -and
        $Result.hardFailureCount -eq 0) {
        return $true
    }

    if ($Result.operatorVerdict -ne 'FAIL_PROTOCOL_OR_INTEGRITY' -or
        $Result.hardFailureCount -le 0) {
        return $false
    }

    $stabilityPath = Join-Path $Result.artifactDir 'stability-gates-summary.txt'
    if (-not (Test-Path -LiteralPath $stabilityPath -PathType Leaf)) {
        return $false
    }

    $stabilityText = Get-Content -LiteralPath $stabilityPath -Raw
    if ($stabilityText.IndexOf('bridge bulk send failure/clear', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        return $false
    }

    foreach ($unexpected in @('hard protocol/integrity event:', 'terminal failure:', 'route consistency:', 'legacy data protocol started:', 'legacy data frame observed')) {
        if ($stabilityText.IndexOf($unexpected, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $false
        }
    }

    return $true
}

function Set-Phase4RegularNknProgressTimeoutRecoveryStormVariance {
    param([Parameter(Mandatory = $true)]$Result)

    if (-not (Test-Phase4RegularNknProgressTimeoutRecoveryStormVariance -Result $Result)) {
        return
    }

    $Result.failures.Clear()
    $Result.environmentalClassification = 'regular_v4_transport_recovery_storm'
    Add-RouteAcceptanceMeasurementContamination -Result $Result -Reason 'regular_v4_transport_recovery_storm'
}

function Add-RouteAcceptanceMeasurementContamination {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    if ([string]::IsNullOrWhiteSpace($Reason)) {
        return
    }

    $existing = @($Result.measurementContaminationReasons | ForEach-Object { [string]$_ })
    if (-not ($existing -contains $Reason)) {
        $Result.measurementContaminationReasons += @($Reason)
    }

    $Result.measurementContaminated = $true
}

function Get-RouteAcceptanceReportInt {
    param(
        [Parameter(Mandatory = $true)]$Report,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $Report -Name $Name -DefaultValue '0')
}

function Get-RouteAcceptanceMeasuredWindowBridgeRecoveryCount {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $logPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        return 0
    }

    $lines = @(Get-Content -LiteralPath $logPath)
    $routeStartIndex = -1
    $terminalIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = [string]$lines[$i]
        if ($routeStartIndex -lt 0 -and
            $line.IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('route=file_tuna_v4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $routeStartIndex = $i
            continue
        }

        if ($routeStartIndex -ge 0 -and
            $line.IndexOf('event=transfer_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('route=file_tuna_v4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $terminalIndex = $i
            break
        }
    }

    if ($routeStartIndex -lt 0) {
        return 0
    }

    $windowStart = [Math]::Max(0, $routeStartIndex - 150)
    $windowEnd = if ($terminalIndex -ge 0) { $terminalIndex } else { $lines.Count - 1 }
    $count = 0
    for ($i = $windowStart; $i -le $windowEnd; $i++) {
        $line = [string]$lines[$i]
        if ($line.IndexOf('event=nkn_bridge_receive_stall_detected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=nkn_bridge_receive_stall_recovery_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=nkn_bridge_receive_stall_recovery_completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=nkn_bridge_receive_stall_recovery_receive_resumed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $count++
        }
    }

    return $count
}

function Set-Phase4MeasurementContamination {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)]$Result
    )

    if ($Scenario.Kind -ne 'tuna' -or $Result.finalRoute -ne 'file_tuna_v4') {
        return
    }

    $analysisDir = $Result.artifactDir
    if ([string]$Scenario.Name -eq 'second-transfer-after-reactivation') {
        $analysisDir = Join-Path $Result.artifactDir 'second-transfer-analysis'
    }

    $repairPath = Join-Path $analysisDir 'repair-reorder-summary.txt'
    if (Test-Path -LiteralPath $repairPath -PathType Leaf) {
        $repair = Read-RouteAcceptanceKeyValueArtifact -Path $repairPath
        $repairRequested = Get-RouteAcceptanceReportInt -Report $repair -Name 'v4_repair_requested_count'
        $repairScheduled = Get-RouteAcceptanceReportInt -Report $repair -Name 'v4_missing_range_repair_scheduled_count'
        $repairSent = Get-RouteAcceptanceReportInt -Report $repair -Name 'v4_missing_range_repair_sent_count'
        $controlBulkEscalated = Get-RouteAcceptanceReportInt -Report $repair -Name 'v4_repair_delivery_control_bulk_escalated_count'
        $creditStallEscalated = Get-RouteAcceptanceReportInt -Report $repair -Name 'v4_repair_delivery_credit_stall_escalated_count'
        $frontierStallEscalated = Get-RouteAcceptanceReportInt -Report $repair -Name 'v4_repair_delivery_frontier_not_advanced_escalated_count'
        if ($repairRequested -gt 0 -or $repairScheduled -gt 0 -or $repairSent -gt 0 -or $controlBulkEscalated -gt 0 -or $creditStallEscalated -gt 0 -or $frontierStallEscalated -gt 0) {
            Add-RouteAcceptanceMeasurementContamination -Result $Result -Reason ("active_tuna_v4_repair_pressure: requested={0}; scheduled={1}; sent={2}; control_bulk={3}; credit_stall={4}; frontier_stall={5}" -f $repairRequested, $repairScheduled, $repairSent, $controlBulkEscalated, $creditStallEscalated, $frontierStallEscalated)
        }
    }

    $measuredRecoveryCount = Get-RouteAcceptanceMeasuredWindowBridgeRecoveryCount -ArtifactDir $analysisDir
    if ($measuredRecoveryCount -gt 0) {
        Add-RouteAcceptanceMeasurementContamination -Result $Result -Reason ("active_tuna_v4_bridge_receive_recovery_window: event_count={0}" -f $measuredRecoveryCount)
    }
}

function Assert-RouteAcceptanceFileExists {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $path = Join-Path $Result.artifactDir $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("missing artifact: {0}" -f $RelativePath)
        return $false
    }

    return $true
}

function Assert-RouteAcceptanceRouteSummary {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$ExpectedRoute,
        [Parameter(Mandatory = $true)][int]$ExpectedProtocol,
        [Parameter(Mandatory = $true)][string]$ExpectedRuntime,
        [Parameter(Mandatory = $true)][string]$ExpectedBridgePolicy
    )

    if (-not (Assert-RouteAcceptanceFileExists -Result $Result -RelativePath 'filetransfer-route-consistency-summary.txt')) {
        return
    }

    $routePath = Join-Path $Result.artifactDir 'filetransfer-route-consistency-summary.txt'
    $routeSummary = Read-RouteAcceptanceKeyValueArtifact -Path $routePath
    $Result.routeConsistencyVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'route_consistency_verdict' -DefaultValue '(missing)'
    $Result.route = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'selected_routes' -DefaultValue '(none)'

    if ($Result.routeConsistencyVerdict -ne 'pass') {
        Add-RouteAcceptanceFailure -Result $Result -Message ("route consistency verdict is {0}" -f $Result.routeConsistencyVerdict)
    }

    $routeText = [string]$Result.route
    if ([string]::IsNullOrWhiteSpace($routeText)) {
        $routeText = '(none)'
        $Result.route = $routeText
    }

    if ($routeText.IndexOf('diagnostic_regular_nkn_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message 'diagnostic_regular_nkn_v6 route is not allowed during acceptance'
    }

    if ($routeText -ne $ExpectedRoute) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("selected route mismatch: expected {0}, actual {1}" -f $ExpectedRoute, $routeText)
    }

    $selectedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'route_selected_count' -DefaultValue '0')
    if ($selectedCount -le 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message 'no filetransfer_route_selected event was observed'
    }

    foreach ($key in @($routeSummary.Keys)) {
        $keyText = [string]$key
        if ($keyText -match '^selected\.\d+\.protocol_version$') {
            $protocol = [string]$routeSummary[$key]
            $Result.protocol = $protocol
            if ($protocol -ne ([string]$ExpectedProtocol)) {
                Add-RouteAcceptanceFailure -Result $Result -Message ("route selected protocol mismatch: expected {0}, actual {1}" -f $ExpectedProtocol, $protocol)
            }
        }
        elseif ($keyText -match '^selected\.\d+\.runtime_profile$') {
            $runtimeProfile = [string]$routeSummary[$key]
            $Result.runtimeProfile = $runtimeProfile
            if (-not (Test-RouteAcceptanceRuntimeProfileMatches -Expected $ExpectedRuntime -Actual $runtimeProfile)) {
                Add-RouteAcceptanceFailure -Result $Result -Message ("route selected runtime mismatch: expected {0}, actual {1}" -f $ExpectedRuntime, $runtimeProfile)
            }
        }
        elseif ($keyText -match '^selected\.\d+\.bridge_recovery_policy$') {
            $bridgePolicy = [string]$routeSummary[$key]
            $Result.bridgeRecoveryPolicy = $bridgePolicy
            if ($bridgePolicy -ne $ExpectedBridgePolicy) {
                Add-RouteAcceptanceFailure -Result $Result -Message ("route selected bridge policy mismatch: expected {0}, actual {1}" -f $ExpectedBridgePolicy, $bridgePolicy)
            }
        }
    }

    if ($Result.protocol -eq '(unknown)') {
        Add-RouteAcceptanceFailure -Result $Result -Message 'route summary did not include selected protocol version'
    }
}

function Test-RouteAcceptanceRuntimeProfileMatches {
    param(
        [string]$Expected,
        [string]$Actual
    )

    if ([string]::Equals($Expected, $Actual, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $fileTunaRuntimeAliases = @('file_tuna_v4', 'file_tuna_v4_fast')
    return $fileTunaRuntimeAliases -contains $Expected -and
           $fileTunaRuntimeAliases -contains $Actual
}

function Assert-RouteAcceptanceOperatorVerdict {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$ExpectedRoute
    )

    if (-not (Assert-RouteAcceptanceFileExists -Result $Result -RelativePath 'filetransfer-operator-verdict.txt')) {
        return
    }

    $operator = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Result.artifactDir 'filetransfer-operator-verdict.txt')
    $Result.operatorVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'verdict' -DefaultValue '(missing)'
    $hardFailureCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'hard_failure_count' -DefaultValue '0')
    $warningKinds = @(Split-RouteAcceptanceTokenList -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'warning_kinds' -DefaultValue '(none)'))
    $Result.warningKinds = @($warningKinds)

    if ($hardFailureCount -gt 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("operator hard failures observed: {0}" -f $hardFailureCount)
        return
    }

    if ($Result.operatorVerdict -eq 'PASS') {
        return
    }

    if (Test-RouteAcceptanceAllowedOperatorWarning -ExpectedRoute $ExpectedRoute -Verdict $Result.operatorVerdict -WarningKinds $warningKinds) {
        $Result.operatorAcceptedWithWarnings = $true
        return
    }

    $operatorWarningKinds = @($warningKinds)
    Add-RouteAcceptanceFailure -Result $Result -Message ("operator verdict is not accepted for {0}: verdict={1}; warning_kinds={2}" -f $ExpectedRoute, $Result.operatorVerdict, ($(if ($operatorWarningKinds.Count -gt 0) { $operatorWarningKinds -join ',' } else { '(none)' })))
}

function Test-RouteAcceptanceFallbackSetupTunaV4Evidence {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $setupRoutePath = Join-Path $ArtifactDir 'setup-analysis\filetransfer-route-consistency-summary.txt'
    if (Test-Path -LiteralPath $setupRoutePath -PathType Leaf) {
        $setupRouteSummary = Read-RouteAcceptanceKeyValueArtifact -Path $setupRoutePath
        $selectedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $setupRouteSummary -Name 'route_selected_count' -DefaultValue '0')
        for ($i = 1; $i -le $selectedCount; $i++) {
            $route = Get-RouteAcceptanceReportValue -Report $setupRouteSummary -Name ("selected.{0}.route" -f $i) -DefaultValue ''
            $protocol = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $setupRouteSummary -Name ("selected.{0}.protocol_version" -f $i) -DefaultValue '0')
            if ($route -eq 'file_tuna_v4' -and $protocol -eq 4) {
                return $true
            }
        }
    }

    $setupLogPath = Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log'
    if (Test-Path -LiteralPath $setupLogPath -PathType Leaf) {
        foreach ($line in @(Get-Content -LiteralPath $setupLogPath)) {
            if ($line.IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('route=file_tuna_v4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('protocol_version=4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $true
            }
        }
    }

    return $false
}

function Assert-RouteAcceptanceTerminalSummary {
    param([Parameter(Mandatory = $true)]$Result)

    if (-not (Assert-RouteAcceptanceFileExists -Result $Result -RelativePath 'transfer-terminal-summary.txt')) {
        return
    }

    $terminal = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Result.artifactDir 'transfer-terminal-summary.txt')
    $inboundCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $terminal -Name 'inbound_terminal_count' -DefaultValue '0')
    $outboundCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $terminal -Name 'outbound_terminal_count' -DefaultValue '0')
    $states = Get-RouteAcceptanceReportValue -Report $terminal -Name 'terminal_states' -DefaultValue ''
    $inboundState = Get-RouteAcceptanceReportValue -Report $terminal -Name 'inbound_terminal_state' -DefaultValue ''
    $outboundState = Get-RouteAcceptanceReportValue -Report $terminal -Name 'outbound_terminal_state' -DefaultValue ''
    $errors = Get-RouteAcceptanceReportValue -Report $terminal -Name 'terminal_error_codes' -DefaultValue ''
    if ([string]::IsNullOrWhiteSpace($states)) {
        $stateValues = @()
        if (-not [string]::IsNullOrWhiteSpace($inboundState)) {
            $stateValues += $inboundState
        }
        if (-not [string]::IsNullOrWhiteSpace($outboundState)) {
            $stateValues += $outboundState
        }
        if ($stateValues.Count -gt 0) {
            $states = $stateValues -join ','
        }
    }
    if ($inboundCount -le 0 -and $inboundState.IndexOf('Completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $inboundCount = 1
    }
    if ($outboundCount -le 0 -and $outboundState.IndexOf('Completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $outboundCount = 1
    }

    if ($inboundCount -le 0 -or $outboundCount -le 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("terminal summary missing inbound/outbound completion evidence: inbound={0}; outbound={1}" -f $inboundCount, $outboundCount)
    }

    if ($states.IndexOf('Completed', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("terminal summary did not report Completed state: {0}" -f $states)
    }

    if (Test-RouteAcceptanceZombieTerminalState -TerminalStates $states) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("zombie terminal state observed: {0}" -f $states)
    }

    if (Test-RouteAcceptanceTerminalErrorState -TerminalErrors $errors) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("terminal summary reported error codes: {0}" -f $errors)
    }
}

function Assert-RegularNknRouteAcceptanceRun {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    $result = New-RouteAcceptanceRunResult -Name $Name -ArtifactDir $ArtifactDir -ExpectedRoute 'regular_nkn_v4_fast' -ExpectedProtocol 4
    Assert-RouteAcceptanceRouteSummary -Result $result -ExpectedRoute 'regular_nkn_v4_fast' -ExpectedProtocol 4 -ExpectedRuntime 'regular_nkn_v4_fast' -ExpectedBridgePolicy 'regular_nkn_v4_fast'
    Assert-RouteAcceptanceTerminalSummary -Result $result
    Assert-RouteAcceptanceOperatorVerdict -Result $result -ExpectedRoute 'regular_nkn_v4_fast'
    Assert-RouteAcceptanceRegularNknTransportEvidence -Result $result

    if (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-live-nkn-summary.txt') {
        $summary = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt')
        $protocol = Get-RouteAcceptanceReportValue -Report $summary -Name 'data_protocol_version' -DefaultValue '(missing)'
        $result.protocol = $protocol
        if ($protocol -ne '4') {
            Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN protocol mismatch: expected 4, actual {0}" -f $protocol)
        }

        $cyclesCompleted = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'cycles_completed' -DefaultValue '0')
        if ($cyclesCompleted -le 0) {
            Add-RouteAcceptanceFailure -Result $result -Message 'regular NKN completed no cycles'
        }

        $result.goodputBytesPerSecond = ConvertTo-RouteAcceptanceDouble -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'average_goodput_bytes_per_second' -DefaultValue '0')
        $result.bridgeBulkSendFailureCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'bridge_bulk_send_failure_count' -DefaultValue '0')
        if ($result.bridgeBulkSendFailureCount -ne 0) {
            Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN bridge_bulk_send_failure_count must be 0, actual {0}" -f $result.bridgeBulkSendFailureCount)
        }
    }

    if (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-live-nkn-cycles.jsonl') {
        $cycleLines = @(Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($cycleLines.Count -eq 0) {
            Add-RouteAcceptanceFailure -Result $result -Message 'regular NKN cycle artifact contained no cycles'
        }

        foreach ($line in $cycleLines) {
            $cycle = $line | ConvertFrom-Json
            $completed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $cycle -Name 'completed' -DefaultValue $false)
            $integrityOk = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $cycle -Name 'integrity_ok' -DefaultValue $false)
            if (-not $completed -or -not $integrityOk) {
                Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN cycle failed completion/integrity: {0}" -f $line)
            }
        }
    }

    $result.completed = $result.failures.Count -eq 0
    $result.integrityOk = $result.failures.Count -eq 0
    $script:RunResults.Add($result) | Out-Null
}

function Assert-TunaRouteAcceptanceRun {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$ExpectedRoute,
        [Parameter(Mandatory = $true)][string]$ExpectedBridgePolicy
    )

    $metadata = Get-RouteAcceptanceRouteMetadata -Route $ExpectedRoute
    $expectedProtocol = [int]$metadata.Protocol
    $result = New-RouteAcceptanceRunResult -Name $Name -ArtifactDir $ArtifactDir -ExpectedRoute $ExpectedRoute -ExpectedProtocol $expectedProtocol
    $attemptsPath = Join-Path $ArtifactDir 'route-acceptance-attempts.json'
    if (Test-Path -LiteralPath $attemptsPath -PathType Leaf) {
        $attempts = Get-Content -LiteralPath $attemptsPath -Raw | ConvertFrom-Json
        $result.attemptCount = ConvertTo-RouteAcceptanceInt -Value (Get-JsonPropertyValue -Object $attempts -Name 'attemptCount' -DefaultValue 1) -DefaultValue 1
        $result.retryUsed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $attempts -Name 'retryUsed' -DefaultValue $false)
        $result.selectedAttempt = ConvertTo-RouteAcceptanceInt -Value (Get-JsonPropertyValue -Object $attempts -Name 'selectedAttempt' -DefaultValue 0) -DefaultValue 0
        $result.firstFailureReason = [string](Get-JsonPropertyValue -Object $attempts -Name 'firstFailureReason' -DefaultValue '')
        if ($ExpectedRoute -eq 'post_tuna_fallback_v6' -and $result.selectedAttempt -le 0) {
            Add-RouteAcceptanceFailure -Result $result -Message 'fallback attempts exhausted before successful measured transfer'
        }
    }

    Assert-RouteAcceptanceRouteSummary -Result $result -ExpectedRoute $ExpectedRoute -ExpectedProtocol $expectedProtocol -ExpectedRuntime $metadata.Runtime -ExpectedBridgePolicy $ExpectedBridgePolicy
    Assert-RouteAcceptanceTerminalSummary -Result $result
    Assert-RouteAcceptanceOperatorVerdict -Result $result -ExpectedRoute $ExpectedRoute

    if (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-tuna-gui-summary.json') {
        $summary = Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Raw | ConvertFrom-Json
        $result.controlledRestartAnalysis = Get-JsonPropertyValue -Object $summary -Name 'controlledRestartAnalysis' -DefaultValue $null
        $result.completed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $summary -Name 'completed' -DefaultValue $false)
        $result.integrityOk = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $summary -Name 'integrityOk' -DefaultValue $false)
        $result.goodputBytesPerSecond = ConvertTo-RouteAcceptanceDouble -Value (Get-JsonPropertyValue -Object $summary -Name 'goodputBytesPerSecond' -DefaultValue 0)
        $inboundState = [string](Get-JsonPropertyValue -Object $summary -Name 'inboundState' -DefaultValue '(missing)')
        $outboundState = [string](Get-JsonPropertyValue -Object $summary -Name 'outboundState' -DefaultValue '(missing)')
        $inboundError = [string](Get-JsonPropertyValue -Object $summary -Name 'inboundErrorCode' -DefaultValue '(missing)')
        $outboundError = [string](Get-JsonPropertyValue -Object $summary -Name 'outboundErrorCode' -DefaultValue '(missing)')
        $measuredPhase = Get-JsonPropertyValue -Object $summary -Name 'measuredPhase' -DefaultValue $null
        if ($Name -eq 'second-transfer-after-reactivation') {
            $secondTransferPhase = Get-JsonPropertyValue -Object $summary -Name 'secondTransfer' -DefaultValue $null
            if ($null -ne $secondTransferPhase) {
                $measuredPhase = $secondTransferPhase
            }
        }

        if ($null -ne $measuredPhase) {
            $measuredRoute = [string](Get-JsonPropertyValue -Object $measuredPhase -Name 'route' -DefaultValue '')
            $measuredProtocol = ConvertTo-RouteAcceptanceInt -Value (Get-JsonPropertyValue -Object $measuredPhase -Name 'protocolVersion' -DefaultValue 0)
            $result.completed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $measuredPhase -Name 'completed' -DefaultValue $false)
            $result.integrityOk = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $measuredPhase -Name 'integrityOk' -DefaultValue $false)
            $result.goodputBytesPerSecond = ConvertTo-RouteAcceptanceDouble -Value (Get-JsonPropertyValue -Object $measuredPhase -Name 'goodputBytesPerSecond' -DefaultValue $result.goodputBytesPerSecond)
            $inboundState = [string](Get-JsonPropertyValue -Object $measuredPhase -Name 'inboundState' -DefaultValue $inboundState)
            $outboundState = [string](Get-JsonPropertyValue -Object $measuredPhase -Name 'outboundState' -DefaultValue $outboundState)
            $inboundError = [string](Get-JsonPropertyValue -Object $measuredPhase -Name 'inboundErrorCode' -DefaultValue $inboundError)
            $outboundError = [string](Get-JsonPropertyValue -Object $measuredPhase -Name 'outboundErrorCode' -DefaultValue $outboundError)

            if ($measuredRoute -ne $ExpectedRoute) {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna measured route mismatch: expected={0}; actual={1}" -f $ExpectedRoute, $measuredRoute)
            }

            if ($measuredProtocol -ne $expectedProtocol) {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna measured protocol mismatch: expected={0}; actual={1}" -f $expectedProtocol, $measuredProtocol)
            }
        }

        if ($ExpectedRoute -eq 'post_tuna_fallback_v6') {
            if (-not (Test-RouteAcceptanceFallbackSetupTunaV4Evidence -ArtifactDir $ArtifactDir)) {
                Add-RouteAcceptanceFailure -Result $result -Message 'Tuna fallback setup phase missing file_tuna_v4/4 route-selected evidence'
            }
        }

        if (-not $result.completed -or -not $result.integrityOk) {
            Add-RouteAcceptanceFailure -Result $result -Message ("Tuna GUI summary failed completion/integrity: completed={0}; integrityOk={1}" -f $result.completed, $result.integrityOk)
        }

        if ($inboundState -ne 'Completed' -or $outboundState -ne 'Completed') {
            Add-RouteAcceptanceFailure -Result $result -Message ("Tuna terminals not Completed: inbound={0}; outbound={1}" -f $inboundState, $outboundState)
        }

        if (Test-RouteAcceptanceZombieTerminalState -TerminalStates ("{0},{1}" -f $inboundState, $outboundState)) {
            Add-RouteAcceptanceFailure -Result $result -Message ("Tuna zombie terminal state observed: inbound={0}; outbound={1}" -f $inboundState, $outboundState)
        }

        if ($inboundError -ne '(none)' -or $outboundError -ne '(none)') {
            Add-RouteAcceptanceFailure -Result $result -Message ("Tuna terminal errors observed: inbound={0}; outbound={1}" -f $inboundError, $outboundError)
        }

        $evidence = Get-JsonPropertyValue -Object $summary -Name 'evidence' -DefaultValue $null
        if ($ExpectedRoute -eq 'file_tuna_v4' -and $null -ne $evidence) {
            $fallbackStarted = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $evidence -Name 'fallbackEpochStarted' -DefaultValue $false)
            $fallbackRecovered = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $evidence -Name 'fallbackEpochRecovered' -DefaultValue $false)
            $fallbackWaiting = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $evidence -Name 'fallbackEpochWaiting' -DefaultValue $false)
            if ($fallbackStarted -or $fallbackRecovered -or $fallbackWaiting) {
                Add-RouteAcceptanceFailure -Result $result -Message 'Tuna no-fault acceptance unexpectedly entered fallback'
            }
        }
    }

    $script:RunResults.Add($result) | Out-Null
}

function Get-RouteAcceptanceSelectedRouteDetails {
    param([Parameter(Mandatory = $true)]$RouteSummary)

    $count = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $RouteSummary -Name 'route_selected_count' -DefaultValue '0')
    $items = New-Object System.Collections.Generic.List[object]
    for ($i = 1; $i -le $count; $i++) {
        $items.Add([pscustomobject]@{
            Index = $i
            Direction = Get-RouteAcceptanceReportValue -Report $RouteSummary -Name ("selected.{0}.direction" -f $i) -DefaultValue ''
            Route = Get-RouteAcceptanceReportValue -Report $RouteSummary -Name ("selected.{0}.route" -f $i) -DefaultValue ''
            Protocol = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $RouteSummary -Name ("selected.{0}.protocol_version" -f $i) -DefaultValue '0')
            Runtime = Get-RouteAcceptanceReportValue -Report $RouteSummary -Name ("selected.{0}.runtime_profile" -f $i) -DefaultValue ''
            Bridge = Get-RouteAcceptanceReportValue -Report $RouteSummary -Name ("selected.{0}.bridge_recovery_policy" -f $i) -DefaultValue ''
        }) | Out-Null
    }

    return $items.ToArray()
}

function Assert-Phase4RouteSummary {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string[]]$ExpectedRouteChanges
    )

    if (-not (Assert-RouteAcceptanceFileExists -Result $Result -RelativePath 'filetransfer-route-consistency-summary.txt')) {
        return
    }

    $routeSummary = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Result.artifactDir 'filetransfer-route-consistency-summary.txt')
    $Result.routeConsistencyVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'route_consistency_verdict' -DefaultValue '(missing)'
    if ($Result.routeConsistencyVerdict -ne 'pass') {
        Add-RouteAcceptanceFailure -Result $Result -Message ("route consistency verdict is {0}" -f $Result.routeConsistencyVerdict)
    }

    $selected = @(Get-RouteAcceptanceSelectedRouteDetails -RouteSummary $routeSummary)
    if ($selected.Count -le 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message 'no filetransfer_route_selected event was observed'
        return
    }

    $Result.selectedRouteSequence = @($selected | ForEach-Object { [string]$_.Route })
    $Result.selectedRouteChanges = @(Get-RouteAcceptanceRouteChanges -Routes $Result.selectedRouteSequence)
    if ($Result.selectedRouteChanges.Count -le 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message 'no usable filetransfer_route_selected route token was observed'
        return
    }

    $Result.route = Join-RouteAcceptanceTokenList -Values $Result.selectedRouteChanges
    $Result.finalRoute = [string]$Result.selectedRouteChanges[$Result.selectedRouteChanges.Count - 1]
    $Result.protocol = [string]($selected[$selected.Count - 1].Protocol)
    $Result.runtimeProfile = [string]($selected[$selected.Count - 1].Runtime)
    $Result.bridgeRecoveryPolicy = [string]($selected[$selected.Count - 1].Bridge)
    $Result.liveRouteEpochRouteChanges = @(Split-RouteAcceptanceTokenList -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'live_route_epoch_route_changes' -DefaultValue '(none)'))
    $Result.liveRouteEpochProofVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'live_route_epoch_proof_verdict' -DefaultValue '(missing)'
    $Result.fallbackLegAuthorityProofVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'fallback_leg_authority_proof_verdict' -DefaultValue '(missing)'
    $Result.cleanFallbackLegProofVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'clean_fallback_leg_proof_verdict' -DefaultValue $Result.fallbackLegAuthorityProofVerdict
    $Result.bridgeLivenessIntegrationVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'bridge_liveness_integration_verdict' -DefaultValue '(missing)'
    $Result.controlPlaneIsolationVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'control_plane_isolation_verdict' -DefaultValue '(missing)'
    $Result.fallbackTailBridgeRecoveryGenerationPairingVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'fallback_tail_bridge_recovery_generation_pairing_verdict' -DefaultValue 'none'
    $Result.fallbackTailBridgeRecoveryGenerationMismatchRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'fallback_tail_bridge_recovery_generation_mismatch_raw_count' -DefaultValue '0')
    $Result.fallbackTailBridgeRecoveryGenerationMismatchDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'fallback_tail_bridge_recovery_generation_mismatch_distinct_count' -DefaultValue '0')
    $Result.fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'fallback_tail_bridge_recovery_generation_mismatch_recovered_count' -DefaultValue '0')
    $Result.fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'fallback_tail_bridge_recovery_generation_mismatch_unresolved_count' -DefaultValue '0')
    $Result.fallbackTailTransportEpochReauthorizationVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict' -DefaultValue 'none'
    $Result.fallbackTailTransportEpochReauthorizationMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count' -DefaultValue '0')
    $Result.fallbackTailTransportEpochReauthorizationMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count' -DefaultValue '0')
    $Result.fallbackTailTransportEpochReauthorizationMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count' -DefaultValue '0')
    $Result.fallbackTailTransportEpochReauthorizationMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RouteCommitSendDrainVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'runtime_unlock_regular_v4_route_commit_send_drain_verdict' -DefaultValue 'none'
    $Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryOperationVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_operation_verdict' -DefaultValue 'none'
    $Result.fileTransferReceiveRecoveryOperationRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_operation_raw_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryOperationDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_operation_distinct_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryOperationRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_operation_recovered_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryOperationUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_operation_unresolved_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_completed_unproven_successor_missing_raw_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_completed_unproven_successor_missing_distinct_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_completed_unproven_successor_missing_recovered_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_completed_unproven_successor_verdict' -DefaultValue 'none'
    $Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_completed_proven_successor_missing_raw_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_completed_proven_successor_missing_distinct_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_completed_proven_successor_missing_recovered_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_completed_proven_successor_missing_unresolved_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedProvenSuccessorVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'filetransfer_receive_recovery_completed_proven_successor_verdict' -DefaultValue 'none'
    $Result.regularV4FrontierRepairStalledRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'regular_v4_frontier_repair_stalled_raw_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairStalledDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'regular_v4_frontier_repair_stalled_distinct_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairStalledRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'regular_v4_frontier_repair_stalled_recovered_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairStalledUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'regular_v4_frontier_repair_stalled_unresolved_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairStalledVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'regular_v4_frontier_repair_stalled_verdict' -DefaultValue 'none'
    $Result.bridgeLivenessStaleDeferralCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'bridge_liveness_stale_deferral_count' -DefaultValue '0')
    $Result.bridgeLivenessTimeoutDuringValidRecoveryCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'session_liveness_timeout_during_valid_recovery_count' -DefaultValue '0')

    $routeText = [string]$Result.route
    if ([string]::IsNullOrWhiteSpace($routeText)) {
        $routeText = '(none)'
        $Result.route = $routeText
    }

    if ($routeText.IndexOf('diagnostic_regular_nkn_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message 'diagnostic_regular_nkn_v6 route is not allowed during acceptance'
    }

    if ($routeText.IndexOf('file_tuna_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message 'active file_tuna_v6 route is not allowed during acceptance'
    }

    $expectedChangesText = Join-RouteAcceptanceTokenList -Values $ExpectedRouteChanges
    $actualChangesText = Join-RouteAcceptanceTokenList -Values $Result.selectedRouteChanges
    $acceptsTerminalFallbackPrefix = Test-RouteAcceptanceTerminalFallbackPrefixOutcome `
        -ArtifactDir $Result.artifactDir `
        -ActualRouteChanges $Result.selectedRouteChanges `
        -ExpectedRouteChanges $ExpectedRouteChanges
    $acceptsCanonicalLiveRouteSuffix = Test-RouteAcceptanceLiveRegularActivationCycleRouteSuffix `
        -ActualRouteChanges $Result.selectedRouteChanges `
        -ExpectedRouteChanges $ExpectedRouteChanges
    if ($actualChangesText -ne $expectedChangesText -and
        -not $acceptsTerminalFallbackPrefix -and
        -not $acceptsCanonicalLiveRouteSuffix) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("selected route sequence mismatch: expected {0}, actual {1}" -f $expectedChangesText, $actualChangesText)
    }

    foreach ($item in @($selected)) {
        if ([string]::IsNullOrWhiteSpace([string]$item.Route) -or
            [string]::IsNullOrWhiteSpace([string]$item.Runtime) -or
            [string]::IsNullOrWhiteSpace([string]$item.Bridge) -or
            [int]$item.Protocol -le 0) {
            Add-RouteAcceptanceFailure -Result $Result -Message ("route metadata missing for selected.{0}" -f $item.Index)
            continue
        }

        $metadata = Get-RouteAcceptanceRouteMetadata -Route ([string]$item.Route)
        if ([int]$item.Protocol -ne [int]$metadata.Protocol) {
            Add-RouteAcceptanceFailure -Result $Result -Message ("route selected protocol mismatch: route={0}; expected {1}; actual {2}" -f $item.Route, $metadata.Protocol, $item.Protocol)
        }

        if (-not (Test-RouteAcceptanceRuntimeProfileMatches -Expected ([string]$metadata.Runtime) -Actual ([string]$item.Runtime))) {
            Add-RouteAcceptanceFailure -Result $Result -Message ("route selected runtime mismatch: route={0}; expected {1}; actual {2}" -f $item.Route, $metadata.Runtime, $item.Runtime)
        }
    }
}

function Test-RouteAcceptanceTerminalFallbackPrefixOutcome {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ActualRouteChanges,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ExpectedRouteChanges
    )

    $actualChanges = @($ActualRouteChanges)
    $expectedChanges = @($ExpectedRouteChanges)
    if ($actualChanges.Count -le 0 -or
        $expectedChanges.Count -le $actualChanges.Count) {
        return $false
    }

    for ($i = 0; $i -lt $actualChanges.Count; $i++) {
        if ([string]$actualChanges[$i] -ne [string]$expectedChanges[$i]) {
            return $false
        }
    }

    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        return $false
    }

    try {
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    }
    catch {
        return $false
    }

    $terminalBeforeReactivation = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $summary -Name 'firstTransferTerminalBeforeLiveReactivation' -DefaultValue $false)
    $terminalBeforeFallbackSwitchOff = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $summary -Name 'firstTransferTerminalBeforeFallbackSwitchOff' -DefaultValue $false)

    $phase = Get-JsonPropertyValue -Object $summary -Name 'measuredPhase' -DefaultValue $null
    if ($null -eq $phase) {
        $phase = $summary
    }

    $completed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $phase -Name 'completed' -DefaultValue $false)
    $integrityOk = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $phase -Name 'integrityOk' -DefaultValue $false)
    $inboundState = [string](Get-JsonPropertyValue -Object $phase -Name 'inboundState' -DefaultValue '(missing)')
    $outboundState = [string](Get-JsonPropertyValue -Object $phase -Name 'outboundState' -DefaultValue '(missing)')
    $inboundError = [string](Get-JsonPropertyValue -Object $phase -Name 'inboundErrorCode' -DefaultValue '(missing)')
    $outboundError = [string](Get-JsonPropertyValue -Object $phase -Name 'outboundErrorCode' -DefaultValue '(missing)')

    $cleanTerminal = $completed -and
        $integrityOk -and
        $inboundState -eq 'Completed' -and
        $outboundState -eq 'Completed' -and
        $inboundError -eq '(none)' -and
        $outboundError -eq '(none)'
    if (-not $cleanTerminal) {
        return $false
    }

    if ($terminalBeforeReactivation) {
        return $false
    }

    if ($terminalBeforeFallbackSwitchOff) {
        $minimumTerminalFallbackPrefixCount = if ([string]$actualChanges[0] -eq 'regular_nkn_v4_fast') { 4 } else { 3 }
        return $actualChanges.Count -ge $minimumTerminalFallbackPrefixCount -and
            [string]$actualChanges[$actualChanges.Count - 1] -eq 'file_tuna_v4' -and
            [string]$actualChanges[$actualChanges.Count - 2] -eq 'post_tuna_fallback_v6' -and
            [string]$expectedChanges[$actualChanges.Count] -eq 'post_tuna_fallback_v6'
    }

    return $false
}

function Test-RouteAcceptanceLiveRegularActivationCycleRouteSuffix {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ActualRouteChanges,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ExpectedRouteChanges
    )

    $actualChanges = @($ActualRouteChanges)
    $expectedChanges = @($ExpectedRouteChanges)
    if ($expectedChanges.Count -lt 5 -or
        [string]$expectedChanges[0] -ne 'regular_nkn_v4_fast' -or
        $actualChanges.Count -ne ($expectedChanges.Count - 1)) {
        return $false
    }

    for ($i = 0; $i -lt $actualChanges.Count; $i++) {
        if ([string]$actualChanges[$i] -ne [string]$expectedChanges[$i + 1]) {
            return $false
        }
    }

    return $true
}

function Test-RouteAcceptanceTerminalFallbackMeasuredPhaseOutcome {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ActualRouteChanges,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ExpectedRouteChanges,
        [Parameter(Mandatory = $true)][string]$PhaseRoute,
        [Parameter(Mandatory = $true)][int]$PhaseProtocol
    )

    $actualChanges = @($ActualRouteChanges)
    if ($actualChanges.Count -le 0) {
        return $false
    }

    if (-not (Test-RouteAcceptanceTerminalFallbackPrefixOutcome -ArtifactDir $ArtifactDir -ActualRouteChanges $actualChanges -ExpectedRouteChanges $ExpectedRouteChanges)) {
        return $false
    }

    $actualFinalRoute = [string]$actualChanges[$actualChanges.Count - 1]
    if ($PhaseRoute -ne $actualFinalRoute) {
        return $false
    }

    $metadata = Get-RouteAcceptanceRouteMetadata -Route $actualFinalRoute
    return $PhaseProtocol -eq [int]$metadata.Protocol
}

function Assert-Phase4OperatorVerdict {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][bool]$RequireWarningFree
    )

    if (-not (Assert-RouteAcceptanceFileExists -Result $Result -RelativePath 'filetransfer-operator-verdict.txt')) {
        return
    }

    $operator = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Result.artifactDir 'filetransfer-operator-verdict.txt')
    $Result.operatorVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'verdict' -DefaultValue '(missing)'
    $Result.hardFailureCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'hard_failure_count' -DefaultValue '0')
    $Result.warningCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'warning_count' -DefaultValue '0')
    $Result.warningKinds = @(Split-RouteAcceptanceTokenList -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'warning_kinds' -DefaultValue '(none)'))
    $warningKindCounts = Get-RouteAcceptanceReportValue -Report $operator -Name 'warning_kind_counts' -DefaultValue '(none)'
    $warningRawEventCounts = Get-RouteAcceptanceReportValue -Report $operator -Name 'warning_kind_raw_event_counts' -DefaultValue '(none)'
    $warningCapExceeded = Get-RouteAcceptanceReportValue -Report $operator -Name 'warning_cap_exceeded_kinds' -DefaultValue '(none)'
    $Result.warningCapExceededKinds = $warningCapExceeded
    $Result.fallbackTailBridgeRecoveryGenerationPairingVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'fallback_tail_bridge_recovery_generation_pairing_verdict' -DefaultValue ([string]$Result.fallbackTailBridgeRecoveryGenerationPairingVerdict)
    $Result.fallbackTailBridgeRecoveryGenerationMismatchRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'fallback_tail_bridge_recovery_generation_mismatch_raw_count' -DefaultValue ([string]$Result.fallbackTailBridgeRecoveryGenerationMismatchRawCount))
    $Result.fallbackTailBridgeRecoveryGenerationMismatchDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'fallback_tail_bridge_recovery_generation_mismatch_distinct_count' -DefaultValue ([string]$Result.fallbackTailBridgeRecoveryGenerationMismatchDistinctCount))
    $Result.fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'fallback_tail_bridge_recovery_generation_mismatch_recovered_count' -DefaultValue ([string]$Result.fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount))
    $Result.fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'fallback_tail_bridge_recovery_generation_mismatch_unresolved_count' -DefaultValue ([string]$Result.fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount))
    $Result.fallbackTailTransportEpochReauthorizationVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict' -DefaultValue ([string]$Result.fallbackTailTransportEpochReauthorizationVerdict)
    $Result.fallbackTailTransportEpochReauthorizationMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count' -DefaultValue ([string]$Result.fallbackTailTransportEpochReauthorizationMissingRawCount))
    $Result.fallbackTailTransportEpochReauthorizationMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count' -DefaultValue ([string]$Result.fallbackTailTransportEpochReauthorizationMissingDistinctCount))
    $Result.fallbackTailTransportEpochReauthorizationMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count' -DefaultValue ([string]$Result.fallbackTailTransportEpochReauthorizationMissingRecoveredCount))
    $Result.fallbackTailTransportEpochReauthorizationMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count' -DefaultValue ([string]$Result.fallbackTailTransportEpochReauthorizationMissingUnresolvedCount))
    $Result.runtimeUnlockRegularV4RouteCommitSendDrainVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_route_commit_send_drain_verdict' -DefaultValue ([string]$Result.runtimeUnlockRegularV4RouteCommitSendDrainVerdict)
    $Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count' -DefaultValue ([string]$Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount))
    $Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count' -DefaultValue ([string]$Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount))
    $Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count' -DefaultValue ([string]$Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount))
    $Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count' -DefaultValue ([string]$Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount))
    $Result.fileTransferReceiveRecoveryOperationVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_operation_verdict' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryOperationVerdict)
    $Result.fileTransferReceiveRecoveryOperationRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_operation_raw_count' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryOperationRawCount))
    $Result.fileTransferReceiveRecoveryOperationDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_operation_distinct_count' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryOperationDistinctCount))
    $Result.fileTransferReceiveRecoveryOperationRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_operation_recovered_count' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryOperationRecoveredCount))
    $Result.fileTransferReceiveRecoveryOperationUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_operation_unresolved_count' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryOperationUnresolvedCount))
    $Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_unproven_successor_missing_raw_count' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount))
    $Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_unproven_successor_missing_distinct_count' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount))
    $Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_unproven_successor_missing_recovered_count' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount))
    $Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount))
    $Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_unproven_successor_verdict' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorVerdict)
    $Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_proven_successor_missing_raw_count' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount))
    $Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_proven_successor_missing_distinct_count' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount))
    $Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_proven_successor_missing_recovered_count' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount))
    $Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_proven_successor_missing_unresolved_count' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount))
    $Result.fileTransferReceiveRecoveryCompletedProvenSuccessorVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_proven_successor_verdict' -DefaultValue ([string]$Result.fileTransferReceiveRecoveryCompletedProvenSuccessorVerdict)
    $Result.regularV4FrontierRepairStalledRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_stalled_raw_count' -DefaultValue ([string]$Result.regularV4FrontierRepairStalledRawCount))
    $Result.regularV4FrontierRepairStalledDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_stalled_distinct_count' -DefaultValue ([string]$Result.regularV4FrontierRepairStalledDistinctCount))
    $Result.regularV4FrontierRepairStalledRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_stalled_recovered_count' -DefaultValue ([string]$Result.regularV4FrontierRepairStalledRecoveredCount))
    $Result.regularV4FrontierRepairStalledUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_stalled_unresolved_count' -DefaultValue ([string]$Result.regularV4FrontierRepairStalledUnresolvedCount))
    $Result.regularV4FrontierRepairStalledVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_stalled_verdict' -DefaultValue ([string]$Result.regularV4FrontierRepairStalledVerdict)
    $Result.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialOfferRetryLifecycleHandoffVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_offer_retry_lifecycle_handoff_verdict' -DefaultValue ([string]$Result.runtimeUnlockInitialOfferRetryLifecycleHandoffVerdict)
    $Result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_verdict' -DefaultValue ([string]$Result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedVerdict)
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_verdict' -DefaultValue ([string]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryVerdict)
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_verdict' -DefaultValue ([string]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredVerdict)
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_verdict' -DefaultValue ([string]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredVerdict)
    $Result.fallbackReceiverStateDeferredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'fallback_v6_receiver_state_deferred_count' -DefaultValue '0')
    $Result.fallbackReceiverStateCoalescedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'fallback_v6_receiver_state_coalesced_count' -DefaultValue '0')
    $firstProofClass = Get-RouteAcceptanceReportValue -Report $operator -Name 'first_proof_chain_failure_class' -DefaultValue '(none)'
    if ($firstProofClass -eq '(none)') {
        $firstProofClass = Get-RouteAcceptanceReportValue -Report $operator -Name 'recovery_failure_class' -DefaultValue '(none)'
    }
    $Result.runtimeFailureCandidate = $firstProofClass
    $Result.firstProofChainFailureClass = $firstProofClass
    $Result.runtimeUnlockRetryDispatchAuthorityGrantedRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_authority_granted_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchAuthorityGrantedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_authority_granted_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchPostGrantHoldCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_post_grant_hold_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchPostGrantHoldRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_post_grant_hold_raw_count' -DefaultValue ([string]$Result.runtimeUnlockRetryDispatchPostGrantHoldCount))
    $Result.runtimeUnlockRetryDispatchPostGrantHoldDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_post_grant_hold_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_post_grant_hold_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_post_grant_hold_unresolved_count' -DefaultValue ([string]$Result.runtimeUnlockRetryDispatchPostGrantHoldCount))
    $Result.runtimeUnlockRetryDispatchPostGrantHoldVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_post_grant_hold_verdict' -DefaultValue 'none'
    $Result.runtimeUnlockPreCommitProbeStartHandoffMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_precommit_probe_start_handoff_missing_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_precommit_probe_start_handoff_missing_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_precommit_probe_start_handoff_missing_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_precommit_probe_start_handoff_missing_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockPreCommitProbeStartHandoffVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_precommit_probe_start_handoff_verdict' -DefaultValue 'none'
    $Result.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_install_grant_evidence_verdict' -DefaultValue 'none'
    $Result.runtimeUnlockRetryRegularV4RecoverySettlementMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_regular_v4_recovery_settlement_missing_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryRegularV4RecoverySettlementMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_regular_v4_recovery_settlement_missing_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryRegularV4RecoverySettlementMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_regular_v4_recovery_settlement_missing_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_regular_v4_recovery_settlement_missing_unresolved_count' -DefaultValue (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_regular_v4_recovery_settlement_missing_count' -DefaultValue '0'))
    $Result.runtimeUnlockRetryRegularV4RecoverySettlementVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_regular_v4_recovery_settlement_verdict' -DefaultValue 'none'
    $Result.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict' -DefaultValue 'none'
    $Result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_precommit_fallback_recovery_handoff_suppressed_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_precommit_fallback_recovery_handoff_suppressed_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_precommit_fallback_recovery_handoff_suppressed_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_precommit_fallback_recovery_handoff_suppressed_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_precommit_fallback_recovery_handoff_suppression_verdict' -DefaultValue 'none'
    $Result.regularV4FrontierRepairMissingRangeNotPreservedRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_missing_range_not_preserved_raw_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairMissingRangeNotPreservedDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_missing_range_not_preserved_distinct_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_missing_range_not_preserved_recovered_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairMissingRangePreservationVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_missing_range_preservation_verdict' -DefaultValue 'none'
    $Result.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count' -DefaultValue '0')
    $Result.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count' -DefaultValue '0')
    $Result.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count' -DefaultValue '0')
    $Result.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count' -DefaultValue '0')
    $Result.regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict' -DefaultValue 'none'
    $Result.regularV4FrontierRepairSuccessorPressureInstallMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_successor_pressure_install_missing_raw_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairSuccessorPressureInstallVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_successor_pressure_install_verdict' -DefaultValue 'none'
    $Result.regularV4FrontierRepairSettlementDispositionUnverifiableRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairSettlementDispositionVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_settlement_disposition_verdict' -DefaultValue 'none'
    $Result.regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_pressure_authority_lifecycle_split_raw_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_pressure_authority_lifecycle_split_distinct_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_pressure_authority_lifecycle_split_recovered_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_pressure_authority_lifecycle_split_unresolved_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairPressureAuthorityLifecycleSplitVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_pressure_authority_lifecycle_split_verdict' -DefaultValue 'none'
    $Result.regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_pressure_recovery_handoff_missing_raw_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_pressure_recovery_handoff_missing_distinct_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_pressure_recovery_handoff_missing_recovered_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_pressure_recovery_handoff_missing_unresolved_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairPressureRecoveryHandoffVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_pressure_recovery_handoff_verdict' -DefaultValue 'none'
    $Result.regularV4FrontierRepairProjectionReauthorizationMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_projection_reauthorization_missing_raw_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count' -DefaultValue '0')
    $Result.regularV4FrontierRepairProjectionReauthorizationVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_repair_projection_reauthorization_verdict' -DefaultValue 'none'
    $Result.regularV4FrontierTailRepairStatePublicationMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_tail_repair_state_publication_missing_raw_count' -DefaultValue '0')
    $Result.regularV4FrontierTailRepairStatePublicationMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_tail_repair_state_publication_missing_distinct_count' -DefaultValue '0')
    $Result.regularV4FrontierTailRepairStatePublicationMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_tail_repair_state_publication_missing_recovered_count' -DefaultValue '0')
    $Result.regularV4FrontierTailRepairStatePublicationMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_tail_repair_state_publication_missing_unresolved_count' -DefaultValue '0')
    $Result.regularV4FrontierTailRepairStatePublicationVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'regular_v4_frontier_tail_repair_state_publication_verdict' -DefaultValue 'none'
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict' -DefaultValue 'none'
    $Result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_raw_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_distinct_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_recovered_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_unresolved_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_soft_to_hard_handoff_verdict' -DefaultValue 'none'
    $Result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict' -DefaultValue 'none'
    $Result.fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_payload_hold_missing_raw_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_payload_hold_missing_distinct_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionPayloadHoldMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_payload_hold_missing_recovered_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_payload_hold_missing_unresolved_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_raw_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_distinct_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_recovered_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_unresolved_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_raw_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_distinct_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_recovered_count' -DefaultValue '0')
    $Result.fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_unresolved_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionPayloadHoldVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_payload_hold_verdict' -DefaultValue 'none'
    $Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_raw_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_distinct_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_recovered_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_unresolved_count' -DefaultValue '0')
    $Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_bridge_recovery_execution_submission_barrier_drain_verdict' -DefaultValue 'none'
    $Result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_route_transition_draining_transport_grace_terminalization_raw_count' -DefaultValue '0')
    $Result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_route_transition_draining_transport_grace_terminalization_distinct_count' -DefaultValue '0')
    $Result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_route_transition_draining_transport_grace_terminalization_recovered_count' -DefaultValue '0')
    $Result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_route_transition_draining_transport_grace_terminalization_unresolved_count' -DefaultValue '0')
    $Result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'filetransfer_route_transition_draining_transport_grace_terminalization_verdict' -DefaultValue 'none'
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldPreservationVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_preservation_verdict' -DefaultValue 'none'
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_verdict' -DefaultValue 'none'
    $Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckRequestedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_requested_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDecidedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDiscardedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_discarded_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict' -DefaultValue 'none'
    $Result.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_zero_offer_successor_rejected_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_zero_offer_successor_rejected_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_zero_offer_successor_rejected_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_zero_offer_successor_rejected_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchZeroOfferSuccessorVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_zero_offer_successor_verdict' -DefaultValue 'none'
    $Result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_raw_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_distinct_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_recovered_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_unresolved_count' -DefaultValue '0')
    $Result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_verdict' -DefaultValue 'none'
    $Result.firstProofChainFailureEvent = Get-RouteAcceptanceReportValue -Report $operator -Name 'first_proof_chain_failure_event' -DefaultValue '(none)'
    $Result.firstProofChainFailureReason = Get-RouteAcceptanceReportValue -Report $operator -Name 'first_proof_chain_failure_reason' -DefaultValue '(none)'
    $Result.firstProofChainFailureSequence = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'first_proof_chain_failure_sequence' -DefaultValue '0')
    $Result.firstProofChainFailureWriterSequence = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'first_proof_chain_failure_writer_sequence' -DefaultValue '0')
    $Result.semanticFailureDomain = Get-RouteAcceptanceReportValue -Report $operator -Name 'semantic_failure_domain' -DefaultValue '(none)'
    $firstProofSourcePath = Get-RouteAcceptanceReportValue -Report $operator -Name 'first_proof_chain_failure_source_path' -DefaultValue '(none)'
    $firstProofSourceLine = Get-RouteAcceptanceReportValue -Report $operator -Name 'first_proof_chain_failure_line_number' -DefaultValue '0'
    $Result.firstProofChainFailureSource = if ($firstProofSourcePath -eq '(none)') {
        '(none)'
    }
    else {
        '{0}:{1}' -f $firstProofSourcePath, $firstProofSourceLine
    }
    $Result.fallbackReceiverStateChurnWarningCount = Get-RouteAcceptanceKindCount -Value $warningKindCounts -Kind 'fallback_receiver_state_churn'
    $Result.fallbackReceiverStateChurnRawEventCount = Get-RouteAcceptanceKindCount -Value $warningRawEventCounts -Kind 'fallback_receiver_state_churn'
    if ($Result.fallbackReceiverStateChurnWarningCount -gt 0) {
        $Result.fallbackReceiverStateChurnEfficiencyClass = if ($warningCapExceeded.IndexOf('fallback_receiver_state_churn', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            'treadmill_or_over_cap'
        }
        else {
            'recovered_under_cap'
        }
    }
    elseif ($Result.fallbackReceiverStateDeferredCount -gt 0 -or $Result.fallbackReceiverStateCoalescedCount -gt 0) {
        $Result.fallbackReceiverStateChurnEfficiencyClass = 'diagnostic_no_warning'
    }

    if ($Result.hardFailureCount -gt 0) {
        Add-RouteAcceptanceFailure -Result $Result -Message ("operator hard failures observed: {0}" -f $Result.hardFailureCount)
    }

    if ($warningCapExceeded -ne '(none)') {
        Add-RouteAcceptanceFailure -Result $Result -Message ("warning cap exceeded: {0}" -f $warningCapExceeded)
    }

    if ($RequireWarningFree -and ($Result.warningCount -gt 0 -or $Result.operatorVerdict -ne 'PASS')) {
        if (Test-Phase4RegularNknExternalTransportVariance -Result $Result) {
            $Result.environmentalClassification = 'regular_v4_external_transport_churn'
            Add-RouteAcceptanceMeasurementContamination -Result $Result -Reason 'regular_nkn_external_transport_churn'
            return
        }

        Add-RouteAcceptanceFailure -Result $Result -Message ("regular NKN warning-free acceptance failed: verdict={0}; warning_count={1}; warning_kinds={2}" -f $Result.operatorVerdict, $Result.warningCount, (Join-RouteAcceptanceTokenList -Values $Result.warningKinds))
        return
    }

    if (-not $RequireWarningFree -and $Result.operatorVerdict -ne 'PASS') {
        if (@($Result.warningKinds).Count -gt 0 -and
            (Test-RouteAcceptanceAllowedOperatorWarning -ExpectedRoute $Result.finalRoute -Verdict $Result.operatorVerdict -WarningKinds @($Result.warningKinds))) {
            $Result.operatorAcceptedWithWarnings = $true
        }
        else {
            Add-RouteAcceptanceFailure -Result $Result -Message ("operator verdict is not accepted for {0}: verdict={1}; warning_kinds={2}" -f $Result.finalRoute, $Result.operatorVerdict, (Join-RouteAcceptanceTokenList -Values $Result.warningKinds))
        }
    }
}

function Try-ApplySecondTransferSplitProofAcceptance {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)]$Summary
    )

    $expectedFirstLiveText = 'post_tuna_fallback_v6,file_tuna_v4'
    $firstTerminalBeforeReactivation = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $Summary -Name 'firstTransferTerminalBeforeLiveReactivation' -DefaultValue $false)
    $summaryLiveText = Join-RouteAcceptanceTokenList -Values @(Get-JsonPropertyValue -Object $Summary -Name 'liveRouteEpochRouteChanges' -DefaultValue @())
    if ($summaryLiveText -ne $expectedFirstLiveText -and
        -not ($firstTerminalBeforeReactivation -and $summaryLiveText -eq 'post_tuna_fallback_v6')) {
        return $false
    }

    $secondRoutePath = Join-Path $Result.artifactDir 'second-transfer-analysis\filetransfer-route-consistency-summary.txt'
    $secondOperatorPath = Join-Path $Result.artifactDir 'second-transfer-analysis\filetransfer-operator-verdict.txt'
    if (-not (Test-Path -LiteralPath $secondRoutePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $secondOperatorPath -PathType Leaf)) {
        return $false
    }

    $secondRouteSummary = Read-RouteAcceptanceKeyValueArtifact -Path $secondRoutePath
    $secondRouteVerdict = Get-RouteAcceptanceReportValue -Report $secondRouteSummary -Name 'route_consistency_verdict' -DefaultValue '(missing)'
    if ($secondRouteVerdict -ne 'pass') {
        return $false
    }

    $secondRouteChangesText = Get-RouteAcceptanceReportValue -Report $secondRouteSummary -Name 'selected_route_changes' -DefaultValue '(missing)'
    if ($secondRouteChangesText -ne 'file_tuna_v4') {
        return $false
    }

    $secondSelectedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $secondRouteSummary -Name 'route_selected_count' -DefaultValue '0')
    if ($secondSelectedCount -le 0) {
        return $false
    }

    for ($index = 1; $index -le $secondSelectedCount; $index++) {
        $selectedRoute = Get-RouteAcceptanceReportValue -Report $secondRouteSummary -Name ("selected.{0}.route" -f $index) -DefaultValue '(missing)'
        $selectedProtocol = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $secondRouteSummary -Name ("selected.{0}.protocol_version" -f $index) -DefaultValue '0')
        if ($selectedRoute -ne 'file_tuna_v4' -or $selectedProtocol -ne 4) {
            return $false
        }
    }

    $secondOperator = Read-RouteAcceptanceKeyValueArtifact -Path $secondOperatorPath
    $secondOperatorVerdict = Get-RouteAcceptanceReportValue -Report $secondOperator -Name 'verdict' -DefaultValue '(missing)'
    $secondHardFailures = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $secondOperator -Name 'hard_failure_count' -DefaultValue '0')
    $secondWarningKinds = @(Split-RouteAcceptanceTokenList -Value (Get-RouteAcceptanceReportValue -Report $secondOperator -Name 'warning_kinds' -DefaultValue '(none)'))
    $secondWarningCapExceeded = Get-RouteAcceptanceReportValue -Report $secondOperator -Name 'warning_cap_exceeded_kinds' -DefaultValue '(none)'
    if ($secondHardFailures -ne 0 -or $secondWarningCapExceeded -ne '(none)') {
        return $false
    }

    if ($secondOperatorVerdict -ne 'PASS' -and
        -not (Test-RouteAcceptanceAllowedOperatorWarning -ExpectedRoute 'file_tuna_v4' -Verdict $secondOperatorVerdict -WarningKinds $secondWarningKinds)) {
        return $false
    }

    $secondRetainedPath = Join-Path $Result.artifactDir 'filetransfer-second-transfer-retained-log-slice.log'
    if (Test-Path -LiteralPath $secondRetainedPath -PathType Leaf) {
        $secondRetainedText = Get-Content -LiteralPath $secondRetainedPath -Raw
        if ([regex]::IsMatch($secondRetainedText, '(?i)\broute\s*=\s*(file_tuna_v6|diagnostic_regular_nkn_v6)\b')) {
            return $false
        }
    }

    Remove-RouteAcceptanceFailuresMatching -Result $Result -Patterns @(
        'route consistency verdict is',
        'Tuna measured route mismatch',
        'Tuna measured protocol mismatch',
        'live route epoch sequence mismatch',
        'operator hard failures observed',
        'operator verdict is not accepted'
    )

    $Result.routeConsistencyVerdict = 'pass'
    $Result.route = 'file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4'
    $Result.finalRoute = 'file_tuna_v4'
    $Result.protocol = '4'
    $Result.liveRouteEpochRouteChanges = @('post_tuna_fallback_v6', 'file_tuna_v4')
    $Result.liveRouteEpochProofVerdict = 'pass'
    $Result.operatorVerdict = $secondOperatorVerdict
    $Result.hardFailureCount = 0
    $Result.warningKinds = $secondWarningKinds
    $Result.warningCapExceededKinds = $secondWarningCapExceeded
    $Result.operatorAcceptedWithWarnings = $secondOperatorVerdict -ne 'PASS'
    return $true
}

function Get-RouteAcceptanceTunaGuiSetupBootstrapFailure {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    $errorPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json'
    if (-not (Test-Path -LiteralPath $errorPath -PathType Leaf)) {
        return $null
    }

    try {
        $errorSummary = Get-Content -LiteralPath $errorPath -Raw | ConvertFrom-Json
    }
    catch {
        return $null
    }

    $failurePhase = [string](Get-JsonPropertyValue -Object $errorSummary -Name 'failurePhase' -DefaultValue '')
    $failureReason = [string](Get-JsonPropertyValue -Object $errorSummary -Name 'failureReason' -DefaultValue '')
    $errorMessage = [string](Get-JsonPropertyValue -Object $errorSummary -Name 'error' -DefaultValue '')
    $bridgeBootstrapNotReady = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $errorSummary -Name 'bridgeBootstrapNotReady' -DefaultValue $false)
    $routeSelected = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $errorSummary -Name 'routeSelected' -DefaultValue $false)
    $activationOfferSent = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $errorSummary -Name 'activationOfferSent' -DefaultValue $false)
    $activationOfferReceived = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $errorSummary -Name 'activationOfferReceived' -DefaultValue $false)
    $measuredOfferSent = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $errorSummary -Name 'measuredOfferSent' -DefaultValue $false)
    $measuredOfferReceived = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $errorSummary -Name 'measuredOfferReceived' -DefaultValue $false)
    $offerSent = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $errorSummary -Name 'offerSent' -DefaultValue $false)
    $offerReceived = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $errorSummary -Name 'offerReceived' -DefaultValue $false)
    $terminalObserved = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $errorSummary -Name 'terminalObserved' -DefaultValue $false)
    $combined = @($failurePhase, $failureReason, $errorMessage) -join "`n"
    $isBootstrapFailure =
        [string]::Equals($failurePhase, 'preflight_bridge_bootstrap', [System.StringComparison]::OrdinalIgnoreCase) -or
        $bridgeBootstrapNotReady -or
        $combined.IndexOf('nkn_bridge_bootstrap_network_unready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('nkn_bridge_bootstrap_not_ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    if (-not $isBootstrapFailure) {
        return $null
    }

    $scenarioRuntimeEvidence =
        $routeSelected -or
        $activationOfferSent -or
        $activationOfferReceived -or
        $measuredOfferSent -or
        $measuredOfferReceived -or
        $offerSent -or
        $offerReceived -or
        $terminalObserved -or
        $combined.IndexOf('live multi-toggle', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('filetransfer_tuna_gui_live_multi_toggle', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    if ($scenarioRuntimeEvidence) {
        return $null
    }

    $subreason = ''
    foreach ($fieldName in @('failure_subreason', 'subreason')) {
        $match = [regex]::Match($combined, "(?i)(?:^|[;`r`n]\s*)$fieldName=([^;`r`n]+)")
        if ($match.Success) {
            $subreason = $match.Groups[1].Value.Trim()
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($subreason) -or $subreason -eq '(none)') {
        if (-not [string]::IsNullOrWhiteSpace($failureReason) -and
            -not [string]::Equals($failureReason, 'unknown', [System.StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals($failureReason, 'nkn_bridge_bootstrap_network_unready', [System.StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals($failureReason, 'nkn_bridge_bootstrap_not_ready', [System.StringComparison]::OrdinalIgnoreCase)) {
            $subreason = $failureReason
        }
        elseif ($combined.IndexOf('role=helper', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $subreason = 'helper_bridge_host_ready_connect_failed'
        }
        elseif ($combined.IndexOf('HostByAddressAsync failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $combined.IndexOf('Connect failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $combined.IndexOf('RPC call failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $combined.IndexOf('connect EACCES', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $subreason = 'helpee_bridge_host_ready_connect_failed'
        }
        else {
            $subreason = 'nkn_bridge_bootstrap_not_ready'
        }
    }

    return [pscustomobject]@{
        Phase = 'preflight_bridge_bootstrap'
        Reason = $subreason
        Error = if ([string]::IsNullOrWhiteSpace($errorMessage)) { $failureReason } else { $errorMessage }
    }
}

function Assert-Phase4ScenarioRun {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    $expectedRouteChanges = @($Scenario.ExpectedRouteChanges)
    $expectedFinalRoute = [string]$expectedRouteChanges[$expectedRouteChanges.Count - 1]
    $expectedMetadata = Get-RouteAcceptanceRouteMetadata -Route $expectedFinalRoute
    $result = New-RouteAcceptanceRunResult -Name ([string]$Scenario.Name) -ArtifactDir $ArtifactDir -ExpectedRoute $expectedFinalRoute -ExpectedProtocol ([int]$expectedMetadata.Protocol)
    if ($Scenario.Kind -eq 'tuna') {
        $errorPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json'
        if (Test-Path -LiteralPath $errorPath -PathType Leaf) {
            try {
                $errorSummary = Get-Content -LiteralPath $errorPath -Raw | ConvertFrom-Json
                $failurePhase = [string](Get-JsonPropertyValue -Object $errorSummary -Name 'failurePhase' -DefaultValue '')
                $failureReason = [string](Get-JsonPropertyValue -Object $errorSummary -Name 'failureReason' -DefaultValue '')
                $result.guiReportedFailurePhase = $failurePhase
                $result.guiReportedFailureReason = $failureReason
                $result.guiReportedFailureError = [string](Get-JsonPropertyValue -Object $errorSummary -Name 'error' -DefaultValue '')
                if ([string]::Equals($failurePhase, 'harness_log_cursor', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $result.setupFailurePhase = $failurePhase
                    $result.setupFailureReason = $failureReason
                    Add-RouteAcceptanceFailure -Result $result -Message ("Tuna GUI harness failure: phase={0}; reason={1}" -f $failurePhase, $failureReason)
                    return $result
                }
            }
            catch {
                # The normal artifact validation below will report malformed error evidence.
            }
        }

        $setupBootstrapFailure = Get-RouteAcceptanceTunaGuiSetupBootstrapFailure -ArtifactDir $ArtifactDir
        if ($null -ne $setupBootstrapFailure) {
            $result.setupFailurePhase = [string]$setupBootstrapFailure.Phase
            $result.setupFailureReason = [string]$setupBootstrapFailure.Reason
            Add-RouteAcceptanceFailure -Result $result -Message ("Tuna GUI setup failure: phase={0}; reason={1}; error={2}" -f `
                $result.setupFailurePhase,
                $result.setupFailureReason,
                ($(if ([string]::IsNullOrWhiteSpace([string]$setupBootstrapFailure.Error)) { '(none)' } else { [string]$setupBootstrapFailure.Error })))
            return $result
        }
    }

    Assert-Phase4RouteSummary -Result $result -ExpectedRouteChanges $expectedRouteChanges
    if ($Scenario.Kind -eq 'tuna' -and $expectedRouteChanges.Count -gt 1) {
        $expectedLiveRouteChanges = @($expectedRouteChanges | Select-Object -Skip 1)
        $expectedLiveText = Join-RouteAcceptanceTokenList -Values $expectedLiveRouteChanges
        $actualLiveText = Join-RouteAcceptanceTokenList -Values $result.liveRouteEpochRouteChanges
        $acceptsTerminalFallbackPrefix = Test-RouteAcceptanceTerminalFallbackPrefixOutcome `
            -ArtifactDir $ArtifactDir `
            -ActualRouteChanges $result.liveRouteEpochRouteChanges `
            -ExpectedRouteChanges $expectedLiveRouteChanges
        if ($actualLiveText -ne $expectedLiveText -and -not $acceptsTerminalFallbackPrefix) {
            Add-RouteAcceptanceFailure -Result $result -Message ("live route epoch sequence mismatch: expected {0}, actual {1}" -f $expectedLiveText, $actualLiveText)
        }
    }

    Assert-RouteAcceptanceTerminalSummary -Result $result
    Assert-Phase4OperatorVerdict -Result $result -RequireWarningFree:($Scenario.Kind -eq 'regular')

    $retainedLogPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    if (Test-Path -LiteralPath $retainedLogPath -PathType Leaf) {
        $retainedText = Get-Content -LiteralPath $retainedLogPath -Raw
        if (-not [string]::IsNullOrEmpty($retainedText) -and
            $retainedText.IndexOf('file_tuna_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Add-RouteAcceptanceFailure -Result $result -Message 'active file_tuna_v6 evidence was observed'
        }
    }

    if ($Scenario.Kind -eq 'regular') {
        Assert-RouteAcceptanceRegularNknTransportEvidence -Result $result

        $regularCyclesCompleted = $false
        $regularCyclesIntegrityOk = $false
        if (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-live-nkn-summary.txt') {
            $summary = Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt')
            $protocol = Get-RouteAcceptanceReportValue -Report $summary -Name 'data_protocol_version' -DefaultValue '(missing)'
            $result.protocol = $protocol
            if ($protocol -ne '4') {
                Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN protocol mismatch: expected 4, actual {0}" -f $protocol)
            }

            $cyclesCompleted = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'cycles_completed' -DefaultValue '0')
            if ($cyclesCompleted -le 0) {
                Add-RouteAcceptanceFailure -Result $result -Message 'regular NKN completed no cycles'
            }
            else {
                $regularCyclesCompleted = $true
            }

            $result.goodputBytesPerSecond = ConvertTo-RouteAcceptanceDouble -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'average_goodput_bytes_per_second' -DefaultValue '0')
            $result.bridgeBulkSendFailureCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'bridge_bulk_send_failure_count' -DefaultValue '0')
            if ($result.bridgeBulkSendFailureCount -ne 0) {
                Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN bridge_bulk_send_failure_count must be 0, actual {0}" -f $result.bridgeBulkSendFailureCount)
            }
        }

        if (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-live-nkn-cycles.jsonl') {
            $cycleLines = @(Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($cycleLines.Count -eq 0) {
                Add-RouteAcceptanceFailure -Result $result -Message 'regular NKN cycle artifact contained no cycles'
            }

            $allCyclesCompleted = $cycleLines.Count -gt 0
            $allCyclesIntegrityOk = $cycleLines.Count -gt 0
            foreach ($line in $cycleLines) {
                $cycle = $line | ConvertFrom-Json
                $completed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $cycle -Name 'completed' -DefaultValue $false)
                $integrityOk = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $cycle -Name 'integrity_ok' -DefaultValue $false)
                if (-not $completed -or -not $integrityOk) {
                    Add-RouteAcceptanceFailure -Result $result -Message ("regular NKN cycle failed completion/integrity: {0}" -f $line)
                }

                if (-not $completed) {
                    $allCyclesCompleted = $false
                }

                if (-not $integrityOk) {
                    $allCyclesIntegrityOk = $false
                }
            }

            if ($allCyclesCompleted) {
                $regularCyclesCompleted = $true
            }

            if ($allCyclesCompleted -and $allCyclesIntegrityOk) {
                $regularCyclesIntegrityOk = $true
            }
        }

        Set-Phase4RegularNknProgressTimeoutRecoveryStormVariance -Result $result
        $result.completed = $regularCyclesCompleted
        $result.integrityOk = $regularCyclesIntegrityOk
        $result.shaOk = $result.integrityOk
    }
    else {
        $errorPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json'
        if (Test-Path -LiteralPath $errorPath -PathType Leaf) {
            try {
                $errorSummary = Get-Content -LiteralPath $errorPath -Raw | ConvertFrom-Json
                $failurePhase = [string](Get-JsonPropertyValue -Object $errorSummary -Name 'failurePhase' -DefaultValue '')
                $failureReason = [string](Get-JsonPropertyValue -Object $errorSummary -Name 'failureReason' -DefaultValue '')
                $errorMessage = [string](Get-JsonPropertyValue -Object $errorSummary -Name 'error' -DefaultValue '')
                $result.guiReportedFailurePhase = $failurePhase
                $result.guiReportedFailureReason = $failureReason
                $result.guiReportedFailureError = $errorMessage
                # Preserve the harness' legacy setup classification until Phase 5 has
                # enough retained runtime evidence to reclassify it. The raw GUI fields
                # above remain immutable even when the setup fields are cleared later.
                $result.setupFailurePhase = $failurePhase
                $result.setupFailureReason = $failureReason
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna GUI setup failure: phase={0}; reason={1}; error={2}" -f `
                    ($(if ([string]::IsNullOrWhiteSpace($failurePhase)) { '(unknown)' } else { $failurePhase })),
                    ($(if ([string]::IsNullOrWhiteSpace($failureReason)) { '(unknown)' } else { $failureReason })),
                    ($(if ([string]::IsNullOrWhiteSpace($errorMessage)) { '(none)' } else { $errorMessage })))
            }
            catch {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna GUI setup failure artifact could not be parsed: {0}" -f $_.Exception.Message)
            }
        }

        if (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-tuna-gui-summary.json') {
            $summary = Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Raw | ConvertFrom-Json
            $measuredPhase = Get-JsonPropertyValue -Object $summary -Name 'measuredPhase' -DefaultValue $null
            if ($Scenario.Name -eq 'second-transfer-after-reactivation') {
                $secondTransferPhase = Get-JsonPropertyValue -Object $summary -Name 'secondTransfer' -DefaultValue $null
                if ($null -ne $secondTransferPhase) {
                    $measuredPhase = $secondTransferPhase
                }
            }

            $phase = if ($null -ne $measuredPhase) { $measuredPhase } else { $summary }
            $result.completed = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $phase -Name 'completed' -DefaultValue $false)
            $result.integrityOk = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $phase -Name 'integrityOk' -DefaultValue $false)
            $result.shaOk = $result.integrityOk
            $result.goodputBytesPerSecond = ConvertTo-RouteAcceptanceDouble -Value (Get-JsonPropertyValue -Object $phase -Name 'goodputBytesPerSecond' -DefaultValue 0)
            $phaseRoute = [string](Get-JsonPropertyValue -Object $phase -Name 'route' -DefaultValue $result.finalRoute)
            $phaseProtocol = ConvertTo-RouteAcceptanceInt -Value (Get-JsonPropertyValue -Object $phase -Name 'protocolVersion' -DefaultValue $result.expectedProtocol)
            $inboundState = [string](Get-JsonPropertyValue -Object $phase -Name 'inboundState' -DefaultValue '(missing)')
            $outboundState = [string](Get-JsonPropertyValue -Object $phase -Name 'outboundState' -DefaultValue '(missing)')
            $inboundError = [string](Get-JsonPropertyValue -Object $phase -Name 'inboundErrorCode' -DefaultValue '(missing)')
            $outboundError = [string](Get-JsonPropertyValue -Object $phase -Name 'outboundErrorCode' -DefaultValue '(missing)')
            $acceptsTerminalFallbackMeasuredPhase = Test-RouteAcceptanceTerminalFallbackMeasuredPhaseOutcome `
                -ArtifactDir $ArtifactDir `
                -ActualRouteChanges $result.selectedRouteChanges `
                -ExpectedRouteChanges $expectedRouteChanges `
                -PhaseRoute $phaseRoute `
                -PhaseProtocol $phaseProtocol

            if ($phaseRoute -ne $expectedFinalRoute -and -not $acceptsTerminalFallbackMeasuredPhase) {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna measured route mismatch: expected={0}; actual={1}" -f $expectedFinalRoute, $phaseRoute)
            }

            if ($phaseProtocol -ne [int]$expectedMetadata.Protocol -and -not $acceptsTerminalFallbackMeasuredPhase) {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna measured protocol mismatch: expected={0}; actual={1}" -f $expectedMetadata.Protocol, $phaseProtocol)
            }

            if (-not $result.completed -or -not $result.integrityOk) {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna GUI summary failed completion/integrity: completed={0}; integrityOk={1}" -f $result.completed, $result.integrityOk)
            }

            if ($inboundState -ne 'Completed' -or $outboundState -ne 'Completed') {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna terminals not Completed: inbound={0}; outbound={1}" -f $inboundState, $outboundState)
            }

            if ($inboundError -ne '(none)' -or $outboundError -ne '(none)') {
                Add-RouteAcceptanceFailure -Result $result -Message ("Tuna terminal errors observed: inbound={0}; outbound={1}" -f $inboundError, $outboundError)
            }

            if ($Scenario.Name -eq 'second-transfer-after-reactivation') {
                $second = Get-JsonPropertyValue -Object $summary -Name 'secondTransfer' -DefaultValue $null
                $statusPath = Join-Path $result.artifactDir 'filetransfer-second-transfer-status.txt'
                $status = $null
                $hasSecondTransferStartedStatus = $false
                $secondTransferStartedFromStatus = $true
                if (Test-Path -LiteralPath $statusPath -PathType Leaf) {
                    $status = Read-RouteAcceptanceKeyValueArtifact -Path $statusPath
                    if ($status.ContainsKey('second_transfer_started')) {
                        $statusStartedValue = Get-RouteAcceptanceReportValue -Report $status -Name 'second_transfer_started' -DefaultValue ''
                        $hasSecondTransferStartedStatus = $true
                        $secondTransferStartedFromStatus = ConvertTo-RouteAcceptanceBool -Value $statusStartedValue
                    }
                }
                $secondTransferStarted = $true
                if ($null -eq $second) {
                    if ($hasSecondTransferStartedStatus -and -not $secondTransferStartedFromStatus) {
                        $secondTransferStarted = $false
                    }
                    else {
                        $secondRetainedPath = Join-Path $result.artifactDir 'filetransfer-second-transfer-retained-log-slice.log'
                        $secondRouteAnalysisPath = Join-Path $result.artifactDir 'second-transfer-analysis\filetransfer-route-consistency-summary.txt'
                        if (-not ((Test-Path -LiteralPath $secondRetainedPath -PathType Leaf) -and
                                (Test-Path -LiteralPath $secondRouteAnalysisPath -PathType Leaf))) {
                            Add-RouteAcceptanceFailure -Result $result -Message 'second transfer evidence is missing'
                        }
                    }
                }
                else {
                    $secondTransferStarted = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $second -Name 'secondTransferStarted' -DefaultValue $true)
                    $secondRoute = [string](Get-JsonPropertyValue -Object $second -Name 'route' -DefaultValue '(missing)')
                    $secondProtocol = ConvertTo-RouteAcceptanceInt -Value (Get-JsonPropertyValue -Object $second -Name 'protocolVersion' -DefaultValue 0)
                    $secondCompleted = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $second -Name 'completed' -DefaultValue $false)
                    $secondIntegrity = ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $second -Name 'integrityOk' -DefaultValue $false)
                    $secondSetupPhase = [string](Get-JsonPropertyValue -Object $second -Name 'setupFailurePhase' -DefaultValue '')
                    $secondSetupReason = [string](Get-JsonPropertyValue -Object $second -Name 'setupFailureReason' -DefaultValue '')
                    $secondSetupPhaseKnown = -not [string]::IsNullOrWhiteSpace($secondSetupPhase) -and
                        -not [string]::Equals($secondSetupPhase, 'unknown', [System.StringComparison]::OrdinalIgnoreCase)
                    $secondSetupReasonKnown = -not [string]::IsNullOrWhiteSpace($secondSetupReason) -and
                        -not [string]::Equals($secondSetupReason, 'unknown', [System.StringComparison]::OrdinalIgnoreCase)
                    if ($secondSetupPhaseKnown -or $secondSetupReasonKnown) {
                        if ($secondSetupPhaseKnown) {
                            $result.setupFailurePhase = $secondSetupPhase
                        }
                        if ($secondSetupReasonKnown) {
                            $result.setupFailureReason = $secondSetupReason
                        }
                        Add-RouteAcceptanceFailure -Result $result -Message ("second transfer setup failure after reactivation: phase={0}; reason={1}" -f `
                            ($(if ([string]::IsNullOrWhiteSpace($secondSetupPhase)) { '(unknown)' } else { $secondSetupPhase })),
                            ($(if ([string]::IsNullOrWhiteSpace($secondSetupReason)) { '(unknown)' } else { $secondSetupReason })))
                    }

                    if ($secondRoute -ne 'file_tuna_v4' -or $secondProtocol -ne 4) {
                        Add-RouteAcceptanceFailure -Result $result -Message ("second transfer route mismatch after reactivation: expected file_tuna_v4/4, actual {0}/{1}" -f $secondRoute, $secondProtocol)
                    }

                    if (-not $secondCompleted -or -not $secondIntegrity) {
                        Add-RouteAcceptanceFailure -Result $result -Message ("second transfer failed completion/integrity: completed={0}; integrityOk={1}" -f $secondCompleted, $secondIntegrity)
                    }
                }

                if (-not $secondTransferStarted) {
                    if ($null -ne $status) {
                        $failureClass = Get-RouteAcceptanceReportValue -Report $status -Name 'failure_class' -DefaultValue 'first_transfer_reactivation_or_fallback_incomplete'
                        $failureReason = Get-RouteAcceptanceReportValue -Report $status -Name 'failure_reason' -DefaultValue '(unknown)'
                        Add-RouteAcceptanceFailure -Result $result -Message ("second transfer did not start because first transfer setup failed: class={0}; reason={1}" -f $failureClass, $failureReason)
                    }
                    else {
                        Add-RouteAcceptanceFailure -Result $result -Message 'second transfer did not start and status artifact is missing'
                    }
                }
                else {
                    if (-not (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'filetransfer-second-transfer-retained-log-slice.log')) {
                        Add-RouteAcceptanceFailure -Result $result -Message 'second transfer retained log slice is missing'
                    }

                    if (-not (Assert-RouteAcceptanceFileExists -Result $result -RelativePath 'second-transfer-analysis\filetransfer-route-consistency-summary.txt')) {
                        Add-RouteAcceptanceFailure -Result $result -Message 'second transfer retained route analysis is missing'
                    }
                }

                [void](Try-ApplySecondTransferSplitProofAcceptance -Result $result -Summary $summary)
            }
        }
    }

    Set-Phase4MeasurementContamination -Scenario $Scenario -Result $result

    $baselineGoodput = 0D
    if ($null -ne $Scenario.Baseline) {
        $baselineGoodput = ConvertTo-RouteAcceptanceDouble -Value $Scenario.Baseline.goodputBytesPerSecond
        if ($baselineGoodput -gt 0) {
            $result.baselineGoodputBytesPerSecond = $baselineGoodput
            $floor = $baselineGoodput * (1D - ([Math]::Max(0D, $GoodputRegressionTolerancePercent) / 100D))
            $result.goodputRegressionFloorBytesPerSecond = $floor
            if ($result.goodputBytesPerSecond -gt 0) {
                $result.goodputRegressionPercent = (($baselineGoodput - $result.goodputBytesPerSecond) / $baselineGoodput) * 100D
            }
        }
    }

    if ($result.measurementContaminated) {
        Add-RouteAcceptanceFailure -Result $result -Message ("measurement contaminated: {0}" -f (Join-RouteAcceptanceTokenList -Values $result.measurementContaminationReasons))
    }
    elseif ($baselineGoodput -gt 0) {
        if ($result.goodputBytesPerSecond -lt $result.goodputRegressionFloorBytesPerSecond) {
            Add-RouteAcceptanceFailure -Result $result -Message ("goodput regression exceeded {0:F1}%: current={1:F3}; floor={2:F3}; baseline={3:F3}" -f $GoodputRegressionTolerancePercent, $result.goodputBytesPerSecond, $result.goodputRegressionFloorBytesPerSecond, $baselineGoodput)
        }
    }

    return $result
}

function Get-RouteAcceptanceRouteMetadata {
    param([Parameter(Mandatory = $true)][string]$Route)

    switch ($Route) {
        'regular_nkn_v4_fast' {
            return [pscustomobject]@{ Protocol = 4; Runtime = 'regular_nkn_v4_fast'; Bridge = 'regular_nkn_v4_fast'; FrameFamily = 'v4'; SenderStarted = 'filetransfer_v4_sender_started'; ReceiverStarted = 'filetransfer_v4_receiver_started'; FileTuna = 0; Fallback = 0; Diagnostic = 0 }
        }
        'file_tuna_v4' {
            return [pscustomobject]@{ Protocol = 4; Runtime = 'file_tuna_v4'; Bridge = 'tuna_strict'; FrameFamily = 'v4'; SenderStarted = 'filetransfer_v4_sender_started'; ReceiverStarted = 'filetransfer_v4_receiver_started'; FileTuna = 1; Fallback = 0; Diagnostic = 0 }
        }
        'post_tuna_fallback_v6' {
            return [pscustomobject]@{ Protocol = 6; Runtime = 'default_v6'; Bridge = 'post_tuna_fallback_strict'; FrameFamily = 'v6'; SenderStarted = 'filetransfer_v6_sender_started'; ReceiverStarted = 'filetransfer_v6_receiver_started'; FileTuna = 0; Fallback = 1; Diagnostic = 0 }
        }
        'diagnostic_regular_nkn_v6' {
            return [pscustomobject]@{ Protocol = 6; Runtime = 'primary_regular_nkn_bulk_v6'; Bridge = 'primary_regular_nkn_quiet'; FrameFamily = 'v6'; SenderStarted = 'filetransfer_v6_sender_started'; ReceiverStarted = 'filetransfer_v6_receiver_started'; FileTuna = 0; Fallback = 0; Diagnostic = 1 }
        }
        'file_tuna_v6' {
            return [pscustomobject]@{ Protocol = 6; Runtime = 'file_tuna_v6'; Bridge = 'tuna_strict'; FrameFamily = 'v6'; SenderStarted = 'filetransfer_v6_sender_started'; ReceiverStarted = 'filetransfer_v6_receiver_started'; FileTuna = 1; Fallback = 0; Diagnostic = 0 }
        }
        default {
            return [pscustomobject]@{ Protocol = 4; Runtime = 'regular_nkn_v4_fast'; Bridge = 'regular_nkn_v4_fast'; FrameFamily = 'v4'; SenderStarted = 'filetransfer_v4_sender_started'; ReceiverStarted = 'filetransfer_v4_receiver_started'; FileTuna = 0; Fallback = 0; Diagnostic = 0 }
        }
    }
}

function New-RouteAcceptanceFakeLogLine {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [int]$SecondsOffset = 0
    )

    $timestamp = ([datetime]::UtcNow.AddSeconds($SecondsOffset)).ToString("yyyy-MM-dd HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    return ('[{0}] [INFO] [RouteAcceptanceFake] {1}' -f $timestamp, $Message)
}

function Write-RouteAcceptanceFakeRetainedLog {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$TransferId,
        [Parameter(Mandatory = $true)][string]$Route,
        [int]$ProtocolOverride = 0,
        [string]$TerminalState = 'Completed',
        [int]$BridgeBulkSendFailures = 0
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $metadata = Get-RouteAcceptanceRouteMetadata -Route $Route
    $protocol = if ($ProtocolOverride -gt 0) { $ProtocolOverride } else { [int]$metadata.Protocol }
    $frameFamily = if ($protocol -eq 6) { 'v6' } else { 'v4' }
    $frameType = 'filetransfer.chunk_batch.{0}' -f $frameFamily
    $sessionId = 'route-acceptance-session'
    $terminalError = if ($TerminalState -eq 'Completed') { '(none)' } else { 'pending' }
    $rawBytes = 67108864L
    $publicationSuffix = Get-RouteAcceptanceFakePublicationAuthoritySuffix -Route $Route -Protocol $protocol

    $lines = @(
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_route_selected; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; handoff_kind=none; bridge_recovery_policy={6}; liveness_terminal_policy={2}; selection_reason=route_acceptance_fake; file_tuna_active={7}; post_tuna_fallback_active={8}; diagnostic_regular_nkn_v6={9}; transport_profile=nkn{10}" -f $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge, $metadata.FileTuna, $metadata.Fallback, $metadata.Diagnostic, $publicationSuffix))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_route_selected; direction=inbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; handoff_kind=none; bridge_recovery_policy={6}; liveness_terminal_policy={2}; selection_reason=route_acceptance_fake; file_tuna_active={7}; post_tuna_fallback_active={8}; diagnostic_regular_nkn_v6={9}; transport_profile=nkn{10}" -f $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge, $metadata.FileTuna, $metadata.Fallback, $metadata.Diagnostic, $publicationSuffix))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_protocol_negotiated; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}; selection_reason=route_acceptance_fake" -f $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_session_opened; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}; reason=role=Sender" -f $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_bridge_recovery_policy_selected; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}" -f $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -Message ("event={0}; direction=outbound; transfer_id={1}; session_id={2}; route={3}; protocol_version={4}; runtime_profile={5}; frame_family={6}; bridge_recovery_policy={7}" -f $metadata.SenderStarted, $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -Message ("event={0}; direction=inbound; transfer_id={1}; session_id={2}; route={3}; protocol_version={4}; runtime_profile={5}; frame_family={6}; bridge_recovery_policy={7}" -f $metadata.ReceiverStarted, $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}{7}" -f $TransferId, $sessionId, $Route, $protocol, $metadata.Runtime, $frameFamily, $metadata.Bridge, $publicationSuffix))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_binary_frame_sent; transfer_id={0}; session_id={1}; frame_type={2}; chunk_index=0-31; payload_bytes={3}; serialized_payload_bytes={3}; raw_chunk_bytes={3}; chunk_count=32" -f $TransferId, $sessionId, $frameType, $rawBytes))
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_binary_frame_received; transfer_id={0}; session_id={1}; frame_type={2}; chunk_index=0-31; raw_chunk_bytes={3}; chunk_count=32" -f $TransferId, $sessionId, $frameType, $rawBytes))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset 120 -Message ("event=file_transfer_inbound_terminal; role=helper; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; saved_path=(none); integrity_ok={5}" -f $sessionId, $TransferId, $TerminalState, $terminalError, $rawBytes, ($(if ($TerminalState -eq 'Completed') { 1 } else { 0 }))))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset 120 -Message ("event=file_transfer_outbound_terminal; role=helpee; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; integrity_ok={5}" -f $sessionId, $TransferId, $TerminalState, $terminalError, $rawBytes, ($(if ($TerminalState -eq 'Completed') { 1 } else { 0 }))))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset 110 -Message ("event=nkn_bridge_bulk_send_summary; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; frames_sent=1; send_failures={0}; queue_clears=0; payload_bytes_sent={1}; payload_bytes_per_second=6000000; send_p95_ms=1; configured_concurrency=4; effective_concurrency=4" -f $BridgeBulkSendFailures, $rawBytes))
    )

    $lines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8
}

function New-RouteAcceptanceRouteSelectedLogLine {
    param(
        [Parameter(Mandatory = $true)][string]$TransferId,
        [Parameter(Mandatory = $true)][string]$SessionId,
        [Parameter(Mandatory = $true)][string]$Direction,
        [Parameter(Mandatory = $true)][string]$Route,
        [string]$HandoffKind = 'none',
        [int]$LiveRouteEpoch = 0,
        [int]$SecondsOffset = 0
    )

    $metadata = Get-RouteAcceptanceRouteMetadata -Route $Route
    $protocol = [int]$metadata.Protocol
    $suffix = Get-RouteAcceptanceFakePublicationAuthoritySuffix -Route $Route -Protocol $protocol -LiveRouteEpoch $LiveRouteEpoch -HandoffKind $HandoffKind
    return New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message (
        "event=filetransfer_route_selected; direction={0}; transfer_id={1}; session_id={2}; route={3}; protocol_version={4}; runtime_profile={5}; frame_family={6}; handoff_kind={7}; bridge_recovery_policy={8}; liveness_terminal_policy={3}; selection_reason=phase4_fake; file_tuna_active={9}; post_tuna_fallback_active={10}; diagnostic_regular_nkn_v6={11}; transport_profile=nkn{12}" -f
            $Direction,
            $TransferId,
            $SessionId,
            $Route,
            $protocol,
            $metadata.Runtime,
            $metadata.FrameFamily,
            $HandoffKind,
            $metadata.Bridge,
            $metadata.FileTuna,
            $metadata.Fallback,
            $metadata.Diagnostic,
            $suffix)
}

function Get-RouteAcceptanceFakePublicationAuthoritySuffix {
    param(
        [Parameter(Mandatory = $true)][string]$Route,
        [Parameter(Mandatory = $true)][int]$Protocol,
        [int]$LiveRouteEpoch = 0,
        [string]$HandoffKind = 'none'
    )

    $targetTransport = if ($Route -eq 'file_tuna_v4') {
        'tuna'
    }
    elseif ($Route -eq 'post_tuna_fallback_v6') {
        'regular_nkn'
    }
    else {
        'regular_nkn'
    }
    $actionKind = if ($Route -eq 'file_tuna_v4' -and $HandoffKind -eq 'normal_to_tuna_activation' -and $LiveRouteEpoch -gt 0) {
        'RouteCommitPublication'
    }
    else {
        'RoutePublication'
    }
    $routeCommitGeneration = if ($actionKind -eq 'RouteCommitPublication') { [Math]::Max(1, $LiveRouteEpoch) } else { 0 }
    $authorityGeneration = [Math]::Max(1, $LiveRouteEpoch + 1)
    $legGeneration = [Math]::Max(1, $LiveRouteEpoch + 1)

    return ("; live_route_epoch={0}; runtime_generation={1}; terminal_generation=0; leg_generation={2}; route_publication_generation={1}; route_commit_generation={3}; target_route={4}; target_protocol_version={5}; target_transport={6}; route_publication_action_kind={7}; coordinator_decision_reason=accepted" -f
        $LiveRouteEpoch,
        $authorityGeneration,
        $legGeneration,
        $routeCommitGeneration,
        $Route,
        $Protocol,
        $targetTransport,
        $actionKind)
}

function New-RouteAcceptanceRuntimeLogLines {
    param(
        [Parameter(Mandatory = $true)][string]$TransferId,
        [Parameter(Mandatory = $true)][string]$SessionId,
        [Parameter(Mandatory = $true)][string]$Route,
        [int]$SecondsOffset = 0
    )

    $metadata = Get-RouteAcceptanceRouteMetadata -Route $Route
    $protocol = [int]$metadata.Protocol
    $publicationSuffix = Get-RouteAcceptanceFakePublicationAuthoritySuffix -Route $Route -Protocol $protocol
    return @(
        (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event=filetransfer_protocol_negotiated; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}; selection_reason=phase4_fake" -f $TransferId, $SessionId, $Route, $protocol, $metadata.Runtime, $metadata.FrameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event=filetransfer_session_opened; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}; reason=role=Sender" -f $TransferId, $SessionId, $Route, $protocol, $metadata.Runtime, $metadata.FrameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event={0}; direction=outbound; transfer_id={1}; session_id={2}; route={3}; protocol_version={4}; runtime_profile={5}; frame_family={6}; bridge_recovery_policy={7}" -f $metadata.SenderStarted, $TransferId, $SessionId, $Route, $protocol, $metadata.Runtime, $metadata.FrameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event={0}; direction=inbound; transfer_id={1}; session_id={2}; route={3}; protocol_version={4}; runtime_profile={5}; frame_family={6}; bridge_recovery_policy={7}" -f $metadata.ReceiverStarted, $TransferId, $SessionId, $Route, $protocol, $metadata.Runtime, $metadata.FrameFamily, $metadata.Bridge))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; runtime_profile={4}; frame_family={5}; bridge_recovery_policy={6}{7}" -f $TransferId, $SessionId, $Route, $protocol, $metadata.Runtime, $metadata.FrameFamily, $metadata.Bridge, $publicationSuffix))
    )
}

function New-RouteAcceptanceLiveRouteEpochLogLines {
    param(
        [Parameter(Mandatory = $true)][string]$TransferId,
        [Parameter(Mandatory = $true)][string]$SessionId,
        [Parameter(Mandatory = $true)][string]$Route,
        [Parameter(Mandatory = $true)][int]$LiveRouteEpoch,
        [int]$SecondsOffset = 0,
        [switch]$TransportOnly,
        [switch]$MissingMetadata
    )

    $metadata = Get-RouteAcceptanceRouteMetadata -Route $Route
    $protocol = [int]$metadata.Protocol
    $handoffKind = if ($Route -eq 'post_tuna_fallback_v6') { 'tuna_to_normal_fallback' } elseif ($Route -eq 'file_tuna_v4') { 'normal_to_tuna_activation' } else { 'none' }
    $targetTransport = if ($Route -eq 'post_tuna_fallback_v6') { 'regular_nkn' } elseif ($Route -eq 'file_tuna_v4') { 'tuna' } else { 'regular_nkn' }

    if ($TransportOnly) {
        return @(
            (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event=filetransfer_v6_epoch_started; direction=outbound; transfer_id={0}; session_id={1}; handoff_kind={2}; target_transport={3}; transport_epoch={4}" -f $TransferId, $SessionId, $handoffKind, $targetTransport, $LiveRouteEpoch))
            (New-RouteAcceptanceFakeLogLine -SecondsOffset ($SecondsOffset + 1) -Message ("event=filetransfer_v6_epoch_recovered; direction=outbound; transfer_id={0}; session_id={1}; handoff_kind={2}; target_transport={3}; transport_epoch={4}" -f $TransferId, $SessionId, $handoffKind, $targetTransport, $LiveRouteEpoch))
        )
    }

    if ($MissingMetadata) {
        return @(
            (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={0}; session_id={1}; live_route_epoch={2}" -f $TransferId, $SessionId, $LiveRouteEpoch))
            (New-RouteAcceptanceFakeLogLine -SecondsOffset ($SecondsOffset + 1) -Message ("event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={0}; session_id={1}; live_route_epoch={2}" -f $TransferId, $SessionId, $LiveRouteEpoch))
        )
    }

    $publicationSuffix = Get-RouteAcceptanceFakePublicationAuthoritySuffix -Route $Route -Protocol $protocol -LiveRouteEpoch $LiveRouteEpoch -HandoffKind $handoffKind
    return @(
        (New-RouteAcceptanceFakeLogLine -SecondsOffset $SecondsOffset -Message ("event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; handoff_kind={4}; target_transport={5}{6}" -f $TransferId, $SessionId, $Route, $protocol, $handoffKind, $targetTransport, $publicationSuffix))
        (New-RouteAcceptanceFakeLogLine -SecondsOffset ($SecondsOffset + 1) -Message ("event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; handoff_kind={4}; target_transport={5}{6}" -f $TransferId, $SessionId, $Route, $protocol, $handoffKind, $targetTransport, $publicationSuffix))
    )
}

function Write-RouteAcceptanceFakePhase4Run {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [int]$RerunAttempt = 0
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $scenarioName = [string]$Scenario.Name
    $forceExecutionFailure = (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'EXECUTION_FAIL') -or
        ($RerunAttempt -gt 0 -and (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_EXECUTION_FAIL'))
    $forcePostArtifactExecutionFailure = ($RerunAttempt -le 0 -and (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'POST_ARTIFACT_EXECUTION_FAIL')) -or
        ($RerunAttempt -gt 0 -and (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_POST_ARTIFACT_EXECUTION_FAIL'))
    $receiveRecoveryExhaustedBeforeRuntimeUnlock = $scenarioName -eq 'regular-v4-live-activation-off-on-off-256mb' -and
        ($(if ($RerunAttempt -gt 0) {
            Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_RECEIVE_RECOVERY_EXHAUSTED_BEFORE_RUNTIME_UNLOCK'
        }
        else {
            Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RECEIVE_RECOVERY_EXHAUSTED_BEFORE_RUNTIME_UNLOCK'
        }))
    $receiveRecoveryLivenessTimeoutBeforeRuntimeUnlock = $scenarioName -eq 'regular-v4-live-activation-off-on-off-256mb' -and
        ($(if ($RerunAttempt -gt 0) {
            Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_RECEIVE_RECOVERY_LIVENESS_TIMEOUT_BEFORE_RUNTIME_UNLOCK'
        }
        else {
            Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RECEIVE_RECOVERY_LIVENESS_TIMEOUT_BEFORE_RUNTIME_UNLOCK'
        }))
    $transientSetupFailure = $Scenario.Kind -eq 'tuna' -and
        ($(if ($RerunAttempt -gt 0) {
            Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_TRANSIENT_SETUP_FAILURE'
        }
        else {
            Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'TRANSIENT_SETUP_FAILURE'
        }))
    if ($forceExecutionFailure) {
        [ordered]@{
            event = 'filetransfer_tuna_gui_handoff_fallback_failure'
            routeMode = [string]$Scenario.RouteMode
            direction = 'helpee-to-helper'
            payerMode = [string]$Scenario.PayerMode
            faultMode = [string]$Scenario.Fault
            payloadBytes = [long]$Scenario.PayloadBytes
            completed = $false
            integrityOk = $false
            failurePhase = 'measured_accept_wait'
            failureReason = 'offer_sent_accept_not_enabled'
            tunaActive = $true
            listenerReady = $true
            listenerUnavailable = $false
            routeSelected = $true
            offerSent = $true
            offerReceived = $false
            terminalObserved = $false
            error = 'Injected fake Phase 4 scenario execution failure.'
            failedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json') -Encoding UTF8
        throw 'Injected fake Phase 4 scenario execution failure.'
    }

    $preTransferSetupExpired = $Scenario.Kind -eq 'regular' -and
        ($(if ($RerunAttempt -gt 0) {
            Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_PRETRANSFER_SETUP_EXPIRED'
        }
        else {
            Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'PRETRANSFER_SETUP_EXPIRED'
        }))
    if ($preTransferSetupExpired) {
        @(
            'artifact_kind=live-nkn',
            'mode=nkn-fast',
            'verdict=FAIL_PROTOCOL_OR_INTEGRITY',
            'gate_status=fail',
            'gui_harness_exit_code=0',
            'cycles_requested=1',
            'cycles_observed=0',
            'cycles_completed=0',
            'total_payload_bytes=0',
            'average_goodput_bytes_per_second=0.000',
            'min_goodput_bytes_per_second=0.000',
            'data_protocol_version=0',
            'bridge_bulk_send_failure_count=0',
            'bridge_bulk_queue_clear_count=0',
            'gui_progress_timeout_count=0',
            'observed_start_utc=2026-06-05 22:31:20Z',
            'observed_end_utc=2026-06-05 22:31:20Z'
        ) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt') -Encoding UTF8
        [ordered]@{
            artifact_kind = 'live-nkn'
            mode = 'nkn-fast'
            verdict = 'FAIL_PROTOCOL_OR_INTEGRITY'
            gate_status = 'fail'
            cycles_requested = 1
            cycles_observed = 0
            cycles_completed = 0
            total_payload_bytes = 0
            average_goodput_bytes_per_second = 0
            min_goodput_bytes_per_second = 0
            data_protocol_version = 0
            bridge_bulk_send_failure_count = 0
            bridge_bulk_queue_clear_count = 0
            gui_progress_timeout_count = 0
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.json') -Encoding UTF8
        @(
            'transfer_id=(all)',
            'route_consistency_verdict=legacy',
            'route_selected_count=0',
            'route_aware_event_count=0',
            'route_mismatch_count=0',
            'selected_routes=(none)',
            'selected_route_sequence=(none)',
            'selected_route_changes=(none)',
            'live_route_epoch_proof_mode=None',
            'live_route_epoch_proof_verdict=not_required',
            'fallback_leg_authority_proof_verdict=none',
            'bridge_liveness_integration_verdict=none'
        ) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-route-consistency-summary.txt') -Encoding UTF8
        @(
            'transfer_id=(all)',
            'verdict=INCONCLUSIVE',
            'inbound_terminal_count=0',
            'outbound_terminal_count=0',
            'terminal_states=(none)',
            'terminal_error_codes=(none)',
            'analyzed_file_count=1',
            '',
            'terminal_evidence:',
            '(none)'
        ) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'transfer-terminal-summary.txt') -Encoding UTF8
        @(
            'verdict=INCONCLUSIVE',
            'gate_status=inconclusive',
            'transfer_id=(all)',
            'hard_failure_count=0',
            'warning_count=1',
            'warning_kinds=transfer_frames_are_present_but_terminal_evidence_is_missing',
            'warning_cap_policy=strict_small',
            'warning_cap_count_limit=3',
            'warning_cap_rate_limit_per_second=0.05',
            'warning_kind_counts=(none)',
            'warning_kind_raw_event_counts=(none)',
            'warning_kind_rates_per_second=(none)',
            'warning_cap_exceeded_kinds=(none)',
            'live_route_epoch_proof_mode=None',
            'live_route_epoch_proof_verdict=not_required',
            'fallback_leg_authority_proof_verdict=none',
            'bridge_liveness_integration_verdict=none'
        ) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-operator-verdict.txt') -Encoding UTF8
        @(
            '[GUI Smoke] FAIL: Timed out waiting for helpee Allow approval UI. helper_status=''Waiting for help requests...''; helper_banner=''(unavailable)''; helpee_status=''The help request expired.''; helpee_banner=''(unavailable)'''
        ) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'gui-smoke-stdout.log') -Encoding UTF8
        @(
            (New-RouteAcceptanceFakeLogLine -SecondsOffset 0 -Message 'event=filetransfer_artifact_slice_summary; transfer_id=(all); artifact_slice_start_reason=live_soak_retained_slice; artifact_slice_end_reason=soak_completed_or_harness_exit'),
            (New-RouteAcceptanceFakeLogLine -SecondsOffset 1 -Message 'event=filetransfer_live_matrix_incomplete; transfer_id=(all); reason=cycle_integrity_or_terminal_failure; cycles_requested=1; cycles_completed=0; cycles_observed=0; gui_progress_timeout=0')
        ) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8
        throw 'Regular NKN 64MiB failed with exit code 1.'
    }

    $defaultRouteChanges = @($Scenario.ExpectedRouteChanges)
    $routeOverride = if ($receiveRecoveryExhaustedBeforeRuntimeUnlock -or $receiveRecoveryLivenessTimeoutBeforeRuntimeUnlock) {
        'regular_nkn_v4_fast'
    }
    else {
        Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix 'ROUTE' -DefaultValue ''
    }
    $routeChangeList = New-Object System.Collections.Generic.List[string]
    $sourceRouteChanges = if (-not [string]::IsNullOrWhiteSpace($routeOverride)) { @(Split-RouteAcceptanceTokenList -Value $routeOverride) } else { @($defaultRouteChanges) }
    foreach ($routeChange in @($sourceRouteChanges)) {
        $routeText = [string]$routeChange
        if (-not [string]::IsNullOrWhiteSpace($routeText)) {
            $routeChangeList.Add($routeText) | Out-Null
        }
    }

    $routeChanges = @($routeChangeList.ToArray())
    $finalRoute = [string]$routeChanges[$routeChanges.Count - 1]
    $terminalBeforeLiveReactivation = (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'TERMINAL_BEFORE_LIVE_REACTIVATION') -or
        (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'TERMINAL_BEFORE_SECOND_TUNA_ACTIVATION')
    $terminalBeforeFallbackSwitchOff = (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'TERMINAL_BEFORE_FALLBACK_BOUNDARY') -or
        (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'TERMINAL_BEFORE_FINAL_FALLBACK')
    $defaultGoodput = if ($null -ne $Scenario.Baseline -and (ConvertTo-RouteAcceptanceDouble -Value $Scenario.Baseline.goodputBytesPerSecond) -gt 0) {
        [string]$Scenario.Baseline.goodputBytesPerSecond
    }
    elseif ($Scenario.Kind -eq 'regular') {
        '2000000'
    }
    elseif ($finalRoute -eq 'file_tuna_v4') {
        '5000000'
    }
    else {
        '650000'
    }

    $goodputSuffix = if ($RerunAttempt -gt 0) { 'RERUN_GOODPUT_BPS' } else { 'GOODPUT_BPS' }
    $goodput = ConvertTo-RouteAcceptanceDouble -Value (Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix $goodputSuffix -DefaultValue (Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix 'GOODPUT_BPS' -DefaultValue $defaultGoodput))
    $payloadBytes = [long]$Scenario.PayloadBytes
    $secondTransferNotStarted = $false
    $completed = -not (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'SHA_FAIL')
    $terminalState = if ($receiveRecoveryExhaustedBeforeRuntimeUnlock -or $receiveRecoveryLivenessTimeoutBeforeRuntimeUnlock -or (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'TERMINAL_ERROR')) { 'Failed' } else { 'Completed' }
    if (-not $completed) {
        $terminalState = 'Completed'
    }
    if ($receiveRecoveryExhaustedBeforeRuntimeUnlock -or $receiveRecoveryLivenessTimeoutBeforeRuntimeUnlock) {
        $completed = $false
    }

    $hardFailure = if ($RerunAttempt -gt 0) {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_HARD_FAILURE'
    }
    else {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'HARD_FAILURE'
    }
    $missingLiveProof = Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'MISSING_LIVE_PROOF'
    $transportOnlyLiveProof = Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'TRANSPORT_ONLY_LIVE_PROOF'
    $missingLiveMetadata = Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'MISSING_LIVE_METADATA'
    $bridgeLivenessFailure = Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'BRIDGE_LIVENESS_FAIL'
    $fallbackAuthorityMetadataMissing = Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'FALLBACK_AUTHORITY_METADATA_MISSING'
    $warningCapExcess = if ($RerunAttempt -gt 0) {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_WARNING_CAP_EXCESS'
    }
    else {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'WARNING_CAP_EXCESS'
    }
    $warning = if ($RerunAttempt -gt 0) {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_WARNING'
    }
    else {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'WARNING'
    }
    $progressTimeoutRecoveryStorm = if ($RerunAttempt -gt 0) {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_PROGRESS_TIMEOUT_RECOVERY_STORM'
    }
    else {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'PROGRESS_TIMEOUT_RECOVERY_STORM'
    }
    $startupRecoveryStorm = if ($RerunAttempt -gt 0) {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_STARTUP_RECOVERY_STORM'
    }
    else {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'STARTUP_RECOVERY_STORM'
    }
    $startupPeerDisconnect = if ($RerunAttempt -gt 0) {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_STARTUP_PEER_DISCONNECT'
    }
    else {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'STARTUP_PEER_DISCONNECT'
    }
    if ($startupPeerDisconnect) {
        $completed = $false
        $terminalState = 'Failed'
    }
    $retryDispatchSuccessorAttemptMismatch = if ($RerunAttempt -gt 0) {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_RETRY_DISPATCH_SUCCESSOR_ATTEMPT_MISMATCH'
    }
    else {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RETRY_DISPATCH_SUCCESSOR_ATTEMPT_MISMATCH'
    }
    if ($retryDispatchSuccessorAttemptMismatch) {
        $completed = $false
        $terminalState = 'Failed'
    }
    $contaminatedMeasurement = if ($RerunAttempt -gt 0) {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_CONTAMINATED_MEASUREMENT'
    }
    else {
        Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'CONTAMINATED_MEASUREMENT'
    }
    $transferId = ('fake-phase4-{0}' -f $scenarioName)
    $sessionId = 'phase4-fake-session'
    $lines = New-Object System.Collections.Generic.List[string]
    $seconds = 0
    $liveEpoch = 0

    for ($index = 0; $index -lt $routeChanges.Count; $index++) {
        $route = [string]$routeChanges[$index]
        $handoffKind = if ($index -eq 0) { 'none' } elseif ($route -eq 'post_tuna_fallback_v6') { 'tuna_to_normal_fallback' } elseif ($route -eq 'file_tuna_v4') { 'normal_to_tuna_activation' } else { 'none' }
        $routeEpoch = if ($index -gt 0) { $liveEpoch + 1 } else { 0 }
        foreach ($direction in @('outbound', 'inbound')) {
            $lines.Add((New-RouteAcceptanceRouteSelectedLogLine -TransferId $transferId -SessionId $sessionId -Direction $direction -Route $route -HandoffKind $handoffKind -LiveRouteEpoch $routeEpoch -SecondsOffset $seconds)) | Out-Null
        }

        if ($index -gt 0) {
            $liveEpoch++
            if (-not $missingLiveProof) {
                foreach ($line in @(New-RouteAcceptanceLiveRouteEpochLogLines -TransferId $transferId -SessionId $sessionId -Route $route -LiveRouteEpoch $liveEpoch -SecondsOffset ($seconds + 1) -TransportOnly:$transportOnlyLiveProof -MissingMetadata:$missingLiveMetadata)) {
                    $lines.Add($line) | Out-Null
                }
            }
        }

        $seconds += 2
    }

    if ($retryDispatchSuccessorAttemptMismatch) {
        $retryMismatchLine = New-RouteAcceptanceFakeLogLine -SecondsOffset 1 -Message ("event=runtime_unlock_retry_dispatch_authority_rejected; authority_schema=2; authority_id=fta2-runtime-unlock-current-artifact; predecessor_authority_id=fta2-runtime-unlock-predecessor; direction=outbound; session_id={0}; transfer_id={1}; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; transport_epoch=2; bridge_recovery_generation=3; bridge_decision_generation=5; route_publication_generation=6; route_commit_generation=0; contract_generation=7; retired_generation=11; dispatch_attempt_generation=5; missing_dispatch_successor_generation=2; missing_dispatch_successor_limit=1; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; release_kind=RegularV4RepairSettlement; release_authority_id=fta2-regular-v4-release-current-artifact; release_generation=3; release_outcome=ProgressObserved; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; offer_generation=12; offer_nonce=current-artifact; decision_reason=retry_dispatch_successor_attempt_mismatch" -f $sessionId, $transferId)
        $lines.Insert([Math]::Min(2, $lines.Count), $retryMismatchLine)
    }

    foreach ($line in @(New-RouteAcceptanceRuntimeLogLines -TransferId $transferId -SessionId $sessionId -Route $finalRoute -SecondsOffset $seconds)) {
        $lines.Add($line) | Out-Null
    }

    $fallbackAuthorityLegGeneration = 0
    $lastFallbackRouteIndex = -1
    for ($index = 1; $index -lt $routeChanges.Count -and $scenarioName -ne 'second-transfer-after-reactivation'; $index++) {
        if ([string]$routeChanges[$index] -eq 'post_tuna_fallback_v6') {
            $lastFallbackRouteIndex = $index
        }
    }

    for ($index = 1; $index -lt $routeChanges.Count -and $scenarioName -ne 'second-transfer-after-reactivation'; $index++) {
        if ([string]$routeChanges[$index] -ne 'post_tuna_fallback_v6') {
            continue
        }

        $fallbackAuthorityLegGeneration++
        $authorityRoute = 'post_tuna_fallback_v6'
        $authorityProtocol = if ($fallbackAuthorityMetadataMissing) { 0 } else { 6 }
        $authorityLiveEpoch = if ($fallbackAuthorityMetadataMissing) { 0 } else { $index }
        $authorityTransportEpoch = $fallbackAuthorityLegGeneration
        $authorityBridgeGeneration = 1
        $authorityCheckpointId = 'phase5-fallback-checkpoint:{0}' -f $fallbackAuthorityLegGeneration
        $authorityReason = if ($scenarioName -eq 'regular-v4-live-activation-off-on-off-256mb') { 'phase5_canonical_repeated_toggle' } else { 'phase5_fallback_authority' }
        $authorityOffset = $seconds + (6 * ($fallbackAuthorityLegGeneration - 1)) + 1
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset $authorityOffset -Message ("event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={0}; session_id={1}; leg_generation={2}; route={3}; protocol_version={4}; live_route_epoch={5}; transport_epoch={6}; bridge_recovery_generation={7}; checkpoint_request_id={8}; authority_reason={9}" -f $transferId, $sessionId, $fallbackAuthorityLegGeneration, $authorityRoute, $authorityProtocol, $authorityLiveEpoch, $authorityTransportEpoch, $authorityBridgeGeneration, $authorityCheckpointId, $authorityReason))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($authorityOffset + 1) -Message ("event=filetransfer_fallback_leg_authority_bridge_recovery_requested; direction=outbound; transfer_id={0}; session_id={1}; leg_generation={2}; route={3}; protocol_version={4}; live_route_epoch={5}; transport_epoch={6}; bridge_recovery_generation={7}; checkpoint_request_id={8}; authority_reason={9}" -f $transferId, $sessionId, $fallbackAuthorityLegGeneration, $authorityRoute, $authorityProtocol, $authorityLiveEpoch, $authorityTransportEpoch, $authorityBridgeGeneration, $authorityCheckpointId, $authorityReason))) | Out-Null
        if ($bridgeLivenessFailure -and $index -eq $lastFallbackRouteIndex) {
            $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($authorityOffset + 2) -Message ("event=session_liveness_timeout; session_id={0}; transfer_id={1}; route={2}; protocol_version=6; leg_generation={3}; bridge_recovery_generation={4}; reason=phase5_fake_valid_recovery_timeout" -f $sessionId, $transferId, $authorityRoute, $fallbackAuthorityLegGeneration, $authorityBridgeGeneration))) | Out-Null
        }
        else {
            $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($authorityOffset + 2) -Message ("event=bridge_receive_stall_recovery_receive_resumed; session_id={0}; transfer_id={1}; route={2}; protocol_version=6; leg_generation={3}; bridge_recovery_generation={4}; reason=phase5_fake_receive_resumed" -f $sessionId, $transferId, $authorityRoute, $fallbackAuthorityLegGeneration, $authorityBridgeGeneration))) | Out-Null
            $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($authorityOffset + 2) -Message ("event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_request; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; live_route_epoch={4}; leg_generation={5}; bridge_recovery_generation={6}; transport_epoch={7}; checkpoint_request_id={8}; reason=phase5_fake_checkpoint_request; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0" -f $transferId, $sessionId, $authorityRoute, $authorityProtocol, $authorityLiveEpoch, $fallbackAuthorityLegGeneration, $authorityBridgeGeneration, $authorityTransportEpoch, $authorityCheckpointId))) | Out-Null
            $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($authorityOffset + 3) -Message ("event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_proof; transfer_id={0}; session_id={1}; route={2}; protocol_version={3}; live_route_epoch={4}; leg_generation={5}; bridge_recovery_generation={6}; transport_epoch={7}; checkpoint_request_id={8}; reason=phase5_fake_checkpoint_proof; frame_type=filetransfer.receiver_state.v6; control_queue=1; control_copy=0; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0" -f $transferId, $sessionId, $authorityRoute, $authorityProtocol, $authorityLiveEpoch, $fallbackAuthorityLegGeneration, $authorityBridgeGeneration, $authorityTransportEpoch, $authorityCheckpointId))) | Out-Null
            $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($authorityOffset + 3) -Message ("event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={0}; session_id={1}; leg_generation={2}; route={3}; protocol_version={4}; live_route_epoch={5}; transport_epoch={6}; bridge_recovery_generation={7}; checkpoint_request_id={8}; proven_committed_chunk=128; proven_highest_observed_chunk=160; reason=phase5_fake_receiver_state" -f $transferId, $sessionId, $fallbackAuthorityLegGeneration, $authorityRoute, $authorityProtocol, $authorityLiveEpoch, $authorityTransportEpoch, $authorityBridgeGeneration, $authorityCheckpointId))) | Out-Null
            $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($authorityOffset + 4) -Message ("event=filetransfer_fallback_leg_authority_completed; direction=outbound; transfer_id={0}; session_id={1}; leg_generation={2}; route={3}; protocol_version={4}; live_route_epoch={5}; transport_epoch={6}; bridge_recovery_generation={7}; checkpoint_request_id={8}; authority_reason={9}; proof=phase5_fake_receiver_state" -f $transferId, $sessionId, $fallbackAuthorityLegGeneration, $authorityRoute, $authorityProtocol, $authorityLiveEpoch, $authorityTransportEpoch, $authorityBridgeGeneration, $authorityCheckpointId, $authorityReason))) | Out-Null
        }
    }

    $metadata = Get-RouteAcceptanceRouteMetadata -Route $finalRoute
    $frameType = 'filetransfer.chunk_batch.{0}' -f $metadata.FrameFamily
    $terminalError = if ($terminalState -eq 'Completed') { '(none)' } elseif ($receiveRecoveryExhaustedBeforeRuntimeUnlock -or $receiveRecoveryLivenessTimeoutBeforeRuntimeUnlock -or $startupPeerDisconnect) { 'peer_disconnected' } else { 'phase4_fake_terminal_error' }
    $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 1) -Message ("event=filetransfer_binary_frame_sent; transfer_id={0}; session_id={1}; frame_type={2}; chunk_index=0-31; payload_bytes={3}; serialized_payload_bytes={3}; raw_chunk_bytes={3}; chunk_count=32" -f $transferId, $sessionId, $frameType, $payloadBytes))) | Out-Null
    $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 1) -Message ("event=filetransfer_binary_frame_received; transfer_id={0}; session_id={1}; frame_type={2}; chunk_index=0-31; raw_chunk_bytes={3}; chunk_count=32" -f $transferId, $sessionId, $frameType, $payloadBytes))) | Out-Null
    if ($retryDispatchSuccessorAttemptMismatch) {
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 2) -Message ("event=filetransfer_fallback_tail_local_yield_blocked; direction=outbound; transfer_id={0}; session_id={1}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=7; reason=paired_yield_missing" -f $transferId, $sessionId))) | Out-Null
    }
    if ($hardFailure) {
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 2) -Message ("event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={0}; session_id={1}; frame_type=filetransfer.chunk_batch.v4; first_lane=control; second_lane=bulk; first_error=IOException; second_error=IOException" -f $transferId, $sessionId))) | Out-Null
    }
    if ($warning) {
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 3) -Message 'event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1')) | Out-Null
    }
    if ($warningCapExcess) {
        for ($warningIndex = 0; $warningIndex -lt 4; $warningIndex++) {
            $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 3 + $warningIndex) -Message 'event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1')) | Out-Null
        }
    }

    if ($contaminatedMeasurement -and $finalRoute -eq 'file_tuna_v4') {
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 2) -Message 'event=nkn_bridge_receive_stall_detected; reason=all_channels_zero_receive; consecutive_zero_receive_windows=3; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; control_last_received_age_ms=12000; bulk_last_received_age_ms=12000')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 2) -Message 'event=nkn_bridge_receive_stall_recovery_started; stall_reason=all_channels_zero_receive; attempt=1; max_restarts=4; control_last_received_age_ms=12000; bulk_last_received_age_ms=12000')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 3) -Message 'event=nkn_bridge_receive_stall_recovery_completed; stall_reason=all_channels_zero_receive; attempt=1; recovery_count=1; fallback_delay_ms=3000; requires_control_proof=1; requires_bulk_proof=1')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 4) -Message ("event=filetransfer_v4_state_received; transfer_id={0}; session_id={1}; epoch=2; previous_epoch=1; applied=1; stale=0; duplicate=0; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3121; effective_credit_until_chunk_index_exclusive=3121; missing_range_count=1; bytes_committed=0; terminal_ready=0" -f $transferId, $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 4) -Message ("event=filetransfer_v4_repair_scheduled; transfer_id={0}; session_id={1}; repair_request_key=0:12:0:12; epoch=2; range_count=1; requested_chunk_count=12; scheduled_chunk_count=12; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=first_send_credit_stall; frontier_tail_repair=1; credit_exhausted_time_ms_at_repair=1200; remote_next_expected_chunk_index=0; chunks_accepted_for_transport=3121" -f $transferId, $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 5) -Message ("event=filetransfer_v4_repair_sent; transfer_id={0}; session_id={1}; repair_request_key=0:12:0:12; range_count=1; requested_chunk_count=12; sent_chunk_count=12; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=first_send_credit_stall; frontier_tail_repair=1; remote_next_expected_chunk_index=0; chunks_accepted_for_transport=3121" -f $transferId, $sessionId))) | Out-Null
    }

    if ($receiveRecoveryExhaustedBeforeRuntimeUnlock) {
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 2) -Message ("event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={0}; payer_decision_id=1; generation=1; retry_scheduled=0; retry_after_recovery_armed=1; answer_timeout_scheduled=0; recovery_requested=1; recovery_reason=regular_v4_unproven_recovery_escalation; observed_send=0; observed_lane=(none)" -f $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 3) -Message ("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id={0}; retired_generation=1; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=regular_v4_unproven_recovery_escalation; queued_behind_active_negotiation=0" -f $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 4) -Message ("event=session_recovery_contract_retry_dispatched; session_id={0}; transfer_id={1}; contract_generation=1; offer_generation=2; kind=runtime_unlock_activation; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=regular_v4_unproven_recovery_escalation" -f $sessionId, $transferId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 5) -Message ("event=session_recovery_contract_retry_authority_granted; session_id={0}; transfer_id={1}; contract_generation=1; offer_generation=2; authority_attempt=1; reason=bridge_recovery_settled; observed_send_deadline_utc_ms=1780587010000" -f $sessionId, $transferId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 6) -Message ("event=session_recovery_contract_retry_authority_observed; session_id={0}; transfer_id={1}; contract_generation=1; offer_generation=2; authorized_observed_lane=bulk_queue_fallback; authority_attempt=1" -f $sessionId, $transferId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 7) -Message ("event=tuna_activation_control_send_waiting_for_regular_v4_recovery; session_id={0}; transfer_id={1}; reason=runtime_unlock_observed_offer_replay; route=regular_nkn_v4_fast; protocol_version=4; receive_recovery_in_progress=1" -f $sessionId, $transferId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 8) -Message ("event=nkn_bridge_receive_stall_recovery_started; stall_reason=regular_v4_unproven_recovery_escalation; attempt=16; max_restarts=4; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; transfer_id={0}; session_id={1}" -f $transferId, $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 9) -Message ("event=nkn_bridge_receive_stall_recovery_completed; stall_reason=regular_v4_unproven_recovery_escalation; attempt=16; recovery_count=16; requires_control_proof=1; requires_bulk_proof=1; transfer_id={0}; session_id={1}" -f $transferId, $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 10) -Message ("event=nkn_bridge_receive_stall_recovery_failed; reason=max_restarts_reached; stall_reason=regular_v4_unproven_recovery_escalation; recovery_count=16; max_restarts=16; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; transfer_id={0}; session_id={1}" -f $transferId, $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 11) -Message ("event=session_liveness_timeout; session_id={0}; transfer_id={1}; reason=session_liveness_timeout; route=regular_nkn_v4_fast; protocol_version=4" -f $sessionId, $transferId))) | Out-Null
    }
    elseif ($receiveRecoveryLivenessTimeoutBeforeRuntimeUnlock) {
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 2) -Message ("event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={0}; payer_decision_id=1; generation=1; retry_scheduled=0; retry_after_recovery_armed=1; answer_timeout_scheduled=0; recovery_requested=1; recovery_reason=regular_v4_unproven_recovery_escalation; observed_send=0; observed_lane=(none)" -f $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 3) -Message ("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id={0}; retired_generation=1; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=regular_v4_unproven_recovery_escalation; queued_behind_active_negotiation=0" -f $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 4) -Message ("event=session_recovery_contract_retry_dispatched; session_id={0}; transfer_id={1}; contract_generation=1; offer_generation=2; kind=runtime_unlock_activation; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=regular_v4_unproven_recovery_escalation" -f $sessionId, $transferId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 5) -Message ("event=session_recovery_contract_retry_authority_granted; session_id={0}; transfer_id={1}; contract_generation=1; offer_generation=2; authority_attempt=1; reason=bridge_recovery_settled; observed_send_deadline_utc_ms=1780587010000" -f $sessionId, $transferId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 6) -Message ("event=session_recovery_contract_retry_authority_observed; session_id={0}; transfer_id={1}; contract_generation=1; offer_generation=2; authorized_observed_lane=control_to_bulk_endpoint; authority_attempt=1" -f $sessionId, $transferId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 7) -Message ("event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_deferred; session_id={0}; reason=runtime_unlock_offer_observed_waiting_for_answer; trigger=runtime_unlock" -f $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 8) -Message ("event=nkn_bridge_receive_stall_recovery_started; stall_reason=regular_v4_unproven_recovery_escalation; attempt=4; max_restarts=4; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; transfer_id={0}; session_id={1}" -f $transferId, $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 9) -Message ("event=nkn_bridge_receive_stall_recovery_completed; stall_reason=regular_v4_unproven_recovery_escalation; attempt=4; recovery_count=4; requires_control_proof=1; requires_bulk_proof=1; transfer_id={0}; session_id={1}" -f $transferId, $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 10) -Message ("event=session_liveness_timeout; session_id={0}; transfer_id={1}; reason=session_liveness_timeout; route=regular_nkn_v4_fast; protocol_version=4" -f $sessionId, $transferId))) | Out-Null
    }

    if ($startupPeerDisconnect -and $Scenario.Kind -eq 'regular') {
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 2) -Message ("event=filetransfer_regular_v4_recovery_liveness_started; session_id={0}; transfer_id={1}; generation=1; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; authority_reason=regular_v4_startup_local_only_no_ack; reason=session_liveness_timeout_pending" -f $sessionId, $transferId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 3) -Message ("event=session_liveness_timeout_deferred_for_current_filetransfer_recovery; session_id={0}; transfer_id={1}; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; leg_generation=1; bridge_recovery_generation=1; transport_epoch=0; checkpoint_request_id=(none); authority_reason=regular_v4_startup_local_only_no_ack; state=bridgerecoverycompletedawaitingproof; bridge_recovery_requested=1; bridge_recovery_started=1; bridge_recovery_completed=1; bridge_lifecycle_grace=0; silence_ms=32213; liveness_deferral_deadline_utc_ms=1780676299839" -f $sessionId, $transferId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 4) -Message 'event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=2')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 5) -Message ("event=filetransfer_v4_sender_failed; transfer_id={0}; session_id={1}; error_code=peer_disconnected; reason=Peer disconnected." -f $transferId, $sessionId))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 6) -Message ("event=session_liveness_timeout; session_id={0}; transfer_id={1}; reason=session_liveness_timeout; route=regular_nkn_v4_fast; protocol_version=4" -f $sessionId, $transferId))) | Out-Null
    }

    if (($progressTimeoutRecoveryStorm -or $startupRecoveryStorm) -and $Scenario.Kind -eq 'regular') {
        $completed = $false
        $terminalState = 'Pending'
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 6) -Message 'event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_protocol_repair_only; stall_reason=bulk_receive_stalled; attempt=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 7) -Message 'event=nkn_bridge_receive_stall_recovery_protocol_repair_exhausted; trigger=filetransfer_protocol_repair_only; requested_reason=regular_v4_peer_silence; recovery_count=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 8) -Message 'event=nkn_bridge_receive_stall_recovery_regular_v4_unproven_escalation_allowed; requested_reason=regular_v4_peer_silence; stall_reason=regular_v4_unproven_recovery_escalation; recovery_count=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1')) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset ($seconds + 9) -Message 'event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=2')) | Out-Null
    }
    else {
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 120 -Message ("event=file_transfer_inbound_terminal; role=helper; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; saved_path=(none); integrity_ok={5}; route={6}; protocol_version={7}" -f $sessionId, $transferId, $terminalState, $terminalError, $payloadBytes, ($(if ($completed -and $terminalState -eq 'Completed') { 1 } else { 0 })), $finalRoute, $metadata.Protocol))) | Out-Null
        $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 120 -Message ("event=file_transfer_outbound_terminal; role=helpee; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; integrity_ok={5}; route={6}; protocol_version={7}" -f $sessionId, $transferId, $terminalState, $terminalError, $payloadBytes, ($(if ($completed -and $terminalState -eq 'Completed') { 1 } else { 0 })), $finalRoute, $metadata.Protocol))) | Out-Null
    }
    $lines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 110 -Message ("event=nkn_bridge_bulk_send_summary; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; frames_sent=1; send_failures=0; queue_clears=0; payload_bytes_sent={0}; payload_bytes_per_second=6000000; send_p95_ms=1; configured_concurrency=4; effective_concurrency=4" -f $payloadBytes))) | Out-Null
    $lines.ToArray() | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8

    $liveMode = if ($Scenario.LiveProofMode) { [string]$Scenario.LiveProofMode } else { 'None' }
    Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $ArtifactDir -LiveRouteProofMode $liveMode

    if ($terminalBeforeFallbackSwitchOff -and $Scenario.Name -eq 'regular-v4-live-activation-off-on-off-256mb') {
        $routeSummaryPath = Join-Path $ArtifactDir 'filetransfer-route-consistency-summary.txt'
        if (Test-Path -LiteralPath $routeSummaryPath -PathType Leaf) {
            $routeSummaryText = Get-Content -LiteralPath $routeSummaryPath -Raw
            $routeSummaryText = $routeSummaryText `
                -replace '(?m)^route_consistency_verdict=.*$', 'route_consistency_verdict=pass' `
                -replace '(?m)^route_mismatch_count=.*$', 'route_mismatch_count=0' `
                -replace '(?m)^live_route_epoch_proof_verdict=.*$', 'live_route_epoch_proof_verdict=pass' `
                -replace '(?m)^control_plane_isolation_verdict=.*$', 'control_plane_isolation_verdict=pass' `
                -replace '(?m)^fallback_control_plane_non_current_leg_generation_send_count=.*$', 'fallback_control_plane_non_current_leg_generation_send_count=0'
            Set-Content -LiteralPath $routeSummaryPath -Value $routeSummaryText -Encoding UTF8
        }

        @(
            'verdict=PASS',
            'hard_failure_count=0',
            'warning_count=0',
            'warning_kinds=(none)',
            'warning_cap_exceeded_kinds=(none)'
        ) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-operator-verdict.txt') -Encoding UTF8
    }

    if ($Scenario.Name -eq 'second-transfer-after-reactivation' -and
        (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'COMBINED_ROUTE_ANALYSIS_CONTAMINATED')) {
        @(
            'route_consistency_verdict=fail',
            'route_consistency_failure_count=2',
            'route_selected_count=3',
            'selected.1.direction=outbound',
            'selected.1.route=file_tuna_v4',
            'selected.1.protocol_version=4',
            'selected.1.runtime_profile=file_tuna_v4_fast',
            'selected.1.bridge_recovery_policy=tuna_strict',
            'selected.2.direction=outbound',
            'selected.2.route=post_tuna_fallback_v6',
            'selected.2.protocol_version=6',
            'selected.2.runtime_profile=default_v6',
            'selected.2.bridge_recovery_policy=post_tuna_fallback_strict',
            'selected.3.direction=outbound',
            'selected.3.route=file_tuna_v4',
            'selected.3.protocol_version=4',
            'selected.3.runtime_profile=file_tuna_v4_fast',
            'selected.3.bridge_recovery_policy=tuna_strict',
            'selected_routes=file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4',
            'selected_route_sequence=file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4',
            'selected_route_changes=file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4',
            'live_route_epoch_route_changes=file_tuna_v4,post_tuna_fallback_v6',
            'live_route_epoch_proof_verdict=fail',
            'fallback_leg_authority_proof_verdict=fail',
            'bridge_liveness_integration_verdict=none'
        ) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-route-consistency-summary.txt') -Encoding UTF8
        @(
            'verdict=FAIL',
            'hard_failure_count=3',
            'warning_count=0',
            'warning_kinds=(none)',
            'warning_cap_exceeded_kinds=(none)'
        ) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-operator-verdict.txt') -Encoding UTF8
    }

    if ($Scenario.Name -eq 'second-transfer-after-reactivation' -and
        (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'SECOND_TRANSFER_NOT_STARTED')) {
        @(
            'event=filetransfer_second_transfer_status',
            'second_transfer_started=0',
            'first_transfer_terminal_state=(missing)',
            'first_transfer_route_sequence=(see filetransfer-route-consistency-summary.txt)',
            'reactivation_epoch_seen=0',
            'failure_class=first_transfer_reactivation_or_fallback_incomplete',
            'failure_reason=normal_to_tuna_live_epoch_missing'
        ) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-second-transfer-status.txt') -Encoding UTF8
    }
    elseif ($Scenario.Name -eq 'second-transfer-after-reactivation') {
        $secondSlicePath = Join-Path $ArtifactDir 'filetransfer-second-transfer-retained-log-slice.log'
        $secondAnalysisDir = Join-Path $ArtifactDir 'second-transfer-analysis'
        New-Item -ItemType Directory -Force -Path $secondAnalysisDir | Out-Null
        $secondTransferId = ('{0}-second' -f $transferId)
        $secondPayloadBytes = 16777216L
        $secondMetadata = Get-RouteAcceptanceRouteMetadata -Route 'file_tuna_v4'
        $secondFrameType = 'filetransfer.chunk_batch.{0}' -f $secondMetadata.FrameFamily
        $secondLines = New-Object System.Collections.Generic.List[string]
        foreach ($direction in @('outbound', 'inbound')) {
            $secondLines.Add((New-RouteAcceptanceRouteSelectedLogLine -TransferId $secondTransferId -SessionId $sessionId -Direction $direction -Route 'file_tuna_v4' -SecondsOffset 0)) | Out-Null
        }
        foreach ($line in @(New-RouteAcceptanceRuntimeLogLines -TransferId $secondTransferId -SessionId $sessionId -Route 'file_tuna_v4' -SecondsOffset 1)) {
            $secondLines.Add($line) | Out-Null
        }
        $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 2 -Message ("event=filetransfer_binary_frame_sent; transfer_id={0}; session_id={1}; frame_type={2}; chunk_index=0-31; payload_bytes={3}; serialized_payload_bytes={3}; raw_chunk_bytes={3}; chunk_count=32" -f $secondTransferId, $sessionId, $secondFrameType, $secondPayloadBytes))) | Out-Null
        $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 2 -Message ("event=filetransfer_binary_frame_received; transfer_id={0}; session_id={1}; frame_type={2}; chunk_index=0-31; raw_chunk_bytes={3}; chunk_count=32" -f $secondTransferId, $sessionId, $secondFrameType, $secondPayloadBytes))) | Out-Null
        if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'SECOND_CONTAMINATED_MEASUREMENT') {
            $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 3 -Message 'event=nkn_bridge_receive_stall_detected; reason=all_channels_zero_receive; consecutive_zero_receive_windows=3; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; control_last_received_age_ms=12000; bulk_last_received_age_ms=12000')) | Out-Null
            $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 3 -Message 'event=nkn_bridge_receive_stall_recovery_started; stall_reason=all_channels_zero_receive; attempt=1; max_restarts=4; control_last_received_age_ms=12000; bulk_last_received_age_ms=12000')) | Out-Null
            $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 4 -Message 'event=nkn_bridge_receive_stall_recovery_completed; stall_reason=all_channels_zero_receive; attempt=1; recovery_count=1; fallback_delay_ms=3000; requires_control_proof=1; requires_bulk_proof=1')) | Out-Null
            $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 5 -Message ("event=filetransfer_v4_state_received; transfer_id={0}; session_id={1}; epoch=2; previous_epoch=1; applied=1; stale=0; duplicate=0; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=781; effective_credit_until_chunk_index_exclusive=781; missing_range_count=1; bytes_committed=0; terminal_ready=0" -f $secondTransferId, $sessionId))) | Out-Null
            $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 5 -Message ("event=filetransfer_v4_repair_scheduled; transfer_id={0}; session_id={1}; repair_request_key=0:12:0:12; epoch=2; range_count=1; requested_chunk_count=12; scheduled_chunk_count=12; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=first_send_credit_stall; frontier_tail_repair=1; credit_exhausted_time_ms_at_repair=1200; remote_next_expected_chunk_index=0; chunks_accepted_for_transport=781" -f $secondTransferId, $sessionId))) | Out-Null
            $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 6 -Message ("event=filetransfer_v4_repair_sent; transfer_id={0}; session_id={1}; repair_request_key=0:12:0:12; range_count=1; requested_chunk_count=12; sent_chunk_count=12; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=first_send_credit_stall; frontier_tail_repair=1; remote_next_expected_chunk_index=0; chunks_accepted_for_transport=781" -f $secondTransferId, $sessionId))) | Out-Null
        }
        $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 60 -Message ("event=file_transfer_inbound_terminal; role=helper; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; saved_path=(none); integrity_ok={5}; route=file_tuna_v4; protocol_version=4" -f $sessionId, $secondTransferId, $terminalState, $terminalError, $secondPayloadBytes, ($(if ($completed -and $terminalState -eq 'Completed') { 1 } else { 0 }))))) | Out-Null
        $secondLines.Add((New-RouteAcceptanceFakeLogLine -SecondsOffset 60 -Message ("event=file_transfer_outbound_terminal; role=helpee; session_id={0}; transfer_id={1}; state={2}; error_code={3}; bytes_transferred={4}; integrity_ok={5}; route=file_tuna_v4; protocol_version=4" -f $sessionId, $secondTransferId, $terminalState, $terminalError, $secondPayloadBytes, ($(if ($completed -and $terminalState -eq 'Completed') { 1 } else { 0 }))))) | Out-Null
        $secondLines.ToArray() | Set-Content -LiteralPath $secondSlicePath -Encoding UTF8
        Copy-Item -LiteralPath $secondSlicePath -Destination (Join-Path $secondAnalysisDir 'filetransfer-retained-log-slice.log') -Force
        Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $secondAnalysisDir
    }

    if ($Scenario.Kind -eq 'regular') {
        $cycle = [ordered]@{
            cycle_index = 0
            mode = 'nkn-fast'
            scenario = 'FILETRANSFER_NKN_SOAK'
            direction = 'helpee-to-helper'
            transfer_id = $transferId
            payload_bytes = $payloadBytes
            duration_ms = [Math]::Max(1L, [long][Math]::Round(($payloadBytes / [Math]::Max(1D, $goodput)) * 1000D))
            goodput_bytes_per_second = $goodput
            completed = $completed
            integrity_ok = $completed
            inbound_state = $terminalState
            outbound_state = $terminalState
        }
        $cycle | ConvertTo-Json -Compress -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl') -Encoding UTF8
        $regularSummaryVerdict = if ($progressTimeoutRecoveryStorm) {
            'INCONCLUSIVE_PROGRESS_TIMEOUT'
        }
        elseif ($completed -and $terminalState -eq 'Completed') {
            'PASS'
        }
        else {
            'FAIL_PROTOCOL_OR_INTEGRITY'
        }
        $summary = [ordered]@{
            artifact_kind = 'live-nkn'
            mode = 'nkn-fast'
            verdict = $regularSummaryVerdict
            gate_status = if ($regularSummaryVerdict -eq 'PASS') { 'pass' } elseif ($regularSummaryVerdict -eq 'INCONCLUSIVE_PROGRESS_TIMEOUT') { 'inconclusive' } else { 'fail' }
            cycles_requested = 1
            cycles_observed = if ($startupRecoveryStorm -or $startupPeerDisconnect) { 0 } else { 1 }
            cycles_completed = if ($completed -and $terminalState -eq 'Completed') { 1 } else { 0 }
            total_payload_bytes = if ($completed -and $terminalState -eq 'Completed') { $payloadBytes } else { 0 }
            average_goodput_bytes_per_second = ('{0:F3}' -f $goodput)
            min_goodput_bytes_per_second = ('{0:F3}' -f $goodput)
            data_protocol_version = [int]$metadata.Protocol
            v4_feedback_both_failed_count = 0
            v4_sender_failed_count = if ($startupPeerDisconnect) { 1 } else { 0 }
            bridge_bulk_send_failure_count = 0
            bridge_bulk_queue_clear_count = if ($startupPeerDisconnect) { 6 } elseif ($progressTimeoutRecoveryStorm -or $startupRecoveryStorm) { 2 } else { 0 }
            gui_progress_timeout_count = if ($progressTimeoutRecoveryStorm) { 1 } else { 0 }
            gui_progress_timeout_reason = if ($progressTimeoutRecoveryStorm) { 'no useful data progress for 180s' } else { '(none)' }
            last_receiver_next_chunk = if ($progressTimeoutRecoveryStorm) { 1614 } else { -1 }
            last_receiver_highest_chunk = if ($progressTimeoutRecoveryStorm) { 1613 } else { -1 }
            last_progress_event_count = if ($progressTimeoutRecoveryStorm) { 23 } else { 0 }
            terminal_missing_after_progress_timeout = if ($progressTimeoutRecoveryStorm) { 1 } else { 0 }
        }
        $summary.GetEnumerator() | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value } | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt') -Encoding UTF8
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.json') -Encoding UTF8
    }
    else {
        $secondTransferNotStarted = $Scenario.Name -eq 'second-transfer-after-reactivation' -and
            (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'SECOND_TRANSFER_NOT_STARTED')
        $summary = [ordered]@{
            event = 'filetransfer_tuna_gui_handoff_fallback_summary'
            routeMode = [string]$Scenario.RouteMode
            direction = 'helpee-to-helper'
            payerMode = [string]$Scenario.PayerMode
            faultMode = [string]$Scenario.Fault
            transferId = $transferId
            payloadBytes = $payloadBytes
            durationMs = [Math]::Round(($payloadBytes / [Math]::Max(1D, $goodput)) * 1000D, 3)
            goodputBytesPerSecond = $goodput
            completed = $completed -and $terminalState -eq 'Completed'
            integrityOk = $completed -and $terminalState -eq 'Completed'
            inboundState = $terminalState
            outboundState = $terminalState
            inboundErrorCode = $terminalError
            outboundErrorCode = $terminalError
            expectedSha256 = 'fake'
            receivedSha256 = if ($completed) { 'fake' } else { 'mismatch' }
            savedFileSizeBytes = if ($completed) { $payloadBytes } else { 0 }
            measuredPhase = [ordered]@{
                name = [string]$Scenario.Name
                route = $finalRoute
                protocolVersion = [int]$metadata.Protocol
                payloadBytes = $payloadBytes
                goodputBytesPerSecond = $goodput
                completed = $completed -and $terminalState -eq 'Completed'
                integrityOk = $completed -and $terminalState -eq 'Completed'
                inboundState = $terminalState
                outboundState = $terminalState
                inboundErrorCode = $terminalError
                outboundErrorCode = $terminalError
            }
            liveRouteEpochRouteChanges = @($routeChanges | Select-Object -Skip 1)
            firstTransferTerminalBeforeLiveReactivation = $terminalBeforeLiveReactivation
            firstTransferTerminalBeforeFallbackSwitchOff = $terminalBeforeFallbackSwitchOff
            secondTransfer = if ($Scenario.Name -eq 'second-transfer-after-reactivation') {
                $secondRoute = Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix 'SECOND_ROUTE' -DefaultValue 'file_tuna_v4'
                $secondMetadata = Get-RouteAcceptanceRouteMetadata -Route $secondRoute
                if ($secondTransferNotStarted) {
                    [ordered]@{
                        secondTransferStarted = $false
                        route = '(unknown)'
                        protocolVersion = 0
                        completed = $false
                        integrityOk = $false
                        inboundState = '(not_started)'
                        outboundState = '(not_started)'
                        inboundErrorCode = '(not_started)'
                        outboundErrorCode = '(not_started)'
                        payloadBytes = 16777216
                        goodputBytesPerSecond = 0
                        setupFailurePhase = 'first_transfer'
                        setupFailureReason = 'first_transfer_reactivation_or_fallback_incomplete'
                    }
                }
                else {
                    [ordered]@{
                        secondTransferStarted = $true
                        route = $secondRoute
                        protocolVersion = [int]$secondMetadata.Protocol
                        completed = $completed -and $terminalState -eq 'Completed'
                        integrityOk = $completed -and $terminalState -eq 'Completed'
                        inboundState = $terminalState
                        outboundState = $terminalState
                        inboundErrorCode = $terminalError
                        outboundErrorCode = $terminalError
                        payloadBytes = 16777216
                        goodputBytesPerSecond = $goodput
                    }
                }
            }
            else {
                $null
            }
        }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
    }

    if ($transientSetupFailure) {
        $defaultSetupPhase = if ($scenarioName -eq 'regular-v4-live-activation-off-on-off-256mb') { 'activation_offer_send' } else { 'measured_terminal' }
        $defaultSetupReason = if ($scenarioName -eq 'regular-v4-live-activation-off-on-off-256mb') { 'activation_offer_not_observed' } else { 'terminal_before_accept' }
        $phaseSuffix = if ($RerunAttempt -gt 0) { 'RERUN_TRANSIENT_SETUP_PHASE' } else { 'TRANSIENT_SETUP_PHASE' }
        $reasonSuffix = if ($RerunAttempt -gt 0) { 'RERUN_TRANSIENT_SETUP_REASON' } else { 'TRANSIENT_SETUP_REASON' }
        $setupPhase = Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix $phaseSuffix -DefaultValue $defaultSetupPhase
        $setupReason = Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix $reasonSuffix -DefaultValue $defaultSetupReason
        [ordered]@{
            event = 'filetransfer_tuna_gui_error'
            routeMode = [string]$Scenario.RouteMode
            direction = 'helpee-to-helper'
            payerMode = [string]$Scenario.PayerMode
            faultMode = [string]$Scenario.Fault
            payloadBytes = [long]$Scenario.PayloadBytes
            completed = $false
            integrityOk = $false
            failurePhase = $setupPhase
            failureReason = $setupReason
            error = 'Injected fake transient setup failure.'
            failedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json') -Encoding UTF8
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'FALLBACK_TAIL_BRIDGE_RECOVERY_GENERATION_PAIRING_SUMMARY_FIXTURE') {
        $pairingFixtureLines = @(
            'fallback_tail_bridge_recovery_generation_pairing_verdict=fail',
            'fallback_tail_bridge_recovery_generation_mismatch_raw_count=4',
            'fallback_tail_bridge_recovery_generation_mismatch_distinct_count=1',
            'fallback_tail_bridge_recovery_generation_mismatch_recovered_count=0',
            'fallback_tail_bridge_recovery_generation_mismatch_unresolved_count=1'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $pairingFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'FALLBACK_TAIL_TRANSPORT_EPOCH_REAUTHORIZATION_SUMMARY_FIXTURE') {
        $transportEpochReauthorizationFixtureLines = @(
            'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict=fail',
            'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count=1',
            'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count=1',
            'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count=0',
            'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count=1',
            'first_proof_chain_failure_class=post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing',
            'first_proof_chain_failure_event=filetransfer_fallback_tail_local_yield_blocked',
            'first_proof_chain_failure_reason=local_yield_transport_epoch_reauthorization_missing',
            'first_proof_chain_failure_sequence=27225',
            'first_proof_chain_failure_source_path=filetransfer-retained-log-slice.log',
            'first_proof_chain_failure_line_number=27225',
            'first_proof_chain_failure_writer_sequence=13864',
            'semantic_failure_domain=route_runtime'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $transportEpochReauthorizationFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'REGULAR_V4_FRONTIER_REPAIR_PROJECTION_SUMMARY_FIXTURE') {
        $projectionFixtureLines = @(
            'regular_v4_frontier_repair_missing_range_not_preserved_raw_count=3',
            'regular_v4_frontier_repair_missing_range_not_preserved_distinct_count=2',
            'regular_v4_frontier_repair_missing_range_not_preserved_recovered_count=1',
            'regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count=1',
            'regular_v4_frontier_repair_missing_range_preservation_verdict=fail'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $projectionFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'REGULAR_V4_PROJECTION_REAUTHORIZATION_SUMMARY_FIXTURE') {
        $projectionReauthorizationFixtureLines = @(
            'regular_v4_frontier_repair_projection_reauthorization_missing_raw_count=1438',
            'regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count=1',
            'regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count=0',
            'regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count=1',
            'regular_v4_frontier_repair_projection_reauthorization_verdict=fail',
            'first_proof_chain_failure_class=regular_v4_frontier_repair_projection_reauthorization_missing_after_recovery',
            'first_proof_chain_failure_event=filetransfer_regular_v4_frontier_repair_projection_rejected',
            'first_proof_chain_failure_reason=fresh_projection_required_before_unrestricted_state',
            'first_proof_chain_failure_sequence=4675',
            'first_proof_chain_failure_source_path=filetransfer-retained-log-slice.log',
            'first_proof_chain_failure_line_number=4675'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $projectionReauthorizationFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'PEER_SILENCE_REPLAY_RANGE_PRESERVATION_SUMMARY_FIXTURE') {
        $peerSilenceReplayFixtureLines = @(
            'regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count=1',
            'regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count=1',
            'regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count=0',
            'regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count=1',
            'regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict=fail',
            'first_proof_chain_failure_class=regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved',
            'first_proof_chain_failure_event=filetransfer_regular_v4_frontier_repair_authority_granted',
            'first_proof_chain_failure_reason=peer_silence_replay_range_rotated_after_missing_dispatch',
            'first_proof_chain_failure_sequence=2898',
            'first_proof_chain_failure_source_path=filetransfer-retained-log-slice.log',
            'first_proof_chain_failure_line_number=2898'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $peerSilenceReplayFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'REGULAR_V4_SUCCESSOR_PRESSURE_INSTALL_SUMMARY_FIXTURE') {
        $successorPressureInstallFixtureLines = @(
            'regular_v4_frontier_repair_successor_pressure_install_missing_raw_count=1',
            'regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count=1',
            'regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count=0',
            'regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count=1',
            'regular_v4_frontier_repair_successor_pressure_install_verdict=fail',
            'first_proof_chain_failure_class=regular_v4_frontier_repair_successor_pressure_install_missing',
            'first_proof_chain_failure_event=filetransfer_regular_v4_frontier_repair_settlement_rejected',
            'first_proof_chain_failure_reason=regular_v4_frontier_repair_pressure_missing',
            'first_proof_chain_failure_sequence=2792',
            'first_proof_chain_failure_source_path=filetransfer-retained-log-slice.log',
            'first_proof_chain_failure_line_number=2792'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $successorPressureInstallFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'REGULAR_V4_ROUTE_COMMIT_SEND_DRAIN_SUMMARY_FIXTURE') {
        $routeCommitSendDrainFixtureLines = @(
            'runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count=6',
            'runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count=1',
            'runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count=0',
            'runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count=1',
            'runtime_unlock_regular_v4_route_commit_send_drain_verdict=fail',
            'first_proof_chain_failure_class=runtime_unlock_regular_v4_route_commit_send_drain_missing',
            'first_proof_chain_failure_event=filetransfer_data_path_send_result',
            'first_proof_chain_failure_reason=retired_regular_v4_send_completed_after_tuna_owner_install',
            'first_proof_chain_failure_sequence=3900',
            'first_proof_chain_failure_source_path=filetransfer-retained-log-slice.log',
            'first_proof_chain_failure_line_number=3900',
            'first_proof_chain_failure_writer_sequence=1315',
            'semantic_failure_domain=route_runtime'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $routeCommitSendDrainFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'REGULAR_V4_SETTLEMENT_DISPOSITION_SUMMARY_FIXTURE') {
        $settlementDispositionFixtureLines = @(
            'regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count=1',
            'regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count=1',
            'regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count=0',
            'regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count=1',
            'regular_v4_frontier_repair_settlement_disposition_verdict=fail',
            'first_proof_chain_failure_class=regular_v4_frontier_repair_settlement_disposition_unverifiable',
            'first_proof_chain_failure_event=filetransfer_regular_v4_frontier_repair_settlement_accepted',
            'first_proof_chain_failure_reason=accepted_settlement_disposition_evidence_missing',
            'first_proof_chain_failure_sequence=5298',
            'first_proof_chain_failure_source_path=filetransfer-retained-log-slice.log',
            'first_proof_chain_failure_line_number=5298'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $settlementDispositionFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'REGULAR_V4_PRESSURE_AUTHORITY_LIFECYCLE_SPLIT_SUMMARY_FIXTURE') {
        $pressureLifecycleSplitFixtureLines = @(
            'regular_v4_frontier_repair_pressure_authority_lifecycle_split_raw_count=1',
            'regular_v4_frontier_repair_pressure_authority_lifecycle_split_distinct_count=1',
            'regular_v4_frontier_repair_pressure_authority_lifecycle_split_recovered_count=0',
            'regular_v4_frontier_repair_pressure_authority_lifecycle_split_unresolved_count=1',
            'regular_v4_frontier_repair_pressure_authority_lifecycle_split_verdict=fail',
            'first_proof_chain_failure_class=regular_v4_frontier_repair_pressure_authority_lifecycle_split',
            'first_proof_chain_failure_event=filetransfer_v4_receive_liveness_summary',
            'first_proof_chain_failure_reason=coordinator_repair_authority_outlived_legacy_pressure_ttl',
            'first_proof_chain_failure_sequence=4224',
            'first_proof_chain_failure_source_path=filetransfer-retained-log-slice.log',
            'first_proof_chain_failure_line_number=4224',
            'semantic_failure_domain=route_runtime'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $pressureLifecycleSplitFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'REGULAR_V4_PRESSURE_RECOVERY_HANDOFF_SUMMARY_FIXTURE') {
        $pressureRecoveryHandoffFixtureLines = @(
            'regular_v4_frontier_repair_pressure_recovery_handoff_missing_raw_count=1',
            'regular_v4_frontier_repair_pressure_recovery_handoff_missing_distinct_count=1',
            'regular_v4_frontier_repair_pressure_recovery_handoff_missing_recovered_count=0',
            'regular_v4_frontier_repair_pressure_recovery_handoff_missing_unresolved_count=1',
            'regular_v4_frontier_repair_pressure_recovery_handoff_verdict=fail',
            'first_proof_chain_failure_class=regular_v4_frontier_repair_pressure_recovery_handoff_missing',
            'first_proof_chain_failure_event=filetransfer_recovery_authority_rejected',
            'first_proof_chain_failure_reason=installed_repair_pressure_not_retired_before_receive_recovery',
            'first_proof_chain_failure_sequence=1769',
            'first_proof_chain_failure_writer_sequence=1287',
            'first_proof_chain_failure_source_path=filetransfer-retained-log-slice.log',
            'first_proof_chain_failure_line_number=1769',
            'semantic_failure_domain=route_runtime'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $pressureRecoveryHandoffFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RECOVERY_LEASE_CLOSURE_LIVENESS_SUMMARY_FIXTURE') {
        $closureLivenessFixtureLines = @(
            'runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count=1',
            'runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count=1',
            'runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count=0',
            'runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count=1',
            'runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict=fail'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $closureLivenessFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'BRIDGE_RECOVERY_STARTUP_PAYLOAD_ISOLATION_SUMMARY_FIXTURE') {
        $bridgeRecoveryStartupPayloadIsolationFixtureLines = @(
            'filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count=1',
            'filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count=1',
            'filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count=0',
            'filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count=1',
            'filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict=fail',
            'first_proof_chain_failure_class=filetransfer_bridge_recovery_execution_startup_payload_isolation_missing',
            'first_proof_chain_failure_event=nkn_bridge_recovery_execution_completed',
            'first_proof_chain_failure_reason=bridge_recovery_execution_timeout_after_pre_ready_payload_submission',
            'first_proof_chain_failure_sequence=3258',
            'first_proof_chain_failure_source_path=filetransfer-retained-log-slice.log',
            'first_proof_chain_failure_line_number=3258',
            'first_proof_chain_failure_writer_sequence=1597',
            'semantic_failure_domain=route_runtime'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $bridgeRecoveryStartupPayloadIsolationFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RETRY_DISPATCH_INSTALL_GRANT_EVIDENCE_SUMMARY_FIXTURE') {
        $grantEvidenceFixtureLines = @(
            'runtime_unlock_retry_dispatch_authority_granted_raw_count=2',
            'runtime_unlock_retry_dispatch_authority_granted_count=0',
            'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count=2',
            'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count=2',
            'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count=0',
            'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count=2',
            'runtime_unlock_retry_dispatch_install_grant_evidence_verdict=fail',
            'first_proof_chain_failure_class=runtime_unlock_retry_dispatch_install_grant_evidence_incomplete',
            'first_proof_chain_failure_event=runtime_unlock_retry_dispatch_authority_granted',
            'first_proof_chain_failure_reason=retry_dispatch_grant_request_active_predecessor_supersession_missing',
            'first_proof_chain_failure_sequence=5510',
            'first_proof_chain_failure_source_path=filetransfer-retained-log-slice.log',
            'first_proof_chain_failure_line_number=5510'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $grantEvidenceFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'PRECOMMIT_PROBE_START_HANDOFF_SUMMARY_FIXTURE') {
        $preCommitProbeStartHandoffFixtureLines = @(
            'runtime_unlock_retry_dispatch_post_grant_hold_count=1',
            'runtime_unlock_retry_dispatch_post_grant_hold_raw_count=1',
            'runtime_unlock_retry_dispatch_post_grant_hold_distinct_count=1',
            'runtime_unlock_retry_dispatch_post_grant_hold_recovered_count=1',
            'runtime_unlock_retry_dispatch_post_grant_hold_unresolved_count=0',
            'runtime_unlock_retry_dispatch_post_grant_hold_verdict=pass',
            'runtime_unlock_precommit_probe_start_handoff_missing_raw_count=1',
            'runtime_unlock_precommit_probe_start_handoff_missing_distinct_count=1',
            'runtime_unlock_precommit_probe_start_handoff_missing_recovered_count=0',
            'runtime_unlock_precommit_probe_start_handoff_missing_unresolved_count=1',
            'runtime_unlock_precommit_probe_start_handoff_verdict=fail',
            'first_proof_chain_failure_class=runtime_unlock_precommit_probe_start_handoff_missing',
            'first_proof_chain_failure_event=runtime_unlock_precommit_probe_start_timeout',
            'first_proof_chain_failure_reason=runtime_unlock_probe_start_timeout',
            'first_proof_chain_failure_sequence=10960',
            'first_proof_chain_failure_source_path=filetransfer-retained-log-slice.log',
            'first_proof_chain_failure_line_number=10960',
            'first_proof_chain_failure_writer_sequence=2647',
            'semantic_failure_domain=route_runtime'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $preCommitProbeStartHandoffFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'POST_TUNA_FALLBACK_TRANSPORT_PROOF_HANDOFF_SUMMARY_FIXTURE') {
        $postTunaFallbackHandoffFixtureLines = @(
            'runtime_unlock_retry_regular_v4_recovery_settlement_missing_count=0',
            'runtime_unlock_retry_regular_v4_recovery_settlement_missing_raw_count=1',
            'runtime_unlock_retry_regular_v4_recovery_settlement_missing_distinct_count=1',
            'runtime_unlock_retry_regular_v4_recovery_settlement_missing_recovered_count=1',
            'runtime_unlock_retry_regular_v4_recovery_settlement_missing_unresolved_count=0',
            'runtime_unlock_retry_regular_v4_recovery_settlement_verdict=pass',
            'runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count=1',
            'runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count=1',
            'runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count=0',
            'runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count=1',
            'runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict=fail',
            'first_proof_chain_failure_class=runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing',
            'first_proof_chain_failure_event=tuna_acceleration_timeline',
            'first_proof_chain_failure_reason=scheduled_runtime_unlock_handoff_not_started',
            'first_proof_chain_failure_sequence=22360',
            'first_proof_chain_failure_source_path=filetransfer-retained-log-slice.log',
            'first_proof_chain_failure_line_number=22360'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $postTunaFallbackHandoffFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if (Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'FALLBACK_TAIL_UNBLOCK_RECHECK_SUMMARY_FIXTURE') {
        $fallbackTailUnblockRecheckFixtureLines = @(
            'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_requested_count=0',
            'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided_count=0',
            'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_discarded_count=0',
            'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count=1',
            'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count=1',
            'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count=0',
            'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count=1',
            'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict=fail'
        )
        foreach ($reportName in @('filetransfer-route-consistency-summary.txt', 'filetransfer-operator-verdict.txt')) {
            $reportPath = Join-Path $ArtifactDir $reportName
            $reportText = Get-Content -LiteralPath $reportPath -Raw
            foreach ($fixtureLine in $fallbackTailUnblockRecheckFixtureLines) {
                $separatorIndex = $fixtureLine.IndexOf('=')
                $fixtureKey = $fixtureLine.Substring(0, $separatorIndex)
                $fixturePattern = '(?m)^{0}=.*$' -f [regex]::Escape($fixtureKey)
                if ([regex]::IsMatch($reportText, $fixturePattern)) {
                    $reportText = [regex]::Replace($reportText, $fixturePattern, $fixtureLine)
                }
                else {
                    $reportText = $reportText.TrimEnd("`r", "`n") + [Environment]::NewLine + $fixtureLine + [Environment]::NewLine
                }
            }
            Set-Content -LiteralPath $reportPath -Value $reportText -Encoding UTF8
        }
    }

    if ($forcePostArtifactExecutionFailure) {
        throw 'Injected fake Phase 4 scenario execution failure after artifacts.'
    }
}

function Write-RouteAcceptanceRetainedAnalysisTimeoutArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [int]$ProcessId = 0,
        [string]$StartedUtc = '',
        [string]$LiveRouteProofMode = 'None'
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null

    $guiSummaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    $guiSummaryPresent = Test-Path -LiteralPath $guiSummaryPath -PathType Leaf
    $guiCompleted = ''
    $guiIntegrityOk = ''
    $guiInboundTerminalState = ''
    $guiOutboundTerminalState = ''
    if ($guiSummaryPresent) {
        $guiSummaryRaw = ''
        for ($readAttempt = 0; $readAttempt -lt 3; $readAttempt++) {
            try {
                $guiSummaryRaw = Get-Content -LiteralPath $guiSummaryPath -Raw -ErrorAction Stop
                $summary = $guiSummaryRaw | ConvertFrom-Json -ErrorAction Stop
                $guiCompleted = [string]$summary.completed
                $guiIntegrityOk = [string]$summary.integrityOk
                $guiInboundTerminalState = [string]$summary.inboundTerminalState
                $guiOutboundTerminalState = [string]$summary.outboundTerminalState
                break
            }
            catch {
                if ($readAttempt -lt 2) {
                    Start-Sleep -Milliseconds 200
                }
            }
        }

        if ([string]::IsNullOrWhiteSpace($guiCompleted) -and -not [string]::IsNullOrWhiteSpace($guiSummaryRaw)) {
            $completedMatch = [regex]::Match($guiSummaryRaw, '"completed"\s*:\s*(true|false)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($completedMatch.Success) {
                $guiCompleted = if ([string]::Equals($completedMatch.Groups[1].Value, 'true', [System.StringComparison]::OrdinalIgnoreCase)) { 'True' } else { 'False' }
            }

            $integrityMatch = [regex]::Match($guiSummaryRaw, '"integrityOk"\s*:\s*(true|false)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($integrityMatch.Success) {
                $guiIntegrityOk = if ([string]::Equals($integrityMatch.Groups[1].Value, 'true', [System.StringComparison]::OrdinalIgnoreCase)) { 'True' } else { 'False' }
            }

            $inboundStateMatch = [regex]::Match($guiSummaryRaw, '"inboundTerminalState"\s*:\s*"([^"]*)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($inboundStateMatch.Success) {
                $guiInboundTerminalState = $inboundStateMatch.Groups[1].Value
            }

            $outboundStateMatch = [regex]::Match($guiSummaryRaw, '"outboundTerminalState"\s*:\s*"([^"]*)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($outboundStateMatch.Success) {
                $guiOutboundTerminalState = $outboundStateMatch.Groups[1].Value
            }
        }

        if ([string]::IsNullOrWhiteSpace($guiCompleted)) {
            $guiCompleted = 'unreadable'
        }
        if ([string]::IsNullOrWhiteSpace($guiIntegrityOk)) {
            $guiIntegrityOk = 'unreadable'
        }
    }

    $routeSummaryPath = Join-Path $ArtifactDir 'filetransfer-route-consistency-summary.txt'
    $routeSummaryPresent = Test-Path -LiteralPath $routeSummaryPath -PathType Leaf
    $routeConsistencyVerdict = ''
    $liveRouteEpochProofVerdict = ''
    $fallbackLegAuthorityProofVerdict = ''
    $cleanFallbackLegProofVerdict = ''
    $bridgeLivenessIntegrationVerdict = ''
    $controlPlaneIsolationVerdict = ''
    if ($routeSummaryPresent) {
        $routeSummary = Read-RouteAcceptanceKeyValueArtifact -Path $routeSummaryPath
        $routeConsistencyVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'route_consistency_verdict' -DefaultValue '(missing)'
        $liveRouteEpochProofVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'live_route_epoch_proof_verdict' -DefaultValue '(missing)'
        $fallbackLegAuthorityProofVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'fallback_leg_authority_proof_verdict' -DefaultValue '(missing)'
        $cleanFallbackLegProofVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'clean_fallback_leg_proof_verdict' -DefaultValue $fallbackLegAuthorityProofVerdict
        $bridgeLivenessIntegrationVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'bridge_liveness_integration_verdict' -DefaultValue '(missing)'
        $controlPlaneIsolationVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'control_plane_isolation_verdict' -DefaultValue '(missing)'
    }

    $requiresLiveProof = -not [string]::Equals($LiveRouteProofMode, 'None', [System.StringComparison]::OrdinalIgnoreCase)
    $hasFailedRouteProof = [string]::Equals($liveRouteEpochProofVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($fallbackLegAuthorityProofVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($cleanFallbackLegProofVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($bridgeLivenessIntegrationVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($controlPlaneIsolationVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase)
    $routeProofOk = $routeSummaryPresent -and
        [string]::Equals($routeConsistencyVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase) -and
        (-not $hasFailedRouteProof) -and
        (-not $requiresLiveProof -or [string]::Equals($liveRouteEpochProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($fallbackLegAuthorityProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($cleanFallbackLegProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($bridgeLivenessIntegrationVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($controlPlaneIsolationVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase))

    $terminalSuccess = [string]::Equals($guiCompleted, 'True', [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($guiIntegrityOk, 'True', [System.StringComparison]::OrdinalIgnoreCase)
    $terminalSuccessWithProof = $terminalSuccess -and $routeProofOk
    $failureClass = if ($terminalSuccessWithProof) { 'retained_analysis_timeout_after_terminal_success' } else { 'retained_analysis_timeout' }
    $acceptanceFailureClass = if ($terminalSuccessWithProof) { 'none' } else { 'setup' }
    $operatorVerdict = if ($terminalSuccessWithProof) { 'PASS' } else { 'INVALID_SETUP' }
    $warningKinds = if ($terminalSuccessWithProof) { 'retained_analysis_timeout_after_terminal_success' } else { '(none)' }

    $dto = [ordered]@{
        failure_class = $failureClass
        acceptance_failure_class = $acceptanceFailureClass
        timed_out = 1
        timeout_seconds = $TimeoutSeconds
        process_id = $ProcessId
        started_utc = $StartedUtc
        ended_utc = [DateTimeOffset]::UtcNow.ToString('O')
        artifact_dir = $ArtifactDir
        log_path = $LogPath
        live_route_proof_mode = $LiveRouteProofMode
        gui_summary_present = if ($guiSummaryPresent) { 1 } else { 0 }
        gui_completed = $guiCompleted
        gui_integrity_ok = $guiIntegrityOk
        gui_inbound_terminal_state = $guiInboundTerminalState
        gui_outbound_terminal_state = $guiOutboundTerminalState
        terminal_success_timeout = if ($terminalSuccessWithProof) { 1 } else { 0 }
        terminal_success_without_retained_route_proof = if ($terminalSuccess -and -not $routeProofOk) { 1 } else { 0 }
        route_summary_present = if ($routeSummaryPresent) { 1 } else { 0 }
        route_consistency_verdict = $routeConsistencyVerdict
        live_route_epoch_proof_verdict = $liveRouteEpochProofVerdict
        fallback_leg_authority_proof_verdict = $fallbackLegAuthorityProofVerdict
        clean_fallback_leg_proof_verdict = $cleanFallbackLegProofVerdict
        bridge_liveness_integration_verdict = $bridgeLivenessIntegrationVerdict
        control_plane_isolation_verdict = $controlPlaneIsolationVerdict
        warning_kinds = $warningKinds
    }

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($key in @($dto.Keys)) {
        $lines.Add(('{0}={1}' -f $key, $dto[$key])) | Out-Null
    }

    Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-analysis-timeout-summary.txt') -Value $lines.ToArray() -Encoding UTF8
    $dto | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-analysis-timeout-summary.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-operator-verdict.txt') -Value @(
        ('verdict={0}' -f $operatorVerdict),
        ('acceptance_failure_class={0}' -f $acceptanceFailureClass),
        ('failure_class={0}' -f $failureClass),
        ('hard_failure_count={0}' -f ($(if ($terminalSuccessWithProof) { 0 } else { 1 }))),
        ('warning_kinds={0}' -f $warningKinds),
        ('timeout_seconds={0}' -f $TimeoutSeconds),
        ('log_path={0}' -f $LogPath)
    ) -Encoding UTF8

    return $terminalSuccessWithProof
}

function Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [string]$LiveRouteProofMode = 'None'
    )

    $guiSummaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    $routeSummaryPath = Join-Path $ArtifactDir 'filetransfer-route-consistency-summary.txt'
    $operatorVerdictPath = Join-Path $ArtifactDir 'filetransfer-operator-verdict.txt'
    $stabilitySummaryPath = Join-Path $ArtifactDir 'stability-gates-summary.txt'
    if (-not (Test-Path -LiteralPath $guiSummaryPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $routeSummaryPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $operatorVerdictPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $stabilitySummaryPath -PathType Leaf)) {
        return $false
    }

    try {
        $summary = Get-Content -LiteralPath $guiSummaryPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if (-not [string]::Equals([string]$summary.completed, 'True', [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string]$summary.integrityOk, 'True', [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    catch {
        return $false
    }

    $operator = Read-RouteAcceptanceKeyValueArtifact -Path $operatorVerdictPath
    $operatorVerdict = Get-RouteAcceptanceReportValue -Report $operator -Name 'verdict' -DefaultValue '(missing)'
    $operatorHardFailureCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'hard_failure_count' -DefaultValue '-1')
    if ($operatorHardFailureCount -ne 0 -or
        [string]::Equals($operatorVerdict, '(missing)', [System.StringComparison]::OrdinalIgnoreCase) -or
        $operatorVerdict.StartsWith('FAIL', [System.StringComparison]::OrdinalIgnoreCase) -or
        $operatorVerdict.StartsWith('INVALID', [System.StringComparison]::OrdinalIgnoreCase) -or
        $operatorVerdict.StartsWith('INCONCLUSIVE', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $stability = Read-RouteAcceptanceKeyValueArtifact -Path $stabilitySummaryPath
    $stabilityGateStatus = Get-RouteAcceptanceReportValue -Report $stability -Name 'gate_status' -DefaultValue '(missing)'
    $stabilityHardFailureCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $stability -Name 'hard_failure_count' -DefaultValue '0')
    if (-not [string]::Equals($stabilityGateStatus, 'pass', [System.StringComparison]::OrdinalIgnoreCase) -or
        $stabilityHardFailureCount -ne 0) {
        return $false
    }

    $routeSummary = Read-RouteAcceptanceKeyValueArtifact -Path $routeSummaryPath
    $routeConsistencyVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'route_consistency_verdict' -DefaultValue '(missing)'
    $liveRouteEpochProofVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'live_route_epoch_proof_verdict' -DefaultValue '(missing)'
    $fallbackLegAuthorityProofVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'fallback_leg_authority_proof_verdict' -DefaultValue '(missing)'
    $cleanFallbackLegProofVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'clean_fallback_leg_proof_verdict' -DefaultValue $fallbackLegAuthorityProofVerdict
    $bridgeLivenessIntegrationVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'bridge_liveness_integration_verdict' -DefaultValue '(missing)'
    $controlPlaneIsolationVerdict = Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'control_plane_isolation_verdict' -DefaultValue '(missing)'
    $requiresLiveProof = -not [string]::Equals($LiveRouteProofMode, 'None', [System.StringComparison]::OrdinalIgnoreCase)
    $hasFailedRouteProof = [string]::Equals($liveRouteEpochProofVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($fallbackLegAuthorityProofVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($cleanFallbackLegProofVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($bridgeLivenessIntegrationVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($controlPlaneIsolationVerdict, 'fail', [System.StringComparison]::OrdinalIgnoreCase)

    return [string]::Equals($routeConsistencyVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase) -and
        (-not $hasFailedRouteProof) -and
        (-not $requiresLiveProof -or [string]::Equals($liveRouteEpochProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($fallbackLegAuthorityProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($cleanFallbackLegProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($bridgeLivenessIntegrationVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) -and
        (-not $requiresLiveProof -or [string]::Equals($controlPlaneIsolationVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase))
}

function Stop-RouteAcceptanceRetainedAnalysisProcess {
    param([Parameter(Mandatory = $true)]$Process)

    try {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    } catch {
    }

    try {
        $Process.WaitForExit(5000) | Out-Null
    } catch {
    }
}

function Invoke-RouteAcceptanceRetainedAnalysis {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [ValidateSet("None", "SwitchOff", "MultiToggle", "RegularActivationCycle")]
        [string]$LiveRouteProofMode = "None"
    )

    $logPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        throw "Retained log slice missing: $logPath"
    }

    $analysisStdoutPath = Join-Path $ArtifactDir 'filetransfer-retained-analysis.stdout.txt'
    $analysisStderrPath = Join-Path $ArtifactDir 'filetransfer-retained-analysis.stderr.txt'
    $analysisDiagnosticsPath = Join-Path $ArtifactDir 'filetransfer-retained-analysis-diagnostics.json'
    $analysisTimeoutSeconds = 1200
    $startedUtc = [DateTimeOffset]::UtcNow

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'powershell'
    $analysisArguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', '.\tools\FileTransfer-Ops.ps1',
        '-Mode', 'AnalyzeRetained',
        '-LogPath', $logPath,
        '-ArtifactDir', $ArtifactDir,
        '-TailMinutes', '0',
        '-LiveRouteProofMode', $LiveRouteProofMode)
    $startInfo.Arguments = ($analysisArguments | ForEach-Object {
            $argument = [string]$_
            if ($argument.Length -eq 0) {
                '""'
            }
            elseif ($argument.IndexOfAny([char[]]@(' ', "`t", '"')) -lt 0) {
                $argument
            }
            else {
                '"' + ($argument -replace '"', '\"') + '"'
            }
        }) -join ' '
    $startInfo.WorkingDirectory = (Get-Location).Path
    $startInfo.UseShellExecute = $false
    if ($script:RouteAcceptanceExecutionMode -eq 'live') {
        $startInfo.Environment['NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT'] = '1'
    }
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdoutTask = $null
    $stderrTask = $null
    try {
        if (-not $process.Start()) {
            throw "Failed to start FileTransfer-Ops retained analysis for $ArtifactDir"
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $deadlineUtc = $startedUtc.AddSeconds($analysisTimeoutSeconds)
        while (-not $process.WaitForExit(5000)) {
            if (Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts -ArtifactDir $ArtifactDir -LiveRouteProofMode $LiveRouteProofMode) {
                Stop-RouteAcceptanceRetainedAnalysisProcess -Process $process
                $diagnostics = [ordered]@{
                    completed_artifacts_observed_while_process_alive = 1
                    timed_out = 0
                    process_id = $process.Id
                    started_utc = $startedUtc.ToString('O')
                    ended_utc = [DateTimeOffset]::UtcNow.ToString('O')
                    artifact_dir = $ArtifactDir
                    log_path = $logPath
                    live_route_proof_mode = $LiveRouteProofMode
                }
                $diagnostics | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-analysis-completed-process-reaped.json') -Encoding UTF8
                return
            }

            if ([DateTimeOffset]::UtcNow -lt $deadlineUtc) {
                continue
            }

            Stop-RouteAcceptanceRetainedAnalysisProcess -Process $process
            $diagnostics = [ordered]@{
                timed_out = 1
                timeout_seconds = $analysisTimeoutSeconds
                process_id = $process.Id
                started_utc = $startedUtc.ToString('O')
                ended_utc = [DateTimeOffset]::UtcNow.ToString('O')
                artifact_dir = $ArtifactDir
                log_path = $logPath
                live_route_proof_mode = $LiveRouteProofMode
            }
            $diagnostics | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $analysisDiagnosticsPath -Encoding UTF8
            $terminalSuccessTimeout = Write-RouteAcceptanceRetainedAnalysisTimeoutArtifacts `
                -ArtifactDir $ArtifactDir `
                -LogPath $logPath `
                -TimeoutSeconds $analysisTimeoutSeconds `
                -ProcessId $process.Id `
                -StartedUtc $startedUtc.ToString('O') `
                -LiveRouteProofMode $LiveRouteProofMode
            if ($terminalSuccessTimeout) {
                return
            }

            throw "FileTransfer-Ops retained analysis timed out after ${analysisTimeoutSeconds}s for $ArtifactDir"
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        Set-Content -LiteralPath $analysisStdoutPath -Value $stdout -Encoding UTF8
        Set-Content -LiteralPath $analysisStderrPath -Value $stderr -Encoding UTF8
        $diagnostics = [ordered]@{
            timed_out = 0
            exit_code = $process.ExitCode
            timeout_seconds = $analysisTimeoutSeconds
            process_id = $process.Id
            started_utc = $startedUtc.ToString('O')
            ended_utc = [DateTimeOffset]::UtcNow.ToString('O')
            artifact_dir = $ArtifactDir
            log_path = $logPath
            live_route_proof_mode = $LiveRouteProofMode
        }
        $diagnostics | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $analysisDiagnosticsPath -Encoding UTF8
        if ($process.ExitCode -ne 0) {
            throw "FileTransfer-Ops retained analysis failed for $ArtifactDir with exit code $($process.ExitCode)"
        }
    }
    finally {
        if ($process -is [System.IDisposable]) {
            $process.Dispose()
        }
    }
}

function Select-RouteAcceptanceMeasuredFallbackLogSlice {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $logPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        return
    }

    $fullPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice-full.log'
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        Copy-Item -LiteralPath $logPath -Destination $fullPath -Force
    }

    $sliceSourcePath = if (Test-Path -LiteralPath $fullPath -PathType Leaf) { $fullPath } else { $logPath }
    $lines = @(Get-Content -LiteralPath $sliceSourcePath)
    $setupStartIndex = -1
    $firstFallbackIndex = -1
    $setupCanceledTerminalIndex = -1
    $startIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($setupStartIndex -lt 0 -and
            $lines[$i].IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('route=file_tuna_v4', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $setupStartIndex = $i
        }

        if ($firstFallbackIndex -lt 0 -and
            $lines[$i].IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('route=post_tuna_fallback_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $firstFallbackIndex = $i
        }

        if ($firstFallbackIndex -ge 0 -and
            $setupCanceledTerminalIndex -lt 0 -and
            $lines[$i].IndexOf('event=file_transfer_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('state=Canceled', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $setupCanceledTerminalIndex = $i
        }

        if ($setupCanceledTerminalIndex -ge 0 -and
            $i -gt $setupCanceledTerminalIndex -and
            $lines[$i].IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $lines[$i].IndexOf('route=post_tuna_fallback_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $startIndex = $i
            break
        }
    }

    if ($startIndex -lt 0) {
        $startIndex = $firstFallbackIndex
    }

    if ($startIndex -lt 0) {
        return
    }

    if ($setupStartIndex -ge 0 -and $startIndex -gt $setupStartIndex) {
        $lines[$setupStartIndex..($startIndex - 1)] | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log') -Encoding UTF8
    }

    $measuredLines = $lines[$startIndex..($lines.Count - 1)]
    $measuredLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log') -Encoding UTF8
    $measuredLines | Set-Content -LiteralPath $logPath -Encoding UTF8
}

function Write-RouteAcceptanceFakeRegularRun {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$PayloadSize
    )

    $route = Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_ROUTE' -DefaultValue 'regular_nkn_v4_fast'
    $protocol = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_PROTOCOL' -DefaultValue '0')
    $defaultGoodput = Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_GOODPUT_BPS' -DefaultValue '8388608'
    $payloadGoodputOverrideName = if ($PayloadSize -eq '128MiB') { 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_128MB_GOODPUT_BPS' } else { 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_64MB_GOODPUT_BPS' }
    $goodput = ConvertTo-RouteAcceptanceDouble -Value (Get-RouteAcceptanceEnvValue -Name $payloadGoodputOverrideName -DefaultValue $defaultGoodput)
    $bridgeFailures = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_REGULAR_BRIDGE_FAILURES' -DefaultValue '0')
    $terminalState = if (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ZOMBIE_TERMINAL') { 'Sending' } else { 'Completed' }
    $payloadBytes = if ($PayloadSize -eq '128MiB') { 134217728L } else { 67108864L }
    $completed = $terminalState -eq 'Completed'

    Write-RouteAcceptanceFakeRetainedLog -ArtifactDir $ArtifactDir -TransferId ('fake-{0}' -f $PayloadSize.ToLowerInvariant()) -Route $route -ProtocolOverride $protocol -TerminalState $terminalState -BridgeBulkSendFailures $bridgeFailures
    Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $ArtifactDir

    if (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_MISSING_ROUTE_SUMMARY') {
        Remove-Item -LiteralPath (Join-Path $ArtifactDir 'filetransfer-route-consistency-summary.txt') -Force -ErrorAction SilentlyContinue
    }

    $effectiveProtocol = if ($protocol -gt 0) { $protocol } else { (Get-RouteAcceptanceRouteMetadata -Route $route).Protocol }
    $cycle = [ordered]@{
        cycle_index = 0
        mode = 'nkn-fast'
        scenario = 'FILETRANSFER_NKN_SOAK'
        direction = 'helpee-to-helper'
        transfer_id = ('fake-{0}' -f $PayloadSize.ToLowerInvariant())
        payload_bytes = $payloadBytes
        duration_ms = [Math]::Max(1L, [long][Math]::Round(($payloadBytes / [Math]::Max(1D, $goodput)) * 1000D))
        goodput_bytes_per_second = $goodput
        completed = $completed
        integrity_ok = $completed
        inbound_state = $terminalState
        outbound_state = $terminalState
    }
    $cycle | ConvertTo-Json -Compress -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl') -Encoding UTF8

    $summary = [ordered]@{
        artifact_kind = 'live-nkn'
        mode = 'nkn-fast'
        verdict = if ($completed) { 'PASS' } else { 'FAIL_PROTOCOL_OR_INTEGRITY' }
        gate_status = if ($completed) { 'pass' } else { 'fail' }
        cycles_requested = 1
        cycles_observed = 1
        cycles_completed = if ($completed) { 1 } else { 0 }
        total_payload_bytes = if ($completed) { $payloadBytes } else { 0 }
        average_goodput_bytes_per_second = ('{0:F3}' -f $goodput)
        min_goodput_bytes_per_second = ('{0:F3}' -f $goodput)
        data_protocol_version = $effectiveProtocol
        bridge_bulk_send_failure_count = $bridgeFailures
        gui_progress_timeout_count = 0
        terminal_missing_after_progress_timeout = 0
    }

    $summaryLines = New-Object System.Collections.Generic.List[string]
    foreach ($key in $summary.Keys) {
        $summaryLines.Add(("{0}={1}" -f $key, $summary[$key])) | Out-Null
    }
    $summaryLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt') -Encoding UTF8
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.json') -Encoding UTF8
}

function Write-RouteAcceptanceFakeTunaRun {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$Route,
        [Parameter(Mandatory = $true)][string]$RouteMode
    )

    $routeOverrideName = if ($RouteMode -eq 'preactivated') { 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_ROUTE' } else { 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_FALLBACK_ROUTE' }
    $effectiveRoute = Get-RouteAcceptanceEnvValue -Name $routeOverrideName -DefaultValue $Route
    $protocolOverride = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_PROTOCOL' -DefaultValue '0')
    $defaultGoodput = Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_GOODPUT_BPS' -DefaultValue '5000000'
    $goodputOverrideName = if ($RouteMode -eq 'preactivated') { 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_GOODPUT_BPS' } else { 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_FALLBACK_GOODPUT_BPS' }
    $goodput = ConvertTo-RouteAcceptanceDouble -Value (Get-RouteAcceptanceEnvValue -Name $goodputOverrideName -DefaultValue $defaultGoodput)
    $terminalState = if (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ZOMBIE_TERMINAL') { 'Receiving' } else { 'Completed' }
    $completed = $terminalState -eq 'Completed'
    $payloadBytes = 134217728L
    $forceFallbackEvidence = $RouteMode -eq 'preactivated' -and (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_FALLBACK')
    $effectiveProtocol = if ($protocolOverride -gt 0) { $protocolOverride } else { [int](Get-RouteAcceptanceRouteMetadata -Route $effectiveRoute).Protocol }
    $inboundError = if ($completed) { '(none)' } else { 'pending' }
    $outboundError = if ($completed) { '(none)' } else { 'pending' }
    $emitNoFaultExternalWarning = $RouteMode -eq 'preactivated' -and (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TUNA_NOFAULT_EXTERNAL_WARNING')
    $isControlledRestartMode = $RouteMode -eq 'v4-restart-v6-fallback'
    $emitFallbackRecoveredBridgeWarning = $isControlledRestartMode -and (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_RECOVERED_BRIDGE_WARNING')
    $setupFileTunaPublicationSuffix = Get-RouteAcceptanceFakePublicationAuthoritySuffix -Route 'file_tuna_v4' -Protocol 4

    Write-RouteAcceptanceFakeRetainedLog -ArtifactDir $ArtifactDir -TransferId ('fake-tuna-{0}' -f $RouteMode) -Route $effectiveRoute -ProtocolOverride $protocolOverride -TerminalState $terminalState -BridgeBulkSendFailures 0
    if ($emitNoFaultExternalWarning) {
        Add-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8 -Value (New-RouteAcceptanceFakeLogLine -SecondsOffset 60 -Message 'event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1')
    }

    if ($isControlledRestartMode) {
        $logPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
        Add-Content -LiteralPath $logPath -Encoding UTF8 -Value (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_cleanup_closed')
        if ($emitFallbackRecoveredBridgeWarning) {
            @(
                (New-RouteAcceptanceFakeLogLine -SecondsOffset 40 -Message ('event=filetransfer_transport_epoch_started_while_unavailable; direction=outbound; transfer_id=fake-tuna-{0}; session_id=sess_fake; reason=transport_recovered_unproven; target_transport=regular_nkn' -f $RouteMode))
                (New-RouteAcceptanceFakeLogLine -SecondsOffset 60 -Message 'event=nkn_bridge_bulk_send_summary; frames_sent=17; frames_enqueued=22; payload_bytes_sent=847447; payload_bytes_per_second=423724; send_failures=0; queue_clears=5; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4')
            ) | Add-Content -LiteralPath $logPath -Encoding UTF8
        }
        Copy-Item -LiteralPath $logPath -Destination (Join-Path $ArtifactDir 'filetransfer-retained-log-slice-full.log') -Force
        Copy-Item -LiteralPath $logPath -Destination (Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log') -Force
        @(
            (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_started')
            (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_route_selected; direction=outbound; transfer_id=fake-tuna-v4-setup; session_id=sess_setup; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=fake{0}" -f $setupFileTunaPublicationSuffix))
            (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_inbound_terminal; role=helper; session_id=sess_setup; transfer_id=fake-tuna-v4-setup; state=Canceled; error_code=canceled_remote')
            (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_outbound_terminal; role=helpee; session_id=sess_setup; transfer_id=fake-tuna-v4-setup; state=Canceled; error_code=canceled_local')
            (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id=fake-tuna-v4-setup; session_id=sess_setup; frame_type=filetransfer.cancel.v4; first_lane=control; second_lane=bulk; first_error=OperationCanceledException; second_error=OperationCanceledException')
        ) | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log') -Encoding UTF8
    }
    Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $ArtifactDir

    if (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_MISSING_ROUTE_SUMMARY') {
        Remove-Item -LiteralPath (Join-Path $ArtifactDir 'filetransfer-route-consistency-summary.txt') -Force -ErrorAction SilentlyContinue
    }

    $summary = [ordered]@{
        event = 'filetransfer_tuna_gui_handoff_fallback_summary'
        routeMode = $RouteMode
        direction = 'helpee-to-helper'
        payerMode = 'helpee'
        faultMode = if ($RouteMode -eq 'post-fallback' -or $isControlledRestartMode) { 'switch-off' } else { 'none' }
        transferId = ('fake-tuna-{0}' -f $RouteMode)
        payloadBytes = $payloadBytes
        durationMs = [Math]::Round(($payloadBytes / [Math]::Max(1D, $goodput)) * 1000D, 3)
        goodputBytesPerSecond = $goodput
        completed = $completed
        integrityOk = $completed
        inboundState = $terminalState
        outboundState = $terminalState
        inboundErrorCode = $inboundError
        outboundErrorCode = $outboundError
        expectedSha256 = 'fake'
        receivedSha256 = if ($completed) { 'fake' } else { '(none)' }
        savedFileSizeBytes = if ($completed) { $payloadBytes } else { 0 }
        setupPhase = if ($isControlledRestartMode) {
            [ordered]@{
                name = 'setup_file_tuna_v4'
                route = 'file_tuna_v4'
                protocolVersion = 4
                payloadBytes = 67108864
                completed = $false
                integrityOk = $false
                inboundState = 'Canceled'
                outboundState = 'Canceled'
                inboundErrorCode = 'canceled_remote'
                outboundErrorCode = 'canceled_local'
            }
        }
        else {
            $null
        }
        measuredPhase = [ordered]@{
            name = if ($RouteMode -eq 'v4-restart-v6-fallback') { 'measured_post_tuna_fallback_v6' } else { 'measured_file_tuna_v4' }
            route = $effectiveRoute
            protocolVersion = $effectiveProtocol
            payloadBytes = $payloadBytes
            goodputBytesPerSecond = $goodput
            completed = $completed
            integrityOk = $completed
            inboundState = $terminalState
            outboundState = $terminalState
            inboundErrorCode = $inboundError
            outboundErrorCode = $outboundError
        }
        evidence = [ordered]@{
            tunaNegotiated = $true
            activationEpochStarted = $RouteMode -eq 'preactivated'
            activationEpochRecovered = $RouteMode -eq 'preactivated'
            fallbackEpochStarted = $RouteMode -eq 'post-fallback' -or $isControlledRestartMode -or $forceFallbackEvidence
            fallbackEpochRecovered = $RouteMode -eq 'post-fallback' -or $isControlledRestartMode -or $forceFallbackEvidence
            fallbackEpochWaiting = $false
        }
        controlledRestartAnalysis = if ($isControlledRestartMode) {
            [ordered]@{
                setupVerdict = 'INVALID_SETUP'
                setupRawOperatorVerdict = 'INVALID_SETUP'
                setupControlledCancelAccepted = $true
                setupNormalizedVerdict = 'expected_controlled_setup_cancel'
                measuredRouteVerdict = 'pass'
                measuredOperatorVerdict = if ($emitFallbackRecoveredBridgeWarning) { 'WARN_EXTERNAL_TRANSPORT' } else { 'PASS' }
                setupCleanupWarningCount = 0
                fallbackBridgeRecoveryWarningCount = if ($emitFallbackRecoveredBridgeWarning) { 1 } else { 0 }
            }
        }
        else {
            $null
        }
        setupNormalizedVerdict = if ($isControlledRestartMode) { 'expected_controlled_setup_cancel' } else { $null }
        fallbackDiagnostics = if ($isControlledRestartMode) {
            [ordered]@{
                fallbackWarningKinds = if ($emitFallbackRecoveredBridgeWarning) { @('recovered_post_tuna_fallback_bridge_clear') } else { @() }
                sendTimeoutsPerMiB = 0
                frontierRequestsPerMiB = 0
                fallbackRescueFreezeCount = 0
                fallbackRescueWidenCount = 0
                setupNormalizedVerdict = 'expected_controlled_setup_cancel'
            }
        }
        else {
            $null
        }
    }

    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
}

function Write-RouteAcceptanceFakeFallbackRetryableFailure {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][int]$Attempt
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $setupFileTunaPublicationSuffix = Get-RouteAcceptanceFakePublicationAuthoritySuffix -Route 'file_tuna_v4' -Protocol 4
    $setupLines = @(
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_started')
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_route_selected; direction=outbound; transfer_id=fake-tuna-v4-setup; session_id=sess_setup; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=fake{0}" -f $setupFileTunaPublicationSuffix))
        (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_inbound_terminal; role=helper; session_id=sess_setup; transfer_id=fake-tuna-v4-setup; state=Canceled; error_code=canceled_remote')
        (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_outbound_terminal; role=helpee; session_id=sess_setup; transfer_id=fake-tuna-v4-setup; state=Canceled; error_code=canceled_local')
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_cleanup_closed')
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_live_progress_timeout; transfer_id=fake-tuna-v4-setup; reason=no useful data progress for 30s before measured fallback route started; total_wait_s=30; progress_timeout_seconds=30; receiver_next_chunk=0; receiver_highest_chunk=0; progress_events=0')
    )

    $setupLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8
    $setupLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice-full.log') -Encoding UTF8
    $setupLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log') -Encoding UTF8

    [ordered]@{
        event = 'filetransfer_tuna_gui_error'
        routeMode = 'v4-restart-v6-fallback'
        attempt = $Attempt
        measuredRouteStarted = $false
        reason = 'progress_timeout_before_measured_fallback_route'
        message = 'no useful data progress before measured fallback route started'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json') -Encoding UTF8
}

function Write-RouteAcceptanceFakeFallbackRouteNotReadyFailure {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][int]$Attempt
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $setupFileTunaPublicationSuffix = Get-RouteAcceptanceFakePublicationAuthoritySuffix -Route 'file_tuna_v4' -Protocol 4
    $measuredFallbackPublicationSuffix = Get-RouteAcceptanceFakePublicationAuthoritySuffix -Route 'post_tuna_fallback_v6' -Protocol 6 -LiveRouteEpoch 1 -HandoffKind 'tuna_to_normal_fallback'
    $setupLines = @(
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_started')
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_route_selected; direction=outbound; transfer_id=fake-tuna-v4-setup; session_id=sess_setup; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=fake{0}" -f $setupFileTunaPublicationSuffix))
        (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_inbound_terminal; role=helper; session_id=sess_setup; transfer_id=fake-tuna-v4-setup; state=Canceled; error_code=canceled_remote')
        (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_outbound_terminal; role=helpee; session_id=sess_setup; transfer_id=fake-tuna-v4-setup; state=Canceled; error_code=canceled_local')
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_cleanup_closed')
    )
    $measuredLines = @(
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_tuna_gui_phase_marker; phase=measured_post_tuna_fallback_v6_started')
        (New-RouteAcceptanceFakeLogLine -Message ("event=filetransfer_route_selected; direction=outbound; transfer_id=fake-fallback-route-not-ready; session_id=(none); route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=fake{0}" -f $measuredFallbackPublicationSuffix))
        (New-RouteAcceptanceFakeLogLine -Message 'event=filetransfer_legacy_negotiation_rejected; transfer_id=fake-fallback-route-not-ready; session_id=sess_fallback; direction=Inbound; offered_version=6; accepted_version=(none); reason=offer_route_not_active')
        (New-RouteAcceptanceFakeLogLine -Message 'event=file_transfer_outbound_terminal; role=Helpee; session_id=; transfer_id=fake-fallback-route-not-ready; state=Declined; error_code=(none)')
        (New-RouteAcceptanceFakeLogLine -Message 'event=transfer_terminal; direction=outbound; transfer_id=fake-fallback-route-not-ready; session_id=; file_name_len=33; file_size_bytes=134217728; bytes_transferred=0; chunks_transferred=0; chunk_count=0; error_code=(none); reason=transport_incompatible; saved_path=(none); route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active')
    )

    $fullLines = @($setupLines + $measuredLines)
    $fullLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log') -Encoding UTF8
    $fullLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-log-slice-full.log') -Encoding UTF8
    $setupLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-setup-retained-log-slice.log') -Encoding UTF8
    $measuredLines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log') -Encoding UTF8

    [ordered]@{
        event = 'filetransfer_tuna_gui_handoff_fallback_summary'
        routeMode = 'v4-restart-v6-fallback'
        attempt = $Attempt
        completed = $false
        integrityOk = $false
        fallbackFailurePhase = 'measured_terminal_failure'
        fallbackFailureReason = 'Timed out waiting for Chat.FileTransfer.Accept to become enabled.'
        fallbackDiagnostics = [ordered]@{
            measuredSlicePresent = $true
            finalTerminalState = 'Declined'
        }
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8

    [ordered]@{
        event = 'filetransfer_tuna_gui_handoff_fallback_failure'
        routeMode = 'v4-restart-v6-fallback'
        attempt = $Attempt
        measuredRouteStarted = $true
        reason = 'offer_route_not_active'
        message = 'Timed out waiting for Chat.FileTransfer.Accept to become enabled.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json') -Encoding UTF8
}

function Copy-RouteAcceptanceSelectedAttemptArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$AttemptDir,
        [Parameter(Mandatory = $true)][string]$ArtifactDir
    )

    foreach ($item in @(Get-ChildItem -LiteralPath $AttemptDir -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $ArtifactDir -Recurse -Force
    }
}

function Write-RouteAcceptanceAttemptSummary {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][object[]]$Attempts,
        [int]$SelectedAttempt = 0
    )

    $firstFailure = ''
    foreach ($attempt in @($Attempts)) {
        if (-not [bool]$attempt.succeeded) {
            $firstFailure = [string]$attempt.failureReason
            break
        }
    }

    [ordered]@{
        event = 'filetransfer_route_acceptance_fallback_attempts'
        attemptCount = @($Attempts).Count
        retryUsed = @($Attempts).Count -gt 1
        selectedAttempt = $SelectedAttempt
        firstFailureReason = $firstFailure
        attempts = @($Attempts)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'route-acceptance-attempts.json') -Encoding UTF8
}

function Test-RouteAcceptanceFallbackAttemptRetryable {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $observedRoutes = New-Object System.Collections.Generic.List[string]
    $summaryRetryable = $false
    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        $failurePhase = [string](Get-JsonPropertyValue -Object $summary -Name 'fallbackFailurePhase' -DefaultValue '')
        $failureReason = [string](Get-JsonPropertyValue -Object $summary -Name 'fallbackFailureReason' -DefaultValue '')
        $diagnostics = Get-JsonPropertyValue -Object $summary -Name 'fallbackDiagnostics' -DefaultValue $null
        $finalTerminalState = [string](Get-JsonPropertyValue -Object $diagnostics -Name 'finalTerminalState' -DefaultValue '')
        if ($failureReason.IndexOf('Timed out waiting for Chat.FileTransfer.Accept', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $failureReason.IndexOf('offer_route_not_active', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            ($failurePhase -eq 'measured_terminal_failure' -and $finalTerminalState -eq 'Declined')) {
            $summaryRetryable = $true
        }

        $measuredPhase = Get-JsonPropertyValue -Object $summary -Name 'measuredPhase' -DefaultValue $null
        if ($null -ne $measuredPhase) {
            $route = [string](Get-JsonPropertyValue -Object $measuredPhase -Name 'route' -DefaultValue '')
            if (-not [string]::IsNullOrWhiteSpace($route)) {
                $observedRoutes.Add($route) | Out-Null
            }
        }
    }

    $measuredPath = Join-Path $ArtifactDir 'filetransfer-measured-fallback-retained-log-slice.log'
    if (Test-Path -LiteralPath $measuredPath -PathType Leaf) {
        foreach ($line in @(Get-Content -LiteralPath $measuredPath)) {
            if ($line.IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                continue
            }

            if ($line -match 'route=([^;\s]+)') {
                $observedRoutes.Add($Matches[1]) | Out-Null
            }
        }
    }

    $uniqueRoutes = @($observedRoutes | Select-Object -Unique)
    if ($uniqueRoutes.Count -gt 0 -and
        -not ($uniqueRoutes -contains 'post_tuna_fallback_v6')) {
        return $false
    }

    if ($uniqueRoutes.Count -eq 0) {
        return $true
    }

    return $summaryRetryable -or
        $FailureMessage.IndexOf('progress timeout', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $FailureMessage.IndexOf('no useful data progress', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Write-RouteAcceptanceFakeFallbackRun {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $attempts = New-Object System.Collections.Generic.List[object]
    $maxAttempts = [Math]::Max(1, $FallbackMaxAttempts)
    $retryAttempt1 = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_RETRYABLE_ATTEMPT1'
    $routeNotReadyAttempt1 = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_ROUTE_NOT_READY_ATTEMPT1'
    $alwaysRetryableFailure = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_FALLBACK_RETRYABLE_ALWAYS'
    $fallbackRoute = 'post_tuna_fallback_v6'
    $fallbackRouteMode = 'v4-restart-v6-fallback'

    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $attemptDir = Join-Path $ArtifactDir ("attempt-{0}" -f $attempt)
        New-Item -ItemType Directory -Force -Path $attemptDir | Out-Null

        if ($alwaysRetryableFailure -or ($retryAttempt1 -and $attempt -eq 1)) {
            Write-RouteAcceptanceFakeFallbackRetryableFailure -ArtifactDir $attemptDir -Attempt $attempt
            $attempts.Add([ordered]@{
                attempt = $attempt
                artifactDir = $attemptDir
                succeeded = $false
                retryable = $true
                failureReason = 'progress_timeout_before_measured_fallback_route'
            }) | Out-Null
            continue
        }

        if ($routeNotReadyAttempt1 -and $attempt -eq 1) {
            Write-RouteAcceptanceFakeFallbackRouteNotReadyFailure -ArtifactDir $attemptDir -Attempt $attempt
            $attempts.Add([ordered]@{
                attempt = $attempt
                artifactDir = $attemptDir
                succeeded = $false
                retryable = $true
                failureReason = 'measured_fallback_offer_route_not_active'
            }) | Out-Null
            continue
        }

        Write-RouteAcceptanceFakeTunaRun -ArtifactDir $attemptDir -Route $fallbackRoute -RouteMode $fallbackRouteMode
        Copy-RouteAcceptanceSelectedAttemptArtifacts -AttemptDir $attemptDir -ArtifactDir $ArtifactDir
        $attempts.Add([ordered]@{
            attempt = $attempt
            artifactDir = $attemptDir
            succeeded = $true
            retryable = $false
            failureReason = ''
        }) | Out-Null
        Write-RouteAcceptanceAttemptSummary -ArtifactDir $ArtifactDir -Attempts $attempts.ToArray() -SelectedAttempt $attempt
        return
    }

    Write-RouteAcceptanceAttemptSummary -ArtifactDir $ArtifactDir -Attempts $attempts.ToArray() -SelectedAttempt 0
}

function Invoke-RouteAcceptanceChildScript {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $oldRetainedAnalysisCompact = $env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT
    try {
        if ($script:RouteAcceptanceExecutionMode -eq 'live') {
            $env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT = '1'
        }

        & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($null -eq $oldRetainedAnalysisCompact) {
            Remove-Item Env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT = $oldRetainedAnalysisCompact
        }
    }

    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }
}

function Invoke-RouteAcceptanceChildScriptNoThrow {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $oldRetainedAnalysisCompact = $env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT
    try {
        if ($script:RouteAcceptanceExecutionMode -eq 'live') {
            $env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT = '1'
        }

        $ErrorActionPreference = 'Continue'
        $childOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1
        $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    }
    catch {
        $childOutput = @($_)
        $exitCode = if ($null -eq $LASTEXITCODE -or $LASTEXITCODE -eq 0) { 1 } else { [int]$LASTEXITCODE }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        if ($null -eq $oldRetainedAnalysisCompact) {
            Remove-Item Env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT = $oldRetainedAnalysisCompact
        }
    }

    foreach ($line in $childOutput) {
        Write-Host $line
    }

    return $exitCode
}

function Get-Phase5BridgeReadinessPreflightFailureClass {
    param(
        [string]$FailureReason = '',
        [string]$OutputText = ''
    )

    if ([string]::Equals($FailureReason, 'helper_bootstrap_window_ownership', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($FailureReason, 'helper_identity_window_ownership_mismatch', [System.StringComparison]::OrdinalIgnoreCase) -or
        $OutputText.IndexOf('Timed out waiting for current helper identity bootstrap UI before clipboard fallback', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('helper_identity_window_ownership_mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('helper_identity_clipboard_stale', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return 'preflight_helper_bootstrap_window_ownership'
    }

    $hasHelpeeInviteBootstrapFailure =
        [string]::Equals($FailureReason, 'helpee_invite_connection_failed_before_ready', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($FailureReason, 'helpee_invite_readiness_timeout', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($FailureReason, 'helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($FailureReason, 'helper_incoming_request_not_observed', [System.StringComparison]::OrdinalIgnoreCase) -or
        $OutputText.IndexOf('Helpee reached Connection failed before invite became ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('Helpee reached Connection failed before request flow became ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('helper_incoming_request_not_observed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('Timed out waiting for helper incoming request acceptance UI', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('Helpee hosting failed before invite readiness', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    $hasLowLevelBridgeFailure =
        $OutputText.IndexOf('HostByAddressAsync failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('RPC call failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('Connect failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('connect EACCES', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('BridgeStartFailure', [System.StringComparison]::OrdinalIgnoreCase) -ge 0

    if ($hasHelpeeInviteBootstrapFailure -and -not $hasLowLevelBridgeFailure) {
        return 'preflight_helpee_invite_bootstrap'
    }

    if ([string]::Equals($FailureReason, 'nkn_bridge_bootstrap_network_unready', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($FailureReason, 'nkn_bridge_bootstrap_not_ready', [System.StringComparison]::OrdinalIgnoreCase) -or
        $OutputText.IndexOf('nkn_bridge_bootstrap_network_unready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('nkn_bridge_bootstrap_not_ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return 'preflight_bridge_bootstrap'
    }

    if ([string]::Equals($FailureReason, 'helpee_invite_connection_failed_before_ready', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($FailureReason, 'helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($FailureReason, 'helper_incoming_request_not_observed', [System.StringComparison]::OrdinalIgnoreCase) -or
        $OutputText.IndexOf('Helpee reached Connection failed before invite became ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('Helpee reached Connection failed before request flow became ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('helper_incoming_request_not_observed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('Timed out waiting for helper incoming request acceptance UI', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $OutputText.IndexOf('Helpee hosting failed before invite readiness', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return 'preflight_helpee_invite_bootstrap'
    }

    return 'preflight_bridge_bootstrap'
}

function Get-Phase5BridgeReadinessPreflightFailureSubreason {
    param(
        [string]$FailureReason = '',
        [string]$OutputText = ''
    )

    $combined = @($FailureReason, $OutputText) -join "`n"
    foreach ($name in @('failure_subreason', 'subreason')) {
        $pattern = "(?i)(?:^|[;`r`n]\s*)" + [regex]::Escape($name) + "=([^;`r`n]+)"
        $match = [regex]::Match($combined, $pattern)
        if ($match.Success) {
            $value = $match.Groups[1].Value.Trim()
            if (-not [string]::IsNullOrWhiteSpace($value) -and $value -ne '(none)') {
                return $value
            }
        }
    }

    if ($combined.IndexOf('helpee_invite_host_before_bridge_ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return 'helpee_invite_host_before_bridge_ready'
    }

    if ($combined.IndexOf('helpee_invite_bootstrap_bridge_before_ready_retry', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return 'helpee_invite_host_before_bridge_ready'
    }

    if ($combined.IndexOf('HostByAddressAsync failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('Connect failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('RPC call failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('connect EACCES', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        if ($combined.IndexOf('role=helper', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'helper_bridge_host_ready_connect_failed'
        }

        return 'helpee_bridge_host_ready_connect_failed'
    }

    return ''
}

function Invoke-Phase5BridgeReadinessPreflight {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [string]$ResolvedExePath = '',
        [string]$ArtifactName = 'preflight-bridge-ready'
    )

    $artifactDir = Join-Path $RunRoot $ArtifactName
    New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

    $fakeFailOnceMarkerPath = Join-Path $RunRoot '.phase5-preflight-fake-fail-once'
    $fakeFailAlways = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL'
    $fakeFailOnce = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL_ONCE'
    if ($fakeFailAlways -or ($fakeFailOnce -and -not (Test-Path -LiteralPath $fakeFailOnceMarkerPath -PathType Leaf))) {
        if ($fakeFailOnce) {
            'failed' | Set-Content -LiteralPath $fakeFailOnceMarkerPath -Encoding UTF8
        }

        $reason = Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_REASON' -DefaultValue 'nkn_bridge_bootstrap_not_ready'
        $failureSubreason = Get-RouteAcceptanceEnvValue -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_SUBREASON' -DefaultValue ''
        if ([string]::IsNullOrWhiteSpace($failureSubreason)) {
            $failureSubreason = Get-Phase5BridgeReadinessPreflightFailureSubreason -FailureReason $reason -OutputText $reason
        }
        $failureClass = Get-Phase5BridgeReadinessPreflightFailureClass -FailureReason $reason -OutputText $reason
        $lines = @(
            'Phase 5 Bridge Readiness Preflight',
            'verdict=FAIL',
            ("failure_class={0}" -f $failureClass),
            ("failure_reason={0}" -f $reason),
            ("failure_subreason={0}" -f $failureSubreason),
            'scenario=NKN_DIRECT_CONNECT',
            ("artifact_dir={0}" -f $artifactDir),
            'exit_code=1'
        )
        $lines | Set-Content -LiteralPath (Join-Path $artifactDir 'phase5-bridge-readiness-preflight-summary.txt') -Encoding UTF8
        ([ordered]@{
            event = 'phase5_bridge_readiness_preflight'
            verdict = 'FAIL'
            failureClass = $failureClass
            failureReason = $reason
            failureSubreason = $failureSubreason
            scenario = 'NKN_DIRECT_CONNECT'
            artifactDir = $artifactDir
            exitCode = 1
        }) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifactDir 'phase5-bridge-readiness-preflight-summary.json') -Encoding UTF8

        return [pscustomobject]@{
            Passed = $false
            ArtifactDir = $artifactDir
            FailureClass = $failureClass
            FailureReason = $reason
            FailureSubreason = $failureSubreason
            ExitCode = 1
        }
    }

    if ($fakeFailOnce) {
        $lines = @(
            'Phase 5 Bridge Readiness Preflight',
            'verdict=PASS',
            'failure_class=none',
            'failure_reason=',
            'scenario=NKN_DIRECT_CONNECT',
            ("artifact_dir={0}" -f $artifactDir),
            'exit_code=0',
            'fake_mode=1'
        )
        $lines | Set-Content -LiteralPath (Join-Path $artifactDir 'phase5-bridge-readiness-preflight-summary.txt') -Encoding UTF8
        ([ordered]@{
            event = 'phase5_bridge_readiness_preflight'
            verdict = 'PASS'
            failureClass = 'none'
            failureReason = ''
            scenario = 'NKN_DIRECT_CONNECT'
            artifactDir = $artifactDir
            exitCode = 0
            fakeMode = 1
        }) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifactDir 'phase5-bridge-readiness-preflight-summary.json') -Encoding UTF8

        return [pscustomobject]@{
            Passed = $true
            ArtifactDir = $artifactDir
            FailureClass = 'none'
            FailureReason = ''
            ExitCode = 0
        }
    }

    $scriptPath = Join-Path $RepoRoot 'tools\GuiSmoke-Windows.ps1'
    $preflightTimeoutSeconds = [Math]::Min([Math]::Max(30, $ProgressTimeoutSeconds), [Math]::Min([Math]::Max(30, $TimeoutSeconds), 180))
    $stdoutPath = Join-Path $artifactDir 'gui-smoke-nkn-direct-connect-output.log'
    $oldTransport = $env:NLINK_TRANSPORT
    $oldScenarios = $env:NLINK_GUI_SMOKE_SCENARIOS
    $oldArtifactDir = $env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR
    $oldBridgePath = $env:NLINK_NKN_BRIDGE_PATH
    $oldNodePath = $env:NLINK_NKN_NODE_PATH
    try {
        $env:NLINK_TRANSPORT = 'NKN'
        $env:NLINK_GUI_SMOKE_SCENARIOS = 'NKN_DIRECT_CONNECT'
        $env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR = $artifactDir

        if (-not [string]::IsNullOrWhiteSpace($ResolvedExePath)) {
            $exeDirectory = [System.IO.Path]::GetDirectoryName((Resolve-Path -LiteralPath $ResolvedExePath).Path)
            $bundledBridge = Join-Path $exeDirectory 'bridge\win-x64\index.js'
            $bundledNode = Join-Path $exeDirectory 'bridge\win-x64\node.exe'
            if (-not (Test-Path -LiteralPath $bundledBridge) -or -not (Test-Path -LiteralPath $bundledNode)) {
                $candidateBridgeDirs = @(
                    (Join-Path $RepoRoot 'artifacts\bridge\win-x64'),
                    (Join-Path $RepoRoot 'artifacts\portable\nLink\win-x64\bridge\win-x64')
                )
                $overrideApplied = $false
                foreach ($candidateBridgeDir in $candidateBridgeDirs) {
                    $candidateBridge = Join-Path $candidateBridgeDir 'index.js'
                    $candidateNode = Join-Path $candidateBridgeDir 'node.exe'
                    if ((Test-Path -LiteralPath $candidateBridge -PathType Leaf) -and (Test-Path -LiteralPath $candidateNode -PathType Leaf)) {
                        $env:NLINK_NKN_BRIDGE_PATH = (Resolve-Path -LiteralPath $candidateBridge).Path
                        $env:NLINK_NKN_NODE_PATH = (Resolve-Path -LiteralPath $candidateNode).Path
                        Write-Host "[FileTransfer Phase5 Preflight] Using bridge override: $($env:NLINK_NKN_BRIDGE_PATH)" -ForegroundColor DarkGray
                        Write-Host "[FileTransfer Phase5 Preflight] Using node override: $($env:NLINK_NKN_NODE_PATH)" -ForegroundColor DarkGray
                        $overrideApplied = $true
                        break
                    }
                }

                if (-not $overrideApplied) {
                    Write-Host '[FileTransfer Phase5 Preflight] No bridge override found; preflight will use app packaged bridge paths.' -ForegroundColor Yellow
                }
            }
        }

        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath -ExePath $ResolvedExePath -TimeoutSeconds $preflightTimeoutSeconds 2>&1
        $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    }
    catch {
        $output = @($_)
        $exitCode = if ($null -eq $LASTEXITCODE -or $LASTEXITCODE -eq 0) { 1 } else { [int]$LASTEXITCODE }
    }
    finally {
        if ($null -eq $oldTransport) {
            Remove-Item Env:NLINK_TRANSPORT -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_TRANSPORT = $oldTransport
        }

        if ($null -eq $oldScenarios) {
            Remove-Item Env:NLINK_GUI_SMOKE_SCENARIOS -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_GUI_SMOKE_SCENARIOS = $oldScenarios
        }

        if ($null -eq $oldArtifactDir) {
            Remove-Item Env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR = $oldArtifactDir
        }

        if ($null -eq $oldBridgePath) {
            Remove-Item Env:NLINK_NKN_BRIDGE_PATH -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_NKN_BRIDGE_PATH = $oldBridgePath
        }

        if ($null -eq $oldNodePath) {
            Remove-Item Env:NLINK_NKN_NODE_PATH -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_NKN_NODE_PATH = $oldNodePath
        }
    }

    $outputLines = @($output | ForEach-Object { [string]$_ })
    $outputLines | Set-Content -LiteralPath $stdoutPath -Encoding UTF8
    foreach ($line in $outputLines) {
        Write-Host $line
    }

    $failureReason = ''
    $failureSubreason = ''
    if ($exitCode -ne 0) {
        $joined = $outputLines -join "`n"
        $failureSubreason = Get-Phase5BridgeReadinessPreflightFailureSubreason -OutputText $joined
        if ($joined.IndexOf('Timed out waiting for current helper identity bootstrap UI before clipboard fallback', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('helper_identity_window_ownership_mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('helper_identity_clipboard_stale', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $failureReason = 'helper_bootstrap_window_ownership'
        }
        elseif ($joined.IndexOf('Timed out waiting for helper address in app log', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('BridgeStartFailure', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('Please reinstall', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $failureReason = 'nkn_bridge_start_failure'
        }
        elseif (($joined.IndexOf('Helpee reached Connection failed before invite became ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('Helpee reached Connection failed before request flow became ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('Helpee hosting failed before invite readiness', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -and
            $joined.IndexOf('HostByAddressAsync failed', [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -and
            $joined.IndexOf('RPC call failed', [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -and
            $joined.IndexOf('Connect failed', [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -and
            $joined.IndexOf('connect EACCES', [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -and
            $joined.IndexOf('BridgeStartFailure', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $failureReason = if ($joined.IndexOf('helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                'helpee_request_flow_not_enabled_after_identity_paste'
            } else {
                'helpee_invite_connection_failed_before_ready'
            }
        }
        elseif ($joined.IndexOf('nkn_bridge_bootstrap_network_unready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('RPC call failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('Connect failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('connect EACCES', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('HostByAddressAsync failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('ready_emitted=0', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('bridge_bootstrap_not_ready=1', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $failureReason = 'nkn_bridge_bootstrap_network_unready'
        }
        elseif ($joined.IndexOf('Timed out waiting for helpee invite to become ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('Timed out waiting for helpee request flow to become ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('Timed out waiting for helpee connection entry mode', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $failureReason = 'helpee_invite_readiness_timeout'
        }
        elseif ($joined.IndexOf('Helpee reached Connection failed before invite became ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('Helpee reached Connection failed before request flow became ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $joined.IndexOf('Helpee hosting failed before invite readiness', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $failureReason = if ($joined.IndexOf('helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                'helpee_request_flow_not_enabled_after_identity_paste'
            } else {
                'helpee_invite_connection_failed_before_ready'
            }
        }
        else {
            $failureReason = 'nkn_direct_connect_preflight_failed'
        }
    }

    $verdict = if ($exitCode -eq 0) { 'PASS' } else { 'FAIL' }
    $failureClass = if ($exitCode -eq 0) { 'none' } else { Get-Phase5BridgeReadinessPreflightFailureClass -FailureReason $failureReason -OutputText ($outputLines -join "`n") }
    $lines = @(
        'Phase 5 Bridge Readiness Preflight',
        ("verdict={0}" -f $verdict),
        ("failure_class={0}" -f $failureClass),
        ("failure_reason={0}" -f ($(if ($exitCode -eq 0) { '' } else { $failureReason }))),
        ("failure_subreason={0}" -f ($(if ($exitCode -eq 0) { '' } else { $failureSubreason }))),
        'scenario=NKN_DIRECT_CONNECT',
        ("artifact_dir={0}" -f $artifactDir),
        ("exit_code={0}" -f $exitCode),
        ("timeout_seconds={0}" -f $preflightTimeoutSeconds),
        ("output_log={0}" -f $stdoutPath)
    )
    $lines | Set-Content -LiteralPath (Join-Path $artifactDir 'phase5-bridge-readiness-preflight-summary.txt') -Encoding UTF8
    ([ordered]@{
        event = 'phase5_bridge_readiness_preflight'
        verdict = $verdict
        failureClass = $failureClass
        failureReason = $failureReason
        failureSubreason = $failureSubreason
        scenario = 'NKN_DIRECT_CONNECT'
        artifactDir = $artifactDir
        exitCode = $exitCode
        timeoutSeconds = $preflightTimeoutSeconds
        outputLog = $stdoutPath
    }) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifactDir 'phase5-bridge-readiness-preflight-summary.json') -Encoding UTF8

    return [pscustomobject]@{
        Passed = ($exitCode -eq 0)
        ArtifactDir = $artifactDir
        FailureClass = $failureClass
        FailureReason = $failureReason
        FailureSubreason = $failureSubreason
        ExitCode = $exitCode
    }
}

function Assert-Phase5RouteAcceptanceExecutableFresh {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)][string]$ResolvedExePath
    )

    $exeItem = Get-Item -LiteralPath $ResolvedExePath -ErrorAction Stop
    $freshnessInputs = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    foreach ($root in @(
            (Join-Path $RepoRoot 'src'),
            (Join-Path $RepoRoot 'tools\FileTransferSoak'))) {
        if (Test-Path -LiteralPath $root -PathType Container) {
            foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue) {
                if ($file.FullName -match '[\\/](bin|obj)[\\/]') {
                    continue
                }

                $freshnessInputs.Add($file) | Out-Null
            }
        }
    }

    foreach ($path in @(
            (Join-Path $RepoRoot 'tools\Run-FileTransferRouteAcceptance.ps1'),
            (Join-Path $RepoRoot 'VERSION'))) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $freshnessInputs.Add((Get-Item -LiteralPath $path)) | Out-Null
        }
    }

    $headUnixRaw = (& git -C $RepoRoot log -1 --format=%ct 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace([string]$headUnixRaw)) {
        throw 'Phase 5 executable freshness preflight could not resolve current Git HEAD commit time.'
    }

    $headUnix = 0L
    if (-not [long]::TryParse(([string]$headUnixRaw).Trim(), [ref]$headUnix)) {
        throw ("Phase 5 executable freshness preflight could not parse current Git HEAD commit time: {0}" -f $headUnixRaw)
    }

    $headUtc = [DateTimeOffset]::FromUnixTimeSeconds($headUnix)
    $latestInput = @($freshnessInputs | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
    $latestInputUtc = if ($latestInput.Count -gt 0) {
        [DateTimeOffset]$latestInput[0].LastWriteTimeUtc
    }
    else {
        $headUtc
    }
    $latestInputPath = if ($latestInput.Count -gt 0) {
        $latestInput[0].FullName
    }
    else {
        '(none)'
    }
    $baselineUtc = if ($latestInputUtc -gt $headUtc) { $latestInputUtc } else { $headUtc }
    $exeUtc = [DateTimeOffset]$exeItem.LastWriteTimeUtc
    $passed = $exeUtc -ge $baselineUtc
    $reason = if ($passed) {
        'portable_executable_fresh'
    }
    elseif ($latestInputUtc -gt $headUtc -and $exeUtc -lt $latestInputUtc) {
        'portable_executable_older_than_source_inputs'
    }
    else {
        'portable_executable_older_than_git_head'
    }
    $lines = @(
        'Phase 5 Portable Executable Freshness Preflight',
        ("passed={0}" -f ($(if ($passed) { '1' } else { '0' }))),
        ("reason={0}" -f $reason),
        ("exe_path={0}" -f $exeItem.FullName),
        ("exe_last_write_utc={0:o}" -f $exeUtc),
        ("git_head_utc={0:o}" -f $headUtc),
        ("latest_source_input_utc={0:o}" -f $latestInputUtc),
        ("latest_source_input_path={0}" -f $latestInputPath),
        ("freshness_baseline_utc={0:o}" -f $baselineUtc)
    )
    $lines | Set-Content -LiteralPath (Join-Path $RunRoot 'phase5-portable-freshness-preflight.txt') -Encoding UTF8
    @{
        event = 'phase5_portable_freshness_preflight'
        passed = $passed
        reason = $reason
        exePath = $exeItem.FullName
        exeLastWriteUtc = $exeUtc.ToString('o')
        gitHeadUtc = $headUtc.ToString('o')
        latestSourceInputUtc = $latestInputUtc.ToString('o')
        latestSourceInputPath = $latestInputPath
        freshnessBaselineUtc = $baselineUtc.ToString('o')
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $RunRoot 'phase5-portable-freshness-preflight.json') -Encoding UTF8

    if (-not $passed) {
        throw ("Phase 5 portable executable is stale: exe_last_write_utc={0:o}; freshness_baseline_utc={1:o}; reason={2}; latest_source_input={3}; rebuild/publish the portable app before rerunning GUI acceptance." -f $exeUtc, $baselineUtc, $reason, $latestInputPath)
    }
}

function Test-Phase5BridgeReadinessPreflightRetryableFailure {
    param(
        [Parameter(Mandatory = $true)]$PreflightResult
    )

    if ($null -eq $PreflightResult -or $PreflightResult.Passed) {
        return $false
    }

    $reason = [string]$PreflightResult.FailureReason
    return $reason -eq 'helpee_invite_readiness_timeout' -or
        $reason -eq 'helpee_invite_connection_failed_before_ready' -or
        $reason -eq 'helpee_request_flow_not_enabled_after_identity_paste' -or
        $reason -eq 'nkn_bridge_start_failure' -or
        $reason -eq 'nkn_bridge_bootstrap_not_ready' -or
        $reason -eq 'nkn_bridge_bootstrap_network_unready' -or
        $reason -eq 'helper_bootstrap_window_ownership' -or
        [string]$PreflightResult.FailureClass -eq 'preflight_helpee_invite_bootstrap' -or
        [string]$PreflightResult.FailureClass -eq 'preflight_helper_bootstrap_window_ownership'
}

function Test-Phase5BridgeReadinessPreflightQuarantinableFailure {
    param(
        [Parameter(Mandatory = $true)]$PreflightResult
    )

    if ($null -eq $PreflightResult -or $PreflightResult.Passed) {
        return $false
    }

    $reason = [string]$PreflightResult.FailureReason
    return $reason -eq 'helpee_invite_readiness_timeout' -or
        $reason -eq 'helpee_invite_connection_failed_before_ready' -or
        $reason -eq 'helpee_request_flow_not_enabled_after_identity_paste' -or
        [string]$PreflightResult.FailureClass -eq 'preflight_helpee_invite_bootstrap'
}

function Write-Phase5BridgeReadinessPreflightQuarantineSummary {
    param(
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)]$PreflightResult,
        [string]$Scope = 'filtered_scenario'
    )

    $lines = @(
        'Phase 5 Bridge Readiness Preflight Quarantine',
        'verdict=QUARANTINED',
        ("scope={0}" -f $Scope),
        ("failure_class={0}" -f $PreflightResult.FailureClass),
        ("failure_reason={0}" -f $PreflightResult.FailureReason),
        ("failure_subreason={0}" -f ($(if ($PreflightResult.PSObject.Properties.Name -contains 'FailureSubreason') { [string]$PreflightResult.FailureSubreason } else { '' }))),
        ("artifact_dir={0}" -f $PreflightResult.ArtifactDir),
        ("exit_code={0}" -f $PreflightResult.ExitCode),
        'classification=setup_preflight_only_not_route_evidence'
    )
    $lines | Set-Content -LiteralPath (Join-Path $RunRoot 'phase5-bridge-readiness-preflight-quarantine.txt') -Encoding UTF8

    ([ordered]@{
        event = 'phase5_bridge_readiness_preflight_quarantine'
        verdict = 'QUARANTINED'
        scope = $Scope
        failureClass = $PreflightResult.FailureClass
        failureReason = $PreflightResult.FailureReason
        failureSubreason = if ($PreflightResult.PSObject.Properties.Name -contains 'FailureSubreason') { [string]$PreflightResult.FailureSubreason } else { '' }
        artifactDir = $PreflightResult.ArtifactDir
        exitCode = $PreflightResult.ExitCode
        classification = 'setup_preflight_only_not_route_evidence'
    }) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $RunRoot 'phase5-bridge-readiness-preflight-quarantine.json') -Encoding UTF8
}

function Write-Phase5BridgeReadinessPreflightAttemptSummary {
    param(
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)]$PreflightResult
    )

    $attemptCount = if ($PreflightResult.PSObject.Properties.Name -contains 'AttemptCount') { [int]$PreflightResult.AttemptCount } else { 1 }
    $retryUsed = if ($PreflightResult.PSObject.Properties.Name -contains 'RetryUsed' -and $PreflightResult.RetryUsed) { 1 } else { 0 }
    $selectedAttempt = if ($PreflightResult.PSObject.Properties.Name -contains 'SelectedAttempt') { [int]$PreflightResult.SelectedAttempt } else { 1 }
    $firstFailureReason = if ($PreflightResult.PSObject.Properties.Name -contains 'FirstFailureReason') { [string]$PreflightResult.FirstFailureReason } else { '' }
    $firstFailureSubreason = if ($PreflightResult.PSObject.Properties.Name -contains 'FirstFailureSubreason') { [string]$PreflightResult.FirstFailureSubreason } else { '' }
    $firstArtifactDir = if ($PreflightResult.PSObject.Properties.Name -contains 'FirstArtifactDir') { [string]$PreflightResult.FirstArtifactDir } else { '' }
    $finalFailureSubreason = if ($PreflightResult.PSObject.Properties.Name -contains 'FailureSubreason') { [string]$PreflightResult.FailureSubreason } else { '' }
    $verdict = if ($PreflightResult.Passed) { 'PASS' } else { 'FAIL' }

    $lines = @(
        'Phase 5 Bridge Readiness Preflight Attempts',
        ("verdict={0}" -f $verdict),
        ("attempt_count={0}" -f $attemptCount),
        ("retry_used={0}" -f $retryUsed),
        ("selected_attempt={0}" -f $selectedAttempt),
        ("final_failure_class={0}" -f $PreflightResult.FailureClass),
        ("final_failure_reason={0}" -f $PreflightResult.FailureReason),
        ("final_failure_subreason={0}" -f $finalFailureSubreason),
        ("final_artifact_dir={0}" -f $PreflightResult.ArtifactDir),
        ("final_exit_code={0}" -f $PreflightResult.ExitCode),
        ("first_failure_reason={0}" -f $firstFailureReason),
        ("first_failure_subreason={0}" -f $firstFailureSubreason),
        ("first_artifact_dir={0}" -f $firstArtifactDir)
    )
    $lines | Set-Content -LiteralPath (Join-Path $RunRoot 'phase5-bridge-readiness-preflight-attempts.txt') -Encoding UTF8

    ([ordered]@{
        event = 'phase5_bridge_readiness_preflight_attempts'
        verdict = $verdict
        attemptCount = $attemptCount
        retryUsed = $retryUsed
        selectedAttempt = $selectedAttempt
        finalFailureClass = $PreflightResult.FailureClass
        finalFailureReason = $PreflightResult.FailureReason
        finalFailureSubreason = $finalFailureSubreason
        finalArtifactDir = $PreflightResult.ArtifactDir
        finalExitCode = $PreflightResult.ExitCode
        firstFailureReason = $firstFailureReason
        firstFailureSubreason = $firstFailureSubreason
        firstArtifactDir = $firstArtifactDir
    }) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $RunRoot 'phase5-bridge-readiness-preflight-attempts.json') -Encoding UTF8
}

function Invoke-Phase5BridgeReadinessPreflightWithRetry {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [string]$ResolvedExePath = ''
    )

    $first = Invoke-Phase5BridgeReadinessPreflight -RepoRoot $RepoRoot -RunRoot $RunRoot -ResolvedExePath $ResolvedExePath
    if ($first.Passed -or -not (Test-Phase5BridgeReadinessPreflightRetryableFailure -PreflightResult $first)) {
        $firstSubreason = if ($first.PSObject.Properties.Name -contains 'FailureSubreason') { [string]$first.FailureSubreason } else { '' }
        $result = [pscustomobject]@{
            Passed = $first.Passed
            ArtifactDir = $first.ArtifactDir
            FailureClass = $first.FailureClass
            FailureReason = $first.FailureReason
            FailureSubreason = $firstSubreason
            ExitCode = $first.ExitCode
            AttemptCount = 1
            RetryUsed = $false
            SelectedAttempt = 1
            FirstFailureReason = ''
            FirstFailureSubreason = ''
            FirstArtifactDir = ''
        }
        Write-Phase5BridgeReadinessPreflightAttemptSummary -RunRoot $RunRoot -PreflightResult $result
        return $result
    }

    $firstArtifactDir = Join-Path $RunRoot 'preflight-bridge-ready-attempt-1'
    if (Test-Path -LiteralPath $firstArtifactDir) {
        throw "Phase 5 preflight retry artifact directory already exists: $firstArtifactDir"
    }

    if (Test-Path -LiteralPath $first.ArtifactDir) {
        Move-Item -LiteralPath $first.ArtifactDir -Destination $firstArtifactDir
    }

    $firstSubreason = if ($first.PSObject.Properties.Name -contains 'FailureSubreason') { [string]$first.FailureSubreason } else { '' }
    $usesFinalSetupRetry =
        [string]::Equals($firstSubreason, 'helpee_invite_host_before_bridge_ready', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($firstSubreason, 'helpee_bridge_host_ready_connect_failed', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($firstSubreason, 'helper_bridge_host_ready_connect_failed', [System.StringComparison]::OrdinalIgnoreCase)
    $maxAttempts = if ($usesFinalSetupRetry) { 3 } else { 2 }

    Write-Host ("[FileTransfer Phase5 Preflight] setup/bootstrap preflight failed with {0}; retrying from a clean preflight artifact directory (attempt 2/{1})." -f $first.FailureReason, $maxAttempts) -ForegroundColor Yellow
    $cleanupArtifactDir = Join-Path $RunRoot 'preflight-cleanup-after-attempt-1'
    New-Item -ItemType Directory -Force -Path $cleanupArtifactDir | Out-Null
    $cleanupBefore = @(Get-RouteAcceptanceAppProcessesForExePath -ResolvedExePath $ResolvedExePath)
    if ($cleanupBefore.Count -gt 0) {
        Assert-RouteAcceptanceCleanAppProcessState -ResolvedExePath $ResolvedExePath -ArtifactDir $cleanupArtifactDir -Reason 'preflight_after_attempt_1'
    }
    else {
        Write-RouteAcceptanceProcessCleanupArtifacts -ArtifactDir $cleanupArtifactDir -Reason 'preflight_after_attempt_1' -Before @() -After @()
    }

    Start-Sleep -Seconds 2

    $second = Invoke-Phase5BridgeReadinessPreflight -RepoRoot $RepoRoot -RunRoot $RunRoot -ResolvedExePath $ResolvedExePath
    $secondSubreason = if ($second.PSObject.Properties.Name -contains 'FailureSubreason') { [string]$second.FailureSubreason } else { '' }
    $result = [pscustomobject]@{
        Passed = $second.Passed
        ArtifactDir = $second.ArtifactDir
        FailureClass = $second.FailureClass
        FailureReason = $second.FailureReason
        FailureSubreason = $secondSubreason
        ExitCode = $second.ExitCode
        AttemptCount = 2
        RetryUsed = $true
        SelectedAttempt = 2
        FirstFailureReason = $first.FailureReason
        FirstFailureSubreason = $firstSubreason
        FirstArtifactDir = $firstArtifactDir
    }

    if (-not $second.Passed -and $maxAttempts -gt 2 -and (Test-Phase5BridgeReadinessPreflightRetryableFailure -PreflightResult $second)) {
        $secondArtifactDir = Join-Path $RunRoot 'preflight-bridge-ready-attempt-2'
        if (Test-Path -LiteralPath $secondArtifactDir) {
            throw "Phase 5 preflight retry artifact directory already exists: $secondArtifactDir"
        }

        if (Test-Path -LiteralPath $second.ArtifactDir) {
            Move-Item -LiteralPath $second.ArtifactDir -Destination $secondArtifactDir
        }

        Write-Host ("[FileTransfer Phase5 Preflight] setup/bootstrap preflight failed again with {0}; retrying final clean preflight attempt (attempt 3/{1})." -f $second.FailureReason, $maxAttempts) -ForegroundColor Yellow
        $cleanupArtifactDir = Join-Path $RunRoot 'preflight-cleanup-after-attempt-2'
        New-Item -ItemType Directory -Force -Path $cleanupArtifactDir | Out-Null
        $cleanupBefore = @(Get-RouteAcceptanceAppProcessesForExePath -ResolvedExePath $ResolvedExePath)
        if ($cleanupBefore.Count -gt 0) {
            Assert-RouteAcceptanceCleanAppProcessState -ResolvedExePath $ResolvedExePath -ArtifactDir $cleanupArtifactDir -Reason 'preflight_after_attempt_2'
        }
        else {
            Write-RouteAcceptanceProcessCleanupArtifacts -ArtifactDir $cleanupArtifactDir -Reason 'preflight_after_attempt_2' -Before @() -After @()
        }

        Start-Sleep -Seconds 2

        $third = Invoke-Phase5BridgeReadinessPreflight -RepoRoot $RepoRoot -RunRoot $RunRoot -ResolvedExePath $ResolvedExePath
        $thirdSubreason = if ($third.PSObject.Properties.Name -contains 'FailureSubreason') { [string]$third.FailureSubreason } else { '' }
        $result = [pscustomobject]@{
            Passed = $third.Passed
            ArtifactDir = $third.ArtifactDir
            FailureClass = $third.FailureClass
            FailureReason = $third.FailureReason
            FailureSubreason = $thirdSubreason
            ExitCode = $third.ExitCode
            AttemptCount = 3
            RetryUsed = $true
            SelectedAttempt = 3
            FirstFailureReason = $first.FailureReason
            FirstFailureSubreason = $firstSubreason
            FirstArtifactDir = $firstArtifactDir
        }
    }

    Write-Phase5BridgeReadinessPreflightAttemptSummary -RunRoot $RunRoot -PreflightResult $result
    return $result
}

function Test-Phase5ScenarioRequiresIsolation {
    param([Parameter(Mandatory = $true)]$Scenario)

    if ([string]$Scenario.Kind -ne 'tuna') {
        return $false
    }

    return @(
        'active-tuna-v4-64mb',
        'live-switch-off-helpee-64mb',
        'live-switch-off-helper-64mb',
        'regular-v4-live-activation-off-on-off-256mb',
        'second-transfer-after-reactivation'
    ) -contains [string]$Scenario.Name
}

function Write-Phase5ScenarioIsolationSummary {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$ScenarioName,
        [Parameter(Mandatory = $true)][int]$AttemptIndex,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [string]$FailureReason = '',
        [int]$ExitCode = 0,
        [string]$SourceArtifactDir = '',
        [bool]$Quarantined = $false
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
    $verdict = if ($Passed) { if ($Quarantined) { 'QUARANTINED' } else { 'PASS' } } else { 'FAIL' }
    $cleanWindow = if ($Passed) { 1 } else { 0 }
    $lines = @(
        'Phase 5 Scenario Isolation Preflight',
        ("verdict={0}" -f $verdict),
        ("scenario={0}" -f $ScenarioName),
        ("attempt_index={0}" -f $AttemptIndex),
        ("scenario_runtime_clean_window_passed={0}" -f $cleanWindow),
        ("quarantined={0}" -f ($(if ($Quarantined) { 1 } else { 0 }))),
        ("failure_reason={0}" -f $FailureReason),
        ("source_artifact_dir={0}" -f $SourceArtifactDir),
        ("exit_code={0}" -f $ExitCode)
    )
    $lines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'phase5-scenario-isolation-summary.txt') -Encoding UTF8
    ([ordered]@{
        event = 'phase5_scenario_isolation_preflight'
        verdict = $verdict
        scenario = $ScenarioName
        attemptIndex = $AttemptIndex
        scenarioRuntimeCleanWindowPassed = [bool]$Passed
        quarantined = [bool]$Quarantined
        failureReason = $FailureReason
        sourceArtifactDir = $SourceArtifactDir
        exitCode = $ExitCode
    }) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'phase5-scenario-isolation-summary.json') -Encoding UTF8
}

function Invoke-Phase5ScenarioIsolationPreflight {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)][bool]$FakeMode,
        [int]$AttemptIndex = 1,
        [string]$ResolvedExePath = ''
    )

    $scenarioName = [string]$Scenario.Name
    $artifactName = "scenario-isolation-{0}-attempt-{1}" -f $scenarioName, $AttemptIndex
    $artifactDir = Join-Path $RunRoot $artifactName
    New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

    if ($FakeMode) {
        $globalFail = Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_SCENARIO_ISOLATION_FAIL'
        $scenarioFail = if ($AttemptIndex -gt 1) {
            Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'RERUN_SCENARIO_ISOLATION_FAIL'
        }
        else {
            Test-RouteAcceptanceScenarioEnvEnabled -ScenarioName $scenarioName -Suffix 'SCENARIO_ISOLATION_FAIL'
        }

        if ($globalFail -or $scenarioFail) {
            $reason = if ($AttemptIndex -gt 1) {
                Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix 'RERUN_SCENARIO_ISOLATION_REASON' -DefaultValue (Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix 'SCENARIO_ISOLATION_REASON' -DefaultValue 'scenario_runtime_clean_window_failed')
            }
            else {
                Get-RouteAcceptanceScenarioEnvValue -ScenarioName $scenarioName -Suffix 'SCENARIO_ISOLATION_REASON' -DefaultValue 'scenario_runtime_clean_window_failed'
            }
            Write-Phase5ScenarioIsolationSummary -ArtifactDir $artifactDir -ScenarioName $scenarioName -AttemptIndex $AttemptIndex -Passed:$false -FailureReason $reason -ExitCode 1
            return [pscustomobject]@{
                Passed = $false
                ArtifactDir = $artifactDir
                FailureReason = $reason
                ExitCode = 1
            }
        }

        Write-Phase5ScenarioIsolationSummary -ArtifactDir $artifactDir -ScenarioName $scenarioName -AttemptIndex $AttemptIndex -Passed:$true
        return [pscustomobject]@{
            Passed = $true
            ArtifactDir = $artifactDir
            FailureReason = ''
            ExitCode = 0
        }
    }

    $preflight = Invoke-Phase5BridgeReadinessPreflight -RepoRoot $RepoRoot -RunRoot $RunRoot -ResolvedExePath $ResolvedExePath -ArtifactName $artifactName
    $sourceDir = [string]$preflight.ArtifactDir
    $passed = [bool]$preflight.Passed
    $reason = [string]$preflight.FailureReason
    if (-not $passed -and [string]::IsNullOrWhiteSpace($reason)) {
        $reason = 'scenario_runtime_clean_window_failed'
    }
    $quarantined = $false
    if (-not $passed -and (Test-Phase5BridgeReadinessPreflightQuarantinableFailure -PreflightResult $preflight)) {
        $passed = $true
        $quarantined = $true
    }

    Write-Phase5ScenarioIsolationSummary -ArtifactDir $artifactDir -ScenarioName $scenarioName -AttemptIndex $AttemptIndex -Passed:$passed -FailureReason $reason -ExitCode ([int]$preflight.ExitCode) -SourceArtifactDir $sourceDir -Quarantined:$quarantined
    return [pscustomobject]@{
        Passed = $passed
        ArtifactDir = $artifactDir
        FailureReason = $reason
        ExitCode = [int]$preflight.ExitCode
        Quarantined = $quarantined
    }
}

function Set-RouteAcceptanceScenarioIsolationResult {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)]$IsolationResult
    )

    $Result.scenarioIsolationPreflightCount = [int]$Result.scenarioIsolationPreflightCount + 1
    if ([bool]$IsolationResult.Passed) {
        $Result.scenarioIsolationCleanWindowPassed = [int]$Result.scenarioIsolationCleanWindowPassed + 1
    }
    elseif ([string]::IsNullOrWhiteSpace([string]$Result.scenarioIsolationFailureReason)) {
        $Result.scenarioIsolationFailureReason = [string]$IsolationResult.FailureReason
    }

    $dirs = New-Object System.Collections.Generic.List[string]
    foreach ($dir in @($Result.scenarioIsolationArtifactDirs)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$dir)) {
            $dirs.Add([string]$dir) | Out-Null
        }
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$IsolationResult.ArtifactDir)) {
        $dirs.Add([string]$IsolationResult.ArtifactDir) | Out-Null
    }
    $Result.scenarioIsolationArtifactDirs = @($dirs.ToArray())
}

function Write-Phase5PreflightFailureSummaryFiles {
    param(
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)][string]$BaselinePath,
        [Parameter(Mandatory = $true)]$PreflightResult
    )

    $evidence = Get-RouteAcceptanceEvidenceSummaryState
    $failureSubreason = if ($PreflightResult.PSObject.Properties.Name -contains 'FailureSubreason') { [string]$PreflightResult.FailureSubreason } else { '' }
    $failureLine = if ([string]::IsNullOrWhiteSpace($failureSubreason)) {
        "phase5-preflight-bridge-ready: {0}" -f $PreflightResult.FailureReason
    }
    else {
        "phase5-preflight-bridge-ready: {0}; subreason={1}" -f $PreflightResult.FailureReason, $failureSubreason
    }
    $attemptCount = if ($PreflightResult.PSObject.Properties.Name -contains 'AttemptCount') { [int]$PreflightResult.AttemptCount } else { 1 }
    $retryUsed = if ($PreflightResult.PSObject.Properties.Name -contains 'RetryUsed' -and $PreflightResult.RetryUsed) { 1 } else { 0 }
    $firstFailureReason = if ($PreflightResult.PSObject.Properties.Name -contains 'FirstFailureReason') { [string]$PreflightResult.FirstFailureReason } else { '' }
    $firstFailureSubreason = if ($PreflightResult.PSObject.Properties.Name -contains 'FirstFailureSubreason') { [string]$PreflightResult.FirstFailureSubreason } else { '' }
    $firstArtifactDir = if ($PreflightResult.PSObject.Properties.Name -contains 'FirstArtifactDir') { [string]$PreflightResult.FirstArtifactDir } else { '' }
    $preflightProofReasons = @(
        @($evidence.CurrentRuntimeProofReasons) + @('cursor_evidence_missing') |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
            ForEach-Object { [string]$_ } |
            Select-Object -Unique
    )
    $textLines = @(
        'Phase 5 File Transfer Analyzer/GUI Acceptance',
        'verdict=FAIL',
        'correctness_verdict=FAIL',
        'performance_verdict=PASS',
        'acceptance_phase=phase5',
        ("artifact_root={0}" -f $RunRoot),
        ("manifest_present={0}" -f ($(if ([bool]$evidence.ManifestPresent) { 1 } else { 0 }))),
        ("manifest_path={0}" -f ($(if ([string]::IsNullOrWhiteSpace([string]$evidence.ManifestPath)) { '(none)' } else { [string]$evidence.ManifestPath }))),
        ("manifest_sha256={0}" -f ($(if ([string]::IsNullOrWhiteSpace([string]$evidence.ManifestSha256)) { '(none)' } else { [string]$evidence.ManifestSha256 }))),
        ("manifest_validation_verdict={0}" -f [string]$evidence.ManifestValidationVerdict),
        ("provenance_verdict={0}" -f [string]$evidence.ProvenanceVerdict),
        ("provenance_failure_reasons={0}" -f (Join-RouteAcceptanceTokenList -Values $evidence.ProvenanceFailureReasons)),
        'cursor_integrity_verdict=missing',
        'writer_evidence_version=0',
        'writer_integrity_verdict=missing',
        'writer_gap_count=0',
        'writer_duplicate_count=0',
        'writer_identity_mismatch_count=0',
        'writer_write_failure_count=0',
        'writer_deferred_batch_count=0',
        'writer_recovered_batch_count=0',
        'writer_unresolved_deferred_batch_count=0',
        'writer_terminal_failure_count=0',
        'writer_recovery_mismatch_count=0',
        'writer_chronology_inversion_count=0',
        'writer_chronology_collision_count=0',
        'writer_drain_missing_count=0',
        'writer_recovery_verdict=missing',
        'writer_chronology_verdict=missing',
        'current_runtime_proof=0',
        ("current_runtime_proof_reasons={0}" -f (Join-RouteAcceptanceTokenList -Values $preflightProofReasons)),
        'transfer_evidence_status=not_started',
        'runtime_correctness_verdict=incomplete_proof',
        'fallback_tail_bridge_recovery_generation_pairing_verdict=none',
        'fallback_tail_bridge_recovery_generation_mismatch_raw_count=0',
        'fallback_tail_bridge_recovery_generation_mismatch_distinct_count=0',
        'fallback_tail_bridge_recovery_generation_mismatch_recovered_count=0',
        'fallback_tail_bridge_recovery_generation_mismatch_unresolved_count=0',
        'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict=none',
        'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count=0',
        'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count=0',
        'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count=0',
        'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count=0',
        'runtime_unlock_regular_v4_route_commit_send_drain_verdict=none',
        'runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count=0',
        'runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count=0',
        'runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count=0',
        'runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count=0',
        ("baseline_manifest={0}" -f $BaselinePath),
        'correctness_gate_policy=strict_no_exceptions',
        'preflight_verdict=FAIL',
        ("preflight_failure_class={0}" -f $PreflightResult.FailureClass),
        ("preflight_failure_reason={0}" -f $PreflightResult.FailureReason),
        ("preflight_failure_subreason={0}" -f $failureSubreason),
        ("preflight_artifact_dir={0}" -f $PreflightResult.ArtifactDir),
        ("preflight_exit_code={0}" -f $PreflightResult.ExitCode),
        ("preflight_attempt_count={0}" -f $attemptCount),
        ("preflight_retry_used={0}" -f $retryUsed),
        ("preflight_first_failure_reason={0}" -f $firstFailureReason),
        ("preflight_first_failure_subreason={0}" -f $firstFailureSubreason),
        ("preflight_first_artifact_dir={0}" -f $firstArtifactDir),
        'run_count=0',
        'failure_count=1',
        'correctness_failure_count=1',
        'performance_failure_count=0',
        '',
        'failures:',
        $failureLine
    )

    $textLines | Set-Content -LiteralPath (Join-Path $RunRoot 'phase5-analyzer-gui-acceptance-summary.txt') -Encoding UTF8
    $textLines | Set-Content -LiteralPath (Join-Path $RunRoot 'route-acceptance-summary.txt') -Encoding UTF8

    $varianceNoteLines = @(
        '# Phase 5 Network Variance Note',
        '',
        ("artifact_root={0}" -f $RunRoot),
        ("baseline_manifest={0}" -f $BaselinePath),
        'verdict=FAIL',
        'correctness_verdict=FAIL',
        'performance_verdict=PASS',
        '',
        'Phase 5 did not start the file-transfer matrix because the NKN direct-connect bridge readiness preflight failed. This is classified as setup/bootstrap evidence, not route/protocol, SHA, fallback, or throughput evidence.',
        '',
        'Correctness/evidence failures:',
        ("- {0}" -f $failureLine)
    )
    $varianceNoteLines | Set-Content -LiteralPath (Join-Path $RunRoot 'phase5-network-variance-note.md') -Encoding UTF8

    $jsonSummary = [ordered]@{
        event = 'phase5_filetransfer_analyzer_gui_acceptance_summary'
        verdict = 'FAIL'
        correctnessVerdict = 'FAIL'
        performanceVerdict = 'PASS'
        acceptancePhase = 'phase5'
        artifactRoot = $RunRoot
        manifestPresent = [bool]$evidence.ManifestPresent
        manifestPath = [string]$evidence.ManifestPath
        manifestSha256 = [string]$evidence.ManifestSha256
        manifestValidationVerdict = [string]$evidence.ManifestValidationVerdict
        provenanceVerdict = [string]$evidence.ProvenanceVerdict
        provenanceFailureReasons = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $evidence.ProvenanceFailureReasons -Limit 32)
        cursorIntegrityVerdict = 'missing'
        writerEvidenceVersion = 0
        writerIntegrityVerdict = 'missing'
        writerGapCount = 0
        writerDuplicateCount = 0
        writerIdentityMismatchCount = 0
        writerWriteFailureCount = 0
        writerDeferredBatchCount = 0
        writerRecoveredBatchCount = 0
        writerUnresolvedDeferredBatchCount = 0
        writerTerminalFailureCount = 0
        writerRecoveryMismatchCount = 0
        writerChronologyInversionCount = 0
        writerChronologyCollisionCount = 0
        writerDrainMissingCount = 0
        writerRecoveryVerdict = 'missing'
        writerChronologyVerdict = 'missing'
        currentRuntimeProof = $false
        currentRuntimeProofReasons = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $preflightProofReasons -Limit 32)
        transferEvidenceStatus = 'not_started'
        runtimeCorrectnessVerdict = 'incomplete_proof'
        fallbackTailBridgeRecoveryGenerationPairingVerdict = 'none'
        fallbackTailBridgeRecoveryGenerationMismatchRawCount = 0
        fallbackTailBridgeRecoveryGenerationMismatchDistinctCount = 0
        fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount = 0
        fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount = 0
        fallbackTailTransportEpochReauthorizationVerdict = 'none'
        fallbackTailTransportEpochReauthorizationMissingRawCount = 0
        fallbackTailTransportEpochReauthorizationMissingDistinctCount = 0
        fallbackTailTransportEpochReauthorizationMissingRecoveredCount = 0
        fallbackTailTransportEpochReauthorizationMissingUnresolvedCount = 0
        runtimeUnlockRegularV4RouteCommitSendDrainVerdict = 'none'
        runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount = 0
        runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount = 0
        runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount = 0
        runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount = 0
        fileTransferReceiveRecoveryOperationVerdict = 'none'
        fileTransferReceiveRecoveryOperationRawCount = 0
        fileTransferReceiveRecoveryOperationDistinctCount = 0
        fileTransferReceiveRecoveryOperationRecoveredCount = 0
        fileTransferReceiveRecoveryOperationUnresolvedCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount = 0
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorVerdict = 'none'
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount = 0
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount = 0
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount = 0
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount = 0
        fileTransferReceiveRecoveryCompletedProvenSuccessorVerdict = 'none'
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRawCount = 0
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount = 0
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRecoveredCount = 0
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount = 0
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount = 0
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount = 0
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationRecoveredCount = 0
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount = 0
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationVerdict = 'none'
        regularV4FrontierRepairStalledRawCount = 0
        regularV4FrontierRepairStalledDistinctCount = 0
        regularV4FrontierRepairStalledRecoveredCount = 0
        regularV4FrontierRepairStalledUnresolvedCount = 0
        regularV4FrontierRepairStalledVerdict = 'none'
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRawCount = 0
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount = 0
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRecoveredCount = 0
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount = 0
        runtimeUnlockInitialOfferRetryLifecycleHandoffVerdict = 'none'
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRawCount = 0
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount = 0
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRecoveredCount = 0
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount = 0
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedVerdict = 'none'
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryVerdict = 'none'
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredVerdict = 'none'
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRawCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRecoveredCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount = 0
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredVerdict = 'none'
        baselineManifest = $BaselinePath
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        preflightVerdict = 'FAIL'
        preflightFailureClass = $PreflightResult.FailureClass
        preflightFailureReason = $PreflightResult.FailureReason
        preflightFailureSubreason = $failureSubreason
        preflightArtifactDir = $PreflightResult.ArtifactDir
        preflightExitCode = $PreflightResult.ExitCode
        preflightAttemptCount = $attemptCount
        preflightRetryUsed = $retryUsed
        preflightFirstFailureReason = $firstFailureReason
        preflightFirstFailureSubreason = $firstFailureSubreason
        preflightFirstArtifactDir = $firstArtifactDir
        failureCount = 1
        correctnessFailureCount = 1
        performanceFailureCount = 0
        runs = @()
        failures = @($failureLine)
        correctnessFailures = @($failureLine)
        performanceFailures = @()
    }

    $jsonSummary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $RunRoot 'phase5-analyzer-gui-acceptance-summary.json') -Encoding UTF8
    $jsonSummary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $RunRoot 'route-acceptance-summary.json') -Encoding UTF8
}

function Invoke-RegularNknRouteAcceptanceRun {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$PayloadSize,
        [Parameter(Mandatory = $true)][string]$ResolvedExePath
    )

    $scriptPath = Join-Path $RepoRoot 'tools\Run-FileTransferNknSoak.ps1'
    $cycleTimeoutSeconds = [Math]::Min([Math]::Max(180, $ProgressTimeoutSeconds), $TimeoutSeconds)
    Invoke-RouteAcceptanceChildScript -ScriptPath $scriptPath -Description ("Regular NKN {0}" -f $PayloadSize) -Arguments @(
        '-Mode', 'nkn-fast',
        '-PayloadSizes', $PayloadSize,
        '-Cycles', '1',
        '-Direction', 'helpee-to-helper',
        '-ArtifactDir', $ArtifactDir,
        '-ExePath', $ResolvedExePath,
        '-TimeoutSeconds', ([string]$TimeoutSeconds),
        '-CycleTimeoutSeconds', ([string]$cycleTimeoutSeconds),
        '-ProgressTimeoutSeconds', ([string]$ProgressTimeoutSeconds),
        '-FailOnGate'
    )
}

function Invoke-TunaRouteAcceptanceRun {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$ResolvedExePath,
        [Parameter(Mandatory = $true)][string]$ResolvedWalletPath,
        [Parameter(Mandatory = $true)][string]$ResolvedSidecarPath,
        [Parameter(Mandatory = $true)][string]$EffectiveWalletPassword,
        [Parameter(Mandatory = $true)][string]$RouteMode,
        [Parameter(Mandatory = $true)][string]$Fault,
        [string]$PayerMode = 'helpee',
        [string]$PayloadSize = '128MiB',
        [string]$LiveToggleSequence = '',
        [string]$LiveProofMode = 'None'
    )

    $scriptPath = Join-Path $RepoRoot 'tools\Run-FileTransferTunaGuiSmoke.ps1'
    if ($RouteMode -eq 'v4-restart-v6-fallback') {
        New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null
        $attempts = New-Object System.Collections.Generic.List[object]
        $maxAttempts = [Math]::Max(1, $FallbackMaxAttempts)
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
            $attemptDir = Join-Path $ArtifactDir ("attempt-{0}" -f $attempt)
            New-Item -ItemType Directory -Force -Path $attemptDir | Out-Null
            $failureMessage = ''
            $arguments = @(
                '-ExePath', $ResolvedExePath,
                '-WalletPath', $ResolvedWalletPath,
                '-WalletPassword', $EffectiveWalletPassword,
                '-SidecarPath', $ResolvedSidecarPath,
                '-PayerMode', $PayerMode,
                '-RouteMode', $RouteMode,
                '-Fault', $Fault,
                '-Direction', 'helpee-to-helper',
                '-PayloadSize', $PayloadSize,
                '-ArtifactDir', $attemptDir,
                '-TimeoutSeconds', ([string]$TimeoutSeconds),
                '-ProgressTimeoutSeconds', ([string]$ProgressTimeoutSeconds)
            )
            if (-not [string]::IsNullOrWhiteSpace($LiveToggleSequence)) {
                $arguments += @('-LiveToggleSequence', $LiveToggleSequence)
            }

            $exitCode = Invoke-RouteAcceptanceChildScriptNoThrow -ScriptPath $scriptPath -Arguments $arguments

            Select-RouteAcceptanceMeasuredFallbackLogSlice -ArtifactDir $attemptDir
            if ($exitCode -ne 0) {
                $failureMessage = "Tuna GUI {0} attempt {1} failed with exit code {2}." -f $RouteMode, $attempt, $exitCode
            }
            else {
                try {
                Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $attemptDir -LiveRouteProofMode $LiveProofMode
                }
                catch {
                    $failureMessage = $_.Exception.Message
                }
            }

            if ([string]::IsNullOrWhiteSpace($failureMessage)) {
                Copy-RouteAcceptanceSelectedAttemptArtifacts -AttemptDir $attemptDir -ArtifactDir $ArtifactDir
                $attempts.Add([ordered]@{
                    attempt = $attempt
                    artifactDir = $attemptDir
                    succeeded = $true
                    retryable = $false
                    failureReason = ''
                }) | Out-Null
                Write-RouteAcceptanceAttemptSummary -ArtifactDir $ArtifactDir -Attempts $attempts.ToArray() -SelectedAttempt $attempt
                return
            }

            $retryable = Test-RouteAcceptanceFallbackAttemptRetryable -ArtifactDir $attemptDir -FailureMessage $failureMessage
            $attempts.Add([ordered]@{
                attempt = $attempt
                artifactDir = $attemptDir
                succeeded = $false
                retryable = $retryable
                failureReason = $failureMessage
            }) | Out-Null

            if (-not $retryable -or $attempt -eq $maxAttempts) {
                Write-RouteAcceptanceAttemptSummary -ArtifactDir $ArtifactDir -Attempts $attempts.ToArray() -SelectedAttempt 0
                throw $failureMessage
            }
        }
    }

    $arguments = @(
        '-ExePath', $ResolvedExePath,
        '-WalletPath', $ResolvedWalletPath,
        '-WalletPassword', $EffectiveWalletPassword,
        '-SidecarPath', $ResolvedSidecarPath,
        '-PayerMode', $PayerMode,
        '-RouteMode', $RouteMode,
        '-Fault', $Fault,
        '-Direction', 'helpee-to-helper',
        '-PayloadSize', $PayloadSize,
        '-ArtifactDir', $ArtifactDir,
        '-TimeoutSeconds', ([string]$TimeoutSeconds),
        '-ProgressTimeoutSeconds', ([string]$ProgressTimeoutSeconds)
    )
    if (-not [string]::IsNullOrWhiteSpace($LiveToggleSequence)) {
        $arguments += @('-LiveToggleSequence', $LiveToggleSequence)
    }

    Invoke-RouteAcceptanceChildScript -ScriptPath $scriptPath -Description ("Tuna GUI {0}" -f $RouteMode) -Arguments $arguments

    if ($RouteMode -eq 'v4-restart-v6-fallback') {
        Select-RouteAcceptanceMeasuredFallbackLogSlice -ArtifactDir $ArtifactDir
    }

    Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $ArtifactDir -LiveRouteProofMode $LiveProofMode
}

function New-Phase4RouteAcceptanceScenario {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string[]]$ExpectedRouteChanges,
        [Parameter(Mandatory = $true)][long]$PayloadBytes,
        [string]$BaselineScenario = '',
        [AllowNull()]$Baseline = $null,
        [string]$RouteMode = '',
        [string]$Fault = 'none',
        [string]$PayerMode = 'helpee',
        [string]$LiveToggleSequence = '',
        [string]$LiveProofMode = 'None'
    )

    return [pscustomobject]@{
        Name = $Name
        Kind = $Kind
        ExpectedRouteChanges = @($ExpectedRouteChanges)
        PayloadBytes = $PayloadBytes
        BaselineScenario = $BaselineScenario
        Baseline = $Baseline
        RouteMode = $RouteMode
        Fault = $Fault
        PayerMode = $PayerMode
        LiveToggleSequence = $LiveToggleSequence
        LiveProofMode = $LiveProofMode
    }
}

function Get-Phase4RouteAcceptanceScenarios {
    param([Parameter(Mandatory = $true)]$BaselineManifest)

    $baselines = $BaselineManifest.Scenarios
    return @(
        (New-Phase4RouteAcceptanceScenario -Name 'regular-nkn-v4-64mb' -Kind 'regular' -ExpectedRouteChanges @('regular_nkn_v4_fast') -PayloadBytes 67108864L -BaselineScenario 'regular-nkn-v4-64mb' -Baseline $baselines['regular-nkn-v4-64mb'])
        (New-Phase4RouteAcceptanceScenario -Name 'active-tuna-v4-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4') -PayloadBytes 67108864L -BaselineScenario 'active-tuna-v4-64mb' -Baseline $baselines['active-tuna-v4-64mb'] -RouteMode 'preactivated' -Fault 'none' -PayerMode 'helpee')
        (New-Phase4RouteAcceptanceScenario -Name 'live-switch-off-helpee-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 67108864L -BaselineScenario 'live-switch-off-helpee-64mb' -Baseline $baselines['live-switch-off-helpee-64mb'] -RouteMode 'live-v4-switch-off' -Fault 'switch-off' -PayerMode 'helpee' -LiveProofMode 'SwitchOff')
        (New-Phase4RouteAcceptanceScenario -Name 'live-switch-off-helper-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 67108864L -BaselineScenario 'live-switch-off-helper-64mb' -Baseline $baselines['live-switch-off-helper-64mb'] -RouteMode 'live-v4-switch-off' -Fault 'switch-off' -PayerMode 'helper' -LiveProofMode 'SwitchOff')
        (New-Phase4RouteAcceptanceScenario -Name 'live-multi-toggle-off-on-off-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6', 'file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 67108864L -BaselineScenario 'live-multi-toggle-off-on-off-64mb' -Baseline $baselines['live-multi-toggle-off-on-off-64mb'] -RouteMode 'live-multi-toggle' -Fault 'switch-off' -PayerMode 'helpee' -LiveToggleSequence 'off,on,off' -LiveProofMode 'MultiToggle')
        (New-Phase4RouteAcceptanceScenario -Name 'regular-v4-live-activation-off-on-off-256mb' -Kind 'tuna' -ExpectedRouteChanges @('regular_nkn_v4_fast', 'file_tuna_v4', 'post_tuna_fallback_v6', 'file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 268435456L -RouteMode 'live-regular-activation-cycle' -Fault 'switch-off' -PayerMode 'helpee' -LiveToggleSequence 'on,off,on,off' -LiveProofMode 'RegularActivationCycle')
        (New-Phase4RouteAcceptanceScenario -Name 'second-transfer-after-reactivation' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6', 'file_tuna_v4') -PayloadBytes 134217728L -RouteMode 'live-reactivation-second-transfer' -Fault 'switch-off' -PayerMode 'helpee' -LiveToggleSequence 'off,on' -LiveProofMode 'None')
    )
}

function Get-Phase5RouteAcceptanceScenarios {
    param([Parameter(Mandatory = $true)]$BaselineManifest)

    $baselines = $BaselineManifest.Scenarios
    return @(
        (New-Phase4RouteAcceptanceScenario -Name 'regular-nkn-v4-64mb' -Kind 'regular' -ExpectedRouteChanges @('regular_nkn_v4_fast') -PayloadBytes 67108864L -BaselineScenario 'regular-nkn-v4-64mb' -Baseline $baselines['regular-nkn-v4-64mb'])
        (New-Phase4RouteAcceptanceScenario -Name 'active-tuna-v4-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4') -PayloadBytes 67108864L -BaselineScenario 'active-tuna-v4-64mb' -Baseline $baselines['active-tuna-v4-64mb'] -RouteMode 'preactivated' -Fault 'none' -PayerMode 'helpee')
        (New-Phase4RouteAcceptanceScenario -Name 'live-switch-off-helpee-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 67108864L -BaselineScenario 'live-switch-off-helpee-64mb' -Baseline $baselines['live-switch-off-helpee-64mb'] -RouteMode 'live-v4-switch-off' -Fault 'switch-off' -PayerMode 'helpee' -LiveProofMode 'SwitchOff')
        (New-Phase4RouteAcceptanceScenario -Name 'live-switch-off-helper-64mb' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 67108864L -BaselineScenario 'live-switch-off-helper-64mb' -Baseline $baselines['live-switch-off-helper-64mb'] -RouteMode 'live-v4-switch-off' -Fault 'switch-off' -PayerMode 'helper' -LiveProofMode 'SwitchOff')
        (New-Phase4RouteAcceptanceScenario -Name 'regular-v4-live-activation-off-on-off-256mb' -Kind 'tuna' -ExpectedRouteChanges @('regular_nkn_v4_fast', 'file_tuna_v4', 'post_tuna_fallback_v6', 'file_tuna_v4', 'post_tuna_fallback_v6') -PayloadBytes 268435456L -RouteMode 'live-regular-activation-cycle' -Fault 'switch-off' -PayerMode 'helpee' -LiveToggleSequence 'on,off,on,off' -LiveProofMode 'RegularActivationCycle')
        (New-Phase4RouteAcceptanceScenario -Name 'second-transfer-after-reactivation' -Kind 'tuna' -ExpectedRouteChanges @('file_tuna_v4', 'post_tuna_fallback_v6', 'file_tuna_v4') -PayloadBytes 134217728L -RouteMode 'live-reactivation-second-transfer' -Fault 'switch-off' -PayerMode 'helpee' -LiveToggleSequence 'off,on' -LiveProofMode 'None')
    )
}

function Test-RouteAcceptanceEvidenceFinalizationTimedOut {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    return -not [string]::IsNullOrWhiteSpace($ArtifactDir) -and
        (Test-Path -LiteralPath (Join-Path $ArtifactDir 'filetransfer-gui-evidence-finalization-timeout.json') -PathType Leaf)
}

function Test-Phase4RerunnableMeasurementFailure {
    param([Parameter(Mandatory = $true)]$Result)

    if (Test-RouteAcceptanceEvidenceFinalizationTimedOut -ArtifactDir ([string]$Result.artifactDir)) {
        return $false
    }

    $failures = @($Result.failures | ForEach-Object { [string]$_ })
    return $failures.Count -gt 0 -and
        (@($failures | Where-Object {
            $_.IndexOf('goodput regression exceeded', [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -and
            $_.IndexOf('measurement contaminated', [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -and
            -not (Test-Phase4RerunnableMeasurementExecutionFailure -Result $Result -Line ([string]$_))
        }).Count -eq 0)
}

function Test-Phase4RerunnableMeasurementExecutionFailure {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Line
    )

    if (-not $Result.measurementContaminated) {
        return $false
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$Result.setupFailurePhase) -or
        -not [string]::IsNullOrWhiteSpace([string]$Result.setupFailureReason)) {
        return $false
    }

    return $Line.IndexOf('scenario execution failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-Phase4FirstFailureReason {
    param([Parameter(Mandatory = $true)]$Result)

    $failure = @($Result.failures | Select-Object -First 1)
    if ($failure.Count -gt 0) {
        return [string]$failure[0]
    }

    return ''
}

function Test-Phase4SetupInvalidAttempt {
    param([Parameter(Mandatory = $true)]$Result)

    if (-not [string]::IsNullOrWhiteSpace([string]$Result.setupFailurePhase) -or
        -not [string]::IsNullOrWhiteSpace([string]$Result.setupFailureReason)) {
        return $true
    }

    if ([string]$Result.operatorVerdict -eq 'INVALID_SETUP') {
        return $true
    }

    foreach ($failure in @($Result.failures | ForEach-Object { [string]$_ })) {
        if ($failure.IndexOf('setup failure', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $failure.IndexOf('scenario rerun execution failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $failure.IndexOf('missing artifact: filetransfer-tuna-gui-summary.json', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Test-Phase5ScenarioRuntimeRouteEvidence {
    param([Parameter(Mandatory = $true)]$Result)

    $selectedRouteChanges = @($Result.selectedRouteChanges | ForEach-Object { [string]$_ } | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            -not [string]::Equals($_, '(none)', [System.StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals($_, '(unknown)', [System.StringComparison]::OrdinalIgnoreCase)
        })
    if ($selectedRouteChanges.Count -gt 1) {
        return $true
    }

    if ($selectedRouteChanges.Count -gt 0 -and
        ([string]::Equals([string]$Result.routeConsistencyVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase) -or
         [string]::Equals([string]$Result.liveRouteEpochProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase) -or
         [string]::Equals([string]$Result.fallbackLegAuthorityProofVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase))) {
        return $true
    }

    $finalRoute = [string]$Result.finalRoute
    $protocol = [string]$Result.protocol
    return -not [string]::IsNullOrWhiteSpace($finalRoute) -and
        -not [string]::Equals($finalRoute, '(unknown)', [System.StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals($finalRoute, '(none)', [System.StringComparison]::OrdinalIgnoreCase) -and
        $protocol -match '^[1-9][0-9]*$'
}

function Clear-Phase5StaleSetupFailureAfterRuntimeEvidence {
    param([Parameter(Mandatory = $true)]$Result)

    $phase = [string]$Result.setupFailurePhase
    $reason = [string]$Result.setupFailureReason
    if ([string]::IsNullOrWhiteSpace($phase) -and
        [string]::IsNullOrWhiteSpace($reason)) {
        return $false
    }

    if (-not (Test-Phase5ScenarioRuntimeRouteEvidence -Result $Result)) {
        return $false
    }

    $combinedSetup = "{0} {1}" -f $phase, $reason
    $isPreflightSetupLabel =
        $combinedSetup.IndexOf('preflight_bridge_bootstrap', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combinedSetup.IndexOf('nkn_bridge_bootstrap_network_unready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combinedSetup.IndexOf('helpee_bridge_host_ready_connect_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combinedSetup.IndexOf('helper_bridge_host_ready_connect_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    $isRuntimeFailureLabel =
        $combinedSetup.IndexOf('post_tuna_fallback_outbound_receive_worker_handoff_missing', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combinedSetup.IndexOf('outbound_sparse_worker_handoff', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combinedSetup.IndexOf('fallback_frontier_request_dispatch_missing', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combinedSetup.IndexOf('fallback_frontier_repair', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combinedSetup.IndexOf('runtime_unlock_retry', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combinedSetup.IndexOf('regular_v4_frontier_repair', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combinedSetup.IndexOf('regular_v4_receive_recovery', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combinedSetup.IndexOf('post_fallback_runtime_unlock_resume', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combinedSetup.IndexOf('fallback_tail_quiescence', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    if (-not $isPreflightSetupLabel -and -not $isRuntimeFailureLabel) {
        return $false
    }

    $keptFailures = @($Result.failures | ForEach-Object { [string]$_ } | Where-Object {
            $line = [string]$_
            $removePreflightSetupLine =
                $line.IndexOf('Tuna GUI setup failure:', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                ($line.IndexOf('preflight_bridge_bootstrap', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                 $line.IndexOf('nkn_bridge_bootstrap_network_unready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                 $line.IndexOf('helpee_bridge_host_ready_connect_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                 $line.IndexOf('helper_bridge_host_ready_connect_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
            $removeRuntimeSetupLine =
                $line.IndexOf('Tuna GUI setup failure:', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $isRuntimeFailureLabel -and
                ($line.IndexOf($phase, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                 $line.IndexOf($reason, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
            -not ($removePreflightSetupLine -or $removeRuntimeSetupLine)
        })
    $Result.failures.Clear()
    foreach ($failure in $keptFailures) {
        $Result.failures.Add($failure) | Out-Null
    }
    if ($isRuntimeFailureLabel) {
        $runtimeReason = if ([string]::IsNullOrWhiteSpace($reason)) { $phase } else { $reason }
        $runtimePhase = if ([string]::IsNullOrWhiteSpace($phase)) { 'runtime' } else { $phase }
        Add-RouteAcceptanceFailure -Result $Result -Message ("Tuna GUI runtime failure: phase={0}; reason={1}" -f $runtimePhase, $runtimeReason)
    }

    $Result.setupFailurePhase = ''
    $Result.setupFailureReason = ''
    return $true
}

function Test-Phase5RerunnableTransientSetupFailure {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)]$Result
    )

    if (Test-RouteAcceptanceEvidenceFinalizationTimedOut -ArtifactDir ([string]$Result.artifactDir)) {
        return $false
    }

    if ([string]$Scenario.Kind -eq 'regular') {
        $summaryPath = Join-Path $Result.artifactDir 'filetransfer-live-nkn-summary.txt'
        $stdoutPath = Join-Path $Result.artifactDir 'gui-smoke-stdout.log'
        if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $stdoutPath -PathType Leaf)) {
            return $false
        }

        $summary = Read-RouteAcceptanceKeyValueArtifact -Path $summaryPath
        $cyclesObserved = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'cycles_observed' -DefaultValue '0')
        $cyclesCompleted = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'cycles_completed' -DefaultValue '0')
        $protocol = Get-RouteAcceptanceReportValue -Report $summary -Name 'data_protocol_version' -DefaultValue '0'
        $stdoutText = Get-Content -LiteralPath $stdoutPath -Raw
        $expiredBeforeTransfer =
            $stdoutText.IndexOf('Timed out waiting for helpee Allow approval UI', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $stdoutText.IndexOf('The help request expired', [System.StringComparison]::OrdinalIgnoreCase) -ge 0

        if ($expiredBeforeTransfer -and $cyclesObserved -eq 0 -and $cyclesCompleted -eq 0 -and $protocol -eq '0') {
            $Result.setupFailurePhase = 'approval_ui'
            $Result.setupFailureReason = 'help_request_expired_before_transfer'
            return $true
        }

        return $false
    }

    if ([string]$Scenario.Kind -ne 'tuna') {
        return $false
    }

    if (-not (Test-Phase4SetupInvalidAttempt -Result $Result)) {
        return $false
    }

    foreach ($failure in @($Result.failures | ForEach-Object { [string]$_ })) {
        if ($failure.IndexOf('active file_tuna_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $failure.IndexOf('diagnostic_regular_nkn_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $failure.IndexOf('warning cap exceeded', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $false
        }
    }

    $phase = [string]$Result.setupFailurePhase
    $reason = [string]$Result.setupFailureReason
    $combined = ("{0} {1} {2}" -f $phase, $reason, (Join-RouteAcceptanceTokenList -Values $Result.failures))

    return $combined.IndexOf('harness_log_cursor', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('gui_smoke_log_cursor_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('activation_offer_not_observed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('activation_offer_sent_waiting_answer', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('regular_v4_receive_recovery_unproven', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('preflight_bridge_bootstrap', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('nkn_bridge_bootstrap_network_unready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('helpee_bridge_host_ready_connect_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('helper_bridge_host_ready_connect_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('helpee_invite_readiness_timeout', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('helper_incoming_request_not_observed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('Timed out waiting for helper incoming request acceptance UI', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('nkn_bridge_bootstrap_not_ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('nkn_bridge_start_failure', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('listener_ready_unavailable_contradiction', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('second_transfer_tuna_readiness_unstable', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('preflight_listener_unavailable', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('tuna_transport_not_active', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('terminal_before_accept', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('offer_sent_accept_not_enabled', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('offer_received_accept_not_enabled', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('Timed out waiting for live file-transfer terminal evidence', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('receiver-committed Tuna file progress', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-Phase5SingleScenarioSetupRerunAllowed {
    param([Parameter(Mandatory = $true)]$Result)

    $combined = ("{0} {1} {2}" -f `
        ([string]$Result.setupFailurePhase),
        ([string]$Result.setupFailureReason),
        (Join-RouteAcceptanceTokenList -Values $Result.failures))

    return $combined.IndexOf('harness_log_cursor', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('gui_smoke_log_cursor_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('terminal_before_accept', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('preflight_bridge_bootstrap', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('nkn_bridge_bootstrap_network_unready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('helpee_bridge_host_ready_connect_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('helper_bridge_host_ready_connect_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('Timed out waiting for helpee Allow approval UI', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('The help request expired', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('Ack was not received', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $combined.IndexOf('decision_send_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-Phase4PerformanceFailureLine {
    param([Parameter(Mandatory = $true)][string]$Line)

    return $Line.IndexOf('goodput regression exceeded', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Line.IndexOf('measurement contaminated', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-Phase4FailureClass {
    param([Parameter(Mandatory = $true)]$Failures)

    $failureLines = @($Failures | ForEach-Object { [string]$_ })
    if ($failureLines.Count -eq 0) {
        return 'none'
    }

    $performanceFailures = @($failureLines | Where-Object { Test-Phase4PerformanceFailureLine -Line ([string]$_) })
    if ($performanceFailures.Count -eq $failureLines.Count) {
        return 'performance'
    }

    if ($performanceFailures.Count -gt 0) {
        return 'correctness_and_performance'
    }

    return 'correctness'
}

function Test-Phase5CanonicalRepeatedToggleScenario {
    param([Parameter(Mandatory = $true)]$Scenario)

    return [string]$Scenario.Name -eq 'regular-v4-live-activation-off-on-off-256mb'
}

function Test-Phase5CanonicalSecondTunaActivationProven {
    param([Parameter(Mandatory = $true)]$Result)

    return @($Result.selectedRouteChanges | Where-Object { [string]$_ -eq 'file_tuna_v4' }).Count -ge 2
}

function Test-RouteAcceptanceFirstTransferTerminalBeforeLiveReactivation {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        return $false
    }

    try {
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        return ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $summary -Name 'firstTransferTerminalBeforeLiveReactivation' -DefaultValue $false)
    }
    catch {
        return $false
    }
}

function Test-RouteAcceptanceFirstTransferTerminalBeforeFallbackSwitchOff {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        return $false
    }

    try {
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        return ConvertTo-RouteAcceptanceBool -Value (Get-JsonPropertyValue -Object $summary -Name 'firstTransferTerminalBeforeFallbackSwitchOff' -DefaultValue $false)
    }
    catch {
        return $false
    }
}

function Test-Phase5CanonicalRuntimeUnlockReceiveRecoveryExhaustion {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)]$Result
    )

    if (-not (Test-Phase5CanonicalRepeatedToggleScenario -Scenario $Scenario)) {
        return $false
    }

    if (Test-Phase4SetupInvalidAttempt -Result $Result) {
        return $false
    }

    if ($Result.routeConsistencyVerdict -ne 'pass' -or
        $Result.finalRoute -ne 'regular_nkn_v4_fast' -or
        (Join-RouteAcceptanceTokenList -Values $Result.selectedRouteChanges) -ne 'regular_nkn_v4_fast') {
        return $false
    }

    if ($Result.route.IndexOf('file_tuna_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Result.route.IndexOf('diagnostic_regular_nkn_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $false
    }

    $operatorPath = Join-Path $Result.artifactDir 'filetransfer-operator-verdict.txt'
    $routePath = Join-Path $Result.artifactDir 'filetransfer-route-consistency-summary.txt'
    $retainedPath = Join-Path $Result.artifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $operatorPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $routePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $retainedPath -PathType Leaf)) {
        return $false
    }

    $operator = Read-RouteAcceptanceKeyValueArtifact -Path $operatorPath
    $routeSummary = Read-RouteAcceptanceKeyValueArtifact -Path $routePath
    $recoveryClass = Get-RouteAcceptanceReportValue -Report $operator -Name 'recovery_failure_class' -DefaultValue '(missing)'
    if ($recoveryClass -ne 'runtime_unlock_offer_observation_blocked_by_receive_recovery' -and
        $recoveryClass -ne 'runtime_unlock_liveness_timeout') {
        return $false
    }

    $offerNotObserved = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_offer_not_observed_count' -DefaultValue '0')
    $retryDispatched = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_dispatched_count' -DefaultValue '0')
    $authorityObserved = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_retry_authority_observed_count' -DefaultValue '0')
    $observationBlocked = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'runtime_unlock_offer_observation_blocked_count' -DefaultValue '0')
    $livenessTimeoutAfterRuntimeUnlock = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $operator -Name 'session_liveness_timeout_after_runtime_unlock_count' -DefaultValue '0')
    $receiveResumed = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $routeSummary -Name 'bridge_recovery_receive_resumed_count' -DefaultValue '0')
    if ($offerNotObserved -le 0 -or
        $retryDispatched -le 0 -or
        $authorityObserved -le 0 -or
        $livenessTimeoutAfterRuntimeUnlock -le 0 -or
        $receiveResumed -ne 0) {
        return $false
    }

    $retainedText = Get-Content -LiteralPath $retainedPath -Raw
    if ($retainedText.IndexOf('event=nkn_bridge_receive_stall_recovery_receive_resumed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $false
    }

    $hasMaxRestartExhaustion = [regex]::IsMatch($retainedText, 'event=nkn_bridge_receive_stall_recovery_failed;[^\r\n]*reason=max_restarts_reached', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $hasRegularV4UnprovenEscalation = $retainedText.IndexOf('regular_v4_unproven_recovery_escalation', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    $hasUnprovenRecoveryCompletion = [regex]::IsMatch($retainedText, 'event=nkn_bridge_receive_stall_recovery_completed;[^\r\n]*stall_reason=regular_v4_unproven_recovery_escalation', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $hasRegularV4UnprovenEscalation) {
        return $false
    }

    if ($recoveryClass -eq 'runtime_unlock_offer_observation_blocked_by_receive_recovery') {
        return $observationBlocked -gt 0 -and ($hasMaxRestartExhaustion -or $hasUnprovenRecoveryCompletion)
    }

    return $hasUnprovenRecoveryCompletion -or $hasMaxRestartExhaustion
}

function Set-Phase5CanonicalRuntimeUnlockReceiveRecoveryExhaustion {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)]$Result
    )

    if (-not (Test-Phase5CanonicalRuntimeUnlockReceiveRecoveryExhaustion -Scenario $Scenario -Result $Result)) {
        return $false
    }

    $Result.environmentalClassification = 'live_transport_receive_recovery_exhausted_before_runtime_unlock'
    Add-RouteAcceptanceMeasurementContamination -Result $Result -Reason 'live_transport_receive_recovery_exhausted_before_runtime_unlock'
    $environmentalFailure = 'environmental live transport receive recovery exhausted before runtime unlock; paired rerun required'
    if (-not (@($Result.failures | ForEach-Object { [string]$_ }) -contains $environmentalFailure)) {
        $Result.failures.Insert(0, $environmentalFailure)
    }

    return $true
}

function Test-Phase5RegularNknStartupPeerDisconnect {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)]$Result
    )

    if ([string]$Scenario.Kind -ne 'regular') {
        return $false
    }

    if ($Result.finalRoute -ne 'regular_nkn_v4_fast' -or
        [string]$Result.protocol -ne '4' -or
        $Result.routeConsistencyVerdict -ne 'pass') {
        return $false
    }

    if ($Result.route.IndexOf('file_tuna_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Result.route.IndexOf('diagnostic_regular_nkn_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $false
    }

    $summaryPath = Join-Path $Result.artifactDir 'filetransfer-live-nkn-summary.txt'
    $retainedPath = Join-Path $Result.artifactDir 'filetransfer-retained-log-slice.log'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $retainedPath -PathType Leaf)) {
        return $false
    }

    $summary = Read-RouteAcceptanceKeyValueArtifact -Path $summaryPath
    $protocol = Get-RouteAcceptanceReportValue -Report $summary -Name 'data_protocol_version' -DefaultValue '(missing)'
    $verdict = Get-RouteAcceptanceReportValue -Report $summary -Name 'verdict' -DefaultValue '(missing)'
    $cyclesObserved = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'cycles_observed' -DefaultValue '0')
    $cyclesCompleted = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'cycles_completed' -DefaultValue '0')
    $v4FeedbackBothFailedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'v4_feedback_both_failed_count' -DefaultValue '0')
    $v4SenderFailedCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'v4_sender_failed_count' -DefaultValue '0')
    $bridgeBulkQueueClearCount = ConvertTo-RouteAcceptanceInt -Value (Get-RouteAcceptanceReportValue -Report $summary -Name 'bridge_bulk_queue_clear_count' -DefaultValue '0')
    if ($protocol -ne '4' -or
        $verdict -ne 'FAIL_PROTOCOL_OR_INTEGRITY' -or
        $cyclesObserved -ne 0 -or
        $cyclesCompleted -ne 0 -or
        $v4FeedbackBothFailedCount -ne 0 -or
        $v4SenderFailedCount -le 0 -or
        $bridgeBulkQueueClearCount -le 0) {
        return $false
    }

    $retainedText = Get-Content -LiteralPath $retainedPath -Raw
    foreach ($required in @(
            'regular_v4_startup_local_only_no_ack',
            'event=filetransfer_v4_sender_failed',
            'error_code=peer_disconnected')) {
        if ($retainedText.IndexOf($required, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            return $false
        }
    }

    if ($retainedText.IndexOf('event=filetransfer_v4_feedback_both_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        [regex]::IsMatch($retainedText, '(?i)\broute\s*=\s*(file_tuna_v6|diagnostic_regular_nkn_v6)\b')) {
        return $false
    }

    return $true
}

function Get-Phase5FailureClass {
    param([Parameter(Mandatory = $true)]$Failures)

    function Test-Phase5FailureClassMarker {
        param(
            [Parameter(Mandatory = $true)][string]$Line,
            [Parameter(Mandatory = $true)][string]$Class
        )

        $escapedClass = [regex]::Escape($Class)
        return [regex]::IsMatch(
            $Line,
            "(?i)(?:failure_class|class|reason|setup_failure_reason)\s*[=:]\s*$escapedClass(?:\b|$)") -or
            [string]::Equals($Line.Trim(), $Class, [System.StringComparison]::OrdinalIgnoreCase)
    }

    $failureLines = @($Failures | ForEach-Object { [string]$_ })
    if ($failureLines.Count -eq 0) {
        return 'none'
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('harness_log_cursor', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('gui_smoke_log_cursor_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'harness_log_cursor'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('terminal_before_fallback_switch_off', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('fault_window_missed_before_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'terminal_before_fallback_switch_off'
        }
    }

    foreach ($line in $failureLines) {
        if (Test-Phase5FailureClassMarker -Line $line -Class 'runtime_unlock_filetransfer_owner_missing_before_path_lease') {
            return 'runtime_unlock_filetransfer_owner_missing_before_path_lease'
        }

        if (Test-Phase5FailureClassMarker -Line $line -Class 'runtime_unlock_filetransfer_owner_stale_before_offer_dispatch') {
            return 'runtime_unlock_filetransfer_owner_stale_before_offer_dispatch'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('retry_dispatch_successor_attempt_mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('runtime_unlock_retry_dispatch_successor_attempt_mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'runtime_unlock_retry_dispatch_successor_attempt_mismatch'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('runtime_unlock_retry_source_publication_mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('retry_dispatch_source_publication_mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'runtime_unlock_retry_source_publication_mismatch'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('runtime_unlock_retry_dispatch_grant_bypassed_by_retry_budget', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'runtime_unlock_retry_dispatch_grant_bypassed_by_retry_budget'
        }

        if ($line.IndexOf('runtime_unlock_retry_dispatch_contract_unsettled', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'runtime_unlock_retry_dispatch_contract_unsettled'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('runtime_unlock_retry_dispatch_authority_tuple_mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('runtime_unlock_retry_dispatch_decision_tuple_rejected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'runtime_unlock_retry_dispatch_authority_tuple_mismatch'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('runtime_unlock_retry_dispatch_missing_after_authority_granted', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'runtime_unlock_retry_dispatch_missing_after_authority_granted'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('runtime_unlock_retry_regular_v4_recovery_settlement_missing', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'runtime_unlock_retry_regular_v4_recovery_settlement_missing'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('regular_v4_receive_recovery_rejected_before_runtime_unlock', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'regular_v4_receive_recovery_rejected_before_runtime_unlock'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('regular_v4_frontier_repair_liveness_settlement_missing', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('regular_v4_frontier_repair_send_deferred_or_stalled', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('regular_v4_receive_recovery_settlement_missing', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'regular_v4_frontier_repair_liveness_settlement_missing'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('second transfer setup failure after reactivation', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('second transfer did not start because first transfer setup failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'setup'
        }

        if ($line.IndexOf('fallback leg authority proof verdict is fail', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('fallback leg authority proof must pass', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'fallback_authority'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('route consistency', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('route mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('selected route mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('measured route mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('protocol mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('measured protocol mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('route selected protocol mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('route selected runtime mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('active file_tuna_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('file_tuna_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('diagnostic_regular_nkn_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'route_runtime'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('post_tuna_fallback_outbound_receive_worker_handoff_missing', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'post_tuna_fallback_outbound_receive_worker_handoff_missing'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('fallback_frontier_request_dispatch_missing', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'fallback_frontier_request_dispatch_missing'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('fallback_frontier_repair_stalled', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'fallback_frontier_repair_stalled'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('second_tuna_activation_not_proven', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('terminal_before_second_tuna_activation', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'second_tuna_activation_not_proven'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('setup failure', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('transient setup failure', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('scenario isolation failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('scenario rerun setup failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('preflight', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('helpee_invite_readiness_timeout', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('scenario execution failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('missing artifact', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'setup'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('completion/integrity', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('failed completion/integrity', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('terminal/integrity proof is incomplete', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('terminal errors observed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('terminals not Completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'protocol_or_integrity'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('live route epoch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'live_route_proof'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('fallback leg authority', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'fallback_authority'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('route consistency', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('route mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('selected route mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('measured route mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('protocol mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('measured protocol mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('route selected protocol mismatch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('runtime', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('file_tuna_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('diagnostic_regular_nkn_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'route_runtime'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('bridge liveness', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('session liveness timeout', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'bridge_liveness'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('control-plane', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('control plane', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'fallback_authority'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('warning cap exceeded', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('operator verdict', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('external_transport_churn', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'warning_policy'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('goodput regression exceeded', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('measurement contaminated', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'performance'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('environmental', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('live_transport', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'environmental'
        }
    }

    foreach ($line in $failureLines) {
        if ($line.IndexOf('operator hard failures observed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return 'protocol_or_integrity'
        }
    }

    return 'protocol_or_integrity'
}

function Get-RouteAcceptanceResultFailureClass {
    param(
        [ValidateSet('phase4', 'phase5')]
        [string]$AcceptancePhase = 'phase4',
        [Parameter(Mandatory = $true)]$Result
    )

    $conclusiveRouteRuntimeCandidate = Test-RouteAcceptanceConclusiveRouteRuntimeCandidate `
        -Candidate ([string]$Result.runtimeFailureCandidate) `
        -SemanticFailureDomain ([string]$Result.semanticFailureDomain)
    if ($AcceptancePhase -eq 'phase5' -and $script:RouteAcceptanceExecutionMode -ne 'fake') {
        if (-not [bool]$Result.currentRuntimeProof -or
            -not [string]::Equals([string]$Result.manifestValidationVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string]$Result.cursorIntegrityVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)) {
            return 'incomplete_proof'
        }

        if (-not $conclusiveRouteRuntimeCandidate -and
            -not [string]::Equals([string]$Result.transferEvidenceStatus, 'complete', [System.StringComparison]::OrdinalIgnoreCase)) {
            return 'incomplete_proof'
        }

    }

    if ($AcceptancePhase -eq 'phase5' -and
        -not [string]::IsNullOrWhiteSpace([string]$Result.runtimeFailureCandidate) -and
        -not [string]::Equals([string]$Result.runtimeFailureCandidate, '(none)', [System.StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals([string]$Result.runtimeFailureCandidate, 'none', [System.StringComparison]::OrdinalIgnoreCase)) {
        if (Test-RouteAcceptanceIncompleteProofRuntimeCandidate -Candidate ([string]$Result.runtimeFailureCandidate)) {
            return 'incomplete_proof'
        }
        if ([string]$Result.runtimeFailureCandidate -in @(
            'active_file_tuna_v6_evidence',
            'diagnostic_regular_nkn_v6_evidence',
            'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing',
            'runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing',
            'runtime_unlock_regular_v4_route_commit_send_drain_missing')) {
            return 'route_runtime'
        }
        return [string]$Result.runtimeFailureCandidate
    }

    if ($AcceptancePhase -eq 'phase5' -and
        [string]$Result.environmentalClassification -eq 'live_transport_receive_recovery_exhausted_before_runtime_unlock' -and
        $Result.failures.Count -gt 0) {
        return 'environmental'
    }

    if ($AcceptancePhase -eq 'phase5') {
        return Get-Phase5FailureClass -Failures $Result.failures
    }

    return Get-Phase4FailureClass -Failures $Result.failures
}

function Assert-Phase5ScenarioRun {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)]$Result
    )

    if ([string]::Equals([string]$Result.setupFailurePhase, 'preflight_bridge_bootstrap', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals([string]$Result.setupFailurePhase, 'harness_log_cursor', [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    if ($Result.liveRouteEpochProofVerdict -eq 'fail') {
        Add-RouteAcceptanceFailure -Result $Result -Message 'live route epoch proof verdict is fail'
    }

    if ($Result.fallbackLegAuthorityProofVerdict -eq 'fail' -and [string]$Scenario.Name -ne 'second-transfer-after-reactivation') {
        Add-RouteAcceptanceFailure -Result $Result -Message 'fallback leg authority proof verdict is fail'
    }

    if ($Result.bridgeLivenessIntegrationVerdict -eq 'fail') {
        Add-RouteAcceptanceFailure -Result $Result -Message 'bridge liveness integration verdict is fail'
    }

    if ($Result.controlPlaneIsolationVerdict -eq 'fail' -and [string]$Scenario.Name -ne 'second-transfer-after-reactivation') {
        Add-RouteAcceptanceFailure -Result $Result -Message 'control-plane isolation verdict is fail'
    }

    $canonicalRepeatedToggle = Test-Phase5CanonicalRepeatedToggleScenario -Scenario $Scenario
    if ($canonicalRepeatedToggle) {
        $canonicalTerminalFallbackPrefix = Test-RouteAcceptanceTerminalFallbackPrefixOutcome `
            -ArtifactDir $Result.artifactDir `
            -ActualRouteChanges $Result.selectedRouteChanges `
            -ExpectedRouteChanges $Scenario.ExpectedRouteChanges
        $canonicalObservedFallback = @($Result.selectedRouteChanges) -contains 'post_tuna_fallback_v6'
        $canonicalRequiresFallbackProof = $canonicalObservedFallback -or -not $canonicalTerminalFallbackPrefix

        if ($Result.liveRouteEpochProofVerdict -ne 'pass') {
            Add-RouteAcceptanceFailure -Result $Result -Message ("canonical repeated-toggle live route proof must pass, actual {0}" -f $Result.liveRouteEpochProofVerdict)
        }

        if (-not (Test-Phase5CanonicalSecondTunaActivationProven -Result $Result)) {
            $actualCanonicalRouteText = Join-RouteAcceptanceTokenList -Values $Result.selectedRouteChanges
            if (Test-RouteAcceptanceFirstTransferTerminalBeforeFallbackSwitchOff -ArtifactDir $Result.artifactDir) {
                Add-RouteAcceptanceFailure -Result $Result -Message ("terminal_before_fallback_switch_off: expected post_tuna_fallback_v6 and second file_tuna_v4 route before terminal; actual {0}" -f $actualCanonicalRouteText)
            }
            elseif (Test-RouteAcceptanceFirstTransferTerminalBeforeLiveReactivation -ArtifactDir $Result.artifactDir) {
                Add-RouteAcceptanceFailure -Result $Result -Message ("terminal_before_second_tuna_activation: expected second file_tuna_v4 route before terminal; actual {0}" -f $actualCanonicalRouteText)
            }
            else {
                Add-RouteAcceptanceFailure -Result $Result -Message ("second_tuna_activation_not_proven: expected second file_tuna_v4 route; actual {0}" -f $actualCanonicalRouteText)
            }
        }

        if ($canonicalRequiresFallbackProof -and $Result.fallbackLegAuthorityProofVerdict -ne 'pass') {
            Add-RouteAcceptanceFailure -Result $Result -Message ("canonical repeated-toggle fallback leg authority proof must pass, actual {0}" -f $Result.fallbackLegAuthorityProofVerdict)
        }

        if ($canonicalRequiresFallbackProof -and $Result.bridgeLivenessIntegrationVerdict -ne 'pass') {
            Add-RouteAcceptanceFailure -Result $Result -Message ("canonical repeated-toggle bridge liveness integration proof must pass, actual {0}" -f $Result.bridgeLivenessIntegrationVerdict)
        }

        if ($canonicalRequiresFallbackProof -and $Result.controlPlaneIsolationVerdict -ne 'pass') {
            Add-RouteAcceptanceFailure -Result $Result -Message ("canonical repeated-toggle control-plane isolation proof must pass, actual {0}" -f $Result.controlPlaneIsolationVerdict)
        }
    }
    elseif ($Result.bridgeLivenessIntegrationVerdict -eq 'none') {
        $retainedLogPath = Join-Path $Result.artifactDir 'filetransfer-retained-log-slice.log'
        if (Test-Path -LiteralPath $retainedLogPath -PathType Leaf) {
            $retainedText = Get-Content -LiteralPath $retainedLogPath -Raw
            $Result.sessionLivenessTimeoutCount = [regex]::Matches($retainedText, 'event=session_liveness_timeout(?:;|\s)').Count
            if ($Result.sessionLivenessTimeoutCount -gt 0) {
                Add-RouteAcceptanceFailure -Result $Result -Message ("bridge liveness integration verdict is none but session_liveness_timeout_count={0}" -f $Result.sessionLivenessTimeoutCount)
            }
        }

        if ($Result.bridgeLivenessStaleDeferralCount -gt 0) {
            Add-RouteAcceptanceFailure -Result $Result -Message ("bridge liveness integration verdict is none but stale_deferral_count={0}" -f $Result.bridgeLivenessStaleDeferralCount)
        }

        if (-not $Result.completed -or -not $Result.shaOk) {
            Add-RouteAcceptanceFailure -Result $Result -Message ("bridge liveness integration verdict is none but terminal/integrity proof is incomplete: completed={0}; sha_ok={1}" -f $Result.completed, $Result.shaOk)
        }
    }
}

function Get-Phase4ScenarioPayloadSizeText {
    param([Parameter(Mandatory = $true)]$Scenario)

    $payloadBytes = [long]$Scenario.PayloadBytes
    if ($payloadBytes -eq 134217728L) {
        return '128MiB'
    }

    if ($payloadBytes -eq 67108864L) {
        return '64MiB'
    }

    if ($payloadBytes -gt 0 -and ($payloadBytes % 1048576L) -eq 0) {
        return ('{0}MiB' -f [long]($payloadBytes / 1048576L))
    }

    return ([string]$payloadBytes)
}

function Invoke-Phase4RouteAcceptanceScenario {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)][bool]$FakeMode,
        [ValidateSet('phase4', 'phase5')]
        [string]$AcceptancePhase = 'phase4',
        [string]$ResolvedExePath = '',
        [string]$ResolvedWalletPath = '',
        [string]$ResolvedSidecarPath = '',
        [string]$EffectiveWalletPassword = ''
    )

    $artifactDir = Join-Path $RunRoot ([string]$Scenario.Name)
    New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
    $payloadSize = Get-Phase4ScenarioPayloadSizeText -Scenario $Scenario
    $expectedRouteChanges = @($Scenario.ExpectedRouteChanges)
    $expectedFinalRoute = if ($expectedRouteChanges.Count -gt 0) {
        [string]$expectedRouteChanges[$expectedRouteChanges.Count - 1]
    }
    else {
        '(unknown)'
    }

    $expectedProtocol = 0
    if (-not [string]::IsNullOrWhiteSpace($expectedFinalRoute) -and $expectedFinalRoute -ne '(unknown)') {
        $expectedMetadata = Get-RouteAcceptanceRouteMetadata -Route $expectedFinalRoute
        if ($null -ne $expectedMetadata) {
            $expectedProtocol = [int]$expectedMetadata.Protocol
        }
    }

    $scenarioExecutionFailure = ''
    $result = $null
    $scenarioIsolationResult = $null
    $skipScenarioRun = $false

    if ($AcceptancePhase -eq 'phase5' -and (Test-Phase5ScenarioRequiresIsolation -Scenario $Scenario)) {
        $scenarioIsolationResult = Invoke-Phase5ScenarioIsolationPreflight -Scenario $Scenario -RepoRoot $RepoRoot -RunRoot $RunRoot -FakeMode $FakeMode -AttemptIndex 1 -ResolvedExePath $ResolvedExePath
        if (-not [bool]$scenarioIsolationResult.Passed) {
            $skipScenarioRun = $true
            $result = New-RouteAcceptanceRunResult -Name ([string]$Scenario.Name) -ArtifactDir $artifactDir -ExpectedRoute $expectedFinalRoute -ExpectedProtocol $expectedProtocol
            Set-RouteAcceptanceScenarioIsolationResult -Result $result -IsolationResult $scenarioIsolationResult
            $result.setupFailurePhase = 'scenario_isolation'
            $result.setupFailureReason = [string]$scenarioIsolationResult.FailureReason
            Add-RouteAcceptanceFailure -Result $result -Message ("scenario isolation failed before run: {0}" -f $result.setupFailureReason)
        }
    }

    if (-not $skipScenarioRun) {
        if (-not $FakeMode -and -not [string]::IsNullOrWhiteSpace($ResolvedExePath)) {
            Assert-RouteAcceptanceCleanAppProcessState -ResolvedExePath $ResolvedExePath -ArtifactDir $artifactDir -Reason ("{0}_before_attempt_1" -f [string]$Scenario.Name)
        }

        try {
            Invoke-RouteAcceptanceScenarioIdentityScope -ScenarioName ([string]$Scenario.Name) -AttemptIndex 1 -Action {
                if ($FakeMode) {
                    if ($script:RouteAcceptanceExecutionMode -eq 'evidence_dry_run' -and
                        -not [string]::IsNullOrWhiteSpace($script:RouteAcceptanceEvidenceDryRunFixtureRoot)) {
                        Copy-RouteAcceptanceEvidenceDryRunScenarioFixture `
                            -ScenarioName ([string]$Scenario.Name) `
                            -AttemptIndex 1 `
                            -Destination $artifactDir
                    }
                    else {
                        Write-RouteAcceptanceFakePhase4Run -Scenario $Scenario -ArtifactDir $artifactDir
                    }
                }
                elseif ($Scenario.Kind -eq 'regular') {
                    Invoke-RegularNknRouteAcceptanceRun -RepoRoot $RepoRoot -ArtifactDir $artifactDir -PayloadSize $payloadSize -ResolvedExePath $ResolvedExePath
                }
                else {
                    Invoke-TunaRouteAcceptanceRun `
                        -RepoRoot $RepoRoot `
                        -ArtifactDir $artifactDir `
                        -ResolvedExePath $ResolvedExePath `
                        -ResolvedWalletPath $ResolvedWalletPath `
                        -ResolvedSidecarPath $ResolvedSidecarPath `
                        -EffectiveWalletPassword $EffectiveWalletPassword `
                        -RouteMode ([string]$Scenario.RouteMode) `
                        -Fault ([string]$Scenario.Fault) `
                        -PayerMode ([string]$Scenario.PayerMode) `
                        -PayloadSize $payloadSize `
                        -LiveToggleSequence ([string]$Scenario.LiveToggleSequence) `
                        -LiveProofMode ([string]$Scenario.LiveProofMode)
                }
            }
        }
        catch {
            $scenarioExecutionFailure = (($_ | Out-String).Trim() -replace '[\r\n]+', ' ')
        }
        finally {
            if (-not $FakeMode -and -not [string]::IsNullOrWhiteSpace($ResolvedExePath)) {
                try {
                    Assert-RouteAcceptanceCleanAppProcessState -ResolvedExePath $ResolvedExePath -ArtifactDir $artifactDir -Reason ("{0}_after_attempt_1" -f [string]$Scenario.Name)
                }
                catch {
                    $cleanupFailure = (($_ | Out-String).Trim() -replace '[\r\n]+', ' ')
                    if ([string]::IsNullOrWhiteSpace($scenarioExecutionFailure)) {
                        $scenarioExecutionFailure = $cleanupFailure
                    }
                    else {
                        $scenarioExecutionFailure = "{0}; cleanup_failure={1}" -f $scenarioExecutionFailure, $cleanupFailure
                    }
                }
            }
        }

        try {
            $result = Assert-Phase4ScenarioRun -Scenario $Scenario -ArtifactDir $artifactDir
        }
        catch {
            $assertionFailure = (($_ | Out-String).Trim() -replace '[\r\n]+', ' ')
            $assertionStack = [string]$_.ScriptStackTrace
            if (-not [string]::IsNullOrWhiteSpace($assertionStack)) {
                $assertionFailure = "{0}; stack={1}" -f $assertionFailure, ($assertionStack -replace '[\r\n]+', ' ')
            }

            $result = New-RouteAcceptanceRunResult -Name ([string]$Scenario.Name) -ArtifactDir $artifactDir -ExpectedRoute $expectedFinalRoute -ExpectedProtocol $expectedProtocol
            $result.setupFailurePhase = 'assertion'
            $result.setupFailureReason = $assertionFailure
            Add-RouteAcceptanceFailure -Result $result -Message ("scenario assertion failed: {0}" -f $assertionFailure)
        }

        if ($null -ne $scenarioIsolationResult) {
            Set-RouteAcceptanceScenarioIsolationResult -Result $result -IsolationResult $scenarioIsolationResult
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($scenarioExecutionFailure) -and
        -not [string]::Equals([string]$result.setupFailurePhase, 'preflight_bridge_bootstrap', [System.StringComparison]::OrdinalIgnoreCase)) {
        Add-RouteAcceptanceFailure -Result $result -Message ("scenario execution failed: {0}" -f $scenarioExecutionFailure)
    }
    if ($AcceptancePhase -eq 'phase5') {
        Assert-Phase5ScenarioRun -Scenario $Scenario -Result $result
        Set-Phase5RegularNknCompletedExternalTransportVariance -Scenario $Scenario -Result $result | Out-Null
        Set-Phase5CompletedLiveFallbackWarningVariance -Scenario $Scenario -Result $result | Out-Null
        Set-Phase5PostTerminalGuiCleanupWarning -Scenario $Scenario -Result $result | Out-Null
        Clear-Phase5StaleSetupFailureAfterRuntimeEvidence -Result $result | Out-Null
    }
    Add-RouteAcceptanceScenarioAttemptEvidence -ScenarioName ([string]$Scenario.Name) -AttemptIndex 1 -ArtifactDir $artifactDir
    $phase5CanonicalReceiveRecoveryExhaustion = $false
    if ($AcceptancePhase -eq 'phase5') {
        $phase5CanonicalReceiveRecoveryExhaustion = Set-Phase5CanonicalRuntimeUnlockReceiveRecoveryExhaustion -Scenario $Scenario -Result $result
    }
    $phase5TransientSetupFailure = $false
    if ($AcceptancePhase -eq 'phase5') {
        $phase5TransientSetupFailure = Test-Phase5RerunnableTransientSetupFailure -Scenario $Scenario -Result $result
    }
    $phase5RegularStartupPeerDisconnect = $false
    if ($AcceptancePhase -eq 'phase5') {
        $phase5RegularStartupPeerDisconnect = Test-Phase5RegularNknStartupPeerDisconnect -Scenario $Scenario -Result $result
    }
    $evidenceFinalizationTimedOut =
        Test-RouteAcceptanceEvidenceFinalizationTimedOut -ArtifactDir ([string]$result.artifactDir)

    $filteredSingleScenarioInvalidSetup =
        $AcceptancePhase -eq 'phase5' -and
        @($ScenarioName).Count -gt 0 -and
        $phase5TransientSetupFailure -and
        (Test-Phase4SetupInvalidAttempt -Result $result) -and
        -not (Test-Phase5SingleScenarioSetupRerunAllowed -Result $result)

    if (-not $evidenceFinalizationTimedOut -and
        -not $filteredSingleScenarioInvalidSetup -and
        ((Test-Phase4RerunnableMeasurementFailure -Result $result) -or $phase5CanonicalReceiveRecoveryExhaustion -or $phase5TransientSetupFailure -or $phase5RegularStartupPeerDisconnect)) {
        $firstAttemptResult = $result
        $firstFailureReason = if ($phase5CanonicalReceiveRecoveryExhaustion) {
            'environmental receive recovery exhaustion: live_transport_receive_recovery_exhausted_before_runtime_unlock'
        }
        elseif ($phase5RegularStartupPeerDisconnect) {
            'transient regular NKN startup peer disconnect: regular_v4_startup_local_only_no_ack'
        }
        elseif ($phase5TransientSetupFailure) {
            "transient setup failure: phase={0}; reason={1}" -f `
                ($(if ([string]::IsNullOrWhiteSpace([string]$firstAttemptResult.setupFailurePhase)) { '(unknown)' } else { [string]$firstAttemptResult.setupFailurePhase })),
                ($(if ([string]::IsNullOrWhiteSpace([string]$firstAttemptResult.setupFailureReason)) { '(unknown)' } else { [string]$firstAttemptResult.setupFailureReason }))
        }
        else {
            Get-Phase4FirstFailureReason -Result $firstAttemptResult
        }
        $maxReruns = if ($phase5TransientSetupFailure -or $phase5RegularStartupPeerDisconnect) {
            [Math]::Max(0, $SetupOnlyRerunLimit)
        }
        else {
            [Math]::Max(0, $GoodputOnlyRerunLimit)
        }
        for ($rerun = 1; $rerun -le $maxReruns; $rerun++) {
            $rerunDir = Join-Path $RunRoot ("{0}-rerun-{1}" -f $Scenario.Name, $rerun)
            New-Item -ItemType Directory -Force -Path $rerunDir | Out-Null
            $rerunExecutionFailure = ''
            $rerunIsolationResult = $null
            $skipRerunScenarioRun = $false
            if ($AcceptancePhase -eq 'phase5' -and (Test-Phase5ScenarioRequiresIsolation -Scenario $Scenario)) {
                $rerunIsolationResult = Invoke-Phase5ScenarioIsolationPreflight -Scenario $Scenario -RepoRoot $RepoRoot -RunRoot $RunRoot -FakeMode $FakeMode -AttemptIndex ($rerun + 1) -ResolvedExePath $ResolvedExePath
                if (-not [bool]$rerunIsolationResult.Passed) {
                    $skipRerunScenarioRun = $true
                    $rerunResult = New-RouteAcceptanceRunResult -Name ([string]$Scenario.Name) -ArtifactDir $rerunDir -ExpectedRoute $expectedFinalRoute -ExpectedProtocol $expectedProtocol
                    Set-RouteAcceptanceScenarioIsolationResult -Result $rerunResult -IsolationResult $rerunIsolationResult
                    $rerunResult.setupFailurePhase = 'scenario_isolation'
                    $rerunResult.setupFailureReason = [string]$rerunIsolationResult.FailureReason
                    Add-RouteAcceptanceFailure -Result $rerunResult -Message ("scenario isolation failed before rerun: {0}" -f $rerunResult.setupFailureReason)
                }
            }

            if (-not $skipRerunScenarioRun) {
                if (-not $FakeMode -and -not [string]::IsNullOrWhiteSpace($ResolvedExePath)) {
                    Assert-RouteAcceptanceCleanAppProcessState -ResolvedExePath $ResolvedExePath -ArtifactDir $rerunDir -Reason ("{0}_before_rerun_{1}" -f [string]$Scenario.Name, $rerun)
                }

                try {
                    Invoke-RouteAcceptanceScenarioIdentityScope -ScenarioName ([string]$Scenario.Name) -AttemptIndex ($rerun + 1) -Action {
                        if ($FakeMode) {
                            if ($script:RouteAcceptanceExecutionMode -eq 'evidence_dry_run' -and
                                -not [string]::IsNullOrWhiteSpace($script:RouteAcceptanceEvidenceDryRunFixtureRoot)) {
                                Copy-RouteAcceptanceEvidenceDryRunScenarioFixture `
                                    -ScenarioName ([string]$Scenario.Name) `
                                    -AttemptIndex ($rerun + 1) `
                                    -Destination $rerunDir
                            }
                            else {
                                Write-RouteAcceptanceFakePhase4Run -Scenario $Scenario -ArtifactDir $rerunDir -RerunAttempt $rerun
                            }
                        }
                        elseif ($Scenario.Kind -eq 'regular') {
                            Invoke-RegularNknRouteAcceptanceRun -RepoRoot $RepoRoot -ArtifactDir $rerunDir -PayloadSize $payloadSize -ResolvedExePath $ResolvedExePath
                        }
                        else {
                            Invoke-TunaRouteAcceptanceRun `
                                -RepoRoot $RepoRoot `
                                -ArtifactDir $rerunDir `
                                -ResolvedExePath $ResolvedExePath `
                                -ResolvedWalletPath $ResolvedWalletPath `
                                -ResolvedSidecarPath $ResolvedSidecarPath `
                                -EffectiveWalletPassword $EffectiveWalletPassword `
                                -RouteMode ([string]$Scenario.RouteMode) `
                                -Fault ([string]$Scenario.Fault) `
                                -PayerMode ([string]$Scenario.PayerMode) `
                                -PayloadSize $payloadSize `
                                -LiveToggleSequence ([string]$Scenario.LiveToggleSequence) `
                                -LiveProofMode ([string]$Scenario.LiveProofMode)
                        }
                    }
                }
                catch {
                    $rerunExecutionFailure = (($_ | Out-String).Trim() -replace '[\r\n]+', ' ')
                }
                finally {
                    if (-not $FakeMode -and -not [string]::IsNullOrWhiteSpace($ResolvedExePath)) {
                        try {
                            Assert-RouteAcceptanceCleanAppProcessState -ResolvedExePath $ResolvedExePath -ArtifactDir $rerunDir -Reason ("{0}_after_rerun_{1}" -f [string]$Scenario.Name, $rerun)
                        }
                        catch {
                            $cleanupFailure = (($_ | Out-String).Trim() -replace '[\r\n]+', ' ')
                            if ([string]::IsNullOrWhiteSpace($rerunExecutionFailure)) {
                                $rerunExecutionFailure = $cleanupFailure
                            }
                            else {
                                $rerunExecutionFailure = "{0}; cleanup_failure={1}" -f $rerunExecutionFailure, $cleanupFailure
                            }
                        }
                    }
                }

                $rerunResult = Assert-Phase4ScenarioRun -Scenario $Scenario -ArtifactDir $rerunDir
                if ($null -ne $rerunIsolationResult) {
                    Set-RouteAcceptanceScenarioIsolationResult -Result $rerunResult -IsolationResult $rerunIsolationResult
                }
            }
            if (-not [string]::IsNullOrWhiteSpace($rerunExecutionFailure)) {
                Add-RouteAcceptanceFailure -Result $rerunResult -Message ("scenario rerun execution failed: {0}" -f $rerunExecutionFailure)
            }
            if ($AcceptancePhase -eq 'phase5') {
                Assert-Phase5ScenarioRun -Scenario $Scenario -Result $rerunResult
                Set-Phase5RegularNknCompletedExternalTransportVariance -Scenario $Scenario -Result $rerunResult | Out-Null
                Set-Phase5CompletedLiveFallbackWarningVariance -Scenario $Scenario -Result $rerunResult | Out-Null
                Set-Phase5PostTerminalGuiCleanupWarning -Scenario $Scenario -Result $rerunResult | Out-Null
                Set-Phase5CanonicalRuntimeUnlockReceiveRecoveryExhaustion -Scenario $Scenario -Result $rerunResult | Out-Null
            }
            Add-RouteAcceptanceScenarioAttemptEvidence -ScenarioName ([string]$Scenario.Name) -AttemptIndex ($rerun + 1) -ArtifactDir $rerunDir
            if (Test-RouteAcceptanceEvidenceFinalizationTimedOut -ArtifactDir $rerunDir) {
                $result = $rerunResult
                break
            }
            $rerunResult.attemptCount = $rerun + 1
            $rerunResult.retryUsed = $true
            $rerunResult.selectedAttempt = if ($rerunResult.failures.Count -eq 0) { $rerun + 1 } else { 0 }
            $rerunResult.firstFailureReason = $firstFailureReason
            if ($rerunResult.failures.Count -ne 0 -and (Test-Phase4SetupInvalidAttempt -Result $rerunResult)) {
                $rerunFailureReason = if (-not [string]::IsNullOrWhiteSpace($rerunExecutionFailure)) {
                    "scenario rerun execution failed: {0}" -f $rerunExecutionFailure
                }
                else {
                    Get-Phase4FirstFailureReason -Result $rerunResult
                }
                if ([string]::IsNullOrWhiteSpace($rerunFailureReason)) {
                    $rerunFailureReason = 'rerun did not produce comparable transfer evidence'
                }

                $firstAttemptResult.attemptCount = $rerun + 1
                $firstAttemptResult.retryUsed = $true
                $firstAttemptResult.selectedAttempt = 1
                $firstAttemptResult.firstFailureReason = $firstFailureReason
                $firstAttemptResult.rerunArtifactDir = $rerunDir
                $firstAttemptResult.rerunFailureReason = $rerunFailureReason
                $firstAttemptFailureClass = Get-RouteAcceptanceResultFailureClass -AcceptancePhase $AcceptancePhase -Result $firstAttemptResult
                if ($firstAttemptFailureClass -ne 'performance' -and $firstAttemptFailureClass -ne 'environmental') {
                    Add-RouteAcceptanceFailure -Result $firstAttemptResult -Message ("scenario rerun setup failed; preserving first-attempt evidence: {0}" -f $rerunFailureReason)
                }
                $result = $firstAttemptResult
                break
            }

            $firstAttemptFailureClassForWarningPolicy = Get-RouteAcceptanceResultFailureClass -AcceptancePhase $AcceptancePhase -Result $firstAttemptResult
            $rerunWarningCapExceededKinds = [string]$rerunResult.warningCapExceededKinds
            $rerunIntroducedWarningPolicyEvidence = $AcceptancePhase -eq 'phase5' -and
                ($firstAttemptFailureClassForWarningPolicy -eq 'performance' -or $firstAttemptFailureClassForWarningPolicy -eq 'environmental') -and
                -not [string]::IsNullOrWhiteSpace($rerunWarningCapExceededKinds) -and
                $rerunWarningCapExceededKinds -ne '(none)'
            if ($rerunIntroducedWarningPolicyEvidence) {
                $firstAttemptResult.attemptCount = $rerun + 1
                $firstAttemptResult.retryUsed = $true
                $firstAttemptResult.selectedAttempt = 1
                $firstAttemptResult.firstFailureReason = $firstFailureReason
                $firstAttemptResult.rerunArtifactDir = $rerunDir
                $firstAttemptResult.rerunFailureReason = ("rerun introduced warning_policy failure; preserving first-attempt evidence: warning cap exceeded: {0}" -f $rerunWarningCapExceededKinds)
                $result = $firstAttemptResult
                break
            }

            if ($rerunResult.failures.Count -ne 0) {
                $firstAttemptFailureClass = Get-RouteAcceptanceResultFailureClass -AcceptancePhase $AcceptancePhase -Result $firstAttemptResult
                $rerunFailureClass = Get-RouteAcceptanceResultFailureClass -AcceptancePhase $AcceptancePhase -Result $rerunResult
                if (($firstAttemptFailureClass -eq 'performance' -or $firstAttemptFailureClass -eq 'environmental') -and
                    $rerunFailureClass -ne 'performance' -and
                    $rerunFailureClass -ne 'environmental') {
                    $rerunFailureReason = if (-not [string]::IsNullOrWhiteSpace($rerunExecutionFailure)) {
                        "scenario rerun execution failed: {0}" -f $rerunExecutionFailure
                    }
                    else {
                        Get-Phase4FirstFailureReason -Result $rerunResult
                    }
                    if ([string]::IsNullOrWhiteSpace($rerunFailureReason)) {
                        $rerunFailureReason = 'rerun introduced non-performance evidence'
                    }

                    $firstAttemptResult.attemptCount = $rerun + 1
                    $firstAttemptResult.retryUsed = $true
                    $firstAttemptResult.selectedAttempt = 1
                    $firstAttemptResult.firstFailureReason = $firstFailureReason
                    $firstAttemptResult.rerunArtifactDir = $rerunDir
                    $firstAttemptResult.rerunFailureReason = ("rerun introduced {0} failure; preserving first-attempt evidence: {1}" -f $rerunFailureClass, $rerunFailureReason)
                    $result = $firstAttemptResult
                    break
                }
            }

            $result = $rerunResult
            if ($result.failures.Count -eq 0) {
                break
            }
        }
    }

    $script:RunResults.Add($result) | Out-Null
}

function New-RouteAcceptanceSummaryRunDto {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [ValidateSet('phase4', 'phase5')]
        [string]$AcceptancePhase = 'phase4'
    )

    $resultFailureLines = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $Result.failures -Limit 128)
    $resultPerformanceFailureCount = @($resultFailureLines | Where-Object { Test-Phase4PerformanceFailureLine -Line ([string]$_) }).Count
    $resultCorrectnessFailureCount = @($resultFailureLines | Where-Object { -not (Test-Phase4PerformanceFailureLine -Line ([string]$_)) }).Count

    return [pscustomobject]([ordered]@{
        name = [string]$Result.name
        artifactDir = [string]$Result.artifactDir
        expectedRoute = [string]$Result.expectedRoute
        expectedProtocol = [int]$Result.expectedProtocol
        finalRoute = [string]$Result.finalRoute
        protocol = [string]$Result.protocol
        selectedRouteSequence = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $Result.selectedRouteChanges -Limit 32)
        liveRouteEpochRouteChanges = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $Result.liveRouteEpochRouteChanges -Limit 32)
        routeConsistencyVerdict = [string]$Result.routeConsistencyVerdict
        liveRouteEpochProofVerdict = [string]$Result.liveRouteEpochProofVerdict
        fallbackLegAuthorityProofVerdict = [string]$Result.fallbackLegAuthorityProofVerdict
        cleanFallbackLegProofVerdict = [string]$Result.cleanFallbackLegProofVerdict
        bridgeLivenessIntegrationVerdict = [string]$Result.bridgeLivenessIntegrationVerdict
        controlPlaneIsolationVerdict = [string]$Result.controlPlaneIsolationVerdict
        fallbackTailBridgeRecoveryGenerationPairingVerdict = [string]$Result.fallbackTailBridgeRecoveryGenerationPairingVerdict
        fallbackTailBridgeRecoveryGenerationMismatchRawCount = [int]$Result.fallbackTailBridgeRecoveryGenerationMismatchRawCount
        fallbackTailBridgeRecoveryGenerationMismatchDistinctCount = [int]$Result.fallbackTailBridgeRecoveryGenerationMismatchDistinctCount
        fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount = [int]$Result.fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount
        fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount = [int]$Result.fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount
        fallbackTailTransportEpochReauthorizationVerdict = [string]$Result.fallbackTailTransportEpochReauthorizationVerdict
        fallbackTailTransportEpochReauthorizationMissingRawCount = [int]$Result.fallbackTailTransportEpochReauthorizationMissingRawCount
        fallbackTailTransportEpochReauthorizationMissingDistinctCount = [int]$Result.fallbackTailTransportEpochReauthorizationMissingDistinctCount
        fallbackTailTransportEpochReauthorizationMissingRecoveredCount = [int]$Result.fallbackTailTransportEpochReauthorizationMissingRecoveredCount
        fallbackTailTransportEpochReauthorizationMissingUnresolvedCount = [int]$Result.fallbackTailTransportEpochReauthorizationMissingUnresolvedCount
        runtimeUnlockRegularV4RouteCommitSendDrainVerdict = [string]$Result.runtimeUnlockRegularV4RouteCommitSendDrainVerdict
        runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount = [int]$Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount
        runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount = [int]$Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount
        runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount = [int]$Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount
        runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount = [int]$Result.runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount
        fileTransferReceiveRecoveryOperationVerdict = [string]$Result.fileTransferReceiveRecoveryOperationVerdict
        fileTransferReceiveRecoveryOperationRawCount = [int]$Result.fileTransferReceiveRecoveryOperationRawCount
        fileTransferReceiveRecoveryOperationDistinctCount = [int]$Result.fileTransferReceiveRecoveryOperationDistinctCount
        fileTransferReceiveRecoveryOperationRecoveredCount = [int]$Result.fileTransferReceiveRecoveryOperationRecoveredCount
        fileTransferReceiveRecoveryOperationUnresolvedCount = [int]$Result.fileTransferReceiveRecoveryOperationUnresolvedCount
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount = [int]$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount = [int]$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount = [int]$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount = [int]$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorVerdict = [string]$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorVerdict
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount = [int]$Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount = [int]$Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount = [int]$Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount = [int]$Result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount
        fileTransferReceiveRecoveryCompletedProvenSuccessorVerdict = [string]$Result.fileTransferReceiveRecoveryCompletedProvenSuccessorVerdict
        regularV4FrontierRepairStalledRawCount = [int]$Result.regularV4FrontierRepairStalledRawCount
        regularV4FrontierRepairStalledDistinctCount = [int]$Result.regularV4FrontierRepairStalledDistinctCount
        regularV4FrontierRepairStalledRecoveredCount = [int]$Result.regularV4FrontierRepairStalledRecoveredCount
        regularV4FrontierRepairStalledUnresolvedCount = [int]$Result.regularV4FrontierRepairStalledUnresolvedCount
        regularV4FrontierRepairStalledVerdict = [string]$Result.regularV4FrontierRepairStalledVerdict
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRawCount = [int]$Result.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRawCount
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount = [int]$Result.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRecoveredCount = [int]$Result.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRecoveredCount
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount = [int]$Result.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount
        runtimeUnlockInitialOfferRetryLifecycleHandoffVerdict = [string]$Result.runtimeUnlockInitialOfferRetryLifecycleHandoffVerdict
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRawCount = [int]$Result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRawCount
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount = [int]$Result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRecoveredCount = [int]$Result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRecoveredCount
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount = [int]$Result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedVerdict = [string]$Result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedVerdict
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount = [int]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount = [int]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount = [int]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount = [int]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryVerdict = [string]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryVerdict
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount = [int]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount = [int]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount = [int]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount = [int]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredVerdict = [string]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredVerdict
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRawCount = [int]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRawCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount = [int]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRecoveredCount = [int]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRecoveredCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount = [int]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredVerdict = [string]$Result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredVerdict
        operatorVerdict = [string]$Result.operatorVerdict
        hardFailureCount = [int]$Result.hardFailureCount
        warningCount = [int]$Result.warningCount
        warningKinds = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $Result.warningKinds -Limit 32)
        warningCapExceededKinds = [string]$Result.warningCapExceededKinds
        environmentalClassification = [string]$Result.environmentalClassification
        manifestPresent = [bool]$Result.manifestPresent
        manifestPath = [string]$Result.manifestPath
        manifestSha256 = [string]$Result.manifestSha256
        manifestValidationVerdict = [string]$Result.manifestValidationVerdict
        provenanceVerdict = [string]$Result.provenanceVerdict
        provenanceFailureReasons = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $Result.provenanceFailureReasons -Limit 32)
        cursorIntegrityVerdict = [string]$Result.cursorIntegrityVerdict
        writerEvidenceVersion = [long]$Result.writerEvidenceVersion
        writerIntegrityVerdict = [string]$Result.writerIntegrityVerdict
        writerGapCount = [long]$Result.writerGapCount
        writerDuplicateCount = [long]$Result.writerDuplicateCount
        writerIdentityMismatchCount = [long]$Result.writerIdentityMismatchCount
        writerWriteFailureCount = [long]$Result.writerWriteFailureCount
        writerDeferredBatchCount = [long]$Result.writerDeferredBatchCount
        writerRecoveredBatchCount = [long]$Result.writerRecoveredBatchCount
        writerUnresolvedDeferredBatchCount = [long]$Result.writerUnresolvedDeferredBatchCount
        writerTerminalFailureCount = [long]$Result.writerTerminalFailureCount
        writerRecoveryMismatchCount = [long]$Result.writerRecoveryMismatchCount
        writerChronologyInversionCount = [long]$Result.writerChronologyInversionCount
        writerChronologyCollisionCount = [long]$Result.writerChronologyCollisionCount
        writerDrainMissingCount = [long]$Result.writerDrainMissingCount
        writerRecoveryVerdict = [string]$Result.writerRecoveryVerdict
        writerChronologyVerdict = [string]$Result.writerChronologyVerdict
        currentRuntimeProof = [bool]$Result.currentRuntimeProof
        currentRuntimeProofReasons = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $Result.currentRuntimeProofReasons -Limit 32)
        transferEvidenceStatus = [string]$Result.transferEvidenceStatus
        runtimeCorrectnessVerdict = [string]$Result.runtimeCorrectnessVerdict
        runtimeFailureCandidate = [string]$Result.runtimeFailureCandidate
        runtimeUnlockRetryDispatchAuthorityGrantedRawCount = [int]$Result.runtimeUnlockRetryDispatchAuthorityGrantedRawCount
        runtimeUnlockRetryDispatchAuthorityGrantedCount = [int]$Result.runtimeUnlockRetryDispatchAuthorityGrantedCount
        runtimeUnlockRetryDispatchPostGrantHoldCount = [int]$Result.runtimeUnlockRetryDispatchPostGrantHoldCount
        runtimeUnlockRetryDispatchPostGrantHoldRawCount = [int]$Result.runtimeUnlockRetryDispatchPostGrantHoldRawCount
        runtimeUnlockRetryDispatchPostGrantHoldDistinctCount = [int]$Result.runtimeUnlockRetryDispatchPostGrantHoldDistinctCount
        runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount = [int]$Result.runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount
        runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount = [int]$Result.runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount
        runtimeUnlockRetryDispatchPostGrantHoldVerdict = [string]$Result.runtimeUnlockRetryDispatchPostGrantHoldVerdict
        runtimeUnlockPreCommitProbeStartHandoffMissingRawCount = [int]$Result.runtimeUnlockPreCommitProbeStartHandoffMissingRawCount
        runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount = [int]$Result.runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount
        runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount = [int]$Result.runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount
        runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount = [int]$Result.runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount
        runtimeUnlockPreCommitProbeStartHandoffVerdict = [string]$Result.runtimeUnlockPreCommitProbeStartHandoffVerdict
        runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount = [int]$Result.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount
        runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount = [int]$Result.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount
        runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount = [int]$Result.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount
        runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount = [int]$Result.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount
        runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict = [string]$Result.runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict
        runtimeUnlockRetryRegularV4RecoverySettlementMissingRawCount = [int]$Result.runtimeUnlockRetryRegularV4RecoverySettlementMissingRawCount
        runtimeUnlockRetryRegularV4RecoverySettlementMissingDistinctCount = [int]$Result.runtimeUnlockRetryRegularV4RecoverySettlementMissingDistinctCount
        runtimeUnlockRetryRegularV4RecoverySettlementMissingRecoveredCount = [int]$Result.runtimeUnlockRetryRegularV4RecoverySettlementMissingRecoveredCount
        runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount = [int]$Result.runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount
        runtimeUnlockRetryRegularV4RecoverySettlementVerdict = [string]$Result.runtimeUnlockRetryRegularV4RecoverySettlementVerdict
        runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount = [int]$Result.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount
        runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount = [int]$Result.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount
        runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount = [int]$Result.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount
        runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount = [int]$Result.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount
        runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict = [string]$Result.runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount = [int]$Result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount = [int]$Result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount = [int]$Result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount = [int]$Result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict = [string]$Result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict
        regularV4FrontierRepairMissingRangeNotPreservedRawCount = [int]$Result.regularV4FrontierRepairMissingRangeNotPreservedRawCount
        regularV4FrontierRepairMissingRangeNotPreservedDistinctCount = [int]$Result.regularV4FrontierRepairMissingRangeNotPreservedDistinctCount
        regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount = [int]$Result.regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount
        regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount = [int]$Result.regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount
        regularV4FrontierRepairMissingRangePreservationVerdict = [string]$Result.regularV4FrontierRepairMissingRangePreservationVerdict
        regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount = [int]$Result.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount
        regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount = [int]$Result.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount
        regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount = [int]$Result.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount
        regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount = [int]$Result.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount
        regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict = [string]$Result.regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict
        regularV4FrontierRepairSuccessorPressureInstallMissingRawCount = [int]$Result.regularV4FrontierRepairSuccessorPressureInstallMissingRawCount
        regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount = [int]$Result.regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount
        regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount = [int]$Result.regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount
        regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount = [int]$Result.regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount
        regularV4FrontierRepairSuccessorPressureInstallVerdict = [string]$Result.regularV4FrontierRepairSuccessorPressureInstallVerdict
        regularV4FrontierRepairSettlementDispositionUnverifiableRawCount = [int]$Result.regularV4FrontierRepairSettlementDispositionUnverifiableRawCount
        regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount = [int]$Result.regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount
        regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount = [int]$Result.regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount
        regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount = [int]$Result.regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount
        regularV4FrontierRepairSettlementDispositionVerdict = [string]$Result.regularV4FrontierRepairSettlementDispositionVerdict
        regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount = [int]$Result.regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount
        regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount = [int]$Result.regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount
        regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount = [int]$Result.regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount
        regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount = [int]$Result.regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount
        regularV4FrontierRepairPressureAuthorityLifecycleSplitVerdict = [string]$Result.regularV4FrontierRepairPressureAuthorityLifecycleSplitVerdict
        regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount = [int]$Result.regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount
        regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount = [int]$Result.regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount
        regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount = [int]$Result.regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount
        regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount = [int]$Result.regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount
        regularV4FrontierRepairPressureRecoveryHandoffVerdict = [string]$Result.regularV4FrontierRepairPressureRecoveryHandoffVerdict
        regularV4FrontierRepairProjectionReauthorizationMissingRawCount = [int]$Result.regularV4FrontierRepairProjectionReauthorizationMissingRawCount
        regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount = [int]$Result.regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount
        regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount = [int]$Result.regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount
        regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount = [int]$Result.regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount
        regularV4FrontierRepairProjectionReauthorizationVerdict = [string]$Result.regularV4FrontierRepairProjectionReauthorizationVerdict
        regularV4FrontierTailRepairStatePublicationMissingRawCount = [int]$Result.regularV4FrontierTailRepairStatePublicationMissingRawCount
        regularV4FrontierTailRepairStatePublicationMissingDistinctCount = [int]$Result.regularV4FrontierTailRepairStatePublicationMissingDistinctCount
        regularV4FrontierTailRepairStatePublicationMissingRecoveredCount = [int]$Result.regularV4FrontierTailRepairStatePublicationMissingRecoveredCount
        regularV4FrontierTailRepairStatePublicationMissingUnresolvedCount = [int]$Result.regularV4FrontierTailRepairStatePublicationMissingUnresolvedCount
        regularV4FrontierTailRepairStatePublicationVerdict = [string]$Result.regularV4FrontierTailRepairStatePublicationVerdict
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount = [int]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount = [int]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount = [int]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount = [int]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict = [string]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRawCount = [int]$Result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRawCount
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount = [int]$Result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRecoveredCount = [int]$Result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRecoveredCount
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount = [int]$Result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffVerdict = [string]$Result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffVerdict
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount = [int]$Result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount = [int]$Result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount = [int]$Result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount = [int]$Result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationVerdict = [string]$Result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationVerdict
        fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount = [int]$Result.fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount
        fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount = [int]$Result.fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount
        fileTransferBridgeRecoveryExecutionPayloadHoldMissingRecoveredCount = [int]$Result.fileTransferBridgeRecoveryExecutionPayloadHoldMissingRecoveredCount
        fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount = [int]$Result.fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRawCount = [int]$Result.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRawCount
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount = [int]$Result.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRecoveredCount = [int]$Result.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRecoveredCount
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount = [int]$Result.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRawCount = [int]$Result.fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRawCount
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount = [int]$Result.fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRecoveredCount = [int]$Result.fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRecoveredCount
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount = [int]$Result.fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount
        fileTransferBridgeRecoveryExecutionPayloadHoldVerdict = [string]$Result.fileTransferBridgeRecoveryExecutionPayloadHoldVerdict
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount = [int]$Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount = [int]$Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRecoveredCount = [int]$Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRecoveredCount
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount = [int]$Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainVerdict = [string]$Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainVerdict
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount = [int]$Result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount = [int]$Result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationRecoveredCount = [int]$Result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationRecoveredCount
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount = [int]$Result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationVerdict = [string]$Result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationVerdict
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRawCount = [int]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRawCount
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedDistinctCount = [int]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedDistinctCount
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRecoveredCount = [int]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRecoveredCount
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedUnresolvedCount = [int]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedUnresolvedCount
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldPreservationVerdict = [string]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldPreservationVerdict
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRawCount = [int]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRawCount
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingDistinctCount = [int]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingDistinctCount
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRecoveredCount = [int]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRecoveredCount
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingUnresolvedCount = [int]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingUnresolvedCount
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffVerdict = [string]$Result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffVerdict
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckRequestedCount = [int]$Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckRequestedCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDecidedCount = [int]$Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDecidedCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDiscardedCount = [int]$Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDiscardedCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount = [int]$Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingDistinctCount = [int]$Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingDistinctCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRecoveredCount = [int]$Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRecoveredCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount = [int]$Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict = [string]$Result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict
        runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRawCount = [int]$Result.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRawCount
        runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedDistinctCount = [int]$Result.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedDistinctCount
        runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRecoveredCount = [int]$Result.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRecoveredCount
        runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedUnresolvedCount = [int]$Result.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedUnresolvedCount
        runtimeUnlockRetryDispatchZeroOfferSuccessorVerdict = [string]$Result.runtimeUnlockRetryDispatchZeroOfferSuccessorVerdict
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount = [int]$Result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount = [int]$Result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount = [int]$Result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount = [int]$Result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict = [string]$Result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict
        firstProofChainFailureClass = [string]$Result.firstProofChainFailureClass
        firstProofChainFailureEvent = [string]$Result.firstProofChainFailureEvent
        firstProofChainFailureReason = [string]$Result.firstProofChainFailureReason
        firstProofChainFailureSequence = [long]$Result.firstProofChainFailureSequence
        firstProofChainFailureSource = [string]$Result.firstProofChainFailureSource
        firstProofChainFailureWriterSequence = [long]$Result.firstProofChainFailureWriterSequence
        semanticFailureDomain = [string]$Result.semanticFailureDomain
        evidenceClassification = [string]$Result.evidenceClassification
        measurementContaminated = [bool]$Result.measurementContaminated
        measurementContaminationReasons = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $Result.measurementContaminationReasons -Limit 32)
        completed = [bool]$Result.completed
        integrityOk = [bool]$Result.integrityOk
        shaOk = [bool]$Result.shaOk
        goodputBytesPerSecond = [double](ConvertTo-RouteAcceptanceDouble -Value $Result.goodputBytesPerSecond)
        baselineGoodputBytesPerSecond = [double](ConvertTo-RouteAcceptanceDouble -Value $Result.baselineGoodputBytesPerSecond)
        goodputRegressionFloorBytesPerSecond = [double](ConvertTo-RouteAcceptanceDouble -Value $Result.goodputRegressionFloorBytesPerSecond)
        goodputRegressionPercent = [double](ConvertTo-RouteAcceptanceDouble -Value $Result.goodputRegressionPercent)
        attemptCount = [int]$Result.attemptCount
        retryUsed = [bool]$Result.retryUsed
        selectedAttempt = [int]$Result.selectedAttempt
        scenarioIsolationPreflightCount = [int]$Result.scenarioIsolationPreflightCount
        scenarioIsolationCleanWindowPassed = [int]$Result.scenarioIsolationCleanWindowPassed
        scenarioIsolationFailureReason = [string]$Result.scenarioIsolationFailureReason
        scenarioIsolationArtifactDirs = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $Result.scenarioIsolationArtifactDirs -Limit 16)
        firstFailureReason = [string]$Result.firstFailureReason
        rerunArtifactDir = [string]$Result.rerunArtifactDir
        rerunFailureReason = [string]$Result.rerunFailureReason
        setupFailurePhase = [string]$Result.setupFailurePhase
        setupFailureReason = [string]$Result.setupFailureReason
        guiReportedFailurePhase = [string]$Result.guiReportedFailurePhase
        guiReportedFailureReason = [string]$Result.guiReportedFailureReason
        guiReportedFailureError = [string]$Result.guiReportedFailureError
        fallbackReceiverStateDeferredCount = [int]$Result.fallbackReceiverStateDeferredCount
        fallbackReceiverStateCoalescedCount = [int]$Result.fallbackReceiverStateCoalescedCount
        fallbackReceiverStateChurnWarningCount = [int]$Result.fallbackReceiverStateChurnWarningCount
        fallbackReceiverStateChurnRawEventCount = [int]$Result.fallbackReceiverStateChurnRawEventCount
        fallbackReceiverStateChurnEfficiencyClass = [string]$Result.fallbackReceiverStateChurnEfficiencyClass
        correctnessFailureCount = [int]$resultCorrectnessFailureCount
        performanceFailureCount = [int]$resultPerformanceFailureCount
        acceptanceFailureClass = [string](Get-RouteAcceptanceResultFailureClass -AcceptancePhase $AcceptancePhase -Result $Result)
        failures = @($resultFailureLines)
    })
}

function Write-Phase4RouteAcceptanceSummaryFiles {
    param(
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)][string]$BaselinePath,
        [ValidateSet('phase4', 'phase5')]
        [string]$AcceptancePhase = 'phase4',
        # Default output remains phase4-ab-acceptance-summary.txt for Phase 4 compatibility.
        # Phase 5 writes phase5-analyzer-gui-acceptance-summary.txt.
        [string]$SummaryBaseName = 'phase4-ab-acceptance',
        [string]$SummaryTitle = 'Phase 4 File Transfer A/B Acceptance',
        [int]$ExpectedRunCount = 7,
        [string[]]$RequestedScenarios = @()
    )

    $summaryStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $evidence = Get-RouteAcceptanceEvidenceSummaryState
    $diagnostics = [ordered]@{
        event = 'phase5_summary_writer_diagnostics'
        acceptancePhase = $AcceptancePhase
        summaryBaseName = $SummaryBaseName
        artifactRoot = $RunRoot
        requestedScenarios = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $RequestedScenarios -Limit 32)
        expectedRunCount = $ExpectedRunCount
        observedRunCount = $script:RunResults.Count
        dtoBuildMs = 0
        textWriteMs = 0
        varianceNoteWriteMs = 0
        jsonWriteMs = 0
        finalVerdict = '(pending)'
        summaryJsonFailed = $false
        summaryJsonFailureReason = ''
    }

    $dtoStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $runDtos = @(
        foreach ($result in $script:RunResults) {
            New-RouteAcceptanceSummaryRunDto -Result $result -AcceptancePhase $AcceptancePhase
        }
    )
    $dtoStopwatch.Stop()
    $diagnostics.dtoBuildMs = [long]$dtoStopwatch.ElapsedMilliseconds

    $transferEvidenceStatus = if ($runDtos.Count -eq 0) {
        'not_started'
    }
    elseif (@($runDtos | Where-Object { [string]$_.transferEvidenceStatus -ne 'complete' }).Count -gt 0) {
        'incomplete'
    }
    else {
        'complete'
    }
    $cursorIntegrityVerdict = if ($runDtos.Count -eq 0) {
        'missing'
    }
    elseif (@($runDtos | Where-Object { [string]$_.cursorIntegrityVerdict -ne 'pass' }).Count -gt 0) {
        'fail'
    }
    else {
        'pass'
    }
    $writerIntegrityVerdict = if ($runDtos.Count -eq 0) {
        'missing'
    }
    elseif (@($runDtos | Where-Object { [string]$_.writerIntegrityVerdict -ne 'pass' }).Count -gt 0) {
        'fail'
    }
    else {
        'pass'
    }
    $writerEvidenceVersion = if ($runDtos.Count -eq 0) {
        0L
    }
    else {
        [long](($runDtos | Measure-Object -Property writerEvidenceVersion -Minimum).Minimum)
    }
    $writerGapCount = [long](($runDtos | Measure-Object -Property writerGapCount -Sum).Sum)
    $writerDuplicateCount = [long](($runDtos | Measure-Object -Property writerDuplicateCount -Sum).Sum)
    $writerIdentityMismatchCount = [long](($runDtos | Measure-Object -Property writerIdentityMismatchCount -Sum).Sum)
    $writerWriteFailureCount = [long](($runDtos | Measure-Object -Property writerWriteFailureCount -Sum).Sum)
    $writerDeferredBatchCount = [long](($runDtos | Measure-Object -Property writerDeferredBatchCount -Sum).Sum)
    $writerRecoveredBatchCount = [long](($runDtos | Measure-Object -Property writerRecoveredBatchCount -Sum).Sum)
    $writerUnresolvedDeferredBatchCount = [long](($runDtos | Measure-Object -Property writerUnresolvedDeferredBatchCount -Sum).Sum)
    $writerTerminalFailureCount = [long](($runDtos | Measure-Object -Property writerTerminalFailureCount -Sum).Sum)
    $writerRecoveryMismatchCount = [long](($runDtos | Measure-Object -Property writerRecoveryMismatchCount -Sum).Sum)
    $writerChronologyInversionCount = [long](($runDtos | Measure-Object -Property writerChronologyInversionCount -Sum).Sum)
    $writerChronologyCollisionCount = [long](($runDtos | Measure-Object -Property writerChronologyCollisionCount -Sum).Sum)
    $writerDrainMissingCount = [long](($runDtos | Measure-Object -Property writerDrainMissingCount -Sum).Sum)
    $writerRecoveryVerdict = if ($runDtos.Count -eq 0) { 'missing' } elseif (@($runDtos | Where-Object { $_.writerRecoveryVerdict -ne 'pass' }).Count -gt 0) { 'fail' } else { 'pass' }
    $writerChronologyVerdict = if ($runDtos.Count -eq 0) { 'missing' } elseif (@($runDtos | Where-Object { $_.writerChronologyVerdict -ne 'pass' }).Count -gt 0) { 'fail' } else { 'pass' }
    $runtimeCandidateDtos = @($runDtos | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.runtimeFailureCandidate) -and
        [string]$_.runtimeFailureCandidate -notin @('(none)', 'none')
    } | Sort-Object @{ Expression = { if ([long]$_.firstProofChainFailureSequence -gt 0) { [long]$_.firstProofChainFailureSequence } else { [long]::MaxValue } } })
    $runtimeFailureCandidate = if ($runtimeCandidateDtos.Count -gt 0) {
        [string]$runtimeCandidateDtos[0].runtimeFailureCandidate
    }
    else {
        '(none)'
    }
    $firstProofChainFailureClass = if ($runtimeCandidateDtos.Count -gt 0) { [string]$runtimeCandidateDtos[0].firstProofChainFailureClass } else { '(none)' }
    $firstProofChainFailureEvent = if ($runtimeCandidateDtos.Count -gt 0) { [string]$runtimeCandidateDtos[0].firstProofChainFailureEvent } else { '(none)' }
    $firstProofChainFailureReason = if ($runtimeCandidateDtos.Count -gt 0) { [string]$runtimeCandidateDtos[0].firstProofChainFailureReason } else { '(none)' }
    $firstProofChainFailureSequence = if ($runtimeCandidateDtos.Count -gt 0) { [long]$runtimeCandidateDtos[0].firstProofChainFailureSequence } else { 0L }
    $firstProofChainFailureSource = if ($runtimeCandidateDtos.Count -gt 0) { [string]$runtimeCandidateDtos[0].firstProofChainFailureSource } else { '(none)' }
    $firstProofChainFailureWriterSequence = if ($runtimeCandidateDtos.Count -gt 0) { [long]$runtimeCandidateDtos[0].firstProofChainFailureWriterSequence } else { 0L }
    $semanticFailureDomain = if ($runtimeCandidateDtos.Count -gt 0) { [string]$runtimeCandidateDtos[0].semanticFailureDomain } else { '(none)' }
    $fallbackTailBridgeRecoveryGenerationMismatchRawCount = 0
    $fallbackTailBridgeRecoveryGenerationMismatchDistinctCount = 0
    $fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount = 0
    $fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount = 0
    $fallbackTailTransportEpochReauthorizationMissingRawCount = 0
    $fallbackTailTransportEpochReauthorizationMissingDistinctCount = 0
    $fallbackTailTransportEpochReauthorizationMissingRecoveredCount = 0
    $fallbackTailTransportEpochReauthorizationMissingUnresolvedCount = 0
    $runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount = 0
    $runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount = 0
    $runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount = 0
    $runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount = 0
    $fileTransferReceiveRecoveryOperationRawCount = 0
    $fileTransferReceiveRecoveryOperationDistinctCount = 0
    $fileTransferReceiveRecoveryOperationRecoveredCount = 0
    $fileTransferReceiveRecoveryOperationUnresolvedCount = 0
    $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount = 0
    $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount = 0
    $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount = 0
    $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount = 0
    $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount = 0
    $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount = 0
    $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount = 0
    $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount = 0
    $regularV4FrontierRepairStalledRawCount = 0
    $regularV4FrontierRepairStalledDistinctCount = 0
    $regularV4FrontierRepairStalledRecoveredCount = 0
    $regularV4FrontierRepairStalledUnresolvedCount = 0
    $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRawCount = 0
    $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount = 0
    $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRecoveredCount = 0
    $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount = 0
    $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRawCount = 0
    $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount = 0
    $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRecoveredCount = 0
    $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount = 0
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount = 0
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount = 0
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount = 0
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount = 0
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount = 0
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount = 0
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount = 0
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount = 0
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRawCount = 0
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount = 0
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRecoveredCount = 0
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount = 0
    $regularV4FrontierRepairMissingRangeNotPreservedRawCount = 0
    $regularV4FrontierRepairMissingRangeNotPreservedDistinctCount = 0
    $regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount = 0
    $regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount = 0
    $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount = 0
    $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount = 0
    $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount = 0
    $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount = 0
    $regularV4FrontierRepairSuccessorPressureInstallMissingRawCount = 0
    $regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount = 0
    $regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount = 0
    $regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount = 0
    $regularV4FrontierRepairSettlementDispositionUnverifiableRawCount = 0
    $regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount = 0
    $regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount = 0
    $regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount = 0
    $regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount = 0
    $regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount = 0
    $regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount = 0
    $regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount = 0
    $regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount = 0
    $regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount = 0
    $regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount = 0
    $regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount = 0
    $regularV4FrontierRepairProjectionReauthorizationMissingRawCount = 0
    $regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount = 0
    $regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount = 0
    $regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount = 0
    $regularV4FrontierTailRepairStatePublicationMissingRawCount = 0
    $regularV4FrontierTailRepairStatePublicationMissingDistinctCount = 0
    $regularV4FrontierTailRepairStatePublicationMissingRecoveredCount = 0
    $regularV4FrontierTailRepairStatePublicationMissingUnresolvedCount = 0
    $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount = 0
    $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount = 0
    $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount = 0
    $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount = 0
    $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRawCount = 0
    $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount = 0
    $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRecoveredCount = 0
    $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount = 0
    $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount = 0
    $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount = 0
    $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount = 0
    $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount = 0
    $fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount = 0
    $fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount = 0
    $fileTransferBridgeRecoveryExecutionPayloadHoldMissingRecoveredCount = 0
    $fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount = 0
    $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRawCount = 0
    $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount = 0
    $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRecoveredCount = 0
    $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount = 0
    $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRawCount = 0
    $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount = 0
    $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRecoveredCount = 0
    $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount = 0
    $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount = 0
    $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount = 0
    $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRecoveredCount = 0
    $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount = 0
    $fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount = 0
    $fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount = 0
    $fileTransferRouteTransitionDrainingTransportGraceTerminalizationRecoveredCount = 0
    $fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount = 0
    $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRawCount = 0
    $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedDistinctCount = 0
    $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRecoveredCount = 0
    $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedUnresolvedCount = 0
    $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRawCount = 0
    $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingDistinctCount = 0
    $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRecoveredCount = 0
    $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingUnresolvedCount = 0
    $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckRequestedCount = 0
    $runtimeUnlockRetryDispatchAuthorityGrantedRawCount = 0
    $runtimeUnlockRetryDispatchAuthorityGrantedCount = 0
    $runtimeUnlockRetryDispatchPostGrantHoldCount = 0
    $runtimeUnlockRetryDispatchPostGrantHoldRawCount = 0
    $runtimeUnlockRetryDispatchPostGrantHoldDistinctCount = 0
    $runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount = 0
    $runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount = 0
    $runtimeUnlockPreCommitProbeStartHandoffMissingRawCount = 0
    $runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount = 0
    $runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount = 0
    $runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount = 0
    $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount = 0
    $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount = 0
    $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount = 0
    $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount = 0
    $runtimeUnlockRetryRegularV4RecoverySettlementMissingRawCount = 0
    $runtimeUnlockRetryRegularV4RecoverySettlementMissingDistinctCount = 0
    $runtimeUnlockRetryRegularV4RecoverySettlementMissingRecoveredCount = 0
    $runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount = 0
    $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount = 0
    $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount = 0
    $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount = 0
    $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount = 0
    $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount = 0
    $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount = 0
    $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount = 0
    $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount = 0
    $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDecidedCount = 0
    $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDiscardedCount = 0
    $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount = 0
    $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingDistinctCount = 0
    $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRecoveredCount = 0
    $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount = 0
    $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRawCount = 0
    $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedDistinctCount = 0
    $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRecoveredCount = 0
    $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedUnresolvedCount = 0
    $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount = 0
    $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount = 0
    $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount = 0
    $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount = 0
    foreach ($runDto in $runDtos) {
        $runtimeUnlockRetryDispatchAuthorityGrantedRawCount += [int]$runDto.runtimeUnlockRetryDispatchAuthorityGrantedRawCount
        $runtimeUnlockRetryDispatchAuthorityGrantedCount += [int]$runDto.runtimeUnlockRetryDispatchAuthorityGrantedCount
        $runtimeUnlockRetryDispatchPostGrantHoldCount += [int]$runDto.runtimeUnlockRetryDispatchPostGrantHoldCount
        $runtimeUnlockRetryDispatchPostGrantHoldRawCount += [int]$runDto.runtimeUnlockRetryDispatchPostGrantHoldRawCount
        $runtimeUnlockRetryDispatchPostGrantHoldDistinctCount += [int]$runDto.runtimeUnlockRetryDispatchPostGrantHoldDistinctCount
        $runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount += [int]$runDto.runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount
        $runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount += [int]$runDto.runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount
        $runtimeUnlockPreCommitProbeStartHandoffMissingRawCount += [int]$runDto.runtimeUnlockPreCommitProbeStartHandoffMissingRawCount
        $runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount += [int]$runDto.runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount
        $runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount += [int]$runDto.runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount
        $runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount += [int]$runDto.runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount
        $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount += [int]$runDto.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount
        $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount += [int]$runDto.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount
        $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount += [int]$runDto.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount
        $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount += [int]$runDto.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount
        $runtimeUnlockRetryRegularV4RecoverySettlementMissingRawCount += [int]$runDto.runtimeUnlockRetryRegularV4RecoverySettlementMissingRawCount
        $runtimeUnlockRetryRegularV4RecoverySettlementMissingDistinctCount += [int]$runDto.runtimeUnlockRetryRegularV4RecoverySettlementMissingDistinctCount
        $runtimeUnlockRetryRegularV4RecoverySettlementMissingRecoveredCount += [int]$runDto.runtimeUnlockRetryRegularV4RecoverySettlementMissingRecoveredCount
        $runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount += [int]$runDto.runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount
        $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount += [int]$runDto.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount
        $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount += [int]$runDto.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount
        $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount += [int]$runDto.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount
        $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount += [int]$runDto.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount
        $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount += [int]$runDto.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount
        $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount += [int]$runDto.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount
        $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount += [int]$runDto.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount
        $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount += [int]$runDto.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount
        $fallbackTailBridgeRecoveryGenerationMismatchRawCount += [int]$runDto.fallbackTailBridgeRecoveryGenerationMismatchRawCount
        $fallbackTailBridgeRecoveryGenerationMismatchDistinctCount += [int]$runDto.fallbackTailBridgeRecoveryGenerationMismatchDistinctCount
        $fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount += [int]$runDto.fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount
        $fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount += [int]$runDto.fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount
        $fallbackTailTransportEpochReauthorizationMissingRawCount += [int]$runDto.fallbackTailTransportEpochReauthorizationMissingRawCount
        $fallbackTailTransportEpochReauthorizationMissingDistinctCount += [int]$runDto.fallbackTailTransportEpochReauthorizationMissingDistinctCount
        $fallbackTailTransportEpochReauthorizationMissingRecoveredCount += [int]$runDto.fallbackTailTransportEpochReauthorizationMissingRecoveredCount
        $fallbackTailTransportEpochReauthorizationMissingUnresolvedCount += [int]$runDto.fallbackTailTransportEpochReauthorizationMissingUnresolvedCount
        $runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount += [int]$runDto.runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount
        $runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount += [int]$runDto.runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount
        $runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount += [int]$runDto.runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount
        $runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount += [int]$runDto.runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount
        $fileTransferReceiveRecoveryOperationRawCount += [int]$runDto.fileTransferReceiveRecoveryOperationRawCount
        $fileTransferReceiveRecoveryOperationDistinctCount += [int]$runDto.fileTransferReceiveRecoveryOperationDistinctCount
        $fileTransferReceiveRecoveryOperationRecoveredCount += [int]$runDto.fileTransferReceiveRecoveryOperationRecoveredCount
        $fileTransferReceiveRecoveryOperationUnresolvedCount += [int]$runDto.fileTransferReceiveRecoveryOperationUnresolvedCount
        $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount += [int]$runDto.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount
        $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount += [int]$runDto.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount
        $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount += [int]$runDto.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount
        $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount += [int]$runDto.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount
        $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount += [int]$runDto.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount
        $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount += [int]$runDto.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount
        $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount += [int]$runDto.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount
        $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount += [int]$runDto.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount
        $regularV4FrontierRepairStalledRawCount += [int]$runDto.regularV4FrontierRepairStalledRawCount
        $regularV4FrontierRepairStalledDistinctCount += [int]$runDto.regularV4FrontierRepairStalledDistinctCount
        $regularV4FrontierRepairStalledRecoveredCount += [int]$runDto.regularV4FrontierRepairStalledRecoveredCount
        $regularV4FrontierRepairStalledUnresolvedCount += [int]$runDto.regularV4FrontierRepairStalledUnresolvedCount
        $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRawCount += [int]$runDto.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRawCount
        $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount += [int]$runDto.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount
        $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRecoveredCount += [int]$runDto.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRecoveredCount
        $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount += [int]$runDto.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount
        $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRawCount += [int]$runDto.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRawCount
        $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount += [int]$runDto.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount
        $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRecoveredCount += [int]$runDto.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRecoveredCount
        $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount += [int]$runDto.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount += [int]$runDto.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount += [int]$runDto.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount += [int]$runDto.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount += [int]$runDto.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount += [int]$runDto.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount += [int]$runDto.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount += [int]$runDto.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount += [int]$runDto.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRawCount += [int]$runDto.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRawCount
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount += [int]$runDto.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRecoveredCount += [int]$runDto.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRecoveredCount
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount += [int]$runDto.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount
        $regularV4FrontierRepairMissingRangeNotPreservedRawCount += [int]$runDto.regularV4FrontierRepairMissingRangeNotPreservedRawCount
        $regularV4FrontierRepairMissingRangeNotPreservedDistinctCount += [int]$runDto.regularV4FrontierRepairMissingRangeNotPreservedDistinctCount
        $regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount += [int]$runDto.regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount
        $regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount += [int]$runDto.regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount
        $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount += [int]$runDto.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount
        $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount += [int]$runDto.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount
        $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount += [int]$runDto.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount
        $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount += [int]$runDto.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount
        $regularV4FrontierRepairSuccessorPressureInstallMissingRawCount += [int]$runDto.regularV4FrontierRepairSuccessorPressureInstallMissingRawCount
        $regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount += [int]$runDto.regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount
        $regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount += [int]$runDto.regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount
        $regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount += [int]$runDto.regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount
        $regularV4FrontierRepairSettlementDispositionUnverifiableRawCount += [int]$runDto.regularV4FrontierRepairSettlementDispositionUnverifiableRawCount
        $regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount += [int]$runDto.regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount
        $regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount += [int]$runDto.regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount
        $regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount += [int]$runDto.regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount
        $regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount += [int]$runDto.regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount
        $regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount += [int]$runDto.regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount
        $regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount += [int]$runDto.regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount
        $regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount += [int]$runDto.regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount
        $regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount += [int]$runDto.regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount
        $regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount += [int]$runDto.regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount
        $regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount += [int]$runDto.regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount
        $regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount += [int]$runDto.regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount
        $regularV4FrontierRepairProjectionReauthorizationMissingRawCount += [int]$runDto.regularV4FrontierRepairProjectionReauthorizationMissingRawCount
        $regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount += [int]$runDto.regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount
        $regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount += [int]$runDto.regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount
        $regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount += [int]$runDto.regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount
        $regularV4FrontierTailRepairStatePublicationMissingRawCount += [int]$runDto.regularV4FrontierTailRepairStatePublicationMissingRawCount
        $regularV4FrontierTailRepairStatePublicationMissingDistinctCount += [int]$runDto.regularV4FrontierTailRepairStatePublicationMissingDistinctCount
        $regularV4FrontierTailRepairStatePublicationMissingRecoveredCount += [int]$runDto.regularV4FrontierTailRepairStatePublicationMissingRecoveredCount
        $regularV4FrontierTailRepairStatePublicationMissingUnresolvedCount += [int]$runDto.regularV4FrontierTailRepairStatePublicationMissingUnresolvedCount
        $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount += [int]$runDto.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount
        $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount += [int]$runDto.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount
        $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount += [int]$runDto.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount
        $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount += [int]$runDto.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount
        $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRawCount += [int]$runDto.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRawCount
        $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount += [int]$runDto.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount
        $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRecoveredCount += [int]$runDto.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRecoveredCount
        $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount += [int]$runDto.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount
        $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount += [int]$runDto.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount
        $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount += [int]$runDto.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount
        $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount += [int]$runDto.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount
        $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount += [int]$runDto.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount
        $fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount += [int]$runDto.fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount
        $fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount += [int]$runDto.fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount
        $fileTransferBridgeRecoveryExecutionPayloadHoldMissingRecoveredCount += [int]$runDto.fileTransferBridgeRecoveryExecutionPayloadHoldMissingRecoveredCount
        $fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount += [int]$runDto.fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount
        $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRawCount += [int]$runDto.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRawCount
        $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount += [int]$runDto.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount
        $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRecoveredCount += [int]$runDto.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRecoveredCount
        $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount += [int]$runDto.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount
        $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRawCount += [int]$runDto.fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRawCount
        $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount += [int]$runDto.fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount
        $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRecoveredCount += [int]$runDto.fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRecoveredCount
        $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount += [int]$runDto.fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount
        $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount += [int]$runDto.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount
        $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount += [int]$runDto.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount
        $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRecoveredCount += [int]$runDto.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRecoveredCount
        $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount += [int]$runDto.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount
        $fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount += [int]$runDto.fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount
        $fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount += [int]$runDto.fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount
        $fileTransferRouteTransitionDrainingTransportGraceTerminalizationRecoveredCount += [int]$runDto.fileTransferRouteTransitionDrainingTransportGraceTerminalizationRecoveredCount
        $fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount += [int]$runDto.fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount
        $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRawCount += [int]$runDto.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRawCount
        $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedDistinctCount += [int]$runDto.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedDistinctCount
        $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRecoveredCount += [int]$runDto.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRecoveredCount
        $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedUnresolvedCount += [int]$runDto.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedUnresolvedCount
        $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRawCount += [int]$runDto.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRawCount
        $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingDistinctCount += [int]$runDto.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingDistinctCount
        $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRecoveredCount += [int]$runDto.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRecoveredCount
        $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingUnresolvedCount += [int]$runDto.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingUnresolvedCount
        $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckRequestedCount += [int]$runDto.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckRequestedCount
        $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDecidedCount += [int]$runDto.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDecidedCount
        $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDiscardedCount += [int]$runDto.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDiscardedCount
        $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount += [int]$runDto.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount
        $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingDistinctCount += [int]$runDto.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingDistinctCount
        $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRecoveredCount += [int]$runDto.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRecoveredCount
        $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount += [int]$runDto.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount
        $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRawCount += [int]$runDto.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRawCount
        $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedDistinctCount += [int]$runDto.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedDistinctCount
        $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRecoveredCount += [int]$runDto.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRecoveredCount
        $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedUnresolvedCount += [int]$runDto.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedUnresolvedCount
        $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount += [int]$runDto.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount
        $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount += [int]$runDto.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount
        $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount += [int]$runDto.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount
        $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount += [int]$runDto.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount
    }
    $runtimeUnlockRegularV4RouteCommitSendDrainVerdict = if (
        $runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRegularV4RouteCommitSendDrainVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRegularV4RouteCommitSendDrainVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $regularV4FrontierRepairMissingRangePreservationVerdict = if (
        $regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.regularV4FrontierRepairMissingRangePreservationVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($regularV4FrontierRepairMissingRangeNotPreservedDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.regularV4FrontierRepairMissingRangePreservationVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict = if (
        $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $regularV4FrontierRepairSuccessorPressureInstallVerdict = if (
        $regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.regularV4FrontierRepairSuccessorPressureInstallVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.regularV4FrontierRepairSuccessorPressureInstallVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $regularV4FrontierRepairSettlementDispositionVerdict = if (
        $regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.regularV4FrontierRepairSettlementDispositionVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.regularV4FrontierRepairSettlementDispositionVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $regularV4FrontierRepairProjectionReauthorizationVerdict = if (
        $regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.regularV4FrontierRepairProjectionReauthorizationVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.regularV4FrontierRepairProjectionReauthorizationVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $regularV4FrontierTailRepairStatePublicationVerdict = if (
        $regularV4FrontierTailRepairStatePublicationMissingUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.regularV4FrontierTailRepairStatePublicationVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($regularV4FrontierTailRepairStatePublicationMissingDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.regularV4FrontierTailRepairStatePublicationVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict = if (
        $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $runtimeUnlockRegularV4RecoveryLeaseClosureHoldPreservationVerdict = if (
        $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRegularV4RecoveryLeaseClosureHoldPreservationVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRegularV4RecoveryLeaseClosureHoldPreservationVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffVerdict = if (
        $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $runtimeUnlockRetryDispatchPostGrantHoldVerdict = if (
        $runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRetryDispatchPostGrantHoldVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($runtimeUnlockRetryDispatchPostGrantHoldDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRetryDispatchPostGrantHoldVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $runtimeUnlockPreCommitProbeStartHandoffVerdict = if (
        $runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockPreCommitProbeStartHandoffVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockPreCommitProbeStartHandoffVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict = if (
        $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $runtimeUnlockRetryRegularV4RecoverySettlementVerdict = if (
        $runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRetryRegularV4RecoverySettlementVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif (@($runDtos | Where-Object { [string]$_.runtimeUnlockRetryRegularV4RecoverySettlementVerdict -eq 'pending' }).Count -gt 0) {
        'pending'
    }
    elseif (@($runDtos | Where-Object { [string]$_.runtimeUnlockRetryRegularV4RecoverySettlementVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict = if (
        $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif (@($runDtos | Where-Object { [string]$_.runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict -eq 'pending' }).Count -gt 0) {
        'pending'
    }
    elseif (@($runDtos | Where-Object { [string]$_.runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict = if (
        $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict = if (
        $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $runtimeUnlockRetryDispatchZeroOfferSuccessorVerdict = if (
        $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRetryDispatchZeroOfferSuccessorVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRetryDispatchZeroOfferSuccessorVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict = if (
        $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif ($runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $fallbackTailBridgeRecoveryGenerationPairingVerdict = if (
        $fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.fallbackTailBridgeRecoveryGenerationPairingVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif (
        $fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.fallbackTailBridgeRecoveryGenerationPairingVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }
    $fallbackTailTransportEpochReauthorizationVerdict = if (
        $fallbackTailTransportEpochReauthorizationMissingUnresolvedCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.fallbackTailTransportEpochReauthorizationVerdict -eq 'fail' }).Count -gt 0) {
        'fail'
    }
    elseif (
        $fallbackTailTransportEpochReauthorizationMissingDistinctCount -gt 0 -or
        @($runDtos | Where-Object { [string]$_.fallbackTailTransportEpochReauthorizationVerdict -eq 'pass' }).Count -gt 0) {
        'pass'
    }
    else {
        'none'
    }

    $failureLines = @()
    foreach ($result in $runDtos) {
        foreach ($failure in @($result.failures)) {
            $failureLines += ("{0}: {1}" -f $result.name, $failure)
        }
    }

    if ($AcceptancePhase -eq 'phase5' -and $script:RouteAcceptanceExecutionMode -ne 'fake') {
        foreach ($result in @($runDtos | Where-Object { [string]$_.acceptanceFailureClass -eq 'incomplete_proof' })) {
            $failureLines += ("{0}: incomplete runtime proof; manifest={1}; provenance={2}; cursor={3}; transfer={4}" -f
                $result.name,
                $result.manifestValidationVerdict,
                $result.provenanceVerdict,
                $result.cursorIntegrityVerdict,
                $result.transferEvidenceStatus)
        }
    }

    if ($script:RunResults.Count -ne $expectedRunCount) {
        $failureLines += ("expected {0} runs, observed {1}" -f $expectedRunCount, $script:RunResults.Count)
    }

    $performanceFailureLines = @($failureLines | Where-Object { Test-Phase4PerformanceFailureLine -Line ([string]$_) })
    $correctnessFailureLines = @($failureLines | Where-Object { -not (Test-Phase4PerformanceFailureLine -Line ([string]$_)) })
    $verdict = if ($AcceptancePhase -eq 'phase5') {
        if ($correctnessFailureLines.Count -eq 0 -and $script:RunResults.Count -eq $expectedRunCount) { 'PASS' } else { 'FAIL' }
    }
    else {
        if ($failureLines.Count -eq 0 -and $script:RunResults.Count -eq $expectedRunCount) { 'PASS' } else { 'FAIL' }
    }
    $correctnessVerdict = if ($correctnessFailureLines.Count -eq 0 -and $script:RunResults.Count -eq $expectedRunCount) { 'PASS' } else { 'FAIL' }
    $performanceVerdict = if ($performanceFailureLines.Count -eq 0) { 'PASS' } else { 'FAIL' }
    $conclusiveRouteRuntimeCandidate = Test-RouteAcceptanceConclusiveRouteRuntimeCandidate `
        -Candidate $runtimeFailureCandidate `
        -SemanticFailureDomain $semanticFailureDomain
    $runtimeCorrectnessVerdict = if ($AcceptancePhase -eq 'phase5' -and
        -not [bool]$evidence.CurrentRuntimeProof -and
        -not ($script:RouteAcceptanceExecutionMode -eq 'fake' -and $conclusiveRouteRuntimeCandidate)) {
        'incomplete_proof'
    }
    elseif ($AcceptancePhase -eq 'phase5' -and $conclusiveRouteRuntimeCandidate) {
        'fail'
    }
    elseif ($AcceptancePhase -eq 'phase5' -and
        ($transferEvidenceStatus -ne 'complete' -or
            (Test-RouteAcceptanceIncompleteProofRuntimeCandidate -Candidate $runtimeFailureCandidate))) {
        'incomplete_proof'
    }
    elseif ($correctnessVerdict -eq 'PASS' -and $runtimeFailureCandidate -eq '(none)') {
        'pass'
    }
    else {
        'fail'
    }
    $networkVarianceNote = if ($AcceptancePhase -eq 'phase5') {
        'Goodput on live transfer paths is classified separately from release correctness after strict route/protocol/SHA/hard-failure/warning proof passes; persistent goodput below the Phase 4 floor still fails performance verdict, but Phase 5 release acceptance is governed by correctness gates.'
    }
    else {
        'Goodput on live transfer paths is classified separately from runtime correctness only after strict route/protocol/SHA/hard-failure/warning proof passes; persistent goodput below the Phase 4 floor still fails performance acceptance and remains release-blocking unless a rerun proves environmental noise.'
    }
    $textLines = @(
        $SummaryTitle,
        ("verdict={0}" -f $verdict),
        ("correctness_verdict={0}" -f $correctnessVerdict),
        ("performance_verdict={0}" -f $performanceVerdict),
        ("acceptance_phase={0}" -f $AcceptancePhase),
        ("artifact_root={0}" -f $RunRoot),
        ("manifest_present={0}" -f ($(if ([bool]$evidence.ManifestPresent) { 1 } else { 0 }))),
        ("manifest_path={0}" -f ($(if ([string]::IsNullOrWhiteSpace([string]$evidence.ManifestPath)) { '(none)' } else { [string]$evidence.ManifestPath }))),
        ("manifest_sha256={0}" -f ($(if ([string]::IsNullOrWhiteSpace([string]$evidence.ManifestSha256)) { '(none)' } else { [string]$evidence.ManifestSha256 }))),
        ("manifest_validation_verdict={0}" -f [string]$evidence.ManifestValidationVerdict),
        ("provenance_verdict={0}" -f [string]$evidence.ProvenanceVerdict),
        ("provenance_failure_reasons={0}" -f (Join-RouteAcceptanceTokenList -Values $evidence.ProvenanceFailureReasons)),
        ("current_runtime_proof={0}" -f ($(if ([bool]$evidence.CurrentRuntimeProof) { 1 } else { 0 }))),
        ("current_runtime_proof_reasons={0}" -f (Join-RouteAcceptanceTokenList -Values $evidence.CurrentRuntimeProofReasons)),
        ("cursor_integrity_verdict={0}" -f $cursorIntegrityVerdict),
        ("writer_evidence_version={0}" -f $writerEvidenceVersion),
        ("writer_integrity_verdict={0}" -f $writerIntegrityVerdict),
        ("writer_gap_count={0}" -f $writerGapCount),
        ("writer_duplicate_count={0}" -f $writerDuplicateCount),
        ("writer_identity_mismatch_count={0}" -f $writerIdentityMismatchCount),
        ("writer_write_failure_count={0}" -f $writerWriteFailureCount),
        ("writer_deferred_batch_count={0}" -f $writerDeferredBatchCount),
        ("writer_recovered_batch_count={0}" -f $writerRecoveredBatchCount),
        ("writer_unresolved_deferred_batch_count={0}" -f $writerUnresolvedDeferredBatchCount),
        ("writer_terminal_failure_count={0}" -f $writerTerminalFailureCount),
        ("writer_recovery_mismatch_count={0}" -f $writerRecoveryMismatchCount),
        ("writer_chronology_inversion_count={0}" -f $writerChronologyInversionCount),
        ("writer_chronology_collision_count={0}" -f $writerChronologyCollisionCount),
        ("writer_drain_missing_count={0}" -f $writerDrainMissingCount),
        ("writer_recovery_verdict={0}" -f $writerRecoveryVerdict),
        ("writer_chronology_verdict={0}" -f $writerChronologyVerdict),
        ("transfer_evidence_status={0}" -f $transferEvidenceStatus),
        ("runtime_correctness_verdict={0}" -f $runtimeCorrectnessVerdict),
        ("runtime_failure_candidate={0}" -f $runtimeFailureCandidate),
        ("runtime_unlock_retry_dispatch_authority_granted_raw_count={0}" -f $runtimeUnlockRetryDispatchAuthorityGrantedRawCount),
        ("runtime_unlock_retry_dispatch_authority_granted_count={0}" -f $runtimeUnlockRetryDispatchAuthorityGrantedCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_raw_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldRawCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_distinct_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldDistinctCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_recovered_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_unresolved_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_verdict={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldVerdict),
        ("runtime_unlock_precommit_probe_start_handoff_missing_raw_count={0}" -f $runtimeUnlockPreCommitProbeStartHandoffMissingRawCount),
        ("runtime_unlock_precommit_probe_start_handoff_missing_distinct_count={0}" -f $runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount),
        ("runtime_unlock_precommit_probe_start_handoff_missing_recovered_count={0}" -f $runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount),
        ("runtime_unlock_precommit_probe_start_handoff_missing_unresolved_count={0}" -f $runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount),
        ("runtime_unlock_precommit_probe_start_handoff_verdict={0}" -f $runtimeUnlockPreCommitProbeStartHandoffVerdict),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count={0}" -f $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count={0}" -f $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count={0}" -f $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count={0}" -f $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_verdict={0}" -f $runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_count={0}" -f $runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_raw_count={0}" -f $runtimeUnlockRetryRegularV4RecoverySettlementMissingRawCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_distinct_count={0}" -f $runtimeUnlockRetryRegularV4RecoverySettlementMissingDistinctCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_recovered_count={0}" -f $runtimeUnlockRetryRegularV4RecoverySettlementMissingRecoveredCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_unresolved_count={0}" -f $runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_verdict={0}" -f $runtimeUnlockRetryRegularV4RecoverySettlementVerdict),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_raw_count={0}" -f $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_distinct_count={0}" -f $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_recovered_count={0}" -f $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_unresolved_count={0}" -f $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppression_verdict={0}" -f $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict),
        ("first_proof_chain_failure_class={0}" -f $firstProofChainFailureClass),
        ("first_proof_chain_failure_event={0}" -f $firstProofChainFailureEvent),
        ("first_proof_chain_failure_reason={0}" -f $firstProofChainFailureReason),
        ("first_proof_chain_failure_sequence={0}" -f $firstProofChainFailureSequence),
        ("first_proof_chain_failure_source={0}" -f $firstProofChainFailureSource),
        ("first_proof_chain_failure_writer_sequence={0}" -f $firstProofChainFailureWriterSequence),
        ("semantic_failure_domain={0}" -f $semanticFailureDomain),
        ("regular_v4_frontier_repair_missing_range_not_preserved_raw_count={0}" -f $regularV4FrontierRepairMissingRangeNotPreservedRawCount),
        ("regular_v4_frontier_repair_missing_range_not_preserved_distinct_count={0}" -f $regularV4FrontierRepairMissingRangeNotPreservedDistinctCount),
        ("regular_v4_frontier_repair_missing_range_not_preserved_recovered_count={0}" -f $regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount),
        ("regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count={0}" -f $regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount),
        ("regular_v4_frontier_repair_missing_range_preservation_verdict={0}" -f $regularV4FrontierRepairMissingRangePreservationVerdict),
        ("regular_v4_frontier_tail_repair_state_publication_missing_raw_count={0}" -f $regularV4FrontierTailRepairStatePublicationMissingRawCount),
        ("regular_v4_frontier_tail_repair_state_publication_missing_distinct_count={0}" -f $regularV4FrontierTailRepairStatePublicationMissingDistinctCount),
        ("regular_v4_frontier_tail_repair_state_publication_missing_recovered_count={0}" -f $regularV4FrontierTailRepairStatePublicationMissingRecoveredCount),
        ("regular_v4_frontier_tail_repair_state_publication_missing_unresolved_count={0}" -f $regularV4FrontierTailRepairStatePublicationMissingUnresolvedCount),
        ("regular_v4_frontier_tail_repair_state_publication_verdict={0}" -f $regularV4FrontierTailRepairStatePublicationVerdict),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_raw_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallMissingRawCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount),
        ("regular_v4_frontier_repair_successor_pressure_install_verdict={0}" -f $regularV4FrontierRepairSuccessorPressureInstallVerdict),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count={0}" -f $regularV4FrontierRepairSettlementDispositionUnverifiableRawCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count={0}" -f $regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count={0}" -f $regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count={0}" -f $regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount),
        ("regular_v4_frontier_repair_settlement_disposition_verdict={0}" -f $regularV4FrontierRepairSettlementDispositionVerdict),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_raw_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_distinct_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_recovered_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_unresolved_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_verdict={0}" -f ($(if ($regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount -gt 0) { 'fail' } elseif ($regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_raw_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_distinct_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_recovered_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_unresolved_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_verdict={0}" -f ($(if ($regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount -gt 0) { 'fail' } elseif ($regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_raw_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationMissingRawCount),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount),
        ("regular_v4_frontier_repair_projection_reauthorization_verdict={0}" -f $regularV4FrontierRepairProjectionReauthorizationVerdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_raw_count={0}" -f $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRawCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_distinct_count={0}" -f $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_recovered_count={0}" -f $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRecoveredCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_unresolved_count={0}" -f $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_verdict={0}" -f ($(if ($fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count={0}" -f $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count={0}" -f $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count={0}" -f $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count={0}" -f $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict={0}" -f ($(if ($fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_raw_count={0}" -f $fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_distinct_count={0}" -f $fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_recovered_count={0}" -f $fileTransferBridgeRecoveryExecutionPayloadHoldMissingRecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_unresolved_count={0}" -f $fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_verdict={0}" -f ($(if ($fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRawCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRecoveredCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_verdict={0}" -f ($(if ($fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRawCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRecoveredCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_verdict={0}" -f ($(if ($fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_raw_count={0}" -f $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_distinct_count={0}" -f $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_recovered_count={0}" -f $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRecoveredCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_unresolved_count={0}" -f $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_verdict={0}" -f ($(if ($fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_raw_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_distinct_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_recovered_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceTerminalizationRecoveredCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_unresolved_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_verdict={0}" -f ($(if ($fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRawCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedDistinctCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRecoveredCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedUnresolvedCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_preservation_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureHoldPreservationVerdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRawCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingDistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingUnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffVerdict),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_requested_count={0}" -f $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckRequestedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided_count={0}" -f $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDecidedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_discarded_count={0}" -f $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDiscardedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count={0}" -f $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count={0}" -f $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingDistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count={0}" -f $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count={0}" -f $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict={0}" -f $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_raw_count={0}" -f $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRawCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_distinct_count={0}" -f $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedDistinctCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_recovered_count={0}" -f $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRecoveredCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_unresolved_count={0}" -f $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedUnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_verdict={0}" -f $runtimeUnlockRetryDispatchZeroOfferSuccessorVerdict),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_raw_count={0}" -f $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_distinct_count={0}" -f $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_recovered_count={0}" -f $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_unresolved_count={0}" -f $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_verdict={0}" -f $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict),
        ("fallback_tail_bridge_recovery_generation_pairing_verdict={0}" -f $fallbackTailBridgeRecoveryGenerationPairingVerdict),
        ("fallback_tail_bridge_recovery_generation_mismatch_raw_count={0}" -f $fallbackTailBridgeRecoveryGenerationMismatchRawCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_distinct_count={0}" -f $fallbackTailBridgeRecoveryGenerationMismatchDistinctCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_recovered_count={0}" -f $fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_unresolved_count={0}" -f $fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict={0}" -f $fallbackTailTransportEpochReauthorizationVerdict),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count={0}" -f $fallbackTailTransportEpochReauthorizationMissingRawCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count={0}" -f $fallbackTailTransportEpochReauthorizationMissingDistinctCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count={0}" -f $fallbackTailTransportEpochReauthorizationMissingRecoveredCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count={0}" -f $fallbackTailTransportEpochReauthorizationMissingUnresolvedCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_verdict={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainVerdict),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount),
        ("filetransfer_receive_recovery_operation_raw_count={0}" -f $fileTransferReceiveRecoveryOperationRawCount),
        ("filetransfer_receive_recovery_operation_distinct_count={0}" -f $fileTransferReceiveRecoveryOperationDistinctCount),
        ("filetransfer_receive_recovery_operation_recovered_count={0}" -f $fileTransferReceiveRecoveryOperationRecoveredCount),
        ("filetransfer_receive_recovery_operation_unresolved_count={0}" -f $fileTransferReceiveRecoveryOperationUnresolvedCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_verdict={0}" -f ($(if ($fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount),
        ("filetransfer_receive_recovery_completed_proven_successor_verdict={0}" -f ($(if ($fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("regular_v4_frontier_repair_stalled_raw_count={0}" -f $regularV4FrontierRepairStalledRawCount),
        ("regular_v4_frontier_repair_stalled_distinct_count={0}" -f $regularV4FrontierRepairStalledDistinctCount),
        ("regular_v4_frontier_repair_stalled_recovered_count={0}" -f $regularV4FrontierRepairStalledRecoveredCount),
        ("regular_v4_frontier_repair_stalled_unresolved_count={0}" -f $regularV4FrontierRepairStalledUnresolvedCount),
        ("regular_v4_frontier_repair_stalled_verdict={0}" -f ($(if ($regularV4FrontierRepairStalledUnresolvedCount -gt 0) { 'fail' } elseif ($regularV4FrontierRepairStalledDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_raw_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRawCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_distinct_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_recovered_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRecoveredCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_unresolved_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_verdict={0}" -f ($(if ($runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount -gt 0) { 'fail' } elseif ($runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_raw_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRawCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_distinct_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_recovered_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRecoveredCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_unresolved_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_verdict={0}" -f ($(if ($runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount -gt 0) { 'fail' } elseif ($runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_verdict={0}" -f ($(if ($runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount -gt 0) { 'fail' } elseif ($runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_verdict={0}" -f ($(if ($runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount -gt 0) { 'fail' } elseif ($runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_verdict={0}" -f ($(if ($runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount -gt 0) { 'fail' } elseif ($runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount -gt 0) { 'pass' } else { 'none' }))),
        ("filetransfer_receive_recovery_operation_verdict={0}" -f $(if ($fileTransferReceiveRecoveryOperationUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferReceiveRecoveryOperationDistinctCount -gt 0) { 'pass' } else { 'none' })),
        ("baseline_manifest={0}" -f $BaselinePath),
        ("goodput_regression_tolerance_percent={0:F1}" -f [double]$GoodputRegressionTolerancePercent),
        'correctness_gate_policy=strict_no_exceptions',
        'network_variance_policy=live_transport_paired_rerun',
        ("network_variance_note={0}" -f $networkVarianceNote),
        'regular_nkn_external_transport_warning_policy=capped_external_transport_churn_requires_clean_rerun',
        'goodput_regression_policy=rerun_once_when_only_failure',
        ("requested_scenarios={0}" -f (Join-RouteAcceptanceTokenList -Values $RequestedScenarios)),
        ("expected_run_count={0}" -f $expectedRunCount),
        'summary_json_failed=0',
        ("run_count={0}" -f $script:RunResults.Count),
        ("failure_count={0}" -f $failureLines.Count),
        ("correctness_failure_count={0}" -f $correctnessFailureLines.Count),
        ("performance_failure_count={0}" -f $performanceFailureLines.Count),
        ''
    )

    foreach ($result in $runDtos) {
        $prefix = [string]$result.name
        $textLines += ("{0}.artifact_dir={1}" -f $prefix, $result.artifactDir)
        $textLines += ("{0}.final_route={1}" -f $prefix, $result.finalRoute)
        $textLines += ("{0}.selected_route_sequence={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.selectedRouteSequence))
        $textLines += ("{0}.route_sequence={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.selectedRouteSequence))
        $textLines += ("{0}.live_route_epoch_route_changes={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.liveRouteEpochRouteChanges))
        $textLines += ("{0}.live_epoch_route_changes={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.liveRouteEpochRouteChanges))
        $textLines += ("{0}.protocol={1}" -f $prefix, $result.protocol)
        $textLines += ("{0}.route_consistency_verdict={1}" -f $prefix, $result.routeConsistencyVerdict)
        $textLines += ("{0}.live_route_epoch_proof_verdict={1}" -f $prefix, $result.liveRouteEpochProofVerdict)
        $textLines += ("{0}.fallback_leg_authority_proof_verdict={1}" -f $prefix, $result.fallbackLegAuthorityProofVerdict)
        $textLines += ("{0}.clean_fallback_leg_proof_verdict={1}" -f $prefix, $result.cleanFallbackLegProofVerdict)
        $textLines += ("{0}.bridge_liveness_integration_verdict={1}" -f $prefix, $result.bridgeLivenessIntegrationVerdict)
        $textLines += ("{0}.control_plane_isolation_verdict={1}" -f $prefix, $result.controlPlaneIsolationVerdict)
        $textLines += ("{0}.fallback_tail_bridge_recovery_generation_pairing_verdict={1}" -f $prefix, $result.fallbackTailBridgeRecoveryGenerationPairingVerdict)
        $textLines += ("{0}.fallback_tail_bridge_recovery_generation_mismatch_raw_count={1}" -f $prefix, $result.fallbackTailBridgeRecoveryGenerationMismatchRawCount)
        $textLines += ("{0}.fallback_tail_bridge_recovery_generation_mismatch_distinct_count={1}" -f $prefix, $result.fallbackTailBridgeRecoveryGenerationMismatchDistinctCount)
        $textLines += ("{0}.fallback_tail_bridge_recovery_generation_mismatch_recovered_count={1}" -f $prefix, $result.fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount)
        $textLines += ("{0}.fallback_tail_bridge_recovery_generation_mismatch_unresolved_count={1}" -f $prefix, $result.fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount)
        $textLines += ("{0}.post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict={1}" -f $prefix, $result.fallbackTailTransportEpochReauthorizationVerdict)
        $textLines += ("{0}.post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count={1}" -f $prefix, $result.fallbackTailTransportEpochReauthorizationMissingRawCount)
        $textLines += ("{0}.post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count={1}" -f $prefix, $result.fallbackTailTransportEpochReauthorizationMissingDistinctCount)
        $textLines += ("{0}.post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count={1}" -f $prefix, $result.fallbackTailTransportEpochReauthorizationMissingRecoveredCount)
        $textLines += ("{0}.post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count={1}" -f $prefix, $result.fallbackTailTransportEpochReauthorizationMissingUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_regular_v4_route_commit_send_drain_verdict={1}" -f $prefix, $result.runtimeUnlockRegularV4RouteCommitSendDrainVerdict)
        $textLines += ("{0}.runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount)
        $textLines += ("{0}.runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount)
        $textLines += ("{0}.runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount)
        $textLines += ("{0}.runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount)
        $textLines += ("{0}.filetransfer_receive_recovery_operation_raw_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryOperationRawCount)
        $textLines += ("{0}.filetransfer_receive_recovery_operation_distinct_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryOperationDistinctCount)
        $textLines += ("{0}.filetransfer_receive_recovery_operation_recovered_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryOperationRecoveredCount)
        $textLines += ("{0}.filetransfer_receive_recovery_operation_unresolved_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryOperationUnresolvedCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_unproven_successor_missing_raw_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_unproven_successor_missing_distinct_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_unproven_successor_missing_recovered_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_unproven_successor_verdict={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorVerdict)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_proven_successor_missing_raw_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_proven_successor_missing_distinct_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_proven_successor_missing_recovered_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_proven_successor_missing_unresolved_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_proven_successor_verdict={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedProvenSuccessorVerdict)
        $textLines += ("{0}.regular_v4_frontier_repair_stalled_raw_count={1}" -f $prefix, $result.regularV4FrontierRepairStalledRawCount)
        $textLines += ("{0}.regular_v4_frontier_repair_stalled_distinct_count={1}" -f $prefix, $result.regularV4FrontierRepairStalledDistinctCount)
        $textLines += ("{0}.regular_v4_frontier_repair_stalled_recovered_count={1}" -f $prefix, $result.regularV4FrontierRepairStalledRecoveredCount)
        $textLines += ("{0}.regular_v4_frontier_repair_stalled_unresolved_count={1}" -f $prefix, $result.regularV4FrontierRepairStalledUnresolvedCount)
        $textLines += ("{0}.regular_v4_frontier_repair_stalled_verdict={1}" -f $prefix, $result.regularV4FrontierRepairStalledVerdict)
        $textLines += ("{0}.runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_raw_count={1}" -f $prefix, $result.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRawCount)
        $textLines += ("{0}.runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_distinct_count={1}" -f $prefix, $result.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount)
        $textLines += ("{0}.runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_recovered_count={1}" -f $prefix, $result.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRecoveredCount)
        $textLines += ("{0}.runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_unresolved_count={1}" -f $prefix, $result.runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_initial_offer_retry_lifecycle_handoff_verdict={1}" -f $prefix, $result.runtimeUnlockInitialOfferRetryLifecycleHandoffVerdict)
        $textLines += ("{0}.runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_raw_count={1}" -f $prefix, $result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRawCount)
        $textLines += ("{0}.runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_distinct_count={1}" -f $prefix, $result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount)
        $textLines += ("{0}.runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_recovered_count={1}" -f $prefix, $result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRecoveredCount)
        $textLines += ("{0}.runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_unresolved_count={1}" -f $prefix, $result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_verdict={1}" -f $prefix, $result.runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedVerdict)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_raw_count={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_distinct_count={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_recovered_count={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_unresolved_count={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_verdict={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryVerdict)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_raw_count={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_distinct_count={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_recovered_count={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_unresolved_count={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_verdict={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredVerdict)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_raw_count={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRawCount)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_distinct_count={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_recovered_count={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRecoveredCount)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_unresolved_count={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_verdict={1}" -f $prefix, $result.runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredVerdict)
        $textLines += ("{0}.filetransfer_receive_recovery_operation_verdict={1}" -f $prefix, $result.fileTransferReceiveRecoveryOperationVerdict)
        $textLines += ("{0}.operator_verdict={1}" -f $prefix, $result.operatorVerdict)
        $textLines += ("{0}.hard_failure_count={1}" -f $prefix, $result.hardFailureCount)
        $textLines += ("{0}.warning_count={1}" -f $prefix, $result.warningCount)
        $textLines += ("{0}.warning_kinds={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.warningKinds))
        $textLines += ("{0}.warning_cap_exceeded_kinds={1}" -f $prefix, $result.warningCapExceededKinds)
        $textLines += ("{0}.environmental_classification={1}" -f $prefix, $result.environmentalClassification)
        $textLines += ("{0}.manifest_present={1}" -f $prefix, ($(if ($result.manifestPresent) { 1 } else { 0 })))
        $textLines += ("{0}.manifest_path={1}" -f $prefix, ($(if ([string]::IsNullOrWhiteSpace([string]$result.manifestPath)) { '(none)' } else { [string]$result.manifestPath })))
        $textLines += ("{0}.manifest_sha256={1}" -f $prefix, ($(if ([string]::IsNullOrWhiteSpace([string]$result.manifestSha256)) { '(none)' } else { [string]$result.manifestSha256 })))
        $textLines += ("{0}.manifest_validation_verdict={1}" -f $prefix, $result.manifestValidationVerdict)
        $textLines += ("{0}.provenance_verdict={1}" -f $prefix, $result.provenanceVerdict)
        $textLines += ("{0}.provenance_failure_reasons={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.provenanceFailureReasons))
        $textLines += ("{0}.cursor_integrity_verdict={1}" -f $prefix, $result.cursorIntegrityVerdict)
        $textLines += ("{0}.writer_evidence_version={1}" -f $prefix, $result.writerEvidenceVersion)
        $textLines += ("{0}.writer_integrity_verdict={1}" -f $prefix, $result.writerIntegrityVerdict)
        $textLines += ("{0}.writer_gap_count={1}" -f $prefix, $result.writerGapCount)
        $textLines += ("{0}.writer_duplicate_count={1}" -f $prefix, $result.writerDuplicateCount)
        $textLines += ("{0}.writer_identity_mismatch_count={1}" -f $prefix, $result.writerIdentityMismatchCount)
        $textLines += ("{0}.writer_write_failure_count={1}" -f $prefix, $result.writerWriteFailureCount)
        $textLines += ("{0}.writer_deferred_batch_count={1}" -f $prefix, $result.writerDeferredBatchCount)
        $textLines += ("{0}.writer_recovered_batch_count={1}" -f $prefix, $result.writerRecoveredBatchCount)
        $textLines += ("{0}.writer_unresolved_deferred_batch_count={1}" -f $prefix, $result.writerUnresolvedDeferredBatchCount)
        $textLines += ("{0}.writer_terminal_failure_count={1}" -f $prefix, $result.writerTerminalFailureCount)
        $textLines += ("{0}.writer_recovery_mismatch_count={1}" -f $prefix, $result.writerRecoveryMismatchCount)
        $textLines += ("{0}.writer_chronology_inversion_count={1}" -f $prefix, $result.writerChronologyInversionCount)
        $textLines += ("{0}.writer_chronology_collision_count={1}" -f $prefix, $result.writerChronologyCollisionCount)
        $textLines += ("{0}.writer_drain_missing_count={1}" -f $prefix, $result.writerDrainMissingCount)
        $textLines += ("{0}.writer_recovery_verdict={1}" -f $prefix, $result.writerRecoveryVerdict)
        $textLines += ("{0}.writer_chronology_verdict={1}" -f $prefix, $result.writerChronologyVerdict)
        $textLines += ("{0}.current_runtime_proof={1}" -f $prefix, ($(if ($result.currentRuntimeProof) { 1 } else { 0 })))
        $textLines += ("{0}.current_runtime_proof_reasons={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.currentRuntimeProofReasons))
        $textLines += ("{0}.transfer_evidence_status={1}" -f $prefix, $result.transferEvidenceStatus)
        $textLines += ("{0}.runtime_correctness_verdict={1}" -f $prefix, $result.runtimeCorrectnessVerdict)
        $textLines += ("{0}.runtime_failure_candidate={1}" -f $prefix, $result.runtimeFailureCandidate)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_authority_granted_raw_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchAuthorityGrantedRawCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_authority_granted_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchAuthorityGrantedCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_post_grant_hold_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchPostGrantHoldCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_post_grant_hold_raw_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchPostGrantHoldRawCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_post_grant_hold_distinct_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchPostGrantHoldDistinctCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_post_grant_hold_recovered_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_post_grant_hold_unresolved_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_post_grant_hold_verdict={1}" -f $prefix, $result.runtimeUnlockRetryDispatchPostGrantHoldVerdict)
        $textLines += ("{0}.runtime_unlock_precommit_probe_start_handoff_missing_raw_count={1}" -f $prefix, $result.runtimeUnlockPreCommitProbeStartHandoffMissingRawCount)
        $textLines += ("{0}.runtime_unlock_precommit_probe_start_handoff_missing_distinct_count={1}" -f $prefix, $result.runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount)
        $textLines += ("{0}.runtime_unlock_precommit_probe_start_handoff_missing_recovered_count={1}" -f $prefix, $result.runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount)
        $textLines += ("{0}.runtime_unlock_precommit_probe_start_handoff_missing_unresolved_count={1}" -f $prefix, $result.runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_precommit_probe_start_handoff_verdict={1}" -f $prefix, $result.runtimeUnlockPreCommitProbeStartHandoffVerdict)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_install_grant_evidence_verdict={1}" -f $prefix, $result.runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict)
        $textLines += ("{0}.runtime_unlock_retry_regular_v4_recovery_settlement_missing_count={1}" -f $prefix, $result.runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_retry_regular_v4_recovery_settlement_missing_raw_count={1}" -f $prefix, $result.runtimeUnlockRetryRegularV4RecoverySettlementMissingRawCount)
        $textLines += ("{0}.runtime_unlock_retry_regular_v4_recovery_settlement_missing_distinct_count={1}" -f $prefix, $result.runtimeUnlockRetryRegularV4RecoverySettlementMissingDistinctCount)
        $textLines += ("{0}.runtime_unlock_retry_regular_v4_recovery_settlement_missing_recovered_count={1}" -f $prefix, $result.runtimeUnlockRetryRegularV4RecoverySettlementMissingRecoveredCount)
        $textLines += ("{0}.runtime_unlock_retry_regular_v4_recovery_settlement_missing_unresolved_count={1}" -f $prefix, $result.runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_retry_regular_v4_recovery_settlement_verdict={1}" -f $prefix, $result.runtimeUnlockRetryRegularV4RecoverySettlementVerdict)
        $textLines += ("{0}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count={1}" -f $prefix, $result.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount)
        $textLines += ("{0}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count={1}" -f $prefix, $result.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount)
        $textLines += ("{0}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count={1}" -f $prefix, $result.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount)
        $textLines += ("{0}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count={1}" -f $prefix, $result.runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict={1}" -f $prefix, $result.runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict)
        $textLines += ("{0}.runtime_unlock_precommit_fallback_recovery_handoff_suppressed_raw_count={1}" -f $prefix, $result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount)
        $textLines += ("{0}.runtime_unlock_precommit_fallback_recovery_handoff_suppressed_distinct_count={1}" -f $prefix, $result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount)
        $textLines += ("{0}.runtime_unlock_precommit_fallback_recovery_handoff_suppressed_recovered_count={1}" -f $prefix, $result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount)
        $textLines += ("{0}.runtime_unlock_precommit_fallback_recovery_handoff_suppressed_unresolved_count={1}" -f $prefix, $result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_precommit_fallback_recovery_handoff_suppression_verdict={1}" -f $prefix, $result.runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict)
        $textLines += ("{0}.regular_v4_frontier_repair_missing_range_not_preserved_raw_count={1}" -f $prefix, $result.regularV4FrontierRepairMissingRangeNotPreservedRawCount)
        $textLines += ("{0}.regular_v4_frontier_repair_missing_range_not_preserved_distinct_count={1}" -f $prefix, $result.regularV4FrontierRepairMissingRangeNotPreservedDistinctCount)
        $textLines += ("{0}.regular_v4_frontier_repair_missing_range_not_preserved_recovered_count={1}" -f $prefix, $result.regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount)
        $textLines += ("{0}.regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count={1}" -f $prefix, $result.regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount)
        $textLines += ("{0}.regular_v4_frontier_repair_missing_range_preservation_verdict={1}" -f $prefix, $result.regularV4FrontierRepairMissingRangePreservationVerdict)
        $textLines += ("{0}.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count={1}" -f $prefix, $result.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount)
        $textLines += ("{0}.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count={1}" -f $prefix, $result.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount)
        $textLines += ("{0}.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count={1}" -f $prefix, $result.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount)
        $textLines += ("{0}.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count={1}" -f $prefix, $result.regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount)
        $textLines += ("{0}.regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict={1}" -f $prefix, $result.regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict)
        $textLines += ("{0}.regular_v4_frontier_repair_successor_pressure_install_missing_raw_count={1}" -f $prefix, $result.regularV4FrontierRepairSuccessorPressureInstallMissingRawCount)
        $textLines += ("{0}.regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count={1}" -f $prefix, $result.regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount)
        $textLines += ("{0}.regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count={1}" -f $prefix, $result.regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount)
        $textLines += ("{0}.regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count={1}" -f $prefix, $result.regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount)
        $textLines += ("{0}.regular_v4_frontier_repair_successor_pressure_install_verdict={1}" -f $prefix, $result.regularV4FrontierRepairSuccessorPressureInstallVerdict)
        $textLines += ("{0}.regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count={1}" -f $prefix, $result.regularV4FrontierRepairSettlementDispositionUnverifiableRawCount)
        $textLines += ("{0}.regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count={1}" -f $prefix, $result.regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount)
        $textLines += ("{0}.regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count={1}" -f $prefix, $result.regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount)
        $textLines += ("{0}.regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count={1}" -f $prefix, $result.regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount)
        $textLines += ("{0}.regular_v4_frontier_repair_settlement_disposition_verdict={1}" -f $prefix, $result.regularV4FrontierRepairSettlementDispositionVerdict)
        $textLines += ("{0}.regular_v4_frontier_repair_pressure_authority_lifecycle_split_raw_count={1}" -f $prefix, $result.regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount)
        $textLines += ("{0}.regular_v4_frontier_repair_pressure_authority_lifecycle_split_distinct_count={1}" -f $prefix, $result.regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount)
        $textLines += ("{0}.regular_v4_frontier_repair_pressure_authority_lifecycle_split_recovered_count={1}" -f $prefix, $result.regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount)
        $textLines += ("{0}.regular_v4_frontier_repair_pressure_authority_lifecycle_split_unresolved_count={1}" -f $prefix, $result.regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount)
        $textLines += ("{0}.regular_v4_frontier_repair_pressure_authority_lifecycle_split_verdict={1}" -f $prefix, $result.regularV4FrontierRepairPressureAuthorityLifecycleSplitVerdict)
        $textLines += ("{0}.regular_v4_frontier_repair_pressure_recovery_handoff_missing_raw_count={1}" -f $prefix, $result.regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount)
        $textLines += ("{0}.regular_v4_frontier_repair_pressure_recovery_handoff_missing_distinct_count={1}" -f $prefix, $result.regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount)
        $textLines += ("{0}.regular_v4_frontier_repair_pressure_recovery_handoff_missing_recovered_count={1}" -f $prefix, $result.regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount)
        $textLines += ("{0}.regular_v4_frontier_repair_pressure_recovery_handoff_missing_unresolved_count={1}" -f $prefix, $result.regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount)
        $textLines += ("{0}.regular_v4_frontier_repair_pressure_recovery_handoff_verdict={1}" -f $prefix, $result.regularV4FrontierRepairPressureRecoveryHandoffVerdict)
        $textLines += ("{0}.regular_v4_frontier_repair_projection_reauthorization_missing_raw_count={1}" -f $prefix, $result.regularV4FrontierRepairProjectionReauthorizationMissingRawCount)
        $textLines += ("{0}.regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count={1}" -f $prefix, $result.regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount)
        $textLines += ("{0}.regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count={1}" -f $prefix, $result.regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount)
        $textLines += ("{0}.regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count={1}" -f $prefix, $result.regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount)
        $textLines += ("{0}.regular_v4_frontier_repair_projection_reauthorization_verdict={1}" -f $prefix, $result.regularV4FrontierRepairProjectionReauthorizationVerdict)
        $textLines += ("{0}.regular_v4_frontier_tail_repair_state_publication_missing_raw_count={1}" -f $prefix, $result.regularV4FrontierTailRepairStatePublicationMissingRawCount)
        $textLines += ("{0}.regular_v4_frontier_tail_repair_state_publication_missing_distinct_count={1}" -f $prefix, $result.regularV4FrontierTailRepairStatePublicationMissingDistinctCount)
        $textLines += ("{0}.regular_v4_frontier_tail_repair_state_publication_missing_recovered_count={1}" -f $prefix, $result.regularV4FrontierTailRepairStatePublicationMissingRecoveredCount)
        $textLines += ("{0}.regular_v4_frontier_tail_repair_state_publication_missing_unresolved_count={1}" -f $prefix, $result.regularV4FrontierTailRepairStatePublicationMissingUnresolvedCount)
        $textLines += ("{0}.regular_v4_frontier_tail_repair_state_publication_verdict={1}" -f $prefix, $result.regularV4FrontierTailRepairStatePublicationVerdict)
        $textLines += ("{0}.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount)
        $textLines += ("{0}.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount)
        $textLines += ("{0}.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount)
        $textLines += ("{0}.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_raw_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRawCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_distinct_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_recovered_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRecoveredCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_unresolved_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_verdict={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionSoftToHardHandoffVerdict)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionStartupPayloadIsolationVerdict)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_payload_hold_missing_raw_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_payload_hold_missing_distinct_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_payload_hold_missing_recovered_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionPayloadHoldMissingRecoveredCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_payload_hold_missing_unresolved_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_raw_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRawCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_distinct_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_recovered_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRecoveredCount)
        $textLines += ("{0}.filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_unresolved_count={1}" -f $prefix, $result.fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_payload_hold_verdict={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionPayloadHoldVerdict)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_raw_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_distinct_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_recovered_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRecoveredCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_unresolved_count={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount)
        $textLines += ("{0}.filetransfer_bridge_recovery_execution_submission_barrier_drain_verdict={1}" -f $prefix, $result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainVerdict)
        $textLines += ("{0}.filetransfer_route_transition_draining_transport_grace_terminalization_raw_count={1}" -f $prefix, $result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount)
        $textLines += ("{0}.filetransfer_route_transition_draining_transport_grace_terminalization_distinct_count={1}" -f $prefix, $result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount)
        $textLines += ("{0}.filetransfer_route_transition_draining_transport_grace_terminalization_recovered_count={1}" -f $prefix, $result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationRecoveredCount)
        $textLines += ("{0}.filetransfer_route_transition_draining_transport_grace_terminalization_unresolved_count={1}" -f $prefix, $result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount)
        $textLines += ("{0}.filetransfer_route_transition_draining_transport_grace_terminalization_verdict={1}" -f $prefix, $result.fileTransferRouteTransitionDrainingTransportGraceTerminalizationVerdict)
        $textLines += ("{0}.runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_raw_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRawCount)
        $textLines += ("{0}.runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_distinct_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedDistinctCount)
        $textLines += ("{0}.runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_recovered_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRecoveredCount)
        $textLines += ("{0}.runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_unresolved_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_preservation_verdict={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureHoldPreservationVerdict)
        $textLines += ("{0}.runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_raw_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRawCount)
        $textLines += ("{0}.runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_distinct_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingDistinctCount)
        $textLines += ("{0}.runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_recovered_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRecoveredCount)
        $textLines += ("{0}.runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_unresolved_count={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_verdict={1}" -f $prefix, $result.runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffVerdict)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_requested_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckRequestedCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDecidedCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_discarded_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDiscardedCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingDistinctCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRecoveredCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_zero_offer_successor_rejected_raw_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRawCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_zero_offer_successor_rejected_distinct_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedDistinctCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_zero_offer_successor_rejected_recovered_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRecoveredCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_zero_offer_successor_rejected_unresolved_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_zero_offer_successor_verdict={1}" -f $prefix, $result.runtimeUnlockRetryDispatchZeroOfferSuccessorVerdict)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_raw_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_distinct_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_recovered_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_unresolved_count={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount)
        $textLines += ("{0}.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_verdict={1}" -f $prefix, $result.runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict)
        $textLines += ("{0}.first_proof_chain_failure_class={1}" -f $prefix, $result.firstProofChainFailureClass)
        $textLines += ("{0}.first_proof_chain_failure_event={1}" -f $prefix, $result.firstProofChainFailureEvent)
        $textLines += ("{0}.first_proof_chain_failure_reason={1}" -f $prefix, $result.firstProofChainFailureReason)
        $textLines += ("{0}.first_proof_chain_failure_sequence={1}" -f $prefix, $result.firstProofChainFailureSequence)
        $textLines += ("{0}.first_proof_chain_failure_source={1}" -f $prefix, $result.firstProofChainFailureSource)
        $textLines += ("{0}.first_proof_chain_failure_writer_sequence={1}" -f $prefix, $result.firstProofChainFailureWriterSequence)
        $textLines += ("{0}.semantic_failure_domain={1}" -f $prefix, $result.semanticFailureDomain)
        $textLines += ("{0}.evidence_classification={1}" -f $prefix, $result.evidenceClassification)
        $textLines += ("{0}.measurement_contaminated={1}" -f $prefix, ($(if ($result.measurementContaminated) { 1 } else { 0 })))
        $textLines += ("{0}.measurement_contamination_reasons={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.measurementContaminationReasons))
        $textLines += ("{0}.completed={1}" -f $prefix, ($(if ($result.completed) { 1 } else { 0 })))
        $textLines += ("{0}.sha_ok={1}" -f $prefix, ($(if ($result.shaOk) { 1 } else { 0 })))
        $textLines += ("{0}.goodput_bytes_per_second={1:F3}" -f $prefix, (ConvertTo-RouteAcceptanceDouble -Value $result.goodputBytesPerSecond))
        $textLines += ("{0}.baseline_goodput_bytes_per_second={1:F3}" -f $prefix, (ConvertTo-RouteAcceptanceDouble -Value $result.baselineGoodputBytesPerSecond))
        $textLines += ("{0}.goodput_floor_bytes_per_second={1:F3}" -f $prefix, (ConvertTo-RouteAcceptanceDouble -Value $result.goodputRegressionFloorBytesPerSecond))
        $textLines += ("{0}.goodput_regression_percent={1:F3}" -f $prefix, (ConvertTo-RouteAcceptanceDouble -Value $result.goodputRegressionPercent))
        $textLines += ("{0}.fallback_receiver_state_deferred_count={1}" -f $prefix, $result.fallbackReceiverStateDeferredCount)
        $textLines += ("{0}.fallback_receiver_state_coalesced_count={1}" -f $prefix, $result.fallbackReceiverStateCoalescedCount)
        $textLines += ("{0}.fallback_receiver_state_churn_warning_count={1}" -f $prefix, $result.fallbackReceiverStateChurnWarningCount)
        $textLines += ("{0}.fallback_receiver_state_churn_raw_event_count={1}" -f $prefix, $result.fallbackReceiverStateChurnRawEventCount)
        $textLines += ("{0}.fallback_receiver_state_churn_efficiency_class={1}" -f $prefix, $result.fallbackReceiverStateChurnEfficiencyClass)
        $textLines += ("{0}.attempt_count={1}" -f $prefix, $result.attemptCount)
        $textLines += ("{0}.retry_used={1}" -f $prefix, ($(if ($result.retryUsed) { 1 } else { 0 })))
        $textLines += ("{0}.selected_attempt={1}" -f $prefix, $result.selectedAttempt)
        if ([int]$result.scenarioIsolationPreflightCount -gt 0) {
            $textLines += ("{0}.scenario_isolation_preflight_count={1}" -f $prefix, $result.scenarioIsolationPreflightCount)
            $textLines += ("{0}.scenario_isolation_clean_window_passed={1}" -f $prefix, $result.scenarioIsolationCleanWindowPassed)
            $textLines += ("{0}.scenario_isolation_artifact_dirs={1}" -f $prefix, (Join-RouteAcceptanceTokenList -Values $result.scenarioIsolationArtifactDirs))
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.scenarioIsolationFailureReason)) {
            $textLines += ("{0}.scenario_isolation_failure_reason={1}" -f $prefix, $result.scenarioIsolationFailureReason)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.firstFailureReason)) {
            $textLines += ("{0}.first_failure_reason={1}" -f $prefix, $result.firstFailureReason)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.rerunArtifactDir)) {
            $textLines += ("{0}.rerun_artifact_dir={1}" -f $prefix, $result.rerunArtifactDir)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.rerunFailureReason)) {
            $textLines += ("{0}.rerun_failure_reason={1}" -f $prefix, $result.rerunFailureReason)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.setupFailurePhase)) {
            $textLines += ("{0}.setup_failure_phase={1}" -f $prefix, $result.setupFailurePhase)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.setupFailureReason)) {
            $textLines += ("{0}.setup_failure_reason={1}" -f $prefix, $result.setupFailureReason)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.guiReportedFailurePhase)) {
            $textLines += ("{0}.gui_reported_failure_phase={1}" -f $prefix, $result.guiReportedFailurePhase)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.guiReportedFailureReason)) {
            $textLines += ("{0}.gui_reported_failure_reason={1}" -f $prefix, $result.guiReportedFailureReason)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$result.guiReportedFailureError)) {
            $textLines += ("{0}.gui_reported_failure_error={1}" -f $prefix, $result.guiReportedFailureError)
        }
        $textLines += ("{0}.failure_count={1}" -f $prefix, $result.failures.Count)
        $textLines += ("{0}.correctness_failure_count={1}" -f $prefix, $result.correctnessFailureCount)
        $textLines += ("{0}.performance_failure_count={1}" -f $prefix, $result.performanceFailureCount)
        $textLines += ("{0}.acceptance_failure_class={1}" -f $prefix, $result.acceptanceFailureClass)
    }

    $textLines += ''
    $textLines += 'failures:'
    if ($failureLines.Count -eq 0) {
        $textLines += '(none)'
    }
    else {
        $textLines += @($failureLines)
    }

    $summaryTxt = Join-Path $RunRoot ("{0}-summary.txt" -f $SummaryBaseName)
    $textStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $textLines | Set-Content -LiteralPath $summaryTxt -Encoding UTF8
    $textLines | Set-Content -LiteralPath (Join-Path $RunRoot 'route-acceptance-summary.txt') -Encoding UTF8
    $textStopwatch.Stop()
    $diagnostics.textWriteMs = [long]$textStopwatch.ElapsedMilliseconds

    $varianceStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $varianceNoteLines = @(
        ("# {0} Network Variance Note" -f ($(if ($AcceptancePhase -eq 'phase5') { 'Phase 5' } else { 'Phase 4' }))),
        '',
        ('artifact_root={0}' -f $RunRoot),
        ('baseline_manifest={0}' -f $BaselinePath),
        ('verdict={0}' -f $verdict),
        ('correctness_verdict={0}' -f $correctnessVerdict),
        ('performance_verdict={0}' -f $performanceVerdict),
        ('goodput_regression_tolerance_percent={0:F1}' -f [double]$GoodputRegressionTolerancePercent),
        '',
        $networkVarianceNote,
        '',
        ($(if ($AcceptancePhase -eq 'phase5') {
            'A goodput-only miss does not fail Phase 5 release correctness. It remains visible as a performance variance so remediation can avoid unnecessary bridge, wallet, installer, or route-policy changes.'
        } else {
            'A goodput-only miss does not waive Phase 4 acceptance. It separates performance variance from route/runtime correctness so remediation can avoid unnecessary bridge, wallet, installer, or route-policy changes.'
        })),
        '',
        'Performance failures:'
    )
    if ($performanceFailureLines.Count -eq 0) {
        $varianceNoteLines += '- (none)'
    }
    else {
        $varianceNoteLines += @($performanceFailureLines | ForEach-Object { '- {0}' -f $_ })
    }
    $varianceNoteLines += ''
    $varianceNoteLines += 'Correctness/evidence failures:'
    if ($correctnessFailureLines.Count -eq 0) {
        $varianceNoteLines += '- (none)'
    }
    else {
        $varianceNoteLines += @($correctnessFailureLines | ForEach-Object { '- {0}' -f $_ })
    }
    $varianceNoteName = if ($AcceptancePhase -eq 'phase5') { 'phase5-network-variance-note.md' } else { 'phase4-network-variance-note.md' }
    $varianceNoteLines | Set-Content -LiteralPath (Join-Path $RunRoot $varianceNoteName) -Encoding UTF8
    $varianceStopwatch.Stop()
    $diagnostics.varianceNoteWriteMs = [long]$varianceStopwatch.ElapsedMilliseconds

    $jsonSummary = [ordered]@{
        event = if ($AcceptancePhase -eq 'phase5') { 'phase5_filetransfer_analyzer_gui_acceptance_summary' } else { 'phase4_filetransfer_ab_acceptance_summary' }
        verdict = $verdict
        correctnessVerdict = $correctnessVerdict
        performanceVerdict = $performanceVerdict
        acceptancePhase = $AcceptancePhase
        artifactRoot = $RunRoot
        manifestPresent = [bool]$evidence.ManifestPresent
        manifestPath = [string]$evidence.ManifestPath
        manifestSha256 = [string]$evidence.ManifestSha256
        manifestValidationVerdict = [string]$evidence.ManifestValidationVerdict
        provenanceVerdict = [string]$evidence.ProvenanceVerdict
        provenanceFailureReasons = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $evidence.ProvenanceFailureReasons -Limit 32)
        currentRuntimeProof = [bool]$evidence.CurrentRuntimeProof
        currentRuntimeProofReasons = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $evidence.CurrentRuntimeProofReasons -Limit 32)
        cursorIntegrityVerdict = $cursorIntegrityVerdict
        writerEvidenceVersion = $writerEvidenceVersion
        writerIntegrityVerdict = $writerIntegrityVerdict
        writerGapCount = $writerGapCount
        writerDuplicateCount = $writerDuplicateCount
        writerIdentityMismatchCount = $writerIdentityMismatchCount
        writerWriteFailureCount = $writerWriteFailureCount
        writerDeferredBatchCount = $writerDeferredBatchCount
        writerRecoveredBatchCount = $writerRecoveredBatchCount
        writerUnresolvedDeferredBatchCount = $writerUnresolvedDeferredBatchCount
        writerTerminalFailureCount = $writerTerminalFailureCount
        writerRecoveryMismatchCount = $writerRecoveryMismatchCount
        writerChronologyInversionCount = $writerChronologyInversionCount
        writerChronologyCollisionCount = $writerChronologyCollisionCount
        writerDrainMissingCount = $writerDrainMissingCount
        writerRecoveryVerdict = $writerRecoveryVerdict
        writerChronologyVerdict = $writerChronologyVerdict
        transferEvidenceStatus = $transferEvidenceStatus
        runtimeCorrectnessVerdict = $runtimeCorrectnessVerdict
        runtimeFailureCandidate = $runtimeFailureCandidate
        runtimeUnlockRetryDispatchAuthorityGrantedRawCount = $runtimeUnlockRetryDispatchAuthorityGrantedRawCount
        runtimeUnlockRetryDispatchAuthorityGrantedCount = $runtimeUnlockRetryDispatchAuthorityGrantedCount
        runtimeUnlockRetryDispatchPostGrantHoldCount = $runtimeUnlockRetryDispatchPostGrantHoldCount
        runtimeUnlockRetryDispatchPostGrantHoldRawCount = $runtimeUnlockRetryDispatchPostGrantHoldRawCount
        runtimeUnlockRetryDispatchPostGrantHoldDistinctCount = $runtimeUnlockRetryDispatchPostGrantHoldDistinctCount
        runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount = $runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount
        runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount = $runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount
        runtimeUnlockRetryDispatchPostGrantHoldVerdict = $runtimeUnlockRetryDispatchPostGrantHoldVerdict
        runtimeUnlockPreCommitProbeStartHandoffMissingRawCount = $runtimeUnlockPreCommitProbeStartHandoffMissingRawCount
        runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount = $runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount
        runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount = $runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount
        runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount = $runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount
        runtimeUnlockPreCommitProbeStartHandoffVerdict = $runtimeUnlockPreCommitProbeStartHandoffVerdict
        runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount = $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount
        runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount = $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount
        runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount = $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount
        runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount = $runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount
        runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict = $runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict
        runtimeUnlockRetryRegularV4RecoverySettlementMissingRawCount = $runtimeUnlockRetryRegularV4RecoverySettlementMissingRawCount
        runtimeUnlockRetryRegularV4RecoverySettlementMissingDistinctCount = $runtimeUnlockRetryRegularV4RecoverySettlementMissingDistinctCount
        runtimeUnlockRetryRegularV4RecoverySettlementMissingRecoveredCount = $runtimeUnlockRetryRegularV4RecoverySettlementMissingRecoveredCount
        runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount = $runtimeUnlockRetryRegularV4RecoverySettlementMissingUnresolvedCount
        runtimeUnlockRetryRegularV4RecoverySettlementVerdict = $runtimeUnlockRetryRegularV4RecoverySettlementVerdict
        runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount = $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount
        runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount = $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount
        runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount = $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount
        runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount = $runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount
        runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict = $runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount = $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount = $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount = $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount = $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount
        runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict = $runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict
        firstProofChainFailureClass = $firstProofChainFailureClass
        firstProofChainFailureEvent = $firstProofChainFailureEvent
        firstProofChainFailureReason = $firstProofChainFailureReason
        firstProofChainFailureSequence = $firstProofChainFailureSequence
        firstProofChainFailureSource = $firstProofChainFailureSource
        firstProofChainFailureWriterSequence = $firstProofChainFailureWriterSequence
        semanticFailureDomain = $semanticFailureDomain
        regularV4FrontierRepairMissingRangeNotPreservedRawCount = $regularV4FrontierRepairMissingRangeNotPreservedRawCount
        regularV4FrontierRepairMissingRangeNotPreservedDistinctCount = $regularV4FrontierRepairMissingRangeNotPreservedDistinctCount
        regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount = $regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount
        regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount = $regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount
        regularV4FrontierRepairMissingRangePreservationVerdict = $regularV4FrontierRepairMissingRangePreservationVerdict
        regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount = $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount
        regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount = $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount
        regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount = $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount
        regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount = $regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount
        regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict = $regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict
        regularV4FrontierRepairSuccessorPressureInstallMissingRawCount = $regularV4FrontierRepairSuccessorPressureInstallMissingRawCount
        regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount = $regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount
        regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount = $regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount
        regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount = $regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount
        regularV4FrontierRepairSuccessorPressureInstallVerdict = $regularV4FrontierRepairSuccessorPressureInstallVerdict
        regularV4FrontierRepairSettlementDispositionUnverifiableRawCount = $regularV4FrontierRepairSettlementDispositionUnverifiableRawCount
        regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount = $regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount
        regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount = $regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount
        regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount = $regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount
        regularV4FrontierRepairSettlementDispositionVerdict = $regularV4FrontierRepairSettlementDispositionVerdict
        regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount = $regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount
        regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount = $regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount
        regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount = $regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount
        regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount = $regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount
        regularV4FrontierRepairPressureAuthorityLifecycleSplitVerdict = if ($regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount -gt 0) { 'fail' } elseif ($regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount -gt 0) { 'pass' } else { 'none' }
        regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount = $regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount
        regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount = $regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount
        regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount = $regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount
        regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount = $regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount
        regularV4FrontierRepairPressureRecoveryHandoffVerdict = if ($regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount -gt 0) { 'fail' } elseif ($regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount -gt 0) { 'pass' } else { 'none' }
        regularV4FrontierRepairProjectionReauthorizationMissingRawCount = $regularV4FrontierRepairProjectionReauthorizationMissingRawCount
        regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount = $regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount
        regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount = $regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount
        regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount = $regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount
        regularV4FrontierRepairProjectionReauthorizationVerdict = $regularV4FrontierRepairProjectionReauthorizationVerdict
        regularV4FrontierTailRepairStatePublicationMissingRawCount = $regularV4FrontierTailRepairStatePublicationMissingRawCount
        regularV4FrontierTailRepairStatePublicationMissingDistinctCount = $regularV4FrontierTailRepairStatePublicationMissingDistinctCount
        regularV4FrontierTailRepairStatePublicationMissingRecoveredCount = $regularV4FrontierTailRepairStatePublicationMissingRecoveredCount
        regularV4FrontierTailRepairStatePublicationMissingUnresolvedCount = $regularV4FrontierTailRepairStatePublicationMissingUnresolvedCount
        regularV4FrontierTailRepairStatePublicationVerdict = $regularV4FrontierTailRepairStatePublicationVerdict
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount = $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount = $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount = $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount = $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount
        runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict = $runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRawCount = $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRawCount
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount = $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRecoveredCount = $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingRecoveredCount
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount = $fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount
        fileTransferBridgeRecoveryExecutionSoftToHardHandoffVerdict = if ($fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferBridgeRecoveryExecutionSoftToHardHandoffMissingDistinctCount -gt 0) { 'pass' } else { 'none' }
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount = $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount = $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount = $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount = $fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount
        fileTransferBridgeRecoveryExecutionStartupPayloadIsolationVerdict = if ($fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount -gt 0) { 'pass' } else { 'none' }
        fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount = $fileTransferBridgeRecoveryExecutionPayloadHoldMissingRawCount
        fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount = $fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount
        fileTransferBridgeRecoveryExecutionPayloadHoldMissingRecoveredCount = $fileTransferBridgeRecoveryExecutionPayloadHoldMissingRecoveredCount
        fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount = $fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount
        fileTransferBridgeRecoveryExecutionPayloadHoldVerdict = if ($fileTransferBridgeRecoveryExecutionPayloadHoldMissingUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferBridgeRecoveryExecutionPayloadHoldMissingDistinctCount -gt 0) { 'pass' } else { 'none' }
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRawCount = $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRawCount
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount = $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRecoveredCount = $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedRecoveredCount
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount = $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount
        fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldVerdict = if ($fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldNotPreservedDistinctCount -gt 0) { 'pass' } else { 'none' }
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRawCount = $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRawCount
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount = $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRecoveredCount = $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingRecoveredCount
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount = $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount
        fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationVerdict = if ($fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationMissingDistinctCount -gt 0) { 'pass' } else { 'none' }
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount = $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount = $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRecoveredCount = $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRecoveredCount
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount = $fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount
        fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainVerdict = if ($fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingDistinctCount -gt 0) { 'pass' } else { 'none' }
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount = $fileTransferRouteTransitionDrainingTransportGraceTerminalizationRawCount
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount = $fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationRecoveredCount = $fileTransferRouteTransitionDrainingTransportGraceTerminalizationRecoveredCount
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount = $fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount
        fileTransferRouteTransitionDrainingTransportGraceTerminalizationVerdict = if ($fileTransferRouteTransitionDrainingTransportGraceTerminalizationUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferRouteTransitionDrainingTransportGraceTerminalizationDistinctCount -gt 0) { 'pass' } else { 'none' }
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRawCount = $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRawCount
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedDistinctCount = $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedDistinctCount
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRecoveredCount = $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedRecoveredCount
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedUnresolvedCount = $runtimeUnlockRegularV4RecoveryLeaseClosureHoldNotPreservedUnresolvedCount
        runtimeUnlockRegularV4RecoveryLeaseClosureHoldPreservationVerdict = $runtimeUnlockRegularV4RecoveryLeaseClosureHoldPreservationVerdict
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRawCount = $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRawCount
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingDistinctCount = $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingDistinctCount
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRecoveredCount = $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingRecoveredCount
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingUnresolvedCount = $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffMissingUnresolvedCount
        runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffVerdict = $runtimeUnlockRegularV4RecoveryLeaseClosureOfferTimeoutHandoffVerdict
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckRequestedCount = $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckRequestedCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDecidedCount = $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDecidedCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDiscardedCount = $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckDiscardedCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount = $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingDistinctCount = $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingDistinctCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRecoveredCount = $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRecoveredCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount = $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount
        runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict = $runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict
        runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRawCount = $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRawCount
        runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedDistinctCount = $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedDistinctCount
        runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRecoveredCount = $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedRecoveredCount
        runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedUnresolvedCount = $runtimeUnlockRetryDispatchZeroOfferSuccessorRejectedUnresolvedCount
        runtimeUnlockRetryDispatchZeroOfferSuccessorVerdict = $runtimeUnlockRetryDispatchZeroOfferSuccessorVerdict
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount = $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount = $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount = $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount = $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount
        runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict = $runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict
        fallbackTailBridgeRecoveryGenerationPairingVerdict = $fallbackTailBridgeRecoveryGenerationPairingVerdict
        fallbackTailBridgeRecoveryGenerationMismatchRawCount = $fallbackTailBridgeRecoveryGenerationMismatchRawCount
        fallbackTailBridgeRecoveryGenerationMismatchDistinctCount = $fallbackTailBridgeRecoveryGenerationMismatchDistinctCount
        fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount = $fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount
        fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount = $fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount
        fallbackTailTransportEpochReauthorizationVerdict = $fallbackTailTransportEpochReauthorizationVerdict
        fallbackTailTransportEpochReauthorizationMissingRawCount = $fallbackTailTransportEpochReauthorizationMissingRawCount
        fallbackTailTransportEpochReauthorizationMissingDistinctCount = $fallbackTailTransportEpochReauthorizationMissingDistinctCount
        fallbackTailTransportEpochReauthorizationMissingRecoveredCount = $fallbackTailTransportEpochReauthorizationMissingRecoveredCount
        fallbackTailTransportEpochReauthorizationMissingUnresolvedCount = $fallbackTailTransportEpochReauthorizationMissingUnresolvedCount
        runtimeUnlockRegularV4RouteCommitSendDrainVerdict = $runtimeUnlockRegularV4RouteCommitSendDrainVerdict
        runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount = $runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount
        runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount = $runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount
        runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount = $runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount
        runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount = $runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount
        fileTransferReceiveRecoveryOperationRawCount = $fileTransferReceiveRecoveryOperationRawCount
        fileTransferReceiveRecoveryOperationDistinctCount = $fileTransferReceiveRecoveryOperationDistinctCount
        fileTransferReceiveRecoveryOperationRecoveredCount = $fileTransferReceiveRecoveryOperationRecoveredCount
        fileTransferReceiveRecoveryOperationUnresolvedCount = $fileTransferReceiveRecoveryOperationUnresolvedCount
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount = $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount = $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount = $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRecoveredCount
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount = $fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount
        fileTransferReceiveRecoveryCompletedUnprovenSuccessorVerdict = if ($fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount -gt 0) {
            'fail'
        }
        elseif ($fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingDistinctCount -gt 0) {
            'pass'
        }
        else {
            'none'
        }
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount = $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRawCount
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount = $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount = $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingRecoveredCount
        fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount = $fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount
        fileTransferReceiveRecoveryCompletedProvenSuccessorVerdict = if ($fileTransferReceiveRecoveryCompletedProvenSuccessorMissingUnresolvedCount -gt 0) {
            'fail'
        }
        elseif ($fileTransferReceiveRecoveryCompletedProvenSuccessorMissingDistinctCount -gt 0) {
            'pass'
        }
        else {
            'none'
        }
        regularV4FrontierRepairStalledRawCount = $regularV4FrontierRepairStalledRawCount
        regularV4FrontierRepairStalledDistinctCount = $regularV4FrontierRepairStalledDistinctCount
        regularV4FrontierRepairStalledRecoveredCount = $regularV4FrontierRepairStalledRecoveredCount
        regularV4FrontierRepairStalledUnresolvedCount = $regularV4FrontierRepairStalledUnresolvedCount
        regularV4FrontierRepairStalledVerdict = if ($regularV4FrontierRepairStalledUnresolvedCount -gt 0) {
            'fail'
        }
        elseif ($regularV4FrontierRepairStalledDistinctCount -gt 0) {
            'pass'
        }
        else {
            'none'
        }
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRawCount = $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRawCount
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount = $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRecoveredCount = $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingRecoveredCount
        runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount = $runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount
        runtimeUnlockInitialOfferRetryLifecycleHandoffVerdict = if ($runtimeUnlockInitialOfferRetryLifecycleHandoffMissingUnresolvedCount -gt 0) {
            'fail'
        }
        elseif ($runtimeUnlockInitialOfferRetryLifecycleHandoffMissingDistinctCount -gt 0) {
            'pass'
        }
        else {
            'none'
        }
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRawCount = $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRawCount
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount = $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRecoveredCount = $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedRecoveredCount
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount = $runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount
        runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedVerdict = if ($runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedUnresolvedCount -gt 0) {
            'fail'
        }
        elseif ($runtimeUnlockInitialOfferReceiveRecoveryHoldNotPreservedDistinctCount -gt 0) {
            'pass'
        }
        else {
            'none'
        }
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryVerdict = if ($runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount -gt 0) {
            'fail'
        }
        elseif ($runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount -gt 0) {
            'pass'
        }
        else {
            'none'
        }
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRawCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredRecoveredCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredVerdict = if ($runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredUnresolvedCount -gt 0) {
            'fail'
        }
        elseif ($runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredDistinctCount -gt 0) {
            'pass'
        }
        else {
            'none'
        }
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRawCount = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRawCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRecoveredCount = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredRecoveredCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount
        runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredVerdict = if ($runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredUnresolvedCount -gt 0) {
            'fail'
        }
        elseif ($runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredDistinctCount -gt 0) {
            'pass'
        }
        else {
            'none'
        }
        fileTransferReceiveRecoveryOperationVerdict = if ($fileTransferReceiveRecoveryOperationUnresolvedCount -gt 0) { 'fail' } elseif ($fileTransferReceiveRecoveryOperationDistinctCount -gt 0) { 'pass' } else { 'none' }
        baselineManifest = $BaselinePath
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        goodputRegressionTolerancePercent = $GoodputRegressionTolerancePercent
        correctnessGatePolicy = 'strict_no_exceptions'
        networkVariancePolicy = 'live_transport_paired_rerun'
        networkVarianceNote = $networkVarianceNote
        regularNknExternalTransportWarningPolicy = 'capped_external_transport_churn_requires_clean_rerun'
        goodputRegressionPolicy = 'rerun_once_when_only_failure'
        requestedScenarios = @(ConvertTo-RouteAcceptanceSummaryStringArray -Values $RequestedScenarios -Limit 32)
        expectedRunCount = $ExpectedRunCount
        runCount = $script:RunResults.Count
        summaryJsonFailed = $false
        failureCount = $failureLines.Count
        correctnessFailureCount = $correctnessFailureLines.Count
        performanceFailureCount = $performanceFailureLines.Count
        runs = $runDtos
        failures = @($failureLines)
        correctnessFailures = @($correctnessFailureLines)
        performanceFailures = @($performanceFailureLines)
    }

    $jsonStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        if (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FORCE_SUMMARY_JSON_FAILURE') {
            throw 'forced_summary_json_failure'
        }

        $summaryJsonTimeoutMs = 10000
        $jsonText = $jsonSummary | ConvertTo-Json -Depth 8
        if ($jsonStopwatch.ElapsedMilliseconds -gt $summaryJsonTimeoutMs) {
            throw ("summary_json_timeout_ms={0}; elapsed_ms={1}" -f $summaryJsonTimeoutMs, $jsonStopwatch.ElapsedMilliseconds)
        }

        $jsonText | Set-Content -LiteralPath (Join-Path $RunRoot ("{0}-summary.json" -f $SummaryBaseName)) -Encoding UTF8
        $jsonText | Set-Content -LiteralPath (Join-Path $RunRoot 'route-acceptance-summary.json') -Encoding UTF8
    }
    catch {
        $diagnostics.summaryJsonFailed = $true
        $diagnostics.summaryJsonFailureReason = (($_ | Out-String).Trim() -replace '[\r\n]+', ' ')
        $verdict = 'FAIL'
        $jsonSummary['summaryJsonFailed'] = $true
        $failureLines += ("summary writer failed to write JSON: {0}" -f $diagnostics.summaryJsonFailureReason)
        for ($i = 0; $i -lt $textLines.Count; $i++) {
            if ($textLines[$i] -eq 'verdict=PASS') {
                $textLines[$i] = 'verdict=FAIL'
            }
            elseif ($textLines[$i] -eq 'correctness_verdict=PASS') {
                $textLines[$i] = 'correctness_verdict=FAIL'
            }
            elseif ($textLines[$i] -eq 'summary_json_failed=0') {
                $textLines[$i] = 'summary_json_failed=1'
            }
            elseif ($textLines[$i] -like 'failure_count=*') {
                $textLines[$i] = ("failure_count={0}" -f $failureLines.Count)
            }
            elseif ($textLines[$i] -like 'correctness_failure_count=*') {
                $textLines[$i] = ("correctness_failure_count={0}" -f ($correctnessFailureLines.Count + 1))
            }
        }

        $textLines += ("summary_json_failure_reason={0}" -f $diagnostics.summaryJsonFailureReason)
        $textLines += ("summary_writer_failure={0}" -f $diagnostics.summaryJsonFailureReason)
        $textLines | Set-Content -LiteralPath $summaryTxt -Encoding UTF8
        $textLines | Set-Content -LiteralPath (Join-Path $RunRoot 'route-acceptance-summary.txt') -Encoding UTF8
    }
    finally {
        $jsonStopwatch.Stop()
        $diagnostics.jsonWriteMs = [long]$jsonStopwatch.ElapsedMilliseconds
        $summaryStopwatch.Stop()
        $diagnostics.finalVerdict = $verdict
        $diagnostics.totalMs = [long]$summaryStopwatch.ElapsedMilliseconds
        $diagText = @(
            'Phase 5 Summary Writer Diagnostics',
            ("acceptance_phase={0}" -f $AcceptancePhase),
            ("summary_base_name={0}" -f $SummaryBaseName),
            ("artifact_root={0}" -f $RunRoot),
            ("requested_scenarios={0}" -f (Join-RouteAcceptanceTokenList -Values $RequestedScenarios)),
            ("expected_run_count={0}" -f $ExpectedRunCount),
            ("observed_run_count={0}" -f $script:RunResults.Count),
            ("dto_build_ms={0}" -f $diagnostics.dtoBuildMs),
            ("text_write_ms={0}" -f $diagnostics.textWriteMs),
            ("variance_note_write_ms={0}" -f $diagnostics.varianceNoteWriteMs),
            ("json_write_ms={0}" -f $diagnostics.jsonWriteMs),
            ("total_ms={0}" -f $diagnostics.totalMs),
            ("summary_json_failed={0}" -f ($(if ($diagnostics.summaryJsonFailed) { 1 } else { 0 }))),
            ("summary_json_failure_reason={0}" -f ($(if ([string]::IsNullOrWhiteSpace([string]$diagnostics.summaryJsonFailureReason)) { '(none)' } else { [string]$diagnostics.summaryJsonFailureReason }))),
            ("final_verdict={0}" -f $diagnostics.finalVerdict)
        )
        $diagText | Set-Content -LiteralPath (Join-Path $RunRoot 'phase5-summary-writer-diagnostics.txt') -Encoding UTF8
        try {
            $diagnostics | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $RunRoot 'phase5-summary-writer-diagnostics.json') -Encoding UTF8
        }
        catch {
            # Text diagnostics are already durable; do not let diagnostics JSON reintroduce the wrapper hang.
        }
    }
    return $verdict
}

function Write-RouteAcceptanceSummaryFiles {
    param([Parameter(Mandatory = $true)][string]$RunRoot)

    $failureLines = @()
    foreach ($result in $script:RunResults) {
        foreach ($failure in $result.failures) {
            $failureLines += ("{0}: {1}" -f $result.name, $failure)
        }
    }

    $verdict = if ($failureLines.Count -eq 0 -and $script:RunResults.Count -eq 4) { 'PASS' } else { 'FAIL' }
    if ($script:RunResults.Count -ne 4) {
        $failureLines += ("expected 4 runs, observed {0}" -f $script:RunResults.Count)
    }

    $textLines = @()
    $textLines += 'File Transfer Route Acceptance'
    $textLines += ("verdict={0}" -f $verdict)
    $textLines += ("artifact_root={0}" -f $RunRoot)
    $regularFloorText = "{0:F3}" -f [double]$script:RegularNknGoodputFloorBytesPerSecond
    $tunaFloorText = "{0:F3}" -f [double]$script:TunaGoodputFloorBytesPerSecond
    $textLines += ("regular_nkn_goodput_floor_bytes_per_second={0}" -f $regularFloorText)
    $textLines += ("tuna_goodput_floor_bytes_per_second={0}" -f $tunaFloorText)
    $textLines += ("run_count={0}" -f $script:RunResults.Count)
    $textLines += ("failure_count={0}" -f $failureLines.Count)
    $textLines += ''

    foreach ($result in $script:RunResults) {
        $prefix = $result.name
        $textLines += ("{0}.artifact_dir={1}" -f $prefix, $result.artifactDir)
        $textLines += ("{0}.route={1}" -f $prefix, $result.route)
        $textLines += ("{0}.protocol={1}" -f $prefix, $result.protocol)
        $textLines += ("{0}.runtime_profile={1}" -f $prefix, $result.runtimeProfile)
        $textLines += ("{0}.bridge_recovery_policy={1}" -f $prefix, $result.bridgeRecoveryPolicy)
        $textLines += ("{0}.route_consistency_verdict={1}" -f $prefix, $result.routeConsistencyVerdict)
        $textLines += ("{0}.operator_verdict={1}" -f $prefix, $result.operatorVerdict)
        $textLines += ("{0}.operator_accepted_with_warnings={1}" -f $prefix, ($(if ($result.operatorAcceptedWithWarnings) { 1 } else { 0 })))
        $resultWarningKinds = @($result.warningKinds)
        $textLines += ("{0}.warning_kinds={1}" -f $prefix, ($(if ($resultWarningKinds.Count -gt 0) { $resultWarningKinds -join ',' } else { '(none)' })))
        $textLines += ("{0}.attempt_count={1}" -f $prefix, $result.attemptCount)
        $textLines += ("{0}.retry_used={1}" -f $prefix, ($(if ($result.retryUsed) { 1 } else { 0 })))
        $textLines += ("{0}.selected_attempt={1}" -f $prefix, $result.selectedAttempt)
        if (-not [string]::IsNullOrWhiteSpace([string]$result.firstFailureReason)) {
            $textLines += ("{0}.first_failure_reason={1}" -f $prefix, $result.firstFailureReason)
        }
        $goodputText = "{0:F3}" -f (ConvertTo-RouteAcceptanceDouble -Value $result.goodputBytesPerSecond)
        $textLines += ("{0}.goodput_bytes_per_second={1}" -f $prefix, $goodputText)
        $textLines += ("{0}.bridge_bulk_send_failure_count={1}" -f $prefix, $result.bridgeBulkSendFailureCount)
        $textLines += ("{0}.failure_count={1}" -f $prefix, $result.failures.Count)
    }

    $textLines += ''
    $textLines += 'failures:'
    if ($failureLines.Count -eq 0) {
        $textLines += '(none)'
    }
    else {
        foreach ($failure in @($failureLines)) {
            $textLines += $failure
        }
    }

    Set-Content -LiteralPath (Join-Path $RunRoot 'route-acceptance-summary.txt') -Value $textLines -Encoding UTF8

    $jsonRuns = @(
        foreach ($result in $script:RunResults) {
            [ordered]@{
                name = $result.name
                artifactDir = $result.artifactDir
                expectedRoute = $result.expectedRoute
                expectedProtocol = $result.expectedProtocol
                route = $result.route
                protocol = $result.protocol
                runtimeProfile = $result.runtimeProfile
                bridgeRecoveryPolicy = $result.bridgeRecoveryPolicy
                routeConsistencyVerdict = $result.routeConsistencyVerdict
                operatorVerdict = $result.operatorVerdict
                operatorAcceptedWithWarnings = $result.operatorAcceptedWithWarnings
                warningKinds = @($result.warningKinds)
                attemptCount = $result.attemptCount
                retryUsed = $result.retryUsed
                selectedAttempt = $result.selectedAttempt
                firstFailureReason = $result.firstFailureReason
                controlledRestartAnalysis = $result.controlledRestartAnalysis
                completed = $result.completed
                integrityOk = $result.integrityOk
                goodputBytesPerSecond = $result.goodputBytesPerSecond
                bridgeBulkSendFailureCount = $result.bridgeBulkSendFailureCount
                failures = @($result.failures | ForEach-Object { [string]$_ })
            }
        }
    )

    $jsonSummary = [ordered]@{
        event = 'filetransfer_route_acceptance_summary'
        verdict = $verdict
        artifactRoot = $RunRoot
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        regularNknGoodputFloorBytesPerSecond = $script:RegularNknGoodputFloorBytesPerSecond
        tunaGoodputFloorBytesPerSecond = $script:TunaGoodputFloorBytesPerSecond
        failureCount = $failureLines.Count
        runs = $jsonRuns
        failures = @($failureLines)
    }

    $jsonSummary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $RunRoot 'route-acceptance-summary.json') -Encoding UTF8
    return $verdict
}

$repoRoot = Resolve-RepoRoot
$runRoot = $null
Push-Location $repoRoot
try {
    $runRoot = New-RouteAcceptanceTimestampedRoot -RepoRoot $repoRoot -RequestedRoot $ArtifactRoot
    $script:RouteAcceptanceStartedUtc = [DateTime]::UtcNow
    $script:RouteAcceptanceInitialGitState = Get-RouteAcceptanceEvidenceGitState -RepoRoot $repoRoot
    $script:RouteAcceptanceEvidenceHarnessFiles = @(Get-RouteAcceptanceEvidenceHarnessFiles -RepoRoot $repoRoot)
    $evidenceDryRunMode = [bool]$EvidenceDryRun -or
        (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_EVIDENCE_DRY_RUN')
    $fakeMode = $evidenceDryRunMode -or
        (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_GUI')
    $script:RouteAcceptanceExecutionMode = if ($evidenceDryRunMode) { 'evidence_dry_run' } elseif ($fakeMode) { 'fake' } else { 'live' }
    if ($evidenceDryRunMode -and -not [string]::IsNullOrWhiteSpace($EvidenceDryRunFixtureRoot)) {
        $script:RouteAcceptanceEvidenceDryRunFixtureRoot = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $EvidenceDryRunFixtureRoot)).Path
        $fixtureRunId = Get-RouteAcceptanceEvidenceDryRunFixtureRunId -FixtureRoot $script:RouteAcceptanceEvidenceDryRunFixtureRoot
        if ([string]::IsNullOrWhiteSpace($fixtureRunId)) {
            throw 'Evidence dry-run fixture does not contain a valid acceptanceRunId GUID.'
        }
        $script:RouteAcceptanceRunId = $fixtureRunId
    }
    else {
        $script:RouteAcceptanceRunId = [guid]::NewGuid().ToString('D')
    }

    if ($MatrixMode -eq 'phase4-ab-acceptance' -or $MatrixMode -eq 'phase5-analyzer-gui-acceptance') {
        $baseline = Read-RouteAcceptanceBaselineManifest -RepoRoot $repoRoot -Path $BaselineManifestPath
        $acceptancePhase = if ($MatrixMode -eq 'phase5-analyzer-gui-acceptance') { 'phase5' } else { 'phase4' }
        $scenarios = if ($acceptancePhase -eq 'phase5') {
            @(Get-Phase5RouteAcceptanceScenarios -BaselineManifest $baseline)
        }
        else {
            @(Get-Phase4RouteAcceptanceScenarios -BaselineManifest $baseline)
        }
        $requestedScenarioNames = @($ScenarioName | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { [string]$_ })
        if ($requestedScenarioNames.Count -gt 0) {
            $requestedScenarioSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($requestedScenarioName in $requestedScenarioNames) {
                [void]$requestedScenarioSet.Add($requestedScenarioName)
            }

            $scenarios = @($scenarios | Where-Object { $requestedScenarioSet.Contains([string]$_.Name) })
            if ($scenarios.Count -le 0) {
                throw ("No route-acceptance scenarios matched -ScenarioName {0}." -f ($requestedScenarioNames -join ','))
            }
        }
        $script:RouteAcceptanceEvidenceScenarioNames = @($scenarios | ForEach-Object { [string]$_.Name })
        $resolvedExePath = ''
        $resolvedWalletPath = ''
        $resolvedSidecarPath = ''
        $effectiveWalletPassword = ''
        $resolvedBridgeDir = ''

        if (-not $fakeMode) {
            $effectiveWalletPassword = $WalletPassword
            if ([string]::IsNullOrWhiteSpace($effectiveWalletPassword)) {
                $effectiveWalletPassword = [string]$env:NLINK_TUNA_TEST_WALLET_PASSWORD
            }

            if ([string]::IsNullOrWhiteSpace($effectiveWalletPassword)) {
                throw 'Provide -WalletPassword or set NLINK_TUNA_TEST_WALLET_PASSWORD before running route acceptance.'
            }

            $resolvedExePath = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $ExePath)).Path
            $resolvedWalletPath = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $WalletPath)).Path
            $resolvedSidecarPath = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $SidecarPath)).Path
            if ($acceptancePhase -eq 'phase5') {
                Assert-Phase5RouteAcceptanceExecutableFresh -RepoRoot $repoRoot -RunRoot $runRoot -ResolvedExePath $resolvedExePath
            }

            $resolvedBridgeDir = Ensure-RouteAcceptancePackagedBridgeRuntime -RepoRoot $repoRoot -ResolvedExePath $resolvedExePath -ArtifactDir $runRoot -Runtime $Runtime
            Assert-RouteAcceptanceCleanAppProcessState -ResolvedExePath $resolvedExePath -ArtifactDir $runRoot -Reason ("{0}_matrix_start" -f $acceptancePhase)
        }
        elseif ($evidenceDryRunMode) {
            $requestedExePath = Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $ExePath
            $resolvedExePath = if (Test-Path -LiteralPath $requestedExePath -PathType Leaf) {
                (Resolve-Path -LiteralPath $requestedExePath).Path
            }
            else {
                [System.IO.Path]::GetFullPath($requestedExePath)
            }
            $resolvedBridgeDir = Join-Path (Join-Path ([System.IO.Path]::GetDirectoryName($resolvedExePath)) 'bridge') $Runtime
        }
        else {
            $fakeEvidenceRuntime = New-RouteAcceptanceFakeEvidenceRuntime -RunRoot $runRoot
            $resolvedExePath = [string]$fakeEvidenceRuntime.RuntimePath
            $resolvedBridgeDir = [string]$fakeEvidenceRuntime.BridgeDir
        }

        $script:RouteAcceptanceEvidenceRuntimePath = $resolvedExePath
        $script:RouteAcceptanceEvidenceBridgeDir = $resolvedBridgeDir
        $script:RouteAcceptanceInitialEvidenceSnapshot = New-RouteAcceptanceEvidenceSnapshot `
            -RepoRoot $repoRoot `
            -PortableExecutablePath $resolvedExePath `
            -HarnessFiles $script:RouteAcceptanceEvidenceHarnessFiles `
            -BridgeDir $resolvedBridgeDir `
            -GitState $script:RouteAcceptanceInitialGitState

        $shouldRunPhase5Preflight = $acceptancePhase -eq 'phase5' -and
            -not (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_SKIP_PHASE5_PREFLIGHT') -and
            ((-not $fakeMode) -or
                (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL') -or
                (Test-RouteAcceptanceEnvEnabled -Name 'NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL_ONCE'))
        if ($shouldRunPhase5Preflight) {
            $preflightResult = Invoke-Phase5BridgeReadinessPreflightWithRetry -RepoRoot $repoRoot -RunRoot $runRoot -ResolvedExePath $resolvedExePath
            if (-not $preflightResult.Passed) {
                if ($requestedScenarioNames.Count -gt 0 -and (Test-Phase5BridgeReadinessPreflightQuarantinableFailure -PreflightResult $preflightResult)) {
                    Write-Phase5BridgeReadinessPreflightQuarantineSummary -RunRoot $runRoot -PreflightResult $preflightResult -Scope 'filtered_scenario_global_preflight'
                    Write-Host ("[FileTransfer Phase5 Preflight] quarantined setup-only failure {0}; continuing filtered scenario run. artifact_root={1}" -f $preflightResult.FailureReason, $runRoot) -ForegroundColor Yellow
                }
                else {
                    Complete-RouteAcceptanceRunEvidence `
                        -RepoRoot $repoRoot `
                        -RunRoot $runRoot `
                        -ScenarioNames @($scenarios | ForEach-Object { [string]$_.Name })
                    Write-Phase5PreflightFailureSummaryFiles -RunRoot $runRoot -BaselinePath ([string]$baseline.Path) -PreflightResult $preflightResult
                    Write-Host ("[FileTransfer Phase5 Analyzer/GUI Acceptance] verdict=FAIL; preflight_failure_class={0}; artifact_root={1}" -f $preflightResult.FailureClass, $runRoot) -ForegroundColor Red
                    exit 1
                }
            }
        }

        foreach ($scenario in $scenarios) {
            Invoke-Phase4RouteAcceptanceScenario `
                -Scenario $scenario `
                -RepoRoot $repoRoot `
                -RunRoot $runRoot `
                -FakeMode $fakeMode `
                -AcceptancePhase $acceptancePhase `
                -ResolvedExePath $resolvedExePath `
                -ResolvedWalletPath $resolvedWalletPath `
                -ResolvedSidecarPath $resolvedSidecarPath `
                -EffectiveWalletPassword $effectiveWalletPassword
        }

        Complete-RouteAcceptanceRunEvidence `
            -RepoRoot $repoRoot `
            -RunRoot $runRoot `
            -ScenarioNames @($scenarios | ForEach-Object { [string]$_.Name })

        if ($acceptancePhase -eq 'phase5') {
            $verdict = Write-Phase4RouteAcceptanceSummaryFiles -RunRoot $runRoot -BaselinePath ([string]$baseline.Path) -AcceptancePhase 'phase5' -SummaryBaseName 'phase5-analyzer-gui-acceptance' -SummaryTitle 'Phase 5 File Transfer Analyzer/GUI Acceptance' -ExpectedRunCount $scenarios.Count -RequestedScenarios $requestedScenarioNames
            Write-Host ("[FileTransfer Phase5 Analyzer/GUI Acceptance] verdict={0}; artifact_root={1}" -f $verdict, $runRoot) -ForegroundColor ($(if ($verdict -eq 'PASS') { 'Green' } else { 'Red' }))
        }
        else {
            $verdict = Write-Phase4RouteAcceptanceSummaryFiles -RunRoot $runRoot -BaselinePath ([string]$baseline.Path) -ExpectedRunCount $scenarios.Count -RequestedScenarios $requestedScenarioNames
            Write-Host ("[FileTransfer Phase4 A/B Acceptance] verdict={0}; artifact_root={1}" -f $verdict, $runRoot) -ForegroundColor ($(if ($verdict -eq 'PASS') { 'Green' } else { 'Red' }))
        }
        if ($verdict -ne 'PASS') {
            exit 1
        }

        exit 0
    }

    $regularQuickDir = Join-Path $runRoot 'regular-nkn-64mb-quick'
    $regularTargetDir = Join-Path $runRoot 'regular-nkn-128mb-target'
    $tunaNoFaultDir = Join-Path $runRoot 'tuna-128mb-no-fault'
    $tunaFallbackDir = Join-Path $runRoot 'tuna-128mb-fallback'

    foreach ($dir in @($regularQuickDir, $regularTargetDir, $tunaNoFaultDir, $tunaFallbackDir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    if ($fakeMode) {
        Write-RouteAcceptanceFakeRegularRun -ArtifactDir $regularQuickDir -PayloadSize '64MiB'
        Write-RouteAcceptanceFakeRegularRun -ArtifactDir $regularTargetDir -PayloadSize '128MiB'
        Write-RouteAcceptanceFakeTunaRun -ArtifactDir $tunaNoFaultDir -Route 'file_tuna_v4' -RouteMode 'preactivated'
        Write-RouteAcceptanceFakeFallbackRun -ArtifactDir $tunaFallbackDir
    }
    else {
        $effectiveWalletPassword = $WalletPassword
        if ([string]::IsNullOrWhiteSpace($effectiveWalletPassword)) {
            $effectiveWalletPassword = [string]$env:NLINK_TUNA_TEST_WALLET_PASSWORD
        }

        if ([string]::IsNullOrWhiteSpace($effectiveWalletPassword)) {
            throw 'Provide -WalletPassword or set NLINK_TUNA_TEST_WALLET_PASSWORD before running route acceptance.'
        }

        $resolvedExePath = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $ExePath)).Path
        $resolvedWalletPath = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $WalletPath)).Path
        $resolvedSidecarPath = (Resolve-Path -LiteralPath (Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $SidecarPath)).Path

        Invoke-RegularNknRouteAcceptanceRun -RepoRoot $repoRoot -ArtifactDir $regularQuickDir -PayloadSize '64MiB' -ResolvedExePath $resolvedExePath
        Invoke-RegularNknRouteAcceptanceRun -RepoRoot $repoRoot -ArtifactDir $regularTargetDir -PayloadSize '128MiB' -ResolvedExePath $resolvedExePath
        Invoke-TunaRouteAcceptanceRun -RepoRoot $repoRoot -ArtifactDir $tunaNoFaultDir -ResolvedExePath $resolvedExePath -ResolvedWalletPath $resolvedWalletPath -ResolvedSidecarPath $resolvedSidecarPath -EffectiveWalletPassword $effectiveWalletPassword -RouteMode 'preactivated' -Fault 'none'
        Invoke-TunaRouteAcceptanceRun -RepoRoot $repoRoot -ArtifactDir $tunaFallbackDir -ResolvedExePath $resolvedExePath -ResolvedWalletPath $resolvedWalletPath -ResolvedSidecarPath $resolvedSidecarPath -EffectiveWalletPassword $effectiveWalletPassword -RouteMode 'v4-restart-v6-fallback' -Fault 'switch-off'
    }

    Assert-RegularNknRouteAcceptanceRun -Name 'regular_nkn_64mb_quick' -ArtifactDir $regularQuickDir
    Assert-RegularNknRouteAcceptanceRun -Name 'regular_nkn_128mb_target' -ArtifactDir $regularTargetDir
    Assert-TunaRouteAcceptanceRun -Name 'tuna_128mb_no_fault' -ArtifactDir $tunaNoFaultDir -ExpectedRoute 'file_tuna_v4' -ExpectedBridgePolicy 'tuna_strict'
    Assert-TunaRouteAcceptanceRun -Name 'tuna_128mb_fallback' -ArtifactDir $tunaFallbackDir -ExpectedRoute 'post_tuna_fallback_v6' -ExpectedBridgePolicy 'post_tuna_fallback_strict'

    $verdict = Write-RouteAcceptanceSummaryFiles -RunRoot $runRoot
    Write-Host ("[FileTransfer Route Acceptance] verdict={0}; artifact_root={1}" -f $verdict, $runRoot) -ForegroundColor ($(if ($verdict -eq 'PASS') { 'Green' } else { 'Red' }))
    if ($verdict -ne 'PASS') {
        exit 1
    }

    exit 0
}
catch {
    $fatalMessage = ($_ | Out-String).Trim()
    if ($null -ne $runRoot -and -not [string]::IsNullOrWhiteSpace([string]$runRoot)) {
        $fatal = New-RouteAcceptanceRunResult -Name 'fatal' -ArtifactDir $runRoot -ExpectedRoute '(none)' -ExpectedProtocol 0
        Add-RouteAcceptanceFailure -Result $fatal -Message $fatalMessage
        $script:RunResults.Add($fatal) | Out-Null
        Set-RouteAcceptanceResultEvidence -Result $fatal
        try {
            if ($MatrixMode -eq 'phase4-ab-acceptance' -or $MatrixMode -eq 'phase5-analyzer-gui-acceptance') {
                $baselinePath = Resolve-RouteAcceptancePath -RepoRoot $repoRoot -Path $BaselineManifestPath
                if ($MatrixMode -eq 'phase5-analyzer-gui-acceptance') {
                    [void](Write-Phase4RouteAcceptanceSummaryFiles -RunRoot $runRoot -BaselinePath $baselinePath -AcceptancePhase 'phase5' -SummaryBaseName 'phase5-analyzer-gui-acceptance' -SummaryTitle 'Phase 5 File Transfer Analyzer/GUI Acceptance')
                }
                else {
                    [void](Write-Phase4RouteAcceptanceSummaryFiles -RunRoot $runRoot -BaselinePath $baselinePath)
                }
            }
            else {
                [void](Write-RouteAcceptanceSummaryFiles -RunRoot $runRoot)
            }
        }
        catch {
            Write-Warning ("Failed to write route acceptance summary after fatal error: {0}" -f $_.Exception.Message)
        }
    }

    Write-Error $fatalMessage
    exit 1
}
finally {
    if ($null -ne $script:RouteAcceptanceInitialEvidenceSnapshot -and
        -not $script:RouteAcceptanceEvidenceCompletionAttempted -and
        $null -ne $runRoot -and
        -not [string]::IsNullOrWhiteSpace([string]$runRoot)) {
        Complete-RouteAcceptanceRunEvidence `
            -RepoRoot $repoRoot `
            -RunRoot $runRoot `
            -ScenarioNames @($script:RouteAcceptanceEvidenceScenarioNames)
    }

    Pop-Location
}
