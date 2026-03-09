using System.Text;

namespace NLink.Core.SessionConnect;

public static class InviteTokenServiceFactory
{
    public const string InviteModeEnvVar = "NLINK_INVITE_MODE";
    public const string InviteModeIssuedSecret = "issued_secret";
    public const string InviteModeLegacySigned = "legacy_signed";
    public const string AllowInsecureLegacyInviteModeEnvVar = "NLINK_ALLOW_INSECURE_LEGACY_INVITE_MODE";
    public const string InviteSigningKeyEnvVar = "NLINK_INVITE_SIGNING_KEY";
    public const string AllowInsecureLegacyInviteSigningEnvVar = "NLINK_ALLOW_INSECURE_LEGACY_INVITE_SIGNING";
    public const string InviteSecurityStorePathEnvVar = "NLINK_INVITE_SECURITY_STORE_PATH";
    public const string DefaultInviteSigningKey = "nlink-invite-signing-key-v1";
    private static readonly PersistentInviteSecurityStore SharedInviteSecurityStore = new(CreateInviteSecurityStoreOptions());

    public static IConnectInputResolver CreateDefaultResolver()
    {
        var codec = CreateInviteTokenCodec();
        return new ConnectInputResolver(codec, new InviteExpiryValidator());
    }

    public static IInviteTokenFactory CreateInviteTokenFactory()
    {
        var codec = CreateInviteTokenCodec();
        return IsLegacySignedInviteModeEnabled()
            ? new InviteTokenFactory(codec, CreateInviteSignatureService(), SharedInviteSecurityStore)
            : new IssuedSecretInviteTokenFactory(codec, SharedInviteSecurityStore);
    }

    public static IInviteTokenValidator CreateInviteTokenValidator()
    {
        var codec = CreateInviteTokenCodec();
        return CreateInviteTokenValidator(codec);
    }

    public static IInviteTokenCodec CreateInviteTokenCodec()
    {
        return new InviteTokenCodec();
    }

    public static IInviteValidationThrottle CreateInviteValidationThrottle()
    {
        return SharedInviteSecurityStore;
    }

    public static IInviteSignatureService CreateInviteSignatureService()
    {
        EnsureLegacyInviteModeAllowed();
        return new HmacSha256InviteSignatureService(ReadInviteSigningKeyMaterial());
    }

    public static string GetInviteMode()
    {
        var raw = Environment.GetEnvironmentVariable(InviteModeEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return InviteModeIssuedSecret;
        }

        return raw.Trim() switch
        {
            InviteModeLegacySigned => InviteModeLegacySigned,
            InviteModeIssuedSecret => InviteModeIssuedSecret,
            _ => InviteModeIssuedSecret,
        };
    }

    public static bool IsLegacySignedInviteModeEnabled()
    {
        var mode = GetInviteMode();
        if (!string.Equals(mode, InviteModeLegacySigned, StringComparison.Ordinal))
        {
            return false;
        }

        EnsureLegacyInviteModeAllowed();
        return true;
    }

    public static bool IsLegacyInviteModeExplicitlyAllowed()
    {
#if DEBUG
        return true;
#else
        return ReadBoolEnvironmentOverride(AllowInsecureLegacyInviteModeEnvVar);
#endif
    }

    public static byte[] ReadInviteSigningKeyMaterial()
    {
        var configured = Environment.GetEnvironmentVariable(InviteSigningKeyEnvVar);
        var effective = string.IsNullOrWhiteSpace(configured) ? DefaultInviteSigningKey : configured.Trim();

#if !DEBUG
        if (string.Equals(effective, DefaultInviteSigningKey, StringComparison.Ordinal) &&
            !IsLegacyInviteSigningExplicitlyAllowed())
        {
            throw new InvalidOperationException(
                $"Invite signing is not configured for release use. Set {InviteSigningKeyEnvVar} to a non-default production value. " +
                $"{AllowInsecureLegacyInviteSigningEnvVar}=1 may be used only for internal/dev legacy invite mode.");
        }
#endif

        return Encoding.UTF8.GetBytes(effective);
    }

    private static void EnsureLegacyInviteModeAllowed()
    {
#if !DEBUG
        if (!IsLegacyInviteModeExplicitlyAllowed())
        {
            throw new InvalidOperationException(
                $"Legacy invite mode is disabled for release use. Leave {InviteModeEnvVar} unset or set it to {InviteModeIssuedSecret}. " +
                $"{AllowInsecureLegacyInviteModeEnvVar}=1 may be used only for internal/dev legacy-compatibility testing.");
        }
#endif
    }

    private static bool IsLegacyInviteSigningExplicitlyAllowed()
    {
        return ReadBoolEnvironmentOverride(AllowInsecureLegacyInviteSigningEnvVar);
    }

    private static IInviteTokenValidator CreateInviteTokenValidator(IInviteTokenCodec codec)
    {
        return IsLegacySignedInviteModeEnabled()
            ? new InviteTokenValidator(
                codec,
                CreateInviteSignatureService(),
                new InviteExpiryValidator(),
                SharedInviteSecurityStore,
                SharedInviteSecurityStore)
            : new IssuedSecretInviteTokenValidator(
                codec,
                new InviteExpiryValidator(),
                SharedInviteSecurityStore);
    }

    private static InviteSecurityStoreOptions CreateInviteSecurityStoreOptions()
    {
        var configuredPath = Environment.GetEnvironmentVariable(InviteSecurityStorePathEnvVar);
        return new InviteSecurityStoreOptions
        {
            FilePath = string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath.Trim(),
        };
    }

    private static bool ReadBoolEnvironmentOverride(string variableName)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim() switch
        {
            "1" => true,
            "true" => true,
            "TRUE" => true,
            "yes" => true,
            "YES" => true,
            "on" => true,
            "ON" => true,
            _ => false,
        };
    }
}
