using System;
using System.Threading;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal readonly record struct H264DecodePreparationResult(
    bool ShouldDecode,
    bool ConfigApplied,
    long EffectiveStreamEpoch,
    long ConfiguredStreamEpoch);

internal sealed class H264DecodeStreamState
{
    private readonly EncodedFrameBitmapDecoder encodedFrameDecoder;
    private long configuredStreamEpoch;
    private ScreenShareVideoStreamConfigV1? lastStreamConfig;

    public H264DecodeStreamState(EncodedFrameBitmapDecoder encodedFrameDecoder)
    {
        this.encodedFrameDecoder = encodedFrameDecoder ?? throw new ArgumentNullException(nameof(encodedFrameDecoder));
    }

    public long ConfiguredStreamEpoch => Interlocked.Read(ref configuredStreamEpoch);

    public H264DecodePreparationResult Prepare(
        string encoding,
        long streamEpoch,
        ScreenShareVideoStreamConfigV1? streamConfig,
        Action? onEpochChanged = null)
    {
        if (!IsH264Encoding(encoding))
        {
            return new H264DecodePreparationResult(
                ShouldDecode: true,
                ConfigApplied: false,
                EffectiveStreamEpoch: streamEpoch,
                ConfiguredStreamEpoch: ConfiguredStreamEpoch);
        }

        if (streamConfig is not null)
        {
            var nextEpoch = streamConfig.StreamEpoch > 0 ? streamConfig.StreamEpoch : streamEpoch;
            var currentEpoch = ConfiguredStreamEpoch;
            if (currentEpoch > 0 && nextEpoch > 0 && currentEpoch != nextEpoch)
            {
                onEpochChanged?.Invoke();
                Reset();
            }

            encodedFrameDecoder.ConfigureH264Stream(streamConfig);
            lastStreamConfig = streamConfig;
            if (nextEpoch > 0)
            {
                Interlocked.Exchange(ref configuredStreamEpoch, nextEpoch);
            }

            return new H264DecodePreparationResult(
                ShouldDecode: true,
                ConfigApplied: true,
                EffectiveStreamEpoch: nextEpoch,
                ConfiguredStreamEpoch: ConfiguredStreamEpoch);
        }

        var configuredEpoch = ConfiguredStreamEpoch;
        if (streamEpoch <= 0 || configuredEpoch == 0 || configuredEpoch != streamEpoch)
        {
            Reset();
            return new H264DecodePreparationResult(
                ShouldDecode: false,
                ConfigApplied: false,
                EffectiveStreamEpoch: streamEpoch,
                ConfiguredStreamEpoch: configuredEpoch);
        }

        return new H264DecodePreparationResult(
            ShouldDecode: true,
            ConfigApplied: false,
            EffectiveStreamEpoch: streamEpoch,
            ConfiguredStreamEpoch: configuredEpoch);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref configuredStreamEpoch, 0);
        lastStreamConfig = null;
        encodedFrameDecoder.ResetH264Stream();
    }

    public void ResetDecoderOnly()
    {
        encodedFrameDecoder.ResetH264Stream();
        if (lastStreamConfig is not null)
        {
            encodedFrameDecoder.ConfigureH264Stream(lastStreamConfig);
        }
    }

    public static bool IsH264Encoding(string? encoding)
    {
        return string.Equals(encoding?.Trim(), "h264", StringComparison.OrdinalIgnoreCase);
    }
}
