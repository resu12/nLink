using System.Reflection;

namespace NLink.Infra.Nkn;

internal static class NknTunaSidecarCompatibility
{
    public const int AppProtocolVersion = 1;

    public static string ExpectedSidecarVersion { get; } = ResolveExpectedSidecarVersion();

    public static NknTunaSidecarCompatibilityResult Validate(
        int? appProtocolVersion,
        int? frameProtocolVersion,
        string? sidecarVersion)
    {
        if (appProtocolVersion != AppProtocolVersion)
        {
            return NknTunaSidecarCompatibilityResult.Reject("sidecar_app_protocol_mismatch", sidecarVersion);
        }

        if (frameProtocolVersion != NknTunaSidecarFrameProtocol.ProtocolVersion)
        {
            return NknTunaSidecarCompatibilityResult.Reject("sidecar_frame_protocol_mismatch", sidecarVersion);
        }

        var normalizedVersion = string.IsNullOrWhiteSpace(sidecarVersion) ? string.Empty : sidecarVersion.Trim();
        if (!IsCompatibleSidecarVersion(normalizedVersion))
        {
            return NknTunaSidecarCompatibilityResult.Reject("sidecar_version_mismatch", normalizedVersion);
        }

        return NknTunaSidecarCompatibilityResult.Accept(normalizedVersion);
    }

    public static bool IsCompatibleSidecarVersion(string? sidecarVersion)
    {
        var normalizedVersion = string.IsNullOrWhiteSpace(sidecarVersion) ? string.Empty : sidecarVersion.Trim();
        if (string.Equals(normalizedVersion, ExpectedSidecarVersion, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

#if DEBUG
        if (string.Equals(normalizedVersion, "dev", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
#endif

        return false;
    }

    private static string ResolveExpectedSidecarVersion()
    {
        var version = typeof(NknTunaSidecarCompatibility).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(version) ? "dev" : version.Trim();
    }
}

internal readonly record struct NknTunaSidecarCompatibilityResult(
    bool IsCompatible,
    string Reason,
    string SidecarVersion)
{
    public static NknTunaSidecarCompatibilityResult Accept(string sidecarVersion)
        => new(true, string.Empty, sidecarVersion);

    public static NknTunaSidecarCompatibilityResult Reject(string reason, string? sidecarVersion = null)
        => new(
            false,
            string.IsNullOrWhiteSpace(reason) ? "sidecar_version_mismatch" : reason.Trim(),
            string.IsNullOrWhiteSpace(sidecarVersion) ? string.Empty : sidecarVersion.Trim());
}
