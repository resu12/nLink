using System.Text.RegularExpressions;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

public sealed class HelperVerificationCodeFormatterTests
{
    private static readonly Regex VerificationCodePattern = new("^[a-z]+-[a-z]+-\\d{4}$", RegexOptions.Compiled);

    [Fact]
    public void Format_SameIdentity_ReturnsDeterministicCode()
    {
        const string helperIdentity = "nlink-2fe2a75c.examplehelperaddress";

        var first = HelperVerificationCodeFormatter.Format(helperIdentity);
        var second = HelperVerificationCodeFormatter.Format(helperIdentity);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Format_NormalizesWhitespaceAndCase()
    {
        const string helperIdentity = "nlink-2fe2a75c.examplehelperaddress";

        var normalized = HelperVerificationCodeFormatter.Format(helperIdentity);
        var mixed = HelperVerificationCodeFormatter.Format("  NLINK-2FE2A75C.EXAMPLEHELPERADDRESS  ");

        Assert.Equal(normalized, mixed);
    }

    [Fact]
    public void Format_PeerAddressOverload_MatchesStringOverload()
    {
        const string helperIdentity = "nlink-8676606c.anotherhelperaddress";

        var fromString = HelperVerificationCodeFormatter.Format(helperIdentity);
        var fromPeerAddress = HelperVerificationCodeFormatter.Format(new PeerAddress(helperIdentity));

        Assert.Equal(fromString, fromPeerAddress);
    }

    [Fact]
    public void Format_UsesHumanFriendlyWordWordNumberPattern()
    {
        var code = HelperVerificationCodeFormatter.Format("nlink-56f845dc.samplehelper");

        Assert.Matches(VerificationCodePattern, code);
    }

    [Fact]
    public void Format_DifferentIdentities_ProduceDifferentCodes_ForRepresentativeSamples()
    {
        var first = HelperVerificationCodeFormatter.Format("nlink-56f845dc.samplehelper");
        var second = HelperVerificationCodeFormatter.Format("nlink-27d603c8.otherhelper");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void FormatOrNull_EmptyIdentity_ReturnsNull()
    {
        Assert.Null(HelperVerificationCodeFormatter.FormatOrNull((string?)null));
        Assert.Null(HelperVerificationCodeFormatter.FormatOrNull(string.Empty));
        Assert.Null(HelperVerificationCodeFormatter.FormatOrNull("   "));
        Assert.Null(HelperVerificationCodeFormatter.FormatOrNull((PeerAddress?)null));
    }
}
