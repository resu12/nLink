using System;
using Avalonia.Media.Imaging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal sealed class EncodedFrameBitmapDecoder
{
    private readonly Func<ReadOnlyMemory<byte>, Bitmap> decodeJpegFrame;
    private readonly IWindowsH264BitmapDecoder? h264Decoder;
    private readonly object h264DecoderGate = new();

    public EncodedFrameBitmapDecoder(
        Func<ReadOnlyMemory<byte>, Bitmap> decodeJpegFrame,
        IWindowsH264BitmapDecoder? h264Decoder = null)
    {
        this.decodeJpegFrame = decodeJpegFrame ?? throw new ArgumentNullException(nameof(decodeJpegFrame));
        this.h264Decoder = h264Decoder;
    }

    public Bitmap Decode(string encoding, ReadOnlyMemory<byte> encodedFrameBytes)
    {
        return Decode(new EncodedFrameDecodeRequest(encoding, encodedFrameBytes));
    }

    public void ConfigureH264Stream(ScreenShareVideoStreamConfigV1 config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (h264Decoder is null)
        {
            return;
        }

        lock (h264DecoderGate)
        {
            h264Decoder.ConfigureStream(config);
        }
    }

    public void ResetH264Stream()
    {
        if (h264Decoder is null)
        {
            return;
        }

        lock (h264DecoderGate)
        {
            h264Decoder.Reset();
        }
    }

    public Bitmap Decode(EncodedFrameDecodeRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Encoding);

        return request.Encoding.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => decodeJpegFrame(request.EncodedFrameBytes),
            "h264" when h264Decoder is not null => DecodeH264(request),
            _ => throw new NotSupportedException($"Encoded frame decoding is not registered for '{request.Encoding}'."),
        };
    }

    private Bitmap DecodeH264(EncodedFrameDecodeRequest request)
    {
        lock (h264DecoderGate)
        {
            if (h264Decoder is null || !h264Decoder.IsSupported)
            {
                throw new NotSupportedException($"Encoded frame decoding is not registered for '{request.Encoding}'.");
            }

            return h264Decoder.Decode(request);
        }
    }
}
