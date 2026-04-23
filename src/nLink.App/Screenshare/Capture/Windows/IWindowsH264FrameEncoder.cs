using System;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.App.Services.ScreenCapture;

internal interface IWindowsH264FrameEncoder : IAsyncDisposable
{
    bool IsSupported { get; }

    ValueTask<WindowsH264EncodedFrame?> EncodeAsync(
        WindowsRawCaptureFrame frame,
        WindowsH264EncodeOptions options,
        CancellationToken cancellationToken);

    void StartRecoveryBurst(string reason, long streamEpoch);
}
