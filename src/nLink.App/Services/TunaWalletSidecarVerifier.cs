using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLink.Infra.Nkn;

namespace NLink.App.Services;

internal sealed record TunaWalletVerifierAvailability(
    bool IsAvailable,
    string Status,
    string? SidecarPath)
{
    public string? ManifestPath { get; init; }

    public string? ExpectedSidecarVersion { get; init; } = NknTunaSidecarCompatibility.ExpectedSidecarVersion;

    public string? ActualSidecarVersion { get; init; }

    public string? ExpectedRuntime { get; init; } = "win-x64";

    public string? ActualRuntime { get; init; }

    public int? ExpectedAppProtocolVersion { get; init; } = NknTunaSidecarCompatibility.AppProtocolVersion;

    public int? ActualAppProtocolVersion { get; init; }

    public int? ExpectedFrameProtocolVersion { get; init; } = NknTunaSidecarFrameProtocol.ProtocolVersion;

    public int? ActualFrameProtocolVersion { get; init; }

    public string? ManifestStatus { get; init; }

    public string? Detail { get; init; }
}

internal interface ITunaWalletVerifier
{
    TunaWalletVerifierAvailability GetAvailability();

    Task<TunaWalletValidationResult> ValidateAsync(string walletPath, char[] password, CancellationToken ct);
}

internal sealed class TunaWalletSidecarVerifier : ITunaWalletVerifier
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(35);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<NknTunaAccelerationOptions> optionsProvider;
    private readonly Func<string> currentDirectoryProvider;
    private readonly TimeSpan timeout;

    public TunaWalletSidecarVerifier(
        Func<NknTunaAccelerationOptions>? optionsProvider = null,
        Func<string>? currentDirectoryProvider = null,
        TimeSpan? timeout = null)
    {
        this.optionsProvider = optionsProvider ?? NknTunaAccelerationOptions.Load;
        this.currentDirectoryProvider = currentDirectoryProvider ?? (() => Environment.CurrentDirectory);
        this.timeout = timeout ?? DefaultTimeout;
    }

    public TunaWalletVerifierAvailability GetAvailability()
    {
        var path = ResolveSidecarPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Availability(
                isAvailable: false,
                status: "sidecar_missing",
                sidecarPath: path,
                manifestPath: ResolveSidecarManifestPath(path),
                detail: "Missing: expected nlink-tuna-sidecar.exe.");
        }

        var manifestPath = ResolveSidecarManifestPath(path);
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return Availability(
                isAvailable: false,
                status: "sidecar_manifest_missing",
                sidecarPath: path,
                manifestPath: manifestPath,
                detail: "Manifest invalid: tuna-sidecar-manifest.json is missing.");
        }

        TunaSidecarManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<TunaSidecarManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions) ?? new TunaSidecarManifest();
        }
        catch
        {
            return Availability(
                isAvailable: false,
                status: "sidecar_manifest_invalid",
                sidecarPath: path,
                manifestPath: manifestPath,
                detail: "Manifest invalid: tuna-sidecar-manifest.json could not be read.");
        }

        var expectedVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion;
        if (manifest.ManifestVersion != 1)
        {
            return Availability(false, "sidecar_manifest_invalid", path, manifestPath, manifest, "Manifest invalid: unsupported manifest version.");
        }

        if (!string.Equals(manifest.Runtime, "win-x64", StringComparison.OrdinalIgnoreCase))
        {
            return Availability(false, "sidecar_manifest_invalid", path, manifestPath, manifest, $"Manifest invalid: expected win-x64 runtime, found {FirstNonEmpty(manifest.Runtime, "(unknown)")}.");
        }

        if (!string.Equals(manifest.AppVersion, expectedVersion, StringComparison.OrdinalIgnoreCase) ||
            !NknTunaSidecarCompatibility.IsCompatibleSidecarVersion(manifest.SidecarVersion))
        {
            return Availability(false, "sidecar_version_mismatch", path, manifestPath, manifest, $"Wrong version: expected {expectedVersion}, found {FirstNonEmpty(manifest.SidecarVersion, manifest.AppVersion, "(unknown)")}.");
        }

        if (manifest.AppProtocolVersion != NknTunaSidecarCompatibility.AppProtocolVersion ||
            manifest.FrameProtocolVersion != NknTunaSidecarFrameProtocol.ProtocolVersion)
        {
            return Availability(false, "sidecar_protocol_mismatch", path, manifestPath, manifest, "Protocol mismatch: packaged Tuna sidecar is not compatible with this app.");
        }

        if (string.IsNullOrWhiteSpace(manifest.SidecarExeSha256) ||
            !string.Equals(ComputeSha256(path), manifest.SidecarExeSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Availability(false, "sidecar_manifest_hash_mismatch", path, manifestPath, manifest, "Manifest invalid: sidecar hash does not match the packaged manifest.");
        }

        return Availability(true, "available", path, manifestPath, manifest, $"Available: {Path.GetFileName(path)} {manifest.SidecarVersion}.");
    }

    public async Task<TunaWalletValidationResult> ValidateAsync(string walletPath, char[] password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(walletPath))
        {
            return TunaWalletValidationResult.Fail("wallet_not_linked");
        }

        var walletFile = Path.GetFileName(walletPath);
        if (!File.Exists(walletPath))
        {
            return TunaWalletValidationResult.Fail("wallet_file_missing", walletFile);
        }

        if (password is null || password.Length == 0)
        {
            return TunaWalletValidationResult.Fail("password_required", walletFile);
        }

        var availability = GetAvailability();
        if (!availability.IsAvailable || string.IsNullOrWhiteSpace(availability.SidecarPath))
        {
            return TunaWalletValidationResult.Fail(availability.Status, walletFile);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = availability.SidecarPath,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("wallet-status");
            process.StartInfo.ArgumentList.Add("--wallet");
            process.StartInfo.ArgumentList.Add(walletPath);
            process.StartInfo.ArgumentList.Add("--password-stdin");
            process.StartInfo.ArgumentList.Add("--jsonl");

            if (!process.Start())
            {
                return TunaWalletValidationResult.Fail("sidecar_start_failed", walletFile);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            var passwordText = new string(password);
            try
            {
                await process.StandardInput.WriteLineAsync(passwordText.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            }
            finally
            {
                process.StandardInput.Close();
                passwordText = string.Empty;
            }

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (TryParseWalletReady(stdout, out var result))
            {
                return result;
            }

            var reason = TryParseSidecarError(stdout) ?? TryParseSidecarError(stderr) ?? $"sidecar_exit_{process.ExitCode}";
            return TunaWalletValidationResult.Fail(SanitizeReason(reason, walletPath), walletFile);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TunaWalletValidationResult.Fail("balance_lookup_timeout", walletFile);
        }
        catch (Exception ex)
        {
            return TunaWalletValidationResult.Fail(SanitizeReason(ex.GetType().Name, walletPath), walletFile);
        }
    }

    private string? ResolveSidecarPath()
    {
        var configured = optionsProvider().SidecarExePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        foreach (var root in EnumerateSearchRoots())
        {
            foreach (var relative in new[]
                     {
                         Path.Combine("tuna", "win-x64", "nlink-tuna-sidecar.exe"),
                         Path.Combine("tools", "nkn-tuna-sidecar", "nlink-tuna-sidecar.exe"),
                         Path.Combine("artifacts", "tuna-sidecar", "nlink-tuna-sidecar.exe"),
                         "nlink-tuna-sidecar.exe",
                     })
            {
                var candidate = Path.GetFullPath(Path.Combine(root, relative));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? ResolveSidecarManifestPath(string? sidecarPath)
        => string.IsNullOrWhiteSpace(sidecarPath)
            ? null
            : Path.Combine(Path.GetDirectoryName(sidecarPath) ?? string.Empty, "tuna-sidecar-manifest.json");

    private string[] EnumerateSearchRoots()
    {
        var roots = new[]
        {
            currentDirectoryProvider(),
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
        };
        return roots;
    }

    private static bool TryParseWalletReady(string text, out TunaWalletValidationResult result)
    {
        foreach (var line in EnumerateLines(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("event", out var eventValue) ||
                    !string.Equals(eventValue.GetString(), "wallet_ready", StringComparison.Ordinal))
                {
                    continue;
                }

                var walletFile = root.TryGetProperty("walletFile", out var fileValue) ? fileValue.GetString() : null;
                var address = root.TryGetProperty("walletAddress", out var addressValue) ? addressValue.GetString() : null;
                var balance = root.TryGetProperty("balanceNkn", out var balanceValue) ? balanceValue.GetString() : null;
                var compatibility = NknTunaSidecarCompatibility.Validate(
                    TryGetInt(root, "appProtocolVersion"),
                    TryGetInt(root, "frameProtocolVersion"),
                    TryGetString(root, "sidecarVersion"));
                if (!compatibility.IsCompatible)
                {
                    result = TunaWalletValidationResult.Fail(compatibility.Reason, walletFile);
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(walletFile) &&
                    !string.IsNullOrWhiteSpace(address) &&
                    !string.IsNullOrWhiteSpace(balance))
                {
                    result = TunaWalletValidationResult.Ok(walletFile!, address!, balance!);
                    return true;
                }
            }
            catch
            {
                // Ignore unrelated sidecar chatter.
            }
        }

        result = TunaWalletValidationResult.Fail("wallet_ready_missing");
        return false;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int? TryGetInt(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value)
            ? value
            : null;

    private static string? TryParseSidecarError(string text)
    {
        foreach (var line in EnumerateLines(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("event", out var eventValue) &&
                    string.Equals(eventValue.GetString(), "error", StringComparison.Ordinal) &&
                    root.TryGetProperty("reason", out var reasonValue))
                {
                    return reasonValue.GetString();
                }
            }
            catch
            {
                // Ignore non-JSON diagnostics.
            }
        }

        return null;
    }

    private static string[] EnumerateLines(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string SanitizeReason(string reason, string walletPath)
    {
        var safe = reason;
        if (!string.IsNullOrWhiteSpace(walletPath))
        {
            safe = safe.Replace(walletPath, Path.GetFileName(walletPath), StringComparison.OrdinalIgnoreCase);
        }

        return DiagnosticsRedactor.Redact(safe);
    }

    private static TunaWalletVerifierAvailability Availability(
        bool isAvailable,
        string status,
        string? sidecarPath,
        string? manifestPath,
        string detail)
        => new(isAvailable, status, sidecarPath)
        {
            ManifestPath = manifestPath,
            ManifestStatus = status,
            Detail = detail,
        };

    private static TunaWalletVerifierAvailability Availability(
        bool isAvailable,
        string status,
        string sidecarPath,
        string manifestPath,
        TunaSidecarManifest manifest,
        string detail)
        => new(isAvailable, status, sidecarPath)
        {
            ManifestPath = manifestPath,
            ExpectedSidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
            ActualSidecarVersion = FirstNonEmpty(manifest.SidecarVersion, manifest.AppVersion),
            ExpectedRuntime = "win-x64",
            ActualRuntime = manifest.Runtime,
            ExpectedAppProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
            ActualAppProtocolVersion = manifest.AppProtocolVersion,
            ExpectedFrameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
            ActualFrameProtocolVersion = manifest.FrameProtocolVersion,
            ManifestStatus = status,
            Detail = detail,
        };

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private sealed class TunaSidecarManifest
    {
        public int ManifestVersion { get; set; }

        public string? AppVersion { get; set; }

        public string? SidecarVersion { get; set; }

        public string? Runtime { get; set; }

        public int AppProtocolVersion { get; set; }

        public int FrameProtocolVersion { get; set; }

        public string? SidecarExeSha256 { get; set; }
    }
}
