using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SensitiveDataRedactorFrontierPayloadMarkerTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Event = "filetransfer_frontier_payload_marker";

    [Fact]
    public void MarkerPreservesOnlyItsExactDigestProjection()
    {
        var fields = $"frontier_payload_sha256={Hex}; frontier_completion_sha256={Hex}; frontier_payload_observation_id={Hex[..32]}; " +
            "frontier_payload_hash_domain=sha256-utf8-frontier-boundary-producer-v1";
        var output = SensitiveDataRedactor.Redact($"event={Event}; {fields}; sharedKey={Hex}; payloadBase64={Hex}; raw_payload={Hex}");
        Assert.Contains(fields, output);
        Assert.DoesNotContain($"sharedKey={Hex}", output);
        Assert.DoesNotContain($"payloadBase64={Hex}", output);
        Assert.DoesNotContain($"raw_payload={Hex}", output);
        Assert.Equal(output, SensitiveDataRedactor.Redact(output));
    }

    [Theory]
    [InlineData("unrelated", "")]
    [InlineData(Event + "_suffix", "")]
    [InlineData(Event + "; event=unrelated", "")]
    [InlineData(Event, "-raw")]
    [InlineData(Event, "0")]
    public void MarkerCannotPreserveUnknownEventsOrMalformedDigests(string name, string suffix)
        => Assert.DoesNotContain(Hex, SensitiveDataRedactor.Redact($"event={name}; frontier_payload_sha256={Hex}{suffix}"));

    [Fact]
    public void MarkerCannotProtectAnotherLineOrFutureHashDomain()
    {
        var lines = SensitiveDataRedactor.Redact($"event={Event}; frontier_payload_sha256={Hex}\nevent=unrelated; frontier_payload_sha256={Hex}").Split('\n');
        Assert.Contains(Hex, lines[0]);
        Assert.DoesNotContain(Hex, lines[1]);
        Assert.DoesNotContain("sha256-utf8-frontier-boundary-producer-v2",
            SensitiveDataRedactor.Redact($"event={Event}; frontier_payload_hash_domain=sha256-utf8-frontier-boundary-producer-v2"));
    }
}
