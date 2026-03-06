using System.Text;
using NLink.Core.SessionConnect;

namespace NLink.SmokeTests;

public sealed class SessionConnectInviteTokenTests
{
    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteToken_ValidRoundTrip_ValidatesAndExtractsTargetAddress()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
        var request = new InviteTokenCreateRequest(
            IssuerAddress: new PeerAddress("nlink-helper.a1b2c3d4"),
            TargetAddress: new PeerAddress("nlink-helpee.e5f6a7b8"),
            SessionId: new SessionId("sess_p7_invite"),
            Capabilities: InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.RemoteControl,
            Lifetime: TimeSpan.FromMinutes(5));

        var codec = new InviteTokenCodec();
        var signer = CreateSigner();
        var factory = new InviteTokenFactory(codec, signer);
        var validator = new InviteTokenValidator(codec, signer, new InviteExpiryValidator());

        var created = factory.Create(request, nowUtc);
        Assert.True(created.IsSuccess, created.Message);
        Assert.NotNull(created.Token);
        Assert.StartsWith(InviteTokenCodec.TokenPrefix + ".", created.Token!, StringComparison.Ordinal);

        var parsed = codec.Parse(created.Token);
        Assert.True(parsed.IsSuccess, parsed.Message);
        Assert.NotNull(parsed.Envelope);

        var validated = validator.Validate(parsed.Envelope!, nowUtc.AddSeconds(1));
        Assert.True(validated.IsSuccess, validated.Message);
        Assert.Equal(InviteValidationResult.Valid, validated.Result);
        Assert.NotNull(validated.Invite);
        Assert.Equal(request.TargetAddress, validated.Invite!.TargetAddress);
        Assert.Equal(request.IssuerAddress, validated.Invite.IssuerAddress);
        Assert.Equal(request.SessionId, validated.Invite.SessionId);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteToken_HelperBoundInvite_RoundTripsBoundHelperAddress()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_010_000);
        var boundHelperAddress = new PeerAddress("nlink-helper.bound.1111");
        var request = new InviteTokenCreateRequest(
            IssuerAddress: new PeerAddress("nlink-helpee.bound.2222"),
            TargetAddress: new PeerAddress("nlink-helpee.bound.2222"),
            SessionId: new SessionId("sess_bound_helper"),
            Capabilities: InviteCapabilities.Chat | InviteCapabilities.RemoteControl,
            Lifetime: TimeSpan.FromMinutes(5),
            BoundHelperAddress: boundHelperAddress);

        var codec = new InviteTokenCodec();
        var signer = CreateSigner();
        var factory = new InviteTokenFactory(codec, signer);
        var validator = new InviteTokenValidator(codec, signer, new InviteExpiryValidator());

        var created = factory.Create(request, nowUtc);
        Assert.True(created.IsSuccess, created.Message);

        var validated = validator.Validate(created.Token, nowUtc.AddSeconds(1));
        Assert.True(validated.IsSuccess, validated.Message);
        Assert.Equal(boundHelperAddress, validated.Invite!.BoundHelperAddress);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteToken_ExpiredInvite_IsRejected()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_100_000);
        var request = new InviteTokenCreateRequest(
            IssuerAddress: new PeerAddress("nlink-helper.1111"),
            TargetAddress: new PeerAddress("nlink-helpee.2222"),
            SessionId: new SessionId("sess_expired"),
            Capabilities: InviteCapabilities.Chat,
            Lifetime: TimeSpan.FromSeconds(30));

        var codec = new InviteTokenCodec();
        var signer = CreateSigner();
        var factory = new InviteTokenFactory(codec, signer);
        var validator = new InviteTokenValidator(codec, signer, new InviteExpiryValidator());

        var created = factory.Create(request, nowUtc);
        Assert.True(created.IsSuccess, created.Message);

        var validated = validator.Validate(created.Token, nowUtc.AddSeconds(31));
        Assert.False(validated.IsSuccess);
        Assert.Equal(InviteValidationResult.Expired, validated.Result);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteToken_ModifiedPayload_FailsSignatureVerification()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_200_000);
        var request = new InviteTokenCreateRequest(
            IssuerAddress: new PeerAddress("nlink-helper.3333"),
            TargetAddress: new PeerAddress("nlink-helpee.4444"),
            SessionId: new SessionId("sess_tamper"),
            Capabilities: InviteCapabilities.ScreenShare,
            Lifetime: TimeSpan.FromMinutes(2));

        var codec = new InviteTokenCodec();
        var signer = CreateSigner();
        var factory = new InviteTokenFactory(codec, signer);
        var validator = new InviteTokenValidator(codec, signer, new InviteExpiryValidator());

        var created = factory.Create(request, nowUtc);
        Assert.True(created.IsSuccess, created.Message);
        Assert.NotNull(created.Token);

        var tokenParts = created.Token!.Split('.', StringSplitOptions.None);
        Assert.Equal(3, tokenParts.Length);

        var payloadBytes = DecodeBase64Url(tokenParts[1]);
        var payloadJson = Encoding.UTF8.GetString(payloadBytes);
        Assert.Contains(request.TargetAddress.Value, payloadJson, StringComparison.Ordinal);

        var tamperedJson = payloadJson.Replace(
            request.TargetAddress.Value,
            "nlink-helpee.tampered",
            StringComparison.Ordinal);
        var tamperedPayloadSegment = EncodeBase64Url(Encoding.UTF8.GetBytes(tamperedJson));
        var tamperedToken = $"{tokenParts[0]}.{tamperedPayloadSegment}.{tokenParts[2]}";

        var validated = validator.Validate(tamperedToken, nowUtc.AddSeconds(1));
        Assert.False(validated.IsSuccess);
        Assert.Equal(InviteValidationResult.InvalidSignature, validated.Result);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteToken_MalformedToken_FailsWithParseResult()
    {
        var validator = new InviteTokenValidator(
            new InviteTokenCodec(),
            CreateSigner(),
            new InviteExpiryValidator());

        var validated = validator.Validate("nlinki1.not-a-valid-token", DateTimeOffset.UtcNow);
        Assert.False(validated.IsSuccess);
        Assert.Equal(InviteValidationResult.Malformed, validated.Result);
        Assert.NotEqual(InviteTokenParseError.None, validated.ParseError);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteToken_UnsupportedVersion_IsRejected()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_220_000);
        var request = new InviteTokenCreateRequest(
            IssuerAddress: new PeerAddress("nlink-helper.5555"),
            TargetAddress: new PeerAddress("nlink-helpee.6666"),
            SessionId: new SessionId("sess_unsupported"),
            Capabilities: InviteCapabilities.Chat,
            Lifetime: TimeSpan.FromMinutes(2));

        var codec = new InviteTokenCodec();
        var signer = CreateSigner();
        var factory = new InviteTokenFactory(codec, signer);
        var validator = new InviteTokenValidator(codec, signer, new InviteExpiryValidator());

        var created = factory.Create(request, nowUtc);
        Assert.True(created.IsSuccess, created.Message);
        Assert.NotNull(created.Token);

        var tokenParts = created.Token!.Split('.', StringSplitOptions.None);
        Assert.Equal(3, tokenParts.Length);
        var payloadJson = Encoding.UTF8.GetString(DecodeBase64Url(tokenParts[1]));
        var upgradedJson = payloadJson.Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal);
        var upgradedPayloadBytes = Encoding.UTF8.GetBytes(upgradedJson);
        var upgradedToken = $"{tokenParts[0]}.{EncodeBase64Url(upgradedPayloadBytes)}.{EncodeBase64Url(signer.Sign(upgradedPayloadBytes))}";

        var validated = validator.Validate(upgradedToken, nowUtc.AddSeconds(1));
        Assert.False(validated.IsSuccess);
        Assert.Equal(InviteValidationResult.UnsupportedVersion, validated.Result);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteToken_ReusedInvite_IsDetectedWhenConsumed()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_240_000);
        var request = new InviteTokenCreateRequest(
            IssuerAddress: new PeerAddress("nlink-helper.7777"),
            TargetAddress: new PeerAddress("nlink-helpee.8888"),
            SessionId: new SessionId("sess_replay"),
            Capabilities: InviteCapabilities.Chat,
            Lifetime: TimeSpan.FromMinutes(2));

        var codec = new InviteTokenCodec();
        var signer = CreateSigner();
        var factory = new InviteTokenFactory(codec, signer);
        var replayCache = new InMemoryInviteReplayCache();
        var validator = new InviteTokenValidator(codec, signer, new InviteExpiryValidator(), replayCache);

        var created = factory.Create(request, nowUtc);
        Assert.True(created.IsSuccess, created.Message);
        Assert.NotNull(created.Token);

        var first = validator.Validate(created.Token, nowUtc.AddSeconds(1), InviteValidationMode.ConsumeIfValid);
        var second = validator.Validate(created.Token, nowUtc.AddSeconds(2), InviteValidationMode.ConsumeIfValid);

        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal(InviteValidationResult.Valid, first.Result);
        Assert.False(second.IsSuccess);
        Assert.Equal(InviteValidationResult.ReplayDetected, second.Result);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteToken_Replay_IsDetectedAcrossValidatorRestart_WhenUsingPersistentStore()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_250_000);
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-invite-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var storePath = Path.Combine(tempDir, "invite-security-store.json");

        try
        {
            var request = new InviteTokenCreateRequest(
                IssuerAddress: new PeerAddress("nlink-helper.persist.1111"),
                TargetAddress: new PeerAddress("nlink-helpee.persist.2222"),
                SessionId: new SessionId("sess_persist_replay"),
                Capabilities: InviteCapabilities.Chat,
                Lifetime: TimeSpan.FromMinutes(2));

            var codec = new InviteTokenCodec();
            var signer = CreateSigner();
            var issueStore = new PersistentInviteSecurityStore(new InviteSecurityStoreOptions { FilePath = storePath });
            var factory = new InviteTokenFactory(codec, signer, issueStore);
            var validatorOne = new InviteTokenValidator(codec, signer, new InviteExpiryValidator(), issueStore, issueStore);

            var created = factory.Create(request, nowUtc);
            Assert.True(created.IsSuccess, created.Message);

            var first = validatorOne.Validate(created.Token, nowUtc.AddSeconds(1), InviteValidationMode.ConsumeIfValid);
            Assert.True(first.IsSuccess, first.Message);

            var replayStore = new PersistentInviteSecurityStore(new InviteSecurityStoreOptions { FilePath = storePath });
            var validatorTwo = new InviteTokenValidator(codec, signer, new InviteExpiryValidator(), replayStore, replayStore);
            var replay = validatorTwo.Validate(created.Token, nowUtc.AddSeconds(2), InviteValidationMode.ConsumeIfValid);

            Assert.False(replay.IsSuccess);
            Assert.Equal(InviteValidationResult.ReplayDetected, replay.Result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteToken_NewInvite_RevokesPreviousInvite_InSameScope()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_260_000);
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-invite-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var storePath = Path.Combine(tempDir, "invite-security-store.json");

        try
        {
            var request = new InviteTokenCreateRequest(
                IssuerAddress: new PeerAddress("nlink-helpee.revoker.1111"),
                TargetAddress: new PeerAddress("nlink-helpee.revoker.1111"),
                SessionId: new SessionId("sess_revoke_one"),
                Capabilities: InviteCapabilities.Chat,
                Lifetime: TimeSpan.FromMinutes(5));

            var codec = new InviteTokenCodec();
            var signer = CreateSigner();
            var store = new PersistentInviteSecurityStore(new InviteSecurityStoreOptions { FilePath = storePath });
            var factory = new InviteTokenFactory(codec, signer, store);
            var validator = new InviteTokenValidator(codec, signer, new InviteExpiryValidator(), store, store);

            var first = factory.Create(request, nowUtc);
            Assert.True(first.IsSuccess, first.Message);

            var second = factory.Create(
                request with
                {
                    SessionId = new SessionId("sess_revoke_two"),
                },
                nowUtc.AddSeconds(1));
            Assert.True(second.IsSuccess, second.Message);

            var firstValidation = validator.Validate(first.Token, nowUtc.AddSeconds(2));
            var secondValidation = validator.Validate(second.Token, nowUtc.AddSeconds(2));

            Assert.False(firstValidation.IsSuccess);
            Assert.Equal(InviteValidationResult.Revoked, firstValidation.Result);
            Assert.True(secondValidation.IsSuccess, secondValidation.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteToken_IssueBurst_IsThrottled()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_270_000);
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-invite-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var storePath = Path.Combine(tempDir, "invite-security-store.json");

        try
        {
            var request = new InviteTokenCreateRequest(
                IssuerAddress: new PeerAddress("nlink-helpee.throttle.1111"),
                TargetAddress: new PeerAddress("nlink-helpee.throttle.1111"),
                SessionId: new SessionId("sess_issue_throttle"),
                Capabilities: InviteCapabilities.Chat,
                Lifetime: TimeSpan.FromMinutes(5));

            var codec = new InviteTokenCodec();
            var signer = CreateSigner();
            var store = new PersistentInviteSecurityStore(new InviteSecurityStoreOptions
            {
                FilePath = storePath,
                MaxIssueAttemptsPerScope = 1,
                IssueWindow = TimeSpan.FromSeconds(30),
            });
            var factory = new InviteTokenFactory(codec, signer, store);

            var first = factory.Create(request, nowUtc);
            var second = factory.Create(
                request with { SessionId = new SessionId("sess_issue_throttle_two") },
                nowUtc.AddSeconds(1));

            Assert.True(first.IsSuccess, first.Message);
            Assert.False(second.IsSuccess);
            Assert.Equal(InviteTokenCreateError.Throttled, second.Error);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static HmacSha256InviteSignatureService CreateSigner()
    {
        return new HmacSha256InviteSignatureService(
            Encoding.UTF8.GetBytes("nlink-test-invite-signing-key-v1"));
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
