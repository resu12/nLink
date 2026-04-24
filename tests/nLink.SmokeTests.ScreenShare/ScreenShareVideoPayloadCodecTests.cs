using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareVideoPayloadCodecTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoPayloadCodec_StreamConfig_RoundTrips()
    {
        var message = new ScreenShareVideoStreamConfigV1
        {
            SessionId = "session-1",
            StreamEpoch = 2,
            Encoding = "h264",
            CodecProfile = "baseline",
            DisplayInfoRevision = 5,
            DecoderConfigData = new byte[] { 0x01, 0x64, 0x00, 0x1F },
        };

        var payload = ScreenShareVideoPayloadCodec.SerializeStreamConfig(message);

        Assert.True(ScreenShareVideoPayloadCodec.TryDeserializeStreamConfig(payload, out var parsed));
        Assert.Equal(message.SessionId, parsed.SessionId);
        Assert.Equal(message.StreamEpoch, parsed.StreamEpoch);
        Assert.Equal(message.Encoding, parsed.Encoding);
        Assert.Equal(message.CodecProfile, parsed.CodecProfile);
        Assert.Equal(message.DisplayInfoRevision, parsed.DisplayInfoRevision);
        Assert.Equal(message.DecoderConfigData, parsed.DecoderConfigData);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoPayloadCodec_Fragment_RoundTrips()
    {
        var message = new ScreenShareVideoFragmentV1
        {
            SessionId = "session-1",
            StreamEpoch = 3,
            FrameId = 11,
            CapturedTsUtcMs = 1234,
            Width = 1920,
            Height = 1080,
            Encoding = "h264",
            IsKeyFrame = true,
            FragmentIndex = 1,
            FragmentCount = 4,
            Data = new byte[] { 0x11, 0x22, 0x33 },
        };

        var payload = ScreenShareVideoPayloadCodec.SerializeFragment(message);

        Assert.True(ScreenShareVideoPayloadCodec.TryDeserializeFragment(payload, out var parsed));
        Assert.Equal(message.SessionId, parsed.SessionId);
        Assert.Equal(message.StreamEpoch, parsed.StreamEpoch);
        Assert.Equal(message.FrameId, parsed.FrameId);
        Assert.Equal(message.CapturedTsUtcMs, parsed.CapturedTsUtcMs);
        Assert.Equal(message.Width, parsed.Width);
        Assert.Equal(message.Height, parsed.Height);
        Assert.Equal(message.Encoding, parsed.Encoding);
        Assert.Equal(message.IsKeyFrame, parsed.IsKeyFrame);
        Assert.Equal(message.FragmentIndex, parsed.FragmentIndex);
        Assert.Equal(message.FragmentCount, parsed.FragmentCount);
        Assert.Equal(message.Data, parsed.Data);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoPayloadCodec_FragmentBatch_RoundTrips()
    {
        var fragments = new[]
        {
            CreateFragment(fragmentIndex: 0, fragmentCount: 3, data: new byte[] { 0x01, 0x02 }),
            CreateFragment(fragmentIndex: 1, fragmentCount: 3, data: new byte[] { 0x03, 0x04 }),
            CreateFragment(fragmentIndex: 2, fragmentCount: 3, data: new byte[] { 0x05, 0x06 }),
        };
        var serializedFragments = fragments
            .Select(ScreenShareVideoPayloadCodec.SerializeFragment)
            .ToArray();

        var payload = ScreenShareVideoPayloadCodec.SerializeFragmentBatch(serializedFragments);

        Assert.True(ScreenShareVideoPayloadCodec.TryDeserializeFragmentBatch(payload, out var parsed));
        Assert.Equal(fragments.Length, parsed.Length);
        for (var i = 0; i < parsed.Length; i++)
        {
            Assert.Equal(fragments[i].SessionId, parsed[i].SessionId);
            Assert.Equal(fragments[i].StreamEpoch, parsed[i].StreamEpoch);
            Assert.Equal(fragments[i].FrameId, parsed[i].FrameId);
            Assert.Equal(fragments[i].FragmentIndex, parsed[i].FragmentIndex);
            Assert.Equal(fragments[i].FragmentCount, parsed[i].FragmentCount);
            Assert.Equal(fragments[i].Data, parsed[i].Data);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoPayloadCodec_FragmentEnvelope_ExpandsBatch()
    {
        var fragments = new[]
        {
            CreateFragment(fragmentIndex: 0, fragmentCount: 2, data: new byte[] { 0x10 }),
            CreateFragment(fragmentIndex: 1, fragmentCount: 2, data: new byte[] { 0x20 }),
        };
        var payload = ScreenShareVideoPayloadCodec.SerializeFragmentBatch(
            fragments.Select(ScreenShareVideoPayloadCodec.SerializeFragment).ToArray());

        Assert.True(ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(payload, out var parsed, out var isBatch));
        Assert.True(isBatch);
        Assert.Equal(2, parsed.Length);
        Assert.Equal(new byte[] { 0x10 }, parsed[0].Data);
        Assert.Equal(new byte[] { 0x20 }, parsed[1].Data);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoPayloadCodec_FragmentBatch_MixedFrameIds_AreRejected()
    {
        var first = ScreenShareVideoPayloadCodec.SerializeFragment(CreateFragment(fragmentIndex: 0, fragmentCount: 2, data: new byte[] { 0x01 }));
        var second = ScreenShareVideoPayloadCodec.SerializeFragment(CreateFragment(frameId: 12, fragmentIndex: 1, fragmentCount: 2, data: new byte[] { 0x02 }));

        Assert.Throws<InvalidOperationException>(() => ScreenShareVideoPayloadCodec.SerializeFragmentBatch(new[] { first, second }));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoPayloadCodec_FragmentBatch_TruncatedPayload_IsRejected()
    {
        var payload = ScreenShareVideoPayloadCodec.SerializeFragmentBatch(
            new[]
            {
                ScreenShareVideoPayloadCodec.SerializeFragment(CreateFragment(fragmentIndex: 0, fragmentCount: 2, data: new byte[] { 0x01 })),
                ScreenShareVideoPayloadCodec.SerializeFragment(CreateFragment(fragmentIndex: 1, fragmentCount: 2, data: new byte[] { 0x02 })),
            });
        var truncated = payload[..^1];

        Assert.False(ScreenShareVideoPayloadCodec.TryDeserializeFragmentBatch(truncated, out _));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoPayloadCodec_Fragment_InvalidIndexes_AreRejected()
    {
        var message = new ScreenShareVideoFragmentV1
        {
            SessionId = "session-1",
            StreamEpoch = 3,
            FrameId = 11,
            CapturedTsUtcMs = 1234,
            Width = 1920,
            Height = 1080,
            Encoding = "h264",
            FragmentIndex = 4,
            FragmentCount = 4,
            Data = new byte[] { 0x11 },
        };

        Assert.Throws<InvalidOperationException>(() => ScreenShareVideoPayloadCodec.SerializeFragment(message));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoPayloadCodec_TruncatedPayload_IsRejected()
    {
        var message = new ScreenShareVideoFragmentV1
        {
            SessionId = "session-1",
            StreamEpoch = 3,
            FrameId = 11,
            CapturedTsUtcMs = 1234,
            Width = 1920,
            Height = 1080,
            Encoding = "h264",
            FragmentIndex = 0,
            FragmentCount = 1,
            Data = new byte[] { 0x11, 0x22, 0x33 },
        };

        var payload = ScreenShareVideoPayloadCodec.SerializeFragment(message);
        var truncated = payload[..^1];

        Assert.False(ScreenShareVideoPayloadCodec.TryDeserializeFragment(truncated, out _));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoPayloadCodec_Fragment_NonH264Encoding_IsRejected()
    {
        var message = new ScreenShareVideoFragmentV1
        {
            SessionId = "session-1",
            StreamEpoch = 3,
            FrameId = 11,
            CapturedTsUtcMs = 1234,
            Width = 1920,
            Height = 1080,
            Encoding = "jpeg",
            FragmentIndex = 0,
            FragmentCount = 1,
            Data = new byte[] { 0x11, 0x22, 0x33 },
        };

        Assert.Throws<InvalidOperationException>(() => ScreenShareVideoPayloadCodec.SerializeFragment(message));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoPayloadCodec_StreamConfig_NonH264Encoding_IsRejectedOnDeserialize()
    {
        var valid = new ScreenShareVideoStreamConfigV1
        {
            SessionId = "session-1",
            StreamEpoch = 2,
            Encoding = "h264",
            CodecProfile = "baseline",
            DisplayInfoRevision = 5,
            DecoderConfigData = new byte[] { 0x01, 0x64, 0x00, 0x1F },
        };

        var payload = ScreenShareVideoPayloadCodec.SerializeStreamConfig(valid);
        var mutated = payload.ToArray();

        var sessionIdLength = BitConverter.ToUInt16(mutated, 7);
        var encodingLength = BitConverter.ToUInt16(mutated, 9);
        var offset = 4 + 1 + 1 + 1 + 2 + 2 + 2 + 8 + 8 + 4 + sessionIdLength;
        Array.Copy(System.Text.Encoding.UTF8.GetBytes("jpeg"), 0, mutated, offset, encodingLength);

        Assert.False(ScreenShareVideoPayloadCodec.TryDeserializeStreamConfig(mutated, out _));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFragmenter_HasOwnBudgetConstant()
    {
        Assert.Equal(24_000, ScreenShareVideoFragmenter.MaxFragmentRawBytes);
        Assert.Equal(ScreenShareVideoFragmenter.MaxFragmentRawBytes, ScreenShareVideoPayloadCodec.MaxFragmentRawBytes);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFragmenter_SplitsAccessUnitIntoIndexedFragments()
    {
        var accessUnit = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();

        var fragments = ScreenShareVideoFragmenter.FragmentAccessUnit(
            sessionId: "session-1",
            streamEpoch: 9,
            frameId: 17,
            capturedTsUtcMs: 111,
            width: 1280,
            height: 720,
            encoding: "h264",
            isKeyFrame: true,
            accessUnitBytes: accessUnit,
            maxFragmentRawBytes: 4);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, fragments[0].Data);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, fragments[1].Data);
        Assert.Equal(new byte[] { 8, 9 }, fragments[2].Data);
        Assert.All(fragments, fragment => Assert.Equal(3, fragment.FragmentCount));
        Assert.Equal(new[] { 0, 1, 2 }, fragments.Select(fragment => fragment.FragmentIndex));
    }

    private static ScreenShareVideoFragmentV1 CreateFragment(
        long frameId = 11,
        int fragmentIndex = 0,
        int fragmentCount = 1,
        byte[]? data = null)
    {
        return new ScreenShareVideoFragmentV1
        {
            SessionId = "session-1",
            StreamEpoch = 3,
            FrameId = frameId,
            CapturedTsUtcMs = 1234,
            Width = 1920,
            Height = 1080,
            Encoding = "h264",
            IsKeyFrame = true,
            FragmentIndex = fragmentIndex,
            FragmentCount = fragmentCount,
            Data = data ?? new byte[] { 0x11, 0x22, 0x33 },
        };
    }
}
