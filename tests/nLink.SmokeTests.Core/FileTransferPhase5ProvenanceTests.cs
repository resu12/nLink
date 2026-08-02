namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Trait("AcceptancePhase5Suite", "Provenance")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPhase5ProvenanceTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task EvidenceManifest_ValidLiveAndDryRunEvidenceHaveDistinctProofVerdictsAndAreWriteOnce()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunEvidenceScriptAsync(
            FixturePrelude +
            """
$fixture = New-EvidenceFixture -Name 'valid' -ScenarioName 'regular-v4-live-activation-off-on-off-256mb'
$attempt = Get-RouteAcceptanceScenarioEvidence `
    -AcceptanceRunId $fixture.RunId `
    -ScenarioName 'regular-v4-live-activation-off-on-off-256mb' `
    -Attempt 1 `
    -ScenarioRunId ($fixture.RunId + ':regular-v4-live-activation-off-on-off-256mb:1') `
    -ArtifactDir $fixture.ArtifactDir `
    -ArtifactFinalized $true `
    -LogLedgerPath $fixture.LedgerPath `
    -AnalyzerDiagnosticsPath $fixture.DiagnosticsPath `
    -AnalyzerInputPath $fixture.AnalyzerInputPath

$final = New-RouteAcceptanceEvidenceSnapshot `
    -RepoRoot $fixture.Root `
    -PortableExecutablePath $fixture.ExecutablePath `
    -HarnessFiles $fixture.HarnessFiles `
    -BridgeDir $fixture.BridgeDir `
    -GitState $fixture.GitState

$live = New-RouteAcceptanceEvidenceManifest `
    -RunId $fixture.RunId `
    -MatrixMode 'phase5-analyzer-gui-acceptance' `
    -ExecutionMode live `
    -ArtifactRoot $fixture.ArtifactDir `
    -StartedUtc ([datetime]'2026-07-12T10:00:00Z') `
    -FinalizedUtc ([datetime]'2026-07-12T10:05:00Z') `
    -ScenarioNames @('regular-v4-live-activation-off-on-off-256mb') `
    -InitialSnapshot $fixture.Initial `
    -FinalSnapshot $final `
    -ScenarioAttempts @($attempt)

$liveValidation = Test-RouteAcceptanceEvidenceManifest -Manifest $live
Assert-Equal $liveValidation.verdict 'PASS' 'valid live manifest verdict'
Assert-True $liveValidation.currentRuntimeProof 'valid live manifest must prove current runtime'
Assert-Equal $liveValidation.provenanceVerdict 'PASS' 'valid live provenance'
Assert-Equal $liveValidation.cursorIntegrityVerdict 'PASS' 'valid live cursor'

$partialAttempt = Get-RouteAcceptanceScenarioEvidence `
    -AcceptanceRunId $fixture.RunId `
    -ScenarioName 'regular-v4-live-activation-off-on-off-256mb' `
    -Attempt 1 `
    -ScenarioRunId ($fixture.RunId + ':regular-v4-live-activation-off-on-off-256mb:1') `
    -ArtifactDir $fixture.ArtifactDir `
    -ArtifactFinalized $false `
    -LogLedgerPath $fixture.LedgerPath `
    -AnalyzerDiagnosticsPath $fixture.DiagnosticsPath `
    -AnalyzerInputPath $fixture.AnalyzerInputPath
$partial = New-RouteAcceptanceEvidenceManifest `
    -RunId $fixture.RunId `
    -MatrixMode 'phase5-analyzer-gui-acceptance' `
    -ExecutionMode live `
    -ArtifactRoot $fixture.ArtifactDir `
    -StartedUtc ([datetime]'2026-07-12T10:00:00Z') `
    -FinalizedUtc ([datetime]'2026-07-12T10:05:00Z') `
    -ScenarioNames @('regular-v4-live-activation-off-on-off-256mb') `
    -InitialSnapshot $fixture.Initial `
    -FinalSnapshot $final `
    -ScenarioAttempts @($partialAttempt)
$partialValidation = Test-RouteAcceptanceEvidenceManifest -Manifest $partial
Assert-Equal $partialValidation.verdict 'FAIL' 'partially finalized live manifest verdict'
Assert-False $partialValidation.currentRuntimeProof 'partially finalized live evidence must fail closed'
Assert-Contains $partialValidation.reasons 'scenario_artifact_not_finalized' 'partial-finalization proof reason'
Assert-Equal @($partialValidation.reasons).Count 1 'partial finalization must not expand absent cursor sentinels into derivative failures'
Assert-Equal @($partialValidation.cursorIntegrityReasons).Count 1 'partial cursor reasons remain canonical'

$timeoutMarkerPath = Join-Path $fixture.ArtifactDir 'filetransfer-gui-evidence-finalization-timeout.json'
[ordered]@{
    schemaVersion = 1
    event = 'filetransfer_gui_evidence_finalization_timeout'
    permanentlyNonAuthoritative = $true
    acceptanceRunId = $fixture.RunId
    scenarioRunId = ($fixture.RunId + ':regular-v4-live-activation-off-on-off-256mb:1')
    artifactDir = $fixture.ArtifactDir
    exitCode = 124
    reason = 'gui_evidence_finalization_hard_deadline_exceeded'
} | ConvertTo-Json | Set-Content -LiteralPath $timeoutMarkerPath -Encoding UTF8
$timeoutAttempt = Get-RouteAcceptanceScenarioEvidence `
    -AcceptanceRunId $fixture.RunId `
    -ScenarioName 'regular-v4-live-activation-off-on-off-256mb' `
    -Attempt 1 `
    -ScenarioRunId ($fixture.RunId + ':regular-v4-live-activation-off-on-off-256mb:1') `
    -ArtifactDir $fixture.ArtifactDir `
    -ArtifactFinalized $false `
    -LogLedgerPath $fixture.LedgerPath `
    -AnalyzerDiagnosticsPath $fixture.DiagnosticsPath `
    -AnalyzerInputPath $fixture.AnalyzerInputPath
Assert-True $timeoutAttempt.finalizationTimeout.exists 'timeout attempt binds the permanent marker'
Assert-True (Test-RouteAcceptanceEvidenceSha256Value -Value $timeoutAttempt.finalizationTimeout.sha256) 'timeout attempt binds the marker hash'
$timeoutManifest = New-RouteAcceptanceEvidenceManifest `
    -RunId $fixture.RunId `
    -MatrixMode 'phase5-analyzer-gui-acceptance' `
    -ExecutionMode live `
    -ArtifactRoot $fixture.ArtifactDir `
    -StartedUtc ([datetime]'2026-07-12T10:00:00Z') `
    -FinalizedUtc ([datetime]'2026-07-12T10:05:00Z') `
    -ScenarioNames @('regular-v4-live-activation-off-on-off-256mb') `
    -InitialSnapshot $fixture.Initial `
    -FinalSnapshot $final `
    -ScenarioAttempts @($timeoutAttempt)
$timeoutValidation = Test-RouteAcceptanceEvidenceManifest -Manifest $timeoutManifest
Assert-Equal @($timeoutValidation.reasons).Count 2 'timeout finalization reports only canonical reasons'
Assert-Contains $timeoutValidation.reasons 'scenario_artifact_not_finalized' 'timeout remains unfinalized'
Assert-Contains $timeoutValidation.reasons 'scenario_evidence_finalization_timeout' 'timeout marker reason'

$malformedArtifactPathAttempt = $timeoutAttempt | ConvertTo-Json -Depth 100 | ConvertFrom-Json
$malformedArtifactPathAttempt.artifactDir = "invalid`0artifact"
$malformedArtifactPathValidation = Get-RouteAcceptanceEvidenceFinalizationTimeoutValidation `
    -ScenarioAttempt $malformedArtifactPathAttempt `
    -AcceptanceRunId $fixture.RunId `
    -ScenarioRunId ($fixture.RunId + ':regular-v4-live-activation-off-on-off-256mb:1')
Assert-True $malformedArtifactPathValidation.Present 'malformed artifact path must fail closed as present invalid evidence'
Assert-False $malformedArtifactPathValidation.Valid 'malformed artifact path must not throw or validate'

$malformedSnapshotPathAttempt = $timeoutAttempt | ConvertTo-Json -Depth 100 | ConvertFrom-Json
$malformedSnapshotPathAttempt.finalizationTimeout.path = "invalid`0marker"
$malformedSnapshotPathValidation = Get-RouteAcceptanceEvidenceFinalizationTimeoutValidation `
    -ScenarioAttempt $malformedSnapshotPathAttempt `
    -AcceptanceRunId $fixture.RunId `
    -ScenarioRunId ($fixture.RunId + ':regular-v4-live-activation-off-on-off-256mb:1')
Assert-True $malformedSnapshotPathValidation.Present 'malformed snapshot path remains bound evidence'
Assert-False $malformedSnapshotPathValidation.Valid 'malformed snapshot path must not throw or validate'

$timeoutMarker = Get-Content -LiteralPath $timeoutMarkerPath -Raw | ConvertFrom-Json
$timeoutMarker.reason = 'mutated'
$timeoutMarker | ConvertTo-Json | Set-Content -LiteralPath $timeoutMarkerPath -Encoding UTF8
$mutatedTimeoutValidation = Get-RouteAcceptanceEvidenceManifestValidation -Manifest $timeoutManifest
Assert-Contains $mutatedTimeoutValidation.reasons 'scenario_evidence_finalization_timeout_invalid' 'mutated timeout marker fails closed'
Remove-Item -LiteralPath $timeoutMarkerPath -Force

$dryRun = New-RouteAcceptanceEvidenceManifest `
    -RunId $fixture.RunId `
    -MatrixMode 'phase5-analyzer-gui-acceptance' `
    -ExecutionMode evidence_dry_run `
    -ArtifactRoot $fixture.ArtifactDir `
    -StartedUtc ([datetime]'2026-07-12T10:00:00Z') `
    -FinalizedUtc ([datetime]'2026-07-12T10:05:00Z') `
    -ScenarioNames @('regular-v4-live-activation-off-on-off-256mb') `
    -InitialSnapshot $fixture.Initial `
    -FinalSnapshot $final `
    -ScenarioAttempts @($attempt)

$dryValidation = Test-RouteAcceptanceEvidenceManifest -Manifest $dryRun
Assert-Equal $dryValidation.verdict 'PASS' 'valid dry-run evidence verdict'
Assert-False $dryValidation.currentRuntimeProof 'dry run must not prove live runtime'
Assert-Contains $dryValidation.reasons 'execution_mode_not_live' 'dry-run proof reason'

$manifestPath = Join-Path $fixture.ArtifactDir 'route-acceptance-run-manifest.json'
$write = Write-RouteAcceptanceEvidenceManifest -Path $manifestPath -Manifest $dryRun
Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'manifest was not written'
Assert-Equal $write.sha256 (Get-RouteAcceptanceEvidenceSha256 -Path $manifestPath) 'manifest SHA-256'
Assert-Equal $write.validation.verdict 'PASS' 'reopened manifest verdict'
Assert-False $write.validation.currentRuntimeProof 'reopened dry-run must remain non-live'

$secondWriteFailed = $false
try {
    Write-RouteAcceptanceEvidenceManifest -Path $manifestPath -Manifest $dryRun | Out-Null
}
catch [System.IO.IOException] {
    $secondWriteFailed = $true
}
Assert-True $secondWriteFailed 'FileMode.CreateNew must reject a second manifest write'

$setFirst = Set-RouteAcceptanceEvidenceManifest -Path $manifestPath -Manifest $dryRun
$setSecond = Set-RouteAcceptanceEvidenceManifest -Path $manifestPath -Manifest $dryRun
Assert-Equal $setFirst.validation.verdict 'PASS' 'atomic manifest checkpoint validation'
Assert-Equal $setSecond.validation.verdict 'PASS' 'idempotent final manifest validation'
Assert-Equal $setFirst.sha256 $setSecond.sha256 'identical manifest updates must preserve content SHA-256'
Assert-Equal @(
    Get-ChildItem -LiteralPath $fixture.ArtifactDir -File |
        Where-Object { $_.Name -like 'route-acceptance-run-manifest.json.*.tmp' -or $_.Name -like 'route-acceptance-run-manifest.json.*.bak' }
).Count 0 'atomic manifest update must not leave temporary files'

$missing = Test-RouteAcceptanceEvidenceManifestFile -Path (Join-Path $fixture.ArtifactDir 'missing.json')
Assert-Equal $missing.verdict 'FAIL' 'missing manifest verdict'
Assert-Contains $missing.reasons 'manifest_missing' 'missing manifest reason'

$malformedPath = Join-Path $fixture.ArtifactDir 'malformed.json'
[System.IO.File]::WriteAllText($malformedPath, '{', [System.Text.UTF8Encoding]::new($false))
$malformed = Test-RouteAcceptanceEvidenceManifestFile -Path $malformedPath
Assert-Equal $malformed.verdict 'FAIL' 'malformed manifest verdict'
Assert-Contains $malformed.reasons 'manifest_malformed' 'malformed manifest reason'
""");

        AssertScriptPassed(result);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task EvidenceManifest_FailsClosedForSourceRuntimeHarnessAndBridgeProvenanceChanges()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunEvidenceScriptAsync(
            FixturePrelude +
            """
function New-ManifestFromFixture {
    param(
        [Parameter(Mandatory = $true)]$Fixture,
        [Parameter(Mandatory = $true)]$FinalSnapshot
    )

    $attempt = Get-RouteAcceptanceScenarioEvidence `
        -AcceptanceRunId $Fixture.RunId `
        -ScenarioName 'scenario' `
        -Attempt 1 `
        -ScenarioRunId ($Fixture.RunId + ':scenario:1') `
        -ArtifactDir $Fixture.ArtifactDir `
        -ArtifactFinalized $true `
        -LogLedgerPath $Fixture.LedgerPath `
        -AnalyzerDiagnosticsPath $Fixture.DiagnosticsPath `
        -AnalyzerInputPath $Fixture.AnalyzerInputPath
    return New-RouteAcceptanceEvidenceManifest `
        -RunId $Fixture.RunId `
        -MatrixMode 'phase5-analyzer-gui-acceptance' `
        -ExecutionMode live `
        -ArtifactRoot $Fixture.ArtifactDir `
        -StartedUtc ([datetime]'2026-07-12T10:00:00Z') `
        -FinalizedUtc ([datetime]'2026-07-12T10:05:00Z') `
        -ScenarioNames @('scenario') `
        -InitialSnapshot $Fixture.Initial `
        -FinalSnapshot $FinalSnapshot `
        -ScenarioAttempts @($attempt)
}

$dirty = New-EvidenceFixture -Name 'dirty'
$dirtyGit = [pscustomobject]@{
    gitCommitSha = $dirty.GitState.gitCommitSha
    dirty = $true
    normalizedStatusSha256 = Get-RouteAcceptanceEvidenceTextSha256 -Value ' M source.cs'
}
$dirtyFinal = New-RouteAcceptanceEvidenceSnapshot -RepoRoot $dirty.Root -PortableExecutablePath $dirty.ExecutablePath -HarnessFiles $dirty.HarnessFiles -BridgeDir $dirty.BridgeDir -GitState $dirtyGit
$dirtyValidation = Test-RouteAcceptanceEvidenceManifest -Manifest (New-ManifestFromFixture -Fixture $dirty -FinalSnapshot $dirtyFinal)
Assert-Contains $dirtyValidation.reasons 'source_dirty' 'dirty source reason'
Assert-False $dirtyValidation.currentRuntimeProof 'dirty source must not prove current runtime'

$head = New-EvidenceFixture -Name 'head-change'
$changedGit = [pscustomobject]@{
    gitCommitSha = ('b' * 40)
    dirty = $false
    normalizedStatusSha256 = $head.GitState.normalizedStatusSha256
}
$headFinal = New-RouteAcceptanceEvidenceSnapshot -RepoRoot $head.Root -PortableExecutablePath $head.ExecutablePath -HarnessFiles $head.HarnessFiles -BridgeDir $head.BridgeDir -GitState $changedGit
$headValidation = Test-RouteAcceptanceEvidenceManifest -Manifest (New-ManifestFromFixture -Fixture $head -FinalSnapshot $headFinal)
Assert-Contains $headValidation.reasons 'source_head_changed' 'HEAD change reason'

$runtime = New-EvidenceFixture -Name 'runtime-change'
[System.IO.File]::AppendAllText($runtime.ExecutablePath, '-changed')
$runtimeFinal = New-RouteAcceptanceEvidenceSnapshot -RepoRoot $runtime.Root -PortableExecutablePath $runtime.ExecutablePath -HarnessFiles $runtime.HarnessFiles -BridgeDir $runtime.BridgeDir -GitState $runtime.GitState
$runtimeValidation = Test-RouteAcceptanceEvidenceManifest -Manifest (New-ManifestFromFixture -Fixture $runtime -FinalSnapshot $runtimeFinal)
Assert-Contains $runtimeValidation.reasons 'runtime_sha256_changed' 'runtime hash change reason'

$harness = New-EvidenceFixture -Name 'harness-change'
[System.IO.File]::AppendAllText($harness.HarnessFiles[0], '-changed')
$harnessFinal = New-RouteAcceptanceEvidenceSnapshot -RepoRoot $harness.Root -PortableExecutablePath $harness.ExecutablePath -HarnessFiles $harness.HarnessFiles -BridgeDir $harness.BridgeDir -GitState $harness.GitState
$harnessValidation = Test-RouteAcceptanceEvidenceManifest -Manifest (New-ManifestFromFixture -Fixture $harness -FinalSnapshot $harnessFinal)
Assert-Contains $harnessValidation.reasons 'harness_sha256_changed' 'harness hash change reason'

$bridge = New-EvidenceFixture -Name 'bridge-change'
[System.IO.File]::AppendAllText((Join-Path $bridge.BridgeDir 'index.js'), '-changed')
$newBridgeHash = Get-RouteAcceptanceEvidenceSha256 -Path (Join-Path $bridge.BridgeDir 'index.js')
Write-JsonNoBom -Path (Join-Path $bridge.BridgeDir 'bridge-manifest.json') -Value ([ordered]@{ bridgeScriptSha256 = $newBridgeHash })
$bridgeFinal = New-RouteAcceptanceEvidenceSnapshot -RepoRoot $bridge.Root -PortableExecutablePath $bridge.ExecutablePath -HarnessFiles $bridge.HarnessFiles -BridgeDir $bridge.BridgeDir -GitState $bridge.GitState
$bridgeValidation = Test-RouteAcceptanceEvidenceManifest -Manifest (New-ManifestFromFixture -Fixture $bridge -FinalSnapshot $bridgeFinal)
Assert-Contains $bridgeValidation.reasons 'bridge_sha256_changed' 'bridge hash change reason'

$declared = New-EvidenceFixture -Name 'bridge-declared-mismatch' -DeclaredBridgeMismatch
$declaredFinal = New-RouteAcceptanceEvidenceSnapshot -RepoRoot $declared.Root -PortableExecutablePath $declared.ExecutablePath -HarnessFiles $declared.HarnessFiles -BridgeDir $declared.BridgeDir -GitState $declared.GitState
$declaredValidation = Test-RouteAcceptanceEvidenceManifest -Manifest (New-ManifestFromFixture -Fixture $declared -FinalSnapshot $declaredFinal)
Assert-Contains $declaredValidation.reasons 'bridge_manifest_sha256_mismatch' 'bridge declared hash mismatch reason'
""");

        AssertScriptPassed(result);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task EvidenceManifest_RecomputesDerivedClaimsAndRejectsIdentityOrHarnessInconsistency()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunEvidenceScriptAsync(
            FixturePrelude +
            """
function Copy-EvidenceManifest {
    param([Parameter(Mandatory = $true)]$Manifest)
    return $Manifest | ConvertTo-Json -Depth 32 | ConvertFrom-Json
}

$fixture = New-EvidenceFixture -Name 'tampered'
$attempt = Get-RouteAcceptanceScenarioEvidence `
    -AcceptanceRunId $fixture.RunId `
    -ScenarioName 'scenario' `
    -Attempt 1 `
    -ScenarioRunId $fixture.ScenarioRunId `
    -ArtifactDir $fixture.ArtifactDir `
    -ArtifactFinalized $true `
    -LogLedgerPath $fixture.LedgerPath `
    -AnalyzerDiagnosticsPath $fixture.DiagnosticsPath `
    -AnalyzerInputPath $fixture.AnalyzerInputPath
$final = New-RouteAcceptanceEvidenceSnapshot -RepoRoot $fixture.Root -PortableExecutablePath $fixture.ExecutablePath -HarnessFiles $fixture.HarnessFiles -BridgeDir $fixture.BridgeDir -GitState $fixture.GitState
$manifest = New-RouteAcceptanceEvidenceManifest `
    -RunId $fixture.RunId `
    -MatrixMode 'phase5-analyzer-gui-acceptance' `
    -ExecutionMode live `
    -ArtifactRoot $fixture.ArtifactDir `
    -StartedUtc ([datetime]'2026-07-12T10:00:00Z') `
    -FinalizedUtc ([datetime]'2026-07-12T10:05:00Z') `
    -ScenarioNames @('scenario') `
    -InitialSnapshot $fixture.Initial `
    -FinalSnapshot $final `
    -ScenarioAttempts @($attempt)
Assert-Equal (Test-RouteAcceptanceEvidenceManifest -Manifest $manifest).verdict 'PASS' 'control manifest'

$head = Copy-EvidenceManifest -Manifest $manifest
$head.source.finalGitCommitSha = ('b' * 40)
$head.source.gitCommitSha = ('b' * 40)
$head.source.headStable = $true
$headResult = Test-RouteAcceptanceEvidenceManifest -Manifest $head
Assert-Contains $headResult.reasons 'source_head_consistency_invalid' 'derived HEAD claims must be recomputed'

$dirty = Copy-EvidenceManifest -Manifest $manifest
$dirty.source.dirtyAtStart = $true
$dirty.source.dirty = $false
$dirtyResult = Test-RouteAcceptanceEvidenceManifest -Manifest $dirty
Assert-Contains $dirtyResult.reasons 'source_dirty_consistency_invalid' 'derived dirty claim must be recomputed'
Assert-Contains $dirtyResult.reasons 'source_dirty' 'primitive dirty state must fail provenance'

$runtime = Copy-EvidenceManifest -Manifest $manifest
$runtime.runtime.finalSha256 = ('b' * 64)
$runtime.runtime.sha256 = ('b' * 64)
$runtime.runtime.hashStable = $true
$runtimeResult = Test-RouteAcceptanceEvidenceManifest -Manifest $runtime
Assert-Contains $runtimeResult.reasons 'runtime_provenance_inconsistent' 'runtime stable/hash claims must be recomputed'
Assert-Contains $runtimeResult.reasons 'runtime_sha256_changed' 'runtime pair mismatch'

$harness = Copy-EvidenceManifest -Manifest $manifest
$harness.harness.files[0].finalSha256 = ('b' * 64)
$harness.harness.files[0].hashStable = $true
$harnessResult = Test-RouteAcceptanceEvidenceManifest -Manifest $harness
Assert-Contains $harnessResult.reasons 'harness_file_provenance_inconsistent' 'harness file stable claims must be recomputed'
Assert-Contains $harnessResult.reasons 'harness_aggregate_sha256_mismatch' 'harness aggregate must be recomputed from all five files'

$missingHarness = Copy-EvidenceManifest -Manifest $manifest
$missingHarness.harness.files = @($missingHarness.harness.files | Select-Object -Skip 1)
$missingHarnessResult = Test-RouteAcceptanceEvidenceManifest -Manifest $missingHarness
Assert-Contains $missingHarnessResult.reasons 'harness_required_files_mismatch' 'exact five-file harness set is required'

$bridge = Copy-EvidenceManifest -Manifest $manifest
$bridge.bridge.declaredIndexSha256 = ('f' * 64)
$bridge.bridge.declaredHashMatches = $true
$bridgeResult = Test-RouteAcceptanceEvidenceManifest -Manifest $bridge
Assert-Contains $bridgeResult.reasons 'bridge_declared_hash_consistency_invalid' 'bridge declared flag must be recomputed'
Assert-Contains $bridgeResult.reasons 'bridge_manifest_sha256_mismatch' 'bridge declared hash mismatch'

$invalidRun = Copy-EvidenceManifest -Manifest $manifest
$invalidRun.runId = 'not-a-guid'
$invalidRunResult = Test-RouteAcceptanceEvidenceManifest -Manifest $invalidRun
Assert-Contains $invalidRunResult.reasons 'manifest_run_id_invalid' 'manifest run ID must be a GUID'

$scenarioIdentity = Copy-EvidenceManifest -Manifest $manifest
$scenarioIdentity.scenarioAttempts[0].scenarioRunId = $fixture.RunId + ':scenario:2'
$scenarioIdentity.scenarioAttempts[0].logLedger.scenarioRunId = $fixture.RunId + ':scenario:2'
$scenarioIdentityResult = Test-RouteAcceptanceEvidenceManifest -Manifest $scenarioIdentity
Assert-Contains $scenarioIdentityResult.reasons 'scenario_run_id_mismatch' 'scenario run ID must use run/name/attempt tuple'

$ledgerIdentity = Copy-EvidenceManifest -Manifest $manifest
$ledgerIdentity.scenarioAttempts[0].logLedger.acceptanceRunId = [guid]::NewGuid().ToString('N')
$ledgerIdentityResult = Test-RouteAcceptanceEvidenceManifest -Manifest $ledgerIdentity
Assert-Contains $ledgerIdentityResult.reasons 'cursor_acceptance_run_id_mismatch' 'ledger acceptance ID must match manifest'

$legacyWriterManifest = Copy-EvidenceManifest -Manifest $manifest
$legacyWriterManifest.scenarioAttempts[0].logLedger.writerEvidenceVersion = 2
$legacyWriterManifestResult = Test-RouteAcceptanceEvidenceManifest -Manifest $legacyWriterManifest
Assert-Contains $legacyWriterManifestResult.reasons 'cursor_writer_evidence_version_invalid' 'version 2 manifest evidence cannot prove current runtime'
Assert-False $legacyWriterManifestResult.currentRuntimeProof 'version 2 manifest evidence must fail closed'

$chronologyCountManifest = Copy-EvidenceManifest -Manifest $manifest
$chronologyCountManifest.scenarioAttempts[0].logLedger.writerChronologyInversionCount = 1
$chronologyCountManifestResult = Test-RouteAcceptanceEvidenceManifest -Manifest $chronologyCountManifest
Assert-Contains $chronologyCountManifestResult.reasons 'cursor_writer_chronology_count_mismatch' 'manifest chronology counts must match the bound ledger slice'

$reopenPath = Join-Path $fixture.ArtifactDir 'tampered-manifest.json'
$reopened = Write-RouteAcceptanceEvidenceManifest -Path $reopenPath -Manifest $runtime
Assert-Contains $reopened.validation.reasons 'runtime_provenance_inconsistent' 'reopened validation must recompute derived claims'
Assert-False $reopened.validation.currentRuntimeProof 'tampered manifest cannot prove runtime'

[System.IO.File]::AppendAllText($fixture.ExecutablePath, '-post-finalization')
$runtimeRace = Test-RouteAcceptanceEvidenceManifest -Manifest $manifest
Assert-Contains $runtimeRace.reasons 'runtime_current_sha256_mismatch' 'runtime bytes are rehashed during reopen validation'

[System.IO.File]::AppendAllText($fixture.HarnessFiles[0], '-post-finalization')
$harnessRace = Test-RouteAcceptanceEvidenceManifest -Manifest $manifest
Assert-Contains $harnessRace.reasons 'harness_current_sha256_mismatch' 'harness bytes are rehashed during reopen validation'

[System.IO.File]::AppendAllText((Join-Path $fixture.BridgeDir 'index.js'), '-post-finalization')
$bridgeRace = Test-RouteAcceptanceEvidenceManifest -Manifest $manifest
Assert-Contains $bridgeRace.reasons 'bridge_current_sha256_mismatch' 'bridge bytes are rehashed during reopen validation'

[System.IO.File]::AppendAllText($fixture.LedgerPath, "`n")
$ledgerRace = Test-RouteAcceptanceEvidenceManifest -Manifest $manifest
Assert-Contains $ledgerRace.reasons 'cursor_ledger_current_sha256_mismatch' 'ledger bytes are rehashed during reopen validation'

[System.IO.File]::AppendAllText($fixture.DiagnosticsPath, "`n")
$diagnosticsRace = Test-RouteAcceptanceEvidenceManifest -Manifest $manifest
Assert-Contains $diagnosticsRace.reasons 'analyzer_diagnostics_current_sha256_mismatch' 'diagnostic bytes are rehashed during reopen validation'

[System.IO.File]::AppendAllText($fixture.AnalyzerInputPath, 'event=late')
$analyzerRace = Test-RouteAcceptanceEvidenceManifest -Manifest $manifest
Assert-Contains $analyzerRace.reasons 'analyzer_input_current_sha256_mismatch' 'analyzer input bytes are rehashed during reopen validation'
""");

        AssertScriptPassed(result);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task EvidenceManifest_HarnessAggregateUsesOrdinalOrderingAndReadsLegacyWindowsPowerShellOrder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunEvidenceScriptAsync(
            FixturePrelude +
            """
$knownFiles = @(
    [pscustomobject]@{ relativePath = 'tools/Run-FileTransferTunaGuiSmoke.ps1'; exists = $true; sha256 = '8dcddf50a85a03e094bc272676d23f312bbc032184f7b057bede548b7e26cfad' },
    [pscustomobject]@{ relativePath = 'tools/GuiSmokeLogLedger.ps1'; exists = $true; sha256 = '91c76fa2bcbcf43883425a2b32479e530b7910dca32470fd1ad012d9f7170464' },
    [pscustomobject]@{ relativePath = 'tools/FileTransferRouteAcceptanceEvidence.ps1'; exists = $true; sha256 = '4ba431f9a4f14a646fb89e7eff79e43fb634cc5783078ec255b90a247d0fc06a' },
    [pscustomobject]@{ relativePath = 'tools/Run-FileTransferRouteAcceptance.ps1'; exists = $true; sha256 = '1feb70f23f648f085cae3e4b5a43819cedc21f41a2405961aa368e1962af2f3e' },
    [pscustomobject]@{ relativePath = 'tools/GuiSmoke-Windows.ps1'; exists = $true; sha256 = 'd82ce847044c94fcb8d307ffd0e28c97137a3bef4d52b8a4e662743c2f5ba876' }
)
$expectedOrdinalAggregate = 'c1328746d144cc0a5a52711c24187d6e18bdf3b6f76e1bf7e3c15d65f419c294'
$actualOrdinalAggregate = Get-RouteAcceptanceEvidenceHarnessAggregateSha256 -Files $knownFiles
Assert-Equal $actualOrdinalAggregate $expectedOrdinalAggregate 'aggregate must use ordinal path ordering'
Assert-Equal (Get-RouteAcceptanceEvidenceHarnessAggregateSha256 -Files @($knownFiles[4], $knownFiles[3], $knownFiles[2], $knownFiles[1], $knownFiles[0])) $expectedOrdinalAggregate 'aggregate must ignore input permutation'

$legacyKnownFiles = @($knownFiles[2], $knownFiles[1], $knownFiles[4], $knownFiles[3], $knownFiles[0])
$legacyKnownAggregate = Get-RouteAcceptanceEvidenceHarnessAggregateSha256 -Files $legacyKnownFiles -PreserveInputOrder
Assert-Equal $legacyKnownAggregate '3aebf60737b24505486ee99a0c57456cb9d19825054a42cd47fdb97eeb8cdea6' 'legacy Windows PowerShell order'
Assert-False ([string]::Equals($legacyKnownAggregate, $expectedOrdinalAggregate, [System.StringComparison]::Ordinal)) 'fixture must expose the host collation boundary'

$fixture = New-EvidenceFixture -Name 'cross-host-aggregate'
$attempt = Get-RouteAcceptanceScenarioEvidence `
    -AcceptanceRunId $fixture.RunId `
    -ScenarioName 'scenario' `
    -Attempt 1 `
    -ScenarioRunId $fixture.ScenarioRunId `
    -ArtifactDir $fixture.ArtifactDir `
    -ArtifactFinalized $true `
    -LogLedgerPath $fixture.LedgerPath `
    -AnalyzerDiagnosticsPath $fixture.DiagnosticsPath `
    -AnalyzerInputPath $fixture.AnalyzerInputPath
$final = New-RouteAcceptanceEvidenceSnapshot -RepoRoot $fixture.Root -PortableExecutablePath $fixture.ExecutablePath -HarnessFiles $fixture.HarnessFiles -BridgeDir $fixture.BridgeDir -GitState $fixture.GitState
$manifest = New-RouteAcceptanceEvidenceManifest `
    -RunId $fixture.RunId `
    -MatrixMode 'phase5-analyzer-gui-acceptance' `
    -ExecutionMode live `
    -ArtifactRoot $fixture.ArtifactDir `
    -StartedUtc ([datetime]'2026-07-12T10:00:00Z') `
    -FinalizedUtc ([datetime]'2026-07-12T10:05:00Z') `
    -ScenarioNames @('scenario') `
    -InitialSnapshot $fixture.Initial `
    -FinalSnapshot $final `
    -ScenarioAttempts @($attempt)
Assert-Equal (Test-RouteAcceptanceEvidenceManifest -Manifest $manifest).verdict 'PASS' 'ordinal control manifest'

$legacyManifest = $manifest | ConvertTo-Json -Depth 32 | ConvertFrom-Json
$legacyPathOrder = @(
    'tools/FileTransferRouteAcceptanceEvidence.ps1',
    'tools/GuiSmokeLogLedger.ps1',
    'tools/GuiSmoke-Windows.ps1',
    'tools/Run-FileTransferRouteAcceptance.ps1',
    'tools/Run-FileTransferTunaGuiSmoke.ps1'
)
$legacyFileList = New-Object System.Collections.Generic.List[object]
foreach ($relativePath in $legacyPathOrder) {
    $match = @($legacyManifest.harness.files | Where-Object { [string]$_.relativePath -eq $relativePath })
    Assert-Equal $match.Count 1 ('legacy harness path ' + $relativePath)
    $legacyFileList.Add($match[0]) | Out-Null
}
$legacyManifest.harness.files = @($legacyFileList.ToArray())
$legacyInitialAggregate = Get-RouteAcceptanceEvidenceMergedHarnessAggregateSha256 -Files @($legacyManifest.harness.files) -HashProperty initialSha256 -PreserveInputOrder
$legacyFinalAggregate = Get-RouteAcceptanceEvidenceMergedHarnessAggregateSha256 -Files @($legacyManifest.harness.files) -HashProperty finalSha256 -PreserveInputOrder
$legacyManifest.harness.initialAggregateSha256 = $legacyInitialAggregate
$legacyManifest.harness.finalAggregateSha256 = $legacyFinalAggregate
$legacyManifest.harness.aggregateSha256 = $legacyFinalAggregate
$legacyManifest.validation = Get-RouteAcceptanceEvidenceManifestValidation -Manifest $legacyManifest
$legacyValidation = Test-RouteAcceptanceEvidenceManifest -Manifest $legacyManifest
Assert-Equal $legacyValidation.verdict 'PASS' 'legacy producer order remains readable'

$arbitraryOrderManifest = $manifest | ConvertTo-Json -Depth 32 | ConvertFrom-Json
$arbitraryOrderManifest.harness.files = @($arbitraryOrderManifest.harness.files[4], $arbitraryOrderManifest.harness.files[3], $arbitraryOrderManifest.harness.files[2], $arbitraryOrderManifest.harness.files[1], $arbitraryOrderManifest.harness.files[0])
$arbitraryInitialAggregate = Get-RouteAcceptanceEvidenceMergedHarnessAggregateSha256 -Files @($arbitraryOrderManifest.harness.files) -HashProperty initialSha256 -PreserveInputOrder
$arbitraryFinalAggregate = Get-RouteAcceptanceEvidenceMergedHarnessAggregateSha256 -Files @($arbitraryOrderManifest.harness.files) -HashProperty finalSha256 -PreserveInputOrder
$arbitraryOrderManifest.harness.initialAggregateSha256 = $arbitraryInitialAggregate
$arbitraryOrderManifest.harness.finalAggregateSha256 = $arbitraryFinalAggregate
$arbitraryOrderManifest.harness.aggregateSha256 = $arbitraryFinalAggregate
$arbitraryOrderResult = Get-RouteAcceptanceEvidenceManifestValidation -Manifest $arbitraryOrderManifest
Assert-Contains $arbitraryOrderResult.reasons 'harness_aggregate_sha256_mismatch' 'legacy compatibility is limited to the known Windows PowerShell order'

$legacyManifestPath = Join-Path $fixture.ArtifactDir 'legacy-cross-host-manifest.json'
[System.IO.File]::WriteAllText($legacyManifestPath, ($legacyManifest | ConvertTo-Json -Depth 32), [System.Text.UTF8Encoding]::new($false))
$pwshPath = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
if (Test-Path -LiteralPath $pwshPath -PathType Leaf) {
    $knownFilesPath = Join-Path $fixture.ArtifactDir 'known-harness-files.json'
    [System.IO.File]::WriteAllText($knownFilesPath, ($knownFiles | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
    $crossHostScriptPath = Join-Path $fixture.ArtifactDir 'validate-cross-host.ps1'
    $crossHostScript = @'
param([string]$HelperPath, [string]$ManifestPath, [string]$FilesPath, [string]$ExpectedAggregate)
$ErrorActionPreference = 'Stop'
. $HelperPath
$files = @(Get-Content -LiteralPath $FilesPath -Raw | ConvertFrom-Json)
$aggregate = Get-RouteAcceptanceEvidenceHarnessAggregateSha256 -Files $files
if (-not [string]::Equals($aggregate, $ExpectedAggregate, [System.StringComparison]::Ordinal)) { exit 21 }
$validation = Test-RouteAcceptanceEvidenceManifestFile -Path $ManifestPath
if ($validation.verdict -ne 'PASS' -or $validation.provenanceVerdict -ne 'PASS') { exit 22 }
exit 0
'@
    [System.IO.File]::WriteAllText($crossHostScriptPath, $crossHostScript, [System.Text.UTF8Encoding]::new($false))
    & $pwshPath -NoProfile -ExecutionPolicy Bypass -File $crossHostScriptPath $HelperPath $legacyManifestPath $knownFilesPath $expectedOrdinalAggregate
    Assert-Equal $LASTEXITCODE 0 'PowerShell 7 must reproduce the ordinal aggregate and read the Windows PowerShell manifest'
}

$mutatedLegacyManifest = $legacyManifest | ConvertTo-Json -Depth 32 | ConvertFrom-Json
$mutatedLegacyManifest.harness.files[0].finalSha256 = ('b' * 64)
$mutatedLegacyResult = Get-RouteAcceptanceEvidenceManifestValidation -Manifest $mutatedLegacyManifest
Assert-Contains $mutatedLegacyResult.reasons 'harness_aggregate_sha256_mismatch' 'legacy compatibility cannot mask a mutated file hash'
""");

        AssertScriptPassed(result);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task CursorEvidence_RequiresMonotonicGenerationsNoGapsNoDuplicatesAndBalancedAnalyzerCounts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunEvidenceScriptAsync(
            FixturePrelude +
            """
$fixture = New-EvidenceFixture -Name 'cursor'
$valid = Get-RouteAcceptanceCursorEvidence -LogLedger $fixture.LedgerPath -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Equal $valid.verdict 'PASS' 'valid rotated cursor verdict'
Assert-Equal $valid.rotationCount 2 'rotation count'
Assert-Equal $valid.writerEvidenceVersion 3 'writer evidence version'
Assert-Equal $valid.writerIntegrityVerdict 'PASS' 'writer integrity verdict'

$appendOnlyLedger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$appendOnlyGenerationKey = 'append-only:test-generation'
$appendOnlyLedger | Add-Member -NotePropertyName storageMode -NotePropertyValue 'AppendOnlyFile'
$appendOnlyLedger | Add-Member -NotePropertyName sourcePath -NotePropertyValue $fixture.AnalyzerInputPath
$appendOnlyLedger.initial.lineCount = 0
$appendOnlyLedger.initial.activeGenerationKey = $appendOnlyGenerationKey
$appendOnlyLedger.initial.generations = @([pscustomobject]@{ generationKey = $appendOnlyGenerationKey; consumedByteOffset = 0; consumedLineCount = 0 })
$appendOnlyLedger.final.activeGenerationKey = $appendOnlyGenerationKey
$appendOnlyLedger.final.generations = @([pscustomobject]@{ generationKey = $appendOnlyGenerationKey; consumedByteOffset = 20; consumedLineCount = 2 })
$appendOnlyLedger.rotationCount = 0
$appendOnlyLedger.consumedIndexRanges = @([pscustomobject]@{ generationKey = $appendOnlyGenerationKey; firstLineOrdinal = 0; lastLineOrdinal = 1; count = 2 })
$appendOnlyLedger.consumedIndexSha256 = Get-RouteAcceptanceEvidenceTextSha256 -Value ("{0}|0|2" -f $appendOnlyGenerationKey)
$appendOnly = Get-RouteAcceptanceCursorEvidence -LogLedger $appendOnlyLedger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Equal $appendOnly.verdict 'PASS' 'append-only compact consumed range digest'

$appendOnlyLedger.consumedIndexSha256 = Get-RouteAcceptanceEvidenceTextSha256 -Value ((@("$appendOnlyGenerationKey`n0", "$appendOnlyGenerationKey`n1") | Sort-Object) -join [char]31)
$appendOnlyEnumeratedDigest = Get-RouteAcceptanceCursorEvidence -LogLedger $appendOnlyLedger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $appendOnlyEnumeratedDigest.reasons 'cursor_consumed_index_sha256_mismatch' 'append-only evidence must use the producer compact range digest'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.writerEvidenceVersion = 2
$legacyWriterEvidence = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $legacyWriterEvidence.reasons 'cursor_writer_evidence_version_invalid' 'version 2 writer evidence cannot prove current runtime'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.writerGapCount = 1
$writerGap = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $writerGap.reasons 'cursor_writer_gap' 'writer sequence gap reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.writerDuplicateCount = 1
$writerDuplicate = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $writerDuplicate.reasons 'cursor_writer_duplicate' 'writer duplicate reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.writerIdentityMismatchCount = 1
$writerMismatch = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $writerMismatch.reasons 'cursor_writer_identity_mismatch' 'writer identity mismatch reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.writerDeferredBatchCount = 1
$ledger.writerDeferredLineCount = 2
$ledger.writerUnresolvedDeferredBatchCount = 1
$ledger.writerUnresolvedDeferredLineCount = 2
$writerDeferred = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $writerDeferred.reasons 'cursor_writer_deferred_unresolved' 'unresolved deferred writer batch reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.writerChronologyInversionCount = 1
$writerChronology = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $writerChronology.reasons 'cursor_writer_chronology_inversion' 'writer chronology inversion reason'
Assert-Contains $writerChronology.reasons 'cursor_writer_chronology_count_mismatch' 'reported chronology count must match recomputed writer evidence'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.writerChronologyCollisionCount = 1
$writerChronologyCollision = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $writerChronologyCollision.reasons 'cursor_writer_chronology_collision' 'writer chronology collision reason'
Assert-Contains $writerChronologyCollision.reasons 'cursor_writer_chronology_count_mismatch' 'reported collision count must match recomputed writer evidence'

$chronologyPath = Join-Path $fixture.ArtifactDir 'writer-chronology-fixture.log'
$crossWriterLines = New-Object System.Collections.Generic.List[string]
for ($batch = 1; $batch -le 24; $batch++) {
    $crossWriterLines.Add("event=b; log_writer_id=writer-b; log_writer_sequence=$batch; log_writer_batch_generation=$batch; log_writer_batch_first_sequence=$batch; log_writer_batch_last_sequence=$batch; log_writer_attempt_utc_ticks=$(1000 + (2 * $batch))") | Out-Null
    $crossWriterLines.Add("event=a; log_writer_id=writer-a; log_writer_sequence=$batch; log_writer_batch_generation=$batch; log_writer_batch_first_sequence=$batch; log_writer_batch_last_sequence=$batch; log_writer_attempt_utc_ticks=$(999 + (2 * $batch))") | Out-Null
}
[System.IO.File]::WriteAllText($chronologyPath, (($crossWriterLines.ToArray() -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
$crossWriterChronology = Get-RouteAcceptanceEvidenceWriterChronology -Path $chronologyPath
Assert-True $crossWriterChronology.batchEvidenceValid 'cross-writer batch evidence must be valid'
Assert-Equal $crossWriterChronology.inversionCount 0 'cross-writer physical order must not create inversions'
Assert-Equal $crossWriterChronology.collisionCount 0 'cross-writer physical order must not create collisions'

$sameWriterInversionLines = @(
    'event=a; log_writer_id=writer-a; log_writer_sequence=1; log_writer_batch_generation=1; log_writer_batch_first_sequence=1; log_writer_batch_last_sequence=1; log_writer_attempt_utc_ticks=200',
    'event=a; log_writer_id=writer-a; log_writer_sequence=2; log_writer_batch_generation=2; log_writer_batch_first_sequence=2; log_writer_batch_last_sequence=2; log_writer_attempt_utc_ticks=100'
)
[System.IO.File]::WriteAllText($chronologyPath, (($sameWriterInversionLines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
$sameWriterInversion = Get-RouteAcceptanceEvidenceWriterChronology -Path $chronologyPath
Assert-Equal $sameWriterInversion.inversionCount 1 'same-writer decreasing batch timestamp must invert'

$sameWriterCollisionLines = @(
    'event=a; log_writer_id=writer-a; log_writer_sequence=1; log_writer_batch_generation=1; log_writer_batch_first_sequence=1; log_writer_batch_last_sequence=1; log_writer_attempt_utc_ticks=100',
    'event=a; log_writer_id=writer-a; log_writer_sequence=2; log_writer_batch_generation=2; log_writer_batch_first_sequence=2; log_writer_batch_last_sequence=2; log_writer_attempt_utc_ticks=100'
)
[System.IO.File]::WriteAllText($chronologyPath, (($sameWriterCollisionLines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
$sameWriterCollision = Get-RouteAcceptanceEvidenceWriterChronology -Path $chronologyPath
Assert-Equal $sameWriterCollision.collisionCount 1 'same-writer distinct equal batch timestamps must collide'

$invalidBatchLines = @(
    'event=a; log_writer_id=writer-a; log_writer_sequence=1; log_writer_batch_generation=1; log_writer_batch_first_sequence=1; log_writer_batch_last_sequence=3; log_writer_attempt_utc_ticks=100',
    'event=a; log_writer_id=writer-a; log_writer_sequence=3; log_writer_batch_generation=1; log_writer_batch_first_sequence=1; log_writer_batch_last_sequence=3; log_writer_attempt_utc_ticks=100'
)
[System.IO.File]::WriteAllText($chronologyPath, (($invalidBatchLines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
$invalidBatchChronology = Get-RouteAcceptanceEvidenceWriterChronology -Path $chronologyPath
Assert-False $invalidBatchChronology.batchEvidenceValid 'noncontiguous batch range must fail validation'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.writerDrainMissingCount = 1
$writerDrain = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $writerDrain.reasons 'cursor_writer_drain_missing' 'writer drain missing reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.final.writers[0].acceptanceRunId = 'wrong-run'
$writerWrongRun = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $writerWrongRun.reasons 'cursor_writer_acceptance_run_id_mismatch' 'writer run identity reason'
Assert-Equal $writerWrongRun.producerWriterIntegrityVerdict 'PASS' 'producer writer verdict remains separately visible'
Assert-Equal $writerWrongRun.writerIntegrityVerdict 'FAIL' 'revalidated writer identity failure overrides producer verdict'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.final.writers[0].firstSequence = 2
$writerInvalidSequence = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $writerInvalidSequence.reasons 'cursor_writer_snapshot_invalid' 'writer sequence snapshot reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.final.writers = @()
$writerMissing = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $writerMissing.reasons 'cursor_final_writer_snapshot_missing' 'final writer snapshot reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.writerFailureMarkerFiles[0].markerCount = 1
$markerCountMismatch = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $markerCountMismatch.reasons 'cursor_writer_failure_marker_count_mismatch' 'writer failure marker count reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$markerPath = [string]$ledger.writerFailureMarkerFiles[0].path
[System.IO.File]::WriteAllText($markerPath, "event=operational_log_write_failed; log_writer_id=tampered; log_writer_sequence=3; failure_code=io_error`n", [System.Text.UTF8Encoding]::new($false))
$tamperedMarkerSnapshot = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $tamperedMarkerSnapshot.reasons 'cursor_writer_failure_marker_sha256_mismatch' 'tampered writer output snapshot hash reason'
Assert-Contains $tamperedMarkerSnapshot.reasons 'cursor_writer_failure_marker_count_mismatch' 'tampered writer output snapshot marker count reason'
Assert-Equal $tamperedMarkerSnapshot.producerWriterIntegrityVerdict 'PASS' 'producer marker verdict remains separately visible'
Assert-Equal $tamperedMarkerSnapshot.writerIntegrityVerdict 'FAIL' 'revalidated marker failure overrides producer verdict'
[System.IO.File]::WriteAllText($markerPath, '', [System.Text.UTF8Encoding]::new($false))

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.initial.activeGenerationKey = ''
$missingInitial = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $missingInitial.reasons 'cursor_initial_generation_missing' 'missing initial generation reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.final.activeGenerationKey = ''
$missingFinal = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $missingFinal.reasons 'cursor_final_generation_missing' 'missing final generation reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.gapCount = 1
$gap = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $gap.reasons 'cursor_rotation_gap' 'cursor gap reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.duplicateIndexCount = 1
$duplicate = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $duplicate.reasons 'cursor_duplicate_index' 'duplicate retained event index reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.consumedIndexSha256 = ('f' * 64)
$arbitraryConsumedDigest = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $arbitraryConsumedDigest.reasons 'cursor_consumed_index_sha256_mismatch' 'consumed generation/ordinal digest must be recomputed'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.consumedIndexRanges = @($ledger.consumedIndexRanges[0], $ledger.consumedIndexRanges[0], $ledger.consumedIndexRanges[1])
$duplicateGenerationRange = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $duplicateGenerationRange.reasons 'cursor_consumed_index_range_duplicate_generation' 'each final generation has one unique consumed range'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.consumedIndexRanges = @($ledger.consumedIndexRanges[0])
$missingGenerationRange = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $missingGenerationRange.reasons 'cursor_consumed_index_range_generation_missing' 'every final generation requires a consumed range'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.consumedIndexRanges[0].count = 2
$ledger.consumedIndexRanges[0].lastLineOrdinal = 1
$ledger.consumedIndexRanges[1].count = 0
$ledger.consumedIndexRanges[1].firstLineOrdinal = -1
$ledger.consumedIndexRanges[1].lastLineOrdinal = -1
$redistributedGenerationRanges = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $redistributedGenerationRanges.reasons 'cursor_consumed_index_range_generation_count_mismatch' 'range count must equal its generation consumed count'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.final.generations = @([pscustomobject]@{ generationKey = 'gen-2' })
$nonMonotonic = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $nonMonotonic.reasons 'cursor_generation_not_monotonic' 'rotation generation monotonicity reason'

$diagnostics = Get-Content -LiteralPath $fixture.DiagnosticsPath -Raw | ConvertFrom-Json
$diagnostics.indexed_event_count = 1
$unbalanced = Get-RouteAcceptanceCursorEvidence -LogLedger $fixture.LedgerPath -AnalyzerDiagnostics $diagnostics -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $unbalanced.reasons 'cursor_event_index_count_mismatch' 'event/index count mismatch reason'

$diagnostics = Get-Content -LiteralPath $fixture.DiagnosticsPath -Raw | ConvertFrom-Json
$diagnostics.log_file_count = 2
$multipleLogs = Get-RouteAcceptanceCursorEvidence -LogLedger $fixture.LedgerPath -AnalyzerDiagnostics $diagnostics -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $multipleLogs.reasons 'cursor_log_file_count_mismatch' 'log file count mismatch reason'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.PSObject.Properties.Remove('cursorIntegrityVerdict')
$missingProducerVerdict = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $missingProducerVerdict.reasons 'cursor_reported_integrity_verdict_missing' 'producer cursor verdict is required'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.PSObject.Properties.Remove('gapCount')
$ledger.PSObject.Properties.Remove('duplicateIndexCount')
$ledger.PSObject.Properties.Remove('consumedIndexCount')
$missingCounts = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $missingCounts.reasons 'cursor_gap_count_missing' 'gap count is required'
Assert-Contains $missingCounts.reasons 'cursor_duplicate_index_count_missing' 'duplicate count is required'
Assert-Contains $missingCounts.reasons 'cursor_consumed_index_count_missing' 'consumed index count is required'

$ledger = Get-Content -LiteralPath $fixture.LedgerPath -Raw | ConvertFrom-Json
$ledger.final.generations[0].PSObject.Properties.Remove('consumedByteOffset')
$missingGenerationConsumption = Get-RouteAcceptanceCursorEvidence -LogLedger $ledger -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $missingGenerationConsumption.reasons 'cursor_generation_consumed_fields_missing' 'generation consumption fields are required'

$wrongRun = Get-RouteAcceptanceCursorEvidence -LogLedger $fixture.LedgerPath -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId ([guid]::NewGuid().ToString('N')) -ScenarioRunId $fixture.ScenarioRunId
Assert-Contains $wrongRun.reasons 'cursor_acceptance_run_id_mismatch' 'acceptance run ID must correlate'
$wrongScenario = Get-RouteAcceptanceCursorEvidence -LogLedger $fixture.LedgerPath -AnalyzerDiagnostics $fixture.DiagnosticsPath -AcceptanceRunId $fixture.RunId -ScenarioRunId ($fixture.RunId + ':scenario:2')
Assert-Contains $wrongScenario.reasons 'cursor_scenario_run_id_mismatch' 'scenario run ID must correlate'
""");

        AssertScriptPassed(result);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task EvidenceSnapshot_HashesActualFilesAndAcceptsSha1GitHead()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunEvidenceScriptAsync(
            FixturePrelude +
            """
$fixture = New-EvidenceFixture -Name 'hashes'
$snapshot = $fixture.Initial
Assert-Equal $snapshot.runtime.sha256 (Get-RouteAcceptanceEvidenceSha256 -Path $fixture.ExecutablePath) 'runtime file hash'
Assert-Equal $snapshot.bridge.sha256 (Get-RouteAcceptanceEvidenceSha256 -Path (Join-Path $fixture.BridgeDir 'index.js')) 'bridge file hash'
Assert-True $snapshot.bridge.declaredHashMatches 'bridge manifest declaration'
Assert-Equal $snapshot.source.gitCommitSha ('a' * 40) 'SHA-1 Git head'
Assert-True (Test-RouteAcceptanceEvidenceGitCommitValue -Value ('a' * 40)) 'SHA-1 Git head validator'
Assert-True (Test-RouteAcceptanceEvidenceGitCommitValue -Value ('a' * 64)) 'SHA-256 Git head validator'
Assert-False (Test-RouteAcceptanceEvidenceGitCommitValue -Value ('a' * 39)) 'invalid Git head validator'
""");

        AssertScriptPassed(result);
    }

    private static async Task<ScriptResult> RunEvidenceScriptAsync(string script)
    {
        var helperPath = Path.Combine(FindRepoRoot(), "tools", "FileTransferRouteAcceptanceEvidence.ps1");
        return await RunPowerShellScriptTextAsync(script, [helperPath]);
    }

    private static void AssertScriptPassed(ScriptResult result)
    {
        Assert.True(
            result.ExitCode == 0,
            $"Expected Phase 5 evidence helper test to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
    }

    private const string FixturePrelude = """
param([Parameter(Mandatory = $true)][string]$HelperPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. $HelperPath

$script:EvidenceTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('nlink-phase5-provenance-' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($script:EvidenceTempRoot) | Out-Null

function Assert-True {
    param([bool]$Value, [string]$Message)
    if (-not $Value) { throw $Message }
}

function Assert-False {
    param([bool]$Value, [string]$Message)
    if ($Value) { throw $Message }
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Message)
    if (-not [string]::Equals([string]$Actual, [string]$Expected, [System.StringComparison]::Ordinal)) {
        throw ("{0}: expected={1}; actual={2}" -f $Message, $Expected, $Actual)
    }
}

function Assert-Contains {
    param([object[]]$Values, [string]$Expected, [string]$Message)
    if (@($Values) -notcontains $Expected) {
        throw ("{0}: expected '{1}' in '{2}'" -f $Message, $Expected, (@($Values) -join ','))
    }
}

function Write-JsonNoBom {
    param([string]$Path, $Value)
    $json = $Value | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function New-EvidenceFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$ScenarioName = 'scenario',
        [switch]$DeclaredBridgeMismatch
    )

    $runId = [guid]::NewGuid().ToString('N')
    $scenarioRunId = $runId + ':' + (ConvertTo-RouteAcceptanceEvidenceScenarioName -ScenarioName $ScenarioName) + ':1'
    $root = Join-Path $script:EvidenceTempRoot $Name
    $artifactDir = Join-Path $root 'artifacts\run'
    $bridgeDir = Join-Path $root 'portable\bridge\win-x64'
    [System.IO.Directory]::CreateDirectory($artifactDir) | Out-Null
    [System.IO.Directory]::CreateDirectory($bridgeDir) | Out-Null

    $executablePath = Join-Path $root 'portable\nLink.exe'
    $harnessOne = Join-Path $root 'tools\Run-FileTransferRouteAcceptance.ps1'
    $harnessTwo = Join-Path $root 'tools\FileTransferRouteAcceptanceEvidence.ps1'
    $harnessThree = Join-Path $root 'tools\Run-FileTransferTunaGuiSmoke.ps1'
    $harnessFour = Join-Path $root 'tools\GuiSmoke-Windows.ps1'
    $harnessFive = Join-Path $root 'tools\GuiSmokeLogLedger.ps1'
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($harnessOne)) | Out-Null
    [System.IO.File]::WriteAllText($executablePath, 'runtime-bytes')
    [System.IO.File]::WriteAllText($harnessOne, 'harness-one')
    [System.IO.File]::WriteAllText($harnessTwo, 'harness-two')
    [System.IO.File]::WriteAllText($harnessThree, 'harness-three')
    [System.IO.File]::WriteAllText($harnessFour, 'harness-four')
    [System.IO.File]::WriteAllText($harnessFive, 'harness-five')
    [System.IO.File]::WriteAllText((Join-Path $bridgeDir 'index.js'), 'bridge-bytes')
    $bridgeHash = Get-RouteAcceptanceEvidenceSha256 -Path (Join-Path $bridgeDir 'index.js')
    $declaredHash = if ($DeclaredBridgeMismatch) { ('f' * 64) } else { $bridgeHash }
    Write-JsonNoBom -Path (Join-Path $bridgeDir 'bridge-manifest.json') -Value ([ordered]@{ bridgeScriptSha256 = $declaredHash })

    $slicePath = Join-Path $artifactDir 'filetransfer-retained-ledger-slice.log'
    $writerLines = @(
        "event=operational_log_writer_started; acceptance_run_id=$runId; scenario_run_id=$scenarioRunId; log_writer_id=writer-current; log_writer_sequence=1; log_writer_batch_generation=1; log_writer_batch_first_sequence=1; log_writer_batch_last_sequence=1; log_writer_attempt_utc_ticks=100",
        "event=operational_log_writer_drained; acceptance_run_id=$runId; scenario_run_id=$scenarioRunId; log_writer_id=writer-current; log_writer_sequence=2; log_writer_batch_generation=2; log_writer_batch_first_sequence=2; log_writer_batch_last_sequence=2; log_writer_attempt_utc_ticks=200"
    )
    [System.IO.File]::WriteAllText($slicePath, (($writerLines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
    $sliceHash = Get-RouteAcceptanceEvidenceSha256 -Path $slicePath
    $writerFailureMarkerPath = Join-Path $artifactDir 'filetransfer-gui-log-writer-failure-markers.log'
    [System.IO.File]::WriteAllText($writerFailureMarkerPath, '', [System.Text.UTF8Encoding]::new($false))
    $writer = [ordered]@{
        writerId = 'writer-current'
        acceptanceRunId = $runId
        scenarioRunId = $scenarioRunId
        firstSequence = 1
        lastSequence = 2
        observedCount = 2
    }
    $ledgerPath = Join-Path $artifactDir 'filetransfer-gui-log-ledger.json'
    $ledger = [ordered]@{
        schemaVersion = 1
        event = 'filetransfer_gui_log_ledger'
        acceptanceRunId = $runId
        scenarioRunId = $scenarioRunId
        ledgerId = [guid]::NewGuid().ToString('N')
        initial = [ordered]@{
            ledgerId = ''
            bookmark = 0
            lineCount = 1
            activeGenerationKey = 'gen-1'
            generations = @([ordered]@{ generationKey = 'gen-1'; consumedByteOffset = 0; consumedLineCount = 1 })
            writers = @()
        }
        final = [ordered]@{
            ledgerId = ''
            bookmark = 0
            lineCount = 2
            activeGenerationKey = 'gen-2'
            generations = @(
                [ordered]@{ generationKey = 'gen-1'; consumedByteOffset = 10; consumedLineCount = 1 },
                [ordered]@{ generationKey = 'gen-2'; consumedByteOffset = 10; consumedLineCount = 1 }
            )
            writers = @($writer)
        }
        rotationCount = 2
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
        writerRecoveryVerdict = 'PASS'
        writerChronologyVerdict = 'PASS'
        writerIntegrityVerdict = 'PASS'
        writerIntegrityReasons = @()
        writerFailureMarkerFiles = @([ordered]@{
            path = $writerFailureMarkerPath
            sha256 = Get-RouteAcceptanceEvidenceSha256 -Path $writerFailureMarkerPath
            markerCount = 0
        })
        writerMarkerFiles = @([ordered]@{
            path = $writerFailureMarkerPath
            sha256 = Get-RouteAcceptanceEvidenceSha256 -Path $writerFailureMarkerPath
            deferredMarkerCount = 0
            recoveredMarkerCount = 0
            terminalFailureMarkerCount = 0
        })
        writerDeferredBatches = @()
        writerDrains = @([ordered]@{
            writerId = $writer.writerId
            drainSequence = $writer.lastSequence
            lastPayloadSequence = ($writer.lastSequence - 1)
            valid = $true
        })
        writerEvidenceSlice = [ordered]@{
            path = $slicePath
            sha256 = $sliceHash
            lineCount = 2
        }
        consumedIndexCount = 2
        consumedIndexSha256 = Get-RouteAcceptanceEvidenceTextSha256 -Value ((@("gen-1`n0", "gen-2`n0") | Sort-Object) -join [char]31)
        consumedIndexRanges = @(
            [ordered]@{ generationKey = 'gen-1'; firstLineOrdinal = 0; lastLineOrdinal = 0; count = 1 },
            [ordered]@{ generationKey = 'gen-2'; firstLineOrdinal = 0; lastLineOrdinal = 0; count = 1 }
        )
        syncCount = 3
        failureCode = ''
        failureDetail = ''
        retainedSlice = [ordered]@{
            path = $slicePath
            sha256 = $sliceHash
            lineCount = 2
        }
        cursorIntegrityVerdict = 'PASS'
        cursorIntegrityReasons = @()
    }
    $ledger.initial.ledgerId = $ledger.ledgerId
    $ledger.final.ledgerId = $ledger.ledgerId
    Write-JsonNoBom -Path $ledgerPath -Value $ledger

    $diagnosticsPath = Join-Path $artifactDir 'filetransfer-retained-analysis-stage-diagnostics.json'
    Write-JsonNoBom -Path $diagnosticsPath -Value ([ordered]@{
        event_count = 2
        indexed_event_count = 2
        log_file_count = 1
    })

    $gitState = [pscustomobject]@{
        gitCommitSha = ('a' * 40)
        dirty = $false
        normalizedStatusSha256 = Get-RouteAcceptanceEvidenceTextSha256 -Value ''
    }
    $harnessFiles = @($harnessOne, $harnessTwo, $harnessThree, $harnessFour, $harnessFive)
    $initial = New-RouteAcceptanceEvidenceSnapshot `
        -RepoRoot $root `
        -PortableExecutablePath $executablePath `
        -HarnessFiles $harnessFiles `
        -BridgeDir $bridgeDir `
        -GitState $gitState

    return [pscustomobject]@{
        Root = $root
        ArtifactDir = $artifactDir
        BridgeDir = $bridgeDir
        ExecutablePath = $executablePath
        HarnessFiles = $harnessFiles
        LedgerPath = $ledgerPath
        DiagnosticsPath = $diagnosticsPath
        AnalyzerInputPath = $slicePath
        GitState = $gitState
        Initial = $initial
        RunId = $runId
        ScenarioRunId = $scenarioRunId
    }
}
""";
}
