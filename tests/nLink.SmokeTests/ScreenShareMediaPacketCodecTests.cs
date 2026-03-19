using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

public sealed class ScreenShareMediaPacketCodecTests
{
    [Fact]
    public void ScreenShareMediaPacketCodec_EncryptDecrypt_RoundTrips()
    {
        var key = Enumerable.Repeat((byte)0x4A, 32).ToArray();
        var chunk = CreateChunk("session-roundtrip", frameId: 7, marker: 0x33);

        var encoded = ScreenShareMediaPacketCodec.EncryptFrame(
            key,
            chunk.SessionId,
            sequence: 3,
            senderIdentity: "sender.peer",
            chunk);

        Assert.True(ScreenShareMediaPacketCodec.TryDeserializeFrame(encoded, out var metadata));
        Assert.Equal(chunk.SessionId, metadata.SessionId);
        Assert.Equal(3, metadata.Sequence);

        Assert.True(ScreenShareMediaPacketCodec.TryDecryptFrame(key, encoded, out var decryptedMetadata, out var payload));
        Assert.Equal(metadata, decryptedMetadata);
        Assert.Equal("sender.peer", payload.SenderIdentity);
        Assert.Equal(chunk.FrameId, payload.Chunk.FrameId);
        Assert.Equal(chunk.Width, payload.Chunk.Width);
        Assert.Equal(chunk.Height, payload.Chunk.Height);
        Assert.Equal(chunk.DataBase64, payload.Chunk.DataBase64);
    }

    [Fact]
    public void ScreenShareMediaPacketCodec_WrongKey_FailsDecrypt()
    {
        var correctKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var wrongKey = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var chunk = CreateChunk("session-wrong-key", frameId: 8, marker: 0x44);
        var encoded = ScreenShareMediaPacketCodec.EncryptFrame(correctKey, chunk.SessionId, 9, "sender.peer", chunk);

        Assert.False(ScreenShareMediaPacketCodec.TryDecryptFrame(wrongKey, encoded, out _, out _));
    }

    [Fact]
    public void ScreenShareMediaPacketCodec_TamperedClearMetadata_FailsDecrypt()
    {
        var key = Enumerable.Repeat((byte)0x55, 32).ToArray();
        var chunk = CreateChunk("session-clear-tamper", frameId: 9, marker: 0x55);
        var encoded = ScreenShareMediaPacketCodec.EncryptFrame(key, chunk.SessionId, 10, "sender.peer", chunk);
        var tampered = System.Text.Encoding.UTF8.GetString(encoded)
            .Replace(chunk.SessionId, "session-clear-tamper-other", StringComparison.Ordinal);

        Assert.False(
            ScreenShareMediaPacketCodec.TryDecryptFrame(
                key,
                System.Text.Encoding.UTF8.GetBytes(tampered),
                out _,
                out _));
    }

    [Fact]
    public void ScreenShareMediaPacketCodec_TamperedCiphertext_FailsDecrypt()
    {
        var key = Enumerable.Repeat((byte)0x66, 32).ToArray();
        var chunk = CreateChunk("session-cipher-tamper", frameId: 10, marker: 0x66);
        var encoded = ScreenShareMediaPacketCodec.EncryptFrame(key, chunk.SessionId, 11, "sender.peer", chunk);
        var tampered = encoded.ToArray();
        tampered[^8] ^= 0x5A;

        Assert.False(ScreenShareMediaPacketCodec.TryDecryptFrame(key, tampered, out _, out _));
    }

    [Fact]
    public void ScreenShareMediaPacketCodec_InvalidSequence_IsRejected()
    {
        var key = Enumerable.Repeat((byte)0x77, 32).ToArray();
        var chunk = CreateChunk("session-sequence", frameId: 11, marker: 0x77);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScreenShareMediaPacketCodec.EncryptFrame(key, chunk.SessionId, sequence: 0, "sender.peer", chunk));
    }

    private static ScreenShareFrameChunkV1 CreateChunk(string sessionId, long frameId, byte marker)
    {
        return new ScreenShareFrameChunkV1
        {
            Kind = "screenshare",
            Type = ScreenShareMediaPacketCodec.ScreenShareFrameTypeV2,
            SessionId = sessionId,
            FrameId = frameId,
            Width = 320,
            Height = 180,
            TimestampUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 1,
            DataBase64 = Convert.ToBase64String(new[] { marker }),
        };
    }
}
