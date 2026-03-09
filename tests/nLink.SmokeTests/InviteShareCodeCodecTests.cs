using System.Text.RegularExpressions;
using NLink.Core.SessionConnect;

namespace NLink.SmokeTests;

public sealed class InviteShareCodeCodecTests
{
    private static readonly Regex CodePattern = new("^ninv1-(?:[0-9A-Z]{4}-)*[0-9A-Z]{2,4}$", RegexOptions.Compiled);

    [Fact]
    public void EncodeDecode_RoundTripsRealInviteToken()
    {
        var inviteToken = CreateInviteToken();

        var shareCode = InviteShareCodeCodec.Encode(inviteToken);
        var decoded = InviteShareCodeCodec.Decode(shareCode);

        Assert.True(decoded.IsSuccess, decoded.Message);
        Assert.Equal(inviteToken, decoded.InviteToken);
    }

    [Fact]
    public void Encode_UsesExpectedPrefixAndGrouping()
    {
        var shareCode = InviteShareCodeCodec.Encode(CreateInviteToken());

        Assert.Matches(CodePattern, shareCode);
    }

    [Fact]
    public void Decode_IsCaseInsensitive_AndIgnoresSeparators()
    {
        var inviteToken = CreateInviteToken();
        var shareCode = InviteShareCodeCodec.Encode(inviteToken);
        var normalized = shareCode.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var pasted = "  ninv1-" + normalized.Substring(5) + "  ";

        var decoded = InviteShareCodeCodec.Decode(pasted);

        Assert.True(decoded.IsSuccess, decoded.Message);
        Assert.Equal(inviteToken, decoded.InviteToken);
    }

    [Fact]
    public void Decode_ModifiedCode_FailsChecksum()
    {
        var shareCode = InviteShareCodeCodec.Encode(CreateInviteToken());
        var chars = shareCode.ToCharArray();

        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] == '-')
            {
                continue;
            }

            chars[i] = chars[i] == 'Z' ? '2' : 'Z';
            break;
        }

        var tampered = new string(chars);

        var decoded = InviteShareCodeCodec.Decode(tampered);

        Assert.False(decoded.IsSuccess);
        Assert.Equal(InviteShareCodeDecodeError.InvalidChecksum, decoded.Error);
    }

    [Fact]
    public void Decode_InvalidPrefix_IsRejected()
    {
        var decoded = InviteShareCodeCodec.Decode("invite-1234-5678");

        Assert.False(decoded.IsSuccess);
        Assert.Equal(InviteShareCodeDecodeError.InvalidPrefix, decoded.Error);
    }

    private static string CreateInviteToken()
    {
        var nowUtc = new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero);
        var factory = InviteTokenServiceFactory.CreateInviteTokenFactory();
        var create = factory.Create(
            new InviteTokenCreateRequest(
                IssuerAddress: new PeerAddress("nlink-issuer.invitecode.1234"),
                TargetAddress: new PeerAddress("nlink-target.invitecode.1234"),
                SessionId: new SessionId("sess_invite_code_codec"),
                Capabilities: InviteCapabilities.Chat | InviteCapabilities.ScreenShare,
                Lifetime: TimeSpan.FromMinutes(5),
                BoundHelperAddress: new PeerAddress("nlink-helper.invitecode.1234")),
            nowUtc);

        Assert.True(create.IsSuccess, create.Message);
        Assert.NotNull(create.Token);
        return create.Token!;
    }
}
