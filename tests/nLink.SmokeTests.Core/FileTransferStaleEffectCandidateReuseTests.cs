namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedAuthorityProof")]
[Trait("RetainedAuthorityProofSuite", "StaleEffectCandidateReuse")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferStaleEffectCandidateReuseTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task StaleEffectReuse_RejectionMatchPlanPreservesExactLegacyPredicateSemantics()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            function Assert-PlanMatches($rejection, $effect) {
                $plan = New-FileTransferStaleRejectedProofMatchPlan -Rejection $rejection
                $expectedScope = Test-FileTransferStaleRejectedProofScopeMatches -Rejection $rejection -Effect $effect
                $actualScope = Test-FileTransferStaleRejectedProofPlannedScopeMatches -Plan $plan -Effect $effect
                if ($expectedScope -ne $actualScope) { throw 'planned scope match changed' }
                $expectedAuthority = Test-FileTransferStaleRejectedProofAuthorityMatches -Rejection $rejection -Effect $effect
                $actualAuthority = Test-FileTransferStaleRejectedProofPlannedAuthorityMatches -Plan $plan -Effect $effect
                if ($expectedAuthority -ne $actualAuthority) { throw 'planned authority match changed' }
            }

            $rejection = Reject 1 'RepairReplay'
            $rejection.Fields.attempted_runtime_generation = '01'
            $rejection.Fields.current_runtime_generation = '2'
            Assert-PlanMatches $rejection (Event 2 'filetransfer_v4_repair_scheduled' @{runtime_generation='01'})
            Assert-PlanMatches $rejection (Event 2 'filetransfer_v4_repair_scheduled' @{runtime_generation='1'})
            Assert-PlanMatches $rejection (Event 2 'filetransfer_v4_repair_scheduled' @{runtime_generation='2'})

            $rejection.Fields.attempted_transport_epoch = '3'
            $rejection.Fields.current_transport_epoch = '4'
            Assert-PlanMatches $rejection (Event 2 'filetransfer_v4_repair_scheduled' @{runtime_generation='01'; transport_epoch='4'})
            Assert-PlanMatches $rejection (Event 2 'filetransfer_v4_repair_scheduled' @{runtime_generation='01'; transport_epoch='3'})

            $callback = Event 1 'filetransfer_runtime_unlock_callback_rejected' @{transaction_generation='5'}
            Assert-PlanMatches $callback (Event 2 'runtime_unlock_probe_started' @{transaction_generation='5'})
            Assert-PlanMatches $callback (Event 2 'runtime_unlock_probe_started' @{transaction_generation='6'})
            $callback.Fields.attempted_runtime_generation = '1'
            $callback.Fields.current_runtime_generation = '2'
            Assert-PlanMatches $callback (Event 2 'runtime_unlock_probe_started' @{runtime_generation='1'; transaction_generation='6'})

            $topLevel = Reject 1 'RepairReplay'
            $topLevel | Add-Member -NotePropertyName TransferId -NotePropertyValue 'top-level'
            Assert-PlanMatches $topLevel (Event 2 'filetransfer_v4_repair_scheduled' @{transfer_id='top-level'; runtime_generation='1'})
            Assert-PlanMatches $topLevel (Event 2 'filetransfer_v4_repair_scheduled' @{transfer_id='transfer'; runtime_generation='1'})
            $topLevel.Fields.direction = ''
            Assert-PlanMatches $topLevel (Event 2 'filetransfer_v4_repair_scheduled' @{transfer_id='top-level'; direction='inbound'; runtime_generation='1'})

            $unscoped = Reject 1 'RepairReplay'
            $unscoped.Fields.transfer_id = '(none)'
            $unscoped.Fields.session_id = ' (none) '
            Assert-PlanMatches $unscoped (Event 2 'filetransfer_v4_repair_scheduled' @{runtime_generation='1'})
            """);
    }

    [Fact]
    public async Task StaleEffectReuse_StrictSequenceDuplicatesOrderingAndTruncationEqualBaseline()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $a = Reject 10 'RepairReplay'
            $b = Reject 20 'RepairReplay'
            $events = @((Event 5 'filetransfer_v4_repair_scheduled'), $a, $a,
                (Event 10 'filetransfer_v4_repair_scheduled'), (Event 11 'filetransfer_v4_repair_scheduled'),
                $b, (Event 20 'filetransfer_v4_repair_scheduled'), (Event 21 'filetransfer_v4_repair_scheduled'))
            $proof = Compare-Proofs $events
            if ($proof.ForbiddenEffectCount -ne 7 -or $proof.Verdict -ne 'fail') { throw 'strict sequence or duplicate pair count changed' }
            $effects = @(30..45 | ForEach-Object { Event $_ 'filetransfer_v4_repair_scheduled' })
            $events += $effects + @($effects[0])
            [array]::Reverse($events)
            $proof = Compare-Proofs $events
            if ($proof.EvidenceEvents.Count -ne 20 -or $proof.FailureEvents.Count -ne 20) { throw 'evidence truncation changed' }
            if ($proof.ForbiddenEffectCount -ne 58) { throw 'duplicate effects were coalesced' }
            """);
    }

    [Fact]
    public async Task StaleEffectReuse_InterleavedFamiliesAndSubfamiliesKeepExactPredicate()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $cases = @(
                @('LivenessDeferral', 'session_liveness_timeout_deferred_for_current_filetransfer_recovery'),
                @('RepairReplay', 'filetransfer_v4_repair_scheduled'),
                @('ProgressMutation', 'filetransfer_v4_state_sent'),
                @('BridgeRecovery', 'bridge_receive_stall_recovery_started'),
                @('RoutePublication', 'filetransfer_route_selected'),
                @('RuntimeUnlockTransactionCallback', 'runtime_unlock_probe_started'),
                @('ControlPlaneSend', 'filetransfer_control_plane_delivery_observed'),
                @('FallbackControlPlanePressure', 'filetransfer_post_tuna_fallback_control_plane_pressure_marked'),
                @('FallbackCheckpointBegin', 'filetransfer_fallback_checkpoint_accepted'),
                @('FallbackCheckpointReissue', 'filetransfer_fallback_checkpoint_reissue_requested'),
                @('FallbackTailQuiescent', 'filetransfer_fallback_tail_quiescent_yield_granted'))
            $all = @()
            foreach ($case in $cases) {
                $events = @((Reject 10 $case[0]), (Reject 20 $case[0]), (Event 30 $case[1]))
                $proof = Compare-Proofs $events
                if ($proof.ForbiddenEffectCount -ne 2) { throw "lost known family/subfamily: $($case[0])" }
                $all += $events
            }
            $null = Compare-Proofs $all
            $proof = Compare-Proofs @((Reject 1 'FallbackCheckpointReissue'), (Reject 2 'FallbackCheckpointBegin'),
                (Reject 3 'FallbackCheckpointReissue'),
                (Event 4 'filetransfer_control_plane_delivery_result' @{kind='unrelated'; reason='none'}),
                (Event 5 'filetransfer_control_plane_delivery_result' @{kind='frontier_request'}),
                (Event 6 'filetransfer_control_plane_delivery_result' @{reason='checkpoint_request_retry'}),
                (Event 7 'filetransfer_fallback_checkpoint_accepted'))
            if ($proof.FallbackReissueForbiddenEffectCount -ne 4 -or $proof.FallbackCheckpointForbiddenEffectCount -ne 1) { throw 'subfamily or kind/reason predicate changed' }
            """);
    }

    [Fact]
    public async Task StaleEffectReuse_EmptyBucketsUnknownActionsAndIndependentInputsDoNotLeak()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $null = Compare-Proofs @()
            $null = Compare-Proofs @((Event 1 'unrelated'))
            $proof = Compare-Proofs @((Reject 1 'UnknownAction'), (Event 2 'filetransfer_v4_repair_scheduled'))
            if ($proof.UnknownActionCount -ne 1 -or $script:ActualVisits -ne 0) { throw 'unknown action was exempted or indexed' }
            $events = @((Reject 1 'RepairReplay'), (Reject 2 'RepairReplay'), (Event 3 'unrelated'))
            $proof = Compare-Proofs $events
            if ($script:ActualVisits -ne 2 -or $proof.Verdict -ne 'pass') { throw 'empty bucket rebuilt' }
            $events[2].EventName = 'filetransfer_v4_repair_scheduled'
            $proof = Compare-Proofs $events
            if ($proof.ForbiddenEffectCount -ne 2) { throw 'candidate cache leaked across calls' }
            foreach ($mode in @('all', 'absent', 'empty')) { $null = Compare-Proofs $events $mode }
            $null = Compare-Proofs @((Reject 1 'RepairReplay'), (Event 2 'filetransfer_v4_repair_scheduled'))
            if ($script:ActualVisits -ne 1) { throw 'single rejection adds predicate calls' }
            """);
    }

    [Fact]
    public async Task StaleEffectReuse_ScopeAuthorityAndPublicationAcceptanceRemainRejectionSpecific()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            foreach ($field in @('session_id', 'transfer_id', 'direction', 'runtime_generation')) {
                $fields = @{}; $fields[$field] = 'different'
                $proof = Compare-Proofs @((Reject 1 'RepairReplay'), (Reject 2 'RepairReplay'), (Event 3 'filetransfer_v4_repair_scheduled' $fields))
                if ($proof.ForbiddenEffectCount -ne 0) { throw "wrong scope/authority admitted: $field" }
            }
            $proof = Compare-Proofs @((Reject 1 'RepairReplay'), (Event 2 'filetransfer_v4_repair_scheduled' @{runtime_generation='2'}))
            if ($proof.ForbiddenEffectCount -ne 0) { throw 'current authority treated as stale' }
            $rejection = Event 1 'filetransfer_route_hint_ignored_missing_publication_authority'
            $accepted = Event 3 'filetransfer_route_hint_authorized_publication_accepted'
            $effect = Event 4 'filetransfer_route_selected'
            $proof = Compare-Proofs @($rejection, $accepted, $effect)
            if ($proof.ForbiddenEffectCount -ne 0) { throw 'prior authorized publication lost' }
            $proof = Compare-Proofs @($rejection, $accepted, (Event 3 'filetransfer_route_hint_ignored_missing_publication_authority'), $effect)
            if ($proof.ForbiddenEffectCount -ne 1) { throw 'acceptance borrowed across rejection sequence' }
            $accepted.Fields.session_id = 'different'
            $proof = Compare-Proofs @($rejection, $accepted, $effect)
            if ($proof.ForbiddenEffectCount -ne 1) { throw 'foreign publication acceptance admitted' }
            """);
    }

    [Fact]
    public async Task StaleEffectReuse_LocalDeliveryPeerCompletionBranchRemainsUnchanged()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $delivery = Event 1 'filetransfer_control_plane_delivery_result' @{route='post_tuna_fallback_v6'; accepted_any='1'; peer_visible_any='0'}
            $grant = Event 3 'filetransfer_runtime_unlock_admission_granted'
            $proof = Compare-Proofs @($delivery, $grant)
            if ($proof.ForbiddenEffectCount -ne 1) { throw 'local-only admission no longer fails' }
            $completion = Event 2 'filetransfer_control_plane_delivery_observed' @{route='post_tuna_fallback_v6'; peer_visible_any='1'}
            $proof = Compare-Proofs @($delivery, $completion, $grant)
            if ($proof.ForbiddenEffectCount -ne 0) { throw 'peer completion no longer closes local-only admission' }
            $completion.Fields.transfer_id = 'other'
            $proof = Compare-Proofs @($delivery, $completion, $grant)
            if ($proof.ForbiddenEffectCount -ne 1) { throw 'foreign completion suppressed failure' }
            """);
    }

    [Fact]
    public async Task StaleEffectReuse_ProductionWidthBoundsPredicateVisitsAndPreservesFailureEvidence()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $events = @(1..50000 | ForEach-Object { Event $_ 'session_liveness_heartbeat' })
            $events += @((Reject 10 'RepairReplay'), (Reject 20 'RepairReplay'), (Reject 30 'RepairReplay'), (Reject 40 'RepairReplay'))
            $proof = Compare-Proofs $events
            if ($proof.Verdict -ne 'pass' -or $script:ActualVisits -ne 49993) { throw 'production-width pass or visit bound changed' }
            $events += @(50001..50008 | ForEach-Object { Event $_ 'filetransfer_v4_repair_scheduled' })
            $proof = Compare-Proofs $events
            if ($proof.Verdict -ne 'fail' -or $proof.ForbiddenEffectCount -ne 32 -or $script:ActualVisits -ne 50001) { throw 'production-width failure or visit bound changed' }
            """);
    }

    private static async Task RunHarnessAsync(string body)
    {
        var result = await RunPowerShellScriptTextAsync(Harness + Environment.NewLine + body + "\nWrite-Output 'stale-reuse-ok'", [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal("stale-reuse-ok", result.Stdout.Trim());
    }

    private const string Harness = """
        param([string]$RepoRoot)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Windows PowerShell 5.1 required' }
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/StaleRejectedActionEffectBaseline.ps1')
        $script:Predicate = (Get-Command Test-FileTransferStaleRejectedProofForbiddenEffect).ScriptBlock
        $script:Visits = 0L
        function Test-FileTransferStaleRejectedProofForbiddenEffect {
            param([string]$Family, $Event, [string]$FallbackSubfamily='')
            $script:Visits++
            & $script:Predicate -Family $Family -Event $Event -FallbackSubfamily $FallbackSubfamily
        }
        function Event([long]$Sequence, [string]$Name, [hashtable]$Overrides=@{}) {
            $fields = @{session_id='session'; transfer_id='transfer'; direction='outbound'; runtime_generation='1'; current_runtime_generation='2'}
            foreach ($key in $Overrides.Keys) { $fields[$key]=$Overrides[$key] }
            [pscustomobject]@{Sequence=$Sequence; LineNumber=$Sequence; EventName=$Name; Fields=$fields; FileName='fixture.log'; Message=$Name; RawLine=$Name}
        }
        function Reject([long]$Sequence, [string]$Action) {
            Event $Sequence 'filetransfer_runtime_owner_stale_update_rejected' @{action_kind=$Action}
        }
        function Compare-Proofs([AllowEmptyCollection()][object[]]$Events, [string]$Mode='all') {
            $summary = [pscustomobject]@{GlobalEvents=@(); TransferEvents=$Events}
            if ($Mode -eq 'all') { $summary | Add-Member -NotePropertyName AllEvents -NotePropertyValue $Events }
            elseif ($Mode -eq 'empty') { $summary | Add-Member -NotePropertyName AllEvents -NotePropertyValue @() }
            $before = ConvertTo-Json -InputObject $summary -Depth 12 -Compress
            $script:Visits = 0L
            $expected = Invoke-StaleEffectBaseline -Summary $summary
            $baselineVisits = $script:Visits
            $script:Visits = 0L
            $actual = Get-FileTransferStaleRejectedActionEffectProof -Summary $summary
            $script:ActualVisits = $script:Visits
            if ((ConvertTo-Json -InputObject $expected -Depth 12 -Compress) -cne (ConvertTo-Json -InputObject $actual -Depth 12 -Compress)) { throw 'complete proof changed' }
            foreach ($property in @('EvidenceEvents', 'FailureEvents')) {
                for ($i=0; $i -lt $expected.$property.Count; $i++) {
                    if (-not [object]::ReferenceEquals($expected.$property[$i], $actual.$property[$i])) { throw "evidence reference/order changed: $property/$i" }
                }
            }
            if ((ConvertTo-Json -InputObject $summary -Depth 12 -Compress) -cne $before) { throw 'input mutated' }
            $bound = 0L; $seen = @{}
            foreach ($rejection in @($Events | Where-Object { $_.EventName -in @('filetransfer_runtime_owner_stale_update_rejected', 'filetransfer_route_hint_ignored_missing_publication_authority') } | Sort-Object Sequence)) {
                $family = Get-FileTransferStaleRejectedProofActionFamily -Event $rejection
                if ($family -eq 'unknown') { continue }
                $subfamily = Get-FileTransferStaleRejectedProofFallbackSubfamily -Event $rejection
                $key = $family + '|' + $subfamily
                if (-not $seen.ContainsKey($key)) {
                    $seen[$key]=$true
                    $bound += @($Events | Where-Object { $_.Sequence -gt $rejection.Sequence }).Count
                }
            }
            if ($script:ActualVisits -ne $bound -or $script:ActualVisits -gt $baselineVisits) { throw "predicate visits outside exact suffix bound: $script:ActualVisits != $bound" }
            return $actual
        }
        """;
}
