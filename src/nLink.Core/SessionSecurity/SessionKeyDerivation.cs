using System.Security.Cryptography;
using System.Text;

namespace NLink.Core.SessionSecurity;

public static class SessionKeyDerivation
{
    public const int DefaultKeyLengthBytes = 32;
    public const string FileTransferInfoLabel = "nlink-file-transfer-v1";

    private const int MaxInfoLabelLength = 128;
    private static readonly byte[] SessionSubkeySalt =
        SHA256.HashData(Encoding.UTF8.GetBytes("nlink-session-subkey-salt-v1"));

    public static byte[] DeriveFileTransferKey(byte[] sessionRootKey, int keyLengthBytes = DefaultKeyLengthBytes)
    {
        return DeriveLabeledSubkey(sessionRootKey, FileTransferInfoLabel, keyLengthBytes);
    }

    public static byte[] DeriveLabeledSubkey(byte[] sessionRootKey, string infoLabel, int keyLengthBytes = DefaultKeyLengthBytes)
    {
        if (sessionRootKey is null || sessionRootKey.Length == 0)
        {
            throw new ArgumentException("session_root_key_missing", nameof(sessionRootKey));
        }

        if (string.IsNullOrWhiteSpace(infoLabel))
        {
            throw new ArgumentException("session_subkey_info_label_missing", nameof(infoLabel));
        }

        if (keyLengthBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keyLengthBytes));
        }

        var normalizedLabel = infoLabel.Trim();
        if (normalizedLabel.Length > MaxInfoLabelLength)
        {
            throw new ArgumentOutOfRangeException(nameof(infoLabel), "session_subkey_info_label_too_long");
        }

        return HkdfSha256(
            sessionRootKey,
            SessionSubkeySalt,
            Encoding.UTF8.GetBytes(normalizedLabel),
            keyLengthBytes);
    }

    public static byte[] HkdfSha256(byte[] ikm, byte[] salt, byte[] info, int okmLen)
    {
        if (ikm is null || ikm.Length == 0)
        {
            throw new ArgumentException("hkdf_ikm_missing", nameof(ikm));
        }

        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(info);
        if (okmLen <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(okmLen));
        }

        byte[] prk;
        using (var extract = new HMACSHA256(salt))
        {
            prk = extract.ComputeHash(ikm);
        }

        byte[] previous = Array.Empty<byte>();
        try
        {
            var okm = new byte[okmLen];
            var offset = 0;
            byte counter = 1;

            while (offset < okmLen)
            {
                using var expand = new HMACSHA256(prk);
                var blockInput = new byte[previous.Length + info.Length + 1];
                Buffer.BlockCopy(previous, 0, blockInput, 0, previous.Length);
                Buffer.BlockCopy(info, 0, blockInput, previous.Length, info.Length);
                blockInput[^1] = counter;

                var next = expand.ComputeHash(blockInput);
                CryptographicOperations.ZeroMemory(blockInput);
                if (previous.Length > 0)
                {
                    CryptographicOperations.ZeroMemory(previous);
                }

                previous = next;
                var bytesToCopy = Math.Min(previous.Length, okmLen - offset);
                Buffer.BlockCopy(previous, 0, okm, offset, bytesToCopy);
                offset += bytesToCopy;
                checked
                {
                    counter++;
                }
            }

            return okm;
        }
        finally
        {
            if (previous.Length > 0)
            {
                CryptographicOperations.ZeroMemory(previous);
            }

            CryptographicOperations.ZeroMemory(prk);
        }
    }
}
