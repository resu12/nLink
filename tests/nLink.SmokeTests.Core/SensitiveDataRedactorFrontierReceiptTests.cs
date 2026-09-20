using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferRuntimeSuite", "FrontierPublicationWriter")]
public sealed class SensitiveDataRedactorFrontierReceiptTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Event = "filetransfer_frontier_publication_receipt_closed";

    [Fact]
    public void FrontierReceiptRedaction_PreservesOnlyCatalogedDigestsAndStillRedactsSecrets()
    {
        var fields = new[] { "frontier_receipt_hash", "frontier_completion_hash", "frontier_observation_id" };
        var input = $"event={Event}; " + string.Join("; ", fields.Select(field => $"{field}=ftfr1-{Hex}")) +
            $"; sharedKey={Hex}; private_key={Hex}; payloadBase64={Hex}; source={Hex}; unrelated_hash=ftfr1-{Hex}";
        var output = SensitiveDataRedactor.Redact(input);
        foreach (var field in fields) { Assert.Contains($"{field}=ftfr1-{Hex};", output, StringComparison.Ordinal); }
        foreach (var field in new[] { "sharedKey", "private_key", "payloadBase64", "source", "unrelated_hash" })
        { Assert.DoesNotContain($"{field}=" + (field == "unrelated_hash" ? "ftfr1-" : "") + Hex, output, StringComparison.Ordinal); }
        Assert.Equal(output, SensitiveDataRedactor.Redact(output));
    }

    [Theory]
    [InlineData("unrelated", "ftfr1-", "")]
    [InlineData(Event + "_suffix", "ftfr1-", "")]
    [InlineData(Event + "; event=unrelated", "ftfr1-", "")]
    [InlineData(Event, "ftfr2-", "")]
    [InlineData(Event, "ftfr1-", "0")]
    [InlineData(Event, "ftfr1-", "-raw")]
    public void FrontierReceiptRedaction_RejectsUncatalogedAmbiguousAndMalformedValues(string name, string prefix, string suffix)
    {
        var value = prefix + Hex + suffix;
        Assert.DoesNotContain(value, SensitiveDataRedactor.Redact($"event={name}; frontier_receipt_hash={value}"), StringComparison.Ordinal);
    }

    [Fact]
    public void FrontierReceiptRedaction_CannotProtectAnotherLineOrAnEncodedPayload()
    {
        var field = $"frontier_receipt_hash=ftfr1-{Hex}";
        var lines = SensitiveDataRedactor.Redact($"event={Event}; {field}\r\nevent=unrelated; {field}").Split("\r\n");
        Assert.Contains(field, lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(field, lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain(Hex, SensitiveDataRedactor.Redact($"event={Event}; frontier_receipt_payload={Hex}"), StringComparison.Ordinal);
    }
}
