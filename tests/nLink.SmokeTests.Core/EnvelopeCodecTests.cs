using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class EnvelopeCodecTests
{
    [Fact]
    public void SerializeDeserialize_RoundTrips_BinaryEnvelope()
    {
        var envelope = new Envelope(
            Version: 1,
            Code: "control",
            MessageId: "msg-123",
            Type: MsgType.ControlInput,
            Payload: [1, 2, 3, 4, 5],
            UnixTimeMs: 123456789L,
            ReplyTo: "reply-123");

        var bytes = EnvelopeCodec.Serialize(envelope);

        Assert.True(EnvelopeCodec.TryDeserialize(bytes, out var parsed));
        Assert.Equal(envelope.Code, parsed.Code);
        Assert.Equal(envelope.MessageId, parsed.MessageId);
        Assert.Equal(envelope.Type, parsed.Type);
        Assert.Equal(envelope.Payload, parsed.Payload);
        Assert.Equal(envelope.UnixTimeMs, parsed.UnixTimeMs);
        Assert.Equal(envelope.ReplyTo, parsed.ReplyTo);
    }

    [Fact]
    public void TryDeserialize_InvalidMagic_ReturnsFalse()
    {
        var envelope = new Envelope(
            Version: 1,
            Code: "chat",
            MessageId: "msg-1",
            Type: MsgType.Chat,
            Payload: [9, 8, 7],
            UnixTimeMs: 1,
            ReplyTo: null);

        var bytes = EnvelopeCodec.Serialize(envelope);
        bytes[0] ^= 0x01;

        Assert.False(EnvelopeCodec.TryDeserialize(bytes, out _));
    }

    [Fact]
    public void TryDeserialize_TruncatedPayload_ReturnsFalse()
    {
        var envelope = new Envelope(
            Version: 1,
            Code: "filetransfer",
            MessageId: "msg-2",
            Type: MsgType.FileTransferDataFrame,
            Payload: [1, 2, 3, 4],
            UnixTimeMs: 2,
            ReplyTo: null);

        var bytes = EnvelopeCodec.Serialize(envelope);
        var truncated = bytes[..^2];

        Assert.False(EnvelopeCodec.TryDeserialize(truncated, out _));
    }
}
