using System.Text;
using NLink.App.Services;
using NLink.Core;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SessionConnectInputResolverTests
{
    [Trait("Category", "Smoke")]
    [Fact]
    public void Resolve_EmptyInput_ReturnsExplicitValidationError()
    {
        var resolver = CreateResolver();

        var result = resolver.Resolve("   ", DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000));

        Assert.False(result.IsValid);
        Assert.Equal(ConnectInputValidationError.Empty, result.Error);
        Assert.Equal(ConnectInputKind.Unknown, result.Kind);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Resolve_RawAddress_ReturnsPeerAddressKind()
    {
        var resolver = CreateResolver();

        var result = resolver.Resolve("nlink-helpee.a1b2c3d4", DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000));

        Assert.True(result.IsValid, result.Message);
        Assert.Equal(ConnectInputKind.PeerAddress, result.Kind);
        Assert.Equal("nlink-helpee.a1b2c3d4", result.TargetAddress?.Value);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Resolve_ValidInvite_ReturnsInviteKindAndTargetAddress()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_100_000);
        var resolver = CreateResolver();
        var token = CreateInviteToken(nowUtc, lifetime: TimeSpan.FromMinutes(2));

        var result = resolver.Resolve(token, nowUtc.AddSeconds(5));

        Assert.True(result.IsValid, result.Message);
        Assert.Equal(ConnectInputKind.InviteToken, result.Kind);
        Assert.NotNull(result.Invite);
        Assert.Equal("nlink-helpee.target", result.TargetAddress?.Value);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Resolve_QrFormattedInvite_ReturnsInviteKindAndTargetAddress()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_100_000);
        var resolver = CreateResolver();
        var token = CreateInviteToken(nowUtc, lifetime: TimeSpan.FromMinutes(2));

        var result = resolver.Resolve(InviteQrPayload.Format(token), nowUtc.AddSeconds(5));

        Assert.True(result.IsValid, result.Message);
        Assert.Equal(ConnectInputKind.InviteToken, result.Kind);
        Assert.NotNull(result.Invite);
        Assert.Equal("nlink-helpee.target", result.TargetAddress?.Value);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Resolve_InviteShareCode_ReturnsInviteKindAndDecodedRawToken()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_100_000);
        var resolver = CreateResolver();
        var token = CreateInviteToken(nowUtc, lifetime: TimeSpan.FromMinutes(2));
        var shareCode = InviteShareCodeCodec.Encode(token);

        var result = resolver.Resolve(shareCode, nowUtc.AddSeconds(5));

        Assert.True(result.IsValid, result.Message);
        Assert.Equal(ConnectInputKind.InviteToken, result.Kind);
        Assert.NotNull(result.Invite);
        Assert.Equal(token, result.InviteTokenText);
        Assert.Equal("nlink-helpee.target", result.TargetAddress?.Value);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Format_QrInvite_ReturnsRawInviteToken()
    {
        var token = "nlinki1.testpayload.testsignature";

        var payload = InviteQrPayload.Format(token);

        Assert.Equal(token, payload);
        Assert.Equal(token, InviteQrPayload.ExtractTokenOrOriginal(payload));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ExtractTokenOrOriginal_LegacyWrappedQrPayload_ReturnsRawInviteToken()
    {
        var token = "nlinki1.testpayload.testsignature";
        var legacyPayload = $"{InviteQrPayload.Header}\n{InviteQrPayload.EncodedPrefix} {EncodeBase64Url(Encoding.UTF8.GetBytes(token))}";

        Assert.Equal(token, InviteQrPayload.ExtractTokenOrOriginal(legacyPayload));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Resolve_ExpiredInvite_ReturnsExpiredError()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_200_000);
        var resolver = CreateResolver();
        var token = CreateInviteToken(nowUtc, lifetime: TimeSpan.FromSeconds(20));

        var result = resolver.Resolve(token, nowUtc.AddSeconds(25));

        Assert.False(result.IsValid);
        Assert.Equal(ConnectInputValidationError.ExpiredInviteToken, result.Error);
        Assert.Equal(InviteTokenValidationError.Expired, result.InviteValidationError);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Resolve_TamperedInvite_ReturnsParsedInviteForHelpeeSideValidation()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_250_000);
        var resolver = CreateResolver();
        var token = CreateInviteToken(nowUtc, lifetime: TimeSpan.FromMinutes(2));
        var parts = token.Split('.', StringSplitOptions.None);
        Assert.Equal(3, parts.Length);

        var payloadBytes = DecodeBase64Url(parts[1]);
        var payloadJson = Encoding.UTF8.GetString(payloadBytes);
        var tamperedJson = payloadJson.Replace("nlink-helpee.target", "nlink-helpee.tampered", StringComparison.Ordinal);
        var tamperedPayload = EncodeBase64Url(Encoding.UTF8.GetBytes(tamperedJson));
        var tamperedToken = $"{parts[0]}.{tamperedPayload}.{parts[2]}";

        var result = resolver.Resolve(tamperedToken, nowUtc.AddSeconds(1));

        Assert.True(result.IsValid, result.Message);
        Assert.Equal(ConnectInputKind.InviteToken, result.Kind);
        Assert.NotNull(result.Invite);
        Assert.Equal(tamperedToken, result.InviteTokenText);
        Assert.Equal("nlink-helpee.tampered", result.TargetAddress?.Value);
        Assert.Equal(InviteTokenValidationError.None, result.InviteValidationError);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Resolve_SixDigitCode_IsRejectedAsUnsupportedInput()
    {
        var resolver = CreateResolver();

        var result = resolver.Resolve("123 456", DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000));

        Assert.False(result.IsValid);
        Assert.Equal(ConnectInputValidationError.UnsupportedInput, result.Error);
        Assert.Equal(ConnectInputKind.Unknown, result.Kind);
    }

    private static ConnectInputResolver CreateResolver()
    {
        var codec = new InviteTokenCodec();
        return new ConnectInputResolver(codec, new InviteExpiryValidator());
    }

    private static string CreateInviteToken(DateTimeOffset nowUtc, TimeSpan lifetime)
    {
        var codec = new InviteTokenCodec();
        var signer = new HmacSha256InviteSignatureService(
            Encoding.UTF8.GetBytes("nlink-invite-signing-key-v1"));
        var factory = new InviteTokenFactory(codec, signer);

        var create = factory.Create(
            new InviteTokenCreateRequest(
                IssuerAddress: new PeerAddress("nlink-helper.issuer"),
                TargetAddress: new PeerAddress("nlink-helpee.target"),
                SessionId: new SessionId("sess_address_native"),
                Capabilities: InviteCapabilities.Chat | InviteCapabilities.ScreenShare,
                Lifetime: lifetime),
            nowUtc);
        Assert.True(create.IsSuccess, create.Message);
        Assert.NotNull(create.Token);
        return create.Token!;
    }

    private static string EncodeBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        while (normalized.Length % 4 != 0)
        {
            normalized += "=";
        }

        return Convert.FromBase64String(normalized);
    }
}
