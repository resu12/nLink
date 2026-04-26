using System;
using Avalonia.Media.Imaging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal interface IWindowsH264BitmapDecoder : IDisposable
{
    bool IsSupported { get; }

    void ConfigureStream(ScreenShareVideoStreamConfigV1 config);

    void Reset();

    Bitmap Decode(EncodedFrameDecodeRequest request);
}
