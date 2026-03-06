using System.Text;
using NLink.App.Services;
using NLink.Core;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

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
    public void Resolve_TamperedInvite_ReturnsSignatureFailure()
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

        Assert.False(result.IsValid);
        Assert.Equal(ConnectInputValidationError.InvalidInviteToken, result.Error);
        Assert.Equal(InviteTokenValidationError.SignatureInvalid, result.InviteValidationError);
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
        var signer = new HmacSha256InviteSignatureService(
            Encoding.UTF8.GetBytes("nlink-invite-signing-key-v1"));
        var validator = new InviteTokenValidator(codec, signer, new InviteExpiryValidator());
        return new ConnectInputResolver(codec, validator);
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

public sealed class SessionRuntimeAddressConnectTests
{
    [Trait("Category", "Smoke")]
    [Fact]
    public async Task StartHelperAsync_Address_UsesAddressTargetTransportPath()
    {
        var fakeTransport = new FakeAddressTransport();
        using var runtime = new SessionRuntime(() => fakeTransport);

        await runtime.StartHelperAsync(new PeerAddress("nlink-helpee.runtime"), CancellationToken.None);

        Assert.Equal("nlink-helpee.runtime", fakeTransport.LastJoinByAddress);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task StartHelperAsync_Invite_UsesInviteTargetTransportPath()
    {
        var fakeTransport = new FakeAddressTransport();
        using var runtime = new SessionRuntime(() => fakeTransport);
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_500_000);
        var token = CreateInviteToken(nowUtc, lifetime: TimeSpan.FromMinutes(2));
        var resolver = CreateResolver();
        var resolution = resolver.Resolve(token, nowUtc.AddSeconds(1));
        Assert.True(resolution.IsValid, resolution.Message);
        Assert.NotNull(resolution.Invite);

        await runtime.StartHelperAsync(token, resolution.Invite!, CancellationToken.None);

        Assert.Equal(token, fakeTransport.LastJoinByInviteToken);
        Assert.Equal("sess_address_native", fakeTransport.LastJoinInviteSessionId);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task StartHelpeeAsync_AddressNative_UsesAddressHostTransportPath()
    {
        var fakeTransport = new FakeAddressTransport();
        using var runtime = new SessionRuntime(() => fakeTransport);

        await runtime.StartHelpeeAsync(CancellationToken.None);

        Assert.True(fakeTransport.HostedByAddress);
    }

    private static ConnectInputResolver CreateResolver()
    {
        var codec = new InviteTokenCodec();
        var signer = new HmacSha256InviteSignatureService(
            Encoding.UTF8.GetBytes("nlink-invite-signing-key-v1"));
        var validator = new InviteTokenValidator(codec, signer, new InviteExpiryValidator());
        return new ConnectInputResolver(codec, validator);
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

    private sealed class FakeAddressTransport : ISignalingTransport, IAddressTargetSignalingTransport, IInviteTargetSignalingTransport, IAddressHostSignalingTransport, ISessionSecuritySignalingTransport
    {
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;

        public string? LastJoinByAddress { get; private set; }
        public string? LastJoinByInviteToken { get; private set; }
        public string? LastJoinInviteSessionId { get; private set; }
        public bool HostedByAddress { get; private set; }
        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public Task HostByAddressAsync(CancellationToken ct)
        {
            HostedByAddress = true;
            return Task.CompletedTask;
        }

        public Task JoinByAddressAsync(string peerAddress, CancellationToken ct)
        {
            LastJoinByAddress = peerAddress;
            return Task.CompletedTask;
        }

        public Task JoinByInviteAsync(string inviteToken, ValidatedInviteV1 invite, CancellationToken ct)
        {
            LastJoinByInviteToken = inviteToken;
            LastJoinInviteSessionId = invite.SessionId.Value;
            return Task.CompletedTask;
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }

        private void UpdateSessionSecurityState(SessionSecurityState nextState)
        {
            if (Equals(currentSessionSecurityState, nextState))
            {
                return;
            }

            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }
    }
}
