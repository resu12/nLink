using System.Security.Cryptography;
using System.Text;

namespace NLink.Infra.Nkn;

internal static class NknSecretStore
{
    private const string SecretFileSuffix = ".seed";
    private const string WindowsSecretPurpose = "nLink.NknIdentitySeed.v1";

    public static bool SupportsProtectedSeedStorage => OperatingSystem.IsWindows();

    public static string GetSecretPath(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        return Path.GetFullPath(keyPath) + SecretFileSuffix;
    }

    public static byte[]? TryLoadSeed(string keyPath)
    {
        if (!SupportsProtectedSeedStorage)
        {
            return null;
        }

        var secretPath = GetSecretPath(keyPath);
        if (!File.Exists(secretPath))
        {
            return null;
        }

        try
        {
            var protectedBase64 = File.ReadAllText(secretPath).Trim();
            if (string.IsNullOrWhiteSpace(protectedBase64))
            {
                return null;
            }

            var protectedBytes = Convert.FromBase64String(protectedBase64);
            var entropy = BuildEntropy(keyPath);
            var seedBytes = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
            return seedBytes.Length > 0 ? seedBytes : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveSeed(string keyPath, ReadOnlySpan<byte> seedBytes)
    {
        if (!SupportsProtectedSeedStorage)
        {
            return;
        }

        var secretPath = GetSecretPath(keyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(secretPath)!);

        var protectedBytes = ProtectedData.Protect(seedBytes.ToArray(), BuildEntropy(keyPath), DataProtectionScope.CurrentUser);
        File.WriteAllText(secretPath, Convert.ToBase64String(protectedBytes));
    }

    public static void DeleteSeed(string keyPath)
    {
        if (!SupportsProtectedSeedStorage)
        {
            return;
        }

        var secretPath = GetSecretPath(keyPath);
        if (File.Exists(secretPath))
        {
            File.Delete(secretPath);
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

    private static byte[] BuildEntropy(string keyPath)
    {
        var normalizedPath = Path.GetFullPath(keyPath);
        return Encoding.UTF8.GetBytes($"{WindowsSecretPurpose}|{normalizedPath}");
    }
}
