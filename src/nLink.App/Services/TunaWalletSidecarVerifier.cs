using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLink.Infra.Nkn;

namespace NLink.App.Services;

internal sealed record TunaWalletVerifierAvailability(
    bool IsAvailable,
    string Status,
    string? SidecarPath);

internal interface ITunaWalletVerifier
{
    TunaWalletVerifierAvailability GetAvailability();

    Task<TunaWalletValidationResult> ValidateAsync(string walletPath, char[] password, CancellationToken ct);
}

internal sealed class TunaWalletSidecarVerifier : ITunaWalletVerifier
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(35);
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
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return new TunaWalletVerifierAvailability(true, "available", path);
        }

        return new TunaWalletVerifierAvailability(false, "sidecar_missing", path);
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
}
