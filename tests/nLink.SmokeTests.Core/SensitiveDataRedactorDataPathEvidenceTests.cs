using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SensitiveDataRedactorDataPathEvidenceTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string EnvelopeFingerprint = "ftenv1-" + Digest;
    private const string ScopeId = "fte1-0123456789abcdef01234567";

    [Theory]
    [InlineData("filetransfer_data_path_send_result")]
    [InlineData("filetransfer_data_path_receive_result")]
    [InlineData("filetransfer_tuna_data_path_peer_visible")]
    [InlineData("filetransfer_tuna_data_path_mismatch")]
    [InlineData("filetransfer_data_frame_ignored")]
    public void DataPathEnvelopeEvidence_SelectedEventsPreserveOnlyProjectedIdentities(string eventName)
    {
        var input = $"event={eventName}; data_path_envelope_fingerprint={EnvelopeFingerprint}; " +
            $"data_path_session_id={ScopeId}; data_path_transfer_id={ScopeId}; " +
            "receive_disposition=rejected_before_dispatch; authority_effect=none; " +
            $"session_id={Digest}; transfer_id={Digest}; message_id={Digest}; payload_fingerprint={Digest}; secret={Digest}";

        var redacted = SensitiveDataRedactor.Redact(input);

        Assert.Contains($"data_path_envelope_fingerprint={EnvelopeFingerprint};", redacted, StringComparison.Ordinal);
        Assert.Contains($"data_path_session_id={ScopeId};", redacted, StringComparison.Ordinal);
        Assert.Contains($"data_path_transfer_id={ScopeId};", redacted, StringComparison.Ordinal);
        Assert.Contains("receive_disposition=rejected_before_dispatch; authority_effect=none;", redacted, StringComparison.Ordinal);
        foreach (var field in new[] { "session_id", "transfer_id", "message_id", "payload_fingerprint" })
        {
            Assert.Contains($"; {field}=[redacted]", redacted, StringComparison.Ordinal);
        }
        Assert.DoesNotContain($"secret={Digest}", redacted, StringComparison.Ordinal);
        Assert.Equal(redacted, SensitiveDataRedactor.Redact(redacted));
    }

    [Theory]
    [InlineData("unrelated_diagnostic")]
    [InlineData("filetransfer_data_path_receive_result_suffix")]
    [InlineData("filetransfer_data_path_receive_result; event=unrelated_diagnostic")]
    public void DataPathEnvelopeEvidence_UncatalogedOrAmbiguousEventsDoNotProtectFingerprint(string eventName)
    {
        var redacted = SensitiveDataRedactor.Redact(
            $"event={eventName}; data_path_envelope_fingerprint={EnvelopeFingerprint}");

        Assert.DoesNotContain(EnvelopeFingerprint, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Digest, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ftenv1-", "")]
    [InlineData("ftenv1-", "0")]
    [InlineData("ftenv2-", "")]
    [InlineData("ftenv1-", "-raw")]
    public void DataPathEnvelopeEvidence_MalformedOrUnknownValuesAreNotProtected(string prefix, string suffix)
    {
        var digest = prefix == "ftenv1-" && suffix.Length == 0 ? Digest[..63] : Digest;
        var value = prefix + digest + suffix;
        var redacted = SensitiveDataRedactor.Redact(
            $"event=filetransfer_data_path_receive_result; data_path_envelope_fingerprint={value}; " +
            $"data_path_session_id=fte1-{Digest}; arbitrary_fingerprint={EnvelopeFingerprint}");

        Assert.DoesNotContain(value, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Digest, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(EnvelopeFingerprint, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void DataPathEnvelopeEvidence_ProtectionDoesNotCrossLinesOrInventMissingIdentity()
    {
        var input = $"event=filetransfer_data_path_receive_result; data_path_envelope_fingerprint={EnvelopeFingerprint}\r\n" +
            $"event=unrelated_diagnostic; data_path_envelope_fingerprint={EnvelopeFingerprint}\r\n" +
            "event=filetransfer_data_frame_ignored; data_path_envelope_fingerprint=(none); data_path_session_id=(none); data_path_transfer_id=(none)";

        var lines = SensitiveDataRedactor.Redact(input).Split("\r\n", StringSplitOptions.None);

        Assert.Contains(EnvelopeFingerprint, lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(EnvelopeFingerprint, lines[1], StringComparison.Ordinal);
        Assert.EndsWith("data_path_envelope_fingerprint=(none); data_path_session_id=(none); data_path_transfer_id=(none)", lines[2], StringComparison.Ordinal);
    }
}
