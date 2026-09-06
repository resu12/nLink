namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRetainedSnapshotReuseTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RetainedSnapshotHandoff_ExactOrderedInputReusesEvaluationWithoutMutation(int lineCount)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            [string[]]$lines = @(for ($i = 0; $i -lt [int]$Case; $i++) { "event=unrelated; value=$i" })
            $evaluation = & $script:RealEvaluator -Lines $lines
            $handoff = [pscustomobject]@{ Lines = [string[]]@($lines); Evaluation = $evaluation }
            $summary = New-TestSummary -Lines $lines
            $evaluationBefore = $evaluation | ConvertTo-Json -Depth 64 -Compress
            $handoffBefore = $handoff | ConvertTo-Json -Depth 64 -Compress
            $summaryBefore = $summary | ConvertTo-Json -Depth 64 -Compress
            foreach ($attempt in 1..2) {
                $output = @(Get-FileTransferRecoveryFailureClassification -Summary $summary -CoordinatorSnapshotEvaluationHandoff $handoff)
                if ($output.Count -ne 1 -or -not [object]::ReferenceEquals($evaluation, $output[0])) { throw 'exact input did not return its original evaluation' }
            }
            if ($script:EvaluationCalls.Count -ne 0) { throw 'exact input was reevaluated' }
            $fresh = Get-FileTransferRecoveryFailureClassification -Summary $summary
            if ($script:EvaluationCalls.Count -ne 1) { throw 'absent handoff did not independently evaluate' }
            Assert-ExactLines $lines $script:EvaluationCalls[0].Lines
            if (($fresh | ConvertTo-Json -Depth 64 -Compress) -cne $evaluationBefore -or
                ($evaluation | ConvertTo-Json -Depth 64 -Compress) -cne $evaluationBefore -or
                ($handoff | ConvertTo-Json -Depth 64 -Compress) -cne $handoffBefore -or
                ($summary | ConvertTo-Json -Depth 64 -Compress) -cne $summaryBefore) { throw 'handoff reuse changed an input or evaluation' }
            Assert-NoHandoffProperties $fresh
            Write-Output 'snapshot-reuse-ok'
            """, lineCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task RetainedSnapshotHandoff_ChangedOrderedInputsAndDifferentSummariesEvaluateIndependently()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            [string[]]$original = @('event=unrelated; value=A', 'event=unrelated; value=B', 'event=unrelated; value=C')
            $evaluation = & $script:RealEvaluator -Lines $original
            $handoff = [pscustomobject]@{ Lines = [string[]]@($original); Evaluation = $evaluation }
            $cases = @(
                [pscustomobject]@{ Lines = [string[]]@($original[1], $original[0], $original[2]) },
                [pscustomobject]@{ Lines = [string[]]@($original[0], 'event=unrelated; value=changed', $original[2]) },
                [pscustomobject]@{ Lines = [string[]]@($original[0], $original[2]) },
                [pscustomobject]@{ Lines = [string[]]@($original[0], $original[1], $original[2], $original[2]) },
                [pscustomobject]@{ Lines = [string[]]@('event=unrelated; value=a', $original[1], $original[2]) },
                [pscustomobject]@{ Lines = [string[]]@($original[0], ($original[1] + ' '), $original[2]) },
                [pscustomobject]@{ Lines = [string[]]@() })
            foreach ($candidate in $cases) {
                $summary = New-TestSummary -Lines $candidate.Lines
                $before = $script:EvaluationCalls.Count
                $actual = Get-FileTransferRecoveryFailureClassification -Summary $summary -CoordinatorSnapshotEvaluationHandoff $handoff
                if ($script:EvaluationCalls.Count -ne $before + 1 -or [object]::ReferenceEquals($actual, $evaluation)) { throw 'different input reused an unrelated evaluation' }
                Assert-ExactLines $candidate.Lines $script:EvaluationCalls[$before].Lines
                $expected = & $script:RealEvaluator -Lines $candidate.Lines
                if (($actual | ConvertTo-Json -Depth 64 -Compress) -cne ($expected | ConvertTo-Json -Depth 64 -Compress)) { throw 'mismatch changed normal evaluation' }
            }
            $summary = New-TestSummary -Lines $original
            $summary.AllEvents[1].RawLine = 'event=unrelated; value=mutated-after-handoff'
            $before = $script:EvaluationCalls.Count
            $null = Get-FileTransferRecoveryFailureClassification -Summary $summary -CoordinatorSnapshotEvaluationHandoff $handoff
            if ($script:EvaluationCalls.Count -ne $before + 1) { throw 'mutated summary retained stale reuse' }
            Assert-ExactLines @($original[0], 'event=unrelated; value=mutated-after-handoff', $original[2]) $script:EvaluationCalls[$before].Lines
            $matchingOtherSummary = New-TestSummary -Lines $original
            $again = Get-FileTransferRecoveryFailureClassification -Summary $matchingOtherSummary -CoordinatorSnapshotEvaluationHandoff $handoff
            if (-not [object]::ReferenceEquals($again, $evaluation) -or $script:EvaluationCalls.Count -ne $before + 1) { throw 'reuse was not invocation-local and exact-input based' }
            Write-Output 'snapshot-reuse-ok'
            """);
    }

    [Fact]
    public async Task RetainedSnapshotHandoff_InvalidShapesAndFallbackSelectionFailToReuse()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            [string[]]$lines = @('event=unrelated; value=global', 'event=unrelated; value=transfer')
            $evaluation = & $script:RealEvaluator -Lines $lines
            $partialNonLegacy = $evaluation | ConvertTo-Json -Depth 64 | ConvertFrom-Json
            $partialNonLegacy.Mode = 'SnapshotV1Authoritative'
            $invalid = @(
                $null,
                [pscustomobject]@{ Evaluation = $evaluation },
                [pscustomobject]@{ Lines = $lines },
                [pscustomobject]@{ Lines = [object[]]@($lines); Evaluation = $evaluation },
                [pscustomobject]@{ Lines = [string[]]@($lines); Evaluation = $null },
                [pscustomobject]@{ Lines = [string[]]@($lines); Evaluation = 'not-an-evaluation' },
                [pscustomobject]@{ Lines = [string[]]@($lines); Evaluation = [pscustomobject]@{ Mode = 'LegacyV0' } },
                [pscustomobject]@{ Lines = [string[]]@($lines); Evaluation = $partialNonLegacy },
                @{ Lines = [string[]]@($lines); Evaluation = $evaluation })
            foreach ($handoff in $invalid) {
                $before = $script:EvaluationCalls.Count
                $actual = Get-FileTransferRecoveryFailureClassification -Summary (New-TestSummary -Lines $lines) -CoordinatorSnapshotEvaluationHandoff $handoff
                if ($script:EvaluationCalls.Count -ne $before + 1 -or [object]::ReferenceEquals($actual, $evaluation)) { throw 'invalid handoff shape was trusted' }
                Assert-ExactLines $lines $script:EvaluationCalls[$before].Lines
            }
            $handoff = [pscustomobject]@{ Lines = [string[]]@($lines); Evaluation = $evaluation }
            if ($null -ne (Resolve-FileTransferCoordinatorSnapshotEvaluationHandoff -Lines $null -Handoff $handoff)) { throw 'absent input identity was accepted' }
            $summary = New-TestSummary -Lines $lines
            $summary.GlobalEvents = [object[]]@($summary.AllEvents[0])
            $summary.TransferEvents = [object[]]@($summary.AllEvents[1])
            $summary.AllEvents = [object[]]@()
            $before = $script:EvaluationCalls.Count
            $fallback = Get-FileTransferRecoveryFailureClassification -Summary $summary -CoordinatorSnapshotEvaluationHandoff $handoff
            if (-not [object]::ReferenceEquals($fallback, $evaluation) -or $script:EvaluationCalls.Count -ne $before) { throw 'exact GlobalEvents + TransferEvents fallback did not reuse' }
            $summary.PSObject.Properties.Remove('AllEvents')
            $fallback = Get-FileTransferRecoveryFailureClassification -Summary $summary -CoordinatorSnapshotEvaluationHandoff $handoff
            if (-not [object]::ReferenceEquals($fallback, $evaluation) -or $script:EvaluationCalls.Count -ne $before) { throw 'missing AllEvents changed exact fallback identity' }
            $summary.GlobalEvents = [object[]]@()
            $null = Get-FileTransferRecoveryFailureClassification -Summary $summary -CoordinatorSnapshotEvaluationHandoff $handoff
            if ($script:EvaluationCalls.Count -ne $before + 1) { throw 'transfer-only selection reused all-event evaluation' }
            Assert-ExactLines @($lines[1]) $script:EvaluationCalls[$before].Lines
            Write-Output 'snapshot-reuse-ok'
            """);
    }

    [Fact]
    public async Task RetainedSnapshotHandoff_MalformedAndConflictingV1EvidenceRemainFailClosed()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $catalog = Get-FileTransferCoordinatorEvidenceCatalog
            $prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=coordinator-reuse; session_id=session-reuse; transfer_id=transfer-reuse; direction=outbound"
            $start = "event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=0; initial_state_hash=initial; emitted_family_coverage=route_runtime; shadow_family_coverage=route_runtime; authoritative_family_coverage=; authority_effect=none"
            $end = "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=0; final_state_hash=initial; transition_count=0; emitted_family_coverage=route_runtime; shadow_family_coverage=route_runtime; authoritative_family_coverage=; authority_effect=none"
            $cases = @(
                [pscustomobject]@{ Lines = [string[]]@("event=filetransfer_coordinator_transition_snapshot; $prefix; snapshot_sha256=bad; snapshot_payload=not-base64") },
                [pscustomobject]@{ Lines = [string[]]@($start, ($start -replace 'initial_state_hash=initial', 'initial_state_hash=conflict'), $end) },
                [pscustomobject]@{ Lines = [string[]]@($start) })
            foreach ($candidate in $cases) {
                $baseline = & $script:RealEvaluator -Lines $candidate.Lines
                if ($baseline.ArtifactEvidenceVerdict -cne 'fail') { throw 'malformed V1 fixture did not fail closed' }
                $beforeJson = $baseline | ConvertTo-Json -Depth 64 -Compress
                $handoff = [pscustomobject]@{ Lines = [string[]]@($candidate.Lines); Evaluation = $baseline }
                $before = $script:EvaluationCalls.Count
                $reused = Get-FileTransferRecoveryFailureClassification -Summary (New-TestSummary -Lines $candidate.Lines) -CoordinatorSnapshotEvaluationHandoff $handoff
                if ($script:EvaluationCalls.Count -ne $before -or -not [object]::ReferenceEquals($baseline, $reused) -or
                    ($reused | ConvertTo-Json -Depth 64 -Compress) -cne $beforeJson) { throw 'reuse changed malformed-evidence evaluation' }
                foreach ($family in @('route_runtime', 'route_transition', 'fallback_tail_yield', 'regular_v4_frontier_repair_pressure')) {
                    $freshProof = Get-FileTransferCoordinatorSnapshotCorrelationResult -Evaluation $baseline -Family $family
                    $reusedProof = Get-FileTransferCoordinatorSnapshotCorrelationResult -Evaluation $reused -Family $family
                    if (($freshProof | ConvertTo-Json -Depth 64 -Compress) -cne ($reusedProof | ConvertTo-Json -Depth 64 -Compress)) { throw 'reuse changed family proof metadata' }
                    Assert-NoHandoffProperties $reusedProof
                }
                if (($baseline | ConvertTo-Json -Depth 64 -Compress) -cne $beforeJson) { throw 'proof consumption mutated shared evaluation' }
                Assert-NoHandoffProperties $reused
            }
            Write-Output 'snapshot-reuse-ok'
            """);
    }

    [Fact]
    public async Task RetainedSnapshotHandoff_DataPathConsumerPreservesCompleteProofAndEvaluation()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            [string[]]$lines = @('event=unrelated; transfer_id=data-path-reuse')
            $evaluation = & $script:RealEvaluator -Lines $lines
            $beforeJson = $evaluation | ConvertTo-Json -Depth 64 -Compress
            $freshSummary = New-TestSummary -Lines $lines
            $reusedSummary = New-TestSummary -Lines $lines
            $baseline = Get-FileTransferFileTunaV4DataPathProof -Summary $freshSummary
            $before = $script:EvaluationCalls.Count
            $classifier = Find-TestFunction $script:ArtifactAst 'Get-FileTransferRecoveryFailureClassification'
            $dataPathAssignment = @($classifier.Body.EndBlock.Statements | Where-Object {
                $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -ceq '$fileTunaV4DataPathProof'
            })
            if ($dataPathAssignment.Count -ne 1) { throw 'classifier data-path assignment is not unique' }
            $Summary = $reusedSummary
            $coordinatorSnapshotEvaluation = $evaluation
            . ([scriptblock]::Create($dataPathAssignment[0].Extent.Text))
            $reused = $fileTunaV4DataPathProof
            if ($before -ne 1 -or $script:EvaluationCalls.Count -ne $before) { throw 'data-path consumer reentered snapshot evaluation' }
            if (($baseline | ConvertTo-Json -Depth 64 -Compress) -cne ($reused | ConvertTo-Json -Depth 64 -Compress) -or
                ($evaluation | ConvertTo-Json -Depth 64 -Compress) -cne $beforeJson) { throw 'data-path reuse changed complete proof or evaluation' }
            $again = Get-FileTransferFileTunaV4DataPathProof -Summary $reusedSummary -CoordinatorSnapshotEvaluation $evaluation
            if (-not [object]::ReferenceEquals($again, $reused) -or $script:EvaluationCalls.Count -ne $before) { throw 'existing per-Summary data-path cache changed' }
            Assert-NoHandoffProperties $reused
            Assert-NoHandoffProperties $reusedSummary
            Write-Output 'snapshot-reuse-ok'
            """);
    }

    [Fact]
    public async Task RetainedSnapshotHandoff_WritersKeepIndependentFullCompactAndCompleteSourceEvaluations()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $orchestration = Read-TestAst (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
            $actionWrapper = Find-TestFunction $orchestration 'Invoke-FileTransferFullReportAction'
            . ([scriptblock]::Create($actionWrapper.Extent.Text))
            $fullReportStageObserver = { param([string]$Status) }
            foreach ($name in @('Write-FileTransferCompactRetainedAnalysisArtifacts', 'Invoke-FileTransferRetainedAnalysis')) {
                $definition = Find-TestFunction $orchestration $name
                $assignments = @($definition.Body.FindAll({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true))
                $full = @($assignments | Where-Object { $_.Left.Extent.Text -ceq '$fullSnapshotEvaluation' })
                $compact = @($assignments | Where-Object { $_.Left.Extent.Text -ceq '$compactSnapshotEvaluation' })
                $parity = @($assignments | Where-Object { $_.Left.Extent.Text -ceq '$snapshotParity' })
                if ($full.Count -ne 1 -or $compact.Count -ne 1 -or $parity.Count -ne 1 -or
                    $full[0].Extent.Text -notmatch 'Get-FileTransferCoordinatorSnapshotEvaluation' -or
                    $compact[0].Extent.Text -notmatch 'Get-FileTransferCoordinatorSnapshotEvaluation' -or
                    $parity[0].Extent.Text -notmatch '-Full\s+\$fullSnapshotEvaluation' -or
                    $parity[0].Extent.Text -notmatch '-Compact\s+\$compactSnapshotEvaluation') { throw "independent full/compact evaluation contract changed: $name" }
                $rawLines = [string[]]@('event=unrelated; value=before', 'event=filetransfer_coordinator_snapshot_stream_started', 'event=unrelated; value=after')
                $eventsForArtifacts = (New-TestSummary -Lines $rawLines).AllEvents
                $Index = [pscustomobject]@{ AllEvents = $eventsForArtifacts }
                $snapshotCatalog = Get-FileTransferCoordinatorEvidenceCatalog
                if ($name -ceq 'Invoke-FileTransferRetainedAnalysis') {
                    $lineAssignment = @($assignments | Where-Object { $_.Left.Extent.Text -match '\$fullSnapshotLines$' })
                    if ($lineAssignment.Count -ne 1) { throw 'outer full input identity assignment missing' }
                    . ([scriptblock]::Create($lineAssignment[0].Extent.Text))
                    Assert-ExactLines $rawLines $fullSnapshotLines
                    $compactLineAssignment = @($assignments | Where-Object { $_.Left.Extent.Text -match '\$fullCompactSnapshotLines$' })
                    if ($compactLineAssignment.Count -ne 1) { throw 'outer compact input identity assignment missing' }
                    . ([scriptblock]::Create($compactLineAssignment[0].Extent.Text))
                    Assert-ExactLines @($rawLines[1]) $fullCompactSnapshotLines
                }
                $before = $script:EvaluationCalls.Count
                . ([scriptblock]::Create($full[0].Extent.Text))
                . ([scriptblock]::Create($compact[0].Extent.Text))
                if ($script:EvaluationCalls.Count -ne $before + 2 -or [object]::ReferenceEquals($fullSnapshotEvaluation, $compactSnapshotEvaluation)) { throw 'full and compact shared one evaluation' }
                Assert-ExactLines $rawLines $script:EvaluationCalls[$before].Lines
                Assert-ExactLines @($rawLines[1]) $script:EvaluationCalls[$before + 1].Lines
            }
            $writer = Find-TestFunction $script:ArtifactAst 'Write-FileTransferDiagnosticsArtifacts'
            $writerEvaluations = @($writer.Body.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Get-FileTransferCoordinatorSnapshotEvaluation'
            }, $true))
            if ($writerEvaluations.Count -ne 1 -or $writerEvaluations[0].Extent.Text -notmatch '-Lines\s+\$writerAllSnapshotLines') { throw 'writer lost complete-source snapshot input' }
            $Summary = New-TestSummary -Lines @('event=unrelated; value=all', 'event=unrelated; value=selected')
            $Summary.TransferEvents = [object[]]@($Summary.AllEvents[1])
            $before = $script:EvaluationCalls.Count
            $null = Write-FileTransferDiagnosticsArtifacts -Summary $Summary -GateResult ([pscustomobject]@{}) -ArtifactDir $RepoRoot
            if ($script:EvaluationCalls.Count -ne $before + 1) { throw 'writer did not independently evaluate without a handoff' }
            Assert-ExactLines @('event=unrelated; value=all', 'event=unrelated; value=selected') $script:EvaluationCalls[$before].Lines
            $classifier = Find-TestFunction $script:ArtifactAst 'Get-FileTransferRecoveryFailureClassification'
            Assert-TestCommandArgument $classifier 'Get-FileTransferFileTunaV4DataPathProof' 'CoordinatorSnapshotEvaluation' '$coordinatorSnapshotEvaluation'
            Assert-TestCommandArgument $writer 'Get-FileTransferRecoveryFailureClassification' 'CoordinatorSnapshotEvaluationHandoff' '$CoordinatorSnapshotEvaluationHandoff'
            $outer = Find-TestFunction $orchestration 'Invoke-FileTransferRetainedAnalysis'
            $writerCalls = @($outer.Body.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Write-FileTransferDiagnosticsArtifacts'
            }, $true))
            if ($writerCalls.Count -ne 1 -or $writerCalls[0].Extent.Text -notmatch '-CoordinatorSnapshotEvaluationHandoff\b') { throw 'outer full evaluation handoff is not forwarded' }
            Write-Output 'snapshot-reuse-ok'
            """);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("partial")]
    [InlineData("conflicting_duplicate")]
    [InlineData("identical_duplicate")]
    [InlineData("multiple_streams")]
    [InlineData("legacy")]
    [InlineData("empty")]
    [InlineData("fallback_empty")]
    [InlineData("fallback_missing")]
    public async Task RetainedSnapshotWriter_CompleteScopeControlsPublicationAndStrictGate(string shape)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var artifactDir = Path.Combine(Path.GetTempPath(), "nlink-snapshot-writer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);
        try
        {
            await RunHarnessAsync($$"""
                $shape = '{{shape}}'; $ArtifactDir = $Case
                $catalog = Get-FileTransferCoordinatorEvidenceCatalog
                $families = $catalog.RequiredFamilies -join ','
                $scope = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=writer-instance-A; session_id=fte1-session; transfer_id=fte1-transfer; direction=outbound; log_writer_id=writer-A"
                $coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families; authority_effect=none"
                $start = "event=filetransfer_coordinator_snapshot_stream_started; $scope; initial_state_generation=0; initial_state_hash=initial; $coverage"
                $end = "event=filetransfer_coordinator_snapshot_stream_completed; $scope; final_state_generation=0; final_state_hash=initial; transition_count=0; $coverage"
                $diagnostic = 'event=file_transfer_outbound_terminal; transfer_id=[redacted]; state=Completed'
                [string[]]$lines = @($start, $end, $diagnostic)
                switch ($shape) {
                    'partial' { $lines = @($start, $diagnostic) }
                    'conflicting_duplicate' { $lines = @($start, ($start -replace 'initial_state_hash=initial', 'initial_state_hash=conflict'), $end, $diagnostic) }
                    'identical_duplicate' { $lines = @($start, $start, $end, $end, $diagnostic) }
                    'multiple_streams' { $lines = @($start, $end, ($start -replace 'writer-instance-A','writer-instance-B' -replace 'writer-A','writer-B' -replace 'fte1-transfer','fte1-other-transfer'), ($end -replace 'writer-instance-A','writer-instance-B' -replace 'writer-A','writer-B' -replace 'fte1-transfer','fte1-other-transfer'), $diagnostic) }
                    'legacy' { $lines = @($diagnostic) }
                    'empty' { $lines = @() }
                }
                $summary = New-TestSummary -Lines $lines
                $summary.TransferEvents = @($summary.AllEvents | Where-Object EventName -CEQ 'file_transfer_outbound_terminal')
                if ($shape -in @('fallback_empty', 'fallback_missing')) {
                    $summary.GlobalEvents = @($summary.AllEvents | Where-Object EventName -CNE 'file_transfer_outbound_terminal')
                    $summary.AllEvents = [object[]]@()
                    if ($shape -ceq 'fallback_missing') { $summary.PSObject.Properties.Remove('AllEvents') }
                }
                $expected = & $script:RealEvaluator -Lines $lines
                $expectedMode = if ($shape -in @('legacy', 'empty')) { 'LegacyV0' } else { 'SnapshotV1Authoritative' }
                $expectedVerdict = if ($shape -in @('partial', 'conflicting_duplicate')) { 'fail' } else { 'pass' }
                if ($expected.Mode -cne $expectedMode -or $expected.ArtifactEvidenceVerdict -cne $expectedVerdict) { throw 'fixture does not establish its intended evidence contract' }
                $expectedJson = $expected | Select-Object * -ExcludeProperty __HeldPublicationProofClosures, __FallbackTailPeerProofBindings, __RoutePublicationEffects | ConvertTo-Json -Depth 64 -Compress
                $beforeSummary = $summary | ConvertTo-Json -Depth 64 -Compress
                foreach ($reuse in @('exact', 'absent', 'changed', 'reordered', 'truncated', 'duplicate')) {
                    [string[]]$handoffLines = @($lines)
                    switch ($reuse) {
                        'changed' { if ($handoffLines.Count) { $handoffLines[0] += ' ' } else { $handoffLines = @('event=changed') } }
                        'reordered' { if ($handoffLines.Count -gt 1) { [array]::Reverse($handoffLines) } else { $handoffLines = @($lines + 'event=other') } }
                        'truncated' { if ($handoffLines.Count) { $handoffLines = @($handoffLines | Select-Object -Skip 1) } else { $handoffLines = @('event=other') } }
                        'duplicate' { $handoffLines = @($lines + 'event=extra') }
                    }
                    $handoff = if ($reuse -ceq 'absent') { $null } else { [pscustomobject]@{ Lines = $handoffLines; Evaluation = $expected } }
                    $stages = [Collections.Generic.List[object]]::new()
                    $observer = { param($Name, $Status, $InputCount, $MissCount, $ReuseCount) $stages.Add([pscustomobject]@{Name=$Name;InputCount=$InputCount}) }
                    $before = $script:EvaluationCalls.Count
                    $snapshotEvaluation = Write-FileTransferDiagnosticsArtifacts -Summary $summary -GateResult ([pscustomobject]@{}) -ArtifactDir $ArtifactDir -CoordinatorSnapshotEvaluationHandoff $handoff -StageObserver $observer
                    $expectedCalls = if ($reuse -ceq 'exact') { 0 } else { 1 }
                    if ($script:EvaluationCalls.Count -ne $before + $expectedCalls) { throw "wrong writer evaluation count: $reuse" }
                    if ($reuse -ceq 'exact' -and -not [object]::ReferenceEquals($snapshotEvaluation, $expected)) { throw 'complete handoff was not reused exactly' }
                    if ($expectedCalls) { Assert-ExactLines $lines $script:EvaluationCalls[$before].Lines }
                    if (@($stages | Where-Object {$_.InputCount -ne $lines.Count}).Count) { throw 'writer stage count still describes a transfer-only subset' }
                    if (($snapshotEvaluation | Select-Object * -ExcludeProperty __HeldPublicationProofClosures, __FallbackTailPeerProofBindings, __RoutePublicationEffects | ConvertTo-Json -Depth 64 -Compress) -cne $expectedJson) { throw 'writer changed complete-source evidence' }

                    $effectiveVerdict='PASS'; $effectiveGateStatus='pass'; $effectiveNextArtifact=''; $effectiveHardFailures=[Collections.Generic.List[string]]::new()
                    . $script:WriterSnapshotGate
                    if (($effectiveVerdict -ceq 'FAIL_ARTIFACT_EVIDENCE') -ne ($expectedVerdict -ceq 'fail')) { throw 'writer strict-invalid gate lost declared V1' }
                    $StageObserver=$null; $verdictLines=@(); $writerAllEventCount=$lines.Count
                    . $script:WriterSnapshotPublication
                    $published = Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-coordinator-snapshot-summary.json') -Raw | ConvertFrom-Json
                    if (($published | ConvertTo-Json -Depth 64 -Compress) -cne $expectedJson) { throw 'generic Snapshot JSON differs from complete evaluation' }
                    Assert-NoHandoffProperties $published
                    $operator = @(Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-operator-verdict.txt'))
                    $text = @(Get-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-coordinator-snapshot-summary.txt'))
                    $LiveRouteProofMode='None'; $recoveryClassification=$null; $reportProofHandoff=$null
                    function New-FileTransferRouteConsistencySummaryLines { param($Summary,$LiveRouteProofMode,$RecoveryClassification,$ReportProofHandoff) 'route-proof-fixture' }
                    . $script:WriterSnapshotRouteMetadata
                    foreach ($line in $text) {
                        $pair=$line -split '=',2; $value=$pair[1]
                        if ($pair[0] -ceq 'boundary_hash' -and $value -ceq '') { $value='(none)' }
                        $metadata="coordinator_snapshot_$($pair[0])=$value"
                        if ($operator -cnotcontains $metadata -or $routeConsistencyLines -cnotcontains $metadata) { throw "writer metadata disagrees: $metadata" }
                    }
                    if (($summary | ConvertTo-Json -Depth 64 -Compress) -cne $beforeSummary) { throw 'writer changed selected transfer metrics or input collections' }
                }
                Write-Output 'snapshot-reuse-ok'
                """, artifactDir);
        }
        finally { TryDeleteDirectory(artifactDir); }
    }

    private static async Task RunHarnessAsync(string body, string argument = "")
    {
        var result = await RunPowerShellScriptTextAsync(HarnessPrefix + Environment.NewLine + body, [FindRepoRoot(), argument]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal("snapshot-reuse-ok", result.Stdout.Trim());
    }

    private const string HarnessPrefix = """
        param([string]$RepoRoot, [string]$Case)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Windows PowerShell 5.1 required' }
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
        function Read-TestAst([string]$Path) {
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
            if ($errors -and $errors.Count -ne 0) { throw ($errors | Out-String) }
            return $ast
        }
        function Find-TestFunction($Ast, [string]$Name) {
            $matches = @($Ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name
            }, $true))
            if ($matches.Count -ne 1) { throw "production function not unique: $Name" }
            return $matches[0]
        }
        function Assert-ExactLines([AllowEmptyCollection()][string[]]$Expected, [AllowEmptyCollection()][string[]]$Actual) {
            if ($Expected.Count -ne $Actual.Count) { throw 'snapshot input count changed' }
            for ($i = 0; $i -lt $Expected.Count; $i++) {
                if (-not [StringComparer]::Ordinal.Equals($Expected[$i], $Actual[$i])) { throw "snapshot input changed at $i" }
            }
        }
        function Assert-NoHandoffProperties($Value) {
            foreach ($property in $Value.PSObject.Properties) {
                if ($property.Name -in @('Handoff', 'CoordinatorSnapshotEvaluationHandoff', 'fullSnapshotEvaluationHandoff',
                        'SnapshotInputLines', 'CoordinatorSnapshotLines', 'fullSnapshotLines', 'Lines')) {
                    throw 'private handoff identity leaked into output'
                }
            }
        }
        function New-TestSummary([AllowEmptyCollection()][string[]]$Lines) {
            $events = New-Object System.Collections.Generic.List[object]
            for ($i = 0; $i -lt $Lines.Count; $i++) {
                $events.Add([pscustomobject]@{
                    EventName = Get-FileTransferCoordinatorSnapshotEventName -Line $Lines[$i]
                    RawLine = $Lines[$i]; Sequence = [long]$i; LineNumber = [long]$i; FilePath = 'snapshot-reuse.log'; Fields = @{}
                }) | Out-Null
            }
            return [pscustomobject]@{ AllEvents = [object[]]$events.ToArray(); GlobalEvents = [object[]]@(); TransferEvents = [object[]]$events.ToArray() }
        }
        function Assert-TestCommandArgument($Definition, [string]$CommandName, [string]$ParameterName, [string]$ExpectedArgument) {
            $commands = @($Definition.Body.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq $CommandName
            }, $true))
            if ($commands.Count -ne 1) { throw "production command not unique: $CommandName" }
            $elements = $commands[0].CommandElements
            for ($i = 0; $i -lt $elements.Count - 1; $i++) {
                if ($elements[$i] -is [System.Management.Automation.Language.CommandParameterAst] -and $elements[$i].ParameterName -ceq $ParameterName) {
                    if ($elements[$i + 1].Extent.Text -cne $ExpectedArgument) { throw "wrong $ParameterName handoff argument" }
                    return
                }
            }
            throw "missing $ParameterName argument on $CommandName"
        }
        $script:ArtifactAst = Read-TestAst (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
        $classifier = Find-TestFunction $script:ArtifactAst 'Get-FileTransferRecoveryFailureClassification'
        $boundary = @($classifier.Body.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -ceq '$snapshotRouteRuntimeAuthoritative'
        })
        if ($boundary.Count -ne 1) { throw 'classifier snapshot boundary missing' }
        # Execute the real selection/reuse statements, stopping before unrelated full-report proof work.
        $prefix = $classifier.Extent.Text.Substring(0, $boundary[0].Extent.StartOffset - $classifier.Extent.StartOffset)
        . ([scriptblock]::Create($prefix + "`nreturn `$coordinatorSnapshotEvaluation`n}"))
        $writer = Find-TestFunction $script:ArtifactAst 'Write-FileTransferDiagnosticsArtifacts'
        $statements = @($writer.Body.EndBlock.Statements)
        $boundary = @($statements | Where-Object {
            $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -ceq '$snapshotRouteConsistency'
        })
        if ($boundary.Count -ne 1) { throw 'writer snapshot boundary missing' }
        $prefix = $writer.Extent.Text.Substring(0, $boundary[0].Extent.StartOffset - $writer.Extent.StartOffset)
        . ([scriptblock]::Create($prefix + "`nreturn `$snapshotEvaluation`n}"))
        $gate = @($statements | Where-Object {
            $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.Text.Contains("`$snapshotEvaluation.Mode -ne 'LegacyV0'")
        })
        $metadata = @($statements | Where-Object {
            $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -ceq '$verdictLines' -and $_.Extent.Text.Contains('coordinator_snapshot_mode=')
        })
        $end = @($statements | Where-Object {
            $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.Text.Contains("-Name 'full_writer_summary_artifacts'")
        })
        $route = @($statements | Where-Object {
            $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -ceq '$routeConsistencyLines'
        })
        if ($gate.Count -ne 1 -or $metadata.Count -ne 1 -or $end.Count -ne 1 -or $route.Count -ne 1) { throw 'writer publication boundaries changed' }
        $script:WriterSnapshotGate = [scriptblock]::Create($gate[0].Extent.Text)
        $script:WriterSnapshotPublication = [scriptblock]::Create((@($statements | Where-Object {
            $_.Extent.StartOffset -ge $metadata[0].Extent.StartOffset -and $_.Extent.EndOffset -le $end[0].Extent.StartOffset
        }).Extent.Text -join "`n"))
        $script:WriterSnapshotRouteMetadata = [scriptblock]::Create($route[0].Extent.Text)
        $script:RealEvaluator = (Get-Command Get-FileTransferCoordinatorSnapshotEvaluation -CommandType Function).ScriptBlock
        $script:EvaluationCalls = New-Object System.Collections.Generic.List[object]
        function Get-FileTransferCoordinatorSnapshotEvaluation {
            param([AllowEmptyCollection()][string[]]$Lines = @())
            $script:EvaluationCalls.Add([pscustomobject]@{ Lines = [string[]]@($Lines) }) | Out-Null
            & $script:RealEvaluator -Lines $Lines
        }
        """;
}
