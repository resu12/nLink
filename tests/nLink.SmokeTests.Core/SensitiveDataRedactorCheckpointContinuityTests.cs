using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SensitiveDataRedactorCheckpointContinuityTests
{
    private const string Profile = "snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1";
    private const string Token = "fte1-0123456789abcdef01234567";
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("filetransfer_v6_frontier_request_deferred")]
    [InlineData("filetransfer_v6_frontier_stall_grace_recheck_decided")]
    [InlineData("filetransfer_fallback_frontier_repair_authority_granted")]
    [InlineData("filetransfer_control_plane_delivery_observed")]
    [InlineData("filetransfer_fallback_frontier_repair_dispatched")]
    [InlineData("filetransfer_v6_frontier_request_sent")]
    [InlineData("filetransfer_fallback_frontier_repair_contract_settled")]
    [InlineData("filetransfer_fallback_frontier_repair_progress_observed")]
    [InlineData("filetransfer_v6_fallback_frontier_repair_progress_state_forced")]
    [InlineData("filetransfer_v6_receiver_state_sent")]
    public void CheckpointContinuity_SelectedEventsPreserveOnlyProjectedEvidence(string eventName)
    {
        var fields = new[] { "continuity_coordinator_id", "continuity_source_fingerprint", "continuity_acceptance_id",
            "continuity_origin_receipt_id", "continuity_origin_receipt_fingerprint", "continuity_receipt_id",
            "continuity_receipt_fingerprint", "continuity_current_scope_fingerprint", "continuity_repair_authority_id",
            "continuity_request_id", "continuity_correlation_id" };
        var input = $"event={eventName}; continuity_profile={Profile}; " +
            string.Join("; ", fields.Select(field => $"{field}={Token}")) +
            $"; continuity_accepted_state_generation=27; continuity_repair_generation=3; secret={Secret}; session_id={Secret}";
        var redacted = SensitiveDataRedactor.Redact(input);
        Assert.Contains($"continuity_profile={Profile};", redacted, StringComparison.Ordinal);
        foreach (var field in fields) { Assert.Contains($"{field}={Token}", redacted, StringComparison.Ordinal); }
        Assert.Contains("continuity_accepted_state_generation=27", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unrelated_diagnostic")]
    [InlineData("filetransfer_v6_frontier_request_deferred_suffix")]
    [InlineData("filetransfer_v6_frontier_request_deferred; event=unrelated_diagnostic")]
    public void CheckpointContinuity_UncatalogedOrAmbiguousEventsDoNotProtectProfile(string eventName)
    {
        var redacted = SensitiveDataRedactor.Redact($"event={eventName}; continuity_profile={Profile}; continuity_receipt_id={Secret}");
        Assert.DoesNotContain(Profile, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckpointContinuity_ProtectionCannotCrossLineBoundariesOrPermitRawCompositeKeys()
    {
        var input = $"event=filetransfer_v6_frontier_request_deferred; continuity_profile={Profile}; continuity_source_fingerprint={Token}\n" +
            $"event=unrelated_diagnostic; continuity_profile={Profile}; continuity_receipt_id=fte1-{Secret}; operation_key=route_runtime|checkpoint_acceptance_retained|{Secret}|27|FallbackCheckpointRepairScopeContinuity";
        var lines = SensitiveDataRedactor.Redact(input).Split('\n');
        Assert.Contains(Profile, lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(Profile, lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void CheckpointContinuity_ExactProfileAndOmittedPayloadPreserveExistingSnapshotEvidence()
    {
        foreach (var profile in new[] { "snapshot-authority-v1", "snapshot-authority-v1-receiver-barrier-v1", Profile })
        {
            var line = $"event=filetransfer_coordinator_snapshot_stream_started; snapshot_catalog_version={profile}; coordinator_instance_id={Token}; initial_state_generation=0";
            Assert.Equal(line, SensitiveDataRedactor.Redact(line));
        }
        var malformed = SensitiveDataRedactor.Redact($"event=filetransfer_v6_frontier_request_deferred; continuity_profile={Profile}-raw-{Secret}; continuity_receipt_id=fte1-{Secret}");
        Assert.DoesNotContain(Secret, malformed, StringComparison.Ordinal);
        Assert.DoesNotContain($"continuity_profile={Profile}-raw-", malformed, StringComparison.Ordinal);
    }
}
