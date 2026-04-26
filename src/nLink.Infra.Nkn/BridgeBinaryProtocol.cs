using System.Buffers.Binary;
using System.Text;

namespace NLink.Infra.Nkn;

internal enum BridgeBinaryFrameKind : byte
{
    Send = 1,
    Message = 2,
}

internal sealed record BridgeBinaryFrame(
    BridgeBinaryFrameKind Kind,
    NknBridgeChannel Channel,
    byte Flags,
    string PrimaryText,
    string? SecondaryText,
    byte[] Payload)
{
    public bool IsTopic => (Flags & BridgeBinaryProtocol.IsTopicFlag) != 0;

    public long BinaryFrameDecodedUtcMs { get; init; }
}

internal sealed record BridgeBinaryFrameHeader(
    byte Version,
    BridgeBinaryFrameKind Kind,
    NknBridgeChannel Channel,
    byte Flags,
    ushort PrimaryLength,
    ushort SecondaryLength,
    int PayloadLength)
{
    public int BodyLength => PrimaryLength + SecondaryLength + PayloadLength;
}

internal static class BridgeBinaryProtocol
{
    public const byte FrameMagic = 0;
    public const byte ProtocolVersion = 2;
    public const byte IsTopicFlag = 0x01;
    public const int HeaderSize = 16;

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static int MeasureSendFrameBytes(string destination, ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return HeaderSize + Utf8.GetByteCount(destination) + payload.Length;
    }

    public static int MeasureMessageFrameBytes(string source, string? topic, ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return HeaderSize +
            Utf8.GetByteCount(source) +
            (string.IsNullOrWhiteSpace(topic) ? 0 : Utf8.GetByteCount(topic)) +
            payload.Length;
    }

    public static byte[] BuildSendFrame(string destination, ReadOnlySpan<byte> payload, NknBridgeChannel channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return BuildFrameCore(
            BridgeBinaryFrameKind.Send,
            channel,
            flags: 0,
            primaryText: destination,
            secondaryText: null,
            payload);
    }

    public static byte[] BuildMessageFrame(string source, ReadOnlySpan<byte> payload, NknBridgeChannel channel, bool isTopic, string? topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return BuildFrameCore(
            BridgeBinaryFrameKind.Message,
            channel,
            flags: isTopic ? IsTopicFlag : (byte)0,
            primaryText: source,
            secondaryText: string.IsNullOrWhiteSpace(topic) ? null : topic,
            payload);
    }

    public static BridgeBinaryFrameHeader ParseHeader(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length != HeaderSize)
        {
            throw new InvalidDataException($"Invalid binary frame header length {headerBytes.Length}.");
        }

        if (headerBytes[0] != FrameMagic)
        {
            throw new InvalidDataException("Invalid binary frame magic.");
        }

        var version = headerBytes[1];
        if (version != ProtocolVersion)
        {
            throw new InvalidDataException($"Unsupported binary frame protocol {version}.");
        }

        var kind = headerBytes[2] switch
        {
            (byte)BridgeBinaryFrameKind.Send => BridgeBinaryFrameKind.Send,
            (byte)BridgeBinaryFrameKind.Message => BridgeBinaryFrameKind.Message,
            var value => throw new InvalidDataException($"Unsupported binary frame kind {value}."),
        };

        var channel = headerBytes[3] switch
        {
            1 => NknBridgeChannel.Media,
            2 => NknBridgeChannel.Bulk,
            _ => NknBridgeChannel.Control,
        };

        var primaryLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.Slice(6, 2));
        var secondaryLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.Slice(8, 2));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.Slice(10, 4));
        if (payloadLength < 0)
        {
            throw new InvalidDataException("Binary frame payload length was negative.");
        }

        return new BridgeBinaryFrameHeader(
            version,
            kind,
            channel,
            headerBytes[4],
            primaryLength,
            secondaryLength,
            payloadLength);
    }

    public static BridgeBinaryFrame DecodeFrame(BridgeBinaryFrameHeader header, ReadOnlySpan<byte> bodyBytes)
    {
        if (bodyBytes.Length != header.BodyLength)
        {
            throw new InvalidDataException($"Binary frame body length mismatch (expected {header.BodyLength}, actual {bodyBytes.Length}).");
        }

        var primaryText = Utf8.GetString(bodyBytes[..header.PrimaryLength]);
        string? secondaryText = null;
        if (header.SecondaryLength > 0)
        {
            secondaryText = Utf8.GetString(bodyBytes.Slice(header.PrimaryLength, header.SecondaryLength));
        }

        var payload = bodyBytes.Slice(header.PrimaryLength + header.SecondaryLength, header.PayloadLength).ToArray();
        return new BridgeBinaryFrame(header.Kind, header.Channel, header.Flags, primaryText, secondaryText, payload);
    }

    private static byte[] BuildFrameCore(
        BridgeBinaryFrameKind kind,
        NknBridgeChannel channel,
        byte flags,
        string primaryText,
        string? secondaryText,
        ReadOnlySpan<byte> payload)
    {
        var primaryBytes = Utf8.GetBytes(primaryText);
        var secondaryBytes = string.IsNullOrWhiteSpace(secondaryText) ? Array.Empty<byte>() : Utf8.GetBytes(secondaryText);
        if (primaryBytes.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("Primary bridge binary text field was too large.");
        }

        if (secondaryBytes.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("Secondary bridge binary text field was too large.");
        }

        var frame = new byte[HeaderSize + primaryBytes.Length + secondaryBytes.Length + payload.Length];
        frame[0] = FrameMagic;
        frame[1] = ProtocolVersion;
        frame[2] = (byte)kind;
        frame[3] = channel switch
        {
            NknBridgeChannel.Media => (byte)1,
            NknBridgeChannel.Bulk => (byte)2,
            _ => (byte)0,
        };
        frame[4] = flags;
        frame[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), (ushort)primaryBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8, 2), (ushort)secondaryBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(10, 4), payload.Length);
        frame[14] = 0;
        frame[15] = 0;
        primaryBytes.CopyTo(frame.AsSpan(HeaderSize, primaryBytes.Length));
        secondaryBytes.CopyTo(frame.AsSpan(HeaderSize + primaryBytes.Length, secondaryBytes.Length));
        payload.CopyTo(frame.AsSpan(HeaderSize + primaryBytes.Length + secondaryBytes.Length, payload.Length));
        return frame;
    }
}
