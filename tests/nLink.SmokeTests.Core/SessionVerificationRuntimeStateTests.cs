using NLink.App.Services;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class SessionVerificationRuntimeStateTests : CoreSmokeTestsBase
{
    [Fact]
    public void SessionSecurityState_VerificationCode_PreservesAcrossVerifiedAndApproval()
    {
        var sessionId = new SessionId("verification-state-preserve");
        var helpeeAddress = new PeerAddress("verification.helpee");
        var helperAddress = new PeerAddress("verification.helper");
        var verificationCode = CreateVerificationCode();
        var challengeExpiresAt = DateTimeOffset.UtcNow.Add(SessionSecurityDefaults.HandshakeTimeout);

        var challenged = SessionSecurityState.CreateHelpeeWaiting(helpeeAddress)
            .WithHandshakeChallenge(sessionId, helpeeAddress, helperAddress, inviteValidated: true, challengeExpiresAt)
            .WithVerificationCode(verificationCode);
        var verified = challenged.WithHandshakeVerified(helperAddress);
        var grant = new SessionGrant(
            helperAddress,
            CapabilityGrant.Chat,
            sessionId,
            DateTimeOffset.UtcNow.Add(SessionSecurityDefaults.GrantLifetime));

        Assert.Equal(verificationCode, verified.VerificationCode);
        Assert.Equal(verificationCode, verified.WithApproval(grant).VerificationCode);
        Assert.Equal(verificationCode, verified.WithoutApproval().VerificationCode);
    }

    [Fact]
    public void SessionSecurityState_VerificationCode_ClearsOnChallengeFailureAndInvalidate()
    {
        var sessionId = new SessionId("verification-state-clear");
        var helpeeAddress = new PeerAddress("verification.clear.helpee");
        var helperAddress = new PeerAddress("verification.clear.helper");
        var verificationCode = CreateVerificationCode();
        var challengeExpiresAt = DateTimeOffset.UtcNow.Add(SessionSecurityDefaults.HandshakeTimeout);
        var state = SessionSecurityState.CreateHelpeeWaiting(helpeeAddress)
            .WithHandshakeChallenge(sessionId, helpeeAddress, helperAddress, inviteValidated: true, challengeExpiresAt)
            .WithVerificationCode(verificationCode);

        Assert.Null(state.WithHandshakeFailure(SessionHandshakeState.Failed, "failed").VerificationCode);
        Assert.Null(state.Invalidate("invalidated").VerificationCode);
        Assert.Null(state.WithHandshakeChallenge(
            sessionId,
            helpeeAddress,
            helperAddress,
            inviteValidated: true,
            challengeExpiresAt).VerificationCode);
    }

    [Fact]
    public async Task SessionRuntime_MirrorsTransportVerificationCode()
    {
        using var transport = new ScriptedSignalingTransport(
            onHostByAddressAsync: _ => Task.CompletedTask,
            localPeerAddress: "verification.runtime.helpee");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelpeeAsync(CancellationToken.None);

        var helpeeAddress = new PeerAddress(transport.LocalPeerAddress);
        var helperAddress = new PeerAddress("verification.runtime.helper");
        var verificationCode = CreateVerificationCode();
        var state = CreateVerifiedSecurityState(helpeeAddress, helperAddress)
            .WithVerificationCode(verificationCode);

        transport.SetSessionSecurityStateForTests(state);

        await WaitUntilAsync(() => runtime.SecurityState.VerificationCode is not null, TimeSpan.FromSeconds(2));
        Assert.Equal(verificationCode, runtime.SecurityState.VerificationCode);
        Assert.Equal(verificationCode, runtime.FlowSnapshot.VerificationCode);
    }

    [Fact]
    public async Task NknHandshake_PublishesMatchingVerificationCodes()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var logStart = GetOperationalLogLength();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var options = NknTransportOptions.Load();
            using var host = new NknSignalingTransport(
                new FakeNknClient("verification.host." + Guid.NewGuid().ToString("N")),
                options,
                new NknIdentity("verification-host", "verification.host.identity"));
            using var helper = new NknSignalingTransport(
                new FakeNknClient("verification.helper." + Guid.NewGuid().ToString("N")),
                options,
                new NknIdentity("verification-helper", "verification.helper.identity"));

            await ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat);

            var hostCode = Assert.IsType<SessionVerificationCode>(host.CurrentSessionSecurityState.VerificationCode);
            var helperCode = Assert.IsType<SessionVerificationCode>(helper.CurrentSessionSecurityState.VerificationCode);
            Assert.Equal(hostCode, helperCode);
            Assert.Equal(SessionVerificationCodeDerivation.SourceHandshakeTranscriptV1, hostCode.Source);
            Assert.Equal(SessionHandshakeState.Verified, host.CurrentSessionSecurityState.HandshakeState);
            Assert.Equal(SessionHandshakeState.Verified, helper.CurrentSessionSecurityState.HandshakeState);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=session_verification_code_ready", logTail, StringComparison.Ordinal);
            Assert.Contains("source=handshake_transcript_v1", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(hostCode.FallbackCode, logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    private static SessionVerificationCode CreateVerificationCode()
    {
        return new SessionVerificationCode(
            "sun moon star cloud leaf fire key",
            "1234-ABCD-5678",
            SessionVerificationCodeDerivation.SourceHandshakeTranscriptV1);
    }
}
