namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedAuthorityProof")]
[Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferStaleDeliveryObservationTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task DelayedObservation_ExactPriorResultIsNotANewSendInEitherDirection()
    {
        await RunAsync("""
            foreach ($direction in @('inbound', 'outbound')) {
                $script:Direction = $direction
                $result = Delivery 10 'result'
                $reject = Reject 20
                $observed = Delivery 30 'observed'
                Assert-Proof @($result, $reject, $observed) 0
                Assert-Proof @($observed, $result, $reject, $result, $observed) 0
                Assert-Proof @($result, (Delivery 15 'unrelated'), $reject, (Delivery 9000 'observed')) 0
                # Independently reparse compact input: retain canonical log bytes, not object aliases.
                $compact = @($result, $reject, $observed) | ForEach-Object {
                    ConvertFrom-FileTransferLogLine -Line $_.RawLine -FilePath 'compact.log' -LineNumber ($_.LineNumber + 100) -Sequence $_.Sequence
                }
                Assert-Proof @($compact) 0
            }
            """);
    }

    [Fact]
    public async Task DelayedObservation_MissingCompetingAndConflictingEvidenceFailsClosed()
    {
        await RunAsync("""
            $result = Delivery 10 'result'; $reject = Reject 20; $observed = Delivery 30 'observed'
            Assert-Proof @($reject, $observed) 1
            Assert-Proof @((Delivery 21 'result'), $reject, $observed) 2
            Assert-Proof @($result, (Delivery 11 'result'), $reject, $observed) 1
            Assert-Proof @($result, $reject, $observed, (Delivery 31 'observed')) 2
            # Conflicting copies remain invalid even if the copy changes the send identity or event name.
            foreach ($override in @(@{authority_id='other'}, @{event='unrelated'}, @{control_copy='0'})) {
                Assert-Proof @($result, (Delivery 10 'result' $override), $reject, $observed) 1
            }
            Assert-Proof @($result, $reject, (Reject 20 @{reason='conflict'}), $observed) 2 2
            Assert-Proof @($result, $reject, $observed, (Delivery 30 'observed' @{authority_id='other'})) 2
            """);
    }

    [Fact]
    public async Task DelayedObservation_EveryIdentityScopeResultAndOptionalFieldRemainsExact()
    {
        await RunAsync("""
            $reject = Reject 20
            foreach ($name in @('log_writer_id', 'session_id', 'transfer_id', 'direction', 'authority_schema', 'authority_id',
                'control_plane_generation', 'kind', 'frame_type', 'reason', 'route', 'protocol_version', 'live_route_epoch',
                'leg_generation', 'transport_epoch', 'bridge_recovery_generation', 'checkpoint_request_id',
                'control_queue', 'control_copy', 'bulk_copy', 'peer_visible_any', 'accepted_any', 'local_only_rejected')) {
                # Mutate the predecessor, leaving the candidate eligible for the original strict matcher.
                Assert-Proof @((Delivery 10 'result' @{$name='wrong'}), $reject, (Delivery 30 'observed')) 1
                Assert-Proof @((Delivery 10 'result' @{$name=$null}), $reject, (Delivery 30 'observed')) 1
            }
            Assert-Proof @((Delivery 10 'result' @{correlation_id='one'}), $reject, (Delivery 30 'observed')) 1
            Assert-Proof @((Delivery 10 'result' @{correlation_id='one'}), $reject, (Delivery 30 'observed' @{correlation_id='two'})) 1
            Assert-Proof @((Delivery 10 'result' @{correlation_id='one'}), $reject, (Delivery 30 'observed' @{correlation_id='one'})) 0
            foreach ($override in @(@{log_writer_attempt_utc_ticks='0'}, @{log_writer_attempt_utc_ticks='999999999999999999'},
                @{log_writer_sequence='40'}, @{log_writer_attempt_utc_ticks='639243240464000000'})) {
                Assert-Proof @((Delivery 10 'result' $override), $reject, (Delivery 30 'observed')) 1
            }
            """);
    }

    [Fact]
    public async Task DelayedObservation_DoesNotExemptActualLaterSendsOrDownstreamEffects()
    {
        await RunAsync("""
            $events = @((Delivery 10 'result'), (Reject 20), (Delivery 30 'observed'))
            Assert-Proof @($events + (Delivery 31 'result' @{authority_id='new'; control_plane_generation='79'})) 1
            Assert-Proof @($events + (Delivery 31 'observed' @{authority_id='new'; control_plane_generation='79'})) 1
            Assert-Proof @($events + (Delivery 31 'result')) 2
            Assert-Proof @($events + (Delivery 31 'observed' @{event='filetransfer_fallback_checkpoint_accepted'})) 1
            """);
    }

    private async Task RunAsync(string body)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var result = await RunPowerShellScriptTextAsync(Harness + Environment.NewLine + body, [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
    }

    private const string Harness = """
        param([string]$RepoRoot)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
        $script:Direction = 'inbound'
        function Delivery([long]$Sequence, [string]$Suffix, [hashtable]$Overrides=@{}) {
            $fields = [ordered]@{
                event="filetransfer_control_plane_delivery_$Suffix"; direction=$script:Direction; kind='receiver_state';
                transfer_id='transfer'; session_id='session'; authority_id='fta2-fallback_control_plane-exact'; authority_schema='2';
                control_plane_generation='78'; route='post_tuna_fallback_v6'; protocol_version='6'; live_route_epoch='2';
                leg_generation='3'; bridge_recovery_generation='1'; transport_epoch='1'; checkpoint_request_id='(none)';
                reason='chunk_batch_committed'; frame_type='filetransfer.receiver_state.v6'; control_queue='1'; control_copy='1';
                bulk_copy='1'; peer_visible_any='1'; accepted_any='1'; local_only_rejected='0';
                log_writer_id='helper'; log_writer_sequence=[string]$Sequence;
                log_writer_attempt_utc_ticks=[string](639243240460000000L + $Sequence * 10000L)
            }
            if ($Suffix -ceq 'result') {
                $fields.transport='nkn'; $fields.msg_id='transport-only'; $fields.control_queue_error='(none)'
                $fields.control_copy_error='(none)'; $fields.bulk_copy_error='(none)'
            }
            foreach ($name in $Overrides.Keys) {
                if ($null -eq $Overrides[$name]) { $fields.Remove($name) } else { $fields[$name]=$Overrides[$name] }
            }
            $text = '[2026-09-06 20:40:46Z] [INFO] [Fixture] ' + [string]::Join('; ', @($fields.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }))
            ConvertFrom-FileTransferLogLine -Line $text -FilePath 'full.log' -LineNumber $Sequence -Sequence $Sequence
        }
        function Reject([long]$Sequence, [hashtable]$Overrides=@{}) {
            $fields = @{event='filetransfer_runtime_owner_stale_update_rejected'; source='fallback_control_plane_send';
                attempted_action='receiver_state'; runtime_generation='3'; current_runtime_generation='4';
                current_route='file_tuna_v4'; current_protocol_version='4'; current_live_route_epoch='3'; reason='route_or_protocol_mismatch';
                authority_schema=$null; authority_id=$null; control_plane_generation=$null}
            foreach ($name in $Overrides.Keys) { $fields[$name]=$Overrides[$name] }
            Delivery $Sequence 'rejected' $fields
        }
        function Assert-Proof([object[]]$Events, [int]$Expected, [int]$Rejected=1) {
            $proof = Get-FileTransferStaleRejectedActionEffectProof -Summary ([pscustomobject]@{AllEvents=$Events})
            if ($proof.ForbiddenEffectCount -ne $Expected -or $proof.FallbackSendForbiddenEffectCount -ne $Expected -or
                $proof.RejectedActionCount -ne $Rejected -or $proof.Verdict -cne $(if ($Expected -eq 0) {'pass'} else {'fail'})) {
                throw "expected $Rejected/$Expected; got $($proof | ConvertTo-Json -Depth 3 -Compress)"
            }
        }
        """;
}
