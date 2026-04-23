using System;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

/// <summary>
/// Represents a single encoded screen-capture frame.
/// </summary>
public sealed class ScreenCaptureFrameEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenCaptureFrameEventArgs"/> class.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="encodedFrameData">Encoded frame payload.</param>
    /// <param name="encoding">Encoding name such as jpeg or png.</param>
    /// <param name="capturedTsUtcMs">UTC capture timestamp in Unix milliseconds; 0 when unavailable.</param>
    /// <param name="isKeyFrame">Whether the encoded payload represents a key frame.</param>
    /// <param name="streamEpoch">Monotonic stream epoch used to distinguish stream restarts.</param>
    /// <param name="streamConfig">Optional stream configuration required to initialize a decoder for this epoch.</param>
    public ScreenCaptureFrameEventArgs(
        int width,
        int height,
        byte[] encodedFrameData,
        string encoding,
        long capturedTsUtcMs = 0,
        bool isKeyFrame = false,
        long streamEpoch = 0,
        ScreenShareVideoStreamConfigV1? streamConfig = null)
    {
        Width = width;
        Height = height;
        EncodedFrameData = encodedFrameData ?? Array.Empty<byte>();
        Encoding = string.IsNullOrWhiteSpace(encoding) ? "unknown" : encoding;
        CapturedTsUtcMs = capturedTsUtcMs > 0 ? capturedTsUtcMs : 0;
        IsKeyFrame = isKeyFrame;
        StreamEpoch = streamEpoch > 0 ? streamEpoch : 0;
        StreamConfig = streamConfig;
    }

    /// <summary>
    /// Gets the frame width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the frame height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the encoded frame bytes.
    /// </summary>
    public byte[] EncodedFrameData { get; }

    /// <summary>
    /// Gets the payload encoding name.
    /// </summary>
    public string Encoding { get; }

    /// <summary>
    /// Gets the UTC capture timestamp in Unix milliseconds.
    /// </summary>
    public long CapturedTsUtcMs { get; }

    /// <summary>
    /// Gets a value indicating whether the payload is a key frame.
    /// </summary>
    public bool IsKeyFrame { get; }

    /// <summary>
    /// Gets the monotonic stream epoch for the encoded payload.
    /// </summary>
    public long StreamEpoch { get; }

    /// <summary>
    /// Gets the optional stream configuration required to initialize a decoder for this epoch.
    /// </summary>
    public ScreenShareVideoStreamConfigV1? StreamConfig { get; }
}
