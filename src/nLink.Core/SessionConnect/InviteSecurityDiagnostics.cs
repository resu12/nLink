namespace NLink.Core.SessionConnect;

public readonly record struct InviteSecurityStatus(
    string Mode,
    string SigningConfiguration,
    string PublicInviteFlow,
    bool ReleaseReady,
    string Warning);

public static class InviteSecurityDiagnostics
{
    public const string AllowInsecureUnboundPublicInvitesEnvVar = "NLINK_ALLOW_INSECURE_UNBOUND_PUBLIC_INVITES";

    public static bool AreUnboundPublicInvitesAllowed()
    {
#if DEBUG
        return true;
#else
        return ReadEnabled(AllowInsecureUnboundPublicInvitesEnvVar);
#endif
    }

    public static bool RequiresBoundHelperForIssuedSecretInvites()
    {
        return !IsOperationalLegacyInviteModeEnabled() &&
               !AreUnboundPublicInvitesAllowed();
    }

    public static InviteSecurityStatus Snapshot()
    {
        var inviteMode = InviteTokenServiceFactory.GetInviteMode();
        var publicInviteFlow = BuildPublicInviteFlow();
        var signingConfiguration = BuildSigningConfiguration(inviteMode);
        var warning = BuildWarning(inviteMode, signingConfiguration, publicInviteFlow);
        var releaseReady = BuildReleaseReady(inviteMode, publicInviteFlow, warning);

        return new InviteSecurityStatus(
            Mode: inviteMode == InviteTokenServiceFactory.InviteModeLegacySigned
                ? "legacy_shared_secret_invites"
                : "issued_one_time_secret_invites",
            SigningConfiguration: signingConfiguration,
            PublicInviteFlow: publicInviteFlow,
            ReleaseReady: releaseReady,
            Warning: warning);
    }

    private static string ReadEffectiveSigningKey()
    {
        var configured = Environment.GetEnvironmentVariable(InviteTokenServiceFactory.InviteSigningKeyEnvVar);
        return string.IsNullOrWhiteSpace(configured)
            ? InviteTokenServiceFactory.DefaultInviteSigningKey
            : configured.Trim();
    }

    private static string BuildSigningConfiguration(string inviteMode)
    {
        if (!string.Equals(inviteMode, InviteTokenServiceFactory.InviteModeLegacySigned, StringComparison.Ordinal))
        {
            return "not_used_in_issued_secret_mode";
        }

        var effectiveSigningKey = ReadEffectiveSigningKey();
        var legacySigningOverride = ReadEnabled(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar);
        if (string.Equals(effectiveSigningKey, InviteTokenServiceFactory.DefaultInviteSigningKey, StringComparison.Ordinal))
        {
            return legacySigningOverride
                ? "default_key_allowed_by_override"
                : "default_key_disallowed";
        }

        return "custom_shared_secret_configured";
    }

    private static string BuildPublicInviteFlow()
    {
#if DEBUG
        return AreUnboundPublicInvitesAllowed()
            ? "unbound_public_invites_allowed_debug"
            : "verified_helper_required";
#else
        return AreUnboundPublicInvitesAllowed()
            ? "unbound_public_invites_allowed_by_override"
            : "verified_helper_required";
#endif
    }

    private static string BuildWarning(string inviteMode, string signingConfiguration, string publicInviteFlow)
    {
        if (string.Equals(inviteMode, InviteTokenServiceFactory.InviteModeLegacySigned, StringComparison.Ordinal) &&
            !InviteTokenServiceFactory.IsLegacyInviteModeExplicitlyAllowed())
        {
            return "Critical: legacy invite mode is requested without explicit internal override.";
        }

        if (string.Equals(inviteMode, InviteTokenServiceFactory.InviteModeLegacySigned, StringComparison.Ordinal) &&
            string.Equals(signingConfiguration, "default_key_allowed_by_override", StringComparison.Ordinal))
        {
            return "Critical: default legacy invite signing override is enabled.";
        }

        if (string.Equals(publicInviteFlow, "unbound_public_invites_allowed_debug", StringComparison.Ordinal) ||
            string.Equals(publicInviteFlow, "unbound_public_invites_allowed_by_override", StringComparison.Ordinal))
        {
            return "High: unbound public invites are enabled.";
        }

        return string.Equals(inviteMode, InviteTokenServiceFactory.InviteModeLegacySigned, StringComparison.Ordinal)
            ? "High: legacy invite compatibility mode is enabled by override."
            : "none";
    }

    private static bool BuildReleaseReady(string inviteMode, string publicInviteFlow, string warning)
    {
        return string.Equals(inviteMode, InviteTokenServiceFactory.InviteModeIssuedSecret, StringComparison.Ordinal) &&
               string.Equals(publicInviteFlow, "verified_helper_required", StringComparison.Ordinal) &&
               string.Equals(warning, "none", StringComparison.Ordinal);
    }

    private static bool IsOperationalLegacyInviteModeEnabled()
    {
        return string.Equals(InviteTokenServiceFactory.GetInviteMode(), InviteTokenServiceFactory.InviteModeLegacySigned, StringComparison.Ordinal) &&
               InviteTokenServiceFactory.IsLegacyInviteModeExplicitlyAllowed();
    }

    private static bool ReadEnabled(string variableName)
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
