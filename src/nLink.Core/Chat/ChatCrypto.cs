using System.Security.Cryptography;
using System.Text;

namespace NLink.Core.Chat;

public sealed class ChatKeyPair : IDisposable
{
    private readonly ECDiffieHellman ecdh;

    internal ChatKeyPair(ECDiffieHellman ecdh, byte[] publicKey)
    {
        this.ecdh = ecdh;
        PublicKey = publicKey;
    }

    public byte[] PublicKey { get; }

    public byte[] DeriveSharedKey(byte[] remotePublicKey)
    {
        ArgumentNullException.ThrowIfNull(remotePublicKey);

        using var remote = ECDiffieHellman.Create();
        remote.ImportSubjectPublicKeyInfo(remotePublicKey, out _);

        var secret = ecdh.DeriveKeyMaterial(remote.PublicKey);
        var context = Encoding.UTF8.GetBytes("nlink-chat-v1");
        var combined = new byte[context.Length + secret.Length];
        Buffer.BlockCopy(context, 0, combined, 0, context.Length);
        Buffer.BlockCopy(secret, 0, combined, context.Length, secret.Length);
        return SHA256.HashData(combined);
    }

    public void Dispose()
    {
        ecdh.Dispose();
    }
}

public static class ChatKeyAgreement
{
    public static ChatKeyPair CreateKeyPair()
    {
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = ecdh.ExportSubjectPublicKeyInfo();
        return new ChatKeyPair(ecdh, publicKey);
    }
}

public readonly record struct ChatEncryptedData(byte[] Nonce, byte[] Tag, byte[] Ciphertext);

public static class ChatAesGcmCrypto
{
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public static ChatEncryptedData Encrypt(byte[] key, ReadOnlySpan<byte> plaintext)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        return EncryptWithNonce(key, plaintext, nonce);
    }

    public static ChatEncryptedData EncryptWithNonce(byte[] key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> nonce)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException($"Nonce must be {NonceSize} bytes.", nameof(nonce));
        }

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return new ChatEncryptedData(nonce.ToArray(), tag, ciphertext);
    }

    public static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> ciphertext)
    {
        ArgumentNullException.ThrowIfNull(key);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
