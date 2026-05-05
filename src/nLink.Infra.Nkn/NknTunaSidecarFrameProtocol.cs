using System.Buffers.Binary;

namespace NLink.Infra.Nkn;

internal enum NknTunaSidecarFrameType : byte
{
    Status = 1,
    Data = 2,
    Ping = 3,
    Pong = 4,
    Close = 5,
}

internal enum NknTunaSidecarLane : byte
{
    Control = 0,
    Media = 1,
    Bulk = 2,
}

internal readonly record struct NknTunaSidecarFrame(
    NknTunaSidecarFrameType Type,
    NknTunaSidecarLane Lane,
    ulong Sequence,
    long TimestampUtcMs,
    byte[] Payload);

internal static class NknTunaSidecarFrameProtocol
{
    public const int ProtocolVersion = 1;
    public const int HeaderSize = 32;
    public const int MaxPayloadBytes = 4 * 1024 * 1024;
    private const uint Magic = 0x4E4C5453; // NLTS

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        NknTunaSidecarFrameType type,
        NknTunaSidecarLane lane,
        ulong sequence,
        long timestampUtcMs,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidOperationException("tuna_sidecar_payload_too_large");
        }

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), Magic);
        header[4] = ProtocolVersion;
        header[5] = (byte)type;
        header[6] = (byte)lane;
        header[7] = 0;
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8, 8), sequence);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16, 8), timestampUtcMs);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24, 4), checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28, 4), 0);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async ValueTask<NknTunaSidecarFrame> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)) != Magic)
        {
            throw new InvalidOperationException("tuna_sidecar_invalid_magic");
        }

        if (header[4] != ProtocolVersion)
        {
            throw new InvalidOperationException("tuna_sidecar_unsupported_protocol");
        }

        if (!Enum.IsDefined(typeof(NknTunaSidecarFrameType), header[5]) ||
            !Enum.IsDefined(typeof(NknTunaSidecarLane), header[6]))
        {
            throw new InvalidOperationException("tuna_sidecar_invalid_frame_header");
        }

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24, 4));
        if (payloadLength > MaxPayloadBytes)
        {
            throw new InvalidOperationException("tuna_sidecar_payload_too_large");
        }

        var payload = payloadLength == 0 ? [] : new byte[payloadLength];
        if (payload.Length > 0)
        {
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        }

        return new NknTunaSidecarFrame(
            (NknTunaSidecarFrameType)header[5],
            (NknTunaSidecarLane)header[6],
            BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8)),
            BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16, 8)),
            payload);
    }
}
