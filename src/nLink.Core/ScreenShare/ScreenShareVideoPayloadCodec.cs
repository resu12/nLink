using System;
using System.Buffers.Binary;
using System.Text;

namespace NLink.Core.ScreenShare;

public static class ScreenShareVideoPayloadCodec
{
    public const string ScreenShareVideoFragmentTypeV1 = "screenshare.video_fragment.v1";
    public const string ScreenShareVideoFragmentBatchTypeV1 = "screenshare.video_fragment_batch.v1";
    public const string ScreenShareVideoStreamConfigTypeV1 = "screenshare.video_stream_config.v1";
    public const int MaxFragmentRawBytes = ScreenShareVideoFragmenter.MaxFragmentRawBytes;
    public const int MaxConfigBytes = 4_096;
    public const int MaxBatchFragments = 128;

    private const uint BinaryMagic = 0x3156534E; // "NSV1"
    private const byte BinaryVersion = 1;
    private const byte MessageKindFragment = 1;
    private const byte MessageKindStreamConfig = 2;
    private const byte MessageKindFragmentBatch = 3;
    private const byte KeyFrameFlag = 0x01;

    public static byte[] SerializeFragment(ScreenShareVideoFragmentV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var normalized = NormalizeFragmentForSerialization(message);
        var sessionIdBytes = Encoding.UTF8.GetBytes(normalized.SessionId);
        var encodingBytes = Encoding.UTF8.GetBytes(normalized.Encoding);
        var dataBytes = normalized.Data;
        if (sessionIdBytes.Length > ushort.MaxValue || encodingBytes.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("Screen share video fragment contained an oversized string field.");
        }

        var payload = new byte[
            sizeof(uint) +
            sizeof(byte) +
            sizeof(byte) +
            sizeof(byte) +
            sizeof(ushort) +
            sizeof(ushort) +
            sizeof(long) +
            sizeof(long) +
            sizeof(long) +
            sizeof(int) +
            sizeof(int) +
            sizeof(int) +
            sizeof(int) +
            sizeof(int) +
            sessionIdBytes.Length +
            encodingBytes.Length +
            dataBytes.Length];

        var span = payload.AsSpan();
        var offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], BinaryMagic);
        offset += sizeof(uint);
        span[offset++] = BinaryVersion;
        span[offset++] = MessageKindFragment;
        span[offset++] = normalized.IsKeyFrame ? KeyFrameFlag : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)sessionIdBytes.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)encodingBytes.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], normalized.StreamEpoch);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], normalized.FrameId);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], normalized.CapturedTsUtcMs);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], normalized.Width);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], normalized.Height);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], normalized.FragmentIndex);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], normalized.FragmentCount);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], dataBytes.Length);
        offset += sizeof(int);
        sessionIdBytes.CopyTo(span[offset..]);
        offset += sessionIdBytes.Length;
        encodingBytes.CopyTo(span[offset..]);
        offset += encodingBytes.Length;
        dataBytes.CopyTo(span[offset..]);
        return payload;
    }

    public static bool TryDeserializeFragment(ReadOnlySpan<byte> payload, out ScreenShareVideoFragmentV1 message)
    {
        message = default!;

        if (!TryReadHeader(payload, out var offset, out var kind, out var flags) ||
            kind != MessageKindFragment ||
            (flags & ~KeyFrameFlag) != 0 ||
            !TryReadUInt16(payload, ref offset, out var sessionIdLength) ||
            !TryReadUInt16(payload, ref offset, out var encodingLength) ||
            !TryReadInt64(payload, ref offset, out var streamEpoch) ||
            !TryReadInt64(payload, ref offset, out var frameId) ||
            !TryReadInt64(payload, ref offset, out var capturedTsUtcMs) ||
            !TryReadInt32(payload, ref offset, out var width) ||
            !TryReadInt32(payload, ref offset, out var height) ||
            !TryReadInt32(payload, ref offset, out var fragmentIndex) ||
            !TryReadInt32(payload, ref offset, out var fragmentCount) ||
            !TryReadInt32(payload, ref offset, out var dataLength) ||
            dataLength <= 0 ||
            dataLength > MaxFragmentRawBytes ||
            !TryReadString(payload, ref offset, sessionIdLength, out var sessionId) ||
            !TryReadString(payload, ref offset, encodingLength, out var encoding) ||
            !TryReadBytes(payload, ref offset, dataLength, out var dataBytes) ||
            offset != payload.Length)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sessionId) ||
            streamEpoch <= 0 ||
            frameId < 0 ||
            capturedTsUtcMs < 0 ||
            width <= 0 ||
            height <= 0 ||
            !string.Equals(encoding, "h264", StringComparison.OrdinalIgnoreCase) ||
            fragmentIndex < 0 ||
            fragmentCount <= 0 ||
            fragmentIndex >= fragmentCount)
        {
            return false;
        }

        message = new ScreenShareVideoFragmentV1
        {
            Kind = "screenshare",
            Type = ScreenShareVideoFragmentTypeV1,
            SessionId = sessionId.Trim(),
            StreamEpoch = streamEpoch,
            FrameId = frameId,
            CapturedTsUtcMs = capturedTsUtcMs,
            Width = width,
            Height = height,
            Encoding = encoding.Trim(),
            IsKeyFrame = (flags & KeyFrameFlag) != 0,
            FragmentIndex = fragmentIndex,
            FragmentCount = fragmentCount,
            Data = dataBytes,
        };
        return true;
    }

    public static byte[] SerializeFragmentBatch(IReadOnlyList<byte[]> serializedFragments)
    {
        ArgumentNullException.ThrowIfNull(serializedFragments);

        if (serializedFragments.Count <= 0 || serializedFragments.Count > MaxBatchFragments)
        {
            throw new InvalidOperationException("Screen share video fragment batch payload is invalid.");
        }

        var totalLength = sizeof(uint) + sizeof(byte) + sizeof(byte) + sizeof(byte) + sizeof(ushort);
        string? sessionId = null;
        string? encoding = null;
        long streamEpoch = 0;
        long frameId = -1;
        long capturedTsUtcMs = -1;
        var width = 0;
        var height = 0;
        var fragmentCount = 0;
        bool? isKeyFrame = null;
        var expectedNextFragmentIndex = -1;

        for (var i = 0; i < serializedFragments.Count; i++)
        {
            var serializedFragment = serializedFragments[i] ?? throw new InvalidOperationException("Screen share video fragment batch payload is invalid.");
            if (!TryDeserializeFragment(serializedFragment, out var fragment))
            {
                throw new InvalidOperationException("Screen share video fragment batch payload is invalid.");
            }

            if (i == 0)
            {
                sessionId = fragment.SessionId;
                encoding = fragment.Encoding;
                streamEpoch = fragment.StreamEpoch;
                frameId = fragment.FrameId;
                capturedTsUtcMs = fragment.CapturedTsUtcMs;
                width = fragment.Width;
                height = fragment.Height;
                fragmentCount = fragment.FragmentCount;
                isKeyFrame = fragment.IsKeyFrame;
                expectedNextFragmentIndex = fragment.FragmentIndex;
            }
            else if (!string.Equals(sessionId, fragment.SessionId, StringComparison.Ordinal) ||
                     streamEpoch != fragment.StreamEpoch ||
                     frameId != fragment.FrameId ||
                     capturedTsUtcMs != fragment.CapturedTsUtcMs ||
                     width != fragment.Width ||
                     height != fragment.Height ||
                     !string.Equals(encoding, fragment.Encoding, StringComparison.Ordinal) ||
                     fragmentCount != fragment.FragmentCount ||
                     isKeyFrame != fragment.IsKeyFrame ||
                     fragment.FragmentIndex != expectedNextFragmentIndex + 1)
            {
                throw new InvalidOperationException("Screen share video fragment batch payload is invalid.");
            }

            expectedNextFragmentIndex = fragment.FragmentIndex;
            totalLength += sizeof(int) + serializedFragment.Length;
        }

        var payload = new byte[totalLength];
        var span = payload.AsSpan();
        var offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], BinaryMagic);
        offset += sizeof(uint);
        span[offset++] = BinaryVersion;
        span[offset++] = MessageKindFragmentBatch;
        span[offset++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)serializedFragments.Count));
        offset += sizeof(ushort);

        foreach (var serializedFragment in serializedFragments)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], serializedFragment.Length);
            offset += sizeof(int);
            serializedFragment.CopyTo(span[offset..]);
            offset += serializedFragment.Length;
        }

        return payload;
    }

    public static bool TryDeserializeFragmentBatch(ReadOnlySpan<byte> payload, out ScreenShareVideoFragmentV1[] fragments)
    {
        fragments = Array.Empty<ScreenShareVideoFragmentV1>();

        if (!TryReadHeader(payload, out var offset, out var kind, out var flags) ||
            kind != MessageKindFragmentBatch ||
            flags != 0 ||
            !TryReadUInt16(payload, ref offset, out var serializedFragmentCount) ||
            serializedFragmentCount == 0 ||
            serializedFragmentCount > MaxBatchFragments)
        {
            return false;
        }

        var parsedFragments = new ScreenShareVideoFragmentV1[serializedFragmentCount];
        string? sessionId = null;
        string? encoding = null;
        long streamEpoch = 0;
        long frameId = -1;
        long capturedTsUtcMs = -1;
        var width = 0;
        var height = 0;
        var fragmentCount = 0;
        bool? isKeyFrame = null;
        var previousFragmentIndex = -1;

        for (var i = 0; i < serializedFragmentCount; i++)
        {
            if (!TryReadInt32(payload, ref offset, out var serializedFragmentLength) ||
                serializedFragmentLength <= 0 ||
                payload.Length - offset < serializedFragmentLength)
            {
                fragments = Array.Empty<ScreenShareVideoFragmentV1>();
                return false;
            }

            if (!TryDeserializeFragment(payload.Slice(offset, serializedFragmentLength), out var fragment))
            {
                fragments = Array.Empty<ScreenShareVideoFragmentV1>();
                return false;
            }

            offset += serializedFragmentLength;

            if (i == 0)
            {
                sessionId = fragment.SessionId;
                encoding = fragment.Encoding;
                streamEpoch = fragment.StreamEpoch;
                frameId = fragment.FrameId;
                capturedTsUtcMs = fragment.CapturedTsUtcMs;
                width = fragment.Width;
                height = fragment.Height;
                fragmentCount = fragment.FragmentCount;
                isKeyFrame = fragment.IsKeyFrame;
            }
            else if (!string.Equals(sessionId, fragment.SessionId, StringComparison.Ordinal) ||
                     streamEpoch != fragment.StreamEpoch ||
                     frameId != fragment.FrameId ||
                     capturedTsUtcMs != fragment.CapturedTsUtcMs ||
                     width != fragment.Width ||
                     height != fragment.Height ||
                     !string.Equals(encoding, fragment.Encoding, StringComparison.Ordinal) ||
                     fragmentCount != fragment.FragmentCount ||
                     isKeyFrame != fragment.IsKeyFrame ||
                     fragment.FragmentIndex != previousFragmentIndex + 1)
            {
                fragments = Array.Empty<ScreenShareVideoFragmentV1>();
                return false;
            }

            previousFragmentIndex = fragment.FragmentIndex;
            parsedFragments[i] = fragment;
        }

        if (offset != payload.Length)
        {
            fragments = Array.Empty<ScreenShareVideoFragmentV1>();
            return false;
        }

        fragments = parsedFragments;
        return true;
    }

    public static bool TryDeserializeFragmentEnvelope(
        ReadOnlySpan<byte> payload,
        out ScreenShareVideoFragmentV1[] fragments,
        out bool isBatch)
    {
        if (TryDeserializeFragment(payload, out var fragment))
        {
            fragments = [fragment];
            isBatch = false;
            return true;
        }

        if (TryDeserializeFragmentBatch(payload, out fragments))
        {
            isBatch = true;
            return true;
        }

        fragments = Array.Empty<ScreenShareVideoFragmentV1>();
        isBatch = false;
        return false;
    }

    public static byte[] SerializeStreamConfig(ScreenShareVideoStreamConfigV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var normalized = NormalizeStreamConfigForSerialization(message);
        var sessionIdBytes = Encoding.UTF8.GetBytes(normalized.SessionId);
        var encodingBytes = Encoding.UTF8.GetBytes(normalized.Encoding);
        var codecProfileBytes = Encoding.UTF8.GetBytes(normalized.CodecProfile);
        var configBytes = normalized.DecoderConfigData;
        if (sessionIdBytes.Length > ushort.MaxValue ||
            encodingBytes.Length > ushort.MaxValue ||
            codecProfileBytes.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("Screen share video stream config contained an oversized string field.");
        }

        var payload = new byte[
            sizeof(uint) +
            sizeof(byte) +
            sizeof(byte) +
            sizeof(byte) +
            sizeof(ushort) +
            sizeof(ushort) +
            sizeof(ushort) +
            sizeof(long) +
            sizeof(long) +
            sizeof(int) +
            sessionIdBytes.Length +
            encodingBytes.Length +
            codecProfileBytes.Length +
            configBytes.Length];

        var span = payload.AsSpan();
        var offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], BinaryMagic);
        offset += sizeof(uint);
        span[offset++] = BinaryVersion;
        span[offset++] = MessageKindStreamConfig;
        span[offset++] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)sessionIdBytes.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)encodingBytes.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], checked((ushort)codecProfileBytes.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], normalized.StreamEpoch);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], normalized.DisplayInfoRevision);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], configBytes.Length);
        offset += sizeof(int);
        sessionIdBytes.CopyTo(span[offset..]);
        offset += sessionIdBytes.Length;
        encodingBytes.CopyTo(span[offset..]);
        offset += encodingBytes.Length;
        codecProfileBytes.CopyTo(span[offset..]);
        offset += codecProfileBytes.Length;
        configBytes.CopyTo(span[offset..]);
        return payload;
    }

    public static bool TryDeserializeStreamConfig(ReadOnlySpan<byte> payload, out ScreenShareVideoStreamConfigV1 message)
    {
        message = default!;

        if (!TryReadHeader(payload, out var offset, out var kind, out var flags) ||
            kind != MessageKindStreamConfig ||
            flags != 0 ||
            !TryReadUInt16(payload, ref offset, out var sessionIdLength) ||
            !TryReadUInt16(payload, ref offset, out var encodingLength) ||
            !TryReadUInt16(payload, ref offset, out var codecProfileLength) ||
            !TryReadInt64(payload, ref offset, out var streamEpoch) ||
            !TryReadInt64(payload, ref offset, out var displayInfoRevision) ||
            !TryReadInt32(payload, ref offset, out var configLength) ||
            configLength < 0 ||
            configLength > MaxConfigBytes ||
            !TryReadString(payload, ref offset, sessionIdLength, out var sessionId) ||
            !TryReadString(payload, ref offset, encodingLength, out var encoding) ||
            !TryReadString(payload, ref offset, codecProfileLength, out var codecProfile) ||
            !TryReadBytes(payload, ref offset, configLength, out var configBytes) ||
            offset != payload.Length)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sessionId) ||
            streamEpoch <= 0 ||
            !string.Equals(encoding, "h264", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(codecProfile))
        {
            return false;
        }

        message = new ScreenShareVideoStreamConfigV1
        {
            Kind = "screenshare",
            Type = ScreenShareVideoStreamConfigTypeV1,
            SessionId = sessionId.Trim(),
            StreamEpoch = streamEpoch,
            Encoding = encoding.Trim(),
            CodecProfile = codecProfile.Trim(),
            DisplayInfoRevision = Math.Max(0, displayInfoRevision),
            DecoderConfigData = configBytes,
        };
        return true;
    }

    private static ScreenShareVideoFragmentV1 NormalizeFragmentForSerialization(ScreenShareVideoFragmentV1 message)
    {
        var sessionId = (message.SessionId ?? string.Empty).Trim();
        var encoding = (message.Encoding ?? string.Empty).Trim();
        var dataBytes = message.Data ?? Array.Empty<byte>();

        if (!string.Equals(message.Kind, "screenshare", StringComparison.Ordinal) ||
            !string.Equals(message.Type, ScreenShareVideoFragmentTypeV1, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            message.StreamEpoch <= 0 ||
            message.FrameId < 0 ||
            message.CapturedTsUtcMs < 0 ||
            message.Width <= 0 ||
            message.Height <= 0 ||
            !string.Equals(encoding, "h264", StringComparison.OrdinalIgnoreCase) ||
            message.FragmentIndex < 0 ||
            message.FragmentCount <= 0 ||
            message.FragmentIndex >= message.FragmentCount ||
            dataBytes.Length == 0 ||
            dataBytes.Length > MaxFragmentRawBytes)
        {
            throw new InvalidOperationException("Screen share video fragment payload is invalid.");
        }

        return message with
        {
            Kind = "screenshare",
            SessionId = sessionId,
            Encoding = encoding.ToLowerInvariant(),
            Data = dataBytes,
        };
    }

    private static ScreenShareVideoStreamConfigV1 NormalizeStreamConfigForSerialization(ScreenShareVideoStreamConfigV1 message)
    {
        var sessionId = (message.SessionId ?? string.Empty).Trim();
        var encoding = (message.Encoding ?? string.Empty).Trim();
        var codecProfile = (message.CodecProfile ?? string.Empty).Trim();
        var configBytes = message.DecoderConfigData ?? Array.Empty<byte>();

        if (!string.Equals(message.Kind, "screenshare", StringComparison.Ordinal) ||
            !string.Equals(message.Type, ScreenShareVideoStreamConfigTypeV1, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            message.StreamEpoch <= 0 ||
            !string.Equals(encoding, "h264", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(codecProfile) ||
            configBytes.Length > MaxConfigBytes)
        {
            throw new InvalidOperationException("Screen share video stream config payload is invalid.");
        }

        return message with
        {
            Kind = "screenshare",
            SessionId = sessionId,
            Encoding = encoding.ToLowerInvariant(),
            CodecProfile = codecProfile,
            DisplayInfoRevision = Math.Max(0, message.DisplayInfoRevision),
            DecoderConfigData = configBytes,
        };
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> payload, out int offset, out byte kind, out byte flags)
    {
        offset = 0;
        kind = 0;
        flags = 0;

        return TryReadUInt32(payload, ref offset, out var magic) &&
               magic == BinaryMagic &&
               TryReadByte(payload, ref offset, out var version) &&
               version == BinaryVersion &&
               TryReadByte(payload, ref offset, out kind) &&
               TryReadByte(payload, ref offset, out flags);
    }

    private static bool TryReadByte(ReadOnlySpan<byte> payload, ref int offset, out byte value)
    {
        value = 0;
        if ((uint)offset >= (uint)payload.Length)
        {
            return false;
        }

        value = payload[offset++];
        return true;
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> payload, ref int offset, out ushort value)
    {
        value = 0;
        if (payload.Length - offset < sizeof(ushort))
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
        if (payload.Length - offset < sizeof(uint))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        offset += sizeof(uint);
        return true;
    }

    private static bool TryReadInt32(ReadOnlySpan<byte> payload, ref int offset, out int value)
    {
        value = 0;
        if (payload.Length - offset < sizeof(int))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        offset += sizeof(int);
        return true;
    }

    private static bool TryReadInt64(ReadOnlySpan<byte> payload, ref int offset, out long value)
    {
        value = 0;
        if (payload.Length - offset < sizeof(long))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += sizeof(long);
        return true;
    }

    private static bool TryReadString(ReadOnlySpan<byte> payload, ref int offset, int byteLength, out string value)
    {
        value = string.Empty;
        if (byteLength < 0 || payload.Length - offset < byteLength)
        {
            return false;
        }

        try
        {
            value = Encoding.UTF8.GetString(payload.Slice(offset, byteLength));
            offset += byteLength;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadBytes(ReadOnlySpan<byte> payload, ref int offset, int byteLength, out byte[] value)
    {
        value = Array.Empty<byte>();
        if (byteLength < 0 || payload.Length - offset < byteLength)
        {
            return false;
        }

        value = payload.Slice(offset, byteLength).ToArray();
        offset += byteLength;
        return true;
    }
}
