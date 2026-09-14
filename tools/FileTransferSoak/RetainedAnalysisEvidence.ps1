Set-StrictMode -Version Latest

if ($null -eq ('NLink.FileTransferEvidenceNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace NLink
{
    public static class FileTransferEvidenceNative
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
    }
}
'@
}

function Write-FileTransferEvidenceAtomicJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $temporaryPath = '{0}.{1}.tmp' -f $Path, ([guid]::NewGuid().ToString('N'))
    $stream = $null
    try {
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 16))
        $stream = [System.IO.FileStream]::new(
            $temporaryPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            $isWindowsPlatform = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
            if ($isWindowsPlatform) {
                $moveFileReplaceExisting = 0x1
                $moveFileWriteThrough = 0x8
                $replaced = $false
                $nativeError = 0
                foreach ($attempt in 1..40) {
                    if ([NLink.FileTransferEvidenceNative]::MoveFileEx(
                            $temporaryPath,
                            $Path,
                            ($moveFileReplaceExisting -bor $moveFileWriteThrough))) {
                        $replaced = $true
                        break
                    }
                    $nativeError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
                    if ($nativeError -notin @(5, 32, 33)) {
                        break
                    }
                    [System.Threading.Thread]::Sleep(5)
                }
                if (-not $replaced) {
                    throw [System.ComponentModel.Win32Exception]::new(
                        $nativeError,
                        "Atomic manifest replacement failed: $Path (win32=$nativeError)")
                }
            }
            else {
                $backupPath = '{0}.{1}.bak' -f $Path, ([guid]::NewGuid().ToString('N'))
                try {
                    [System.IO.File]::Replace($temporaryPath, $Path, $backupPath, $true)
                }
                finally {
                    if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                        Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
                    }
                }
            }
        }
        else {
            [System.IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-FileTransferEvidenceSha256 {
    param([AllowEmptyString()][string]$Path, $ReadLease = $null)

    if ($null -ne $ReadLease -and ($ReadLease -isnot [ref] -or $null -ne $ReadLease.Value)) {
        throw 'input_read_lease_requires_empty_reference'
    }

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ''
    }

    if ($null -ne $ReadLease) {
        $loaded = $false
        try {
            . (Join-Path (Split-Path -Parent $PSScriptRoot) 'FileTransferOps/LocalInputReadLease.ps1')
            $loaded = $true
        }
        catch { } # Optional module loading must not prevent the normal hash.
        if ($loaded) {
            $captured = [NLink.Tools.LocalInputReadLease]::HashAndHold($Path)
            try { $ReadLease.Value = $captured.Lease }
            catch {
                if ($null -ne $captured.Lease) { $captured.Lease.Dispose() }
            }
            return $captured.Sha256
        }
    }

    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

function Get-FileTransferEvidenceLineCount {
    param([AllowEmptyString()][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return -1L
    }

    $count = 0L
    $reader = [System.IO.StreamReader]::new($Path, [System.Text.UTF8Encoding]::new($false, $true), $true)
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

function Write-FileTransferTunaDirectEvidenceManifestProjection {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Source,
        [System.Collections.IDictionary]$Overrides = @{}
    )

    $propertyNames = @(
        'schemaVersion',
        'event',
        'acceptanceRunId',
        'scenarioRunId',
        'sourceCommit',
        'sourceDirty',
        'guiExitCode',
        'guiExitCodeAvailable',
        'routeMode',
        'payloadSize',
        'direction',
        'payerMode',
        'fault',
        'liveToggleSequence',
        'executablePath',
        'executableSha256',
        'harnessSha256',
        'wrapperSha256',
        'finalizationStatePath',
        'ledgerPath',
        'ledgerSha256',
        'writerEvidenceSlicePath',
        'writerEvidenceSliceSha256',
        'writerEvidenceSliceLineCount',
        'writerChronologyInversionCount',
        'writerChronologyCollisionCount',
        'writerChronologyVerdict',
        'capturePath',
        'captureSha256',
        'analyzerInputPath',
        'analyzerInputSha256',
        'derivedAnalyzerInputReceiptPath',
        'derivedAnalyzerInputReceiptSha256',
        'artifactFinalized',
        'cursorIntegrityVerdict',
        'writerEvidenceVersion',
        'writerIntegrityVerdict',
        'currentRuntimeProof',
        'retainedAnalysisOwner',
        'retainedAnalysisReceiptPaths',
        'retainedAnalysisReceiptSha256',
        'retainedAnalysisReceiptValid',
        'retainedAnalysisCompleted',
        'retainedAnalysisStatus',
        'retainedAnalysisExitCodeAvailable',
        'retainedAnalysisExitCode',
        'coordinatorSnapshotMode',
        'coordinatorSnapshotContractVersion',
        'coordinatorSnapshotCatalogVersion',
        'coordinatorSnapshotCatalogHash',
        'coordinatorSnapshotBoundaryHash',
        'coordinatorSnapshotParityVerdict',
        'generatedAtUtc')
    $projection = [ordered]@{}
    foreach ($name in $propertyNames) {
        if ($Overrides.Contains($name)) {
            $projection[$name] = $Overrides[$name]
            continue
        }

        $property = $Source.PSObject.Properties[$name]
        if ($null -ne $property) {
            $projection[$name] = $property.Value
        }
    }

    Write-FileTransferEvidenceAtomicJson -Path $Path -Value $projection
    return [pscustomobject]$projection
}

function Write-FileTransferRetainedAnalysisAttemptReceipt {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][ValidateSet('Wrapper', 'RouteAcceptance')][string]$Owner,
        [Parameter(Mandatory = $true)][string]$AttemptId,
        [AllowEmptyString()][string]$AcceptanceRunId = '',
        [AllowEmptyString()][string]$ScenarioRunId = '',
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][string]$InputSha256Before,
        [Parameter(Mandatory = $true)][long]$InputLineCountBefore,
        [Parameter(Mandatory = $true)][string]$LiveRouteProofMode,
        [Parameter(Mandatory = $true)][bool]$CompactRequested,
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][string]$StartedUtc,
        [Parameter(Mandatory = $true)][string]$EndedUtc,
        [Parameter(Mandatory = $true)][bool]$TimedOut,
        [Parameter(Mandatory = $true)][bool]$ExitCodeAvailable,
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [Parameter(Mandatory = $true)][bool]$CompletedProofArtifacts,
        [AllowNull()]$LocalPublicationCustody = $null
    )

    $receiptPath = Join-Path $ArtifactDir 'filetransfer-retained-analysis-attempt-receipt.json'
    Set-Variable -Name LocalPublicationCustody -Option Private -Value $LocalPublicationCustody
    if ($null -ne $LocalPublicationCustody) {
        $private:localCustodyType = 'NLink.Tools.LocalRetainedSourceRequest+ParentAdoptionCustody' -as [type]
        if ($null -eq $private:localCustodyType -or -not $private:localCustodyType.IsInstanceOfType($LocalPublicationCustody)) {
            $LocalPublicationCustody = $null
        }
    }
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        throw "retained_analysis_attempt_receipt_already_terminal:$receiptPath"
    }

    $inputSha256After = Get-FileTransferEvidenceSha256 -Path $LogPath
    $inputLineCountAfter = Get-FileTransferEvidenceLineCount -Path $LogPath
    $inputUnchanged = -not [string]::IsNullOrWhiteSpace($InputSha256Before) -and
        [string]::Equals($InputSha256Before, $inputSha256After, [System.StringComparison]::OrdinalIgnoreCase) -and
        $InputLineCountBefore -eq $inputLineCountAfter
    $status = if ($TimedOut) {
        'timed_out'
    }
    elseif (-not $ExitCodeAvailable) {
        'exit_unavailable'
    }
    elseif ($ExitCode -ne 0) {
        'failed'
    }
    elseif (-not $inputUnchanged) {
        'input_mutated'
    }
    elseif (-not $CompletedProofArtifacts) {
        'proof_incomplete'
    }
    else {
        'completed'
    }

    $artifact = {
        param([string]$Name)
        $path = Join-Path $ArtifactDir $Name
        [ordered]@{
            path = $path
            sha256 = Get-FileTransferEvidenceSha256 -Path $path
        }
    }
    $snapshotMode = ''
    $snapshotContractVersion = 0
    $snapshotCatalogVersion = ''
    $snapshotCatalogHash = ''
    $snapshotBoundaryHash = ''
    $snapshotParityVerdict = 'missing'
    try {
        $snapshotFull = Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-coordinator-snapshot-full-summary.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $snapshotParity = Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-coordinator-snapshot-parity-summary.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $snapshotMode = [string]$snapshotFull.Mode
        $snapshotContractVersion = [int]$snapshotFull.ContractVersion
        $snapshotCatalogVersion = [string]$snapshotFull.CatalogVersion
        $snapshotCatalogHash = [string]$snapshotFull.CatalogHash
        $snapshotBoundaryHash = [string]$snapshotFull.BoundaryHash
        $snapshotParityVerdict = [string]$snapshotParity.Verdict
    }
    catch {
    }
    $receipt = [ordered]@{
        schemaVersion = 1
        event = 'filetransfer_retained_analysis_attempt_receipt'
        owner = $Owner
        attemptId = $AttemptId
        acceptanceRunId = $AcceptanceRunId
        scenarioRunId = $ScenarioRunId
        analyzerInputPath = [System.IO.Path]::GetFullPath($LogPath)
        analyzerInputSha256Before = $InputSha256Before
        analyzerInputLineCountBefore = $InputLineCountBefore
        analyzerInputSha256After = $inputSha256After
        analyzerInputLineCountAfter = $inputLineCountAfter
        analyzerInputUnchanged = $inputUnchanged
        liveRouteProofMode = $LiveRouteProofMode
        compactRequested = $CompactRequested
        processId = $ProcessId
        startedUtc = $StartedUtc
        endedUtc = $EndedUtc
        timedOut = $TimedOut
        exitCodeAvailable = $ExitCodeAvailable
        exitCode = $ExitCode
        completedProofArtifacts = $CompletedProofArtifacts
        status = $status
        coordinatorSnapshotMode = $snapshotMode
        coordinatorSnapshotContractVersion = $snapshotContractVersion
        coordinatorSnapshotCatalogVersion = $snapshotCatalogVersion
        coordinatorSnapshotCatalogHash = $snapshotCatalogHash
        coordinatorSnapshotBoundaryHash = $snapshotBoundaryHash
        coordinatorSnapshotParityVerdict = $snapshotParityVerdict
        stdout = & $artifact 'filetransfer-retained-analysis.stdout.txt'
        stderr = & $artifact 'filetransfer-retained-analysis.stderr.txt'
        diagnostics = & $artifact 'filetransfer-retained-analysis-diagnostics.json'
        stageDiagnostics = & $artifact 'filetransfer-retained-analysis-stage-diagnostics.json'
        compactSummary = & $artifact 'filetransfer-retained-compact-summary.json'
        coordinatorSnapshotFullSummary = & $artifact 'filetransfer-coordinator-snapshot-full-summary.json'
        coordinatorSnapshotCompactSummary = & $artifact 'filetransfer-coordinator-snapshot-compact-summary.json'
        coordinatorSnapshotParitySummary = & $artifact 'filetransfer-coordinator-snapshot-parity-summary.json'
        routeSummary = & $artifact 'filetransfer-route-consistency-summary.txt'
        operatorVerdict = & $artifact 'filetransfer-operator-verdict.txt'
        stabilitySummary = & $artifact 'stability-gates-summary.txt'
    }
    $private:localReceiptPrepared = $false
    $private:localReceiptRecorded = $false
    $private:localReceiptReturn = $null
    if ($null -ne $LocalPublicationCustody) {
        try { $private:localReceiptPrepared = $LocalPublicationCustody.PrepareReceipt($receipt, $receiptPath) }
        catch { } # Optional custody cannot replace the primary receipt write.
    }
    try {
        Write-FileTransferEvidenceAtomicJson -Path $receiptPath -Value $receipt
        $private:localReceiptReturn = [pscustomobject]$receipt
        if ($private:localReceiptPrepared) {
            try { $private:localReceiptRecorded = $LocalPublicationCustody.RecordWrittenReceipt($receipt, $private:localReceiptReturn) }
            catch { }
        }
        return $private:localReceiptReturn
    }
    finally {
        if ($null -ne $LocalPublicationCustody -and -not $private:localReceiptRecorded) {
            try { $LocalPublicationCustody.Dispose() } catch { }
        }
    }
}

function Test-FileTransferRetainedAnalysisAttemptReceipt {
    param(
        [Parameter(Mandatory = $true)]$Receipt,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$ExpectedOwner,
        [AllowEmptyString()][string]$AcceptanceRunId = '',
        [AllowEmptyString()][string]$ScenarioRunId = '',
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][string]$LiveRouteProofMode,
        [bool]$CompactRequired = $false
    )

    try {
        $startedUtc = [DateTimeOffset]::MinValue
        $endedUtc = [DateTimeOffset]::MinValue
        $timestampsValid = [DateTimeOffset]::TryParse([string]$Receipt.startedUtc, [ref]$startedUtc) -and
            [DateTimeOffset]::TryParse([string]$Receipt.endedUtc, [ref]$endedUtc) -and
            $endedUtc -ge $startedUtc
        $currentInputSha256 = Get-FileTransferEvidenceSha256 -Path $LogPath
        $currentInputLineCount = Get-FileTransferEvidenceLineCount -Path $LogPath
        $inputBindingValid = -not [string]::IsNullOrWhiteSpace($currentInputSha256) -and
            [string]::Equals([string]$Receipt.analyzerInputSha256Before, [string]$Receipt.analyzerInputSha256After, [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals([string]$Receipt.analyzerInputSha256After, $currentInputSha256, [System.StringComparison]::OrdinalIgnoreCase) -and
            [long]$Receipt.analyzerInputLineCountBefore -eq [long]$Receipt.analyzerInputLineCountAfter -and
            [long]$Receipt.analyzerInputLineCountAfter -eq $currentInputLineCount

        $artifactBindings = @(
            [pscustomobject]@{ Receipt = $Receipt.stdout; Name = 'filetransfer-retained-analysis.stdout.txt'; Required = $true }
            [pscustomobject]@{ Receipt = $Receipt.stderr; Name = 'filetransfer-retained-analysis.stderr.txt'; Required = $true }
            [pscustomobject]@{ Receipt = $Receipt.diagnostics; Name = 'filetransfer-retained-analysis-diagnostics.json'; Required = $true }
            [pscustomobject]@{ Receipt = $Receipt.stageDiagnostics; Name = 'filetransfer-retained-analysis-stage-diagnostics.json'; Required = $true }
            [pscustomobject]@{ Receipt = $Receipt.compactSummary; Name = 'filetransfer-retained-compact-summary.json'; Required = ($CompactRequired -or [bool]$Receipt.compactRequested) }
            [pscustomobject]@{ Receipt = $Receipt.coordinatorSnapshotFullSummary; Name = 'filetransfer-coordinator-snapshot-full-summary.json'; Required = $true }
            [pscustomobject]@{ Receipt = $Receipt.coordinatorSnapshotCompactSummary; Name = 'filetransfer-coordinator-snapshot-compact-summary.json'; Required = $true }
            [pscustomobject]@{ Receipt = $Receipt.coordinatorSnapshotParitySummary; Name = 'filetransfer-coordinator-snapshot-parity-summary.json'; Required = $true }
            [pscustomobject]@{ Receipt = $Receipt.routeSummary; Name = 'filetransfer-route-consistency-summary.txt'; Required = $true }
            [pscustomobject]@{ Receipt = $Receipt.operatorVerdict; Name = 'filetransfer-operator-verdict.txt'; Required = $true }
            [pscustomobject]@{ Receipt = $Receipt.stabilitySummary; Name = 'stability-gates-summary.txt'; Required = $true }
        )
        $artifactBindingsValid = $true
        foreach ($binding in $artifactBindings) {
            $expectedPath = [System.IO.Path]::GetFullPath((Join-Path $ArtifactDir $binding.Name))
            $recordedPath = [System.IO.Path]::GetFullPath([string]$binding.Receipt.path)
            $currentSha256 = Get-FileTransferEvidenceSha256 -Path $expectedPath
            if (-not [string]::Equals($recordedPath, $expectedPath, [System.StringComparison]::OrdinalIgnoreCase) -or
                (-not [string]::IsNullOrWhiteSpace([string]$binding.Receipt.sha256) -and
                    -not [string]::Equals([string]$binding.Receipt.sha256, $currentSha256, [System.StringComparison]::OrdinalIgnoreCase)) -or
                ($binding.Required -and ([string]::IsNullOrWhiteSpace([string]$binding.Receipt.sha256) -or
                        [string]::IsNullOrWhiteSpace($currentSha256)))) {
                $artifactBindingsValid = $false
                break
            }
        }

        $snapshotBindingValid = $false
        try {
            $snapshotFull = Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-coordinator-snapshot-full-summary.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            $snapshotParity = Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-coordinator-snapshot-parity-summary.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            $snapshotBindingValid =
                [string]::Equals([string]$Receipt.coordinatorSnapshotMode, [string]$snapshotFull.Mode, [System.StringComparison]::Ordinal) -and
                [int]$Receipt.coordinatorSnapshotContractVersion -eq [int]$snapshotFull.ContractVersion -and
                [string]::Equals([string]$Receipt.coordinatorSnapshotCatalogVersion, [string]$snapshotFull.CatalogVersion, [System.StringComparison]::Ordinal) -and
                [string]::Equals([string]$Receipt.coordinatorSnapshotCatalogHash, [string]$snapshotFull.CatalogHash, [System.StringComparison]::Ordinal) -and
                [string]::Equals([string]$Receipt.coordinatorSnapshotBoundaryHash, [string]$snapshotFull.BoundaryHash, [System.StringComparison]::Ordinal) -and
                [string]::Equals([string]$Receipt.coordinatorSnapshotParityVerdict, [string]$snapshotParity.Verdict, [System.StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $snapshotBindingValid = $false
        }

        return [int]$Receipt.schemaVersion -eq 1 -and
            [string]::Equals([string]$Receipt.event, 'filetransfer_retained_analysis_attempt_receipt', [System.StringComparison]::Ordinal) -and
            [string]::Equals([string]$Receipt.owner, $ExpectedOwner, [System.StringComparison]::Ordinal) -and
            -not [string]::IsNullOrWhiteSpace([string]$Receipt.attemptId) -and
            [string]::Equals([string]$Receipt.acceptanceRunId, $AcceptanceRunId, [System.StringComparison]::Ordinal) -and
            [string]::Equals([string]$Receipt.scenarioRunId, $ScenarioRunId, [System.StringComparison]::Ordinal) -and
            [string]::Equals([string]$Receipt.analyzerInputPath, [System.IO.Path]::GetFullPath($LogPath), [System.StringComparison]::OrdinalIgnoreCase) -and
            $inputBindingValid -and
            [bool]$Receipt.analyzerInputUnchanged -and
            [string]::Equals([string]$Receipt.liveRouteProofMode, $LiveRouteProofMode, [System.StringComparison]::Ordinal) -and
            (-not $CompactRequired -or [bool]$Receipt.compactRequested) -and
            $timestampsValid -and
            $artifactBindingsValid -and
            $snapshotBindingValid -and
            -not [bool]$Receipt.timedOut -and
            [bool]$Receipt.exitCodeAvailable -and
            [int]$Receipt.exitCode -eq 0 -and
            [bool]$Receipt.completedProofArtifacts -and
            [string]::Equals([string]$Receipt.status, 'completed', [System.StringComparison]::Ordinal)
    }
    catch {
        return $false
    }
}

function Update-FileTransferTunaDirectEvidenceManifestFromReceipt {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][ValidateSet('Wrapper', 'RouteAcceptance')][string]$ExpectedOwner,
        [AllowEmptyString()][string]$AcceptanceRunId = '',
        [AllowEmptyString()][string]$ScenarioRunId = '',
        [Parameter(Mandatory = $true)][string]$ExePath,
        [Parameter(Mandatory = $true)][string]$RouteMode,
        [Parameter(Mandatory = $true)][string]$PayloadSize,
        [Parameter(Mandatory = $true)][string]$Direction,
        [Parameter(Mandatory = $true)][string]$PayerMode,
        [Parameter(Mandatory = $true)][string]$Fault,
        [AllowEmptyString()][string]$LiveToggleSequence = '',
        [Parameter(Mandatory = $true)][string]$LiveRouteProofMode
    )

    $manifestPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-evidence-manifest.json'
    $statePath = Join-Path $ArtifactDir 'filetransfer-gui-evidence-finalization-state.json'
    $ledgerPath = Join-Path $ArtifactDir 'filetransfer-gui-log-ledger.json'
    $capturePath = Join-Path $ArtifactDir 'filetransfer-gui-log-ledger-slice.log'
    $inputPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    $receiptPath = Join-Path $ArtifactDir 'filetransfer-retained-analysis-attempt-receipt.json'
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $state = Get-Content -LiteralPath $statePath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $ledger = Get-Content -LiteralPath $ledgerPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $summary = Get-Content -LiteralPath $summaryPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $receipt = Get-Content -LiteralPath $receiptPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "tuna_direct_manifest_reconciliation_input_invalid:$($_.Exception.Message)"
    }

    $identityMatches = [string]::Equals([string]$manifest.acceptanceRunId, $AcceptanceRunId, [System.StringComparison]::Ordinal) -and
        [string]::Equals([string]$manifest.scenarioRunId, $ScenarioRunId, [System.StringComparison]::Ordinal) -and
        [string]::Equals([string]$state.acceptanceRunId, $AcceptanceRunId, [System.StringComparison]::Ordinal) -and
        [string]::Equals([string]$state.scenarioRunId, $ScenarioRunId, [System.StringComparison]::Ordinal) -and
        [string]::Equals([string]$ledger.acceptanceRunId, $AcceptanceRunId, [System.StringComparison]::Ordinal) -and
        [string]::Equals([string]$ledger.scenarioRunId, $ScenarioRunId, [System.StringComparison]::Ordinal)
    $configurationMatches = [string]::Equals([string]$manifest.routeMode, $RouteMode, [System.StringComparison]::Ordinal) -and
        [string]::Equals([string]$manifest.payloadSize, $PayloadSize, [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string]$manifest.direction, $Direction, [System.StringComparison]::Ordinal) -and
        [string]::Equals([string]$manifest.payerMode, $PayerMode, [System.StringComparison]::Ordinal) -and
        [string]::Equals([string]$manifest.fault, $Fault, [System.StringComparison]::Ordinal) -and
        [string]::Equals([string]$manifest.liveToggleSequence, $LiveToggleSequence, [System.StringComparison]::Ordinal)
    $captureSha = Get-FileTransferEvidenceSha256 -Path $capturePath
    $inputSha = Get-FileTransferEvidenceSha256 -Path $inputPath
    $captureMatches = -not [string]::IsNullOrWhiteSpace($captureSha) -and
        [string]::Equals($captureSha, [string]$ledger.retainedSlice.sha256, [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($captureSha, [string]$state.captureSha256, [System.StringComparison]::OrdinalIgnoreCase)
    $inputMatches = -not [string]::IsNullOrWhiteSpace($inputSha) -and
        [string]::Equals($inputSha, [string]$ledger.analyzerInput.captureSha256, [System.StringComparison]::OrdinalIgnoreCase)
    if ($manifest.PSObject.Properties.Name -contains 'derivedAnalyzerInputReceiptPath' -and
        -not [string]::IsNullOrWhiteSpace([string]$manifest.derivedAnalyzerInputReceiptPath)) {
        $inputMatches = $inputMatches -and
            [string]::Equals(
                (Get-FileTransferEvidenceSha256 -Path ([string]$manifest.derivedAnalyzerInputReceiptPath)),
                [string]$manifest.derivedAnalyzerInputReceiptSha256,
                [System.StringComparison]::OrdinalIgnoreCase)
    }
    $artifactFinalized = $identityMatches -and $configurationMatches -and
        -not [bool]$state.timedOut -and
        [string]::Equals([string]$state.lastCompletedStage, 'gui_child_exited', [System.StringComparison]::Ordinal) -and
        [bool]$state.exitCodeAvailable -and [int]$state.exitCode -eq 0 -and
        $captureMatches -and $inputMatches
    $cursorPass = [string]::Equals([string]$ledger.cursorIntegrityVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)
    $writerPass = [int]$ledger.writerEvidenceVersion -eq 3 -and
        [string]::Equals([string]$ledger.writerIntegrityVerdict, 'pass', [System.StringComparison]::OrdinalIgnoreCase)
    $summaryValid = [string]::Equals([string]$summary.event, 'filetransfer_tuna_gui_handoff_fallback_summary', [System.StringComparison]::Ordinal) -and
        [string]::Equals([string]$summary.routeMode, $RouteMode, [System.StringComparison]::Ordinal) -and
        [bool]$summary.completed -and [bool]$summary.integrityOk
    $receiptValid = Test-FileTransferRetainedAnalysisAttemptReceipt `
        -Receipt $receipt `
        -ArtifactDir $ArtifactDir `
        -ExpectedOwner $ExpectedOwner `
        -AcceptanceRunId $AcceptanceRunId `
        -ScenarioRunId $ScenarioRunId `
        -LogPath $inputPath `
        -LiveRouteProofMode $LiveRouteProofMode `
        -CompactRequired ($ExpectedOwner -eq 'RouteAcceptance')
    $sourceCommit = [string](& git -C $RepoRoot rev-parse HEAD 2>$null)
    $sourceDirty = @(& git -C $RepoRoot status --porcelain 2>$null).Count -gt 0
    $binaryMatches = [string]::Equals((Get-FileTransferEvidenceSha256 -Path $ExePath), [string]$manifest.executableSha256, [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals((Get-FileTransferEvidenceSha256 -Path (Join-Path $RepoRoot 'tools\GuiSmoke-Windows.ps1')), [string]$manifest.harnessSha256, [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals((Get-FileTransferEvidenceSha256 -Path (Join-Path $RepoRoot 'tools\Run-FileTransferTunaGuiSmoke.ps1')), [string]$manifest.wrapperSha256, [System.StringComparison]::OrdinalIgnoreCase)
    $currentRuntimeProof = $artifactFinalized -and $cursorPass -and $writerPass -and
        $summaryValid -and $receiptValid -and $binaryMatches -and -not $sourceDirty -and
        [string]::Equals($sourceCommit.Trim(), [string]$manifest.sourceCommit, [System.StringComparison]::OrdinalIgnoreCase)

    $updates = [ordered]@{
        artifactFinalized = $artifactFinalized
        cursorIntegrityVerdict = $(if ($cursorPass) { 'pass' } else { 'fail' })
        writerEvidenceVersion = [int]$ledger.writerEvidenceVersion
        writerIntegrityVerdict = $(if ($writerPass) { 'pass' } else { 'fail' })
        currentRuntimeProof = $currentRuntimeProof
        retainedAnalysisOwner = $ExpectedOwner
        retainedAnalysisReceiptPaths = @($receiptPath)
        retainedAnalysisReceiptSha256 = Get-FileTransferEvidenceSha256 -Path $receiptPath
        retainedAnalysisReceiptValid = $receiptValid
        retainedAnalysisCompleted = $receiptValid
        retainedAnalysisStatus = [string]$receipt.status
        retainedAnalysisExitCodeAvailable = [bool]$receipt.exitCodeAvailable
        retainedAnalysisExitCode = [int]$receipt.exitCode
        coordinatorSnapshotMode = [string]$receipt.coordinatorSnapshotMode
        coordinatorSnapshotContractVersion = [int]$receipt.coordinatorSnapshotContractVersion
        coordinatorSnapshotCatalogVersion = [string]$receipt.coordinatorSnapshotCatalogVersion
        coordinatorSnapshotCatalogHash = [string]$receipt.coordinatorSnapshotCatalogHash
        coordinatorSnapshotBoundaryHash = [string]$receipt.coordinatorSnapshotBoundaryHash
        coordinatorSnapshotParityVerdict = [string]$receipt.coordinatorSnapshotParityVerdict
        analyzerInputSha256 = $inputSha
        captureSha256 = $captureSha
        ledgerSha256 = Get-FileTransferEvidenceSha256 -Path $ledgerPath
        sourceDirty = $sourceDirty
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    Write-FileTransferTunaDirectEvidenceManifestProjection `
        -Path $manifestPath `
        -Source $manifest `
        -Overrides $updates | Out-Null
    return [pscustomobject]@{
        ManifestPath = $manifestPath
        CurrentRuntimeProof = $currentRuntimeProof
        ReceiptValid = $receiptValid
    }
}
