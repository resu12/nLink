using NLink.Core.Logging;

namespace NLink.SmokeTests;

public sealed class SensitiveDataRedactorRepairOwnerTests
{
    private const string Profile = "snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1-source-callback-v1-repair-owner-v1";
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("filetransfer_fallback_frontier_repair_authority_granted", "")]
    [InlineData("filetransfer_fallback_frontier_repair_contract_settled", "")]
    [InlineData("filetransfer_fallback_frontier_repair_authority_granted", "-activation-retry-v1")]
    [InlineData("filetransfer_fallback_frontier_repair_contract_settled", "-activation-retry-v1")]
    [InlineData("filetransfer_fallback_frontier_repair_authority_granted", "-activation-retry-v1-receiver-pre-admission-v1")]
    [InlineData("filetransfer_fallback_frontier_repair_contract_settled", "-activation-retry-v1-receiver-pre-admission-v1")]
    [InlineData("filetransfer_fallback_frontier_repair_authority_granted", "-activation-retry-v1-receiver-pre-admission-v1-initial-data-session-v1")]
    [InlineData("filetransfer_fallback_frontier_repair_contract_settled", "-activation-retry-v1-receiver-pre-admission-v1-initial-data-session-v1")]
    public void RepairOwnerRedaction_PreservesExactProfileAndProjectedReferenceOnly(string eventName, string suffix)
    {
        const string token = "fte1-0123456789abcdef01234567";
        var profile = Profile + suffix;
        var line = $"event={eventName}; repair_owner_profile={profile}; repair_owner_coordinator_id={token}; " +
            $"repair_owner_operation_key=route_runtime|fallback_frontier_repair_owner|{token}|1|FallbackFrontierRepairOwnerEvidence; " +
            $"repair_owner_receipt_id={token}; repair_owner_receipt_fingerprint={token}; secret={Secret}";
        var redacted = SensitiveDataRedactor.Redact(line);
        Assert.Contains($"repair_owner_profile={profile};", redacted, StringComparison.Ordinal);
        Assert.Contains($"repair_owner_receipt_id={token}", redacted, StringComparison.Ordinal);
        Assert.Contains($"|{token}|1|FallbackFrontierRepairOwnerEvidence", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
        var snapshot = $"event=filetransfer_coordinator_snapshot_stream_started; snapshot_catalog_version={profile}";
        Assert.Equal(snapshot, SensitiveDataRedactor.Redact(snapshot));
    }

    [Theory]
    [InlineData("unrelated_diagnostic")]
    [InlineData("filetransfer_fallback_frontier_repair_authority_granted_suffix")]
    [InlineData("filetransfer_fallback_frontier_repair_authority_granted; event=unrelated_diagnostic")]
    public void RepairOwnerRedaction_RejectsUncatalogedAndAmbiguousEvents(string eventName)
    {
        var line = $"event={eventName}; repair_owner_profile={Profile}; repair_owner_receipt_id={Secret}";
        var redacted = SensitiveDataRedactor.Redact(line);
        Assert.DoesNotContain(Profile, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RepairOwnerRedaction_RejectsUnknownProfileAndCrossLineProtection()
    {
        var line = $"event=filetransfer_fallback_frontier_repair_authority_granted; repair_owner_profile={Profile}-unknown\n" +
            $"event=unrelated; repair_owner_profile={Profile}; repair_owner_operation_key=route_runtime|fallback_frontier_repair_owner|{Secret}|1|FallbackFrontierRepairOwnerEvidence";
        var redacted = SensitiveDataRedactor.Redact(line);
        Assert.DoesNotContain(Profile, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, redacted, StringComparison.Ordinal);
    }
}
