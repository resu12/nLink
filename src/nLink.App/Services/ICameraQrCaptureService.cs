using System.Threading;
using System.Threading.Tasks;

namespace NLink.App.Services;

public readonly record struct CameraQrCaptureResult(
    bool IsSuccess,
    bool IsCancelled,
    string? FilePath,
    string? Message = null);

public interface ICameraQrCaptureService
{
    bool IsSupported { get; }

    Task<CameraQrCaptureResult> CapturePhotoAsync(CancellationToken ct);
}
