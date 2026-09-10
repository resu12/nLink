param([string]$RepoRoot, [string]$ArtifactRoot, [string]$FixturePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/GuiDataPathDiagnosticCompanion.ps1')
$tokens = $null; $errors = $null
$guiAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$errors)
if (@($errors).Count) { throw 'GUI parse failed' }
foreach ($name in @('Write-GuiSmokeAtomicJson', 'ConvertTo-GuiSmokeProcessArgument')) {
    $definition = $guiAst.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    Invoke-Expression $definition.Extent.Text
}

$primary = [ordered]@{ failurePhase='evidence_harness'; failureReason='exact-primary';
    classificationStatus='completed'; classificationComplete=$true;
    classificationEnrichmentStatus='completed'; coordinatorUnresolvedCount=0 }
$primaryPath = Join-Path $ArtifactRoot 'filetransfer-tuna-gui-error.json'
Write-GuiSmokeAtomicJson -Path $primaryPath -Value $primary
$primaryBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($primaryPath))
$lines = [IO.File]::ReadAllLines($FixturePath)
$inputs = @([pscustomobject]@{ path=$FixturePath; lineCount=$lines.Count;
    contentSha256=(Get-TunaGuiDiagnosticContentHash -Lines $lines) })
$eligibility = [pscustomobject]@{ snapshotMode='SnapshotV1Authoritative';
    artifactEvidenceVerdict='pass'; streamComplete=$true; parityVerdict='pass';
    authorityBoundaryPresent=$false; eligible=$true; evidenceInputs=$inputs }
function New-TestContext {
    return @{ Eligibility=$eligibility; EvidencePaths=@($inputs.path);
        Stopwatch=[Diagnostics.Stopwatch]::StartNew(); BudgetMilliseconds=20000L }
}
function Assert-PrimaryUnchanged {
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($primaryPath)) -cne $primaryBytes) {
        throw 'Companion changed durable primary'
    }
}

# The real child must run only after the primary file exists. Its diagnostics
# cannot rewrite that file, even when the shared matcher reports recovery.
$result = Invoke-TunaGuiDataPathDiagnosticCompanion -ArtifactDir $ArtifactRoot -Context (New-TestContext)
if ($result.status -cne 'completed' -or $result.candidateCount -ne 1 -or
    $result.sharedMatcherRecoveredCount -ne 1 -or $result.unresolvedCandidateCount -ne 0) {
    throw "Real child result: $($result | ConvertTo-Json -Depth 8 -Compress)"
}
if ($result.records[0].sourcePath -cne $FixturePath -or
    $result.records[0].transferId -cne 'transfer_file_tuna_v4_receive_regular_rejected' -or
    $null -ne $result.records[0].evidenceSequence -or
    $result.records[0].sourceLineNumber -le 0 -or $result.records[0].writerSequence -le 0) {
    throw 'Candidate provenance missing'
}
Assert-PrimaryUnchanged
foreach ($change in @('missing','invalid','incomplete','boundary','parity','legacy','string_boolean','budget','path','stopped_clock')) {
    $context = New-TestContext
    $context.Eligibility = $eligibility | ConvertTo-Json -Depth 5 | ConvertFrom-Json
    switch ($change) {
        'missing' { $context.Remove('Eligibility') }
        'invalid' { $context.Eligibility.artifactEvidenceVerdict='fail' }
        'incomplete' { $context.Eligibility.streamComplete=$false }
        'boundary' { $context.Eligibility.authorityBoundaryPresent=$true }
        'parity' { $context.Eligibility.parityVerdict='fail' }
        'legacy' { $context.Eligibility.snapshotMode='LegacyV0' }
        'string_boolean' { $context.Eligibility.eligible='true' }
        'budget' { $context.BudgetMilliseconds=0L }
        'path' { $context.EvidencePaths=@('C:\unrequested-input.log') }
        'stopped_clock' { $context.Stopwatch.Stop() }
    }
    $skipped = Invoke-TunaGuiDataPathDiagnosticCompanion -ArtifactDir $ArtifactRoot -Context $context
    if ($skipped.status -cne 'not_evaluated' -or $null -ne $skipped.candidateCount) {
        throw "Unsafe eligibility/count result: $change"
    }
    Assert-PrimaryUnchanged
}

$previousDeadlinePath = $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH
try {
    $deadlinePath = Join-Path $ArtifactRoot 'diagnostic-deadline.json'
    $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH = $deadlinePath
    foreach ($deadline in @([DateTimeOffset]::UtcNow.AddSeconds(5).ToString('O'), 'invalid')) {
        Write-GuiSmokeAtomicJson -Path $deadlinePath -Value @{deadlineUtc=$deadline}
        $bounded = Invoke-TunaGuiDataPathDiagnosticCompanion -ArtifactDir $ArtifactRoot -Context (New-TestContext)
        if ($bounded.status -cne 'not_evaluated' -or $bounded.reason -cne 'diagnostic_budget_exhausted') {
            throw 'Companion renewed the budget or consumed finalization reserve'
        }
        Assert-PrimaryUnchanged
    }
}
finally { $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH = $previousDeadlinePath }

[IO.File]::AppendAllText($FixturePath, "`nevent=unrelated_mutation")
$changed = Invoke-TunaGuiDataPathDiagnosticCompanion -ArtifactDir $ArtifactRoot -Context (New-TestContext)
if ($changed.status -cne 'failed' -or $changed.reason -cne 'diagnostic_input_changed') {
    throw "Changed input was accepted: $($changed | ConvertTo-Json -Compress)"
}
Assert-PrimaryUnchanged
[IO.File]::WriteAllLines($FixturePath, $lines)

# Generated second activation with an exact Tuna resend but no Tuna receive.
# Independently parse full and regenerated compact inputs; preserve original
# evidence sequence while recording each input file's own physical location.
. (Join-Path $RepoRoot 'tools/FileTransferOps/GuiRejectedIngressShadow.ps1')
function Set-FixtureField {
    param([string]$Line, [string]$Name, [string]$Value)
    $pattern = '(?<=; )' + [regex]::Escape($Name) + '=[^;]*'
    if ([regex]::IsMatch($Line, $pattern)) { return [regex]::Replace($Line, $pattern, "$Name=$Value") }
    return "$Line; $Name=$Value"
}
$receive = @($lines | Where-Object { $_ -match 'event=filetransfer_data_path_receive_result;' })[0]
$source = @($lines | Where-Object { $_ -match 'event=filetransfer_data_path_send_result;' })[0]
$tunaSend = $source.Replace('route=regular_nkn_v4_fast','route=file_tuna_v4').Replace('transport=regular_nkn','transport=tuna')
$tunaSend = Set-FixtureField $tunaSend 'log_writer_sequence' '3'
foreach ($field in @('live_route_epoch','runtime_generation','terminal_generation','leg_generation',
    'route_publication_generation','route_commit_generation')) {
    $value = [regex]::Match($receive, '(?:^|;\s*)' + $field + '=([^;]*)').Groups[1].Value
    $tunaSend = Set-FixtureField $tunaSend $field $value
}
$second = [Collections.Generic.List[string]]::new()
foreach ($line in $lines) {
    $second.Add('event=unrelated_telemetry; text=padding')
    if ($line -ceq $receive) { $second.Add($tunaSend) }
    $second.Add($line.Replace('previous_route=regular_nkn_v4_fast','previous_route=post_tuna_fallback_v6'))
}
[string[]]$full = @(for ($i=0; $i -lt $second.Count; $i++) { "$($second[$i]); gui_evidence_sequence=$($i+1)" })
$reference = Get-TunaGuiRejectedIngressShadowDiagnostics -Lines $full
if ($reference.SharedDiagnostics[0].Recovered -or
    $reference.SharedDiagnostics[0].Variant -notmatch 'tuna_receive') { throw 'Missing-receive fixture is not unresolved' }
$eventNames = Get-TunaGuiRejectedIngressShadowEventNames
[string[]]$compact = @($full | Where-Object { [regex]::Match($_,'event=([a-z0-9_]+)').Groups[1].Value -in $eventNames })
$diagnosticJson = $null
foreach ($inputKind in @('full','compact','split')) {
    $path = Join-Path $ArtifactRoot ($inputKind + '.log')
    [string[]]$inputLines = if ($inputKind -eq 'compact') { $compact } else { $full }
    [IO.File]::WriteAllLines($path, $inputLines)
    # This fixture deliberately includes a BOM-prefixed record. If compaction
    # moves it to byte zero, ReadAllLines consumes that BOM, just as the primary
    # classifier does. Bind the persisted decoded lines, not the pre-write array.
    $inputLines = [IO.File]::ReadAllLines($path)
    $context = New-TestContext
    $context.Eligibility = $eligibility | ConvertTo-Json -Depth 5 | ConvertFrom-Json
    $context.Eligibility.evidenceInputs = @([pscustomobject][ordered]@{path=$path;lineCount=$inputLines.Count;
        contentSha256=(Get-TunaGuiDiagnosticContentHash $inputLines)})
    $context.EvidencePaths = @($path)
    if ((Get-TunaGuiDiagnosticContentHash ([IO.File]::ReadAllLines($path))) -cne
        $context.Eligibility.evidenceInputs[0].contentSha256) { throw "Fixture round-trip hash differs: $inputKind" }
    if ($inputKind -eq 'split') {
        $context.Eligibility.evidenceInputs = @()
        $middle = [int][Math]::Floor($inputLines.Count / 2)
        foreach ($part in @(0,1)) {
            $partPath = Join-Path $ArtifactRoot ("split-$part.log")
            [string[]]$partLines = if ($part -eq 0) { $inputLines[0..($middle-1)] } else { $inputLines[$middle..($inputLines.Count-1)] }
            [IO.File]::WriteAllLines($partPath, $partLines)
            $partLines = [IO.File]::ReadAllLines($partPath)
            $context.Eligibility.evidenceInputs += [pscustomobject][ordered]@{path=$partPath;lineCount=$partLines.Count;
                contentSha256=(Get-TunaGuiDiagnosticContentHash -Lines $partLines)}
        }
        $context.EvidencePaths = @($context.Eligibility.evidenceInputs.path)
    }
    $expectedLocations = @(foreach ($sourceInput in $context.Eligibility.evidenceInputs) {
        $sourceLines = [IO.File]::ReadAllLines($sourceInput.path)
        for ($sourceLine=0; $sourceLine -lt $sourceLines.Count; $sourceLine++) {
            if ($sourceLines[$sourceLine] -match 'event=filetransfer_data_path_receive_result;') {
                [pscustomobject]@{Path=$sourceInput.path;Line=($sourceLine+1)}
            }
        }
    })
    if ($expectedLocations.Count -ne 1) { throw 'Fixture receive location is ambiguous' }
    $unresolved = Invoke-TunaGuiDataPathDiagnosticCompanion -ArtifactDir $ArtifactRoot -Context $context
    if ($unresolved.status -cne 'completed' -or $unresolved.candidateCount -ne 1 -or
        $unresolved.unresolvedCandidateCount -ne 1 -or $unresolved.sharedMatcherRecoveredCount -ne 0 -or
        $unresolved.records[0].evidenceSequence -ne $reference.SharedDiagnostics[0].LineNumber -or
        $unresolved.records[0].variant -cne $reference.SharedDiagnostics[0].Variant -or
        $unresolved.records[0].sourcePath -cne $expectedLocations[0].Path -or
        $unresolved.records[0].sourceLineNumber -ne $expectedLocations[0].Line) {
        throw "Missing-receive companion drift: $inputKind $($unresolved | ConvertTo-Json -Depth 8 -Compress)"
    }
    $json = ConvertTo-Json -InputObject @($unresolved.records | Select-Object evidenceSequence,sessionId,
        transferId,writerId,writerSequence,sharedMatcherRecovered,rejectionChainMatched,receiveDisposition,reason,variant) -Compress
    if ($null -ne $diagnosticJson -and $diagnosticJson -cne $json) { throw 'Full/compact diagnostic JSON drift' }
    $diagnosticJson = $json
    Assert-PrimaryUnchanged
}
Write-Output ('companion-parity-hash=' + (Get-GuiSmokeLogLedgerHash $diagnosticJson))

# A real subprocess exercises timeout/nonzero/malformed/late-output handling.
# Only the test's launcher substitutes its worker; production has no test mode.
$worker = Join-Path $ArtifactRoot 'controlled-worker.ps1'
$realWorker = Join-Path $RepoRoot 'tools/FileTransferOps/GuiDataPathDiagnosticCompanion.ps1'
$workerSource = @'
param($RequestPath, $Mode, $RealWorker)
$ErrorActionPreference='Stop'
$request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
if ($Mode -eq 'exit') { exit 9 }
if ($Mode -eq 'missing') { exit 0 }
if ($Mode -eq 'malformed') { [IO.File]::WriteAllText($request.responsePath, '{'); exit 0 }
& $RealWorker -RequestPath $RequestPath
if ($Mode -eq 'late') { Start-Sleep -Seconds 10; exit 0 }
$response = Get-Content -LiteralPath $request.responsePath -Raw | ConvertFrom-Json
if ($Mode -eq 'identity') { $response.requestId='wrong-request' }
if ($Mode -eq 'counts') { $response.nonAuthorityDataPathDiagnostics.candidateCount=999 }
[IO.File]::WriteAllText($request.responsePath, ($response | ConvertTo-Json -Depth 12))
'@
[IO.File]::WriteAllText($worker, $workerSource)
function Start-Process {
    param($FilePath, $ArgumentList, $WindowStyle, $WorkingDirectory,
        [switch]$PassThru, $RedirectStandardOutput, $RedirectStandardError)
    if (-not (Test-Path -LiteralPath $primaryPath)) { throw 'Primary not saved before child launch' }
    if ($WindowStyle -cne 'Hidden') { throw 'Visible diagnostic child' }
    $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$worker,
        '-RequestPath',(Join-Path $ArtifactRoot 'filetransfer-tuna-gui-data-path-diagnostic-request.json'),
        '-Mode',$script:childMode,'-RealWorker',$realWorker)
    $line = ($arguments | ForEach-Object { ConvertTo-GuiSmokeProcessArgument ([string]$_) }) -join ' '
    $child = Microsoft.PowerShell.Management\Start-Process -FilePath $FilePath -ArgumentList $line `
        -WindowStyle Hidden -WorkingDirectory $WorkingDirectory -PassThru `
        -RedirectStandardOutput $RedirectStandardOutput -RedirectStandardError $RedirectStandardError
    $script:lastChildId = $child.Id
    return $child
}
foreach ($mode in @('exit','missing','malformed','identity','counts','late')) {
    $script:childMode = $mode
    $context = New-TestContext
    if ($mode -eq 'late') { $context.BudgetMilliseconds=2500L }
    $receipt = Invoke-TunaGuiDataPathDiagnosticCompanion -ArtifactDir $ArtifactRoot -Context $context
    $expected = if ($mode -eq 'late') { 'timed_out' } else { 'failed' }
    if ($receipt.status -cne $expected -or $null -ne $receipt.candidateCount) {
        throw "Child isolation failed: $mode $($receipt | ConvertTo-Json -Compress)"
    }
    if ($null -ne (Get-Process -Id $script:lastChildId -ErrorAction SilentlyContinue)) {
        throw "Diagnostic child survived: $mode"
    }
    Assert-PrimaryUnchanged
}
Remove-Item Function:Start-Process

# Additive persistence preserves every primary field and never mutates its object.
$before = $primary | ConvertTo-Json -Compress
if (-not (Write-TunaGuiDataPathDiagnosticSummary -Path $primaryPath -PrimarySummary $primary -Diagnostics $result)) {
    throw 'Companion summary write failed'
}
$summary = Get-Content -LiteralPath $primaryPath -Raw | ConvertFrom-Json
if ($summary.nonAuthorityDataPathDiagnostics.candidateCount -ne 1) { throw 'Companion dropped by projection' }
$summary.PSObject.Properties.Remove('nonAuthorityDataPathDiagnostics')
if (($summary | ConvertTo-Json -Compress) -cne $before -or ($primary | ConvertTo-Json -Compress) -cne $before) {
    throw 'Primary fields changed during additive serialization'
}
Write-GuiSmokeAtomicJson -Path $primaryPath -Value $primary
$writer = (Get-Command Write-GuiSmokeAtomicJson).ScriptBlock
function Write-GuiSmokeAtomicJson { throw 'injected optional write failure' }
if (Write-TunaGuiDataPathDiagnosticSummary -Path $primaryPath -PrimarySummary $primary -Diagnostics $result) {
    throw 'Optional write failure was not isolated'
}
Assert-PrimaryUnchanged
Set-Item Function:Write-GuiSmokeAtomicJson $writer

# Execute the actual classifier and request projection with controlled evaluator
# outcomes. Neither stale source labels nor incomplete/full-compact divergence
# can opt the companion in, and primary authority returns remain authoritative.
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification',
    'Get-TunaGuiFileTransferSetupFailureClassification','Invoke-TunaGuiFailureClassificationRequest')) {
    $definition = $guiAst.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    Invoke-Expression $definition.Extent.Text
}
$script:evaluationCase = 'pass'
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param($Lines, $SnapshotRecords)
    $evaluation = [ordered]@{ Mode='SnapshotV1Authoritative'; ContractVersion=1; CatalogVersion='fixture'; CatalogHash='fixture';
        StreamComplete=$true; ArtifactEvidenceVerdict='pass'; TransitionCount=1L; EffectCount=0L; BoundaryCount=0L;
        BoundaryHash='same'; FirstUnresolvedBoundary=$null; LegacySelectionCount=0L; RouteFamilyLegacySelectionCount=0L;
        RuntimeUnlockRetryFamilyLegacySelectionCount=0L; ReceiveRecoveryFamilyLegacySelectionCount=0L;
        FallbackTailYieldFamilyLegacySelectionCount=0L; RegularV4FrontierRepairPressureFamilyLegacySelectionCount=0L;
        ResponderProbeHandoffFamilyLegacySelectionCount=0L }
    switch ($script:evaluationCase) {
        'invalid' { $evaluation.ArtifactEvidenceVerdict='fail' }
        'incomplete' { $evaluation.StreamComplete=$false }
        'parity' { if ($null -ne $Lines) { $evaluation.BoundaryHash='full-only' } }
        'boundary' {
            $evaluation.FirstUnresolvedBoundary=[pscustomobject]@{ families=@('route_runtime'); phase='route_runtime';
                decisionReason='exact-authority'; taxonomyClass='exact-authority-class'; variant='exact';
                boundaryId='boundary'; boundaryHash='hash'; rawCount=1;distinctCount=1;recoveredCount=0;unresolvedCount=1;
                writer='writer';writerSequence=42 }
        }
    }
    return [pscustomobject]$evaluation
}
function Test-FileTransferCoordinatorSnapshotFamilyAuthoritative { return $true }
function Get-FileTransferCoordinatorSnapshotRouteConsistency {
    return [pscustomobject]@{Verdict='pass';NormalizedJson='{}';NormalizedHash='same';BoundaryHash='same'}
}
$requestFile = Join-Path $ArtifactRoot 'primary-request.json'
$responseFile = Join-Path $ArtifactRoot 'primary-response.json'
Write-GuiSmokeAtomicJson -Path $requestFile -Value ([ordered]@{
    errorMessage='unclassified fixture failure';routeMode='live-regular-activation-cycle';
    evidencePaths=@($FixturePath);responsePath=$responseFile })
foreach ($case in @('pass','invalid','incomplete','parity','boundary')) {
    $script:evaluationCase=$case
    Invoke-TunaGuiFailureClassificationRequest -RequestPath $requestFile
    $response = Get-Content -LiteralPath $responseFile -Raw | ConvertFrom-Json
    if (-not $response.classification.ClassificationComplete) { throw 'Primary classification incomplete' }
    if ($response.diagnosticEligibility.eligible -ne ($case -eq 'pass')) { throw "Eligibility leak: $case" }
    if ($case -eq 'boundary' -and $response.classification.FailureClass -cne 'exact-authority-class') {
        throw 'Companion displaced an authority boundary'
    }
    if ($case -eq 'invalid' -and $response.classification.FailureClass -cne 'filetransfer_coordinator_snapshot_evidence_invalid') {
        throw 'Companion displaced invalid snapshot evidence'
    }
    if ($case -eq 'pass' -and $response.diagnosticEligibility.evidenceInputs[0].contentSha256 -cne $inputs[0].contentSha256) {
        throw 'Carrier does not describe primary input'
    }
    if ($case -eq 'pass') { $script:eligiblePrimaryCarrier = $response.diagnosticEligibility }
}

# Execute the production finalization branch (not GUI interaction): primary
# adoption/write -> completed stage -> isolated child -> additive write -> original throw.
$scenario = $guiAst.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Run-ScenarioFileTransferTunaHandoffFallback'
}, $true)
$summaryAssignment = $scenario.Find({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -ceq '$newFailureSummary'
}, $true)
Invoke-Expression $summaryAssignment.Extent.Text
$finalBranch = $scenario.Find({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text.StartsWith('if ($null -ne $scenarioFailure) {', [StringComparison]::Ordinal)
}, $true)
foreach ($name in @('New-TunaGuiFailureClassificationEnrichmentReceipt')) {
    $definition = $guiAst.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    Invoke-Expression $definition.Extent.Text
}
$script:adoptedClassification = New-TunaGuiFileTransferFailureClassificationResult `
    -Phase 'evidence_harness' -Reason 'adopted-exact-primary' -RouteMode 'live-regular-activation-cycle' `
    -ClassificationSource 'immutable_evidence'
$adoptedJson = $script:adoptedClassification | ConvertTo-Json -Depth 10 -Compress
function Invoke-TunaGuiFailureClassificationEnrichment {
    param($ArtifactDir, $ErrorMessage, $RouteMode, $BaseClassification, $DiagnosticContext)
    $DiagnosticContext['Eligibility'] = $script:eligiblePrimaryCarrier
    $DiagnosticContext['EvidencePaths'] = @($script:eligiblePrimaryCarrier.evidenceInputs.path)
    $DiagnosticContext['Stopwatch'] = [Diagnostics.Stopwatch]::StartNew()
    $DiagnosticContext['BudgetMilliseconds'] = 20000L
    return New-TunaGuiFailureClassificationEnrichmentReceipt -Classification $script:adoptedClassification `
        -Status 'completed' -Reason '(none)' -ElapsedMilliseconds 7L
}
function Update-FileTransferGuiEvidenceFinalizationStage {
    param($Stage)
    if ($Stage -ceq 'failure_classification_completed') {
        $persisted = Get-Content -LiteralPath $primaryPath -Raw | ConvertFrom-Json
        if ($persisted.failureReason -cne 'adopted-exact-primary' -or
            $persisted.classificationStatus -cne 'completed' -or
            $null -ne $persisted.PSObject.Properties['nonAuthorityDataPathDiagnostics']) {
            throw 'Primary was not independently persisted before companion'
        }
        $script:primaryCompletedBeforeCompanion = $true
    }
}
$artifactDir=$ArtifactRoot; $direction='outbound'; $payerMode='payer'; $faultMode='off-on-off'
$routeMode='live-regular-activation-cycle'; $payloadSize=268435456L
$requiresFailureClassificationEnrichment=$true; $failureClassification=$null
$classificationEnrichmentReceipt=$null; $script:primaryCompletedBeforeCompanion=$false
try { throw 'original-scenario-failure' } catch { $scenarioFailure=$_ }
$originalThrown=$false
try { & ([scriptblock]::Create($finalBranch.Extent.Text)) }
catch { if ($_.Exception.Message -cne 'original-scenario-failure') { throw }; $originalThrown=$true }
$finalSummary = Get-Content -LiteralPath $primaryPath -Raw | ConvertFrom-Json
if (-not $originalThrown -or -not $script:primaryCompletedBeforeCompanion -or
    $finalSummary.failureReason -cne 'adopted-exact-primary' -or
    $finalSummary.classificationEnrichmentStatus -cne 'completed' -or
    $finalSummary.classificationEnrichmentElapsedMilliseconds -ne 7 -or
    $finalSummary.nonAuthorityDataPathDiagnostics.status -cne 'completed' -or
    ($script:adoptedClassification | ConvertTo-Json -Depth 10 -Compress) -cne $adoptedJson) {
    throw "Finalization branch changed primary: $($finalSummary | ConvertTo-Json -Depth 10 -Compress)"
}
'gui-diagnostic-companion-ok'
