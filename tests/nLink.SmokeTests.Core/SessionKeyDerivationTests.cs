using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SessionKeyDerivationTests
{
    [Fact]
    public void DeriveFileTransferKey_IsDeterministic_AndUsesStableLabel()
    {
        var sessionRootKey = Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray();

        var derived = SessionKeyDerivation.DeriveFileTransferKey(sessionRootKey);

        Assert.Equal(SessionKeyDerivation.DefaultKeyLengthBytes, derived.Length);
        Assert.Equal("v3w06CANJySY7v6Oq8UD/vjXdn90HfYzNcGuwsNPm14=", Convert.ToBase64String(derived));
    }

    [Fact]
    public void DeriveLabeledSubkey_SeparatesLabels()
    {
        var sessionRootKey = Enumerable.Range(0, 32).Select(static i => (byte)(255 - i)).ToArray();

        var fileTransferKey = SessionKeyDerivation.DeriveFileTransferKey(sessionRootKey);
        var clipboardKey = SessionKeyDerivation.DeriveLabeledSubkey(sessionRootKey, "nlink-clipboard-v1");

        Assert.NotEqual(fileTransferKey, clipboardKey);
    }

    [Fact]
    public void DeriveFileTransferKey_RejectsMissingSessionRootKey()
    {
        var ex = Assert.Throws<ArgumentException>(() => SessionKeyDerivation.DeriveFileTransferKey(Array.Empty<byte>()));

        Assert.Equal("sessionRootKey", ex.ParamName);
    }
}
