param([string]$RequestPath = '')

Set-StrictMode -Version Latest
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'GuiSmokeContentHash.ps1')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'FileTransferSoak\DataPathRejectedIngressExplanation.ps1')

# Safe to load in the GUI parent: parser/shadow initialization occurs only in
# the separate request process, never in an active retained analyzer.
function Get-TunaGuiDiagnosticContentHash {
    param([AllowEmptyCollection()][string[]]$Lines = @())
    return Get-GuiSmokeLogLedgerHash -Value ([string]::Join("`n", $Lines))
}

function New-TunaGuiDataPathDiagnosticReceipt {
    param([string]$Status, [string]$Reason, [long]$ElapsedMilliseconds = 0)
    return [pscustomobject][ordered]@{
        status=$Status; reason=$Reason; authorityEffect='none'; elapsedMilliseconds=$ElapsedMilliseconds
        countDomain='identical_receive_record'; inputHashDomain='utf8-lines-lf-v1'
        evidenceInputs=@(); candidateCount=$null; sharedMatcherRecoveredCount=$null
        unresolvedCandidateCount=$null; records=@()
    }
}

function Get-TunaGuiDiagnosticRemainingMilliseconds {
    param([System.Collections.IDictionary]$Context)
    if ($null -eq $Context -or $Context['Stopwatch'] -isnot [Diagnostics.Stopwatch] -or
        $null -eq $Context['BudgetMilliseconds']) { return 0L }
    if (-not $Context['Stopwatch'].IsRunning) { return 0L }
    $remaining = [long]$Context['BudgetMilliseconds'] - $Context['Stopwatch'].ElapsedMilliseconds
    $statePath = [string]$env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH
    if (-not [string]::IsNullOrWhiteSpace($statePath)) {
        try {
            $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            $deadline = [DateTimeOffset]::Parse([string]$state.deadlineUtc,
                [Globalization.CultureInfo]::InvariantCulture)
            $remaining = [Math]::Min($remaining,
                [long][Math]::Floor(($deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds) - 10000L)
        }
        catch { return 0L }
    }
    return [long][Math]::Max(0L, $remaining)
}

function Invoke-TunaGuiDataPathDiagnosticRequest {
    param([Parameter(Mandatory = $true)]$Request)
    . (Join-Path $PSScriptRoot 'GuiRejectedIngressShadow.ps1')
    $lines = [Collections.Generic.List[string]]::new()
    $ranges = [Collections.Generic.List[object]]::new()
    foreach ($inputEvidence in $Request.evidenceInputs) {
        [string[]]$sourceLines = [IO.File]::ReadAllLines([string]$inputEvidence.path,
            [Text.UTF8Encoding]::new($false, $true))
        if ($sourceLines.Count -ne $inputEvidence.lineCount -or
            (Get-TunaGuiDiagnosticContentHash -Lines $sourceLines) -cne $inputEvidence.contentSha256) {
            throw 'diagnostic_input_changed'
        }
        $ranges.Add([pscustomobject]@{ Path=[string]$inputEvidence.path; Start=$lines.Count; Count=$sourceLines.Count })
        $lines.AddRange($sourceLines)
    }
    $shadow = Get-TunaGuiRejectedIngressShadowDiagnostics -Lines $lines.ToArray() -SourceRanges $ranges.ToArray()
    $receipt = New-TunaGuiDataPathDiagnosticReceipt -Status 'completed' -Reason '(none)'
    $receipt.evidenceInputs = @($Request.evidenceInputs)
    $receipt.records = @($shadow.SharedDiagnostics | ForEach-Object {
        $record = [pscustomobject][ordered]@{
            sourcePath=$_.SourcePath; sourceLineNumber=$_.SourceLineNumber; evidenceSequence=$_.OriginalEvidenceSequence
            sessionId=$_.SessionId; transferId=$_.TransferId; writerId=$_.WriterId; writerSequence=$_.WriterSequence
            sharedMatcherRecovered=$_.Recovered; rejectionChainMatched=$_.RejectionChainMatched
            receiveDisposition=$_.ReceiveDisposition; reason=$_.Reason; variant=$_.Variant
        }
        if ($null -ne $_.PSObject.Properties['Explanation']) {
            $record | Add-Member -NotePropertyName explanation -NotePropertyValue $_.Explanation
        }
        $record
    })
    $receipt.candidateCount = $receipt.records.Count
    $receipt.sharedMatcherRecoveredCount = @($receipt.records | Where-Object { $_.sharedMatcherRecovered }).Count
    $receipt.unresolvedCandidateCount = $receipt.candidateCount - $receipt.sharedMatcherRecoveredCount
    return $receipt
}

function Invoke-TunaGuiDataPathDiagnosticCompanion {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir,
        [AllowNull()][System.Collections.IDictionary]$Context)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = $null
    try {
        # A missing or malformed optional carrier cannot reject the primary.
        $eligible = $false
        $inputs = @()
        $sources = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        try {
            $e = $Context['Eligibility']
            $eligible = $e.eligible -is [bool] -and $e.eligible -and
                $e.snapshotMode -ceq 'SnapshotV1Authoritative' -and
                $e.artifactEvidenceVerdict -ceq 'pass' -and $e.parityVerdict -ceq 'pass' -and
                $e.streamComplete -is [bool] -and $e.streamComplete -and
                $e.authorityBoundaryPresent -is [bool] -and -not $e.authorityBoundaryPresent
            $inputs = @($e.evidenceInputs)
            if ($inputs.Count -eq 0) { $eligible = $false }
            if (-not [string]::Equals(
                (ConvertTo-Json -InputObject @($inputs.path) -Compress),
                (ConvertTo-Json -InputObject @($Context['EvidencePaths']) -Compress),
                [StringComparison]::Ordinal)) { $eligible = $false }
            foreach ($inputEvidence in $inputs) {
                if (-not [IO.Path]::IsPathRooted([string]$inputEvidence.path) -or
                    $inputEvidence.contentSha256 -cnotmatch '^[0-9a-f]{64}$' -or
                    ($inputEvidence.lineCount -isnot [int] -and $inputEvidence.lineCount -isnot [long]) -or
                    $inputEvidence.lineCount -lt 0) { $eligible = $false }
                $sources.Add([string]$inputEvidence.path, $inputEvidence)
            }
        }
        catch { $eligible = $false }
        if (-not $eligible) {
            return New-TunaGuiDataPathDiagnosticReceipt 'not_evaluated' 'diagnostic_ineligible'
        }
        if ((Get-TunaGuiDiagnosticRemainingMilliseconds $Context) -le 0) {
            return New-TunaGuiDataPathDiagnosticReceipt 'not_evaluated' 'diagnostic_budget_exhausted'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-error.json') -PathType Leaf)) {
            return New-TunaGuiDataPathDiagnosticReceipt 'not_evaluated' 'diagnostic_primary_not_persisted'
        }
        $requestId = [guid]::NewGuid().ToString('N')
        $requestFile = Join-Path $ArtifactDir 'filetransfer-tuna-gui-data-path-diagnostic-request.json'
        $responseFile = Join-Path $ArtifactDir 'filetransfer-tuna-gui-data-path-diagnostic-response.json'
        Remove-Item -LiteralPath $responseFile -Force -ErrorAction SilentlyContinue
        Write-GuiSmokeAtomicJson -Path $requestFile -Value ([ordered]@{
            requestId=$requestId; evidenceInputs=$inputs; responsePath=$responseFile
        })
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-RequestPath', $requestFile)
        $argumentLine = ($arguments | ForEach-Object { ConvertTo-GuiSmokeProcessArgument ([string]$_) }) -join ' '
        $remaining = Get-TunaGuiDiagnosticRemainingMilliseconds $Context
        if ($remaining -le 0) {
            return New-TunaGuiDataPathDiagnosticReceipt 'not_evaluated' 'diagnostic_budget_exhausted'
        }
        $process = Start-Process -FilePath 'powershell' -ArgumentList $argumentLine -WindowStyle Hidden `
            -WorkingDirectory $ArtifactDir -PassThru `
            -RedirectStandardOutput (Join-Path $ArtifactDir 'filetransfer-tuna-gui-data-path-diagnostic.stdout.log') `
            -RedirectStandardError (Join-Path $ArtifactDir 'filetransfer-tuna-gui-data-path-diagnostic.stderr.log')
        [void]$process.Handle
        $remaining = Get-TunaGuiDiagnosticRemainingMilliseconds $Context
        if ($remaining -le 0 -or -not $process.WaitForExit([int][Math]::Min([int]::MaxValue, $remaining))) {
            return New-TunaGuiDataPathDiagnosticReceipt 'timed_out' 'diagnostic_timeout' $watch.ElapsedMilliseconds
        }
        $process.WaitForExit()
        $process.Refresh()
        if ((Get-TunaGuiDiagnosticRemainingMilliseconds $Context) -le 0) {
            return New-TunaGuiDataPathDiagnosticReceipt 'timed_out' 'diagnostic_timeout' $watch.ElapsedMilliseconds
        }
        if ($null -eq $process.ExitCode -or $process.ExitCode -ne 0) { throw 'diagnostic_child_failed' }
        $response = Get-Content -LiteralPath $responseFile -Raw | ConvertFrom-Json
        if ($response.schemaVersion -ne 1 -or $response.requestId -cne $requestId) { throw 'diagnostic_response_invalid' }
        $receipt = $response.nonAuthorityDataPathDiagnostics
        if ($receipt.status -ceq 'failed' -and $receipt.reason -ceq 'diagnostic_input_changed') {
            return New-TunaGuiDataPathDiagnosticReceipt 'failed' 'diagnostic_input_changed' $watch.ElapsedMilliseconds
        }
        if ($receipt.status -cne 'completed' -or $receipt.authorityEffect -cne 'none' -or
            $receipt.countDomain -cne 'identical_receive_record' -or $receipt.inputHashDomain -cne 'utf8-lines-lf-v1' -or
            (ConvertTo-Json -InputObject @($receipt.evidenceInputs) -Depth 5 -Compress) -cne
                (ConvertTo-Json -InputObject $inputs -Depth 5 -Compress)) { throw 'diagnostic_response_invalid' }
        $recovered = 0
        foreach ($record in @($receipt.records)) {
            $source = $null
            $sourceMatched = $sources.TryGetValue([string]$record.sourcePath, [ref]$source)
            if ($record.sharedMatcherRecovered -isnot [bool] -or $record.rejectionChainMatched -isnot [bool] -or
                -not $sourceMatched -or $record.sourceLineNumber -le 0 -or
                $record.sourceLineNumber -gt $source.lineCount -or
                ($null -ne $record.evidenceSequence -and $record.evidenceSequence -le 0)) { throw 'diagnostic_response_invalid' }
            $explanation = $record.PSObject.Properties['explanation']
            if ($null -ne $explanation) {
                $expected = Get-FileTransferRejectedIngressExplanation -Recovered $record.sharedMatcherRecovered `
                    -RejectionChainMatched $record.rejectionChainMatched -ReceiveDisposition $record.receiveDisposition `
                    -Reason $record.reason -Variant $record.variant
                if ($explanation.Value -isnot [string] -or $null -eq $expected -or
                    $explanation.Value -cne $expected) { throw 'diagnostic_response_invalid' }
            }
            if ($record.sharedMatcherRecovered) { $recovered++ }
        }
        foreach ($name in @('candidateCount','sharedMatcherRecoveredCount','unresolvedCandidateCount')) {
            if ($receipt.$name -isnot [int] -and $receipt.$name -isnot [long]) { throw 'diagnostic_response_invalid' }
        }
        if ($receipt.candidateCount -ne @($receipt.records).Count -or
            $receipt.sharedMatcherRecoveredCount -ne $recovered -or
            $receipt.unresolvedCandidateCount -ne (@($receipt.records).Count - $recovered)) {
            throw 'diagnostic_response_invalid'
        }
        $receipt.elapsedMilliseconds = $watch.ElapsedMilliseconds
        if ((Get-TunaGuiDiagnosticRemainingMilliseconds $Context) -le 0) {
            return New-TunaGuiDataPathDiagnosticReceipt 'timed_out' 'diagnostic_timeout' $watch.ElapsedMilliseconds
        }
        return $receipt
    }
    catch { return New-TunaGuiDataPathDiagnosticReceipt 'failed' 'diagnostic_failed' $watch.ElapsedMilliseconds }
    finally {
        if ($null -ne $process) {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
                if (-not $process.WaitForExit(5000)) { throw 'diagnostic_child_cleanup_failed' }
            }
            $process.Dispose()
        }
    }
}

function Write-TunaGuiDataPathDiagnosticSummary {
    param([string]$Path, [System.Collections.IDictionary]$PrimarySummary, $Diagnostics)
    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
        $summary = [ordered]@{}
        foreach ($key in $PrimarySummary.Keys) { $summary[$key] = $PrimarySummary[$key] }
        $summary['nonAuthorityDataPathDiagnostics'] = $Diagnostics
        Write-GuiSmokeAtomicJson -Path $Path -Value $summary
        return $true
    }
    catch { return $false } # The completed primary file has already been persisted.
}

if (-not [string]::IsNullOrWhiteSpace($RequestPath)) {
    $ErrorActionPreference = 'Stop'
    $request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
    try { $diagnostics = Invoke-TunaGuiDataPathDiagnosticRequest -Request $request }
    catch {
        $reason = if ($_.Exception.Message -ceq 'diagnostic_input_changed') { 'diagnostic_input_changed' } else { 'diagnostic_failed' }
        $diagnostics = New-TunaGuiDataPathDiagnosticReceipt 'failed' $reason
    }
    # Only the parent reads this private response, and only after child exit.
    [IO.File]::WriteAllText([string]$request.responsePath, (ConvertTo-Json -Depth 12 -InputObject ([ordered]@{
        schemaVersion=1; requestId=$request.requestId; nonAuthorityDataPathDiagnostics=$diagnostics
    })), [Text.UTF8Encoding]::new($false))
}
