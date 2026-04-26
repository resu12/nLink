using System.Security.Cryptography;
using System.Text;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SessionSecureEnvelopeCodecTests
{
    private static readonly byte[] TestKey = SHA256.HashData(Encoding.UTF8.GetBytes("session-secure-envelope-test-key"));

    [Fact]
    public void EncryptDecrypt_RoundTrips_Metadata_And_Plaintext()
    {
        var metadata = CreateMetadata(
            SessionSecureMessageFamily.RemoteControl,
            "control_input",
            requestId: "req_123");
        var plaintext = Encoding.UTF8.GetBytes("{\"kind\":\"mouse_move\"}");

        var encoded = SessionSecureEnvelopeCodec.Encrypt(TestKey, metadata, plaintext);
        var decoded = SessionSecureEnvelopeCodec.Decrypt(
            TestKey,
            encoded,
            new SessionSecureEnvelopeExpectation(
                Family: SessionSecureMessageFamily.RemoteControl,
                MessageType: "control_input",
                SessionId: metadata.SessionId,
                SenderIdentity: metadata.SenderIdentity,
                RequestId: "req_123"));

        Assert.Equal(metadata, decoded.Metadata);
        Assert.Equal(plaintext, decoded.Plaintext);
    }

    [Fact]
    public void Decrypt_Tampered_Metadata_FailsAuthentication()
    {
        var metadata = CreateMetadata(SessionSecureMessageFamily.ScreenShare, "frame_chunk", requestId: null);
        var encoded = SessionSecureEnvelopeCodec.Encrypt(TestKey, metadata, Encoding.UTF8.GetBytes("frame-bytes"));
        var tampered = (byte[])encoded.Clone();
        FlipUtf8Byte(tampered, "frame_chunk"u8.ToArray());

        Assert.ThrowsAny<CryptographicException>(() =>
            SessionSecureEnvelopeCodec.Decrypt(TestKey, tampered));
    }

    [Fact]
    public void Decrypt_Wrong_Session_Context_Fails()
    {
        var metadata = CreateMetadata(SessionSecureMessageFamily.Lifecycle, "approve", requestId: "req_approve");
        var encoded = SessionSecureEnvelopeCodec.Encrypt(TestKey, metadata, Encoding.UTF8.GetBytes("approved"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SessionSecureEnvelopeCodec.Decrypt(
                TestKey,
                encoded,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.Lifecycle,
                    MessageType: "approve",
                    SessionId: new SessionId("sess_other"),
                    SenderIdentity: metadata.SenderIdentity,
                    RequestId: "req_approve")));

        Assert.Equal("session_secure_session_id_mismatch", ex.Message);
    }

    [Fact]
    public void Decrypt_Wrong_RequestId_Context_Fails()
    {
        var metadata = CreateMetadata(SessionSecureMessageFamily.RemoteControl, "control_start", requestId: "req_start");
        var encoded = SessionSecureEnvelopeCodec.Encrypt(TestKey, metadata, Encoding.UTF8.GetBytes("start"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SessionSecureEnvelopeCodec.Decrypt(
                TestKey,
                encoded,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.RemoteControl,
                    MessageType: "control_start",
                    SessionId: metadata.SessionId,
                    SenderIdentity: metadata.SenderIdentity,
                    RequestId: "req_other")));

        Assert.Equal("session_secure_request_id_mismatch", ex.Message);
    }

    [Fact]
    public void Decrypt_Wrong_Family_Context_Fails()
    {
        var metadata = CreateMetadata(SessionSecureMessageFamily.RemoteControl, "control_input", requestId: "req_control");
        var encoded = SessionSecureEnvelopeCodec.Encrypt(TestKey, metadata, Encoding.UTF8.GetBytes("move"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SessionSecureEnvelopeCodec.Decrypt(
                TestKey,
                encoded,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.ScreenShare,
                    MessageType: "control_input",
                    SessionId: metadata.SessionId,
                    SenderIdentity: metadata.SenderIdentity,
                    RequestId: "req_control")));

        Assert.Equal("session_secure_family_mismatch", ex.Message);
    }

    [Fact]
    public void Decrypt_Wrong_MessageType_Context_Fails()
    {
        var metadata = CreateMetadata(SessionSecureMessageFamily.Lifecycle, "session_end", requestId: null);
        var encoded = SessionSecureEnvelopeCodec.Encrypt(TestKey, metadata, Encoding.UTF8.GetBytes("end"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SessionSecureEnvelopeCodec.Decrypt(
                TestKey,
                encoded,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.Lifecycle,
                    MessageType: "approve",
                    SessionId: metadata.SessionId,
                    SenderIdentity: metadata.SenderIdentity,
                    RequestId: null)));

        Assert.Equal("session_secure_message_type_mismatch", ex.Message);
    }

    [Fact]
    public void Encrypt_Negative_Sequence_Throws()
    {
        var metadata = new SessionSecureEnvelopeMetadata(
            SessionSecureMessageFamily.Chat,
            "chat_message",
            new SessionId("sess_123"),
            new PeerAddress("nlink-helper.123"),
            -1,
            null);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionSecureEnvelopeCodec.Encrypt(TestKey, metadata, Encoding.UTF8.GetBytes("hello")));
    }

    [Fact]
    public void Decrypt_InvalidMagic_Fails()
    {
        var encoded = SessionSecureEnvelopeCodec.Encrypt(
            TestKey,
            CreateMetadata(SessionSecureMessageFamily.Chat, "chat_message", null),
            Encoding.UTF8.GetBytes("hello"));

        var tampered = (byte[])encoded.Clone();
        tampered[0] ^= 0x01;

        var ex = Assert.Throws<CryptographicException>(() => SessionSecureEnvelopeCodec.Decrypt(TestKey, tampered));
        Assert.Equal("session_secure_payload_invalid", ex.Message);
    }

    [Fact]
    public void Decrypt_TruncatedPayload_Fails()
    {
        var encoded = SessionSecureEnvelopeCodec.Encrypt(
            TestKey,
            CreateMetadata(SessionSecureMessageFamily.Chat, "chat_message", "req_1"),
            Encoding.UTF8.GetBytes("hello"));

        var truncated = encoded[..^5];

        var ex = Assert.Throws<CryptographicException>(() => SessionSecureEnvelopeCodec.Decrypt(TestKey, truncated));
        Assert.Equal("session_secure_payload_invalid", ex.Message);
    }

    private static SessionSecureEnvelopeMetadata CreateMetadata(
        SessionSecureMessageFamily family,
        string messageType,
        string? requestId)
    {
        return new SessionSecureEnvelopeMetadata(
            family,
            messageType,
            new SessionId("sess_123"),
            new PeerAddress("nlink-helper.123"),
            42,
            requestId);
    }

    private static void FlipUtf8Byte(byte[] buffer, byte[] pattern)
    {
        var index = FindPattern(buffer, pattern);
        Assert.True(index >= 0);
        buffer[index] ^= 0x01;
    }

    private static int FindPattern(byte[] buffer, byte[] pattern)
    {
        for (var i = 0; i <= buffer.Length - pattern.Length; i++)
        {
            if (buffer.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        return -1;
    }
}
