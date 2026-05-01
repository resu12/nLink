using System.Text.RegularExpressions;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SessionVerificationCodeDerivationTests
{
    private static readonly Regex FallbackCodePattern =
        new("^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$", RegexOptions.Compiled);

    private static readonly string[] ExpectedEmojiAlphabet =
    {
        "\u2600\uFE0F",
        "\U0001F319",
        "\u2B50",
        "\u2601\uFE0F",
        "\u2744\uFE0F",
        "\U0001F525",
        "\U0001F4A7",
        "\U0001F33F",
        "\U0001F333",
        "\U0001F33C",
        "\U0001F340",
        "\u26F0\uFE0F",
        "\U0001F30A",
        "\u2693\uFE0F",
        "\U0001F9ED",
        "\U0001F511",
        "\U0001F512",
        "\U0001F6E1\uFE0F",
        "\U0001F514",
        "\U0001F4A1",
        "\U0001F4D6",
        "\u270F\uFE0F",
        "\u2709\uFE0F",
        "\U0001F4E6",
        "\U0001F381",
        "\U0001F4F7",
        "\U0001F4F1",
        "\U0001F4BB",
        "\u2328\uFE0F",
        "\u2699\uFE0F",
        "\U0001F527",
        "\U0001F528",
        "\U0001F9F2",
        "\U0001F50B",
        "\U0001F50C",
        "\u231B",
        "\U0001F550",
        "\U0001F4FB",
        "\U0001F6F0\uFE0F",
        "\U0001F680",
        "\U0001F48E",
        "\U0001F451",
        "\U0001F3C6",
        "\U0001F3C5",
        "\U0001F3AF",
        "\U0001F388",
        "\U0001FA81",
        "\u2602\uFE0F",
        "\u26FA\uFE0F",
        "\U0001F5FA\uFE0F",
        "\U0001F4CC",
        "\U0001F4CE",
        "\u2702\uFE0F",
        "\U0001F4CF",
        "\U0001F4CB",
        "\U0001F4C1",
        "\U0001F50D",
        "\U0001F52C",
        "\U0001F52D",
        "\u2697\uFE0F",
        "\U0001F537",
        "\U0001F535",
        "\U0001F7E9",
        "\U0001F536",
    };

    [Fact]
    public void Derive_IsDeterministic_ForFixedMaterial()
    {
        var first = SessionVerificationCodeDerivation.Derive(FixedMaterial());
        var second = SessionVerificationCodeDerivation.Derive(FixedMaterial());

        Assert.Equal(first, second);
    }

    [Fact]
    public void Derive_MatchesForHelperAndHelpee_WhenMaterialMatches()
    {
        var helperView = SessionVerificationCodeDerivation.Derive(FixedMaterial());
        var helpeeView = SessionVerificationCodeDerivation.Derive(FixedMaterial());

        Assert.Equal(helperView.EmojiSequence, helpeeView.EmojiSequence);
        Assert.Equal(helperView.FallbackCode, helpeeView.FallbackCode);
    }

    [Fact]
    public void Derive_Changes_WhenTranscriptMaterialChanges()
    {
        var baseline = SessionVerificationCodeDerivation.Derive(FixedMaterial());
        var baselineCombined = Combine(baseline);

        var variants = new[]
        {
            FixedMaterial() with { SessionId = new SessionId("sess_verification_changed") },
            FixedMaterial() with { HelperAddress = new PeerAddress("helper.changed.0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") },
            FixedMaterial() with { HelpeeAddress = new PeerAddress("helpee.changed.0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") },
            FixedMaterial() with { SessionRootKey = Bytes(0x21, 32) },
            FixedMaterial() with { HelperEcdhPublicKey = Bytes(0x62, 65) },
            FixedMaterial() with { HelpeeEcdhPublicKey = Bytes(0xB3, 65) },
            FixedMaterial() with { ChallengeNonce = "changed-challenge-nonce" },
            FixedMaterial() with { SessionContextCode = "addr.fedcba9876543210" },
        };

        foreach (var variant in variants)
        {
            var changed = SessionVerificationCodeDerivation.Derive(variant);

            Assert.NotEqual(baselineCombined, Combine(changed));
        }
    }

    [Fact]
    public void Derive_DefaultOutputShape_IsStable()
    {
        var code = SessionVerificationCodeDerivation.Derive(FixedMaterial());
        var emojis = code.EmojiSequence.Split(' ');
        var alphabet = ExpectedEmojiAlphabet.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(SessionVerificationCodeDerivation.SourceHandshakeTranscriptV1, code.Source);
        Assert.Equal(SessionVerificationCodeDerivation.DefaultEmojiCount, emojis.Length);
        Assert.All(emojis, emoji => Assert.Contains(emoji, alphabet));
        Assert.Matches(FallbackCodePattern, code.FallbackCode);
    }

    [Fact]
    public void DefaultEmojiAlphabet_OrderIsLocked()
    {
        Assert.Equal(64, SessionVerificationCodeDerivation.DefaultEmojiAlphabet.Count);
        Assert.Equal(ExpectedEmojiAlphabet, SessionVerificationCodeDerivation.DefaultEmojiAlphabet);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(10)]
    public void Derive_AcceptsSupportedEmojiCounts(int emojiCount)
    {
        var code = SessionVerificationCodeDerivation.Derive(FixedMaterial(), emojiCount);

        Assert.Equal(emojiCount, code.EmojiSequence.Split(' ').Length);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    public void Derive_RejectsUnsupportedEmojiCounts(int emojiCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SessionVerificationCodeDerivation.Derive(FixedMaterial(), emojiCount));
    }

    [Fact]
    public void Derive_RejectsNullMaterial()
    {
        SessionVerificationMaterial material = null!;

        Assert.Throws<ArgumentNullException>(() => SessionVerificationCodeDerivation.Derive(material));
    }

    [Theory]
    [MemberData(nameof(InvalidMaterials))]
    public void Derive_RejectsMissingMaterialFields(SessionVerificationMaterial material)
    {
        Assert.Throws<ArgumentException>(() => SessionVerificationCodeDerivation.Derive(material));
    }

    public static IEnumerable<object[]> InvalidMaterials()
    {
        yield return new object[] { FixedMaterial() with { SessionId = default } };
        yield return new object[] { FixedMaterial() with { HelperAddress = default } };
        yield return new object[] { FixedMaterial() with { HelpeeAddress = default } };
        yield return new object[] { FixedMaterial() with { SessionRootKey = Array.Empty<byte>() } };
        yield return new object[] { FixedMaterial() with { SessionRootKey = null! } };
        yield return new object[] { FixedMaterial() with { HelperEcdhPublicKey = Array.Empty<byte>() } };
        yield return new object[] { FixedMaterial() with { HelperEcdhPublicKey = null! } };
        yield return new object[] { FixedMaterial() with { HelpeeEcdhPublicKey = Array.Empty<byte>() } };
        yield return new object[] { FixedMaterial() with { HelpeeEcdhPublicKey = null! } };
        yield return new object[] { FixedMaterial() with { ChallengeNonce = " " } };
        yield return new object[] { FixedMaterial() with { SessionContextCode = " " } };
    }

    private static SessionVerificationMaterial FixedMaterial()
    {
        return new SessionVerificationMaterial(
            new SessionId("sess_verification_fixed"),
            new PeerAddress("helper.fixed.0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            new PeerAddress("helpee.fixed.fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"),
            Bytes(0x10, 32),
            Bytes(0x50, 65),
            Bytes(0xA0, 65),
            "fixed-challenge-nonce",
            "addr.0123456789abcdef");
    }

    private static byte[] Bytes(int start, int length)
    {
        return Enumerable.Range(0, length).Select(i => unchecked((byte)(start + i))).ToArray();
    }

    private static string Combine(SessionVerificationCode code)
    {
        return code.EmojiSequence + "|" + code.FallbackCode;
    }
}
