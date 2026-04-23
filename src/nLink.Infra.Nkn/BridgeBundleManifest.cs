using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NLink.Infra.Nkn;

internal sealed class BridgeBundleManifest
{
    [JsonPropertyName("manifestVersion")]
    public int ManifestVersion { get; init; }

    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; init; }

    [JsonPropertyName("buildTimestampUtc")]
    public string? BuildTimestampUtc { get; init; }

    [JsonPropertyName("bridgeScriptSha256")]
    public string? BridgeScriptSha256 { get; init; }

    [JsonPropertyName("nodeVersion")]
    public string? NodeVersion { get; init; }

    [JsonPropertyName("capabilities")]
    public BridgeBundleManifestCapabilities? Capabilities { get; init; }
}

internal sealed class BridgeBundleManifestCapabilities
{
    [JsonPropertyName("ownerPidWatchdog")]
    public bool OwnerPidWatchdog { get; init; }

    [JsonPropertyName("killOnCloseJob")]
    public bool KillOnCloseJob { get; init; }
}

internal sealed class BridgeBundleIdentity
{
    private BridgeBundleIdentity(
        string bridgeScriptPath,
        string manifestPath,
        string actualScriptSha256,
        string manifestStatus,
        string manifestReason,
        int? manifestVersion,
        string? appVersion,
        string? buildTimestampUtc,
        string? manifestScriptSha256,
        string? nodeVersion,
        bool ownerPidWatchdog,
        bool killOnCloseJob)
    {
        BridgeScriptPath = bridgeScriptPath;
        ManifestPath = manifestPath;
        ActualScriptSha256 = actualScriptSha256;
        ManifestStatus = manifestStatus;
        ManifestReason = manifestReason;
        ManifestVersion = manifestVersion;
        AppVersion = appVersion;
        BuildTimestampUtc = buildTimestampUtc;
        ManifestScriptSha256 = manifestScriptSha256;
        NodeVersion = nodeVersion;
        OwnerPidWatchdog = ownerPidWatchdog;
        KillOnCloseJob = killOnCloseJob;
    }

    public string BridgeScriptPath { get; }

    public string ManifestPath { get; }

    public string ActualScriptSha256 { get; }

    public string ManifestStatus { get; }

    public string ManifestReason { get; }

    public int? ManifestVersion { get; }

    public string? AppVersion { get; }

    public string? BuildTimestampUtc { get; }

    public string? ManifestScriptSha256 { get; }

    public string? NodeVersion { get; }

    public bool OwnerPidWatchdog { get; }

    public bool KillOnCloseJob { get; }

    public bool HasMismatch => !string.Equals(ManifestStatus, "ok", StringComparison.Ordinal);

    public static BridgeBundleIdentity Load(string bridgeScriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeScriptPath);

        var resolvedScriptPath = Path.GetFullPath(bridgeScriptPath);
        var manifestPath = Path.Combine(Path.GetDirectoryName(resolvedScriptPath) ?? Environment.CurrentDirectory, "bridge-manifest.json");
        var actualScriptSha256 = ComputeSha256Hex(resolvedScriptPath);

        if (!File.Exists(manifestPath))
        {
            return Create(
                manifestStatus: "manifest_missing",
                manifestReason: "missing",
                manifestVersion: null,
                appVersion: null,
                buildTimestampUtc: null,
                manifestScriptSha256: null,
                nodeVersion: null,
                ownerPidWatchdog: false,
                killOnCloseJob: false);
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<BridgeBundleManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            if (manifest is null)
            {
                return Create(
                    manifestStatus: "manifest_invalid",
                    manifestReason: "null_document",
                    manifestVersion: null,
                    appVersion: null,
                    buildTimestampUtc: null,
                    manifestScriptSha256: null,
                    nodeVersion: null,
                    ownerPidWatchdog: false,
                    killOnCloseJob: false);
            }

            var manifestScriptSha256 = NormalizeHash(manifest.BridgeScriptSha256);
            var ownerPidWatchdog = manifest.Capabilities?.OwnerPidWatchdog ?? false;
            var killOnCloseJob = manifest.Capabilities?.KillOnCloseJob ?? false;

            if (manifest.ManifestVersion <= 0 ||
                string.IsNullOrWhiteSpace(manifestScriptSha256) ||
                string.IsNullOrWhiteSpace(manifest.NodeVersion))
            {
                return Create(
                    manifestStatus: "manifest_invalid",
                    manifestReason: "missing_required_fields",
                    manifestVersion: manifest.ManifestVersion <= 0 ? null : manifest.ManifestVersion,
                    appVersion: NormalizeText(manifest.AppVersion),
                    buildTimestampUtc: NormalizeText(manifest.BuildTimestampUtc),
                    manifestScriptSha256: manifestScriptSha256,
                    nodeVersion: NormalizeText(manifest.NodeVersion),
                    ownerPidWatchdog: ownerPidWatchdog,
                    killOnCloseJob: killOnCloseJob);
            }

            if (!ownerPidWatchdog || !killOnCloseJob)
            {
                return Create(
                    manifestStatus: "capability_mismatch",
                    manifestReason: "required_capability_missing",
                    manifestVersion: manifest.ManifestVersion,
                    appVersion: NormalizeText(manifest.AppVersion),
                    buildTimestampUtc: NormalizeText(manifest.BuildTimestampUtc),
                    manifestScriptSha256: manifestScriptSha256,
                    nodeVersion: NormalizeText(manifest.NodeVersion),
                    ownerPidWatchdog: ownerPidWatchdog,
                    killOnCloseJob: killOnCloseJob);
            }

            if (!string.Equals(actualScriptSha256, manifestScriptSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Create(
                    manifestStatus: "script_hash_mismatch",
                    manifestReason: "bridge_script_sha256_mismatch",
                    manifestVersion: manifest.ManifestVersion,
                    appVersion: NormalizeText(manifest.AppVersion),
                    buildTimestampUtc: NormalizeText(manifest.BuildTimestampUtc),
                    manifestScriptSha256: manifestScriptSha256,
                    nodeVersion: NormalizeText(manifest.NodeVersion),
                    ownerPidWatchdog: ownerPidWatchdog,
                    killOnCloseJob: killOnCloseJob);
            }

            return Create(
                manifestStatus: "ok",
                manifestReason: "ok",
                manifestVersion: manifest.ManifestVersion,
                appVersion: NormalizeText(manifest.AppVersion),
                buildTimestampUtc: NormalizeText(manifest.BuildTimestampUtc),
                manifestScriptSha256: manifestScriptSha256,
                nodeVersion: NormalizeText(manifest.NodeVersion),
                ownerPidWatchdog: ownerPidWatchdog,
                killOnCloseJob: killOnCloseJob);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Create(
                manifestStatus: "manifest_malformed",
                manifestReason: ex.GetType().Name,
                manifestVersion: null,
                appVersion: null,
                buildTimestampUtc: null,
                manifestScriptSha256: null,
                nodeVersion: null,
                ownerPidWatchdog: false,
                killOnCloseJob: false);
        }

        BridgeBundleIdentity Create(
            string manifestStatus,
            string manifestReason,
            int? manifestVersion,
            string? appVersion,
            string? buildTimestampUtc,
            string? manifestScriptSha256,
            string? nodeVersion,
            bool ownerPidWatchdog,
            bool killOnCloseJob)
        {
            return new BridgeBundleIdentity(
                bridgeScriptPath: resolvedScriptPath,
                manifestPath: manifestPath,
                actualScriptSha256: actualScriptSha256,
                manifestStatus: manifestStatus,
                manifestReason: manifestReason,
                manifestVersion: manifestVersion,
                appVersion: appVersion,
                buildTimestampUtc: buildTimestampUtc,
                manifestScriptSha256: manifestScriptSha256,
                nodeVersion: nodeVersion,
                ownerPidWatchdog: ownerPidWatchdog,
                killOnCloseJob: killOnCloseJob);
        }
    }

    public string BuildStructuredLogFields()
    {
        return
            $"; bridge_script_path={BridgeScriptPath}" +
            $"; bridge_manifest_path={ManifestPath}" +
            $"; manifest_status={ManifestStatus}" +
            $"; manifest_reason={ManifestReason}" +
            $"; manifest_version={FormatNullableInt(ManifestVersion)}" +
            $"; app_version={FormatNullable(AppVersion)}" +
            $"; build_timestamp_utc={FormatNullable(BuildTimestampUtc)}" +
            $"; bridge_script_sha256={ActualScriptSha256}" +
            $"; manifest_bridge_script_sha256={FormatNullable(ManifestScriptSha256)}" +
            $"; node_version={FormatNullable(NodeVersion)}" +
            $"; owner_pid_watchdog={FormatBool(OwnerPidWatchdog)}" +
            $"; kill_on_close_job={FormatBool(KillOnCloseJob)}";
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string? NormalizeHash(string? value)
    {
        var trimmed = NormalizeText(value);
        return trimmed?.ToLowerInvariant();
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static string FormatNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string FormatNullableInt(int? value)
    {
        return value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)";
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }
}
