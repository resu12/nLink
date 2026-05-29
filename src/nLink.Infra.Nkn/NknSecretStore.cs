using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NLink.Core.Diagnostics;

namespace NLink.Infra.Nkn;

internal static class NknSecretStore
{
    private const string SecretFileSuffix = ".seed";
    private const string SecretPurpose = "nLink.NknIdentitySeed.v2";
    private const int TransientSecretReadRetryCount = 5;
    private const int TransientSecretReadRetryDelayMs = 50;
    private static Func<IProtectedSeedBackend>? backendOverrideForTests;

    public static bool SupportsProtectedSeedStorage => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public static string GetSecretPath(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        return Path.GetFullPath(keyPath) + SecretFileSuffix;
    }

    public static void EnsureProtectedSeedStorageAvailable(string keyPath)
    {
        try
        {
            _ = ResolveBackendOrThrow(keyPath);
        }
        catch (Exception ex)
        {
            PersistenceDiagnostics.Record(
                domain: "nkn_secret_store",
                operation: "ensure_backend",
                severity: PersistenceDiagnosticSeverity.Error,
                outcome: PersistenceDiagnosticOutcome.FailedClosed,
                reason: ex.GetType().Name,
                userWarning: "Protected seed storage is unavailable.");
            throw;
        }
    }

    public static byte[]? TryLoadSeed(string keyPath)
    {
        try
        {
            return ResolveBackendOrThrow(keyPath).TryLoadSeed(keyPath);
        }
        catch (Exception ex)
        {
            PersistenceDiagnostics.Record(
                domain: "nkn_secret_store",
                operation: "load_seed",
                severity: PersistenceDiagnosticSeverity.Error,
                outcome: PersistenceDiagnosticOutcome.FailedClosed,
                reason: ex.GetType().Name,
                userWarning: "Protected seed storage could not be read.");
            throw;
        }
    }

    public static void SaveSeed(string keyPath, ReadOnlySpan<byte> seedBytes)
    {
        try
        {
            ResolveBackendOrThrow(keyPath).SaveSeed(keyPath, seedBytes);
        }
        catch (Exception ex)
        {
            PersistenceDiagnostics.Record(
                domain: "nkn_secret_store",
                operation: "save_seed",
                severity: PersistenceDiagnosticSeverity.Error,
                outcome: PersistenceDiagnosticOutcome.FailedClosed,
                reason: ex.GetType().Name,
                userWarning: "Protected seed storage could not be updated.");
            throw;
        }
    }

    public static void DeleteSeed(string keyPath)
    {
        try
        {
            ResolveBackendOrThrow(keyPath).DeleteSeed(keyPath);
        }
        catch (Exception ex)
        {
            PersistenceDiagnostics.Record(
                domain: "nkn_secret_store",
                operation: "delete_seed",
                severity: PersistenceDiagnosticSeverity.Warning,
                outcome: PersistenceDiagnosticOutcome.Fallback,
                reason: ex.GetType().Name);
            throw;
        }
    }

    public static string? ReadSeedBase64ForConnect(string keyPath)
    {
        var protectedSeed = TryLoadSeed(keyPath);
        if (protectedSeed is not null)
        {
            return Convert.ToBase64String(protectedSeed);
        }

        return null;
    }

    internal static IDisposable OverrideBackendForTests(IProtectedSeedBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        var previous = backendOverrideForTests;
        backendOverrideForTests = () => backend;
        return new DelegateDisposable(() => backendOverrideForTests = previous);
    }

    private static IProtectedSeedBackend ResolveBackendOrThrow(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);

        var backend = backendOverrideForTests?.Invoke() ?? CreateBackend();
        if (backend is null)
        {
            throw new InvalidOperationException($"Protected NKN seed storage is unavailable for '{keyPath}'.");
        }

        return backend;
    }

    private static IProtectedSeedBackend? CreateBackend()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsProtectedSeedBackend();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsKeychainSeedBackend();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxSecretServiceSeedBackend();
        }

        return null;
    }

    private static string BuildAccountName(string keyPath)
    {
        var normalizedPath = Path.GetFullPath(keyPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] BuildEntropy(string keyPath)
    {
        var normalizedPath = Path.GetFullPath(keyPath);
        return Encoding.UTF8.GetBytes($"{SecretPurpose}|{normalizedPath}");
    }

    internal interface IProtectedSeedBackend
    {
        byte[]? TryLoadSeed(string keyPath);

        void SaveSeed(string keyPath, ReadOnlySpan<byte> seedBytes);

        void DeleteSeed(string keyPath);
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsProtectedSeedBackend : IProtectedSeedBackend
    {
        public byte[]? TryLoadSeed(string keyPath)
        {
            var secretPath = GetSecretPath(keyPath);
            if (!File.Exists(secretPath))
            {
                return null;
            }

            var protectedBase64 = ReadAllTextWithTransientAccessRetry(secretPath).Trim();
            if (string.IsNullOrWhiteSpace(protectedBase64))
            {
                return null;
            }

            var protectedBytes = Convert.FromBase64String(protectedBase64);
            var seedBytes = ProtectedData.Unprotect(protectedBytes, BuildEntropy(keyPath), DataProtectionScope.CurrentUser);
            return seedBytes.Length > 0 ? seedBytes : null;
        }

        public void SaveSeed(string keyPath, ReadOnlySpan<byte> seedBytes)
        {
            var secretPath = GetSecretPath(keyPath);
            Directory.CreateDirectory(Path.GetDirectoryName(secretPath)!);

            var protectedBytes = ProtectedData.Protect(seedBytes.ToArray(), BuildEntropy(keyPath), DataProtectionScope.CurrentUser);
            File.WriteAllText(secretPath, Convert.ToBase64String(protectedBytes));
        }

        public void DeleteSeed(string keyPath)
        {
            var secretPath = GetSecretPath(keyPath);
            if (File.Exists(secretPath))
            {
                File.Delete(secretPath);
            }
        }
    }

    private static string ReadAllTextWithTransientAccessRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (IsTransientFileAccess(ex) && attempt < TransientSecretReadRetryCount)
            {
                Thread.Sleep(TransientSecretReadRetryDelayMs * (attempt + 1));
            }
        }
    }

    private static bool IsTransientFileAccess(Exception ex)
        => ex is IOException or UnauthorizedAccessException;

    [SupportedOSPlatform("macos")]
    private sealed class MacOsKeychainSeedBackend : IProtectedSeedBackend
    {
        public byte[]? TryLoadSeed(string keyPath)
        {
            var result = RunProcessOrThrow(
                "/usr/bin/security",
                ["find-generic-password", "-w", "-s", SecretPurpose, "-a", BuildAccountName(keyPath)],
                stdinText: null,
                keyPath,
                missingItemExitCodes: [44]);
            return string.IsNullOrWhiteSpace(result.StdOut)
                ? null
                : Convert.FromBase64String(result.StdOut.Trim());
        }

        public void SaveSeed(string keyPath, ReadOnlySpan<byte> seedBytes)
        {
            _ = RunProcessOrThrow(
                "/usr/bin/security",
                ["add-generic-password", "-U", "-s", SecretPurpose, "-a", BuildAccountName(keyPath), "-w", Convert.ToBase64String(seedBytes)],
                stdinText: null,
                keyPath,
                missingItemExitCodes: []);
        }

        public void DeleteSeed(string keyPath)
        {
            _ = RunProcessOrThrow(
                "/usr/bin/security",
                ["delete-generic-password", "-s", SecretPurpose, "-a", BuildAccountName(keyPath)],
                stdinText: null,
                keyPath,
                missingItemExitCodes: [44]);
        }
    }

    [SupportedOSPlatform("linux")]
    private sealed class LinuxSecretServiceSeedBackend : IProtectedSeedBackend
    {
        public byte[]? TryLoadSeed(string keyPath)
        {
            var result = RunProcessOrThrow(
                "secret-tool",
                ["lookup", "service", SecretPurpose, "account", BuildAccountName(keyPath)],
                stdinText: null,
                keyPath,
                missingItemExitCodes: [1]);
            return string.IsNullOrWhiteSpace(result.StdOut)
                ? null
                : Convert.FromBase64String(result.StdOut.Trim());
        }

        public void SaveSeed(string keyPath, ReadOnlySpan<byte> seedBytes)
        {
            _ = RunProcessOrThrow(
                "secret-tool",
                ["store", "--label=nLink NKN Seed", "service", SecretPurpose, "account", BuildAccountName(keyPath)],
                Convert.ToBase64String(seedBytes),
                keyPath,
                missingItemExitCodes: []);
        }

        public void DeleteSeed(string keyPath)
        {
            _ = RunProcessOrThrow(
                "secret-tool",
                ["clear", "service", SecretPurpose, "account", BuildAccountName(keyPath)],
                stdinText: null,
                keyPath,
                missingItemExitCodes: [1]);
        }
    }

    private static ProcessResult RunProcessOrThrow(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdinText,
        string keyPath,
        IReadOnlyCollection<int> missingItemExitCodes)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdinText is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException($"Failed to start protected seed backend process '{fileName}'.");
            }

            if (stdinText is not null)
            {
                process.StandardInput.Write(stdinText);
                process.StandardInput.Close();
            }

            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (missingItemExitCodes.Contains(process.ExitCode))
            {
                return new ProcessResult(process.ExitCode, stdOut, stdErr, MissingItem: true);
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Protected NKN seed storage failed for '{keyPath}' via '{fileName}' (exit={process.ExitCode}, stderr={stdErr.Trim()}).");
            }

            return new ProcessResult(process.ExitCode, stdOut, stdErr, MissingItem: false);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Protected NKN seed storage is unavailable for '{keyPath}' via '{fileName}'.", ex);
        }
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool MissingItem);

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref dispose, null)?.Invoke();
        }
    }
}
