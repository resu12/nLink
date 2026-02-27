using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NLink.Infra.Nkn;

internal static class NknIdentityStore
{
    public static NknIdentity LoadOrCreate(NknTransportOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.KeyPath)!);

        PersistedIdentityFile? persisted = null;
        if (File.Exists(options.KeyPath))
        {
            try
            {
                var json = File.ReadAllText(options.KeyPath);
                persisted = JsonSerializer.Deserialize<PersistedIdentityFile>(json);
            }
            catch
            {
                // Regenerate a valid file below if the stored file cannot be read.
            }
        }

        var seedBytes = TryParseSeed(persisted?.SeedBase64) ?? RandomNumberGenerator.GetBytes(32);
        var identifier = ResolveIdentifier(options.Identifier, persisted?.Identifier);
        var address = DeriveAddress(identifier, seedBytes);

        var file = new PersistedIdentityFile
        {
            Version = 1,
            CreatedUtc = persisted?.CreatedUtc ?? DateTimeOffset.UtcNow,
            Identifier = identifier,
            SeedBase64 = Convert.ToBase64String(seedBytes),
            Address = address,
        };

        var serialized = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(options.KeyPath, serialized);

        return new NknIdentity(identifier, address);
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
