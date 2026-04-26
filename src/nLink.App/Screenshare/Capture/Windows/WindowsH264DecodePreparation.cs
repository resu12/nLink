using System;
using System.Collections.Generic;
using System.IO;

namespace NLink.App.Services.ScreenCapture;

internal static class WindowsH264DecodePreparation
{
    internal sealed class DecoderConfiguration
    {
        public DecoderConfiguration(byte[] decoderConfigData, int nalLengthSize, List<byte[]> spsUnits, List<byte[]> ppsUnits, int expectedWidth, int expectedHeight)
        {
            DecoderConfigData = decoderConfigData;
            NalLengthSize = nalLengthSize;
            SpsUnits = spsUnits;
            PpsUnits = ppsUnits;
            ExpectedWidth = expectedWidth;
            ExpectedHeight = expectedHeight;
            AnnexBDecoderConfig = BuildAnnexBDecoderConfig(spsUnits, ppsUnits);
        }

        public byte[] DecoderConfigData { get; }

        public int NalLengthSize { get; }

        public List<byte[]> SpsUnits { get; }

        public List<byte[]> PpsUnits { get; }

        public int ExpectedWidth { get; }

        public int ExpectedHeight { get; }

        public byte[] AnnexBDecoderConfig { get; }
    }

    public static bool TryCreateDecoderConfiguration(byte[] decoderConfigData, out DecoderConfiguration? configuration)
    {
        configuration = null;
        decoderConfigData ??= Array.Empty<byte>();
        if (!TryParseAvcConfiguration(decoderConfigData, out var nalLengthSize, out var spsUnits, out var ppsUnits))
        {
            return false;
        }

        var expectedWidth = 0;
        var expectedHeight = 0;
        foreach (var sps in spsUnits)
        {
            if (TryParseH264SpsDimensions(sps, out expectedWidth, out expectedHeight))
            {
                break;
            }
        }

        configuration = new DecoderConfiguration(decoderConfigData, nalLengthSize, spsUnits, ppsUnits, expectedWidth, expectedHeight);
        return true;
    }

    public static bool TryParseNalLengthSize(byte[] decoderConfigData, out int nalLengthSize)
    {
        nalLengthSize = 0;
        if (decoderConfigData.Length < 5)
        {
            return false;
        }

        nalLengthSize = (decoderConfigData[4] & 0x03) + 1;
        return nalLengthSize is >= 1 and <= 4;
    }

    public static bool TryParseExpectedCodedSize(byte[] decoderConfigData, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!TryParseAvcConfiguration(decoderConfigData, out _, out var spsUnits, out _))
        {
            return false;
        }

        foreach (var sps in spsUnits)
        {
            if (TryParseH264SpsDimensions(sps, out width, out height))
            {
                return true;
            }
        }

        width = 0;
        height = 0;
        return false;
    }

    public static ReadOnlyMemory<byte> NormalizeForMediaFoundation(ReadOnlyMemory<byte> encodedBytes, DecoderConfiguration? configuration)
    {
        if (encodedBytes.IsEmpty ||
            configuration is null ||
            configuration.NalLengthSize <= 0 ||
            !LooksLikeAnnexB(encodedBytes.Span))
        {
            return encodedBytes;
        }

        var converted = ConvertAnnexBToLengthPrefixed(encodedBytes.Span, configuration.NalLengthSize, stripDecoderConfigNalUnits: false);
        return converted.Length > 0 ? converted : encodedBytes;
    }

    public static byte[] BuildAnnexBPacketForSoftwareDecode(ReadOnlyMemory<byte> encodedBytes, bool isKeyFrame, DecoderConfiguration? configuration, bool prependDecoderConfig)
    {
        if (encodedBytes.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        byte[] annexBPayload;
        if (LooksLikeAnnexB(encodedBytes.Span))
        {
            annexBPayload = encodedBytes.ToArray();
        }
        else if (configuration is not null && configuration.NalLengthSize > 0)
        {
            annexBPayload = ConvertLengthPrefixedToAnnexB(encodedBytes.Span, configuration.NalLengthSize, stripDecoderConfigNalUnits: false);
        }
        else
        {
            annexBPayload = encodedBytes.ToArray();
        }

        if (annexBPayload.Length == 0)
        {
            return encodedBytes.ToArray();
        }

        var shouldPrefixDecoderConfig = configuration is not null &&
                                        configuration.AnnexBDecoderConfig.Length > 0 &&
                                        (prependDecoderConfig || (isKeyFrame && !ContainsDecoderConfigNalUnits(annexBPayload)));
        if (!shouldPrefixDecoderConfig)
        {
            return annexBPayload;
        }

        var result = new byte[configuration!.AnnexBDecoderConfig.Length + annexBPayload.Length];
        Buffer.BlockCopy(configuration.AnnexBDecoderConfig, 0, result, 0, configuration.AnnexBDecoderConfig.Length);
        Buffer.BlockCopy(annexBPayload, 0, result, configuration.AnnexBDecoderConfig.Length, annexBPayload.Length);
        return result;
    }

    public static byte[] DebugConvertAnnexBToLengthPrefixed(byte[] annexBBytes, byte[] decoderConfigData)
    {
        ArgumentNullException.ThrowIfNull(annexBBytes);
        ArgumentNullException.ThrowIfNull(decoderConfigData);

        if (!TryParseNalLengthSize(decoderConfigData, out var nalLengthSize))
        {
            throw new InvalidOperationException("Decoder config data does not contain a valid AVCC NAL length size.");
        }

        return ConvertAnnexBToLengthPrefixed(annexBBytes, nalLengthSize, stripDecoderConfigNalUnits: false);
    }

    public static bool TryParseAvcConfiguration(
        byte[] avcC,
        out int nalLengthSize,
        out List<byte[]> spsUnits,
        out List<byte[]> ppsUnits)
    {
        nalLengthSize = 4;
        spsUnits = new List<byte[]>();
        ppsUnits = new List<byte[]>();
        if (avcC.Length < 7)
        {
            return false;
        }

        nalLengthSize = (avcC[4] & 0x03) + 1;
        var offset = 5;
        var spsCount = avcC[offset++] & 0x1F;
        for (var i = 0; i < spsCount; i++)
        {
            if (!TryReadLengthPrefixedBlob(avcC, ref offset, out var sps))
            {
                return false;
            }

            spsUnits.Add(sps);
        }

        if (offset >= avcC.Length)
        {
            return false;
        }

        var ppsCount = avcC[offset++];
        for (var i = 0; i < ppsCount; i++)
        {
            if (!TryReadLengthPrefixedBlob(avcC, ref offset, out var pps))
            {
                return false;
            }

            ppsUnits.Add(pps);
        }

        return spsUnits.Count > 0;
    }

    public static bool LooksLikeAnnexB(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i <= bytes.Length - 3; i++)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0)
            {
                if (bytes[i + 2] == 1)
                {
                    return true;
                }

                if (i + 3 < bytes.Length && bytes[i + 2] == 0 && bytes[i + 3] == 1)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static byte[] ConvertAnnexBToLengthPrefixed(ReadOnlySpan<byte> annexBBytes, int nalLengthSize, bool stripDecoderConfigNalUnits)
    {
        using var stream = new MemoryStream(annexBBytes.Length);
        var offset = 0;
        while (TryReadAnnexBNalUnit(annexBBytes, ref offset, out var nalUnit))
        {
            if (nalUnit.Length == 0)
            {
                continue;
            }

            if (stripDecoderConfigNalUnits && IsDecoderConfigNalUnit(nalUnit))
            {
                continue;
            }

            for (var shift = (nalLengthSize - 1) * 8; shift >= 0; shift -= 8)
            {
                stream.WriteByte((byte)((nalUnit.Length >> shift) & 0xFF));
            }

            stream.Write(nalUnit);
        }

        return stream.ToArray();
    }

    public static byte[] ConvertLengthPrefixedToAnnexB(ReadOnlySpan<byte> bytes, int nalLengthSize, bool stripDecoderConfigNalUnits)
    {
        using var stream = new MemoryStream(bytes.Length + 64);
        var offset = 0;
        while (offset + nalLengthSize <= bytes.Length)
        {
            var nalLength = 0;
            for (var i = 0; i < nalLengthSize; i++)
            {
                nalLength = (nalLength << 8) | bytes[offset + i];
            }

            offset += nalLengthSize;
            if (nalLength <= 0 || offset + nalLength > bytes.Length)
            {
                break;
            }

            var nalUnit = bytes.Slice(offset, nalLength);
            offset += nalLength;
            if (stripDecoderConfigNalUnits && IsDecoderConfigNalUnit(nalUnit))
            {
                continue;
            }

            stream.WriteByte(0);
            stream.WriteByte(0);
            stream.WriteByte(0);
            stream.WriteByte(1);
            stream.Write(nalUnit);
        }

        return stream.ToArray();
    }

    public static bool ContainsDecoderConfigNalUnits(ReadOnlySpan<byte> annexBBytes)
    {
        var offset = 0;
        while (TryReadAnnexBNalUnit(annexBBytes, ref offset, out var nalUnit))
        {
            if (IsDecoderConfigNalUnit(nalUnit))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] BuildAnnexBDecoderConfig(IEnumerable<byte[]> spsUnits, IEnumerable<byte[]> ppsUnits)
    {
        using var stream = new MemoryStream();
        foreach (var nal in spsUnits)
        {
            WriteAnnexBNalUnit(stream, nal);
        }

        foreach (var nal in ppsUnits)
        {
            WriteAnnexBNalUnit(stream, nal);
        }

        return stream.ToArray();
    }

    private static void WriteAnnexBNalUnit(Stream stream, byte[] nalUnit)
    {
        if (nalUnit.Length == 0)
        {
            return;
        }

        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(1);
        stream.Write(nalUnit, 0, nalUnit.Length);
    }

    private static bool IsDecoderConfigNalUnit(ReadOnlySpan<byte> nalUnit)
    {
        if (nalUnit.IsEmpty)
        {
            return false;
        }

        var nalType = nalUnit[0] & 0x1F;
        return nalType is 7 or 8;
    }

    private static bool TryReadLengthPrefixedBlob(byte[] bytes, ref int offset, out byte[] blob)
    {
        blob = Array.Empty<byte>();
        if (offset + 2 > bytes.Length)
        {
            return false;
        }

        var length = (bytes[offset] << 8) | bytes[offset + 1];
        offset += 2;
        if (length <= 0 || offset + length > bytes.Length)
        {
            return false;
        }

        blob = new byte[length];
        Buffer.BlockCopy(bytes, offset, blob, 0, length);
        offset += length;
        return true;
    }

    private static bool TryParseH264SpsDimensions(byte[] spsNalUnit, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (spsNalUnit.Length < 4)
        {
            return false;
        }

        try
        {
            var rbsp = RemoveEmulationPreventionBytes(spsNalUnit.AsSpan(1));
            var reader = new H264BitReader(rbsp);
            var profileIdc = reader.ReadBits(8);
            reader.SkipBits(8);
            reader.SkipBits(8);
            reader.ReadUnsignedExpGolomb();

            var chromaFormatIdc = 1;
            if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chromaFormatIdc = reader.ReadUnsignedExpGolomb();
                if (chromaFormatIdc == 3)
                {
                    reader.SkipBits(1);
                }

                reader.ReadUnsignedExpGolomb();
                reader.ReadUnsignedExpGolomb();
                reader.SkipBits(1);
                if (reader.ReadFlag())
                {
                    var scalingCount = chromaFormatIdc == 3 ? 12 : 8;
                    for (var i = 0; i < scalingCount; i++)
                    {
                        if (reader.ReadFlag())
                        {
                            SkipScalingList(reader, i < 6 ? 16 : 64);
                        }
                    }
                }
            }

            reader.ReadUnsignedExpGolomb();
            var picOrderCntType = reader.ReadUnsignedExpGolomb();
            if (picOrderCntType == 0)
            {
                reader.ReadUnsignedExpGolomb();
            }
            else if (picOrderCntType == 1)
            {
                reader.SkipBits(1);
                reader.ReadSignedExpGolomb();
                reader.ReadSignedExpGolomb();
                var cycleCount = reader.ReadUnsignedExpGolomb();
                for (var i = 0; i < cycleCount; i++)
                {
                    reader.ReadSignedExpGolomb();
                }
            }

            reader.ReadUnsignedExpGolomb();
            reader.SkipBits(1);
            var picWidthInMbsMinus1 = reader.ReadUnsignedExpGolomb();
            var picHeightInMapUnitsMinus1 = reader.ReadUnsignedExpGolomb();
            var frameMbsOnlyFlag = reader.ReadFlag();
            if (!frameMbsOnlyFlag)
            {
                reader.SkipBits(1);
            }

            reader.SkipBits(1);
            var frameCroppingFlag = reader.ReadFlag();
            var cropLeft = 0;
            var cropRight = 0;
            var cropTop = 0;
            var cropBottom = 0;
            if (frameCroppingFlag)
            {
                cropLeft = reader.ReadUnsignedExpGolomb();
                cropRight = reader.ReadUnsignedExpGolomb();
                cropTop = reader.ReadUnsignedExpGolomb();
                cropBottom = reader.ReadUnsignedExpGolomb();
            }

            var frameWidth = (picWidthInMbsMinus1 + 1) * 16;
            var frameHeight = (2 - (frameMbsOnlyFlag ? 1 : 0)) * (picHeightInMapUnitsMinus1 + 1) * 16;
            GetCropUnits(chromaFormatIdc, frameMbsOnlyFlag, out var cropUnitX, out var cropUnitY);
            frameWidth -= (cropLeft + cropRight) * cropUnitX;
            frameHeight -= (cropTop + cropBottom) * cropUnitY;
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                return false;
            }

            width = frameWidth;
            height = frameHeight;
            return true;
        }
        catch
        {
            width = 0;
            height = 0;
            return false;
        }
    }

    private static byte[] RemoveEmulationPreventionBytes(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        using var stream = new MemoryStream(source.Length);
        var zeroCount = 0;
        foreach (var next in source)
        {
            if (zeroCount >= 2 && next == 0x03)
            {
                zeroCount = 0;
                continue;
            }

            stream.WriteByte(next);
            zeroCount = next == 0 ? zeroCount + 1 : 0;
        }

        return stream.ToArray();
    }

    private static void SkipScalingList(H264BitReader reader, int count)
    {
        var lastScale = 8;
        var nextScale = 8;
        for (var i = 0; i < count; i++)
        {
            if (nextScale != 0)
            {
                var deltaScale = reader.ReadSignedExpGolomb();
                nextScale = (lastScale + deltaScale + 256) % 256;
            }

            lastScale = nextScale == 0 ? lastScale : nextScale;
        }
    }

    private static void GetCropUnits(int chromaFormatIdc, bool frameMbsOnlyFlag, out int cropUnitX, out int cropUnitY)
    {
        var frameMultiplier = frameMbsOnlyFlag ? 1 : 2;
        switch (chromaFormatIdc)
        {
            case 0:
                cropUnitX = 1;
                cropUnitY = frameMultiplier;
                return;
            case 1:
                cropUnitX = 2;
                cropUnitY = 2 * frameMultiplier;
                return;
            case 2:
                cropUnitX = 2;
                cropUnitY = frameMultiplier;
                return;
            default:
                cropUnitX = 1;
                cropUnitY = frameMultiplier;
                return;
        }
    }

    private static bool TryReadAnnexBNalUnit(ReadOnlySpan<byte> bytes, ref int offset, out ReadOnlySpan<byte> nalUnit)
    {
        nalUnit = default;
        var startCodeOffset = FindAnnexBStartCode(bytes, offset, out var startCodeLength);
        if (startCodeOffset < 0)
        {
            return false;
        }

        var nalStart = startCodeOffset + startCodeLength;
        var nextStart = FindAnnexBStartCode(bytes, nalStart, out _);
        var nalEnd = nextStart >= 0 ? nextStart : bytes.Length;

        while (nalEnd > nalStart && bytes[nalEnd - 1] == 0)
        {
            nalEnd--;
        }

        nalUnit = bytes.Slice(nalStart, Math.Max(0, nalEnd - nalStart));
        offset = nextStart >= 0 ? nextStart : bytes.Length;
        return true;
    }

    private static int FindAnnexBStartCode(ReadOnlySpan<byte> bytes, int offset, out int startCodeLength)
    {
        for (var i = Math.Max(0, offset); i <= bytes.Length - 3; i++)
        {
            if (bytes[i] != 0 || bytes[i + 1] != 0)
            {
                continue;
            }

            if (bytes[i + 2] == 1)
            {
                startCodeLength = 3;
                return i;
            }

            if (i <= bytes.Length - 4 && bytes[i + 2] == 0 && bytes[i + 3] == 1)
            {
                startCodeLength = 4;
                return i;
            }
        }

        startCodeLength = 0;
        return -1;
    }

    private sealed class H264BitReader
    {
        private readonly byte[] bytes;
        private int bitOffset;

        public H264BitReader(byte[] bytes)
        {
            this.bytes = bytes;
        }

        public int ReadBits(int bitCount)
        {
            var value = 0;
            for (var i = 0; i < bitCount; i++)
            {
                value = (value << 1) | ReadBit();
            }

            return value;
        }

        public void SkipBits(int bitCount)
        {
            bitOffset += bitCount;
        }

        public bool ReadFlag() => ReadBit() != 0;

        public int ReadUnsignedExpGolomb()
        {
            var leadingZeroBits = 0;
            while (ReadBit() == 0)
            {
                leadingZeroBits++;
            }

            var codeNum = (1 << leadingZeroBits) - 1;
            if (leadingZeroBits > 0)
            {
                codeNum += ReadBits(leadingZeroBits);
            }

            return codeNum;
        }

        public int ReadSignedExpGolomb()
        {
            var codeNum = ReadUnsignedExpGolomb();
            var sign = ((codeNum & 1) == 0) ? -1 : 1;
            return sign * ((codeNum + 1) / 2);
        }

        private int ReadBit()
        {
            var byteOffset = bitOffset / 8;
            if (byteOffset >= bytes.Length)
            {
                throw new InvalidOperationException("Unexpected end of SPS bitstream.");
            }

            var bitIndex = 7 - (bitOffset % 8);
            bitOffset++;
            return (bytes[byteOffset] >> bitIndex) & 0x01;
        }
    }
}
