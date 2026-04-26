using System.Buffers.Binary;
using System.Text;

namespace NLink.Core.ScreenShare;

public static class ScreenSharePayloadCodec
{
    public const string ScreenShareStopTypeV1 = "screenshare.stop.v1";

    private const uint BinaryMagic = 0x3153534E; // "NSS1"
    private const byte BinaryVersion = 1;
    private const byte MessageKindStop = 2;
    private const byte StopReasonPresentFlag = 0x01;

    public static byte[] SerializeStop(ScreenShareStopMessageV1 msg)
    {
        ArgumentNullException.ThrowIfNull(msg);

        var normalized = NormalizeStopMessageForSerialization(msg);
        var sessionIdBytes = Encoding.UTF8.GetBytes(normalized.SessionId);
        var reasonBytes = string.IsNullOrWhiteSpace(normalized.Reason)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(normalized.Reason);
        if (sessionIdBytes.Length > ushort.MaxValue || reasonBytes.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("Screen share stop payload contained an oversized string field.");
        }

        var flags = reasonBytes.Length > 0 ? StopReasonPresentFlag : (byte)0;
        var payload = new byte[
            sizeof(uint) +
            sizeof(byte) +
            sizeof(byte) +
            sizeof(byte) +
            sizeof(ushort) +
            sizeof(ushort) +
            sessionIdBytes.Length +
            reasonBytes.Length];
        var span = payload.AsSpan();
        var offset = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], BinaryMagic);
        offset += sizeof(uint);
        span[offset++] = BinaryVersion;
        span[offset++] = MessageKindStop;
        span[offset++] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)sessionIdBytes.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)reasonBytes.Length));
        offset += sizeof(ushort);
        sessionIdBytes.CopyTo(span[offset..]);
        offset += sessionIdBytes.Length;
        reasonBytes.CopyTo(span[offset..]);

        return payload;
    }

    public static bool TryDeserializeStop(ReadOnlySpan<byte> payload, out ScreenShareStopMessageV1 msg)
    {
        msg = default!;

        if (!TryReadHeader(payload, out var offset, out var messageKind, out var flags) ||
            messageKind != MessageKindStop ||
            (flags & ~StopReasonPresentFlag) != 0 ||
            !TryReadUInt16(payload, ref offset, out var sessionIdLength) ||
            !TryReadUInt16(payload, ref offset, out var reasonLength) ||
            !TryReadString(payload, ref offset, sessionIdLength, out var sessionId))
        {
            return false;
        }

        string? reason = null;
        if ((flags & StopReasonPresentFlag) != 0)
        {
            if (reasonLength == 0 || !TryReadString(payload, ref offset, reasonLength, out var parsedReason))
            {
                return false;
            }

            reason = string.IsNullOrWhiteSpace(parsedReason) ? null : parsedReason.Trim();
        }
        else if (reasonLength != 0)
        {
            return false;
        }

        if (offset != payload.Length || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        msg = new ScreenShareStopMessageV1
        {
            Kind = "screenshare",
            Type = ScreenShareStopTypeV1,
            SessionId = sessionId.Trim(),
            Reason = reason,
        };
        return true;
    }

    private static ScreenShareStopMessageV1 NormalizeStopMessageForSerialization(ScreenShareStopMessageV1 msg)
    {
        var normalizedSessionId = (msg.SessionId ?? string.Empty).Trim();
        var normalizedReason = string.IsNullOrWhiteSpace(msg.Reason) ? null : msg.Reason.Trim();

        if (!string.IsNullOrWhiteSpace(msg.Kind) &&
            !string.Equals(msg.Kind, "screenshare", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Screen share stop kind is invalid.");
        }

        if (!string.Equals(msg.Type, ScreenShareStopTypeV1, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Screen share stop type is invalid.");
        }

        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            throw new InvalidOperationException("Screen share stop payload is invalid.");
        }

        return msg with
        {
            Kind = "screenshare",
            SessionId = normalizedSessionId,
            Reason = normalizedReason,
        };
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> payload, out int offset, out byte messageKind, out byte flags)
    {
        offset = 0;
        messageKind = 0;
        flags = 0;

        if (!TryReadUInt32(payload, ref offset, out var magic) ||
            magic != BinaryMagic ||
            !TryReadByte(payload, ref offset, out var version) ||
            version != BinaryVersion ||
            !TryReadByte(payload, ref offset, out messageKind) ||
            !TryReadByte(payload, ref offset, out flags))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadByte(ReadOnlySpan<byte> payload, ref int offset, out byte value)
    {
        value = 0;
        if (offset + sizeof(byte) > payload.Length)
        {
            return false;
        }

        value = payload[offset++];
        return true;
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> payload, ref int offset, out ushort value)
    {
        value = 0;
        if (offset + sizeof(ushort) > payload.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        offset += sizeof(ushort);
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> payload, ref int offset, out uint value)
    {
        value = 0;
        if (offset + sizeof(uint) > payload.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        offset += sizeof(uint);
        return true;
    }

    private static bool TryReadString(ReadOnlySpan<byte> payload, ref int offset, int byteLength, out string value)
    {
        value = string.Empty;
        if (byteLength < 0 || offset + byteLength > payload.Length)
        {
            return false;
        }

        try
        {
            value = Encoding.UTF8.GetString(payload.Slice(offset, byteLength));
            offset += byteLength;
            return true;
        }
        catch (DecoderFallbackException)
        {
            value = string.Empty;
            return false;
        }
    }
}
