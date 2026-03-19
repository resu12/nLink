using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLink.Core.Diagnostics;

namespace NLink.Infra.Nkn;

internal static class NknIdentityStore
{
    private const int ProtectedSeedIdentityVersion = 3;

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
        var protectedSeed = NknSecretStore.TryLoadSeed(keyPath);
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

    private sealed class PersistedIdentityFile
    {
        public int Version { get; set; }

        public DateTimeOffset CreatedUtc { get; set; }

        public string? Identifier { get; set; }

        public string? SeedBase64 { get; set; }

        public string? Address { get; set; }
    }
}

internal sealed record NknIdentity(string Identifier, string Address);
