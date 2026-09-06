namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedAuthorityProof")]
[Trait("RetainedAuthorityProofSuite", "ControlPlaneLazyExtraction")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferControlPlaneLazyExtractionTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task ControlPlaneLazy_EmptyMalformedAndCaseVariantFieldsPreserveCompleteProof()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $null = Compare-Proofs @()
            $events = @((Event 1 'unfamiliar_event'), (Event 2 'unfamiliar_event'), (Event 3 'unfamiliar_event'), (Event 4 'unfamiliar_event'))
            $events[0].PSObject.Properties.Remove('Fields')
            $events[1].Fields = $null
            $events[2].Fields = 'not-a-dictionary'
            $events[3].Fields = [pscustomobject]@{route='post_tuna_fallback_v6'}
            $events += @((Event 5 'filetransfer_control_plane_delivery_result' @{protocol_version='bad'; leg_generation='bad'; live_route_epoch='bad'}),
                (Event 6 'FILETRANSFER_CONTROL_PLANE_DELIVERY_RESULT' @{route='POST_TUNA_FALLBACK_V6'; kind='RECEIVER_STATE'; checkpoint_request_id='(none)'; direction='OUTBOUND'}))
            $proof = Compare-Proofs $events
            if ($proof.MetadataMissingCount -ne 1 -or $proof.DeliveryResultCount -ne 2) { throw 'malformed parsing or case comparison changed' }
            foreach ($direction in @('', 'none', '(none)', '  ', 'inbound')) {
                $null = Compare-Proofs @((Event 1 'filetransfer_route_selected'), (Event 2 'filetransfer_control_plane_delivery_result' @{direction=$direction}))
            }
            """);
    }

    [Fact]
    public async Task ControlPlaneLazy_DirectionalRouteLegAndEpochOverrideKeepMalformedDeliveryChecks()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $events = @(
                (Event 1 'filetransfer_route_selected' @{route='regular_nkn_v4_fast'}),
                (Event 2 'filetransfer_control_plane_delivery_result' @{protocol_version='wrong'; leg_generation='0'}),
                (Event 3 'filetransfer_route_selected' @{live_route_epoch='2'}),
                (Event 4 'filetransfer_leg_started' @{leg_generation='2'}),
                (Event 5 'filetransfer_live_route_epoch_started' @{route='regular_nkn_v4_fast'; direction='inbound'; live_route_epoch='2'}),
                (Event 6 'filetransfer_control_plane_delivery_result' @{leg_generation='2'; live_route_epoch='2'}),
                (Event 7 'filetransfer_control_plane_delivery_result' @{leg_generation='2'; live_route_epoch='2'; direction='(none)'}),
                (Event 8 'filetransfer_live_route_epoch_recovered' @{direction='inbound'; live_route_epoch='3'}),
                (Event 9 'filetransfer_fallback_leg_authority_started' @{direction='inbound'; leg_generation='9'}),
                (Event 10 'filetransfer_control_plane_delivery_result' @{leg_generation='2'; live_route_epoch='3'}),
                (Event 11 'filetransfer_control_plane_delivery_result' @{leg_generation='2'; live_route_epoch='2'}))
            $proof = Compare-Proofs $events
            if ($proof.ControlPlaneNonCurrentLegGenerationSendCount -ne 3) { throw 'directional route/leg selection or same-epoch override changed' }
            $proof = Compare-Proofs @((Event 1 'filetransfer_control_plane_delivery_result' @{protocol_version='wrong'; leg_generation='0'}))
            if ($proof.ControlPlaneNonCurrentLegGenerationSendCount -ne 0) { throw 'unknown current state inferred a stale send' }
            """);
    }

    [Fact]
    public async Task ControlPlaneLazy_UnfamiliarFallbackEventsRetainCheckpointAndBridgeState()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $events = @(
                (Event 1 'filetransfer_fallback_leg_authority_started' @{checkpoint_request_id='A'}),
                (Event 2 'unfamiliar_fallback_observation' @{checkpoint_request_id='B'; bridge_recovery_generation='1'}),
                (Event 3 'filetransfer_fallback_checkpoint_reissue_requested' @{checkpoint_request_id='C'; bridge_recovery_generation='1'}),
                (Event 4 'unfamiliar_fallback_observation' @{checkpoint_request_id='none'; bridge_recovery_generation='0'}),
                (Event 5 'filetransfer_fallback_checkpoint_reissue_requested' @{checkpoint_request_id='D'; bridge_recovery_generation='1'}),
                (Event 6 'filetransfer_fallback_tail_reconciliation_requested' @{checkpoint_request_id='E'; bridge_recovery_generation='1'}),
                (Event 7 'filetransfer_fallback_tail_reconciliation_accepted' @{checkpoint_request_id='E'; bridge_recovery_generation='1'}),
                (Event 8 'filetransfer_fallback_checkpoint_reissue_requested' @{checkpoint_request_id='F'; bridge_recovery_generation='1'}),
                (Event 9 'filetransfer_fallback_checkpoint_exchange_retired' @{retired_leg_generation='1'; current_leg_generation='2'; retired_checkpoint_request_id='F'; current_checkpoint_request_id='G'}),
                (Event 10 'filetransfer_fallback_checkpoint_reissue_requested' @{checkpoint_request_id='H'; bridge_recovery_generation='1'}),
                (Event 11 'filetransfer_control_plane_delivery_result' @{kind='receiver_state'; leg_generation='2'; checkpoint_request_id='(none)'}),
                (Event 12 'filetransfer_control_plane_delivery_result' @{kind='fallback_checkpoint_proof'; leg_generation='2'; checkpoint_request_id='H'}))
            $proof = Compare-Proofs $events
            if ($proof.CheckpointOwnerViolationCount -ne 2 -or $proof.CheckpointIdsByLegGeneration -cne '1:A|B|C|D|E|F|H;2:G|H') { throw 'generic fallback tracking, bridge regression, owner retirement or ID ordering changed' }
            $null = Compare-Proofs @((Event 1 'filetransfer_fallback_leg_authority_started' @{checkpoint_request_id='A'}),
                (Event 2 'filetransfer_fallback_leg_authority_checkpoint_accepted' @{checkpoint_request_id='(none)'}),
                (Event 3 'filetransfer_fallback_checkpoint_reissue_requested' @{checkpoint_request_id='B'}))
            """);
    }

    [Fact]
    public async Task ControlPlaneLazy_RecoveryMetadataAndTreadmillRemainUnchanged()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $events = @((Event 1 'filetransfer_route_selected'),
                (Event 2 'filetransfer_control_plane_delivery_failed'),
                (Event 3 'filetransfer_control_plane_delivery_result' @{kind='receiver_state'; checkpoint_request_id='(none)'}),
                (Event 4 'file_transfer_outbound_terminal' @{state='Completed'; error_code='(none)'}))
            $proof = Compare-Proofs $events
            if ($proof.Verdict -ne 'pass' -or $proof.RecoveredDeliveryFailureCount -ne 1) { throw 'exact terminal recovery changed' }
            $noise = @((Event 5 'filetransfer_fallback_stale_proof_ignored'), (Event 6 'filetransfer_fallback_stale_proof_replay_suppressed'),
                (Event 7 'filetransfer_post_tuna_fallback_state_refresh_deferred_discarded' @{reason='checkpoint_not_pending'}),
                (Event 8 'filetransfer_control_plane_send_retired_stale_generation'),
                (Event 9 'filetransfer_fallback_checkpoint_reissue_suppressed' @{reason='checkpoint_not_pending'}))
            $proof = Compare-Proofs @($events + $noise)
            if ($proof.StaleGenerationTreadmillScore -ne 3 -or $proof.Verdict -ne 'pass') { throw 'bounded stale cleanup changed' }
            $proof = Compare-Proofs @($events + $noise + @(10..13 | ForEach-Object { Event $_ 'filetransfer_fallback_stale_proof_ignored' }))
            if ($proof.StaleGenerationTreadmillScore -ne 7 -or $proof.Verdict -ne 'fail') { throw 'unbounded stale replay lost' }
            $proof = Compare-Proofs @($events + (Event 10 'session_liveness_timeout'))
            if ($proof.RecoveredDeliveryFailureCount -ne 0 -or $proof.LivenessTimeoutDuringValidExchangeCount -ne 1) { throw 'timeout falsely recovered' }
            foreach ($kind in @('receiver_state', 'frontier_request', 'fallback_checkpoint_request', 'fallback_checkpoint_proof')) {
                $null = Compare-Proofs @((Event 1 'filetransfer_control_plane_delivery_result' @{kind=$kind; checkpoint_request_id='(none)'}),
                    (Event 2 'filetransfer_control_plane_delivery_result' @{kind=$kind; route='regular_nkn_v4_fast'; protocol_version='4'}),
                    (Event 3 'filetransfer_control_plane_delivery_result' @{kind=$kind; peer_visible_any='0'; accepted_any='0'; local_only_rejected='1'}))
            }
            """);
    }

    [Fact]
    public async Task ControlPlaneLazy_DuplicateReferencesSequenceTiesAndShuffledInputPreserveEvidenceOrder()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $duplicate = Event 2 'filetransfer_control_plane_delivery_result' @{checkpoint_request_id='B'; kind='fallback_checkpoint_request'}
            $events = @((Event 1 'filetransfer_leg_started' @{checkpoint_request_id='A'}), $duplicate, $duplicate,
                (Event 2 'filetransfer_control_plane_delivery_result' @{checkpoint_request_id='C'; kind='fallback_checkpoint_proof'}))
            $events += @(3..25 | ForEach-Object { Event $_ 'filetransfer_control_plane_delivery_result' @{peer_visible_any='0'; accepted_any='0'} })
            $proof = Compare-Proofs $events
            if ($proof.DeliveryResultCount -ne 26 -or $proof.EvidenceEvents.Count -ne 20) { throw 'duplicates or evidence truncation changed' }
            [array]::Reverse($events)
            $null = Compare-Proofs $events
            $events = @($events | Sort-Object { ([long]$_.Sequence * 17) % 29 })
            $null = Compare-Proofs $events
            """);
    }

    [Fact]
    public async Task ControlPlaneLazy_ProductionWidthNonFallbackEventsReadOnlyRouteInTrackingTail()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $events = @(1..50000 | ForEach-Object {
                Event $_ 'irrelevant_liveness' @{route='regular_nkn_v4_fast'; kind='unused'; protocol_version='unused'; leg_generation='unused'; live_route_epoch='unused'; direction='unused'; checkpoint_request_id='unused'; bridge_recovery_generation='unused'}
            })
            $events += @((Event 50001 'filetransfer_route_selected'), (Event 50002 'filetransfer_leg_started'),
                (Event 50003 'filetransfer_control_plane_delivery_result' @{checkpoint_request_id='(none)'}))
            $proof = Compare-Proofs $events
            if ($proof.Verdict -ne 'pass' -or $script:ActualNoiseReads.Count -ne 1 -or $script:ActualNoiseReads.route -ne 50000) { throw 'irrelevant events extracted unused tail fields' }
            $withoutNoise = Get-FileTransferControlPlaneIsolationProof -Summary ([pscustomobject]@{TransferEvents=@($events | Where-Object { $_.EventName -ne 'irrelevant_liveness' })})
            Assert-Equivalent $proof $withoutNoise
            """);
    }

    private static async Task RunHarnessAsync(string body)
    {
        var result = await RunPowerShellScriptTextAsync(Harness + Environment.NewLine + body + "\nWrite-Output 'control-plane-lazy-ok'", [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal("control-plane-lazy-ok", result.Stdout.Trim());
    }

    private const string Harness = """
        param([string]$RepoRoot)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Windows PowerShell 5.1 required' }
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/ControlPlaneIsolationBaseline.ps1')
        $script:FieldReader = (Get-Command Get-FileTransferEventField).ScriptBlock
        $script:CountReads = $false
        $script:NoiseReads = @{}
        function Get-FileTransferEventField {
            param($Event, [string]$Name, [string]$Default='')
            if ($script:CountReads -and $null -ne $Event -and $Event.EventName -eq 'irrelevant_liveness') {
                if (-not $script:NoiseReads.ContainsKey($Name)) { $script:NoiseReads[$Name] = 0L }
                $script:NoiseReads[$Name]++
            }
            & $script:FieldReader -Event $Event -Name $Name -Default $Default
        }
        function Event([long]$Sequence, [string]$Name, [hashtable]$Overrides=@{}) {
            $fields = @{route='post_tuna_fallback_v6'; protocol_version='6'; leg_generation='1'; live_route_epoch='1'; direction='outbound';
                kind='receiver_state'; checkpoint_request_id='checkpoint'; bridge_recovery_generation='0'; transport_epoch='1';
                peer_visible_any='1'; accepted_any='1'; local_only_rejected='0'; control_queue='0'; error_code='(none)'}
            foreach ($key in $Overrides.Keys) { $fields[$key]=$Overrides[$key] }
            [pscustomobject]@{Sequence=$Sequence; LineNumber=$Sequence; EventName=$Name; Fields=$fields; FileName='fixture.log'; Message=$Name; RawLine=$Name}
        }
        function Assert-Equivalent($Expected, $Actual) {
            if ((ConvertTo-Json -InputObject $Expected -Depth 12 -Compress) -cne (ConvertTo-Json -InputObject $Actual -Depth 12 -Compress)) { throw 'complete proof changed' }
            for ($i=0; $i -lt $Expected.EvidenceEvents.Count; $i++) {
                if (-not [object]::ReferenceEquals($Expected.EvidenceEvents[$i], $Actual.EvidenceEvents[$i])) { throw "evidence reference/order changed: $i" }
            }
        }
        function Compare-Proofs([AllowEmptyCollection()][object[]]$Events) {
            $summary = [pscustomobject]@{TransferEvents=$Events}
            $before = ConvertTo-Json -InputObject $summary -Depth 12 -Compress
            $script:CountReads = $false
            $expected = Invoke-ControlPlaneIsolationBaseline -Summary $summary
            $script:NoiseReads = @{}; $script:CountReads = $true
            $actual = Get-FileTransferControlPlaneIsolationProof -Summary $summary
            $script:CountReads = $false; $script:ActualNoiseReads = $script:NoiseReads
            Assert-Equivalent $expected $actual
            if ((ConvertTo-Json -InputObject $summary -Depth 12 -Compress) -cne $before) { throw 'input mutated' }
            return $actual
        }
        """;
}
