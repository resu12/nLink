namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedAuthorityProof")]
[Trait("RetainedAuthorityProofSuite", "SnapshotLocalYieldCandidate")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferSnapshotLocalYieldCandidateTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task SnapshotLocalYieldCandidates_EmptyAndIneligibleInputsDoNotBuildCandidates()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            foreach ($events in @(@(), @((New-Event 'unrelated' 35)))) {
                $scenario = New-Scenario
                $scenario.Events = $events
                Assert-Quartet (Compare-Proofs $scenario 'empty-or-singleton') 0 0 0 0
            }
            foreach ($kind in @('no-start', 'incomplete', 'legacy', 'failed', 'wrong-family')) {
                $scenario = New-Scenario
                switch ($kind) {
                    'no-start' { $scenario.Evaluation.__FallbackTailPeerProofBindings[0].AfterState = 'None' }
                    'incomplete' { $scenario.Evaluation.StreamComplete = $false }
                    'legacy' { $scenario.Evaluation.Mode = 'LegacyV0' }
                    'failed' { $scenario.Evaluation.ArtifactEvidenceVerdict = 'fail' }
                    'wrong-family' { $scenario.Evaluation.AuthoritativeFamilies = @('route_runtime') }
                }
                $proof = Compare-Proofs $scenario $kind
                Assert-Quartet $proof 0 0 0 0
                if ([long]$script:ActualVisits['sequence'] -ne 0) { throw "$kind built a candidate view without an eligible start" }
            }
            """);
    }

    [Fact]
    public async Task SnapshotLocalYieldCandidates_IndependentWritersAndInputModesKeepCompleteProofAndReferences()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            foreach ($recovered in @($false, $true)) {
                $scenario = New-Scenario 'writer' $recovered
                $compact = Compare-Proofs $scenario 'compact'
                $scenario.Events += New-Event 'unrelated' 35
                [array]::Reverse($scenario.Events)
                $full = Compare-Proofs $scenario 'full-shuffled'
                if (($full | ConvertTo-Json -Depth 32 -Compress) -cne ($compact | ConvertTo-Json -Depth 32 -Compress)) { throw 'full/compact proof metadata differs' }
                Assert-Quartet $full 4 1 ([int]$recovered) ([int](-not $recovered))
            }
            $first = New-Scenario 'writer-a' $true
            $second = New-Scenario 'writer-b' $false
            $third = New-Scenario 'WRITER-A' $false
            $first.Evaluation.Boundaries += @($second.Evaluation.Boundaries) + @($third.Evaluation.Boundaries)
            $first.Evaluation.__FallbackTailPeerProofBindings += @($second.Evaluation.__FallbackTailPeerProofBindings) + @($third.Evaluation.__FallbackTailPeerProofBindings)
            $first.Events += $second.Events + $third.Events
            [array]::Reverse($first.Events)
            Assert-Quartet (Compare-Proofs $first 'independent-case-sensitive-writers') 12 3 1 2
            """);
    }

    [Fact]
    public async Task SnapshotLocalYieldCandidates_AllPoisonMutationBranchesAndMixedCaseRemainExact()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $explicitPoison = @('filetransfer_runtime_terminalized', 'filetransfer_terminalized_by_peer_down',
                'filetransfer_terminalized_by_peer_session_end', 'filetransfer_terminalized_by_peer_disconnect',
                'filetransfer_terminalized_by_session_end', 'filetransfer_session_replaced',
                'filetransfer_data_session_recreated', 'filetransfer_data_session_removed',
                'file_transfer_inbound_terminal', 'file_transfer_outbound_terminal', 'transfer_terminal')
            $cases = @($explicitPoison | ForEach-Object { @{Name=$_; Fields=@{}} })
            $cases += @(
                @{Name='filetransfer_fallback_tail_peer_proof_pending_decided'; Fields=@{decision_status='Accepted'; decision_reason='fallback_tail_initial_peer_proof_receipt_superseded_after_checkpoint_advance'; checkpoint_generation='2'; peer_direction='inbound'}},
                @{Name='filetransfer_fallback_leg_transport_epoch_adopted'; Fields=@{previous_transport_epoch='1'; transport_epoch='2'}},
                @{Name='filetransfer_fallback_transport_epoch_adopted'; Fields=@{previous_transport_epoch='1'; transport_epoch='2'}},
                @{Name='filetransfer_leg_started'; Fields=@{leg_generation='4'}},
                @{Name='filetransfer_fallback_leg_authority_started'; Fields=@{leg_generation='4'}},
                @{Name='filetransfer_live_route_epoch_started'; Fields=@{previous_route='post_tuna_fallback_v6'; route='file_tuna_v4'; protocol_version='4'; live_route_epoch='3'}},
                @{Name='filetransfer_live_route_epoch_terminal'; Fields=@{}},
                @{Name='filetransfer_fallback_leg_authority_completed'; Fields=@{}},
                @{Name='filetransfer_fallback_leg_authority_superseded_by_route_hint'; Fields=@{}},
                @{Name='filetransfer_leg_frozen'; Fields=@{}},
                @{Name='filetransfer_v6_epoch_terminal'; Fields=@{}},
                @{Name='filetransfer_runtime_owner_workers_retired'; Fields=@{retired_route='post_tuna_fallback_v6'; retired_runtime_generation='0'}})
            foreach ($case in $cases) {
                foreach ($name in @($case.Name, $case.Name.ToUpperInvariant())) {
                    foreach ($position in @(25L,45L)) {
                        $scenario = New-Scenario
                        foreach ($event in $scenario.Events) { $event.Fields['checkpoint_generation']='1' }
                        $poison = Add-Poison $scenario $name $position $case.Fields
                        $applies = (($name -in $explicitPoison) -and
                            (Test-FileTransferFallbackTailLocalYieldPoisonScopeMatch -Pending $scenario.Events[0] -Event $poison)) -or
                            (Test-FileTransferFallbackTailLocalYieldScopeMutation -Pending $scenario.Events[0] -Event $poison)
                        if ($name -ceq $case.Name -and -not $applies) { throw "mutation fixture did not exercise its branch: $name" }
                        $proof = Compare-Proofs $scenario "$name/$position"
                        if ($applies -and $position -eq 25) { Assert-Quartet $proof 0 0 0 0 }
                        else { Assert-Quartet $proof 4 1 ([int](-not $applies)) ([int]$applies) }
                    }
                }
            }
            # An ABA restoration does not erase the first scope-changing event.
            $scenario = New-Scenario
            $null = Add-Poison $scenario 'filetransfer_fallback_transport_epoch_adopted' 45 @{previous_transport_epoch='1';transport_epoch='2'}
            $null = Add-Poison $scenario 'filetransfer_fallback_transport_epoch_adopted' 46 @{previous_transport_epoch='2';transport_epoch='1'}
            Assert-Quartet (Compare-Proofs $scenario 'transport-aba') 4 1 0 1
            """);
    }

    [Fact]
    public async Task SnapshotLocalYieldCandidates_DirectionlessPoisonAndStrictRawIdentityAreNotKeyedAway()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            foreach ($name in @('transfer_terminal','file_transfer_outbound_terminal','file_transfer_inbound_terminal','FILE_TRANSFER_INBOUND_TERMINAL')) {
                $scenario = New-Scenario
                $poison = Add-Poison $scenario $name 45 @{direction=''}
                $applies = $name -cne 'file_transfer_inbound_terminal'
                Assert-Quartet (Compare-Proofs $scenario $name) 4 1 ([int](-not $applies)) ([int]$applies)
            }
            foreach ($field in @('log_writer_id','session_id','transfer_id','direction')) {
                $scenario = New-Scenario
                $poison = Add-Poison $scenario 'transfer_terminal' 45
                $poison.Fields[$field] = 'foreign'
                Assert-Quartet (Compare-Proofs $scenario "foreign-poison-$field") 4 1 1 0
            }
            foreach ($field in @('session_id','transfer_id','owner_key','pair_key','raw_pair_key','canonical_pair_key')) {
                foreach ($value in @('redacted','(none)','evidence:wrong-token')) {
                    $scenario = New-Scenario
                    $scenario.Events[0].Fields[$field] = $value
                    Assert-Quartet (Compare-Proofs $scenario "raw-identity-$field/$value") 0 0 0 0
                }
            }
            $scenario = New-Scenario
            foreach ($event in $scenario.Events) {
                $event.Fields.session_id = 'sess_[redacted]'
                $event.Fields.transfer_id = '[redacted]'
            }
            $scenario.Events[0].Fields.owner_key = 'sess_[redacted]|[redacted]|post_tuna_fallback_v6|6|2|3|1|1|(none)'
            foreach ($field in @('pair_key','raw_pair_key','canonical_pair_key')) {
                $scenario.Events[0].Fields[$field] = 'sess_[redacted]|[redacted]|post_tuna_fallback_v6|6|2|1|(none)'
            }
            Assert-Quartet (Compare-Proofs $scenario 'frozen-operational-redaction') 4 1 1 0
            $competing = Copy-Value $scenario.Evaluation.__FallbackTailPeerProofBindings[0]
            $competing.TransitionId = 'redacted-competing'; $competing.OperationKey = 'redacted-competing-key'
            $scenario.Evaluation.__FallbackTailPeerProofBindings += $competing
            Assert-Quartet (Compare-Proofs $scenario 'redaction-still-requires-unique-owner') 0 0 0 0
            foreach ($name in @('FILETRANSFER_FALLBACK_TAIL_YIELD_PROOF_RECEIVED','FILETRANSFER_FALLBACK_TAIL_LOCAL_YIELD_BLOCKED')) {
                $scenario = New-Scenario
                foreach ($event in $scenario.Events) {
                    if ($event.EventName -ieq $name) { $event.EventName = $name }
                }
                Assert-Quartet (Compare-Proofs $scenario "case-sensitive-$name") 0 0 0 0
            }
            """);
    }

    [Fact]
    public async Task SnapshotLocalYieldCandidates_DuplicatesAmbiguousOwnersAndRetirementsRemainFailClosed()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $scenario = New-Scenario
            $scenario.Events += $scenario.Events[0]
            Assert-Quartet (Compare-Proofs $scenario 'duplicate-receipt-reference') 0 0 0 0
            $scenario = New-Scenario
            $scenario.Events += Copy-Event $scenario.Events[0]
            Assert-Quartet (Compare-Proofs $scenario 'duplicate-receipt-object') 0 0 0 0
            $scenario = New-Scenario
            $scenario.Events += $scenario.Events[1]
            Assert-Quartet (Compare-Proofs $scenario 'duplicate-blocked-reference') 5 1 0 1
            $scenario = New-Scenario
            $competing = Copy-Value $scenario.Evaluation.__FallbackTailPeerProofBindings[0]
            $competing.TransitionId = 'competing-start'; $competing.OperationKey = 'competing-key'
            $scenario.Evaluation.__FallbackTailPeerProofBindings += $competing
            Assert-Quartet (Compare-Proofs $scenario 'ambiguous-receipt-owner') 0 0 0 0
            $scenario = New-Scenario
            $competing = Copy-Value $scenario.Evaluation.__FallbackTailPeerProofBindings[1]
            $competing.TransitionId = 'competing-recovery'; $competing.TransitionSequence = 61L
            $scenario.Evaluation.__FallbackTailPeerProofBindings += $competing
            Assert-Quartet (Compare-Proofs $scenario 'ambiguous-recovery') 4 1 0 1
            $scenario = New-Scenario
            $competing = Copy-Value $scenario.Evaluation.Boundaries[1]
            $competing.boundaryId = 'competing-retirement'; $competing.writerSequence = 51L
            $scenario.Evaluation.Boundaries += $competing
            Assert-Quartet (Compare-Proofs $scenario 'multiple-retirements') 4 1 0 1
            # The first retirement still bounds receipt eligibility even when recovery is ambiguous.
            $scenario.Events[0].Fields.log_writer_sequence = '50'
            Assert-Quartet (Compare-Proofs $scenario 'ambiguous-retirement-first-bound') 0 0 0 0
            $scenario = New-Scenario 'writer' $false
            Assert-Quartet (Compare-Proofs $scenario 'missing-retirement') 4 1 0 1
            """);
    }

    [Fact]
    public async Task SnapshotLocalYieldCandidates_StrictIntervalsPhysicalOrderingAndMissingSequencesRemainExact()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            foreach ($sequence in @('10','20','50','0','-1','bad','')) {
                $scenario = New-Scenario
                $scenario.Events[0].Fields.log_writer_sequence = $sequence
                Assert-Quartet (Compare-Proofs $scenario "receipt-sequence-$sequence") 0 0 0 0
            }
            foreach ($sequence in @('30','50','0','-1','bad','')) {
                $scenario = New-Scenario
                foreach ($event in @($scenario.Events | Select-Object -Skip 1)) { $event.Fields.log_writer_sequence = $sequence }
                Assert-Quartet (Compare-Proofs $scenario "blocked-sequence-$sequence") 0 0 0 0
            }
            foreach ($position in @(10L,30L,60L)) {
                $scenario = New-Scenario
                $null = Add-Poison $scenario 'transfer_terminal' $position
                Assert-Quartet (Compare-Proofs $scenario "poison-endpoint-$position") 4 1 1 0
            }
            $scenario = New-Scenario 'writer' $false
            $scenario.Events[0].Sequence = 90L
            $scenario.Events[1].Sequence = 80L; $scenario.Events[1].LineNumber = 8L
            $scenario.Events[2].Sequence = 80L; $scenario.Events[2].LineNumber = 7L
            $scenario.Events[3].Sequence = 70L
            $scenario.Events[4].Sequence = 60L
            [array]::Reverse($scenario.Events)
            $proof = Compare-Proofs $scenario 'global-order-differs-from-writer-order'
            Assert-Quartet $proof 4 1 0 1
            if (-not [object]::ReferenceEquals($proof.UnresolvedCandidateRecords[0].Event,$scenario.Events[0])) { throw 'first physical blocked evidence changed' }
            $scenario = New-Scenario
            $null = Add-Poison $scenario 'transfer_terminal' 42
            $second = Add-Poison $scenario 'filetransfer_leg_frozen' 42
            $second.Sequence = 1L
            $proof = Compare-Proofs $scenario 'equal-writer-poison-sequences'
            Assert-Quartet $proof 2 1 0 1
            """);
    }

    [Fact]
    public async Task SnapshotLocalYieldCandidates_ProductionWidthAddsNoPerStartMatchingWork()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $scenario = New-Scenario 'writer-1' $false
            foreach ($writer in @('writer-2','writer-3')) {
                $other = New-Scenario $writer $false
                $scenario.Events += $other.Events
                $scenario.Evaluation.Boundaries += $other.Evaluation.Boundaries
                $scenario.Evaluation.__FallbackTailPeerProofBindings += $other.Evaluation.__FallbackTailPeerProofBindings
            }
            $small = Compare-Proofs $scenario 'three-writers'
            $smallVisits = $script:ActualVisits.Clone()
            $noise = [Collections.Generic.List[object]]::new()
            for ($i=0; $i -lt 50000; $i++) {
                # Retain in-interval noise as well as a production-width prefix.
                # The frozen oracle still visits every row for every start, but
                # need not perform its expensive scope join on all 50,000 rows.
                $writerSequence = if ($i -lt 1000) { '41' } else { '1' }
                $noise.Add([pscustomobject]@{EventName='unrelated';Sequence=(100L+$i);LineNumber=(100L+$i);
                    Fields=@{log_writer_id='writer-1';log_writer_sequence=$writerSequence;session_id='session-raw';transfer_id='transfer-raw';direction='outbound'}})
            }
            $scenario.Events += $noise.ToArray()
            $wide = Compare-Proofs $scenario '50000-unrelated'
            Assert-Quartet $wide 12 3 0 3
            if (($small | ConvertTo-Json -Depth 32 -Compress) -cne ($wide | ConvertTo-Json -Depth 32 -Compress)) { throw 'noise changed complete proof' }
            if ([long]$script:ActualVisits['unrelated'] -ne 0 -or [long]$script:BaselineVisits['unrelated'] -lt 50000) { throw 'unrelated events entered a per-start predicate or writer-sequence scan' }
            foreach ($kind in @('receipt','mutation','poison','blocked','sequence')) {
                if ([long]$script:ActualVisits[$kind] -ne [long]$smallVisits[$kind]) { throw "unrelated input increased $kind matching work" }
            }
            """);
    }

    [Fact]
    public async Task SnapshotLocalYieldCandidates_CandidateNamesCoverEveryExistingPredicateEventLiteral()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $reducer = (Get-Command Get-FileTransferFallbackTailSnapshotLocalYieldMissingCorrelation).ScriptBlock.Ast
            $admission = @($reducer.FindAll({param($node)
                $node -is [Management.Automation.Language.ForEachStatementAst] -and
                    $node.Body.Extent.Text -match '\$candidateEventNames\.Add\(\$name\)'
            },$true))
            if ($admission.Count -ne 1) { throw 'candidate-name admission table must be uniquely identified' }
            $candidateNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($literal in @($admission[0].Condition.FindAll({param($node)
                $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
                    $node.Value -match '^(filetransfer_|file_transfer_|transfer_terminal$)'
            },$true))) { [void]$candidateNames.Add($literal.Value) }
            foreach ($predicate in @($script:MutationPredicate,$script:PoisonPredicate)) {
                foreach ($literal in @($predicate.Ast.FindAll({param($node)
                    $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
                        $node.Value -match '^(filetransfer_|file_transfer_|transfer_terminal$)'
                },$true))) {
                    if (-not $candidateNames.Contains($literal.Value)) { throw "candidate admission is missing mutation/poison event $($literal.Value)" }
                }
            }
            if ($reducer.Extent.Text -notmatch 'OrdinalIgnoreCase') { throw 'candidate name admission narrowed existing case-insensitive branches' }
            """);
    }

    private static async Task RunHarnessAsync(string body)
    {
        const string setup = """
            param([string]$RepoRoot)
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/SnapshotLocalYieldCandidateHarness.ps1')
            """;
        var result = await RunPowerShellScriptTextAsync(setup + Environment.NewLine + body + "\nWrite-Output 'local-yield-candidates-ok'", [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal("local-yield-candidates-ok", result.Stdout.Trim());
    }
}
