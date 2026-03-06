using System;

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
    public ScreenCaptureFrameEventArgs(
        int width,
        int height,
        byte[] encodedFrameData,
        string encoding,
        long capturedTsUtcMs = 0)
    {
        Width = width;
        Height = height;
        EncodedFrameData = encodedFrameData ?? Array.Empty<byte>();
        Encoding = string.IsNullOrWhiteSpace(encoding) ? "unknown" : encoding;
        CapturedTsUtcMs = capturedTsUtcMs > 0 ? capturedTsUtcMs : 0;
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
}
