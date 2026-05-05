using System;
using System.Drawing;
using NLink.App.Configuration;

namespace NLink.App.Services.ScreenCapture;

internal readonly record struct WindowsH264EncodeProfile(
    string ProfileName,
    int Width,
    int Height,
    uint TargetBitrate,
    int TargetFramesPerSecond,
    bool TransportIpOnly);

internal static class WindowsH264EncodePolicy
{
    private const int PreviewNormalMaxWidth = 1280;
    private const int PreviewNormalMaxHeight = 720;
    private const int PreviewBandwidthReducedMaxWidth = 960;
    private const int PreviewBandwidthReducedMaxHeight = 540;
    private const int TransportNormalMaxWidth = 1440;
    private const int TransportNormalMaxHeight = 810;
    private const int TransportBandwidthReducedMaxWidth = 1280;
    private const int TransportBandwidthReducedMaxHeight = 720;
    private const int TransportTunaQualityMaxWidth = 1600;
    private const int TransportTunaQualityMaxHeight = 900;
    private const int NormalTargetFramesPerSecond = 8;
    private const int TunaQualityTargetFramesPerSecond = 15;
    private const int BandwidthReducedTargetFramesPerSecond = 5;
    private const uint NormalBitrateFloor = 1_800_000;
    private const uint NormalBitrateCeiling = 2_400_000;
    private const uint TransportIpOnlyNormalBitrateFloor = 4_500_000;
    private const uint TransportIpOnlyNormalBitrateCeiling = 6_000_000;
    private const uint TransportIpOnlyTunaQualityBitrateFloor = 6_000_000;
    private const uint TransportIpOnlyTunaQualityBitrateCeiling = 9_000_000;
    private const uint BandwidthReducedBitrateFloor = 800_000;
    private const uint BandwidthReducedBitrateCeiling = 1_100_000;
    private const uint TransportIpOnlyBandwidthReducedBitrateFloor = 2_000_000;
    private const uint TransportIpOnlyBandwidthReducedBitrateCeiling = 3_000_000;

    internal static WindowsH264EncodeProfile ResolveProfile(
        int sourceWidth,
        int sourceHeight,
        int targetFramesPerSecond,
        ScreenShareTransportTuningLevel tuningLevel,
        bool transportIpOnly = false)
    {
        var normalizedFramesPerSecond = ResolveProfileTargetFramesPerSecond(targetFramesPerSecond, tuningLevel, transportIpOnly);
        var normalizedSize = NormalizeDimensions(sourceWidth, sourceHeight, tuningLevel, transportIpOnly);
        var bitrate = ComputeTargetBitrate(
            normalizedSize.Width,
            normalizedSize.Height,
            normalizedFramesPerSecond,
            tuningLevel,
            transportIpOnly);

        return new WindowsH264EncodeProfile(
            GetProfileName(tuningLevel),
            normalizedSize.Width,
            normalizedSize.Height,
            bitrate,
            normalizedFramesPerSecond,
            transportIpOnly);
    }

    internal static Size NormalizeDimensions(
        int width,
        int height,
        ScreenShareTransportTuningLevel tuningLevel,
        bool transportIpOnly = false)
    {
        var normalizedWidth = Math.Max(2, width & ~1);
        var normalizedHeight = Math.Max(2, height & ~1);

        var maxSize = GetModeMaxSize(tuningLevel, transportIpOnly);
        if (normalizedWidth <= maxSize.Width && normalizedHeight <= maxSize.Height)
        {
            return new Size(normalizedWidth, normalizedHeight);
        }

        var scale = Math.Min((double)maxSize.Width / normalizedWidth, (double)maxSize.Height / normalizedHeight);
        normalizedWidth = Math.Max(2, ((int)Math.Floor(normalizedWidth * scale)) & ~1);
        normalizedHeight = Math.Max(2, ((int)Math.Floor(normalizedHeight * scale)) & ~1);
        return new Size(normalizedWidth, normalizedHeight);
    }

    internal static uint ComputeTargetBitrate(
        int width,
        int height,
        int targetFramesPerSecond,
        ScreenShareTransportTuningLevel tuningLevel,
        bool transportIpOnly = false)
    {
        var normalizedFps = Math.Max(1, targetFramesPerSecond);
        if (transportIpOnly)
        {
            if (tuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced)
            {
                var reducedTransportBaseline = Math.Round((double)width * height * normalizedFps / 1.4d);
                return checked((uint)Math.Clamp(
                    reducedTransportBaseline,
                    TransportIpOnlyBandwidthReducedBitrateFloor,
                    TransportIpOnlyBandwidthReducedBitrateCeiling));
            }

            if (UseTunaQualityTransportProfile(transportIpOnly, tuningLevel))
            {
                var tunaQualityBaseline = Math.Round((double)width * height * normalizedFps / 2.4d);
                return checked((uint)Math.Clamp(
                    tunaQualityBaseline,
                    TransportIpOnlyTunaQualityBitrateFloor,
                    TransportIpOnlyTunaQualityBitrateCeiling));
            }

            var normalTransportBaseline = Math.Round((double)width * height * normalizedFps / 1.25d);
            return checked((uint)Math.Clamp(
                normalTransportBaseline,
                TransportIpOnlyNormalBitrateFloor,
                TransportIpOnlyNormalBitrateCeiling));
        }

        if (tuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced)
        {
            var reducedBaseline = Math.Round((double)width * height * normalizedFps / 3.5d);
            return checked((uint)Math.Clamp(
                reducedBaseline,
                BandwidthReducedBitrateFloor,
                BandwidthReducedBitrateCeiling));
        }

        var baseline = Math.Clamp((double)width * height * normalizedFps / 3d, NormalBitrateFloor, NormalBitrateCeiling);
        var multiplier = tuningLevel switch
        {
            ScreenShareTransportTuningLevel.QualityProtected => 0.75d,
            _ => 1d,
        };

        return checked((uint)Math.Clamp(Math.Round(baseline * multiplier), 1d, NormalBitrateCeiling));
    }

    private static int ResolveProfileTargetFramesPerSecond(
        int requestedFramesPerSecond,
        ScreenShareTransportTuningLevel tuningLevel,
        bool transportIpOnly)
    {
        if (tuningLevel != ScreenShareTransportTuningLevel.BandwidthReduced &&
            UseTunaQualityTransportProfile(transportIpOnly, tuningLevel))
        {
            return Math.Clamp(requestedFramesPerSecond, 1, TunaQualityTargetFramesPerSecond);
        }

        return tuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced
            ? Math.Clamp(requestedFramesPerSecond, 1, BandwidthReducedTargetFramesPerSecond)
            : Math.Clamp(requestedFramesPerSecond, 1, NormalTargetFramesPerSecond);
    }

    private static string GetProfileName(ScreenShareTransportTuningLevel tuningLevel)
    {
        return tuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced
            ? "reduced"
            : "normal";
    }

    private static Size GetModeMaxSize(ScreenShareTransportTuningLevel tuningLevel, bool transportIpOnly)
    {
        var baseWidth = tuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced
            ? transportIpOnly ? TransportBandwidthReducedMaxWidth : PreviewBandwidthReducedMaxWidth
            : UseTunaQualityTransportProfile(transportIpOnly, tuningLevel) ? TransportTunaQualityMaxWidth
            : transportIpOnly ? TransportNormalMaxWidth : PreviewNormalMaxWidth;
        var baseHeight = tuningLevel == ScreenShareTransportTuningLevel.BandwidthReduced
            ? transportIpOnly ? TransportBandwidthReducedMaxHeight : PreviewBandwidthReducedMaxHeight
            : UseTunaQualityTransportProfile(transportIpOnly, tuningLevel) ? TransportTunaQualityMaxHeight
            : transportIpOnly ? TransportNormalMaxHeight : PreviewNormalMaxHeight;

        var scale = FeatureFlags.ScreenShareScale;
        if (scale >= 1d)
        {
            return new Size(baseWidth, baseHeight);
        }

        var scaledWidth = Math.Max(2, ((int)Math.Floor(baseWidth * scale)) & ~1);
        var scaledHeight = Math.Max(2, ((int)Math.Floor(baseHeight * scale)) & ~1);
        return new Size(scaledWidth, scaledHeight);
    }

    private static bool UseTunaQualityTransportProfile(bool transportIpOnly, ScreenShareTransportTuningLevel tuningLevel)
    {
        return transportIpOnly &&
               tuningLevel == ScreenShareTransportTuningLevel.Normal &&
               string.Equals(
                   FeatureFlags.ScreenShareQualityProfile,
                   FeatureFlags.ScreenShareQualityProfileTunaQuality,
                   StringComparison.Ordinal);
    }
}
