using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed class SensitiveDataRedactorContentEvidenceTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [Trait("FileTransferInfraSuite", "ContentEvidenceCapture")]
    public void ContentEvidence_ExactCanonicalSuffixSurvivesWithoutProtectingSecrets(int count, bool retired)
    {
        using var scope = new FileTransferContentEvidenceScope("session", "transfer");
        Assert.True(scope.TryCapture(FileTransferDataFrameCodec.SerializeLegacyV4(new FileTransferChunkBatchFrameV4
        {
            SessionId = "session", TransferId = "transfer", StartChunkIndex = 12, ChunkCount = count,
            DataSegments = Enumerable.Range(0, count).Select(_ => new byte[] { 17, 34 }).ToArray(),
        }), out var evidence));
        if (retired) { scope.Dispose(); }
        var suffix = evidence.FormatLogSuffix();
        var secret = new string('a', 64);
        var input = $"event=filetransfer_data_path_send_result; sharedKey={secret}; source={secret}; arbitrary_content=ftcc1-{secret}; arbitrary_content_scope_id=ftcs1-{secret[..32]}; arbitrary_content_segment_0_commitment=ftcc1-{secret}" + suffix;
        var output = SensitiveDataRedactor.Redact(input);
        Assert.EndsWith(suffix, output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain("ftcs1-" + secret[..32], output, StringComparison.Ordinal);
        Assert.Equal(output, SensitiveDataRedactor.Redact(output));
        var otherLine = SensitiveDataRedactor.Redact(input + "\r\nevent=unrelated" + suffix).Split("\r\n")[1];
        Assert.DoesNotContain(evidence.Scope.ScopeId, otherLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("ambiguous-event")]
    [InlineData("profile")]
    [InlineData("duplicate-profile")]
    [InlineData("duplicate-slot")]
    [InlineData("unknown-field")]
    [InlineData("slot-3")]
    [InlineData("wrong-slot")]
    [InlineData("digest-prefix")]
    [InlineData("digest-length")]
    [InlineData("count")]
    [InlineData("retired-with-segment")]
    [InlineData("trailing-data")]
    [Trait("FileTransferInfraSuite", "ContentEvidenceCapture")]
    public void ContentEvidence_MalformedOrUncatalogedSuffixDoesNotProtectTokens(string change)
    {
        var scope = "ftcs1-" + new string('a', 32);
        var digest = "ftcc1-" + new string('b', 64);
        var suffix = $"; content_evidence_profile=sender_chunk_hmac_v1; content_scope_id={scope}; content_evidence_state=captured; content_segment_count=1; content_segment_0_index=12; content_segment_0_bytes=1024; content_segment_0_commitment={digest}";
        var input = "event=filetransfer_data_path_send_result" + suffix;
        input = change switch
        {
            "event" => input.Replace("send_result", "receive_result"),
            "ambiguous-event" => "event=unrelated; " + input,
            "profile" => input.Replace("sender_chunk_hmac_v1", "sender_chunk_hmac_v2"),
            "duplicate-profile" => "content_evidence_profile=sender_chunk_hmac_v1; " + input,
            "duplicate-slot" => "content_segment_0_bytes=1024; " + input,
            "unknown-field" => "content_secret=" + new string('c', 64) + "; " + input,
            "slot-3" => input.Replace("segment_0", "segment_3"),
            "wrong-slot" => input.Replace("segment_0_bytes", "segment_1_bytes"),
            "digest-prefix" => input.Replace("ftcc1-", "ftcc2-"),
            "digest-length" => input + "b",
            "count" => input.Replace("segment_count=1", "segment_count=2"),
            "retired-with-segment" => input.Replace("state=captured", "state=retired"),
            _ => input + "; arbitrary=tail",
        };
        var output = SensitiveDataRedactor.Redact(input);
        Assert.DoesNotContain(scope, output, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('b', 64), output, StringComparison.Ordinal);
    }
}
