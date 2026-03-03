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
    public ScreenCaptureFrameEventArgs(int width, int height, byte[] encodedFrameData, string encoding)
    {
        Width = width;
        Height = height;
        EncodedFrameData = encodedFrameData ?? Array.Empty<byte>();
        Encoding = string.IsNullOrWhiteSpace(encoding) ? "unknown" : encoding;
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
}
