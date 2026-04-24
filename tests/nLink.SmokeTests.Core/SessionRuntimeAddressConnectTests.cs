using System.Text;
using NLink.App.Services;
using NLink.Core;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
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
    public async Task StartHelperAsync_InviteShareCode_UsesDecodedInviteTargetTransportPath()
    {
        var fakeTransport = new FakeAddressTransport();
        using var runtime = new SessionRuntime(() => fakeTransport);
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_500_000);
        var token = CreateInviteToken(nowUtc, lifetime: TimeSpan.FromMinutes(2));
        var shareCode = InviteShareCodeCodec.Encode(token);
        var resolver = CreateResolver();
        var resolution = resolver.Resolve(shareCode, nowUtc.AddSeconds(1));
        Assert.True(resolution.IsValid, resolution.Message);
        Assert.NotNull(resolution.Invite);
        Assert.Equal(token, resolution.InviteTokenText);

        await runtime.StartHelperAsync(resolution.InviteTokenText!, resolution.Invite!, CancellationToken.None);

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
