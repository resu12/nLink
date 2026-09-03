using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SensitiveDataRedactorTests
{
    [Fact]
    public void SensitiveDataRedactor_Preserves_Structured_Event_Names_While_Redacting_Sensitive_Values()
    {
        const string input = "event=helper_screenshare_viewer_surface_visible; secret=0123456789abcdef0123456789abcdef; message=hello world";

        var redacted = SensitiveDataRedactor.Redact(input);

        Assert.Contains("event=helper_screenshare_viewer_surface_visible;", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hello world", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveDataRedactor_Preserves_Long_Structured_Diagnostic_Keys()
    {
        const string input =
            "event=screenshare_helper_frame_loss_summary; recovery_progress_corridor_success_count=4; recovery_progress_corridor_applied_count=23; evidence_token=0123456789abcdef0123456789abcdef";

        var redacted = SensitiveDataRedactor.Redact(input);

        Assert.Contains("event=screenshare_helper_frame_loss_summary;", redacted, StringComparison.Ordinal);
        Assert.Contains("recovery_progress_corridor_success_count=4", redacted, StringComparison.Ordinal);
        Assert.Contains("recovery_progress_corridor_applied_count=23", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveDataRedactor_Preserves_Long_Colon_Diagnostic_Keys()
    {
        const string input =
            "tuna_last_session_completed_from_summary: yes\r\n" +
            "tuna_last_session_run_id_hash: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var redacted = SensitiveDataRedactor.Redact(input);

        Assert.Contains("tuna_last_session_completed_from_summary: yes", redacted, StringComparison.Ordinal);
        Assert.Contains("tuna_last_session_run_id_hash: [redacted]", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file_transfer_bulk_frame_received")]
    [InlineData("file_transfer_v6_state_frame_received")]
    [InlineData("file_transfer_v6_frontier_request_frame_received")]
    public void SensitiveDataRedactor_Preserves_Cataloged_FallbackBootstrap_ProofKinds(
        string proofKind)
    {
        var input =
            "event=filetransfer_fallback_v6_proof_bootstrap_peer_first_wake_decided; " +
            $"proof_kind={proofKind}; secret=0123456789abcdef0123456789abcdef";

        var redacted = SensitiveDataRedactor.Redact(input);

        Assert.Contains($"proof_kind={proofKind};", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "0123456789abcdef0123456789abcdef",
            redacted,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveDataRedactor_DoesNotProtect_UncatalogedBootstrapProofKind()
    {
        const string proofKind =
            "file_transfer_v6_frontier_request_frame_received_secret_1234567890";
        var redacted = SensitiveDataRedactor.Redact(
            "event=filetransfer_fallback_v6_proof_bootstrap_observed; " +
            $"proof_kind={proofKind}");

        Assert.DoesNotContain(proofKind, redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
    }
}
