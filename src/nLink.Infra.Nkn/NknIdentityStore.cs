using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using NLink.Core.Diagnostics;

namespace NLink.Infra.Nkn;

internal static class NknIdentityStore
{
    private const int ProtectedSeedIdentityVersion = 3;
    private const string AutoRecoveredIdentityUserWarning = "Local protected identity storage was unreadable. nLink created a new local identity. Previous helper address and invites are no longer valid.";
    private static Func<string>? defaultSharedKeyPathOverrideForTests;

    public static NknIdentity LoadOrCreate(NknTransportOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.KeyPath)!);
        NknSecretStore.EnsureProtectedSeedStorageAvailable(options.KeyPath);

        var identityFileExists = File.Exists(options.KeyPath);
        PersistedIdentityFile? persisted = null;
        var identityFileUnreadable = false;
        if (identityFileExists)
        {
            try
            {
                var json = File.ReadAllText(options.KeyPath);
                persisted = JsonSerializer.Deserialize<PersistedIdentityFile>(json);
            }
            catch (Exception ex)
            {
                identityFileUnreadable = true;
                PersistenceDiagnostics.Record(
                    domain: "nkn_identity_store",
                    operation: "read_identity",
                    severity: PersistenceDiagnosticSeverity.Error,
                    outcome: PersistenceDiagnosticOutcome.FailedClosed,
                    reason: ex.GetType().Name,
                    userWarning: "NKN identity file could not be read.");
            }
        }

        var seedBytes = ResolveSeedBytes(options.KeyPath, persisted?.SeedBase64, identityFileExists, identityFileUnreadable);
        var identifier = ResolveIdentifier(options.Identifier, persisted?.Identifier);
        var address = DeriveAddress(identifier, seedBytes);

        var file = new PersistedIdentityFile
        {
            Version = ProtectedSeedIdentityVersion,
            CreatedUtc = persisted?.CreatedUtc ?? DateTimeOffset.UtcNow,
            Identifier = identifier,
            SeedBase64 = null,
            Address = address,
        };

        NknSecretStore.SaveSeed(options.KeyPath, seedBytes);

        var serialized = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(options.KeyPath, serialized);

        return new NknIdentity(identifier, address);
    }

    internal static string? ReadSeedBase64ForConnect(string keyPath)
    {
        NknSecretStore.EnsureProtectedSeedStorageAvailable(keyPath);

        var protectedSeedBase64 = NknSecretStore.ReadSeedBase64ForConnect(keyPath);
        if (!string.IsNullOrWhiteSpace(protectedSeedBase64))
        {
            return protectedSeedBase64;
        }

        var legacySeedBytes = TryParseSeed(ReadLegacySeedBase64(keyPath));
        if (legacySeedBytes is not null)
        {
            NknSecretStore.SaveSeed(keyPath, legacySeedBytes);
            RewriteIdentityWithoutLegacySeed(keyPath);
            PersistenceDiagnostics.Record(
                domain: "nkn_identity_store",
                operation: "migrate_legacy_seed",
                severity: PersistenceDiagnosticSeverity.Info,
                outcome: PersistenceDiagnosticOutcome.Partial,
                reason: "legacy_json_seed_migrated");
            return Convert.ToBase64String(legacySeedBytes);
        }

        throw new InvalidOperationException($"Protected NKN seed is unavailable for '{keyPath}'.");
    }

    private static byte[]? TryParseSeed(string? seedBase64)
    {
        if (string.IsNullOrWhiteSpace(seedBase64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(seedBase64);
            return bytes.Length > 0 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveIdentifier(string? configuredIdentifier, string? persistedIdentifier)
    {
        if (!string.IsNullOrWhiteSpace(configuredIdentifier))
        {
            return configuredIdentifier.Trim();
        }

        if (!string.IsNullOrWhiteSpace(persistedIdentifier))
        {
            return persistedIdentifier.Trim();
        }

        Span<byte> random = stackalloc byte[4];
        RandomNumberGenerator.Fill(random);
        var suffix = Convert.ToHexString(random).ToLowerInvariant();
        return "nlink-" + suffix;
    }

    private static string DeriveAddress(string identifier, byte[] seedBytes)
    {
        using var sha = SHA256.Create();
        var identifierBytes = Encoding.UTF8.GetBytes(identifier);
        var input = new byte[identifierBytes.Length + 1 + seedBytes.Length];
        Buffer.BlockCopy(identifierBytes, 0, input, 0, identifierBytes.Length);
        input[identifierBytes.Length] = (byte)':';
        Buffer.BlockCopy(seedBytes, 0, input, identifierBytes.Length + 1, seedBytes.Length);

        var hash = sha.ComputeHash(input);
        var shortHash = Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant();
        return $"{identifier}.{shortHash}";
    }

    private static byte[] ResolveSeedBytes(string keyPath, string? legacySeedBase64, bool identityFileExists, bool identityFileUnreadable)
    {
        byte[]? protectedSeed;
        try
        {
            protectedSeed = NknSecretStore.TryLoadSeed(keyPath);
        }
        catch (CryptographicException ex) when (CanAutoRecoverCorruptedIdentity(keyPath, legacySeedBase64, identityFileExists, identityFileUnreadable))
        {
            protectedSeed = RecoverCorruptedDefaultIdentity(keyPath, ex);
        }

        if (protectedSeed is not null)
        {
            return protectedSeed;
        }

        var legacySeed = TryParseSeed(legacySeedBase64);
        if (legacySeed is not null)
        {
            return legacySeed;
        }

        if (identityFileUnreadable)
        {
            PersistenceDiagnostics.Record(
                domain: "nkn_identity_store",
                operation: "resolve_seed",
                severity: PersistenceDiagnosticSeverity.Error,
                outcome: PersistenceDiagnosticOutcome.FailedClosed,
                reason: "identity_file_unreadable",
                userWarning: "NKN identity file is unreadable.");
            throw new InvalidOperationException($"Existing NKN identity file '{keyPath}' is unreadable. Refusing to rotate identity silently.");
        }

        if (identityFileExists)
        {
            PersistenceDiagnostics.Record(
                domain: "nkn_identity_store",
                operation: "resolve_seed",
                severity: PersistenceDiagnosticSeverity.Error,
                outcome: PersistenceDiagnosticOutcome.FailedClosed,
                reason: "missing_recoverable_seed",
                userWarning: "Protected NKN seed is missing.");
            throw new InvalidOperationException($"Existing NKN identity at '{keyPath}' is missing recoverable seed material. Refusing to rotate identity silently.");
        }

        return RandomNumberGenerator.GetBytes(32);
    }

    internal static IDisposable OverrideDefaultSharedKeyPathForTests(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        var previous = defaultSharedKeyPathOverrideForTests;
        defaultSharedKeyPathOverrideForTests = () => Path.GetFullPath(keyPath);
        return new DelegateDisposable(() => defaultSharedKeyPathOverrideForTests = previous);
    }

    internal static string? GetAutomaticRecoveryUserWarning()
    {
        var warning = PersistenceDiagnostics.Snapshot().LastWarning;
        return string.Equals(warning, AutoRecoveredIdentityUserWarning, StringComparison.Ordinal)
            ? warning
            : null;
    }

    private static string? ReadLegacySeedBase64(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(keyPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var persisted = JsonSerializer.Deserialize<PersistedIdentityFile>(json);
            return persisted?.SeedBase64;
        }
        catch (Exception ex)
        {
            PersistenceDiagnostics.Record(
                domain: "nkn_identity_store",
                operation: "read_legacy_seed",
                severity: PersistenceDiagnosticSeverity.Warning,
                outcome: PersistenceDiagnosticOutcome.Fallback,
                reason: ex.GetType().Name);
            return null;
        }
    }

    private static void RewriteIdentityWithoutLegacySeed(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
        {
            return;
        }

        PersistedIdentityFile? persisted;
        try
        {
            persisted = JsonSerializer.Deserialize<PersistedIdentityFile>(File.ReadAllText(keyPath));
        }
        catch (Exception ex)
        {
            PersistenceDiagnostics.Record(
                domain: "nkn_identity_store",
                operation: "rewrite_identity_without_legacy_seed",
                severity: PersistenceDiagnosticSeverity.Warning,
                outcome: PersistenceDiagnosticOutcome.Fallback,
                reason: ex.GetType().Name,
                userWarning: "Legacy NKN seed was migrated, but the identity file cleanup did not complete.");
            return;
        }

        if (persisted is null)
        {
            return;
        }

        persisted.Version = Math.Max(persisted.Version, ProtectedSeedIdentityVersion);
        persisted.SeedBase64 = null;
        var serialized = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(keyPath, serialized);
    }

    private static bool CanAutoRecoverCorruptedIdentity(string keyPath, string? legacySeedBase64, bool identityFileExists, bool identityFileUnreadable)
    {
        return OperatingSystem.IsWindows() &&
               identityFileExists &&
               !identityFileUnreadable &&
               string.IsNullOrWhiteSpace(legacySeedBase64) &&
               File.Exists(NknSecretStore.GetSecretPath(keyPath)) &&
               (IsDefaultSharedIdentityPath(keyPath) || IsPerProcessIdentityPath(keyPath));
    }

    private static byte[] RecoverCorruptedDefaultIdentity(string keyPath, CryptographicException ex)
    {
        PersistenceDiagnostics.Record(
            domain: "nkn_identity_store",
            operation: "detect_corrupted_protected_seed",
            severity: PersistenceDiagnosticSeverity.Warning,
            outcome: PersistenceDiagnosticOutcome.Partial,
            reason: ex.GetType().Name);

        try
        {
            QuarantineCorruptedIdentityFiles(keyPath);
            PersistenceDiagnostics.Record(
                domain: "nkn_identity_store",
                operation: "quarantine_corrupted_identity",
                severity: PersistenceDiagnosticSeverity.Info,
                outcome: PersistenceDiagnosticOutcome.Partial,
                reason: "default_identity");
        }
        catch (Exception quarantineEx)
        {
            PersistenceDiagnostics.Record(
                domain: "nkn_identity_store",
                operation: "quarantine_corrupted_identity",
                severity: PersistenceDiagnosticSeverity.Error,
                outcome: PersistenceDiagnosticOutcome.FailedClosed,
                reason: quarantineEx.GetType().Name,
                userWarning: "Protected seed storage could not be read.");
            throw;
        }

        var seed = RandomNumberGenerator.GetBytes(32);
        PersistenceDiagnostics.Record(
            domain: "nkn_identity_store",
            operation: "automatic_identity_recovery",
            severity: PersistenceDiagnosticSeverity.Warning,
            outcome: PersistenceDiagnosticOutcome.Partial,
            reason: "default_identity_recreated",
            userWarning: AutoRecoveredIdentityUserWarning);
        return seed;
    }

    private static void QuarantineCorruptedIdentityFiles(string keyPath)
    {
        var identityPath = Path.GetFullPath(keyPath);
        var secretPath = NknSecretStore.GetSecretPath(identityPath);
        var recoveryDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nLink",
            "security",
            "identity-recovery");
        Directory.CreateDirectory(recoveryDir);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        MoveIfExists(identityPath, BuildQuarantinePath(recoveryDir, identityPath, timestamp));
        MoveIfExists(secretPath, BuildQuarantinePath(recoveryDir, secretPath, timestamp));
    }

    private static string BuildQuarantinePath(string recoveryDir, string sourcePath, string timestamp)
    {
        var fileName = Path.GetFileName(sourcePath);
        var extension = Path.GetExtension(fileName);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var candidate = Path.Combine(recoveryDir, $"{nameWithoutExtension}.{timestamp}.corrupt{extension}");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        return Path.Combine(recoveryDir, $"{nameWithoutExtension}.{timestamp}.{Guid.NewGuid():N}.corrupt{extension}");
    }

    private static void MoveIfExists(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        File.Move(sourcePath, destinationPath);
    }

    private static bool IsDefaultSharedIdentityPath(string keyPath)
    {
        var normalized = Path.GetFullPath(keyPath);
        var expected = defaultSharedKeyPathOverrideForTests?.Invoke() ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nLink",
            "identity.json");
        return string.Equals(normalized, Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPerProcessIdentityPath(string keyPath)
    {
        var fileName = Path.GetFileName(Path.GetFullPath(keyPath));
        return fileName.StartsWith("identity.instance-", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PersistedIdentityFile
    {
        public int Version { get; set; }

        public DateTimeOffset CreatedUtc { get; set; }

        public string? Identifier { get; set; }

        public string? SeedBase64 { get; set; }

        public string? Address { get; set; }
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        private Action? disposeAction = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref disposeAction, null)?.Invoke();
        }
    }
}

internal sealed record NknIdentity(string Identifier, string Address);
