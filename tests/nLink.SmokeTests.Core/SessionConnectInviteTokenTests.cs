using System.Security.Cryptography;
using System.Text;
using NLink.Core.SessionConnect;
using NLink.SmokeTests.TestUtilities;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
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

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteTokenServiceFactory_DefaultLegacySigningMode_IsBlockedInReleaseWithoutExplicitOptIn()
    {
        using var inviteMode = new ScopedEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar, InviteTokenServiceFactory.InviteModeLegacySigned);
        using var legacyModeOptIn = new ScopedEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteModeEnvVar, null);
        using var signingKey = new ScopedEnvironmentVariable(InviteTokenServiceFactory.InviteSigningKeyEnvVar, null);
        using var legacyOptIn = new ScopedEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar, null);

#if DEBUG
        var keyMaterial = InviteTokenServiceFactory.ReadInviteSigningKeyMaterial();
        Assert.Equal(InviteTokenServiceFactory.DefaultInviteSigningKey, Encoding.UTF8.GetString(keyMaterial));
#else
        var modeEx = Assert.Throws<InvalidOperationException>(() => InviteTokenServiceFactory.CreateInviteTokenFactory());
        Assert.Contains(InviteTokenServiceFactory.InviteModeEnvVar, modeEx.Message, StringComparison.Ordinal);
        Assert.Contains(InviteTokenServiceFactory.AllowInsecureLegacyInviteModeEnvVar, modeEx.Message, StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(() => InviteTokenServiceFactory.ReadInviteSigningKeyMaterial());
        Assert.Contains(InviteTokenServiceFactory.InviteSigningKeyEnvVar, ex.Message, StringComparison.Ordinal);
        Assert.Contains(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar, ex.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => InviteTokenServiceFactory.CreateInviteTokenValidator());
        Assert.Throws<InvalidOperationException>(() => InviteTokenServiceFactory.CreateInviteSignatureService());
        Assert.NotNull(InviteTokenServiceFactory.CreateDefaultResolver());
#endif
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteTokenServiceFactory_LegacySigningMode_CanBeExplicitlyEnabledForInternalUse()
    {
        using var inviteMode = new ScopedEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar, InviteTokenServiceFactory.InviteModeLegacySigned);
        using var legacyModeOptIn = new ScopedEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteModeEnvVar, "1");
        using var signingKey = new ScopedEnvironmentVariable(InviteTokenServiceFactory.InviteSigningKeyEnvVar, null);
        using var legacyOptIn = new ScopedEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar, "1");

        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_280_000);
        var request = new InviteTokenCreateRequest(
            IssuerAddress: new PeerAddress("nlink-helper.legacy.1111"),
            TargetAddress: new PeerAddress("nlink-helpee.legacy.2222"),
            SessionId: new SessionId("sess_legacy_optin"),
            Capabilities: InviteCapabilities.Chat,
            Lifetime: TimeSpan.FromMinutes(2));

        var factory = InviteTokenServiceFactory.CreateInviteTokenFactory();
        var created = factory.Create(request, nowUtc);
        Assert.True(created.IsSuccess, created.Message);

        var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        var validated = validator.Validate(created.Token, nowUtc.AddSeconds(1));
        Assert.True(validated.IsSuccess, validated.Message);
        Assert.NotNull(validated.Invite);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteTokenServiceFactory_DefaultMode_UsesIssuedSecretInvites_WithoutSigningKey()
    {
        using var inviteMode = new ScopedEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar, null);
        using var signingKey = new ScopedEnvironmentVariable(InviteTokenServiceFactory.InviteSigningKeyEnvVar, null);
        using var legacyOptIn = new ScopedEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar, null);

        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_290_000);
        var request = new InviteTokenCreateRequest(
            IssuerAddress: new PeerAddress("nlink-helper.configured.1111"),
            TargetAddress: new PeerAddress("nlink-helpee.configured.2222"),
            SessionId: new SessionId("sess_configured_key"),
            Capabilities: InviteCapabilities.Chat,
            Lifetime: TimeSpan.FromMinutes(2));

        var factory = InviteTokenServiceFactory.CreateInviteTokenFactory();
        var created = factory.Create(request, nowUtc);
        Assert.True(created.IsSuccess, created.Message);
        Assert.NotNull(created.Token);

        var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        var validated = validator.Validate(created.Token, nowUtc.AddSeconds(1));
        Assert.True(validated.IsSuccess, validated.Message);
        Assert.NotNull(validated.Invite);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void InviteIssuedSecretToken_InspectOnly_DoesNotConsume_AndPersistentStore_DoesNotContainRawProof()
    {
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_295_000);
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-issued-invite-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var storePath = Path.Combine(tempDir, "invite-security-store.json");

        try
        {
            var request = new InviteTokenCreateRequest(
                IssuerAddress: new PeerAddress("nlink-helper.issued.1111"),
                TargetAddress: new PeerAddress("nlink-helpee.issued.2222"),
                SessionId: new SessionId("sess_issued_store"),
                Capabilities: InviteCapabilities.Chat,
                Lifetime: TimeSpan.FromMinutes(2));

            var codec = new InviteTokenCodec();
            var store = new PersistentInviteSecurityStore(new InviteSecurityStoreOptions { FilePath = storePath });
            var factory = new IssuedSecretInviteTokenFactory(codec, store);
            var validator = new IssuedSecretInviteTokenValidator(codec, new InviteExpiryValidator(), store);

            var created = factory.Create(request, nowUtc);
            Assert.True(created.IsSuccess, created.Message);
            Assert.NotNull(created.Token);

            var tokenParts = created.Token!.Split('.', StringSplitOptions.None);
            Assert.Equal(3, tokenParts.Length);
            var rawProofSegment = tokenParts[2];
            var proofHashKey = ComputeProofHashKey(DecodeBase64Url(rawProofSegment));

            var persisted = File.ReadAllText(storePath);
            Assert.Contains("issuedInvitesByProofHash", persisted, StringComparison.Ordinal);
            Assert.Contains(proofHashKey, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(rawProofSegment, persisted, StringComparison.Ordinal);

            var firstInspect = validator.Validate(created.Token, nowUtc.AddSeconds(1), InviteValidationMode.InspectOnly);
            var secondInspect = validator.Validate(created.Token, nowUtc.AddSeconds(2), InviteValidationMode.InspectOnly);
            var firstConsume = validator.Validate(created.Token, nowUtc.AddSeconds(3), InviteValidationMode.ConsumeIfValid);
            var secondConsume = validator.Validate(created.Token, nowUtc.AddSeconds(4), InviteValidationMode.ConsumeIfValid);

            Assert.True(firstInspect.IsSuccess, firstInspect.Message);
            Assert.True(secondInspect.IsSuccess, secondInspect.Message);
            Assert.True(firstConsume.IsSuccess, firstConsume.Message);
            Assert.False(secondConsume.IsSuccess);
            Assert.Equal(InviteValidationResult.ReplayDetected, secondConsume.Result);
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
    public void InviteTokenServiceFactory_DefaultMode_TamperedCapabilities_AreRejectedOnConsume()
    {
        using var inviteMode = new ScopedEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar, null);
        using var signingKey = new ScopedEnvironmentVariable(InviteTokenServiceFactory.InviteSigningKeyEnvVar, null);
        using var legacyOptIn = new ScopedEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar, null);

        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_300_000);
        var request = new InviteTokenCreateRequest(
            IssuerAddress: new PeerAddress("nlink-helper.default.1111"),
            TargetAddress: new PeerAddress("nlink-helpee.default.2222"),
            SessionId: new SessionId("sess_default_tamper"),
            Capabilities: InviteCapabilities.Chat,
            Lifetime: TimeSpan.FromMinutes(2));

        var factory = InviteTokenServiceFactory.CreateInviteTokenFactory();
        var created = factory.Create(request, nowUtc);
        Assert.True(created.IsSuccess, created.Message);
        Assert.NotNull(created.Token);

        var parts = created.Token!.Split('.', StringSplitOptions.None);
        Assert.Equal(3, parts.Length);
        var payloadJson = Encoding.UTF8.GetString(DecodeBase64Url(parts[1]));
        var tamperedJson = payloadJson.Replace("\"cap\":1", "\"cap\":7", StringComparison.Ordinal);
        var tamperedPayload = EncodeBase64Url(Encoding.UTF8.GetBytes(tamperedJson));
        var tamperedToken = $"{parts[0]}.{tamperedPayload}.{parts[2]}";

        var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        var inspected = validator.Validate(tamperedToken, nowUtc.AddSeconds(1), InviteValidationMode.InspectOnly);
        Assert.True(inspected.IsSuccess, inspected.Message);

        var consumed = validator.Validate(tamperedToken, nowUtc.AddSeconds(1), InviteValidationMode.ConsumeIfValid);
        Assert.False(consumed.IsSuccess);
        Assert.Equal(InviteValidationResult.InvalidSignature, consumed.Result);
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

    private static string ComputeProofHashKey(byte[] proofBytes)
    {
        return EncodeBase64Url(SHA256.HashData(proofBytes));
    }

    private sealed class ScopedEnvironmentVariable : IDisposable
    {
        private readonly string name;
        private readonly string? previousValue;

        public ScopedEnvironmentVariable(string name, string? value)
        {
            this.name = name;
            previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(name, previousValue);
        }
    }
}
