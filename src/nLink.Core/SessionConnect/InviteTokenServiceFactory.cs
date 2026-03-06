using System.Text;

namespace NLink.Core.SessionConnect;

public static class InviteTokenServiceFactory
{
    public const string InviteSigningKeyEnvVar = "NLINK_INVITE_SIGNING_KEY";
    public const string InviteSecurityStorePathEnvVar = "NLINK_INVITE_SECURITY_STORE_PATH";
    public const string DefaultInviteSigningKey = "nlink-invite-signing-key-v1";
    private static readonly PersistentInviteSecurityStore SharedInviteSecurityStore = new(CreateInviteSecurityStoreOptions());

    public static IConnectInputResolver CreateDefaultResolver()
    {
        var codec = CreateInviteTokenCodec();
        return new ConnectInputResolver(codec, CreateInviteTokenValidator(codec));
    }

    public static IInviteTokenFactory CreateInviteTokenFactory()
    {
        var codec = CreateInviteTokenCodec();
        return new InviteTokenFactory(codec, CreateInviteSignatureService(), SharedInviteSecurityStore);
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
        return new HmacSha256InviteSignatureService(ReadInviteSigningKeyMaterial());
    }

    public static byte[] ReadInviteSigningKeyMaterial()
    {
        var configured = Environment.GetEnvironmentVariable(InviteSigningKeyEnvVar);
        var effective = string.IsNullOrWhiteSpace(configured) ? DefaultInviteSigningKey : configured.Trim();
        return Encoding.UTF8.GetBytes(effective);
    }

    private static IInviteTokenValidator CreateInviteTokenValidator(IInviteTokenCodec codec)
    {
        return new InviteTokenValidator(
            codec,
            CreateInviteSignatureService(),
            new InviteExpiryValidator(),
            SharedInviteSecurityStore,
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
}
