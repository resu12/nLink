namespace NLink.SmokeTests;

public sealed class FileTransferSnapshotCandidateVariantTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", "responder_probe_handoff")]
    [InlineData("pwsh", "responder_probe_handoff")]
    [InlineData("powershell", "route_transition")]
    [InlineData("pwsh", "route_transition")]
    [Trait("OpsSuite", "ScriptSurface")]
    public async Task SnapshotCandidates_PreserveVariantThroughCompactFailureSelection(string host, string family)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot, [string]$Family)
            Set-StrictMode -Version Latest
            $ErrorActionPreference='Stop'
            function Parse([string]$Path) {
                $tokens=$null; $errors=$null
                $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot $Path),[ref]$tokens,[ref]$errors)
                if ($errors.Count) { throw "Parse failed: $Path" }
                return $ast
            }
            function Definition($Ast, [string]$Name) {
                $nodes=@($Ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $n.Name -ceq $Name},$true))
                if ($nodes.Count -ne 1) { throw "Expected one function: $Name" }
                return $nodes[0]
            }
            $producer=Definition (Parse 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1') 'Get-FileTransferCoordinatorSnapshotCorrelationResult'
            . ([scriptblock]::Create($producer.Extent.Text))
            $body=(Definition (Parse 'tools/FileTransferOps/AnalyzerOrchestration.ps1') 'Write-FileTransferCompactRetainedAnalysisArtifacts').Body
            $proofName=if ($Family -ceq 'responder_probe_handoff') {'runtimeUnlockResponderPeerProbeHandoffProof'} else {'fileTransferFallbackV6ProofBootstrapProof'}
            $class=if ($Family -ceq 'responder_probe_handoff') {'runtime_unlock_responder_acked_probe_route_transition_handoff_missing'} else {
                'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing'
            }
            $loops=@($body.FindAll({param($n) $n -is [Management.Automation.Language.ForEachStatementAst] -and
                ($n.Condition.Extent.Text -replace '\s+','') -ceq ('@($'+$proofName+'.UnresolvedCandidateRecords)')},$true))
            if ($loops.Count -ne 1) { throw 'Expected one candidate projection' }
            function Assignments([string]$Name) {
                return @($body.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and
                    $n.Left.Extent.Text -ceq ('$'+$Name)},$true))
            }
            function Boundary([long]$Sequence, [int]$Raw, [int]$Distinct, [int]$Recovered, [int]$Unresolved) {
                return [pscustomobject]@{families=@($Family);taxonomyClass=$class;decisionReason="synthetic-$Sequence";
                    writer='synthetic-writer';writerSequence=$Sequence;evidenceLineIndex=($Sequence+100);
                    rawCount=$Raw;distinctCount=$Distinct;recoveredCount=$Recovered;unresolvedCount=$Unresolved;
                    verdict=$(if ($Unresolved) {'fail'} else {'pass'})}
            }
            $variantCounts=@{
                initial_activation_peer_acceptance_scope_rejected='InitialActivationPeerAcceptanceScopeRejected'
                ack_before_probe_adoption_recheck_missing='AckBeforeProbeAdoptionRecheckMissing'
                binding_missing='BindingMissing'
                after_bound_handoff_missing='AfterBoundHandoffMissing'
            }
            $snapshotFallbackTailYieldAuthoritative=$false; $snapshotReceiveRecoveryEvidenceSelected=$false
            # Invented, already-evaluated DTOs only: no authority evaluation or historical input.
            foreach ($mode in @('initial_activation_peer_acceptance_scope_rejected','ack_before_probe_adoption_recheck_missing',
                    'binding_missing','after_bound_handoff_missing','absent','null','empty','none')) {
                $first=Boundary 10 3 2 0 2; $later=Boundary 20 2 1 0 1; $recovered=Boundary 5 4 1 1 0
                $expected=if ($mode -cin @('absent','none')) {'(none)'} elseif ($mode -cin @('null','empty')) {''} else {$mode}
                if ($mode -cne 'absent') { $first | Add-Member variant $(if ($mode -ceq 'null') {$null} else {$expected}) }
                $later | Add-Member variant 'later-variant'; $recovered | Add-Member variant 'recovered-variant'
                $otherFamily=Boundary 1 99 99 0 99; $otherFamily.families=@('unrelated')
                $otherClass=Boundary 2 99 99 0 99; $otherClass.taxonomyClass='unrelated'
                $evaluation=[pscustomobject]@{Boundaries=@($later,$recovered,$first,$otherFamily,$otherClass)}
                $before=$evaluation | ConvertTo-Json -Depth 8 -Compress
                $proof=Get-FileTransferCoordinatorSnapshotCorrelationResult -Evaluation $evaluation -Family $Family -TaxonomyClasses @($class)
                Set-Variable -Name $proofName -Value $proof
                $compactProofChainCandidates=[Collections.Generic.List[object]]::new()
                # This is the actual writer's unconditional Variant read, not a test-side substitute.
                . ([scriptblock]::Create($loops[0].Extent.Text))
                if ($proof.RawCount -ne 9 -or $proof.DistinctCount -ne 4 -or $proof.RecoveredCount -ne 1 -or
                    $proof.UnresolvedCount -ne 3 -or $proof.Verdict -cne 'fail' -or $proof.Source -cne 'SnapshotAuthorityV1' -or
                    $proof.CandidateRecords.Count -ne 3 -or $proof.RecoveredCandidateRecords.Count -ne 1 -or
                    $proof.UnresolvedCandidateRecords.Count -ne 2 -or $compactProofChainCandidates.Count -ne 2) { throw 'Proof accounting changed' }
                if ($proof.CandidateRecords[2].Variant -cne $expected -or
                    $proof.RecoveredCandidateRecords[0].Variant -cne 'recovered-variant' -or
                    -not [object]::ReferenceEquals($proof.CandidateRecords[2].Boundary,$first) -or
                    -not [object]::ReferenceEquals($compactProofChainCandidates[1].Event,$proof.CandidateRecords[2].Event)) {
                    throw 'Variant or candidate identity changed'
                }
                foreach ($variant in $variantCounts.Keys) {
                    $prefix=$variantCounts[$variant]; $matches=$mode -ceq $variant
                    foreach ($field in @('RawCount','DistinctCount','RecoveredCount','UnresolvedCount')) {
                        $count=if (-not $matches -or $field -ceq 'RecoveredCount') {0} elseif ($field -ceq 'RawCount') {3} else {2}
                        if ($proof.($prefix+$field) -ne $count) { throw "Variant accounting changed: $prefix$field" }
                    }
                    if ($proof.($prefix+'Verdict') -cne $(if ($matches) {'fail'} else {'pass'})) { throw 'Variant verdict changed' }
                }
                $compactProofChainCandidateArray=$compactProofChainCandidates.ToArray()
                $selectors=@(Assignments 'firstCompactProofChainFailure')
                if ($selectors.Count -ne 2) { throw 'Expected ordered selection and unwrap' }
                foreach ($assignment in $selectors) { . ([scriptblock]::Create($assignment.Extent.Text)) }
                foreach ($name in @('firstProofChainFailure','firstProofChainFailureClass','firstProofChainFailureReason','failureVariant',
                        'firstProofChainFailureSequence','firstProofChainFailureSourcePath','firstProofChainFailureLineNumber')) {
                    $assignments=@(Assignments $name)
                    if ($assignments.Count -ne 1) { throw "Expected one assignment: $name" }
                    . ([scriptblock]::Create($assignments[0].Extent.Text))
                }
                $reported=if ([string]::IsNullOrWhiteSpace($expected)) {'(none)'} else {$expected}
                if ($failureVariant -cne $reported -or $firstProofChainFailureClass -cne $class -or
                    $firstProofChainFailureReason -cne 'synthetic-10' -or $firstProofChainFailureSequence -ne 10 -or
                    $firstProofChainFailureSourcePath -cne '(coordinator_snapshot)' -or $firstProofChainFailureLineNumber -ne 111) {
                    throw 'Selected failure lost variant, ordering, reason or source'
                }
                if ($before -cne ($evaluation | ConvertTo-Json -Depth 8 -Compress) -or $proof.Verdict -cne 'fail') {
                    throw 'Reporting mutated evaluation or cleared failure'
                }
                Write-Output "pass $mode"
            }
            Write-Output 'snapshot-candidate-variant-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), family], host);
        Assert.True(result.ExitCode == 0, $"{host}/{family}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("snapshot-candidate-variant-ok", result.Stdout, StringComparison.Ordinal);
    }
}
