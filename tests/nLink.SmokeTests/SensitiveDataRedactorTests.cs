using NLink.Core.Logging;

namespace NLink.SmokeTests;

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
}
