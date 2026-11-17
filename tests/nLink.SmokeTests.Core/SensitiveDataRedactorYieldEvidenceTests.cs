using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SensitiveDataRedactorYieldEvidenceTests
{
    private const string Id = "ftye1-0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("filetransfer_fallback_tail_yield_evidence", true)]
    [InlineData("filetransfer_fallback_tail_yield_evidence_suffix", false)]
    [InlineData("other", false)]
    [InlineData("filetransfer_fallback_tail_yield_evidence; event=other", false)]
    public void YieldEvidence_ProtectsOnlyExactEventAndDomain(string eventName, bool preserve)
    {
        var text = $"event={eventName}; yield_evidence_id={Id}; sharedKey={Id}; other_id={Id}; authority_effect=none";
        var result = SensitiveDataRedactor.Redact(text);
        Assert.Equal(preserve, result.Contains($"yield_evidence_id={Id};", StringComparison.Ordinal));
        Assert.DoesNotContain($"sharedKey={Id}", result, StringComparison.Ordinal);
        Assert.DoesNotContain($"other_id={Id}", result, StringComparison.Ordinal);
        Assert.Equal(result, SensitiveDataRedactor.Redact(result));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-raw")]
    public void YieldEvidence_DoesNotProtectLongerOrCrossLineValues(string suffix)
    {
        Assert.DoesNotContain(Id + suffix, SensitiveDataRedactor.Redact(
            $"event=filetransfer_fallback_tail_yield_evidence; yield_evidence_id={Id}{suffix}"), StringComparison.Ordinal);
        var lines = SensitiveDataRedactor.Redact(
            $"event=filetransfer_fallback_tail_yield_evidence; yield_evidence_id={Id}\nevent=other; yield_evidence_id={Id}").Split('\n');
        Assert.Contains(Id, lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(Id, lines[1], StringComparison.Ordinal);
    }
}
