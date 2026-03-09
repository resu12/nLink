using System.Runtime.CompilerServices;
using NLink.App.Configuration;
using NLink.Core.SessionConnect;

namespace NLink.SmokeTests.TestUtilities;

internal static class LegacyInviteSigningTestBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar)))
        {
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteSigningEnvVar, "1");
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar)))
        {
            Environment.SetEnvironmentVariable(AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, "1");
        }
    }
}
