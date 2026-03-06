using System.Threading;
using System.Threading.Tasks;

namespace NLink.App.Services;

public sealed class UnsupportedCameraQrCaptureService : ICameraQrCaptureService
{
    public bool IsSupported => false;

    public Task<CameraQrCaptureResult> CapturePhotoAsync(CancellationToken ct)
    {
        return Task.FromResult(new CameraQrCaptureResult(
            IsSuccess: false,
            IsCancelled: false,
            FilePath: null,
            Message: "Camera QR scan is not supported on this platform."));
    }
}
