using System.Buffers.Binary;
using System.Text;

namespace NLink.Infra.Nkn;

internal static class EnvelopeCodec
{
    private const int CurrentVersion = 1;
    private const byte ReplyToPresentFlag = 0x01;
    private static readonly byte[] Magic = "NLE"u8.ToArray();

    public static byte[] Serialize(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var code = (envelope.Code ?? string.Empty).Trim();
        var messageId = (envelope.MessageId ?? string.Empty).Trim();
        var replyTo = string.IsNullOrWhiteSpace(envelope.ReplyTo) ? null : envelope.ReplyTo.Trim();
        var payload = envelope.Payload ?? Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(messageId))
        {
            throw new ArgumentException("envelope_invalid", nameof(envelope));
        }

        var codeBytes = Encoding.UTF8.GetBytes(code);
        var messageIdBytes = Encoding.UTF8.GetBytes(messageId);
        var replyToBytes = replyTo is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(replyTo);
        if (codeBytes.Length > ushort.MaxValue ||
            messageIdBytes.Length > ushort.MaxValue ||
            replyToBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("envelope_invalid", nameof(envelope));
        }

        var totalLength =
            Magic.Length +
            sizeof(byte) +
            sizeof(byte) +
            sizeof(byte) +
            sizeof(long) +
            sizeof(ushort) +
            sizeof(ushort) +
            sizeof(ushort) +
            sizeof(uint) +
            codeBytes.Length +
            messageIdBytes.Length +
            replyToBytes.Length +
            payload.Length;
        var buffer = new byte[totalLength];
        var span = buffer.AsSpan();
        var offset = 0;

        Magic.CopyTo(span[offset..]);
        offset += Magic.Length;
        span[offset++] = CurrentVersion;
        span[offset++] = replyTo is null ? (byte)0 : ReplyToPresentFlag;
        span[offset++] = checked((byte)envelope.Type);
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], envelope.UnixTimeMs);
        offset += sizeof(long);
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)codeBytes.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)messageIdBytes.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)replyToBytes.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], checked((uint)payload.Length));
        offset += sizeof(uint);

        codeBytes.CopyTo(span[offset..]);
        offset += codeBytes.Length;
        messageIdBytes.CopyTo(span[offset..]);
        offset += messageIdBytes.Length;
        replyToBytes.CopyTo(span[offset..]);
        offset += replyToBytes.Length;
        payload.CopyTo(span[offset..]);

        return buffer;
    }

    public static bool TryDeserialize(byte[] data, out Envelope env)
    {
        env = default!;

        if (data is null || data.Length == 0)
        {
            return false;
        }

        if (data.Length < MinimumLength)
        {
            return false;
        }

        if (!data.AsSpan(0, Magic.Length).SequenceEqual(Magic) || data[Magic.Length] != CurrentVersion)
        {
            return false;
        }

        try
        {
            var offset = Magic.Length + sizeof(byte);
            var flags = data[offset++];
            var typeValue = data[offset++];
            var unixTimeMs = ReadInt64(data, ref offset);
            var codeLength = ReadUInt16(data, ref offset);
            var messageIdLength = ReadUInt16(data, ref offset);
            var replyToLength = ReadUInt16(data, ref offset);
            var payloadLength = ReadUInt32(data, ref offset);

            if ((flags & ~ReplyToPresentFlag) != 0 ||
                ((flags & ReplyToPresentFlag) == 0 && replyToLength != 0) ||
                ((flags & ReplyToPresentFlag) != 0 && replyToLength == 0) ||
                !Enum.IsDefined(typeof(MsgType), (int)typeValue))
            {
                return false;
            }

            var code = ReadUtf8(data, ref offset, codeLength);
            var messageId = ReadUtf8(data, ref offset, messageIdLength);
            var replyTo = replyToLength == 0 ? null : ReadUtf8(data, ref offset, replyToLength);
            var payload = ReadBytes(data, ref offset, checked((int)payloadLength));

            if (offset != data.Length ||
                string.IsNullOrWhiteSpace(code) ||
                string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            env = new Envelope(
                Version: CurrentVersion,
                Code: code.Trim(),
                MessageId: messageId.Trim(),
                Type: (MsgType)typeValue,
                Payload: payload,
                UnixTimeMs: unixTimeMs,
                ReplyTo: string.IsNullOrWhiteSpace(replyTo) ? null : replyTo.Trim());

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + sizeof(ushort) > data.Length)
        {
            throw new InvalidOperationException();
        }

        var value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += sizeof(ushort);
        return value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + sizeof(uint) > data.Length)
        {
            throw new InvalidOperationException();
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += sizeof(uint);
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + sizeof(long) > data.Length)
        {
            throw new InvalidOperationException();
        }

        var value = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        offset += sizeof(long);
        return value;
    }

    private static string ReadUtf8(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        if (length < 0 || offset + length > data.Length)
        {
            throw new InvalidOperationException();
        }

        try
        {
            var value = Encoding.UTF8.GetString(data[offset..(offset + length)]);
            offset += length;
            return value;
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException();
        }
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        if (length < 0 || offset + length > data.Length)
        {
            throw new InvalidOperationException();
        }

        var value = data[offset..(offset + length)].ToArray();
        offset += length;
        return value;
    }

    private const int MinimumLength =
        3 + // magic
        1 + // version
        1 + // flags
        1 + // type
        8 + // unix time
        2 + // code length
        2 + // message id length
        2 + // replyTo length
        4;  // payload length
}
